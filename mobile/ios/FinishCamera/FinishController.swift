import Foundation
import UIKit
import AVFoundation
import Combine

@MainActor final class FinishController: ObservableObject {
    let link = PhoneLink()
    let capture = CaptureEngine()
    @Published var modes: [CaptureMode] = []
    @Published var modeID = 0
    @Published var pairingText = ""
    @Published var showScanner = false
    @Published var showPairText = false
    @Published var raceLabel = "等待电脑选择选手"
    @Published var frameLabel = "逐帧选择过线画面"
    @Published var quality = "网络估计计时 · 端到端精度未经实测"
    @Published var image: UIImage?
    @Published var selectedFrame = 0.0
    @Published var finishLine = 0.5
    @Published var frameCount = 0
    @Published var saving = false
    private(set) var record: CaptureRecord?
    private var recordingRun: [String: Any]?
    private var generator: AVAssetImageGenerator?
    private var generation = 0
    private let decodeQueue = DispatchQueue(label: "finish.review.decode", qos: .userInitiated)
    private var observations = Set<AnyCancellable>()
    init() {
        link.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }.store(in: &observations)
        capture.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }.store(in: &observations)
        link.onRun = { [weak self] run in self?.onRun(run) }
        capture.onError = { [weak self] text in self?.link.status = text }
        capture.onFirst = { [weak self] first, mode, verified in self?.firstFrame(first, mode, verified) }
        capture.onStop = { [weak self] record in self?.stopped(record) }
        NotificationCenter.default.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in if self?.capture.recording == true { self?.link.status = "应用离开前台，正在保留录像"; self?.capture.stop() } }
        }
        recover()
    }
    func loadModes() async {
        let allowed = await AVCaptureDevice.requestAccess(for: .video)
        guard allowed else { link.status = "请在设置中允许相机权限"; return }
        modes = capture.modes(); modeID = modes.first?.id ?? 0
        if modes.isEmpty { link.status = "未发现可用的后置相机采集模式" }
    }
    private func onRun(_ run: [String: Any]) {
        let athlete = run["athlete"] as? [String: Any] ?? [:]
        raceLabel = "\(athlete["bib"] as? String ?? "") / \(athlete["name"] as? String ?? "") · \(run["state"] as? String ?? "")"
        if record != nil && run["id"] as? String != recordingRun?["id"] as? String {
            let previous = recordingRun?["athlete"] as? [String: Any] ?? [:]
            raceLabel = "当前录像：\(previous["bib"] as? String ?? "") / \(previous["name"] as? String ?? "")；下一场请点击准备录像"
        }
        if run["id"] as? String == recordingRun?["id"] as? String {
            recordingRun = run
            if let id = run["id"] as? String { try? saveJSON(link.root.appendingPathComponent(id).appendingPathComponent("run.json"), run) }
            if run["start"] as? [String: Any] != nil && capture.recording { link.status = "已收到 Timy3 起跑，持续录像；过线后结束并判读" }
        }
    }
    func prepare() {
        do {
            guard link.connected, let run = link.run, let id = run["id"] as? String else { throw failure("请先连接电脑并创建场次") }
            guard ["preparing", "ready"].contains(run["state"] as? String ?? ""), !capture.recording else { throw failure("场次当前不能准备录像") }
            _ = try link.clockMap()
            guard let mode = modes.first(where: { $0.id == modeID }) else { throw failure("请选择相机模式") }
            let folder = link.root.appendingPathComponent(id, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            guard !FileManager.default.fileExists(atPath: folder.appendingPathComponent("recording.mp4").path) else { throw failure("本场已有录像，请在电脑取消并新建场次；不会覆盖原片") }
            try saveJSON(folder.appendingPathComponent("run.json"), run)
            recordingRun = run; record = nil; image = nil; frameCount = 0
            capture.start(folder: folder, mode: mode); link.status = "相机启动中，首帧保存后才报告就绪"
        } catch { link.status = error.localizedDescription }
    }
    private func firstFrame(_ first: Int64, _ mode: CaptureMode, _ verified: Bool) {
        do {
            guard let id = recordingRun?["id"] as? String else { throw failure("场次关联丢失") }
            let clock = try link.clockMap()
            let ready: [String: Any] = ["runId": id, "deviceId": link.deviceID, "clock": clock,
                "recordingStartClientUs": first, "cameraTimebase": verified ? "ios-host-clock-mapped" : "ios-unverified",
                "model": UIDevice.current.model, "fps": mode.fps, "width": mode.dimensions.width, "height": mode.dimensions.height]
            try saveJSON(link.root.appendingPathComponent(id).appendingPathComponent("ready.json"), ready)
            Task { await link.send("ready", ready) }
            quality = "\(mode.fps) fps · \(verified ? "帧时钟已作运行映射检查" : "帧时钟待核验") · 端到端精度未验证"
        } catch { link.status = "录像保留，尚未报告就绪：\(error.localizedDescription)" }
    }
    private func stopped(_ completed: CaptureRecord) {
        record = completed; frameCount = completed.pts.count
        generator = AVAssetImageGenerator(asset: AVURLAsset(url: completed.video))
        generator?.appliesPreferredTrackTransform = true
        generator?.requestedTimeToleranceBefore = .zero; generator?.requestedTimeToleranceAfter = .zero
        if let id = recordingRun?["id"] as? String { Task { await link.send("review", ["runId": id]) } }
        selectFrame(completed.pts.count - 1)
        link.status = "录像已保存，共 \(completed.pts.count) 帧；请选择过线画面"
    }
    func selectFrame(_ index: Int) {
        guard let record = record, let generator = generator else { return }
        let bounded = max(0, min(index, record.pts.count - 1)); selectedFrame = Double(bounded); image = nil; generation += 1
        let request = generation, target = record.pts[bounded]
        frameLabel = "正在解码第 \(bounded + 1)/\(record.pts.count) 帧…"
        decodeQueue.async { [weak self] in
            do {
                var actual = CMTime.zero
                let cg = try generator.copyCGImage(at: CMTime(value: target, timescale: 1_000_000), actualTime: &actual)
                guard abs(Int64((CMTimeGetSeconds(actual) * 1_000_000).rounded()) - target) <= 500 else { throw failure("无法准确解码所选帧，不使用近似关键帧代替") }
                let uiImage = UIImage(cgImage: cg)
                DispatchQueue.main.async {
                    guard let self = self, request == self.generation else { return }
                    self.image = uiImage
                    var estimate = "待处理"
                    if let start = self.recordingRun?["start"] as? [String: Any], let ready = try? readJSON(record.folder.appendingPathComponent("ready.json")),
                       let map = ready["clock"] as? [String: Any], let offset = map["offsetUs"] as? Double, record.verified {
                        let elapsed = (Double(record.clientTimes[bounded]) + offset - Double(int64(start["hostUs"]))) / 1_000_000
                        estimate = String(format: "%.3f s", elapsed)
                    }
                    self.frameLabel = "帧 \(bounded + 1)/\(record.pts.count) · PTS \(target) μs · 试算 \(estimate)"
                }
            } catch { DispatchQueue.main.async { self?.link.status = "判读失败：\(error.localizedDescription)" } }
        }
    }
    func submit() {
        do {
            guard !saving, let record = record, let image = image, let run = recordingRun, let runID = run["id"] as? String else { throw failure("请先选取并等待所选帧解码完成") }
            saving = true; defer { saving = false }
            let evidence = newID(), request = newID(), index = Int(selectedFrame)
            let output = record.folder.appendingPathComponent(evidence + ".jpg")
            let renderer = UIGraphicsImageRenderer(size: image.size)
            let screenshot = renderer.image { context in
                image.draw(at: .zero); let line = context.cgContext
                line.setStrokeColor(UIColor.red.cgColor); line.setLineWidth(2)
                let x = image.size.width * finishLine; line.move(to: CGPoint(x: x, y: 0)); line.addLine(to: CGPoint(x: x, y: image.size.height)); line.strokePath()
            }
            guard let jpeg = screenshot.jpegData(compressionQuality: 0.95) else { throw failure("截图生成失败") }; try jpeg.write(to: output, options: .atomic)
            var metadata = record.metadata; metadata["selectedFrame"] = index; metadata["finishLine"] = finishLine
            let meta = record.folder.appendingPathComponent(evidence + ".json"); try saveJSON(meta, metadata)
            let payload: [String: Any] = ["requestId": request, "runId": runID, "deviceId": link.deviceID,
                "expectedRevision": int64(run["revision"]), "framePtsUs": record.pts[index], "frameClientUs": record.clientTimes[index],
                "recordingEndClientUs": record.endUs, "evidenceId": evidence, "finishLine": finishLine,
                "note": "手机裁判手动选帧 #\(index + 1)", "cameraMappingVerified": record.verified]
            try link.enqueue(folder: record.folder, payload: payload, image: output, metadata: meta, video: record.video)
            link.status = "判读已在本机保存，先传结果与截图，再传完整录像"
        } catch { link.status = "保存失败：\(error.localizedDescription)"; saving = false }
    }
    private func recover() {
        guard let folders = try? FileManager.default.contentsOfDirectory(at: link.root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let candidates = folders.filter { FileManager.default.fileExists(atPath: $0.appendingPathComponent("frames.json").path) }
        let latest = candidates.max { a, b in
            ((try? a.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast) <
            ((try? b.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast)
        }
        guard let folder = latest else { return }
        do { recordingRun = try readJSON(folder.appendingPathComponent("run.json")); stopped(try CaptureRecord.load(folder)); link.status = "已恢复上次录像，可继续判读或补传" }
        catch { link.status = "恢复失败，原始文件保留：\(error.localizedDescription)" }
    }
}
