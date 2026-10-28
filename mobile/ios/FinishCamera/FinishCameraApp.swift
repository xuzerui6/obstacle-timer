import SwiftUI
import AVFoundation

@main struct FinishCameraApp: App {
    @StateObject private var controller = FinishController()
    var body: some Scene { WindowGroup { FinishView(model: controller).preferredColorScheme(.dark) } }
}

struct FinishView: View {
    @ObservedObject var model: FinishController
    @State private var zoom: CGFloat = 1
    @State private var drag = CGSize.zero
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("OBSTACLE / 终点判读").font(.title2.bold()).foregroundStyle(Color(red: 0.75, green: 0.94, blue: 0.41))
                Text(model.link.status).font(.footnote).foregroundStyle(.secondary)
                HStack {
                    Button("扫码配对") { model.showScanner = true }.disabled(model.capture.recording)
                    Button("粘贴配对") { model.showPairText.toggle() }.disabled(model.capture.recording)
                    Button("补传") { Task { await model.link.retryOutbox() } }
                }.buttonStyle(.bordered)
                if model.showPairText {
                    TextEditor(text: $model.pairingText).frame(height: 80).border(.gray)
                    Button("连接电脑") { Task { await model.link.pair(model.pairingText) } }.buttonStyle(.borderedProminent).disabled(model.capture.recording)
                }
                Text(model.raceLabel).font(.headline)
                Text(model.quality).font(.caption).foregroundStyle(.orange)
                ZStack {
                    Color.black
                    if let image = model.image {
                        GeometryReader { geometry in
                            let aspect = image.size.width / image.size.height
                            let width = min(geometry.size.width, geometry.size.height * aspect)
                            let height = width / aspect
                            ZStack(alignment: .topLeading) {
                                Image(uiImage: image).resizable().frame(width: width, height: height)
                                Rectangle().fill(.green).frame(width: 2, height: height).offset(x: width * model.finishLine)
                            }.frame(width: width, height: height).scaleEffect(zoom).offset(drag)
                                .frame(width: geometry.size.width, height: geometry.size.height)
                                .gesture(MagnificationGesture().onChanged { zoom = min(8, max(1, $0)) })
                                .simultaneousGesture(DragGesture().onChanged { drag = $0.translation })
                                .onTapGesture(count: 2) { zoom = 1; drag = .zero }
                        }
                    } else { CapturePreview(session: model.capture.session) }
                }.frame(height: 300).clipped()
                Picker("相机模式", selection: $model.modeID) { ForEach(model.modes) { Text($0.label).tag($0.id) } }.disabled(model.capture.recording)
                HStack {
                    Button("准备录像") { model.prepare() }.disabled(model.capture.recording)
                    Button("结束并判读") { model.capture.stop() }.disabled(!model.capture.recording)
                }.buttonStyle(.borderedProminent)
                Text(model.frameLabel).font(.caption).monospacedDigit()
                if model.frameCount > 0 {
                    Slider(value: $model.selectedFrame, in: 0...Double(max(1, model.frameCount - 1)), step: 1) { editing in
                        if !editing { model.selectFrame(Int(model.selectedFrame)) }
                    }
                    HStack {
                        Button("上一帧") { model.selectFrame(Int(model.selectedFrame) - 1) }
                        Button("下一帧") { model.selectFrame(Int(model.selectedFrame) + 1) }
                        Button("确认所选帧") { model.submit() }.disabled(model.image == nil || model.saving)
                    }.buttonStyle(.bordered)
                    Text("终点参考线").font(.caption)
                    Slider(value: $model.finishLine, in: 0...1)
                }
                Text("手机需在起跑前录像。过线部位由裁判按现场规则判定。文件在电脑确认接收前始终保留。")
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(20)
        }
        .background(Color(red: 0.04, green: 0.08, blue: 0.11))
        .task { await model.loadModes() }
        .sheet(isPresented: $model.showScanner) {
            QRScanner { payload in model.showScanner = false; Task { await model.link.pair(payload) } }
        }
    }
}

final class PreviewUIView: UIView {
    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
    var preview: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
}
struct CapturePreview: UIViewRepresentable {
    let session: AVCaptureSession
    func makeUIView(context: Context) -> PreviewUIView {
        let view = PreviewUIView(); view.preview.session = session; view.preview.videoGravity = .resizeAspect; return view
    }
    func updateUIView(_ uiView: PreviewUIView, context: Context) {
        if let connection = uiView.preview.connection, connection.isVideoOrientationSupported { connection.videoOrientation = .portrait }
    }
}

struct QRScanner: UIViewControllerRepresentable {
    let scanned: (String) -> Void
    func makeUIViewController(context: Context) -> ScannerController { ScannerController(scanned: scanned) }
    func updateUIViewController(_ uiViewController: ScannerController, context: Context) {}
}
final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    let capture = AVCaptureSession()
    let scanned: (String) -> Void
    private var delivered = false
    init(scanned: @escaping (String) -> Void) { self.scanned = scanned; super.init(nibName: nil, bundle: nil) }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .black
        guard let device = AVCaptureDevice.default(for: .video), let input = try? AVCaptureDeviceInput(device: device), capture.canAddInput(input) else { return }
        capture.addInput(input); let output = AVCaptureMetadataOutput()
        guard capture.canAddOutput(output) else { return }; capture.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
        let preview = AVCaptureVideoPreviewLayer(session: capture); preview.videoGravity = .resizeAspectFill; preview.frame = view.bounds; view.layer.addSublayer(preview)
        DispatchQueue.global(qos: .userInitiated).async { self.capture.startRunning() }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); view.layer.sublayers?.compactMap { $0 as? AVCaptureVideoPreviewLayer }.first?.frame = view.bounds }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !delivered, let value = (metadataObjects.first as? AVMetadataMachineReadableCodeObject)?.stringValue, value.contains("server"), value.contains("secret") else { return }
        delivered = true; scanned(value)
    }
    override func viewWillDisappear(_ animated: Bool) { super.viewWillDisappear(animated); DispatchQueue.global().async { self.capture.stopRunning() } }
}
