using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ObstacleTimer.Core;

namespace ObstacleTimer.Desktop;

public sealed partial class TimingHub
{
    private static string RigId()
    {
        string dll=Path.Combine(AppContext.BaseDirectory,"bridge","AlgeTimyUsb.x64.dll");
        string usb="";
        using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_0C4A&PID_088B"))
            if(key!=null)usb=string.Join("|",key.GetSubKeyNames().Order());
        string text=Environment.MachineName+"|"+Environment.OSVersion+"|"+usb+"|"+(File.Exists(dll)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))):"no-sdk")+"|vision-v3";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
    private string SyncProof(string device,string id,long t1,long t2,long t3)=>Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(PairSecret),
        Encoding.UTF8.GetBytes($"{Engine.SessionId}|{device}|{id}|{t1}|{t2}|{t3}"))).ToLowerInvariant();
    private bool VerifyNetwork(VisionSubmission s)=>s.Clock.SessionId==Engine.SessionId && s.Clock.Samples.Length>=5 && s.Clock.Samples.All(t=>
        t.T4>=t.T1 && t.T3>=t.T2 && t.Proof==SyncProof(s.DeviceId,t.Id,t.T1,t.T2,t.T3));
    public void AssignVision(string heat,string vision,string run,int version,string note)=>Mutate("visionAssigned",()=>{Engine.AssignVision(heat,vision,run,version,note);return true;});
    public void UnassignVision(string heat,string vision,int version,string note,bool ignore=false)=>Mutate("visionUnassigned",()=>{Engine.UnassignVision(heat,vision,version,note,ignore);return true;});
    public void SelectOfficial(string heat,string run,int version,string source,string note)=>Mutate("officialSource",()=>{Engine.SelectOfficialSource(heat,run,version,source,note);return true;});
    public void ImportValidation(string file)
    {
        var profile=JsonSerializer.Deserialize<CameraValidationProfile>(File.ReadAllText(file),Wire.Json)??throw new DomainException("配置文件无效");
        Mutate("validationImported",()=>{Engine.AddValidation(profile);return true;});
    }
    private void UpdateVisionEvidence(string owner)
    {
        var h=Engine.GetHeat(owner);
        foreach(var v in h.VisionEvents)
            Engine.SetVisionEvidence(v.Id,new[]{"image","before","after","mask","metadata"}.All(kind=>store.HasEvidence(v.Id,kind,h.Id)));
    }
    private void MapVisionEndpoints(WebApplication app)
    {
        app.MapPost("/api/vision-events",async(HttpContext c)=>
        {
            var s=await JsonSerializer.DeserializeAsync<VisionSubmission>(c.Request.Body,Wire.Json)??throw new DomainException("缺少视觉事件");
            if(s.DeviceId!=Device(c)||!SafeId(s.EventId)||!SafeId(s.RequestId)||s.Clock?.Samples==null||s.Clock.Samples.Length>120)throw new DomainException("视觉事件身份或采样无效");
            var result=Mutate("visionCandidate",()=>Engine.SubmitVision(s,VerifyNetwork(s)));
            await c.Response.WriteAsJsonAsync(new{accepted=true,result},Wire.Json);
        });
        app.MapPost("/api/vision-events/{id}/correspondence",async(string id,HttpContext c)=>
        {
            var p=await JsonSerializer.DeserializeAsync<FrameCorrespondence>(c.Request.Body,Wire.Json)??throw new DomainException("缺少帧对应证据");
            Mutate("frameCorrespondence",()=>
            {
                var v=Engine.GetVision(id);var h=Engine.GetHeat(v.Submission.HeatId);
                if(h.DeviceId!=Device(c)||p.EventId!=id)throw new DomainException("帧对应归属不符");
                string hash=store.EvidenceHash(h.RecordingId,"video",h.Id);
                if(hash.Length==0)throw new DomainException("完整原片尚未校验接收");
                Engine.SetFrameCorrespondence(p,hash);return true;
            });
            await c.Response.WriteAsJsonAsync(new{accepted=true},Wire.Json);
        });
    }
}
