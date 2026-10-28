package cn.obstacletimer.finish

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.ImageFormat
import android.hardware.camera2.*
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import android.widget.TextView
import com.google.zxing.*
import com.google.zxing.common.HybridBinarizer

class ScanActivity : Activity() {
    private val thread = HandlerThread("pair-qr").also { it.start() }
    private val handler = Handler(thread.looper)
    private var camera: CameraDevice? = null
    private var reader: ImageReader? = null
    private var found = false
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val preview = TextureView(this)
        val root = FrameLayout(this); root.addView(preview)
        root.addView(TextView(this).apply { text = "对准电脑上的配对二维码"; textSize = 22f; setTextColor(-1); setPadding(30, 60, 0, 0) })
        setContentView(root)
        preview.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(surface: android.graphics.SurfaceTexture, width: Int, height: Int) { open(preview) }
            override fun onSurfaceTextureSizeChanged(surface: android.graphics.SurfaceTexture, width: Int, height: Int) {}
            override fun onSurfaceTextureDestroyed(surface: android.graphics.SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(surface: android.graphics.SurfaceTexture) {}
        }
    }
    @SuppressLint("MissingPermission")
    private fun open(preview: TextureView) {
        val manager = getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val id = manager.cameraIdList.first { manager.getCameraCharacteristics(it)[CameraCharacteristics.LENS_FACING] == CameraCharacteristics.LENS_FACING_BACK }
        reader = ImageReader.newInstance(640, 480, ImageFormat.YUV_420_888, 2)
        val decoder = MultiFormatReader().apply { setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true)) }
        reader!!.setOnImageAvailableListener({ source ->
            val image = source.acquireLatestImage() ?: return@setOnImageAvailableListener
            try {
                if (!found) {
                    val plane = image.planes[0]; val pixels = ByteArray(image.width * image.height)
                    for (y in 0 until image.height) for (x in 0 until image.width) pixels[y * image.width + x] = plane.buffer.get(y * plane.rowStride + x * plane.pixelStride)
                    val luminance = PlanarYUVLuminanceSource(pixels, image.width, image.height, 0, 0, image.width, image.height, false)
                    val result = decoder.decodeWithState(BinaryBitmap(HybridBinarizer(luminance)))
                    if (result.text.contains("server") && result.text.contains("secret")) { found = true; runOnUiThread { setResult(RESULT_OK, Intent().putExtra("qr", result.text)); finish() } }
                }
            } catch (_: NotFoundException) { } finally { image.close(); decoder.reset() }
        }, handler)
        manager.openCamera(id, object : CameraDevice.StateCallback() {
            override fun onOpened(device: CameraDevice) {
                camera = device; preview.surfaceTexture!!.setDefaultBufferSize(640, 480)
                val surface = Surface(preview.surfaceTexture)
                device.createCaptureSession(listOf(surface, reader!!.surface), object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(session: CameraCaptureSession) { val request = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply { addTarget(surface); addTarget(reader!!.surface) }.build(); session.setRepeatingRequest(request, null, handler) }
                    override fun onConfigureFailed(session: CameraCaptureSession) { runOnUiThread { finish() } }
                }, handler)
            }
            override fun onDisconnected(device: CameraDevice) { device.close() }
            override fun onError(device: CameraDevice, error: Int) { device.close(); runOnUiThread { finish() } }
        }, handler)
    }
    override fun onDestroy() { camera?.close(); reader?.close(); thread.quitSafely(); super.onDestroy() }
}
