using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using ObstacleTimer.Core;

namespace ObstacleTimer.Desktop;

public sealed partial class TimingHub : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Store store;
    private readonly LocalCertificates certificates;
    private readonly ConcurrentDictionary<string, string> tokens = new();
    private readonly ConcurrentDictionary<string, PhoneSocket> sockets = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> uploadLocks = new();
    private WebApplication? server;
    private Process? bridge;
    private Task? bridgeTask;
    private CancellationTokenSource lifetime = new();
    private bool stopping;
    public RaceEngine Engine { get; }
    public string PairSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public int Port { get; }
    public int HttpsPort => Port + 1;
    public bool Simulation { get; }
    public string DataDirectory => store.DirectoryPath;
    public string LastError { get; private set; } = "";
    public string LastDeviceLine { get; private set; } = "等待 Timy3 计时事件";
    public event Action? Changed;
    public TimingHub(string directory, int port = 8765, bool simulation = false)
    {
        Port = port; Simulation = simulation; Engine = new RaceEngine(Wire.Id());
        Engine.RigFingerprint = RigId();
        store = new Store(directory); store.Load(Engine); store.Save(Engine, "hostStarted", new { Engine.SessionId });
        certificates = new LocalCertificates(directory);
    }
    public static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).Distinct().ToArray();
    public object Snapshot(string? deviceId = null)
    {
        lock (gate) return new
        {
            sessionId = Engine.SessionId, timyOnline = Engine.TimyOnline, phoneOnline = Engine.OnlinePhone,
            simulation = Simulation, lastError = LastError,
            visionDiagnostics=visionDiagnostics.Values.Where(d=>deviceId==null||Engine.GetHeat(d.HeatId).DeviceId==deviceId).ToArray(),
            activeRun = Engine.Runs.LastOrDefault(r => deviceId == null || r.DeviceId == deviceId),
            protocol = 3, visionModel = RaceEngine.VisionModelVersion, rigFingerprint = Engine.RigFingerprint,
            validationProfiles = Engine.ValidationProfiles.Select(p=>new{p.Id,p.CaptureFingerprint,p.ClockSource,p.Passes,count=p.Measurements.Count,p.BiasUs,p.P95AbsoluteUs,p.MaxAbsoluteUs}).ToArray(),
            currentHeat = HeatView(Engine.ActiveHeat is { } activeHeat && (deviceId == null || activeHeat.DeviceId == deviceId) ? activeHeat : Engine.Heats.LastOrDefault(h => deviceId == null || h.DeviceId == deviceId)),
            heats = deviceId == null ? Engine.Heats.ToArray() : [],
            runs = deviceId == null ? Engine.Runs.ToArray() : [],
            athletes = deviceId == null ? Engine.Athletes.ToArray() : [],
            hostUs = Wire.NowUs()
        };
    }
    private T Mutate<T>(string kind, Func<T> action)
    {
        T value;
        lock (gate)
        {
            var oldAthletes = Engine.Athletes.ToArray();
            var oldRuns = JsonSerializer.Serialize(Engine.Runs, Wire.Json);
            var oldHeats = JsonSerializer.Serialize(Engine.Heats, Wire.Json);
            var oldImpulses = Engine.DeviceImpulses.ToArray(); var oldBindings = Engine.BindingHistory.ToArray();
            var oldProfiles = Engine.ValidationProfiles.ToArray();
            try { value = action(); store.Save(Engine, kind, new { hostUs = Wire.NowUs() }); }
            catch
            {
                Engine.Athletes.Clear(); Engine.Athletes.AddRange(oldAthletes);
                Engine.Runs.Clear(); Engine.Runs.AddRange(JsonSerializer.Deserialize<List<RaceRun>>(oldRuns, Wire.Json)!);
                Engine.Heats.Clear(); Engine.Heats.AddRange(JsonSerializer.Deserialize<List<Heat>>(oldHeats, Wire.Json)!);
                Engine.DeviceImpulses.Clear(); Engine.DeviceImpulses.AddRange(oldImpulses);
                Engine.BindingHistory.Clear(); Engine.BindingHistory.AddRange(oldBindings);
                Engine.ValidationProfiles.Clear(); Engine.ValidationProfiles.AddRange(oldProfiles);
                throw;
            }
        }
        Notify(); return value;
    }
    public Athlete AddAthlete(string bib, string name, string team) => Mutate("athleteAdded", () =>
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(bib)) throw new DomainException("请输入号码和姓名");
        if (Engine.Athletes.Any(a => a.Bib == bib.Trim())) throw new DomainException("号码已存在");
        var a = new Athlete(Wire.Id(), bib.Trim(), name.Trim(), team.Trim()); Engine.Athletes.Add(a); return a;
    });
    public RaceRun Prepare(string athlete) => Mutate("runPrepared", () => Engine.Prepare(athlete));
    public void Arm(string run) => Mutate("runArmed", () => { Engine.Arm(run); return true; });
    public void SetStatus(string run, RunState state, string note) => Mutate("statusChanged", () => { Engine.SetStatus(run, state, note); return true; });
    private void Notify()
    {
        Changed?.Invoke();
        foreach (var p in sockets) _ = p.Value.Send(Wire.Pack("state", Snapshot(p.Key)), lifetime.Token);
    }
    public void Inject(string raw)
    {
        if (!Simulation) throw new DomainException("模拟输入未启用");
        lock (gate) Engine.TimyOnline = true;
        HandleLine(raw, "simulated", "simulation-" + Engine.SessionId, Wire.NowUs(), "simulation");
    }
    private void HandleLine(string raw, string device, string connection, long hostUs, string source = "timy")
    {
        LastDeviceLine = raw;
        var impulse = TimyParser.Parse(raw, device, connection, hostUs, source);
        if (impulse == null) { lock (gate) store.Log("raw", new { raw, device, connection, hostUs }); Changed?.Invoke(); return; }
        Mutate("impulse", () => { store.Log("deviceImpulse", impulse); return Engine.OnImpulse(impulse); });
    }
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(Port);
            options.ListenAnyIP(HttpsPort, endpoint => endpoint.UseHttps(certificates.Server));
        });
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
        server = builder.Build(); server.UseWebSockets();
        string webRoot = Path.Combine(AppContext.BaseDirectory, "phone");
        if (Directory.Exists(webRoot))
        {
            var files = new PhysicalFileProvider(webRoot);
            server.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/phone" });
            server.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = "/phone" });
        }
        server.Use(async (ctx, next) =>
        {
            try
            {
                if (ctx.Request.Path != "/api/health" && ctx.Request.Path != "/api/pair" && ctx.Request.Path != "/setup" &&
                    ctx.Request.Path != "/camera-ca.cer" && ctx.Request.Path != "/camera-ca.mobileconfig" && ctx.Request.Path != "/" && ctx.Request.Path != "/favicon.ico")
                {
                    string token = ctx.Request.Headers.Authorization.ToString();
                    if (ctx.Request.Path == "/api/ws" && ctx.Request.IsHttps && string.IsNullOrEmpty(token)) token = ctx.Request.Query["access_token"].ToString();
                    if (token.StartsWith("Bearer ")) token = token[7..];
                    if (!tokens.TryGetValue(token, out var device)) { ctx.Response.StatusCode = 401; return; }
                    ctx.Items["device"] = device;
                }
                await next(ctx);
            }
            catch (DomainException ex) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, Wire.Json); }
            catch (JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "消息格式错误" }, Wire.Json); }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                LastError = ex.Message; Changed?.Invoke(); ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = "操作未保存：" + ex.Message }, Wire.Json);
            }
        });
        server.MapGet("/api/health", () => new { name = "ObstacleTimer", protocol = 3, sessionId = Engine.SessionId });
        server.MapGet("/", () => Results.Redirect("/setup"));
        server.MapGet("/favicon.ico", () => Results.NoContent());
        server.MapGet("/setup", (HttpContext c) => Results.Content(LocalCertificates.SetupHtml(c.Request.Host.Host, HttpsPort), "text/html; charset=utf-8"));
        server.MapGet("/camera-ca.cer", () => Results.File(certificates.Root.RawData, "application/x-x509-ca-cert", "camera-ca.cer"));
        server.MapGet("/camera-ca.mobileconfig", () => Results.File(certificates.AppleProfile(), "application/x-apple-aspen-config", "camera-ca.mobileconfig"));
        if (!Directory.Exists(webRoot))
            server.MapGet("/phone/{**path}", () => Results.Content("手机网页尚未构建，请使用完整安装包或先构建 web/phone。", "text/plain; charset=utf-8", statusCode: 503));
        server.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            var p = await JsonSerializer.DeserializeAsync<PairRequest>(ctx.Request.Body, Wire.Json) ?? throw new DomainException("缺少配对信息");
            if (p.Secret != PairSecret || !SafeId(p.DeviceId)) { ctx.Response.StatusCode = 403; return; }
            lock (gate)
            {
                if (Engine.ActiveHeat is { } heat && heat.DeviceId != p.DeviceId) throw new DomainException("已有手机绑定当前组次");
                if (Engine.Active is { DeviceId: not null } run && run.DeviceId != p.DeviceId) throw new DomainException("已有手机绑定当前比赛");
            }
            string access = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            tokens[access] = p.DeviceId;
            await ctx.Response.WriteAsJsonAsync(new { token = access, sessionId = Engine.SessionId, protocol = 3 }, Wire.Json);
        });
        server.MapGet("/api/state", (HttpContext c) => Results.Json(Snapshot(Device(c)), Wire.Json));
        server.MapMethods("/api/ws", ["GET", "CONNECT"], ServeSocket);
        server.MapPost("/api/results", async (HttpContext ctx) =>
        {
            var finish = await JsonSerializer.DeserializeAsync<FinishSubmission>(ctx.Request.Body, Wire.Json) ?? throw new DomainException("缺少判读结果");
            if (finish.DeviceId != Device(ctx)) throw new DomainException("设备身份不匹配");
            if (!SafeId(finish.EvidenceId) || !SafeId(finish.RequestId)) throw new DomainException("无效的证据或请求标识");
            var result = Mutate("finishConfirmed", () => Engine.Submit(finish));
            await ctx.Response.WriteAsJsonAsync(new { accepted = true, result, run = Engine.Get(finish.RunId) }, Wire.Json);
        });
        server.MapMethods("/api/evidence/{id}/{kind}", ["GET", "POST"], UploadEvidence);
        MapHeatEndpoints(server);
        MapVisionEndpoints(server);
        await server.StartAsync(lifetime.Token);
        if (Simulation) Engine.TimyOnline = true;
        else bridgeTask = Task.Run(() => WatchBridge(lifetime.Token));
    }
    private static string Device(HttpContext c) => (string)c.Items["device"]!;
    private static bool SafeId(string? value) => value is { Length: > 0 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task ServeSocket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        string device = Device(ctx);
        lock (gate)
        {
            if (Engine.OnlinePhone != null && Engine.OnlinePhone != device) throw new DomainException("只允许一台终点手机在线");
            Engine.OnlinePhone = device;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var peer = new PhoneSocket(socket);
        if (sockets.TryGetValue(device, out var old)) old.Abort();
        sockets[device] = peer; Notify();
        try
        {
            var buffer = new byte[16384];
            while (socket.State == WebSocketState.Open)
            {
                using var body = new MemoryStream(); WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || body.Length + received.Count > 65536) throw new DomainException("消息过大或类型无效");
                    body.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                long receivedUs = Wire.NowUs();
                try
                {
                    using var doc = JsonDocument.Parse(body.ToArray());
                    string type = doc.RootElement.GetProperty("type").GetString()!;
                    var data = doc.RootElement.GetProperty("data");
                    if (type == "sync")
                    {
                        string id=data.GetProperty("id").GetString()!;long t1=data.GetProperty("t1").GetInt64(),t3=Wire.NowUs();
                        await peer.Send(Wire.Pack("sync",new{id,t1,t2=receivedUs,t3,sessionId=Engine.SessionId,proof=SyncProof(device,id,t1,receivedUs,t3)}),lifetime.Token);
                    }
                    else if (type == "recordingReady")
                    {
                        var ready = data.Deserialize<RecordingReady>(Wire.Json)!;
                        if (ready.DeviceId != device || !SafeId(ready.RecordingId)) throw new DomainException("手机或录像标识无效");
                        Mutate("recordingReady", () => { Engine.RegisterRecording(ready); return true; });
                        await peer.Send(Wire.Pack("recordingAccepted", new { ready.HeatId, ready.RecordingId }), lifetime.Token);
                    }
                    else if (type == "recordingStatus")
                        Mutate("recordingStatus", () => { Engine.RecordingUpdate(data.Deserialize<RecordingStatus>(Wire.Json)!, device); return true; });
                    else if(type=="visionStatus")ReceiveVisionDiagnostic(data,device);
                    else if (type == "ready")
                    {
                        var ready = data.Deserialize<PhoneReady>(Wire.Json)!;
                        if (ready.DeviceId != device) throw new DomainException("手机身份不符");
                        Mutate("phoneReady", () => { Engine.SetReady(ready); return true; });
                        await peer.Send(Wire.Pack("readyAccepted", new { ready.RunId }), lifetime.Token);
                    }
                    else if (type == "review")
                        Mutate("phoneReview", () => { Engine.MarkReview(data.GetProperty("runId").GetString()!, device); return true; });
                    else if (type == "ping") await peer.Send(Wire.Pack("pong", new { hostUs = Wire.NowUs() }), lifetime.Token);
                }
                catch (Exception ex) when (ex is DomainException or JsonException or InvalidOperationException or KeyNotFoundException)
                { await peer.Send(Wire.Pack("error", new { message = ex.Message }), lifetime.Token); }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            if (sockets.TryGetValue(device, out var current) && ReferenceEquals(current, peer))
            {
                sockets.TryRemove(device, out _); Mutate("phoneDisconnected", () => { Engine.PhoneDisconnected(device); return true; });
            }
        }
    }
    private async Task UploadEvidence(HttpContext ctx)
    {
        string id = ctx.Request.RouteValues["id"]?.ToString() ?? "", kind = ctx.Request.RouteValues["kind"]?.ToString() ?? "";
        string runId = ctx.Request.Query["runId"].ToString();
        if (!SafeId(id) || kind is not ("image" or "video" or "metadata" or "before" or "after" or "mask")) throw new DomainException("证据标识无效");
        string videoExtension;
        bool shared = ctx.Request.Query["heatId"].Count > 0;
        if (shared)
        {
            runId = ctx.Request.Query["heatId"].ToString();
            lock (gate)
            {
                var h = Engine.GetHeat(runId);
                if (h.DeviceId != Device(ctx)) throw new DomainException("组次未绑定该手机");
                if (kind == "video")
                {
                    if (id != h.RecordingId) throw new DomainException("原片标识与组次不符");
                    if (!Engine.Members(h).SelectMany(r => r.Reviews).Any(v => store.HasEvidence(v.Submission.EvidenceId, "image", h.Id)) &&
                        !h.VisionEvents.Any(v=>store.HasEvidence(v.Id,"image",h.Id)))
                        throw new DomainException("请先提交判读及截图，再上传整组原片");
                }
                else if (!Engine.Members(h).SelectMany(r => r.Reviews).Any(v => v.Submission.EvidenceId == id) && !h.VisionEvents.Any(v=>v.Id==id))
                    throw new DomainException("证据尚未关联本组判读");
                videoExtension = h.VideoMime == "video/webm" ? "webm" : "mp4";
            }
        }
        else lock (gate)
        {
            var run = Engine.Get(runId);
            if (run.DeviceId != Device(ctx) || !run.Revisions.Any(r => r.EvidenceId == id)) throw new DomainException("证据未关联到该手机的判读结果");
            if (kind == "video" && !store.HasEvidence(id, "image", runId)) throw new DomainException("请先上传截图");
            videoExtension = run.Revisions.Last(r => r.EvidenceId == id).VideoMime == "video/webm" ? "webm" : "mp4";
        }
        var semaphore = uploadLocks.GetOrAdd(id + kind, _ => new SemaphoreSlim(1));
        await semaphore.WaitAsync(ctx.RequestAborted);
        try
        {
            string directory = Path.Combine(DataDirectory, "evidence", runId); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, id + (kind is "before" or "after" ? "."+kind : kind=="mask"?".mask":"") + "." + (kind == "video" ? videoExtension : kind is "image" or "before" or "after" ? "jpg" : kind=="mask" ? "png" : "json"));
            string partial = path + ".partial";
            if (ctx.Request.Method == "GET")
            {
                bool acknowledged;
                lock (gate) acknowledged = File.Exists(path) && store.HasEvidence(id, kind, runId);
                // Crash between atomic rename and SQLite registration: request a fresh POST so its
                // declared hash is checked against the existing file before acknowledging it.
                await ctx.Response.WriteAsJsonAsync(new { offset = acknowledged ? new FileInfo(path).Length : File.Exists(partial) ? new FileInfo(partial).Length : 0,
                    complete = acknowledged }, Wire.Json); return;
            }
            if (!long.TryParse(ctx.Request.Query["offset"], out long offset) ||
                !long.TryParse(ctx.Request.Query["length"], out long total) || total is < 1 or > 4_294_967_296L)
                throw new DomainException("上传长度无效");
            string hash = ctx.Request.Query["sha256"].ToString().ToLowerInvariant();
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new DomainException("需要 SHA256 校验值");
            if (kind != "video" && total > 16 * 1024 * 1024) throw new DomainException("截图/元数据超过限制");
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(existing, ctx.RequestAborted)).ToLowerInvariant();
                if (actual != hash || existing.Length != total) throw new DomainException("证据已经存在，内容不同");
                lock (gate) store.AddEvidence(id, kind, runId, hash, total, path);
                if(shared)Mutate("visionEvidenceRecovered",()=>{UpdateVisionEvidence(runId);return true;});
                await ctx.Response.WriteAsJsonAsync(new { complete = true, offset = total }, Wire.Json); return;
            }
            long current = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset != current || current > total) throw new DomainException("上传位置冲突，请查询断点");
            await using (var output = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.None, 65536, true))
            {
                try
                {
                    byte[] bytes = new byte[65536]; long written = 0; int count;
                    while ((count = await ctx.Request.Body.ReadAsync(bytes, ctx.RequestAborted)) > 0)
                    {
                        written += count;
                        if (written > 8 * 1024 * 1024 || output.Length + count > total) throw new DomainException("上传块超过限制");
                        await output.WriteAsync(bytes.AsMemory(0, count), ctx.RequestAborted);
                    }
                    await output.FlushAsync(ctx.RequestAborted);
                }
                catch { output.SetLength(current); throw; }
            }
            current = new FileInfo(partial).Length;
            if (current == total)
            {
                await using (var input = File.OpenRead(partial))
                {
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ctx.RequestAborted)).ToLowerInvariant();
                    if (actual != hash) { input.Close(); File.Delete(partial); throw new DomainException("完整文件校验失败，请重新上传"); }
                }
                File.Move(partial, path);
                lock (gate) store.AddEvidence(id, kind, runId, hash, total, path);
                if(shared)Mutate("visionEvidenceReceived",()=>{UpdateVisionEvidence(runId);return true;});
                Notify();
            }
            await ctx.Response.WriteAsJsonAsync(new { offset = current, complete = current == total }, Wire.Json);
        }
        finally { semaphore.Release(); }
    }
    private async Task WatchBridge(CancellationToken cancel)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "bridge", "ObstacleTimer.TimyBridge.exe");
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(executable)) { LastError = "未找到 USB 接收进程，请使用完整安装包或运行构建脚本"; Notify(); return; }
                bridge = new Process { StartInfo = new ProcessStartInfo(executable)
                    { Arguments = "--parent-pid " + Environment.ProcessId, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 } };
                bridge.Start(); _ = bridge.StandardError.ReadToEndAsync(cancel);
                while (!bridge.HasExited && !cancel.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    string? line;
                    try { line = await bridge.StandardOutput.ReadLineAsync(deadline.Token); }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        LastError = "USB 接收进程超过 5 秒无响应，正在重启接收";
                        bridge.Kill(true); break;
                    }
                    if (line == null) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString(); var data = root.GetProperty("data");
                        if (type == "line") HandleLine(data.GetProperty("raw").GetString()!, data.GetProperty("deviceId").GetString()!,
                            data.GetProperty("connectionId").GetString()!, data.GetProperty("hostUs").GetInt64());
                        else if (type is "connected" or "disconnected" or "status" or "bridgeReady" or "heartbeat")
                        {
                            bool online = data.GetProperty("count").GetInt32() > 0;
                            lock (gate)
                            {
                                if (online) Engine.TimyOnline = true;
                                else if (Engine.TimyOnline)
                                {
                                    Engine.DeviceUnavailable(); store.Save(Engine, "deviceDisconnected", new { hostUs = Wire.NowUs() });
                                }
                                if (!online && Engine.Active?.State == RunState.Armed) Engine.Active.State = RunState.Ready;
                            }
                            Changed?.Invoke();
                        }
                        else if (type == "error") { LastError = data.GetProperty("message").GetString()!; Notify(); }
                    }
                    catch (Exception ex) { LastError = ex.Message; Changed?.Invoke(); }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception ex) { LastError = "USB 接收：" + ex.Message; }
            finally
            {
                lock (gate) Engine.DeviceUnavailable();
                Notify();
                try { if (bridge is { HasExited: false }) bridge.Kill(true); } catch (InvalidOperationException) { }
                bridge?.Dispose(); bridge = null;
            }
            try { await Task.Delay(3000, cancel); } catch (OperationCanceledException) { return; }
        }
    }
    public void ExportCsv(string path)
    {
        static string Cell(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        lock (gate)
        {
            var rows = new List<string> { "组次,组内名次,场次,号码,姓名,队伍,状态,原始微秒,显示秒,精度微秒,舍入,版本,计时质量,终点事件,判读版本,备注,视频事件,视频候选微秒,备份微秒,视频减备份微秒,采用来源,视频核验状态,验证配置" };
            foreach (var r in Engine.Runs)
                rows.Add(string.Join(',', new[] { Cell(r.HeatId == null ? "旧版记录" : Engine.GetHeat(r.HeatId).Name), RaceEngine.RankOf(r, Engine.Runs.Where(member => member.HeatId == r.HeatId))?.ToString() ?? "", Cell(r.Id), Cell(r.Athlete.Bib), Cell(r.Athlete.Name), Cell(r.Athlete.Team), Cell(r.State.ToString()),
                    r.ElapsedUs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", r.ElapsedUs.HasValue ? r.Profile?.Format(r.ElapsedUs.Value) ?? (r.ElapsedUs.Value / 1_000_000d).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.Profile?.UnitUs.ToString() ?? "", r.Profile?.Rounding.ToString() ?? "", r.Revision.ToString(), Cell(r.Quality), Cell(r.FinishEventId), r.Reviews.Count.ToString(), Cell(r.Revisions.LastOrDefault()?.Note), Cell(r.VisionEventId), Engine.VisionEvents.FirstOrDefault(v=>v.Id==r.VisionEventId)?.EstimatedElapsedUs?.ToString()??"", r.BackupElapsedUs?.ToString()??"", (Engine.VisionEvents.FirstOrDefault(v=>v.Id==r.VisionEventId)?.EstimatedElapsedUs-r.BackupElapsedUs)?.ToString()??"",Cell(r.OfficialSource),Cell(r.VisionStatus),Cell(Engine.VisionEvents.FirstOrDefault(v=>v.Id==r.VisionEventId)?.ValidationId) }));
            File.WriteAllLines(path, rows, new UTF8Encoding(true));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (stopping) return; stopping = true; lifetime.Cancel();
        foreach (var p in sockets.Values) p.Abort();
        try { if (bridge is { HasExited: false }) bridge.Kill(true); } catch (InvalidOperationException) { }
        if (server != null) { await server.StopAsync(); await server.DisposeAsync(); }
        if (bridgeTask != null) await bridgeTask;
        store.Dispose(); certificates.Dispose(); lifetime.Dispose();
    }
    private sealed record PairRequest(string Secret, string DeviceId);
    private sealed class PhoneSocket(WebSocket socket)
    {
        private readonly SemaphoreSlim sendGate = new(1);
        public void Abort() => socket.Abort();
        public async Task Send(string message, CancellationToken cancel)
        {
            try
            {
                await sendGate.WaitAsync(cancel);
                try { if (socket.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel); }
                finally { sendGate.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
