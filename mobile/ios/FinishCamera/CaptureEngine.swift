import AVFoundation
import UIKit
import Combine

struct CaptureMode: Identifiable {
    let id: Int
    let device: AVCaptureDevice
    let format: AVCaptureDevice.Format
    let fps: Int
    var dimensions: CMVideoDimensions { CMVideoFormatDescriptionGetDimensions(format.formatDescription) }
    var label: String { "\(dimensions.width)×\(dimensions.height) / \(fps) fps" + (fps < 120 ? " · 低帧率" : "") }
}
struct CaptureRecord {
    let folder: URL, video: URL
    let pts: [Int64], clientTimes: [Int64]
    let verified: Bool, startUs: Int64, endUs: Int64
    let fps: Int, width: Int, height: Int, dropped: Int
    var metadata: [String: Any] {
        ["platform": "ios", "model": UIDevice.current.model, "clockDomain": "coremedia-host-time-us", "framePtsUs": pts,
         "frameClientUs": clientTimes, "cameraMappingVerified": verified, "startClientUs": startUs, "endClientUs": endUs,
         "fps": fps, "width": width, "height": height, "dropped": dropped]
    }
    static func load(_ folder: URL) throws -> CaptureRecord {
        let data = try readJSON(folder.appendingPathComponent("frames.json"))
        guard let pts = data["framePtsUs"] as? [NSNumber], let times = data["frameClientUs"] as? [NSNumber], !pts.isEmpty, pts.count == times.count else { throw failure("帧索引无效") }
        return CaptureRecord(folder: folder, video: folder.appendingPathComponent("recording.mp4"), pts: pts.map(\.int64Value), clientTimes: times.map(\.int64Value),
            verified: data["cameraMappingVerified"] as? Bool ?? false, startUs: int64(data["startClientUs"]), endUs: int64(data["endClientUs"]),
            fps: Int(int64(data["fps"])), width: Int(int64(data["width"])), height: Int(int64(data["height"])), dropped: Int(int64(data["dropped"])))
    }
}

final class CaptureEngine: NSObject, ObservableObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "finish.camera.capture", qos: .userInitiated)
    @Published private(set) var recording = false
    private var writing = false
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var folder: URL?
    private var mode: CaptureMode?
    private var sourcePTS: [CMTime] = [], captureTimes: [Int64] = []
    private var firstPTS: CMTime?
    private var verified = true
    private var dropped = 0
    private var captureStart: Int64 = 0
    private var captureEnd: Int64 = 0
    private var videoWidth = 0, videoHeight = 0
    var onFirst: ((Int64, CaptureMode, Bool) -> Void)?
    var onStop: ((CaptureRecord) -> Void)?
    var onError: ((String) -> Void)?

    func modes() -> [CaptureMode] {
        let devices = AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera], mediaType: .video, position: .back).devices
        var result: [CaptureMode] = []
        for device in devices {
            for format in device.formats {
                let dim = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
                guard dim.width <= 1920 && dim.height <= 1080 && dim.width >= 640 else { continue }
                for fps in [240, 120, 60, 30] where format.videoSupportedFrameRateRanges.contains(where: { $0.minFrameRate <= Double(fps) && $0.maxFrameRate >= Double(fps) }) {
                    if !result.contains(where: { $0.fps == fps && $0.dimensions.width == dim.width && $0.dimensions.height == dim.height }) {
                        result.append(CaptureMode(id: result.count, device: device, format: format, fps: fps))
                    }
                }
            }
        }
        return result.sorted { $0.fps == $1.fps ? $0.dimensions.width * $0.dimensions.height > $1.dimensions.width * $1.dimensions.height : $0.fps > $1.fps }
    }
    func start(folder: URL, mode: CaptureMode) {
        guard !recording else { return }; recording = true
        queue.async {
            var configuring = false
            do {
                self.folder = folder; self.mode = mode; self.captureStart = monoUs(); self.captureEnd = 0
                self.firstPTS = nil; self.input = nil; self.sourcePTS = []; self.captureTimes = []; self.dropped = 0; self.verified = true
                self.session.beginConfiguration()
                configuring = true
                self.session.inputs.forEach { self.session.removeInput($0) }; self.session.outputs.forEach { self.session.removeOutput($0) }
                self.session.sessionPreset = .inputPriority
                let deviceInput = try AVCaptureDeviceInput(device: mode.device)
                guard self.session.canAddInput(deviceInput) else { throw failure("无法打开后置相机") }
                self.session.addInput(deviceInput)
                try mode.device.lockForConfiguration()
                mode.device.activeFormat = mode.format
                mode.device.activeVideoMinFrameDuration = CMTime(value: 1, timescale: CMTimeScale(mode.fps))
                mode.device.activeVideoMaxFrameDuration = CMTime(value: 1, timescale: CMTimeScale(mode.fps))
                mode.device.unlockForConfiguration()
                let output = AVCaptureVideoDataOutput(); output.alwaysDiscardsLateVideoFrames = true
                output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
                output.setSampleBufferDelegate(self, queue: self.queue)
                guard self.session.canAddOutput(output) else { throw failure("该模式无法输出视频帧") }
                self.session.addOutput(output)
                if let connection = output.connection(with: .video) {
                    if connection.isVideoOrientationSupported { connection.videoOrientation = .portrait }
                    if connection.isVideoStabilizationSupported { connection.preferredVideoStabilizationMode = .off }
                }
                self.session.commitConfiguration()
                configuring = false
                self.writer = try AVAssetWriter(url: folder.appendingPathComponent("recording.mp4"), fileType: .mp4)
                self.writing = true; self.session.startRunning()
            } catch {
                if configuring { self.session.commitConfiguration() }
                self.writing = false
                DispatchQueue.main.async { self.recording = false; self.onError?("相机启动失败：\(error.localizedDescription)") }
            }
        }
    }
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard writing, let mode = mode, let writer = writer, CMSampleBufferDataIsReady(sampleBuffer), let image = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        var clientUs = monoUs()
        if let captureClock = session.synchronizationClock {
            let converted = CMSyncConvertTime(pts, from: captureClock, to: CMClockGetHostTimeClock())
            if converted.isValid && converted.isNumeric {
                clientUs = Int64((CMTimeGetSeconds(converted) * 1_000_000).rounded())
                if abs(clientUs - monoUs()) > 5_000_000 { verified = false }
            } else { verified = false }
        } else { verified = false }
        if firstPTS == nil {
            videoWidth = CVPixelBufferGetWidth(image); videoHeight = CVPixelBufferGetHeight(image)
            let settings: [String: Any] = [AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: videoWidth, AVVideoHeightKey: videoHeight,
                AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: max(6_000_000, min(80_000_000, videoWidth * videoHeight * mode.fps / 8)),
                    AVVideoExpectedSourceFrameRateKey: mode.fps, AVVideoAllowFrameReorderingKey: false,
                    AVVideoMaxKeyFrameIntervalKey: mode.fps]]
            let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings); input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else { reportFailure("该设备无法编码所选模式"); return }
            writer.add(input); self.input = input
            guard writer.startWriting() else { reportFailure(writer.error?.localizedDescription ?? "编码器启动失败"); return }
            writer.startSession(atSourceTime: pts); firstPTS = pts
        }
        guard let input = input, input.isReadyForMoreMediaData else { dropped += 1; return }
        guard input.append(sampleBuffer) else { reportFailure(writer.error?.localizedDescription ?? "视频帧保存失败"); return }
        sourcePTS.append(pts); captureTimes.append(clientUs)
        if captureTimes.count == 1 {
            let valid = verified
            DispatchQueue.main.async { self.onFirst?(clientUs, mode, valid) }
        }
    }
    func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) { dropped += 1 }
    private func reportFailure(_ text: String) {
        DispatchQueue.main.async { self.onError?(text) }; stop()
    }
    func stop() {
        queue.async {
            guard self.writing else { return }; self.writing = false; self.captureEnd = monoUs()
            self.session.stopRunning()
            guard let writer = self.writer, let input = self.input, let folder = self.folder, let mode = self.mode, !self.captureTimes.isEmpty else {
                DispatchQueue.main.async { self.recording = false; self.onError?("没有可判读视频帧，已有文件仍保留") }; return
            }
            input.markAsFinished()
            writer.finishWriting {
                self.queue.async {
                    do {
                        guard writer.status == .completed else { throw writer.error ?? failure("录像结束失败") }
                        let video = folder.appendingPathComponent("recording.mp4"), asset = AVURLAsset(url: video)
                        guard let track = asset.tracks(withMediaType: .video).first else { throw failure("录像缺少视频轨道") }
                        let reader = try AVAssetReader(asset: asset), output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
                        reader.add(output); guard reader.startReading() else { throw reader.error ?? failure("无法建立帧索引") }
                        var pts: [Int64] = []
                        while let buffer = output.copyNextSampleBuffer() { pts.append(Int64((CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(buffer)) * 1_000_000).rounded())) }
                        guard reader.status == .completed, !pts.isEmpty else { throw failure("读取视频帧索引失败") }
                        var valid = self.verified && pts.count == self.captureTimes.count
                        if let first = self.sourcePTS.first, pts.count == self.sourcePTS.count {
                            for (i, source) in self.sourcePTS.enumerated() {
                                let relative = Int64((CMTimeGetSeconds(CMTimeSubtract(source, first)) * 1_000_000).rounded())
                                if abs((pts[i] - pts[0]) - relative) > 500 { valid = false }
                            }
                        }
                        let times = pts.count == self.captureTimes.count ? self.captureTimes : pts.map { self.captureStart + $0 - pts[0] }
                        let record = CaptureRecord(folder: folder, video: video, pts: pts, clientTimes: times, verified: valid,
                            startUs: times[0], endUs: self.captureEnd, fps: mode.fps, width: self.videoWidth, height: self.videoHeight, dropped: self.dropped)
                        try saveJSON(folder.appendingPathComponent("frames.json"), record.metadata)
                        DispatchQueue.main.async { self.recording = false; self.onStop?(record) }
                    } catch { DispatchQueue.main.async { self.recording = false; self.onError?("原片保留，帧索引失败：\(error.localizedDescription)") } }
                }
            }
        }
    }
}
