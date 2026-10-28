package cn.obstacletimer.finish

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.graphics.SurfaceTexture
import android.hardware.camera2.*
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.*
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import android.util.Size
import android.view.Surface
import android.view.TextureView
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs

data class CaptureMode(val cameraId: String, val size: Size, val fps: Int, val highSpeed: Boolean)
data class CaptureRecord(val directory: File, val video: File, val pts: List<Long>, val clientTimes: List<Long>,
                         val verified: Boolean, val startUs: Long, val endUs: Long, val mode: CaptureMode, val rotation: Int) {
    fun metadata(): JSONObject = JSONObject().put("platform", "android").put("model", "${Build.MANUFACTURER} ${Build.MODEL}")
        .put("fps", mode.fps).put("width", mode.size.width).put("height", mode.size.height).put("rotation", rotation)
        .put("clockDomain", "android-system-nanoTime-us").put("cameraMappingVerified", verified)
        .put("startClientUs", startUs).put("endClientUs", endUs).put("framePtsUs", JSONArray(pts)).put("frameClientUs", JSONArray(clientTimes))
    companion object {
        fun load(dir: File): CaptureRecord {
            val j = JSONObject(File(dir, "frames.json").readText())
            fun longs(key: String) = j.getJSONArray(key).let { a -> (0 until a.length()).map { a.getLong(it) } }
            return CaptureRecord(dir, File(dir, "recording.mp4"), longs("framePtsUs"), longs("frameClientUs"), j.getBoolean("cameraMappingVerified"),
                j.getLong("startClientUs"), j.getLong("endClientUs"), CaptureMode("", Size(j.getInt("width"), j.getInt("height")), j.getInt("fps"), false), j.optInt("rotation"))
        }
    }
}

class CaptureEngine(private val context: Context, private val preview: TextureView,
                    private val firstFrame: (Long, CaptureMode, Boolean) -> Unit,
                    private val stopped: (CaptureRecord) -> Unit, private val error: (String) -> Unit) {
    private val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private val thread = HandlerThread("finish-camera").also { it.start() }
    private val handler = Handler(thread.looper)
    private val sessionExecutor = Executors.newSingleThreadExecutor()
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var encoder: MediaCodec? = null
    private var muxer: MediaMuxer? = null
    private var encodeSurface: Surface? = null
    private var previewSurface: Surface? = null
    private var draining: Thread? = null
    private val stopRequested = AtomicBoolean(false)
    @Volatile var recording = false; private set
    private var directory: File? = null
    private var mode: CaptureMode? = null
    private var requestedStart = 0L
    private var requestedEnd = 0L
    private var rotation = 0

    fun modes(): List<CaptureMode> {
        val result = mutableListOf<CaptureMode>()
        manager.cameraIdList.forEach { id ->
            val info = manager.getCameraCharacteristics(id)
            if (info[CameraCharacteristics.LENS_FACING] != CameraCharacteristics.LENS_FACING_BACK) return@forEach
            val map = info[CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP] ?: return@forEach
            val caps = info[CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES] ?: intArrayOf()
            if (caps.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_CONSTRAINED_HIGH_SPEED_VIDEO)) {
                map.highSpeedVideoSizes.filter { it.width <= 1920 && it.height <= 1080 }.forEach { size ->
                    map.getHighSpeedVideoFpsRangesFor(size).filter { it.lower == it.upper && it.upper in listOf(120, 240) }.forEach { fps -> result.add(CaptureMode(id, size, fps.upper, true)) }
                }
            }
            val ranges = info[CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES] ?: emptyArray()
            map.getOutputSizes(MediaCodec::class.java)?.filter { it.width <= 1920 && it.height <= 1080 && it.width >= 640 }?.forEach { size ->
                ranges.filter { it.lower == it.upper && it.upper in listOf(30, 60) }.forEach { fps ->
                    val duration = map.getOutputMinFrameDuration(MediaCodec::class.java, size)
                    if (duration == 0L || duration <= 1_000_000_000L / fps.upper + 1000) result.add(CaptureMode(id, size, fps.upper, false))
                }
            }
        }
        return result.distinct().sortedWith(compareByDescending<CaptureMode> { it.fps }.thenByDescending { it.size.width * it.size.height })
    }

    @SuppressLint("MissingPermission")
    fun start(dir: File, selected: CaptureMode) {
        check(!recording) { "录像已经开始" }; check(preview.isAvailable) { "相机预览尚未准备好" }
        directory = dir.also { it.mkdirs() }; mode = selected; stopRequested.set(false); recording = true
        requestedStart = monoUs(); requestedEnd = 0
        val info = manager.getCameraCharacteristics(selected.cameraId)
        val sensor = info[CameraCharacteristics.SENSOR_ORIENTATION] ?: 90
        val displayDegrees = when (preview.display.rotation) { Surface.ROTATION_90 -> 90; Surface.ROTATION_180 -> 180; Surface.ROTATION_270 -> 270; else -> 0 }
        rotation = (sensor - displayDegrees + 360) % 360
        try {
            val format = MediaFormat.createVideoFormat("video/avc", selected.size.width, selected.size.height).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, (selected.size.width * selected.size.height * selected.fps * .12).toInt().coerceIn(6_000_000, 80_000_000))
                setInteger(MediaFormat.KEY_FRAME_RATE, selected.fps); setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
                setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline)
                setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            }
            encoder = MediaCodec.createEncoderByType("video/avc").apply { configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE) }
            encodeSurface = encoder!!.createInputSurface()
            muxer = MediaMuxer(File(dir, "recording.mp4").absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4).apply { setOrientationHint(rotation) }
            preview.surfaceTexture!!.setDefaultBufferSize(selected.size.width, selected.size.height)
            previewSurface = Surface(preview.surfaceTexture)
            encoder!!.start()
            draining = Thread({ drain(selected) }, "finish-encoder").also { it.start() }
            manager.openCamera(selected.cameraId, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) { camera = device; createSession(device, selected) }
                override fun onDisconnected(device: CameraDevice) { error("相机断开，正在保留录像"); stop() }
                override fun onError(device: CameraDevice, code: Int) { error("相机错误 $code，正在保留录像"); stop() }
            }, handler)
        } catch (e: Exception) { error("录像启动失败：${e.message}"); stop() }
    }
    private fun createSession(device: CameraDevice, selected: CaptureMode) {
        val surfaces = listOf(previewSurface!!, encodeSurface!!)
        val callback = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(configured: CameraCaptureSession) {
                session = configured
                try {
                    val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                        surfaces.forEach { addTarget(it) }
                        set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(selected.fps, selected.fps))
                        set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
                        set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)
                    }.build()
                    if (selected.highSpeed) (configured as CameraConstrainedHighSpeedCaptureSession).setRepeatingBurst(configured.createHighSpeedRequestList(request), null, handler)
                    else configured.setRepeatingRequest(request, null, handler)
                } catch (e: Exception) { error("无法启用所选采集模式：${e.message}"); stop() }
            }
            override fun onConfigureFailed(configured: CameraCaptureSession) { error("机型不支持该预览/编码组合，请选择较低帧率"); stop() }
        }
        val outputs = surfaces.map { surface -> OutputConfiguration(surface).apply {
            if (Build.VERSION.SDK_INT >= 33 && surface === encodeSurface) timestampBase = OutputConfiguration.TIMESTAMP_BASE_MONOTONIC
        } }
        device.createCaptureSession(SessionConfiguration(if (selected.highSpeed) SessionConfiguration.SESSION_HIGH_SPEED else SessionConfiguration.SESSION_REGULAR,
            outputs, sessionExecutor, callback))
    }
    private fun drain(selected: CaptureMode) {
        val codec = encoder ?: return; val out = muxer ?: return
        val info = MediaCodec.BufferInfo(); val times = mutableListOf<Long>()
        var track = -1; var started = false; var eosSent = false; var verified = false
        try {
            while (true) {
                if (stopRequested.get() && !eosSent) { codec.signalEndOfInputStream(); eosSent = true }
                val index = codec.dequeueOutputBuffer(info, 10000)
                if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) { track = out.addTrack(codec.outputFormat); out.start(); started = true }
                else if (index >= 0) {
                    val encoded = codec.getOutputBuffer(index)!!
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) info.size = 0
                    if (info.size > 0 && started) {
                        encoded.position(info.offset); encoded.limit(info.offset + info.size)
                        out.writeSampleData(track, encoded, info)
                        val timeUs = info.presentationTimeUs
                        if (times.isEmpty()) {
                            verified = timeUs >= requestedStart - 500_000 && abs(timeUs - monoUs()) < 5_000_000
                            firstFrame(if (verified) timeUs else requestedStart, selected, verified)
                        }
                        times.add(timeUs)
                    }
                    val end = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                    codec.releaseOutputBuffer(index, false); if (end) break
                }
                if (eosSent && monoUs() - requestedEnd > 10_000_000) throw IllegalStateException("编码器未能及时结束；录像保留，需检查")
            }
        } catch (e: Exception) { error("编码错误：${e.message}") }
        finally {
            try { if (started) out.stop() } catch (_: Exception) { }; out.release()
            try { codec.stop() } catch (_: Exception) { }; codec.release()
            encodeSurface?.release(); previewSurface?.release(); camera?.close(); camera = null; session = null
            recording = false
            if (requestedEnd == 0L) requestedEnd = monoUs()
            val file = File(directory, "recording.mp4")
            try {
                val extractor = MediaExtractor(); extractor.setDataSource(file.absolutePath)
                val videoTrack = (0 until extractor.trackCount).first { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true }
                extractor.selectTrack(videoTrack); val pts = mutableListOf<Long>()
                while (extractor.sampleTime >= 0) { pts.add(extractor.sampleTime); extractor.advance() }; extractor.release()
                val ordered = times.zipWithNext().all { (a, b) -> b > a }
                verified = verified && ordered && times.size == pts.size && times.lastOrNull()?.let { it <= requestedEnd + 500_000 } == true
                require(pts.isNotEmpty()) { "没有可判读视频帧" }
                val mapped = if (verified) times.toList() else pts.map { requestedStart + it - pts.first() }
                val record = CaptureRecord(directory!!, file, pts, mapped, verified, if (verified) times.first() else requestedStart, requestedEnd, selected, rotation)
                PhoneLink.atomicWrite(File(directory, "frames.json"), record.metadata().toString())
                stopped(record)
            } catch (e: Exception) { error("录像已保留，但无法建立帧索引：${e.message}") }
        }
    }
    fun stop() {
        if (!recording || stopRequested.getAndSet(true)) return
        requestedEnd = monoUs()
        handler.post { try { session?.stopRepeating(); session?.abortCaptures(); session?.close(); camera?.close() } catch (_: Exception) { } }
    }
    fun close() { stop(); sessionExecutor.shutdown(); thread.quitSafely() }
}
