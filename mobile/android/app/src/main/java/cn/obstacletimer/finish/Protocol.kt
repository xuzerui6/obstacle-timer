package cn.obstacletimer.finish

import android.content.Context
import android.os.Build
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.roundToLong

fun monoUs(): Long = System.nanoTime() / 1000
fun newId(): String = UUID.randomUUID().toString().replace("-", "")

data class SyncPoint(val t1: Long, val t2: Long, val t3: Long, val t4: Long) {
    val rtt: Double get() = ((t4 - t1) - (t3 - t2)).toDouble()
    val offset: Double get() = ((t2 - t1).toDouble() + (t3 - t4)) / 2
}

class PhoneLink(private val context: Context, val message: (String) -> Unit,
                val state: (JSONObject) -> Unit) {
    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)
    val deviceId: String = prefs.getString("deviceId", null) ?: newId().also { prefs.edit().putString("deviceId", it).apply() }
    private val client = OkHttpClient.Builder().pingInterval(5, TimeUnit.SECONDS).connectTimeout(10, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build()
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private val uploader = Executors.newSingleThreadExecutor()
    private val uploading = AtomicBoolean(false)
    private var socket: WebSocket? = null
    @Volatile var connected = false
    @Volatile var sessionId = ""
    @Volatile var currentRun: JSONObject? = null
    @Volatile var token: String = prefs.getString("token", "") ?: ""
    @Volatile var server: String = prefs.getString("server", "") ?: ""
    private val samples = mutableListOf<SyncPoint>()
    private val pendingSync = mutableMapOf<String, Long>()
    private var latestClient = 0L
    private var closing = false
    init {
        worker.scheduleWithFixedDelay({ if (connected) { sync(); retry() } else if (server.isNotBlank() && token.isNotBlank()) connect() }, 2, 5, TimeUnit.SECONDS)
    }
    fun pair(payload: String) = worker.execute {
        try {
            val qr = JSONObject(payload)
            val address = qr.getString("server").trimEnd('/')
            val url = java.net.URI(address)
            require(url.scheme == "http" && url.host != null && url.rawUserInfo == null && url.rawPath.isNullOrEmpty()) { "二维码中的服务器地址无效" }
            val body = JSONObject().put("secret", qr.getString("secret")).put("deviceId", deviceId)
            val response = client.newCall(Request.Builder().url("$address/api/pair").post(body.toString().toRequestBody(JSON)).build()).execute()
            response.use {
                require(it.isSuccessful) { "配对失败：${it.code}" }
                val data = JSONObject(it.body!!.string())
                server = address; token = data.getString("token"); sessionId = data.getString("sessionId")
                prefs.edit().putString("server", server).putString("token", token).apply()
            }
            connect()
        } catch (e: Exception) { message("配对失败：${e.message}") }
    }
    @Synchronized private fun connect() {
        if (closing || connected || token.isBlank()) return
        socket?.cancel()
        val request = Request.Builder().url(server.replaceFirst("http://", "ws://") + "/api/ws").header("Authorization", "Bearer $token").build()
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { connected = true; message("已连接电脑，正在同步"); repeat(30) { worker.schedule({ sync() }, it * 50L, TimeUnit.MILLISECONDS) } }
            override fun onMessage(webSocket: WebSocket, text: String) {
                val t4 = monoUs()
                try {
                    val envelope = JSONObject(text); val data = envelope.getJSONObject("data")
                    when (envelope.getString("type")) {
                        "state" -> {
                            val nextSession = data.getString("sessionId")
                            synchronized(samples) { if (sessionId != nextSession) { samples.clear(); pendingSync.clear(); sessionId = nextSession } }
                            currentRun = data.optJSONObject("activeRun"); state(data)
                        }
                        "sync" -> synchronized(samples) {
                            val id = data.getString("id"); val t1 = pendingSync.remove(id)
                            if (t1 != null && t1 == data.getLong("t1") && data.getString("sessionId") == sessionId) {
                                val sample = SyncPoint(t1, data.getLong("t2"), data.getLong("t3"), t4)
                                if (sample.rtt in 0.0..2_000_000.0) { samples.add(sample); if (samples.size > 60) samples.removeAt(0); latestClient = t4 }
                            }
                        }
                        "error" -> message(data.optString("message", "电脑拒绝操作"))
                        "readyAccepted" -> message("电脑已确认录像就绪，请在电脑进入待发令")
                    }
                } catch (e: Exception) { message("消息解析失败：${e.message}") }
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { connected = false; message("连接中断，录像保留；请检查网络或重新扫码：${t.message}") }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { connected = false }
        })
    }
    private fun sync() {
        if (!connected) return
        val id = newId(); val t1 = monoUs()
        synchronized(samples) { pendingSync[id] = t1; pendingSync.entries.removeAll { t1 - it.value > 10_000_000 } }
        send("sync", JSONObject().put("id", id).put("t1", t1))
    }
    fun clockMap(): JSONObject = synchronized(samples) {
        require(samples.size >= 5 && monoUs() - latestClient < 60_000_000) { "时钟尚未同步，请等待数秒" }
        val best = samples.sortedBy { it.rtt }.take(5)
        val offsets = best.map { it.offset }.sorted()
        JSONObject().put("sessionId", sessionId).put("offsetUs", offsets[offsets.size / 2])
            .put("rttUs", best.minOf { it.rtt }).put("jitterUs", offsets.last() - offsets.first())
            .put("samples", samples.size).put("sampledClientUs", latestClient)
    }
    fun send(type: String, data: JSONObject) { socket?.send(JSONObject().put("type", type).put("data", data).toString()) }
    fun root(): File = File(context.filesDir, "races").also { it.mkdirs() }
    fun enqueue(directory: File, payload: JSONObject, image: File, metadata: File, video: File) {
        val outbox = JSONObject().put("payload", payload).put("image", image.absolutePath).put("metadata", metadata.absolutePath)
            .put("video", video.absolutePath).put("complete", false)
        atomicWrite(File(directory, "outbox-${payload.getString("requestId")}.json"), outbox.toString())
        retry()
    }
    fun retry() {
        if (uploading.compareAndSet(false, true)) uploader.execute { try { retryOutbox() } finally { uploading.set(false) } }
    }
    private fun retryOutbox() {
        if (!connected || token.isBlank()) return
        root().walkTopDown().filter { it.isFile && it.name.startsWith("outbox-") && it.extension == "json" }.toList().sortedBy { it.lastModified() }.forEach { file ->
            try {
                val item = JSONObject(file.readText()); if (item.optBoolean("complete")) return@forEach
                val payload = item.getJSONObject("payload")
                val response = post("/api/results", payload.toString().toByteArray(), JSON)
                val accepted = response.getJSONObject("result")
                item.put("acceptedRevision", accepted.getInt("revision")); atomicWrite(file, item.toString())
                for (kind in listOf("image", "metadata", "video")) upload(payload.getString("runId"), payload.getString("evidenceId"), kind, File(item.getString(kind)))
                item.put("complete", true); atomicWrite(file, item.toString())
                message("判读 v${accepted.getInt("revision")} 与完整录像已获电脑确认；本机原文件仍保留")
            } catch (e: Exception) { message("待补传：${e.message}"); return }
        }
    }
    private fun post(path: String, bytes: ByteArray, type: okhttp3.MediaType): JSONObject =
        client.newCall(Request.Builder().url(server + path).header("Authorization", "Bearer $token").post(bytes.toRequestBody(type)).build()).execute().use {
            val text = it.body?.string() ?: "{}"; require(it.isSuccessful) { "${it.code}：$text" }; JSONObject(text)
        }
    private fun upload(runId: String, id: String, kind: String, file: File) {
        require(file.exists() && file.length() > 0) { "$kind 文件尚未准备好" }
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input -> val buffer = ByteArray(65536); while (true) { val count = input.read(buffer); if (count < 0) break; digest.update(buffer, 0, count) } }
        val sha = digest.digest().joinToString("") { "%02x".format(it) }
        val base = "/api/evidence/$id/$kind?runId=$runId"
        val checkpoint = client.newCall(Request.Builder().url(server + base).header("Authorization", "Bearer $token").build()).execute().use {
            require(it.isSuccessful) { "查询上传断点失败 ${it.code}" }; JSONObject(it.body!!.string())
        }
        var offset = checkpoint.getLong("offset")
        if (checkpoint.optBoolean("complete")) return
        file.inputStream().use { input ->
            var remaining = offset
            while (remaining > 0) { val skipped = input.skip(remaining); require(skipped > 0) { "本机文件短于断点" }; remaining -= skipped }
            val buffer = ByteArray(4 * 1024 * 1024)
            while (offset < file.length()) {
                var count = 0
                while (count < buffer.size) { val n = input.read(buffer, count, buffer.size - count); if (n < 0) break; count += n }
                require(count > 0) { "上传文件被截断" }
                val ack = post("$base&offset=$offset&length=${file.length()}&sha256=$sha", buffer.copyOf(count), OCTET)
                offset = ack.getLong("offset")
                if (offset == file.length()) require(ack.getBoolean("complete")) { "电脑未确认完整文件" }
            }
        }
    }
    fun close() { closing = true; connected = false; socket?.cancel(); worker.shutdownNow(); uploader.shutdownNow() }
    companion object {
        val JSON = "application/json; charset=utf-8".toMediaType()
        val OCTET = "application/octet-stream".toMediaType()
        fun atomicWrite(file: File, value: String) {
            val temp = File(file.parentFile, file.name + ".tmp")
            java.io.FileOutputStream(temp).use { it.write(value.toByteArray(Charsets.UTF_8)); it.fd.sync() }
            check(temp.renameTo(file)) { "本机记录保存失败" }
        }
    }
}
