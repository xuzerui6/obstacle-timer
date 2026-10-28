using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ObstacleTimer.Core;
using ObstacleTimer.Desktop;

internal static class HeatTests
{
    public static async Task Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var cut = new TimingProfile(10_000, RoundingMode.Cut, true);
        check(cut.Format(1_239_900) == "1.23" && (cut with { Rounding = RoundingMode.Up }).Format(1_230_100) == "1.24", "CUT and UP use integer device time");
        check((cut with { Rounding = RoundingMode.Round }).Format(1_235_000) == "1.24" && (cut with { Rounding = RoundingMode.Round }).Format(1_245_000) == "1.25", "ROUND is half-up, never banker's rounding");
        reject(() => new TimingProfile().Validate(), "device precision profile must be acknowledged");
        var ties = new long[]{3_571_000,3_579_000,3_581_000}.Select(us => new RaceRun {TimingMethod="timy-device",State=RunState.Confirmed,Profile=cut,
            Start=TimyParser.Parse("0001 C0 12:00:00.0000 00","d","c",1),Revisions=[new ResultRevision(1,"r","confirmed",us,null,null,null,"",DateTimeOffset.UtcNow)]}).ToArray();
        check(ties.Select(r=>RaceEngine.RankOf(r,ties)).SequenceEqual(new int?[]{1,1,3}),"rounded equal device scores share competition ranks 1,1,3");
        long now = 1_000_000_000;
        RaceEngine New(int count)
        {
            var e = new RaceEngine(Wire.Id(), () => now) { OnlinePhone = "phone", TimyOnline = true };
            for (int i = 0; i < count; i++) e.Athletes.Add(new("a" + i, (i+1).ToString(), "选手" + i, ""));
            return e;
        }
        Heat Prepare(RaceEngine e) => e.PrepareHeat(e.Athletes.Select(a => a.Id), "集体组", cut, true, "timy-device");
        void Ready(RaceEngine e, Heat h) => e.SetRecordingReady(new(h.Id, "recording-"+h.Id, "phone", "video/webm", 30, 1280, 720, 1));
        TimingImpulse Event(string raw, long host = 1_000_000_000, string connection = "usb-session") => TimyParser.Parse(raw, "timy1", connection, host)!;
        var e = New(100); var h = Prepare(e);
        reject(() => e.PrepareHeat(["a0"], "重复组", cut), "cannot run two heats concurrently");
        reject(() => e.ArmHeat(h.Id), "group cannot arm before saved recording chunk");
        var early = Event("0000 C0M 11:59:59.00 00"); e.OnImpulse(early); Ready(e,h); e.ArmHeat(h.Id); e.OnImpulse(early);
        check(h.Start == null, "pre-arm start replay cannot start the group");
        var start = Event("0013 C0M 12:00:00.00 00", now + 900_000); e.OnImpulse(start);
        check(e.Members(h).All(r => r.Start?.EventId == start.EventId && r.State == RunState.Running), "100 athletes share exactly one hardware start");
        e.OnImpulse(Event("0013 C0M 12:00:00.00 00", now+8_000_000));
        e.OnImpulse(Event("0014 C0M 12:00:00.30 00"));
        check(h.Start!.EventId == start.EventId && h.Warning.Length > 0, "duplicate and extra starts never reset group start");
        e.PhoneDisconnected("phone");
        for (int i=0;i<100;i++) e.OnImpulse(Event($"{i+1:0000} C1 {new TimeSpan(0,12,0,i+1).ToString(@"hh\:mm\:ss")}.1234 00", now + 20_000_000 + i*37_000));
        check(h.Finishes.Count==100 && h.Finishes.All(f=>f.RunId==null), "100 finish events retained while phone offline; no automatic bib assignment");
        var members=e.Members(h).ToArray();
        for (int i=0;i<100;i++) e.AssignFinish(h.Id,h.Finishes[i].Id,members[(i*37)%100].Id,h.Version,"裁判匹配");
        check(members.All(r=>r.ElapsedUs.HasValue&&r.Revision==1)&&h.Finishes.Select(f=>f.RunId).Distinct().Count()==100, "100 out-of-order athlete assignments remain one-to-one");
        check(members[(99*37)%100].ElapsedUs==100_123_400&&members[0].ElapsedUs==1_123_400, "scores use hardware difference regardless of USB delivery times");
        reject(()=>e.AssignFinish(h.Id,h.Finishes[0].Id,members[1].Id,h.Version,""), "assigned event cannot be reused");
        reject(()=>e.SwapFinishes(h.Id,members[0].Id,members[1].Id,h.Version-1,"过期"), "stale group version cannot alter bindings");
        long? a=members[0].ElapsedUs,b=members[1].ElapsedUs; e.SwapFinishes(h.Id,members[0].Id,members[1].Id,h.Version,"号码反了");
        check(members[0].ElapsedUs==b&&members[1].ElapsedUs==a&&members[0].Revisions[0].ElapsedUs==a, "swap is atomic and original scores remain in history");
        string oldEvent=members[0].FinishEventId!; e.UnassignFinish(h.Id,oldEvent,h.Version,"撤销错误匹配");
        check(members[0].ElapsedUs==null&&h.Finishes.Single(f=>f.Id==oldEvent).RunId==null, "unassignment clears current score and frees event without deleting history");
        e.AssignFinish(h.Id,oldEvent,members[0].Id,h.Version,"重新核对");
        e.OnlinePhone="phone";
        var s=new ReviewSubmission("review-1",h.Id,members[0].Id,h.RecordingId,"phone",0,999_000_000,.5,"image-1","任意视频帧只留证");
        int resultVersion=members[0].Revision; long? scoreBefore=members[0].ElapsedUs;
        var v=e.SubmitReview(s); e.SubmitReview(s);
        check(members[0].ElapsedUs==scoreBefore&&members[0].Revision==resultVersion&&members[0].Reviews.Count==1,"video PTS cannot change hardware score or result revision; retry is idempotent");
        reject(()=>e.SubmitReview(s with { FramePtsUs=1 }),"changed review retry is rejected");
        reject(()=>e.SubmitReview(s with { RequestId="new-request",EvidenceId="new-image" }),"stale review revision is rejected independently of timing version");
        reject(()=>e.Submit(new FinishSubmission("legacy",members[0].Id,"phone",resultVersion,1,1,1,"legacy-evidence",.5,"",true)),"legacy estimation API cannot overwrite hardware results");
        reject(()=>e.MarkReview(members[0].Id,"phone"),"legacy phone review cannot change hardware runner state");
        reject(()=>e.CloseHeat(h.Id,h.Version),"group cannot close before recording is ended or explicitly marked lost");
        e.RecordingUpdate(new(h.Id,h.RecordingId,false,true,"整组完成",100),"phone"); e.CloseHeat(h.Id,h.Version);
        check(h.State==HeatState.Closed&&e.Ranking().Count()==100,"completed group closes and hardware results remain ranked");
        reject(()=>e.UnassignFinish(h.Id,oldEvent,h.Version,"历史撤销"),"closed heat requires reopening before unassignment");
        var next=e.PrepareHeat(["a0","a1"],"下一组",cut,true,"timy-device"); check(next.Id!=h.Id,"next group can prepare before previous evidence upload");
        Ready(e,next);now+=11_000_000;
        e.RecordingUpdate(new(next.Id,next.RecordingId,true,false,"",1),"phone");
        reject(()=>e.ArmHeat(next.Id),"stalled saved-chunk count cannot keep readiness alive");
        e.RecordingUpdate(new(next.Id,next.RecordingId,true,false,"",2),"phone");e.ArmHeat(next.Id);e.PhoneDisconnected("phone");
        check(next.State==HeatState.Ready&&!next.RecordingLive,"phone disconnect disarms group before start");
        e.OnImpulse(Event("0009 C0M 13:00:00.00 00"));check(next.Start==null,"start while phone offline is logged without starting group");
        e.OnlinePhone="phone";e.RecordingUpdate(new(next.Id,next.RecordingId,true,false,"",3),"phone");e.ArmHeat(next.Id);
        e.OnImpulse(Event("0010 C0M 13:00:01.00 00"));e.OnImpulse(Event("0010 C1M 13:00:05.00 00"));e.OnImpulse(Event("0011 C1M 13:00:03.00 00"));e.OnImpulse(Event("0012 C1M 13:00:04.00 00"));
        check(next.State==HeatState.Pending&&next.Finishes[1].Issue.Length>0&&next.Finishes[2].Issue.Length>0,"clock rollback marks current and subsequent finish segments pending");
        e.IgnoreFinish(next.Id,next.Finishes[1].Id,next.Version,true,"误按，保留原始记录");
        check(e.BindingHistory.Last().Ignored![next.Finishes[1].Id]&&e.DeviceImpulses.Any(i=>i.EventId==next.Finishes[1].Id),"ignored event and explicit ignored-state audit remain durable");

        var midnight=New(1);var mh=Prepare(midnight);Ready(midnight,mh);midnight.ArmHeat(mh.Id);
        midnight.OnImpulse(Event("0001 C0 23:59:59.9000 00"));midnight.OnImpulse(Event("0001 C1 00:00:01.2000 00"));var mf=mh.Finishes[0];var mr=midnight.Members(mh).Single();
        reject(()=>midnight.AssignFinish(mh.Id,mf.Id,mr.Id,mh.Version,""),"cross-midnight is pending until explicitly confirmed");
        midnight.ReviewEvent(mh.Id,mf.Id,mh.Version,true,"确认跨午夜，设备未调时");midnight.AssignFinish(mh.Id,mf.Id,mr.Id,mh.Version,"");
        check(mr.ElapsedUs==1_300_000,"confirmed midnight wrap adds exactly one device day");

        var reconnect=New(2);var rh=Prepare(reconnect);Ready(reconnect,rh);reconnect.ArmHeat(rh.Id);reconnect.OnImpulse(Event("0001 C0M 15:50:00.86 00"));
        reconnect.DeviceUnavailable();reconnect.OnImpulse(Event("?0010 C1M 15:50:02.72 00",now+80_000_000,"new-usb"));var rf=rh.Finishes[0];var rr=reconnect.Members(rh).First();
        reject(()=>reconnect.AssignFinish(rh.Id,rf.Id,rr.Id,rh.Version,""),"reconnection cannot silently join device clock segments");
        reconnect.ReviewEvent(rh.Id,rf.Id,rh.Version,false,"核验同一设备，未重置时钟");reconnect.AssignFinish(rh.Id,rf.Id,rr.Id,rh.Version,"");
        check(rr.ElapsedUs==1_860_000&&rr.Quality.Contains("手动"),"actual captured C0M/C1M pair gives 1.86 seconds despite old-number RT");
        reconnect.RecoverAfterRestart();check(rr.ElapsedUs==1_860_000&&rh.State==HeatState.Pending,"restart retains confirmed hardware time while unfinished group becomes pending");
        reconnect.SetStatus(reconnect.Members(rh).Last().Id,RunState.Dnf,"未完成");
        check(reconnect.Members(rh).Last().State==RunState.Dnf,"per-athlete DNF does not alter another athlete score");
        reconnect.DeclareRecordingLost(rh.Id,rh.Version,"已核查本机录像缺失");reconnect.CloseHeat(rh.Id,rh.Version);reconnect.ReopenHeat(rh.Id,rh.Version,"更正旧组");
        reconnect.OnImpulse(Event("0019 C1M 16:00:00.00 00"));
        check(rh.Finishes.Count==1,"reopened historical heat cannot consume new live finish impulses");
        var stopped=New(1);var sh=Prepare(stopped);Ready(stopped,sh);stopped.RecordingUpdate(new(sh.Id,sh.RecordingId,false,false,"提前停止",1),"phone");
        reject(()=>Ready(stopped,sh),"stopped recording cannot be re-armed through replayed readiness");

        foreach(long latency in new long[]{0,1_000,500_000,30_000_000})
        {
            var jitter=New(1);var jh=Prepare(jitter);Ready(jitter,jh);jitter.ArmHeat(jh.Id);
            jitter.OnImpulse(Event("0001 C0M 12:00:00.00 00",now+latency));jitter.OnImpulse(Event("0001 C1M 12:00:03.57 00",now+10_000_000-latency/4));
            var jr=jitter.Members(jh).Single();jitter.AssignFinish(jh.Id,jh.Finishes[0].Id,jr.Id,jh.Version,"");check(jr.ElapsedUs==3_570_000,"3.57s hardware result invariant under USB latency "+latency);
        }
        var manual=New(2);var manualHeat=manual.PrepareHeat(manual.Athletes.Select(a=>a.Id),"终点前手动开启",cut,false,"timy-device");
        check(!manualHeat.RequireRecordingBeforeStart&&manualHeat.RecordingId.Length==0,"new heats default to camera off and no pre-record requirement");
        manual.ArmHeat(manualHeat.Id);manual.PhoneDisconnected("phone");
        check(manualHeat.State==HeatState.Armed,"on-demand group remains armed when phone disconnects");
        manual.OnImpulse(Event("0001 C0M 12:00:00.00 00"));
        check(manualHeat.State==HeatState.Running&&manual.Members(manualHeat).All(r=>r.State==RunState.Running),"C0 starts all athletes while camera is off and phone is offline");
        manual.OnImpulse(Event("0001 C1M 12:00:01.70 00"));var manualRuns=manual.Members(manualHeat).ToArray();manual.AssignFinish(manualHeat.Id,manualHeat.Finishes[0].Id,manualRuns[0].Id,manualHeat.Version,"");
        check(manualRuns[0].ElapsedUs==1_700_000,"finish before camera starts still produces exact device score");
        manual.OnlinePhone="phone";var lateReady=new RecordingReady(manualHeat.Id,"late-original","phone","video/webm",30,640,480,1,"test-phone");
        var commonStart=manualHeat.Start;manual.RegisterRecording(lateReady);
        check(manualHeat.State==HeatState.Running&&manualHeat.Start==commonStart&&manualRuns[0].ElapsedUs==1_700_000,"manual camera registration after start never resets start, heat or score");
        check(manualHeat.EvidenceIncomplete&&manualHeat.RecordingNote.Contains("晚于"),"late recording acknowledgment warns about possibly missing finish footage");
        manual.OnImpulse(Event("0002 C1M 12:00:02.11 00"));manual.AssignFinish(manualHeat.Id,manualHeat.Finishes[1].Id,manualRuns[1].Id,manualHeat.Version,"");
        manual.RecordingUpdate(new(manualHeat.Id,lateReady.RecordingId,false,true,"完成",2),"phone");int stoppedVersion=manualHeat.Version;
        manual.RegisterRecording(lateReady with {SavedChunks=2});
        check(!manualHeat.RecordingLive&&manualHeat.RecordingStopped&&manualHeat.Version==stoppedVersion,"HTTP metadata retry after stop acknowledges without reviving recording");
        reject(()=>manual.RegisterRecording(lateReady with {Width=100}),"registered original metadata cannot be changed by retry");
        check(manualHeat.RecordingNote.Contains("晚于")&&manualRuns[1].ElapsedUs==2_110_000,"stop and delayed metadata do not erase late-capture warning or timing");
        var failure=New(1);var failureHeat=failure.PrepareHeat(["a0"],"相机故障",cut,false,"timy-device");failure.ArmHeat(failureHeat.Id);failure.DeclareRecordingLost(failureHeat.Id,failureHeat.Version,"相机权限被拒绝");
        failure.OnImpulse(Event("0001 C0M 13:00:00.00 00"));failure.OnImpulse(Event("0001 C1M 13:00:03.57 00"));var failureRun=failure.Members(failureHeat).Single();failure.AssignFinish(failureHeat.Id,failureHeat.Finishes[0].Id,failureRun.Id,failureHeat.Version,"");
        check(failureRun.ElapsedUs==3_570_000&&failureHeat.EvidenceIncomplete,"camera permission/storage failure cannot disarm on-demand timing");
        var legacyJson=JsonSerializer.SerializeToNode(new Heat(),Wire.Json)!;legacyJson.AsObject().Remove("requireRecordingBeforeStart");
        check(legacyJson.Deserialize<Heat>(Wire.Json)!.RequireRecordingBeforeStart,"0.3.0 stored heats retain their original pre-recording policy");
        await Integration(check);
    }

    private static async Task Integration(Action<bool,string> check)
    {
        string data=Path.Combine(Path.GetTempPath(),"ObstacleHeat-tests-"+Wire.Id());Directory.CreateDirectory(data);
        using(var old=new SqliteConnection("Data Source="+Path.Combine(data,"timing.sqlite")))
        {old.Open();using var c=old.CreateCommand();c.CommandText="PRAGMA user_version=1; CREATE TABLE athletes(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE runs(id TEXT PRIMARY KEY,json TEXT NOT NULL);";c.ExecuteNonQuery();}
        using var listener=new TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string heatId,runId,recording="shared-video";long? official;
        await using(var hub=new TimingHub(data,port,true))
        {
            check(Directory.GetFiles(data,"timing.pre-v3-*.sqlite").Length==1,"SQLite v1 is backed up before v2 migration");
            await hub.StartAsync();using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}")};
            var pair=await http.PostAsync("/api/pair",Body(new{secret=hub.PairSecret,deviceId="phone"}));pair.EnsureSuccessStatusCode();var auth=JsonDocument.Parse(await pair.Content.ReadAsStringAsync()).RootElement;
            check(auth.GetProperty("protocol").GetInt32()==3,"pair advertises v3 protocol");string token=auth.GetProperty("token").GetString()!;http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Authorization","Bearer "+token);await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"),CancellationToken.None);
            var receive=new byte[65536];await ws.ReceiveAsync(receive,CancellationToken.None);
            var one=hub.AddAthlete("1","甲","");var two=hub.AddAthlete("2","乙","");var h=hub.PrepareHeat([one.Id,two.Id],"双人组",new(10_000,RoundingMode.Cut,true),"timy-device");heatId=h.Id;
            var ready=new RecordingReady(h.Id,recording,"phone","video/webm",30,640,480,1);
            await ws.SendAsync(Encoding.UTF8.GetBytes(Wire.Pack("recordingReady",ready)),WebSocketMessageType.Text,true,CancellationToken.None);
            for(int tries=0;h.State!=HeatState.Ready&&tries<100;tries++)await Task.Delay(20);
            hub.ArmHeat(h.Id);hub.Inject("0001 C0M 12:00:00.00 00");hub.Inject("0001 C1M 12:00:03.57 00");hub.Inject("0002 C1M 12:00:04.00 00");
            var members=hub.Engine.Members(h).ToArray();runId=members[0].Id;
            using(var fault=new SqliteConnection("Data Source="+Path.Combine(data,"timing.sqlite")))
            {
                fault.Open();using var cmd=fault.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_save BEFORE UPDATE ON heats BEGIN SELECT RAISE(ABORT,'forced-storage-failure'); END;";cmd.ExecuteNonQuery();
                bool rejected=false;try{hub.AssignFinish(h.Id,h.Finishes[0].Id,runId,h.Version,"模拟写盘失败");}catch(SqliteException){rejected=true;}
                check(rejected&&hub.Engine.Get(runId).ElapsedUs==null&&hub.Engine.GetHeat(h.Id).Finishes[0].RunId==null,"database failure rolls back binding and score together");
                cmd.CommandText="DROP TRIGGER fail_save";cmd.ExecuteNonQuery();
            }
            h=hub.Engine.GetHeat(h.Id);members=hub.Engine.Members(h).ToArray();
            hub.AssignFinish(h.Id,h.Finishes[0].Id,runId,h.Version,"");hub.AssignFinish(h.Id,h.Finishes[1].Id,members[1].Id,h.Version,"");official=members[0].ElapsedUs;
            var s=new ReviewSubmission("req-1",h.Id,runId,recording,"phone",0,100,.5,"img-1","截图");
            var posted=await http.PostAsync("/api/reviews",Body(s));posted.EnsureSuccessStatusCode();
            var oldWrite=await http.PostAsync("/api/results",Body(new FinishSubmission("old",runId,"phone",1,100,100,100,"old",.5,"",true)));
            check(oldWrite.StatusCode==System.Net.HttpStatusCode.Conflict&&members[0].ElapsedUs==official,"HTTP legacy result write rejected for hardware heat");
            h=hub.Engine.GetHeat(heatId); members=hub.Engine.Members(h).ToArray();
            byte[] bytes=Encoding.UTF8.GetBytes("shared-video-bytes");string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string Base(string id,string kind)=>$"/api/heat-evidence/{id}/{kind}?heatId={h.Id}";
            async Task Upload(string id,string kind)
            {
                var response=await http.PostAsync(Base(id,kind)+$"&offset=0&length={bytes.Length}&sha256={hash}",new ByteArrayContent(bytes));response.EnsureSuccessStatusCode();
            }
            var premature=await http.GetAsync(Base(recording,"video"));check(premature.StatusCode==System.Net.HttpStatusCode.Conflict,"shared original waits for at least one associated screenshot");
            await Upload("img-1","image");await Upload("img-1","metadata");await Upload(recording,"video");
            var s2=s with {RequestId="req-2",RunId=members[1].Id,EvidenceId="img-2"};(await http.PostAsync("/api/reviews",Body(s2))).EnsureSuccessStatusCode();await Upload("img-2","image");await Upload("img-2","metadata");
            var checkpoint=JsonDocument.Parse(await http.GetStringAsync(Base(recording,"video"))).RootElement;
            check(checkpoint.GetProperty("complete").GetBoolean()&&Directory.GetFiles(Path.Combine(data,"evidence",h.Id),"*.webm").Length==1,"two athletes reference one SHA256-verified shared original");
            check(members.All(r=>r.Revision==1&&r.Reviews.Count==1)&&members[0].ElapsedUs==3_570_000,"HTTP evidence submission never modifies device score or timing revision");
            (await http.PostAsync("/api/recording-status",Body(new RecordingStatus(h.Id,recording,false,true,"完成",3)))).EnsureSuccessStatusCode();hub.CloseHeat(h.Id,h.Version);
            hub.ExportCsv(Path.Combine(data,"results.csv"));check(File.ReadAllText(Path.Combine(data,"results.csv")).Contains("3.57"),"CSV uses group precision and device score");
            ws.Abort();
        }
        await using(var restored=new TimingHub(data,port,true))
        {check(restored.Engine.Get(runId).ElapsedUs==official&&restored.Engine.Get(runId).Reviews.Count==1&&restored.Engine.GetHeat(heatId).State==HeatState.Closed,"SQLite reload retains shared recording, device scores and independent reviews");
         check(Directory.GetFiles(data,"timing.pre-v3-*.sqlite").Length==1,"v2 reopen does not repeat v1 migration backup");}
    }
    private static StringContent Body(object value)=>new(JsonSerializer.Serialize(value,Wire.Json),Encoding.UTF8,"application/json");
}
