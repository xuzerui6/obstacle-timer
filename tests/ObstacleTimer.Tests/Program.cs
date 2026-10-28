using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ObstacleTimer.Core;
using ObstacleTimer.Desktop;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (DomainException) { Check(true, name); return; } throw new Exception("Should reject: " + name); }
const long now = 1_000_000_000;
TimingImpulse Impulse(string raw = "0001 c0 23:59:59.9999 00", long time = now + 1_000_000) => TimyParser.Parse(raw, "usb", "connection", time)!;
var parsed = Impulse();
Check(parsed.Channel == "C0" && parsed.DeviceTimeUs == 86_399_999_900 && !parsed.Manual, "hardware C0 and 100us precision");
Check(Impulse("0015 c0M 15:43:55,62 00").Manual, "manual marker retained");
Check(Impulse("?0001 C0M 12:34:54.09   00").DeviceTimeUs == 45_294_090_000, "actual captured Timy3 question-mark C0M row");
Check(TimyParser.Parse("m 0001 c0 15:44:15,7847 00", "d", "c", now) == null, "memory replay is not a live start");
Check(TimyParser.Parse("c 0023 c1 15:44:15,7847 00", "d", "c", now) == null, "deleted events rejected");
Check(TimyParser.Parse("i 0023 c0 15:44:15,7847 00", "d", "c", now) == null, "edited events cannot start a race");
Check(TimyParser.Parse("07:50:43.2", "d", "c", now) == null, "rolling display is not a timing impulse");
Check(TimyParser.Parse("0001 c0 25:00:00.0000 00", "d", "c", now) == null, "invalid time rejected");
Check(Impulse("0001 C0 00:00:00.0001 00").DeviceTimeUs == 100, "midnight parsed without wall-clock subtraction");
var samples = Enumerable.Range(0, 20).Select(i => new SyncSample(1_000_000 + i * 1000, 1_005_100 + i * 1000, 1_005_200 + i * 1000, 1_000_300 + i * 1000));
var estimate = SyncEstimator.Estimate("s", samples);
Check(estimate.OffsetUs == 5000 && estimate.RttUs == 200, "four timestamp offset and RTT");
Reject(() => SyncEstimator.Estimate("s", samples.Take(4)), "insufficient sync samples");
var engine = new RaceEngine("session", () => now) { OnlinePhone = "phone", TimyOnline = true };
engine.Athletes.Add(new("a", "1", "测试运动员", "测试队"));
var run = engine.Prepare("a");
Reject(() => engine.Arm(run.Id), "cannot arm before recording");
Check(engine.OnImpulse(parsed) == null && run.Start == null, "early start is logged but not bound");
var clockMap = new ClockMap("session", 10_000, 2000, 100, 20, now - 10_000);
var ready = new PhoneReady(run.Id, "phone", clockMap, now - 20_000, "monotonic-mapped", "Test", 240, 1280, 720);
Reject(() => engine.SetReady(ready with { DeviceId = "other" }), "another phone cannot bind race");
Reject(() => engine.SetReady(ready with { Clock = clockMap with { SessionId = "other" } }), "different clock session rejected");
engine.SetReady(ready); engine.Arm(run.Id);
var accepted = Impulse("0002 c0 00:00:00.1000 00");
Check(engine.OnImpulse(accepted) == run, "armed race receives hardware start");
Check(engine.OnImpulse(accepted) == null, "duplicate start ignored");
engine.PhoneDisconnected("phone");
Check(run.State == RunState.Running && run.Start != null, "disconnect keeps running race and start");
engine.OnlinePhone = "phone";
var finish = new FinishSubmission("request1", run.Id, "phone", 0, 5_000_000, now + 6_000_000 - 10_000, now + 7_000_000, "evidence1", .5, "裁判确认", true);
var result = engine.Submit(finish);
Check(result.ElapsedUs == 5_000_000 && run.State == RunState.Confirmed, "result uses frozen phone clock mapping");
Check(engine.Submit(finish).Revision == 1 && run.Revision == 1, "result retry is idempotent");
Reject(() => engine.Submit(finish with { FrameClientUs = finish.FrameClientUs + 1 }), "changed content under same request ID rejected");
Reject(() => engine.Submit(finish with { RequestId = "request2" }), "stale correction rejected");
engine.Submit(finish with { RequestId = "request2", ExpectedRevision = 1, EvidenceId = "evidence2", FrameClientUs = finish.FrameClientUs + 1000 });
Check(run.Revision == 2 && run.Revisions[0].ElapsedUs == 5_000_000, "correction preserves original");
engine.SetStatus(run.Id, RunState.Dsq, "规则判罚");
Check(!engine.Ranking().Any() && run.Revision == 3, "DSQ removes ranking and preserves history");
var orphan = engine.Prepare("a"); engine.SetReady(ready with { RunId = orphan.Id }); engine.MarkReview(orphan.Id, "phone");
Reject(() => engine.Submit(finish with { RunId = orphan.Id, RequestId = "orphan" }), "evidence cannot be reused across athletes");
engine.Submit(finish with { RunId = orphan.Id, RequestId = "orphan", EvidenceId = "orphan-evidence" });
Check(orphan.State == RunState.Pending && orphan.ElapsedUs == null, "missing start remains pending");
engine.RecoverAfterRestart(); Check(orphan.State == RunState.Pending, "restarted host does not reuse clock mapping");

// Fifty synthetic entrants exercise isolation and revision history. This is NOT field acceptance.
var batch = new RaceEngine("batch", () => now) { OnlinePhone = "phone", TimyOnline = true };
for (int i = 0; i < 50; i++)
{
    string athleteId = "a" + i; batch.Athletes.Add(new(athleteId, (i + 1).ToString(), "运动员" + i, "队伍"));
    var race = batch.Prepare(athleteId);
    var batchMap = clockMap with { SessionId = "batch" };
    batch.SetReady(ready with { RunId = race.Id, Clock = batchMap }); batch.Arm(race.Id);
    var start = Impulse($"{i + 1:0000} c0 12:00:{i:00}.0000 00"); batch.OnImpulse(start);
    batch.Submit(finish with { RunId = race.Id, RequestId = "batch-" + i, EvidenceId = "batch-evidence-" + i,
        FrameClientUs = start.HostUs + 5_000_000 + i * 1000 - 10_000, RecordingEndClientUs = start.HostUs + 10_000_000 });
}
Check(batch.Runs.Count == 50 && batch.Runs.Select(r => r.Athlete.Id).Distinct().Count() == 50 && !batch.Ranking().Any(),
    "50 legacy synthetic entrants isolated and excluded from hardware ranking");
Check(batch.Runs.All(r => r.Revision == 1 && r.ElapsedUs >= 5_000_000 && r.ElapsedUs <= 5_049_000), "50 synthetic results preserve exact microseconds");

var browserEngine = new RaceEngine("browser", () => now) { OnlinePhone = "phone", TimyOnline = true };
browserEngine.Athletes.Add(new("web", "1", "网页选手", "")); var browserRun = browserEngine.Prepare("web");
browserEngine.SetReady(ready with { RunId = browserRun.Id, Clock = clockMap with { SessionId = "browser" }, CameraTimebase = "browser-presentation-estimate-mapped" });
browserEngine.Arm(browserRun.Id); browserEngine.OnImpulse(Impulse());
var browserFinish = finish with { RunId = browserRun.Id, RequestId = "web-result", EvidenceId = "web-evidence", VideoMime = "video/webm", TimingMethod = "browser-estimate" };
Reject(() => browserEngine.Submit(browserFinish with { TimingMethod = "native" }), "browser cannot be mislabeled as native capture");
browserEngine.Submit(browserFinish); Check(browserRun.Quality.Contains("浏览器") && browserRun.Revisions[0].VideoMime == "video/webm", "browser timing quality and WebM format retained");
browserEngine.SetStatus(browserRun.Id, RunState.Dsq, "人工判罚");
Check(browserRun.Revisions.Last().VideoMime == "video/webm", "status change preserves original WebM evidence type");

// Actual HTTP/WebSocket, SQLite reload and resumable integrity-checked evidence upload.
string directory = Path.Combine(Path.GetTempPath(), "ObstacleTimer-tests-" + Wire.Id());
using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
await using (var hub = new TimingHub(directory, port, true))
{
    await hub.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    var unauthorized = await http.GetAsync("/api/state"); Check(unauthorized.StatusCode == System.Net.HttpStatusCode.Unauthorized, "unauthenticated access denied");
    var pair = await http.PostAsync("/api/pair", new StringContent(JsonSerializer.Serialize(new { secret = hub.PairSecret, deviceId = "phone" }), Encoding.UTF8, "application/json"));
    pair.EnsureSuccessStatusCode(); var pairData = JsonDocument.Parse(await pair.Content.ReadAsStringAsync()); string token = pairData.RootElement.GetProperty("token").GetString()!;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), CancellationToken.None);
    var buffer = new byte[65536]; await ws.ReceiveAsync(buffer, CancellationToken.None);
    var athlete = hub.AddAthlete("99", "集成测试", "本地"); var live = hub.Prepare(athlete.Id);
    long at = Wire.NowUs();
    var liveReady = new PhoneReady(live.Id, "phone", new ClockMap(hub.Engine.SessionId, 0, 1000, 1, 30, at), at - 10000, "monotonic-mapped", "Test", 240, 1280, 720);
    await ws.SendAsync(Encoding.UTF8.GetBytes(Wire.Pack("ready", liveReady)), WebSocketMessageType.Text, true, CancellationToken.None);
    for (int i = 0; i < 100 && live.State != RunState.Ready; i++) await Task.Delay(10);
    hub.Inject("0010 c1 10:00:00.0000 00"); // Establishes simulated-online without issuing C0.
    hub.Arm(live.Id); hub.Inject("0011 c0 10:00:01.0000 00");
    Check(live.State == RunState.Running, "WebSocket ready -> arm -> simulated USB starts race");
    var liveFinish = new FinishSubmission("request-http", live.Id, "phone", 0, 1000000, live.Start!.HostUs + 1000000,
        live.Start.HostUs + 2000000, "evidence-http", .5, "集成判读", true);
    var submission = await http.PostAsync("/api/results", new StringContent(JsonSerializer.Serialize(liveFinish, Wire.Json), Encoding.UTF8, "application/json"));
    submission.EnsureSuccessStatusCode();
    Check(live.ElapsedUs == 1000000 && !hub.Engine.Ranking().Any(), "HTTP result persisted; simulation excluded from ranking");
    byte[] bytes = Encoding.UTF8.GetBytes("reference screenshot integrity bytes");
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    string endpoint = $"/api/evidence/evidence-http/image?runId={live.Id}&length={bytes.Length}&sha256={hash}";
    var chunk = await http.PostAsync(endpoint + "&offset=0", new ByteArrayContent(bytes[..10])); chunk.EnsureSuccessStatusCode();
    var offset = JsonDocument.Parse(await http.GetStringAsync($"/api/evidence/evidence-http/image?runId={live.Id}"));
    Check(offset.RootElement.GetProperty("offset").GetInt64() == 10, "upload checkpoint persisted");
    var conflict = await http.PostAsync(endpoint + "&offset=0", new ByteArrayContent(bytes)); Check(conflict.StatusCode == System.Net.HttpStatusCode.Conflict, "wrong resume offset rejected");
    chunk = await http.PostAsync(endpoint + "&offset=10", new ByteArrayContent(bytes[10..])); chunk.EnsureSuccessStatusCode();
    Check(JsonDocument.Parse(await chunk.Content.ReadAsStringAsync()).RootElement.GetProperty("complete").GetBoolean(), "resumed upload acknowledged after SHA256");
    var replay = await http.PostAsync(endpoint + "&offset=0", new ByteArrayContent(bytes)); replay.EnsureSuccessStatusCode(); Check(true, "completed upload retry is idempotent");
    using (var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "timing.sqlite")))
    {
        db.Open(); using var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM evidence WHERE id='evidence-http' AND kind='image'"; delete.ExecuteNonQuery();
    }
    var recoveryCheckpoint = JsonDocument.Parse(await http.GetStringAsync($"/api/evidence/evidence-http/image?runId={live.Id}"));
    Check(!recoveryCheckpoint.RootElement.GetProperty("complete").GetBoolean() && recoveryCheckpoint.RootElement.GetProperty("offset").GetInt64() == 0,
        "file rename without DB acknowledgment requests revalidation");
    var recovered = await http.PostAsync(endpoint + "&offset=0", new ByteArrayContent(bytes)); recovered.EnsureSuccessStatusCode();
    Check(JsonDocument.Parse(await recovered.Content.ReadAsStringAsync()).RootElement.GetProperty("complete").GetBoolean(), "existing file hash revalidated after crash window");
    var corrected = liveFinish with { RequestId = "corrected-http", ExpectedRevision = 1, EvidenceId = "evidence-corrected", Note = "更正" };
    var correction = await http.PostAsync("/api/results", new StringContent(JsonSerializer.Serialize(corrected, Wire.Json), Encoding.UTF8, "application/json")); correction.EnsureSuccessStatusCode();
    var oldEvidence = await http.GetAsync($"/api/evidence/evidence-http/image?runId={live.Id}");
    Check(oldEvidence.IsSuccessStatusCode, "previous revision evidence remains accessible for resuming uploads");
    string correctedEndpoint = $"/api/evidence/evidence-corrected/image?runId={live.Id}&length={bytes.Length}";
    var corrupt = await http.PostAsync(correctedEndpoint + "&offset=0&sha256=" + new string('0', 64), new ByteArrayContent(bytes));
    Check(corrupt.StatusCode == System.Net.HttpStatusCode.Conflict, "corrupt full upload never acknowledged");
    var reset = JsonDocument.Parse(await http.GetStringAsync($"/api/evidence/evidence-corrected/image?runId={live.Id}"));
    Check(reset.RootElement.GetProperty("offset").GetInt64() == 0, "corrupt partial reset permits retry");
    var missingImage = await http.PostAsync($"/api/evidence/evidence-corrected/video?runId={live.Id}&length={bytes.Length}&offset=0&sha256={hash}", new ByteArrayContent(bytes));
    Check(missingImage.StatusCode == System.Net.HttpStatusCode.Conflict, "video waits for its corresponding screenshot");
    var otherPair = await http.PostAsync("/api/pair", new StringContent(JsonSerializer.Serialize(new { secret = hub.PairSecret, deviceId = "other-phone" }), Encoding.UTF8, "application/json"));
    otherPair.EnsureSuccessStatusCode(); var otherToken = JsonDocument.Parse(await otherPair.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
    using var otherHttp = new HttpClient { BaseAddress = http.BaseAddress }; otherHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
    var crossDevice = await otherHttp.PostAsync("/api/results", new StringContent(JsonSerializer.Serialize(corrected, Wire.Json), Encoding.UTF8, "application/json"));
    Check(crossDevice.StatusCode == System.Net.HttpStatusCode.Conflict, "another phone cannot submit results for current device");
    hub.ExportCsv(Path.Combine(directory, "results.csv")); Check(File.ReadAllText(Path.Combine(directory, "results.csv")).Contains("集成测试"), "CSV contains saved result");
    ws.Abort();
}
await using (var restored = new TimingHub(directory, port, true))
    Check(restored.Engine.Runs.Single().ElapsedUs == 1000000 && restored.Engine.Runs.Single().Revision == 2, "SQLite reload preserves confirmed result and correction");
await HeatTests.Run(Check, Reject);
VisionTests.Run(Check, Reject);
await VisionHttpTests.Run(Check);
CloseReviewTests.Run(Check,Reject);
Console.WriteLine($"\n{passed} checks passed. Test data: {directory}");
