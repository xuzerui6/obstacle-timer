using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ObstacleTimer.Core;
using ObstacleTimer.Desktop;

internal static class VisionHttpTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var listener=new TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string folder=Path.Combine(Path.GetTempPath(),"Obstacle-vision-http-"+Wire.Id());
        await using(var hub=new TimingHub(folder,port,true))
        {
            await hub.StartAsync();using var http=new HttpClient(new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,_,_,_)=>true}){BaseAddress=new Uri($"https://127.0.0.1:{port+1}")};
            async Task<HttpResponseMessage> Post(string url,object data)=>await http.PostAsync(url,new StringContent(JsonSerializer.Serialize(data,Wire.Json),Encoding.UTF8,"application/json"));
            var paired=await Post("/api/pair",new{secret=hub.PairSecret,deviceId="phone"});paired.EnsureSuccessStatusCode();
            string token=JsonDocument.Parse(await paired.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var ws=new ClientWebSocket();ws.Options.RemoteCertificateValidationCallback=(_,_,_,_)=>true;
            await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port+1}/api/ws?access_token={token}"),default);
            async Task<JsonElement> Receive(string type)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while(true){var bytes=new byte[1024*512];var result=await ws.ReceiveAsync(bytes,timeout.Token);var doc=JsonDocument.Parse(bytes.AsMemory(0,result.Count));if(doc.RootElement.GetProperty("type").GetString()==type)return doc.RootElement.GetProperty("data").Clone();}
            }
            await Receive("state");var athlete=hub.AddAthlete("1","视频协议测试","");
            var h=hub.PrepareHeat([athlete.Id],"v3",new(10000,RoundingMode.Cut,true));hub.ArmHeat(h.Id);hub.Inject("0001 C0 12:00:00.00 00");
            await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="visionStatus",data=new VisionDiagnostic(h.Id,"","检测错误：回归样例",120,8,1,30000,30,0,0,"回归诊断")},Wire.Json)),WebSocketMessageType.Text,true,default);
            JsonElement diagnosticState;do{diagnosticState=await Receive("state");}while(diagnosticState.GetProperty("visionDiagnostics").GetArrayLength()==0);
            check(diagnosticState.GetProperty("visionDiagnostics")[0].GetProperty("lastError").GetString()=="回归诊断","computer receives authenticated phone analysis counters and errors while candidate queue is empty");
            var settings=new VisionSettings(FixedSide:true);
            var ready=await Post("/api/recording-ready",new RecordingReady(h.Id,"original","phone","video/webm",120,1280,720,1,"test","capture",settings));ready.EnsureSuccessStatusCode();
            var samples=new List<SignedSyncSample>();
            for(int i=0;i<5;i++){
                long t1=10_000_000+i*10_000;string id="sync"+i;
                await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="sync",data=new{id,t1}},Wire.Json)),WebSocketMessageType.Text,true,default);
                var reply=await Receive("sync");samples.Add(new(id,t1,reply.GetProperty("t2").GetInt64(),reply.GetProperty("t3").GetInt64(),t1+5000,reply.GetProperty("proof").GetString()!));
            }
            var s=new VisionSubmission("request","vision",h.Id,"original","phone","track",RaceEngine.VisionModelVersion,"capture",settings,
                new(hub.Engine.SessionId,"page","map","rvfc-presentationTime",100000,12_000_000,99_000_000,samples.ToArray()),90000,110000,10000,.95,false,[],[new('0',64),new('1',64),new('2',64)]);
            var response=await Post("/api/vision-events",s);response.EnsureSuccessStatusCode();
            check(hub.Engine.GetVision("vision").NetworkProofVerified,"v3 verifies real WSS HMAC sample proofs on the computer");
            hub.AssignVision(h.Id,"vision",hub.Engine.Members(h).Single().Id,h.Version,"测试号码");
            var retry=await Post("/api/vision-events",s);retry.EnsureSuccessStatusCode();check(h.VisionEvents.Count==1,"HTTP visual retry cannot duplicate a crossing");
            var corrupted=await Post("/api/vision-events",s with{RequestId="bad",EventId="bad",TrackId="bad",Clock=s.Clock with{Samples=samples.Select(x=>x with{Proof="bad"}).ToArray()}});corrupted.EnsureSuccessStatusCode();
            check(!hub.Engine.GetVision("bad").NetworkProofVerified&&!hub.Engine.GetVision("bad").Eligible,"invalid signed samples are retained as pending instead of producing an official score");
            var proof=await Post("/api/vision-events/vision/correspondence",new FrameCorrespondence("proof","vision",1,2,3,0,8,s.FrameHashes,new('a',64)));
            check(!proof.IsSuccessStatusCode,"original frame proof is rejected before SHA256-confirmed video receipt");
            foreach(string kind in new[]{"image","before","after","mask","metadata"}){
                byte[] bytes=Encoding.UTF8.GetBytes("TEST ATTACHMENT "+kind);string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var uploaded=await http.PostAsync($"/api/heat-evidence/vision/{kind}?heatId={h.Id}&offset=0&length={bytes.Length}&sha256={hash}",new ByteArrayContent(bytes));uploaded.EnsureSuccessStatusCode();
            }
            h=hub.Engine.GetHeat(h.Id);var v=hub.Engine.GetVision("vision");var run=hub.Engine.Members(h).Single();
            check(v.EvidenceReceived&&!v.Eligible&&run.ElapsedUs==null,"all five vision evidence receipts never bypass profile/original-frame gates");
            hub.Inject("0002 C1M 12:00:02.00 00");hub.AssignFinish(h.Id,h.Finishes.Single().Id,run.Id,h.Version,"备份号码");
            check(run.BackupElapsedUs==2000000&&run.ElapsedUs==null,"HTTP vision group keeps optical backup independent");
            hub.SelectOfficial(h.Id,run.Id,h.Version,"photocell-backup","测试明确采用备份");
            string csv=Path.Combine(folder,"vision.csv");hub.ExportCsv(csv);check(File.ReadAllText(csv).Contains("视频候选微秒")&&File.ReadAllText(csv).Contains("photocell-backup"),"CSV retains both sources, difference and selected source");
            ws.Abort();
        }
        using(var db=new SqliteConnection("Data Source="+Path.Combine(folder,"timing.sqlite"))){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=2";cmd.ExecuteNonQuery();}
        await using(var restored=new TimingHub(folder,port,true)){
            check(Directory.GetFiles(folder,"timing.pre-v3-*.sqlite").Length==1,"SQLite v2 is backed up before the v3 migration");
            check(restored.Engine.Runs.Single().OfficialSource=="photocell-backup"&&restored.Engine.Runs.Single().ElapsedUs==2000000,"restart preserves explicitly selected backup and visual history");
        }
    }
}
