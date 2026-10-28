package cn.obstacletimer.finish

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.*
import android.media.MediaMetadataRetriever
import android.os.Bundle
import android.view.*
import android.widget.*
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors
import kotlin.math.abs
import kotlin.math.roundToInt

class MainActivity : Activity() {
    private lateinit var link: PhoneLink
    private lateinit var status: TextView
    private lateinit var raceText: TextView
    private lateinit var quality: TextView
    private lateinit var preview: TextureView
    private lateinit var review: FrameView
    private lateinit var slider: SeekBar
    private lateinit var lineSlider: SeekBar
    private lateinit var pairText: EditText
    private lateinit var modeSpinner: Spinner
    private lateinit var frameText: TextView
    private var capture: CaptureEngine? = null
    private var captureModes = emptyList<CaptureMode>()
    private var record: CaptureRecord? = null
    private var recordingRun: JSONObject? = null
    private var selectedFrame = 0
    private var selectedBitmap: Bitmap? = null
    private var evidenceBusy = false
    private var reviewer: MediaMetadataRetriever? = null
    private val decoder = Executors.newSingleThreadExecutor()
    private var frameGeneration = 0
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        buildUi()
        link = PhoneLink(this, { text -> runOnUiThread { status.text = text } }, { state -> runOnUiThread { onState(state) } })
        loadRecovery()
    }
    private fun buildUi() {
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(16, 20, 16, 12); setBackgroundColor(Color.rgb(11, 20, 28)) }
        fun label(text: String, size: Float = 14f): TextView = TextView(this).apply { this.text = text; textSize = size; setTextColor(Color.rgb(226, 239, 244)); setPadding(0, 4, 0, 4) }
        root.addView(label("OBSTACLE / 终点判读", 22f))
        status = label("连接现场电脑；原始录像始终保存在本机"); root.addView(status)
        val pairRow = LinearLayout(this)
        pairRow.addView(button("扫码配对") { require(capture?.recording != true) { "请先结束当前录像" }; if (permission()) startActivityForResult(Intent(this, ScanActivity::class.java), 10) })
        pairRow.addView(button("粘贴配对") { pairText.visibility = if (pairText.visibility == View.GONE) View.VISIBLE else View.GONE })
        pairRow.addView(button("连接") { require(capture?.recording != true) { "录像中不能更换电脑" }; if (pairText.text.isNotBlank()) link.pair(pairText.text.toString()) else status.text = "先扫码，或粘贴电脑上显示的完整配对内容" })
        root.addView(pairRow)
        pairText = EditText(this).apply { hint = "粘贴电脑上的配对 JSON"; setTextColor(Color.WHITE); visibility = View.GONE; maxLines = 3 }; root.addView(pairText)
        raceText = label("等待电脑选择选手", 19f); root.addView(raceText)
        quality = label("网络估计计时 · 精度未经现场验证"); root.addView(quality)
        val imageArea = FrameLayout(this)
        preview = TextureView(this)
        review = FrameView(this).apply { visibility = View.GONE }
        imageArea.addView(preview, FrameLayout.LayoutParams(-1, -1)); imageArea.addView(review, FrameLayout.LayoutParams(-1, -1))
        root.addView(imageArea, LinearLayout.LayoutParams(-1, (280 * resources.displayMetrics.density).roundToInt()))
        modeSpinner = Spinner(this)
        root.addView(modeSpinner)
        val controls = LinearLayout(this)
        controls.addView(button("刷新相机模式") { loadModes() })
        controls.addView(button("准备录像") { prepareRecording() })
        controls.addView(button("结束并判读") { capture?.stop() })
        root.addView(controls)
        frameText = label("逐帧选取过线画面；双指缩放，拖动画面。线位置可单独调整。")
        root.addView(frameText)
        slider = SeekBar(this).apply { max = 0; setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(bar: SeekBar?, progress: Int, user: Boolean) { if (user) showFrame(progress) }
            override fun onStartTrackingTouch(bar: SeekBar?) {} ; override fun onStopTrackingTouch(bar: SeekBar?) {}
        }) }; root.addView(slider)
        lineSlider = SeekBar(this).apply { max = 1000; progress = 500; setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(bar: SeekBar?, progress: Int, user: Boolean) { review.finishLine = progress / 1000f; review.invalidate() }
            override fun onStartTrackingTouch(bar: SeekBar?) {}; override fun onStopTrackingTouch(bar: SeekBar?) {}
        }) }; root.addView(lineSlider)
        val actions = LinearLayout(this)
        actions.addView(button("上一帧") { showFrame(selectedFrame - 1) })
        actions.addView(button("下一帧") { showFrame(selectedFrame + 1) })
        actions.addView(button("确认所选帧") { submitFrame() })
        actions.addView(button("补传") { link.retry() })
        root.addView(actions)
        setContentView(ScrollView(this).apply { isFillViewport = true; addView(root) })
    }
    private fun button(text: String, action: () -> Unit) = Button(this).apply {
        this.text = text; textSize = 12f; minWidth = 0; minimumWidth = 0; setPadding(12, 8, 12, 8)
        setOnClickListener { try { action() } catch (e: Exception) { status.text = e.message } }
    }
    private fun permission(): Boolean {
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) return true
        requestPermissions(arrayOf(Manifest.permission.CAMERA), 1); status.text = "授权相机后请再次操作"; return false
    }
    private fun onState(data: JSONObject) {
        val run = data.optJSONObject("activeRun") ?: return
        val athlete = run.getJSONObject("athlete")
        raceText.text = "${athlete.getString("bib")} / ${athlete.getString("name")} · ${run.getString("state")}"
        val id = run.getString("id")
        if (record != null && recordingRun?.optString("id") != id) {
            val previous = recordingRun?.optJSONObject("athlete")
            raceText.text = "当前录像：${previous?.optString("bib")} / ${previous?.optString("name")}；下一场请点击准备录像"
        }
        if (recordingRun?.optString("id") == id) {
            recordingRun = run
            val dir = File(link.root(), id)
            PhoneLink.atomicWrite(File(dir, "run.json"), run.toString())
            val start = run.optJSONObject("start")
            if (start != null && capture?.recording == true) status.text = "已收到 Timy3 起跑事件，持续录像；过线后点击结束并判读"
        }
    }
    private fun loadModes() {
        if (!permission()) return
        if (capture?.recording == true) return
        if (capture == null) capture = CaptureEngine(this, preview,
            { first, mode, verified -> runOnUiThread { onFirstFrame(first, mode, verified) } },
            { finished -> runOnUiThread { onStopped(finished) } },
            { text -> runOnUiThread { status.text = text } })
        captureModes = capture!!.modes()
        modeSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            captureModes.map { "${it.size.width}×${it.size.height} / ${it.fps} fps${if (it.fps < 120) " · 低帧率" else ""}" })
        require(captureModes.isNotEmpty()) { "未找到支持的后置相机编码模式" }
    }
    private fun prepareRecording() {
        require(link.connected) { "请先配对并连接电脑" }
        val run = link.currentRun ?: error("请在电脑先选择选手并创建本场")
        require(run.getString("state") in listOf("preparing", "ready")) { "当前场次不在准备阶段" }
        require(capture?.recording != true) { "录像已经开始" }
        link.clockMap()
        if (captureModes.isEmpty()) loadModes()
        val mode = captureModes.getOrNull(modeSpinner.selectedItemPosition) ?: error("请选择采集模式")
        val dir = File(link.root(), run.getString("id")).also { it.mkdirs() }
        require(!File(dir, "recording.mp4").exists()) { "本场已有录像，请在电脑取消并新建场次；不会覆盖原片" }
        recordingRun = run; record = null; selectedBitmap = null
        PhoneLink.atomicWrite(File(dir, "run.json"), run.toString())
        review.visibility = View.GONE; preview.visibility = View.VISIBLE
        capture!!.start(dir, mode)
        status.text = "相机启动中，首帧落盘后才报告就绪"
    }
    private fun onFirstFrame(first: Long, mode: CaptureMode, verified: Boolean) {
        try {
            val run = recordingRun ?: return
            val clock = link.clockMap()
            val ready = JSONObject().put("runId", run.getString("id")).put("deviceId", link.deviceId).put("clock", clock)
                .put("recordingStartClientUs", first).put("cameraTimebase", if (verified) "android-monotonic-mapped" else "android-unverified")
                .put("model", "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}").put("fps", mode.fps).put("width", mode.size.width).put("height", mode.size.height)
            PhoneLink.atomicWrite(File(File(link.root(), run.getString("id")), "ready.json"), ready.toString())
            link.send("ready", ready)
            quality.text = "${mode.fps} fps · ${if (verified) "单调时钟映射已作运行检查" else "帧时钟未映射，结果将待处理"} · 端到端精度未验证"
            status.text = "录像已开始；等待电脑确认并进入待发令"
        } catch (e: Exception) { status.text = "录像保留，但未能报告就绪：${e.message}" }
    }
    private fun onStopped(finished: CaptureRecord) {
        record = finished
        preview.visibility = View.GONE; review.visibility = View.VISIBLE
        val run = recordingRun
        if (run != null) link.send("review", JSONObject().put("runId", run.getString("id")))
        reviewer?.release(); reviewer = MediaMetadataRetriever().apply { setDataSource(finished.video.absolutePath) }
        slider.max = finished.pts.lastIndex
        showFrame(finished.pts.lastIndex)
        status.text = "录像已保存，共 ${finished.pts.size} 帧；逐帧确认过线，提交后原片仍保留"
    }
    private fun showFrame(index: Int) {
        val current = record ?: return
        val bounded = index.coerceIn(0, current.pts.lastIndex)
        selectedFrame = bounded; slider.progress = bounded
        selectedBitmap = null
        val generation = ++frameGeneration
        frameText.text = "正在解码第 ${bounded + 1}/${current.pts.size} 帧…"
        decoder.execute {
            try {
                val bitmap = reviewer?.getFrameAtIndex(bounded) ?: error("无法准确解码该帧；不会用近似关键帧代替")
                runOnUiThread {
                    if (generation != frameGeneration) return@runOnUiThread
                    selectedBitmap = bitmap; review.bitmap = bitmap; review.invalidate()
                    val start = recordingRun?.optJSONObject("start")
                    val ready = File(current.directory, "ready.json").takeIf { it.exists() }?.readText()?.let { JSONObject(it) }
                    val offset = ready?.getJSONObject("clock")?.getDouble("offsetUs")
                    val elapsed = if (start != null && offset != null && current.verified) (current.clientTimes[bounded] + offset - start.getLong("hostUs")) / 1_000_000 else null
                    frameText.text = "帧 ${bounded + 1}/${current.pts.size} · PTS ${current.pts[bounded]} μs · 试算 ${elapsed?.let { "%.3f s".format(it) } ?: "待处理"}"
                }
            } catch (e: Exception) { runOnUiThread { status.text = "判读失败：${e.message}"; selectedBitmap = null } }
        }
    }
    private fun submitFrame() {
        val current = record ?: error("请先结束录像并选择画面")
        val bitmap = selectedBitmap ?: error("所选帧尚未解码完成")
        require(!evidenceBusy) { "正在保存证据" }
        val run = recordingRun ?: error("未关联场次")
        val index = selectedFrame; val evidence = newId(); val request = newId(); val line = review.finishLine.toDouble()
        evidenceBusy = true
        decoder.execute {
            try {
                val image = File(current.directory, "$evidence.jpg")
                val evidenceBitmap = bitmap.copy(Bitmap.Config.ARGB_8888, true)
                Canvas(evidenceBitmap).drawLine((evidenceBitmap.width * line).toFloat(), 0f, (evidenceBitmap.width * line).toFloat(), evidenceBitmap.height.toFloat(), Paint().apply { color = Color.RED; strokeWidth = 2f })
                image.outputStream().use { require(evidenceBitmap.compress(Bitmap.CompressFormat.JPEG, 95, it)) }; evidenceBitmap.recycle()
                val metadata = File(current.directory, "$evidence.json")
                PhoneLink.atomicWrite(metadata, current.metadata().put("selectedFrame", index).put("finishLine", line).toString())
                val payload = JSONObject().put("requestId", request).put("runId", run.getString("id")).put("deviceId", link.deviceId)
                    .put("expectedRevision", run.optInt("revision", 0)).put("framePtsUs", current.pts[index]).put("frameClientUs", current.clientTimes[index])
                    .put("recordingEndClientUs", current.endUs).put("evidenceId", evidence).put("finishLine", line)
                    .put("note", "手机裁判手动选帧 #${index + 1}").put("cameraMappingVerified", current.verified)
                link.enqueue(current.directory, payload, image, metadata, current.video)
                runOnUiThread { status.text = "判读已保存，先传结果和截图，再补传完整录像"; evidenceBusy = false }
            } catch (e: Exception) { runOnUiThread { status.text = "保存失败：${e.message}"; evidenceBusy = false } }
        }
    }
    private fun loadRecovery() {
        val latest = link.root().listFiles()?.filter { File(it, "frames.json").exists() && File(it, "run.json").exists() }?.maxByOrNull { File(it, "frames.json").lastModified() } ?: return
        try {
            recordingRun = JSONObject(File(latest, "run.json").readText())
            onStopped(CaptureRecord.load(latest))
            status.text = "已恢复上次录像；可继续判读或连接电脑补传"
        } catch (e: Exception) { status.text = "恢复记录失败，原文件保留：${e.message}" }
    }
    @Deprecated("Android activity result bridge")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 10 && resultCode == RESULT_OK) data?.getStringExtra("qr")?.let { link.pair(it) }
    }
    override fun onPause() { super.onPause(); if (capture?.recording == true) { status.text = "应用离开前台，正在保留录像"; capture?.stop() } }
    override fun onDestroy() { capture?.close(); link.close(); decoder.shutdown(); reviewer?.release(); super.onDestroy() }
}

class FrameView(context: android.content.Context) : View(context) {
    var bitmap: Bitmap? = null
    var finishLine = .5f
    private var zoom = 1f; private var panX = 0f; private var panY = 0f
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val scale = ScaleGestureDetector(context, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScale(detector: ScaleGestureDetector): Boolean { zoom = (zoom * detector.scaleFactor).coerceIn(1f, 8f); invalidate(); return true }
    })
    private val gesture = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent) = true
        override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean { panX -= dx; panY -= dy; invalidate(); return true }
        override fun onDoubleTap(e: MotionEvent): Boolean { zoom = 1f; panX = 0f; panY = 0f; invalidate(); return true }
    })
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas); val image = bitmap ?: return
        val factor = minOf(width.toFloat() / image.width, height.toFloat() / image.height) * zoom
        val left = (width - image.width * factor) / 2 + panX; val top = (height - image.height * factor) / 2 + panY
        canvas.drawBitmap(image, null, RectF(left, top, left + image.width * factor, top + image.height * factor), paint)
        paint.color = Color.rgb(192, 240, 105); paint.strokeWidth = 2f
        val x = left + image.width * factor * finishLine
        canvas.drawLine(x, top, x, top + image.height * factor, paint)
    }
    override fun onTouchEvent(event: MotionEvent): Boolean { scale.onTouchEvent(event); gesture.onTouchEvent(event); return true }
}
