using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using ObstacleTimer.Core;

namespace ObstacleTimer.Desktop;

public sealed partial class TimingHub
{
    private object? HeatView(Heat? h) => h == null ? null : new { heat = h, runs = Engine.Members(h).ToArray() };
    public Heat PrepareHeat(string[] athletes, string name, TimingProfile profile, string authority = "camera-primary") => Mutate("heatPrepared", () => Engine.PrepareHeat(athletes, name, profile, false, authority));
    public void ArmHeat(string id) => Mutate("heatArmed", () => { Engine.ArmHeat(id); return true; });
    public void AssignFinish(string heat, string finish, string run, int version, string note) => Mutate("finishAssigned", () => { Engine.AssignFinish(heat, finish, run, version, note); return true; });
    public void UnassignFinish(string heat, string finish, int version, string note) => Mutate("finishUnassigned", () => { Engine.UnassignFinish(heat, finish, version, note); return true; });
    public void SwapFinishes(string heat, string first, string second, int version, string note) => Mutate("finishesSwapped", () => { Engine.SwapFinishes(heat, first, second, version, note); return true; });
    public void IgnoreFinish(string heat, string finish, int version, bool ignore, string note) => Mutate("finishIgnored", () => { Engine.IgnoreFinish(heat, finish, version, ignore, note); return true; });
    public void ReviewEvent(string heat, string finish, int version, bool midnight, string note) => Mutate("eventReviewed", () => { Engine.ReviewEvent(heat, finish, version, midnight, note); return true; });
    public void CloseHeat(string heat, int version) => Mutate("heatClosed", () => { Engine.CloseHeat(heat, version); return true; });
    public void CloseHeatForReview(string heat,int version,string note)=>Mutate("heatClosedWithPending",()=>{Engine.CloseHeatForReview(heat,version,note);return true;});
    public void ReopenHeat(string heat, int version, string note) => Mutate("heatReopened", () => { Engine.ReopenHeat(heat, version, note); return true; });
    public void DeclareRecordingLost(string heat, int version, string note) => Mutate("recordingLost", () => { Engine.DeclareRecordingLost(heat, version, note); return true; });
    public void RestoreRunner(string heat, string run, int version, string note) => Mutate("runnerRestored", () => { Engine.RestoreRunner(heat, run, version, note); return true; });
    private void MapHeatEndpoints(WebApplication app)
    {
        app.MapPost("/api/recording-ready", async (HttpContext c) =>
        {
            var ready = await JsonSerializer.DeserializeAsync<RecordingReady>(c.Request.Body, Wire.Json) ?? throw new DomainException("缺少录像信息");
            if (ready.DeviceId != Device(c) || !SafeId(ready.RecordingId)) throw new DomainException("手机或录像标识无效");
            Mutate("recordingRegistered", () => { Engine.RegisterRecording(ready); return true; });
            await c.Response.WriteAsJsonAsync(new { accepted = true }, Wire.Json);
        });
        app.MapPost("/api/recording-status", async (HttpContext c) =>
        {
            var status = await JsonSerializer.DeserializeAsync<RecordingStatus>(c.Request.Body, Wire.Json) ?? throw new DomainException("缺少录像状态");
            Mutate("recordingStatus", () => { Engine.RecordingUpdate(status, Device(c)); return true; });
            await c.Response.WriteAsJsonAsync(new { accepted = true }, Wire.Json);
        });
        app.MapGet("/api/heats/{id}", (string id, HttpContext c) =>
        {
            lock (gate)
            {
                var h = Engine.GetHeat(id);
                if (h.DeviceId != Device(c)) throw new DomainException("组次不属于该手机");
                return Results.Json(HeatView(h), Wire.Json);
            }
        });
        app.MapPost("/api/reviews", async (HttpContext c) =>
        {
            var s = await JsonSerializer.DeserializeAsync<ReviewSubmission>(c.Request.Body, Wire.Json) ?? throw new DomainException("缺少判读");
            if (s.DeviceId != Device(c) || !SafeId(s.RequestId) || !SafeId(s.EvidenceId)) throw new DomainException("手机或证据标识无效");
            var review = Mutate("videoReviewed", () => Engine.SubmitReview(s));
            await c.Response.WriteAsJsonAsync(new { accepted = true, review }, Wire.Json);
        });
        app.MapMethods("/api/heat-evidence/{id}/{kind}", ["GET", "POST"], UploadEvidence);
    }
}
