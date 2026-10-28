import Foundation
import CoreMedia
import Combine
import CryptoKit
import UIKit

func monoUs() -> Int64 { Int64((CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock())) * 1_000_000).rounded()) }
func newID() -> String { UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }
func int64(_ value: Any?) -> Int64 { (value as? NSNumber)?.int64Value ?? 0 }
func jsonData(_ value: Any) throws -> Data { try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) }
func readJSON(_ file: URL) throws -> [String: Any] { try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any] ?? [:] }
func saveJSON(_ file: URL, _ value: Any) throws { try jsonData(value).write(to: file, options: .atomic) }

struct SyncPoint {
    let t1: Int64, t2: Int64, t3: Int64, t4: Int64
    var rtt: Double { Double((t4 - t1) - (t3 - t2)) }
    var offset: Double { (Double(t2 - t1) + Double(t3 - t4)) / 2 }
}

@MainActor final class PhoneLink: ObservableObject {
    @Published var status = "连接现场电脑；原始录像保存在本机"
    @Published var connected = false
    @Published var run: [String: Any]?
    let deviceID: String
    private(set) var sessionID = ""
    private var server = UserDefaults.standard.string(forKey: "server") ?? ""
    private var token = UserDefaults.standard.string(forKey: "token") ?? ""
    private var socket: URLSessionWebSocketTask?
    private var syncSamples: [SyncPoint] = []
    private var pending: [String: Int64] = [:]
    private var latestClient: Int64 = 0
    private var maintenance: Task<Void, Never>?
    private var receiving: Task<Void, Never>?
    private var uploading = false
    var onRun: (([String: Any]) -> Void)?
    init() {
        deviceID = UserDefaults.standard.string(forKey: "deviceID") ?? newID()
        UserDefaults.standard.set(deviceID, forKey: "deviceID")
        maintenance = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                guard let self = self else { return }
                if self.connected { await self.sync(); await self.retryOutbox() }
                else if !self.server.isEmpty && !self.token.isEmpty { self.connect() }
            }
        }
    }
    var root: URL {
        let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("races", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }
    func pair(_ payload: String) async {
        do {
            let data = try JSONSerialization.jsonObject(with: Data(payload.utf8)) as? [String: Any]
            guard let address = data?["server"] as? String, let url = URL(string: address), url.scheme == "http", url.host != nil,
                  url.user == nil, url.path.isEmpty || url.path == "/", let secret = data?["secret"] as? String else { throw failure("配对二维码格式无效") }
            var request = URLRequest(url: URL(string: address.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/api/pair")!)
            request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try jsonData(["secret": secret, "deviceId": deviceID])
            let response = try await call(request)
            guard let access = response["token"] as? String, let session = response["sessionId"] as? String else { throw failure("配对响应无效") }
            server = address.trimmingCharacters(in: CharacterSet(charactersIn: "/")); token = access; sessionID = session
            UserDefaults.standard.set(server, forKey: "server"); UserDefaults.standard.set(token, forKey: "token")
            connect()
        } catch { status = "配对失败：\(error.localizedDescription)" }
    }
    private func connect() {
        guard !connected, !token.isEmpty, let url = URL(string: server.replacingOccurrences(of: "http://", with: "ws://") + "/api/ws") else { return }
        receiving?.cancel(); socket?.cancel(with: .goingAway, reason: nil)
        var request = URLRequest(url: url); request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let ws = URLSession.shared.webSocketTask(with: request); socket = ws; ws.resume()
        receiving = Task { [weak self] in
            guard let self = self else { return }
            do {
                while !Task.isCancelled {
                    let message = try await ws.receive(); let t4 = monoUs(); let bytes: Data
                    switch message { case .data(let d): bytes = d; case .string(let s): bytes = Data(s.utf8); @unknown default: continue }
                    guard let envelope = try JSONSerialization.jsonObject(with: bytes) as? [String: Any],
                          let type = envelope["type"] as? String, let data = envelope["data"] as? [String: Any] else { continue }
                    if !self.connected { self.connected = true; self.status = "电脑已连接，正在同步"; self.initialSync() }
                    switch type {
                    case "state":
                        let newSession = data["sessionId"] as? String ?? ""
                        if newSession != self.sessionID { self.sessionID = newSession; self.syncSamples.removeAll(); self.pending.removeAll(); self.initialSync() }
                        self.run = data["activeRun"] as? [String: Any]
                        if let run = self.run { self.onRun?(run) }
                    case "sync":
                        let id = data["id"] as? String ?? ""
                        if let t1 = self.pending.removeValue(forKey: id), t1 == int64(data["t1"]), data["sessionId"] as? String == self.sessionID {
                            let sample = SyncPoint(t1: t1, t2: int64(data["t2"]), t3: int64(data["t3"]), t4: t4)
                            if sample.rtt >= 0 && sample.rtt <= 2_000_000 {
                                self.syncSamples.append(sample); if self.syncSamples.count > 60 { self.syncSamples.removeFirst() }; self.latestClient = t4
                            }
                        }
                    case "readyAccepted": self.status = "电脑已确认录像就绪，请在电脑进入待发令"
                    case "error": self.status = data["message"] as? String ?? "电脑拒绝操作"
                    default: break
                    }
                }
            } catch {
                guard !Task.isCancelled else { return }
                self.connected = false; self.status = "连接中断，录像保留；请检查网络或重新扫码"
            }
        }
    }
    private func initialSync() {
        Task { for _ in 0..<30 { await sync(); try? await Task.sleep(nanoseconds: 50_000_000) } }
    }
    private func sync() async {
        guard connected else { return }; let id = newID(), t1 = monoUs(); pending[id] = t1
        pending = pending.filter { t1 - $0.value < 10_000_000 }
        await send("sync", ["id": id, "t1": t1])
    }
    func clockMap() throws -> [String: Any] {
        guard syncSamples.count >= 5 && monoUs() - latestClient < 60_000_000 else { throw failure("同步尚未完成或已过期，请稍等数秒") }
        let best = Array(syncSamples.sorted { $0.rtt < $1.rtt }.prefix(5))
        let offsets = best.map(\.offset).sorted()
        return ["sessionId": sessionID, "offsetUs": offsets[offsets.count / 2], "rttUs": best.map(\.rtt).min()!,
                "jitterUs": offsets.last! - offsets.first!, "samples": syncSamples.count, "sampledClientUs": latestClient]
    }
    func send(_ type: String, _ data: [String: Any]) async {
        do { try await socket?.send(.data(jsonData(["type": type, "data": data]))) }
        catch { status = "发送失败，记录保留：\(error.localizedDescription)" }
    }
    func enqueue(folder: URL, payload: [String: Any], image: URL, metadata: URL, video: URL) throws {
        let item: [String: Any] = ["payload": payload, "image": image.path, "metadata": metadata.path, "video": video.path, "complete": false]
        try saveJSON(folder.appendingPathComponent("outbox-\(payload["requestId"] as! String).json"), item)
        Task { await retryOutbox() }
    }
    func retryOutbox() async {
        guard connected && !uploading else { return }; uploading = true; defer { uploading = false }
        let files = (FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)?.allObjects as? [URL] ?? [])
            .filter { $0.lastPathComponent.hasPrefix("outbox-") && $0.pathExtension == "json" }.sorted { $0.path < $1.path }
        for file in files {
            do {
                var item = try readJSON(file); if item["complete"] as? Bool == true { continue }
                let payload = item["payload"] as! [String: Any]
                let ack = try await post("/api/results", jsonData(payload), "application/json")
                let result = ack["result"] as! [String: Any]; item["acceptedRevision"] = result["revision"]; try saveJSON(file, item)
                for kind in ["image", "metadata", "video"] {
                    try await upload(runID: payload["runId"] as! String, evidenceID: payload["evidenceId"] as! String, kind: kind, file: URL(fileURLWithPath: item[kind] as! String))
                }
                item["complete"] = true; try saveJSON(file, item)
                status = "判读和完整录像已获电脑确认，本机原文件仍保留"
            } catch { status = "待补传：\(error.localizedDescription)"; return }
        }
    }
    private func upload(runID: String, evidenceID: String, kind: String, file: URL) async throws {
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path); let length = (attributes[.size] as! NSNumber).int64Value
        guard length > 0 else { throw failure("证据文件为空") }
        let hash = try await Task.detached(priority: .utility) {
            let input = try FileHandle(forReadingFrom: file); defer { try? input.close() }; var digest = SHA256()
            while let data = try input.read(upToCount: 65536), !data.isEmpty { digest.update(data: data) }
            return digest.finalize().map { String(format: "%02x", $0) }.joined()
        }.value
        let base = "/api/evidence/\(evidenceID)/\(kind)?runId=\(runID)"
        var request = URLRequest(url: URL(string: server + base)!); request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let checkpoint = try await call(request); if checkpoint["complete"] as? Bool == true { return }
        var offset = int64(checkpoint["offset"])
        let input = try FileHandle(forReadingFrom: file); defer { try? input.close() }; try input.seek(toOffset: UInt64(offset))
        while offset < length {
            guard let data = try input.read(upToCount: 4 * 1024 * 1024), !data.isEmpty else { throw failure("证据文件被截断") }
            let response = try await post(base + "&offset=\(offset)&length=\(length)&sha256=\(hash)", data, "application/octet-stream")
            offset = int64(response["offset"])
            if offset == length && response["complete"] as? Bool != true { throw failure("电脑尚未确认完整文件") }
        }
    }
    private func post(_ path: String, _ bytes: Data, _ contentType: String) async throws -> [String: Any] {
        var request = URLRequest(url: URL(string: server + path)!); request.httpMethod = "POST"; request.httpBody = bytes
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization"); request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        return try await call(request)
    }
    private func call(_ request: URLRequest) async throws -> [String: Any] {
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else { throw failure(String(data: data, encoding: .utf8) ?? "请求失败") }
        return try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
    }
}
func failure(_ message: String) -> NSError { NSError(domain: "ObstacleFinish", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
