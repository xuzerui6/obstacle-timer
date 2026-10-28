using System.Globalization;

namespace ObstacleTimer.Core;

public enum HeatState { Preparing, Ready, Armed, Running, Pending, Closed }
public enum RoundingMode { Cut, Up, Round }
public sealed record TimingProfile(long UnitUs = 10_000, RoundingMode Rounding = RoundingMode.Cut, bool Confirmed = false)
{
    public void Validate()
    {
        if (UnitUs is not (1_000_000 or 100_000 or 10_000 or 1000 or 100) || !Enum.IsDefined(Rounding))
            throw new DomainException("精度或舍入配置无效");
        if (!Confirmed) throw new DomainException("请先核对并确认 Timy3 的精度和 CUT／UP／ROUND 设置");
    }
    public long Round(long us) => checked((us / UnitUs + (Rounding == RoundingMode.Up && us % UnitUs > 0 ||
        Rounding == RoundingMode.Round && us % UnitUs * 2 >= UnitUs ? 1 : 0)) * UnitUs);
    public string Format(long us) => (Round(us) / 1_000_000m).ToString("F" + (UnitUs == 1_000_000 ? 0 : UnitUs == 100_000 ? 1 : UnitUs == 10_000 ? 2 : UnitUs == 1000 ? 3 : 4), CultureInfo.InvariantCulture);
}
public sealed class Heat
{
    public string FinishAuthority { get; set; } = "timy-device";
    public bool ClosedWithPending { get; set; }
    public string ClosureNote { get; set; } = "";
    public VisionSettings Vision { get; set; } = new();
    public List<VisionEvent> VisionEvents { get; set; } = [];
    public string Id { get; set; } = Wire.Id();
    public string Name { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string RecordingId { get; set; } = "";
    public string VideoMime { get; set; } = "video/mp4";
    public RecordingReady? Capture { get; set; }
    public TimingProfile Profile { get; set; } = new();
    public List<string> RunIds { get; set; } = [];
    public HeatState State { get; set; }
    public TimingImpulse? Start { get; set; }
    public List<FinishEvent> Finishes { get; set; } = [];
    public int Version { get; set; }
    public bool RecordingLive { get; set; }
    // Missing in 0.3.0 JSON: preserve the original group's pre-recording contract.
    public bool RequireRecordingBeforeStart { get; set; } = true;
    public bool RecordingStopped { get; set; }
    public bool EvidenceIncomplete { get; set; }
    public string RecordingNote { get; set; } = "";
    public long RecordingHeartbeatUs { get; set; }
    public int SavedChunks { get; set; }
    public bool Interrupted { get; set; }
    public bool AcceptingFinishes { get; set; }
    public string Warning { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class FinishEvent
{
    public TimingImpulse Impulse { get; set; } = null!;
    public string? RunId { get; set; }
    public bool Ignored { get; set; }
    public bool MidnightConfirmed { get; set; }
    public string Issue { get; set; } = "";
    public string ReviewNote { get; set; } = "";
    public string Id => Impulse.EventId;
}
public record RecordingReady(string HeatId, string RecordingId, string DeviceId, string VideoMime, double Fps, int Width, int Height, int SavedChunks, string Model = "", string CaptureFingerprint = "", VisionSettings? Vision = null);
public record RecordingStatus(string HeatId, string RecordingId, bool Recording, bool Complete, string Note, int SavedChunks);
public record ReviewSubmission(string RequestId, string HeatId, string RunId, string RecordingId, string DeviceId,
    int ExpectedReviewRevision, long FramePtsUs, double FinishLine, string EvidenceId, string Note, string VisionEventId = "");
public record VideoReview(int Revision, ReviewSubmission Submission, DateTimeOffset CreatedAt);
public record BindingChange(string Id, string HeatId, string Action, string Note, DateTimeOffset CreatedAt,
    Dictionary<string, string?> Bindings, Dictionary<string, bool>? Ignored = null, Dictionary<string, string>? EventReviews = null,
    Dictionary<string,string?>? VisionBindings=null,Dictionary<string,string>? OfficialSources=null);

public sealed partial class RaceEngine
{
    public List<Heat> Heats { get; } = [];
    public List<TimingImpulse> DeviceImpulses { get; } = [];
    public List<BindingChange> BindingHistory { get; } = [];
    public Heat? ActiveHeat => Heats.LastOrDefault(h => h.State != HeatState.Closed);
    private static string ImpulseKey(TimingImpulse i) => $"{i.Source}|{i.ConnectionId}|{i.DeviceId}|{i.DeviceNumber}|{i.Channel}|{i.DeviceTimeUs}";
    public Heat GetHeat(string id) => Heats.SingleOrDefault(h => h.Id == id) ?? throw new DomainException("组次不存在");
    public IEnumerable<RaceRun> Members(Heat heat) => heat.RunIds.Select(Get);
    public Heat PrepareHeat(IEnumerable<string> ids, string name, TimingProfile profile, bool requireRecordingBeforeStart = false, string finishAuthority = "camera-primary")
    {
        profile.Validate();
        if (finishAuthority is not ("camera-primary" or "timy-device")) throw new DomainException("计时来源无效");
        if (ActiveHeat != null) throw new DomainException("请先结束当前组次");
        if (OnlinePhone == null) throw new DomainException("请先连接终点手机");
        var selected = ids.ToArray();
        if (selected.Length == 0 || selected.Distinct().Count() != selected.Length) throw new DomainException("请选择不重复的选手");
        var athletes = selected.Select(id => Athletes.SingleOrDefault(a => a.Id == id) ?? throw new DomainException("选手不存在")).ToArray();
        var heat = new Heat { Name = string.IsNullOrWhiteSpace(name) ? $"第 {Heats.Count + 1} 组" : name.Trim(), SessionId = SessionId,
            DeviceId = OnlinePhone, Profile = profile, State = HeatState.Preparing, RequireRecordingBeforeStart = requireRecordingBeforeStart, FinishAuthority = finishAuthority };
        foreach (var athlete in athletes)
        {
            var run = new RaceRun { Athlete = athlete, HeatId = heat.Id, SessionId = SessionId, DeviceId = OnlinePhone,
                TimingMethod = finishAuthority, Profile = profile };
            Runs.Add(run); heat.RunIds.Add(run.Id);
        }
        Heats.Add(heat); return heat;
    }
    public void SetRecordingReady(RecordingReady ready)
    {
        var heat = GetHeat(ready.HeatId);
        if (heat.RecordingStopped) throw new DomainException("本组原片已结束，不能重放就绪消息，请新建组次");
        if (heat.State == HeatState.Closed || heat.State == HeatState.Pending && !heat.AcceptingFinishes ||
            heat.RequireRecordingBeforeStart && heat.State is not (HeatState.Preparing or HeatState.Ready))
            throw new DomainException("当前组次不能开始新录像");
        if (heat.DeviceId != ready.DeviceId || OnlinePhone != ready.DeviceId) throw new DomainException("手机未绑定本组");
        if (string.IsNullOrWhiteSpace(ready.RecordingId) || ready.VideoMime is not ("video/mp4" or "video/webm") ||
            ready.SavedChunks < 1 || ready.Width < 1 || ready.Height < 1 || !double.IsFinite(ready.Fps) || ready.Fps < 1)
            throw new DomainException("尚未保存可用的录像分片");
        if (Heats.Any(h => h.Id != heat.Id && h.RecordingId == ready.RecordingId)) throw new DomainException("录像标识已被其他组使用");
        if (heat.RecordingId.Length > 0 && heat.RecordingId != ready.RecordingId) throw new DomainException("本组原片不能覆盖，请取消后新建组次");
        heat.RecordingId = ready.RecordingId; heat.VideoMime = ready.VideoMime; heat.RecordingLive = true;
        heat.Capture = ready;
        if (ready.Vision != null) { ready.Vision.Validate(); heat.Vision = ready.Vision; }
        heat.SavedChunks = ready.SavedChunks;
        heat.RecordingHeartbeatUs = clock(); heat.RecordingStopped = false;
        if (heat.State is HeatState.Preparing or HeatState.Ready) heat.State = HeatState.Ready;
        if (!heat.RequireRecordingBeforeStart && heat.Finishes.Count > 0)
        {
            heat.EvidenceIncomplete = true;
            heat.RecordingNote = "录像就绪确认晚于已记录终点，部分选手的过线画面可能缺失，请复核";
        }
        heat.Version++;
    }
    public void RegisterRecording(RecordingReady ready)
    {
        var heat = GetHeat(ready.HeatId);
        if (heat.DeviceId != ready.DeviceId) throw new DomainException("录像归属不符");
        if (heat.Capture is { } existing)
        {
            if (existing.RecordingId != ready.RecordingId || existing.VideoMime != ready.VideoMime || existing.Width != ready.Width ||
                existing.Height != ready.Height || existing.Fps != ready.Fps || existing.Model != ready.Model || existing.CaptureFingerprint != ready.CaptureFingerprint || existing.Vision != ready.Vision)
                throw new DomainException("已登记的原片信息不能改变");
            return; // A delivery retry acknowledges metadata only; it never revives or refreshes a stopped camera.
        }
        SetRecordingReady(ready);
    }
    public void RecordingUpdate(RecordingStatus status, string device)
    {
        var heat = GetHeat(status.HeatId);
        if (heat.DeviceId != device || heat.RecordingId != status.RecordingId) throw new DomainException("录像归属不符");
        if (status.SavedChunks < 1 || status.Note?.Length > 2000) throw new DomainException("录像状态无效");
        if (heat.RecordingStopped && status.Recording) throw new DomainException("已结束的原片不能重新录像");
        if (status.Recording && status.SavedChunks <= heat.SavedChunks) return;
        heat.SavedChunks = status.SavedChunks;
        heat.RecordingLive = status.Recording; heat.RecordingHeartbeatUs = clock();
        if (!status.Recording)
        {
            heat.RecordingStopped = true;
            heat.EvidenceIncomplete |= !status.Complete || heat.FinishAuthority == "timy-device" && heat.Start != null && heat.Finishes.Count(f => !f.Ignored) < Members(heat).Count(r => r.State is not (RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled));
            if (!string.IsNullOrWhiteSpace(status.Note) && !heat.RecordingNote.Contains(status.Note, StringComparison.Ordinal))
                heat.RecordingNote += (heat.RecordingNote.Length > 0 ? "；" : "") + status.Note;
            if (heat.RequireRecordingBeforeStart && (heat.State is HeatState.Armed or HeatState.Ready)) heat.State = HeatState.Preparing;
            heat.Version++;
        }
    }
    public void ArmHeat(string id)
    {
        var h = GetHeat(id);
        if (h.State is not (HeatState.Preparing or HeatState.Ready)) throw new DomainException("本组当前不能进入待发令");
        if (h.RequireRecordingBeforeStart && (h.State != HeatState.Ready || !h.RecordingLive || h.RecordingStopped || clock() - h.RecordingHeartbeatUs > 10_000_000))
            throw new DomainException("手机录像未就绪或状态已过期");
        if (!TimyOnline || h.RequireRecordingBeforeStart && OnlinePhone != h.DeviceId) throw new DomainException("Timy3 或终点手机未连接");
        if (!Members(h).Any(r => !r.IsTerminal)) throw new DomainException("本组没有待出发选手");
        h.State = HeatState.Armed; h.Version++;
    }
    private void OnHeatImpulse(TimingImpulse impulse)
    {
        var h = ActiveHeat!;
        if (impulse.Channel is "C0" or "C0M")
        {
            if (h.Start != null) { h.Warning = "组内出现额外起跑事件，已记录但未重置共同起跑"; h.Version++; return; }
            if (h.State != HeatState.Armed) return;
            if (h.RequireRecordingBeforeStart && (!h.RecordingLive || h.RecordingStopped || OnlinePhone != h.DeviceId || clock() - h.RecordingHeartbeatUs > 10_000_000))
            { h.State = HeatState.Ready; h.Warning = "起跑时手机录像状态已失效，本次只记录原始事件"; h.Version++; return; }
            h.Start = impulse; h.State = HeatState.Running; h.AcceptingFinishes = true;
            foreach (var r in Members(h).Where(r => !r.IsTerminal)) { r.Start = impulse; r.State = RunState.Running; }
            h.Version++; return;
        }
        if (impulse.Channel is not ("C1" or "C1M") || h.Start == null || !h.AcceptingFinishes) return;
        bool backwards = h.Finishes.LastOrDefault()?.Impulse.DeviceTimeUs > impulse.DeviceTimeUs;
        if (backwards)
        {
            h.Interrupted = true; h.State = HeatState.Pending; h.Warning = "设备时刻逆序，后续终点需逐条人工核验";
            foreach (var r in Members(h).Where(r => !r.IsTerminal)) r.State = RunState.Pending;
        }
        string issue = impulse.DeviceId != h.Start.DeviceId ? "终点来自不同设备，禁止匹配" :
            backwards ? "设备时刻逆序，需要人工核验" :
            h.Interrupted || impulse.ConnectionId != h.Start.ConnectionId ? "设备断开、重连或时钟异常，需要人工核验" : "";
        h.Finishes.Add(new FinishEvent { Impulse = impulse, Issue = issue }); h.Version++;
    }
    public void DeviceUnavailable()
    {
        TimyOnline = false;
        if (ActiveHeat is not { } h) return;
        if (h.State == HeatState.Armed) h.State = HeatState.Ready;
        if (h.Start != null)
        {
            h.Interrupted = true; h.State = HeatState.Pending; h.Warning = "设备断开，未绑定终点需人工核验";
            foreach (var r in Members(h).Where(r => !r.IsTerminal)) r.State = RunState.Pending;
            foreach (var f in h.Finishes.Where(f => f.RunId == null && !f.Ignored)) f.Issue = "设备曾断开，需要人工核验";
        }
        h.Version++;
    }
    private Heat EditableHeat(string id, int version)
    {
        var h = GetHeat(id);
        if (h.Version != version) throw new DomainException("组次已经更新，请重新选择后操作");
        return h;
    }
    public void ReviewEvent(string heatId, string eventId, int version, bool midnight, string note)
    {
        var h = EditableHeat(heatId, version); var f = FindFinish(h, eventId);
        RequireNote(note);
        if (f.RunId != null || f.Ignored) throw new DomainException("请先撤销匹配或恢复该事件");
        if (h.Start == null || f.Impulse.DeviceId != h.Start.DeviceId) throw new DomainException("不同设备的时刻不能相减");
        f.ReviewNote = note; f.MidnightConfirmed = midnight; h.Version++; Audit(h, "eventReviewed", note);
    }
    private static FinishEvent FindFinish(Heat h, string id) => h.Finishes.SingleOrDefault(f => f.Id == id) ?? throw new DomainException("终点事件不存在");
    private static void RequireNote(string note) { if (string.IsNullOrWhiteSpace(note)) throw new DomainException("请填写操作原因"); }
    private static long DeviceElapsed(Heat h, FinishEvent f)
    {
        if (h.Start == null || h.Start.DeviceId != f.Impulse.DeviceId || h.Start.Source != f.Impulse.Source)
            throw new DomainException("起终点设备或来源不一致");
        if (f.Issue.Length > 0 && f.ReviewNote.Length == 0) throw new DomainException("该事件需先人工核验");
        long elapsed = f.Impulse.DeviceTimeUs - h.Start.DeviceTimeUs;
        if (elapsed < 0)
        {
            if (!f.MidnightConfirmed || f.ReviewNote.Length == 0) throw new DomainException("终点早于起跑；确认跨午夜前只保留待处理");
            elapsed += 86_400_000_000L;
        }
        if (elapsed <= 0 || elapsed >= 86_400_000_000L) throw new DomainException("设备用时无效");
        return elapsed;
    }
    public void AssignFinish(string heatId, string eventId, string runId, int version, string note)
    {
        var h = EditableHeat(heatId, version); var f = FindFinish(h, eventId); var r = Get(runId);
        if (h.State == HeatState.Closed) throw new DomainException("请先重新打开组次再匹配");
        if (r.HeatId != h.Id || h.Start == null) throw new DomainException("选手不属于本组或尚未起跑");
        if (r.State is RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled) throw new DomainException("请先恢复选手再匹配");
        if (f.Ignored || f.RunId != null || r.FinishEventId != null) throw new DomainException("事件或选手已匹配，请先撤销或使用交换");
        long elapsed = DeviceElapsed(h, f);
        f.RunId = r.Id; r.FinishEventId = f.Id;
        if (h.FinishAuthority == "camera-primary") { r.BackupElapsedUs = elapsed; r.BackupManual=f.Impulse.Manual; }
        else AppendDeviceResult(r, f, elapsed, note);
        h.Version++; Audit(h, "assigned", note);
    }
    private static void AppendDeviceResult(RaceRun r, FinishEvent f, long elapsed, string note)
    {
        r.Revisions.Add(new ResultRevision(r.Revision + 1, Wire.Id(), "confirmed", elapsed, null, null, null, note,
            DateTimeOffset.UtcNow, "", r.Start?.Manual == true || f.Impulse.Manual ? "timy-manual" : "timy-device"));
        r.State = RunState.Confirmed;
    }
    private static void ClearBinding(RaceRun r, string note)
    {
        r.FinishEventId = null; r.State = RunState.Pending;
        r.Revisions.Add(new ResultRevision(r.Revision + 1, Wire.Id(), "pending", null, null, null, null, note, DateTimeOffset.UtcNow, "", r.TimingMethod=="camera-primary"?"source-pending":"timy-device"));
    }
    public void UnassignFinish(string heatId, string eventId, int version, string note)
    {
        var h = EditableHeat(heatId, version); var f = FindFinish(h, eventId); RequireNote(note);
        if (h.State == HeatState.Closed) throw new DomainException("请先重新打开组次再撤销");
        if (f.RunId == null) throw new DomainException("事件尚未匹配");
        var r = Get(f.RunId);
        if (h.FinishAuthority == "camera-primary")
        {
            r.FinishEventId = null; r.BackupElapsedUs = null;
            if (r.OfficialSource == "photocell-backup") { ClearBinding(r, note); r.OfficialSource = "pending"; r.SourceReviewRequired=true; }
        }
        else ClearBinding(r, note);
        f.RunId = null; h.Version++; Audit(h, "unassigned", note);
    }
    public void SwapFinishes(string heatId, string firstRunId, string secondRunId, int version, string note)
    {
        var h = EditableHeat(heatId, version); RequireNote(note);
        var a = Get(firstRunId); var b = Get(secondRunId);
        if (a.Id == b.Id || a.HeatId != h.Id || b.HeatId != h.Id || a.FinishEventId == null || b.FinishEventId == null)
            throw new DomainException("请选择本组两个已匹配的选手");
        var fa = FindFinish(h, a.FinishEventId); var fb = FindFinish(h, b.FinishEventId);
        long ea = DeviceElapsed(h, fb), eb = DeviceElapsed(h, fa);
        if (h.FinishAuthority == "camera-primary" && (a.OfficialSource == "photocell-backup" || b.OfficialSource == "photocell-backup"))
            throw new DomainException("已启用的备份先撤销启用，再交换备份归属");
        fa.RunId = b.Id; fb.RunId = a.Id; a.FinishEventId = fb.Id; b.FinishEventId = fa.Id;
        if (h.FinishAuthority == "camera-primary") { a.BackupElapsedUs = ea; b.BackupElapsedUs = eb; a.BackupManual=fb.Impulse.Manual;b.BackupManual=fa.Impulse.Manual; }
        else { AppendDeviceResult(a, fb, ea, note); AppendDeviceResult(b, fa, eb, note); }
        h.Version++; Audit(h, "swapped", note);
    }
    public void IgnoreFinish(string heatId, string eventId, int version, bool ignore, string note)
    {
        var h = EditableHeat(heatId, version); var f = FindFinish(h, eventId); RequireNote(note);
        if (f.RunId != null) throw new DomainException("先撤销匹配，才能处理误按事件");
        if (h.State == HeatState.Closed && !ignore) throw new DomainException("请先重新打开组次再恢复误按事件");
        f.Ignored = ignore; h.Version++; Audit(h, ignore ? "ignored" : "restored", note);
    }
    private void SetHeatRunStatus(RaceRun run, RunState state, string note)
    {
        RequireNote(note); var h = GetHeat(run.HeatId!);
        if (h.State == HeatState.Closed && run.State == RunState.Confirmed && state == RunState.Dsq) { }
        else if (h.State == HeatState.Closed) throw new DomainException("请先重新打开组次再改选手状态");
        if (run.FinishEventId != null)
        {
            var f = FindFinish(h, run.FinishEventId); f.RunId = null; f.Ignored = true; run.FinishEventId = null;
        }
        run.State = state;
        run.BackupElapsedUs = null;
        run.Revisions.Add(new ResultRevision(run.Revision + 1, Wire.Id(), state.ToString().ToLowerInvariant(), null, null, null, null, note, DateTimeOffset.UtcNow, "", "timy-device"));
        h.Version++; Audit(h, "status", note);
    }
    public void RestoreRunner(string heatId, string runId, int version, string note)
    {
        var h = EditableHeat(heatId, version); RequireNote(note); var r = Get(runId);
        if (r.HeatId != h.Id || h.State == HeatState.Closed || r.State == RunState.Confirmed || r.FinishEventId != null) throw new DomainException("请先打开组次并撤销匹配");
        ClearBinding(r, note); h.Version++; Audit(h, "runnerRestored", note);
    }
    public void CloseHeat(string id, int version)
    {
        var h = EditableHeat(id, version);
        if (Members(h).Any(r => !r.IsTerminal) || h.Finishes.Any(f => !f.Ignored && f.RunId == null) || h.VisionEvents.Any(v => v.RunId == null && !v.Ignored))
            throw new DomainException("请先处理所有选手与待匹配终点（未完成者可标为 DNF）");
        if (!h.RecordingStopped) throw new DomainException("本组未录像或录像尚未结束；请在手机结束录像，或填写原因并记录录像缺失");
        h.State = HeatState.Closed; h.AcceptingFinishes = false; h.ClosedWithPending=false; h.Version++; Audit(h, "closed", "组次结束");
    }
    public void CloseHeatForReview(string id,int version,string note)
    {
        var h=EditableHeat(id,version);RequireNote(note);
        if(h.State==HeatState.Closed)throw new DomainException("本组已经结束");
        var unresolved=Members(h).Where(r=>!r.IsTerminal).ToArray();
        foreach(var r in unresolved){
            r.Revisions.Add(new ResultRevision(r.Revision+1,Wire.Id(),"pending",null,null,null,r.VisionEventId,"结束组次待复核："+note,DateTimeOffset.UtcNow,"", "group-pending"));
            r.State=RunState.Pending;r.SourceReviewRequired=true;
        }
        if(!h.RecordingStopped){h.EvidenceIncomplete=true;h.RecordingNote="结束组次时录像未完成核验："+note;}
        h.RecordingLive=false;h.RecordingStopped=true;h.AcceptingFinishes=false;
        h.State=HeatState.Closed;h.ClosedWithPending=true;
        h.ClosureNote=$"{note}；保留待复核：选手{unresolved.Length}，光电事件{h.Finishes.Count(f=>f.RunId==null&&!f.Ignored)}，视频候选{h.VisionEvents.Count(v=>v.RunId==null&&!v.Ignored)}";
        h.Version++;Audit(h,"closedWithPending",h.ClosureNote);
    }
    public void DeclareRecordingLost(string id, int version, string note)
    {
        var h = EditableHeat(id, version); RequireNote(note);
        h.RecordingLive = false; h.RecordingStopped = true; h.EvidenceIncomplete = true; h.RecordingNote = note;
        if (h.RequireRecordingBeforeStart && (h.State is HeatState.Ready or HeatState.Armed)) h.State = HeatState.Preparing;
        h.Version++; Audit(h, "recordingLost", note);
    }
    public void ReopenHeat(string id, int version, string note)
    {
        var h = EditableHeat(id, version); RequireNote(note);
        if (h.State != HeatState.Closed || ActiveHeat != null) throw new DomainException("请先结束其他组次");
        h.State = HeatState.Pending; h.AcceptingFinishes = false; h.Version++; Audit(h, "reopened", note);
    }
    private void Audit(Heat h, string action, string note) => BindingHistory.Add(new BindingChange(Wire.Id(), h.Id, action, note,
        DateTimeOffset.UtcNow, h.Finishes.ToDictionary(f => f.Id, f => f.RunId), h.Finishes.ToDictionary(f => f.Id, f => f.Ignored),
        h.Finishes.ToDictionary(f => f.Id, f => $"次日确认={f.MidnightConfirmed}; {f.ReviewNote}"),
        h.VisionEvents.ToDictionary(v=>v.Id,v=>v.RunId),Members(h).ToDictionary(r=>r.Id,r=>r.OfficialSource)));
    public static int? RankOf(RaceRun run, IEnumerable<RaceRun> scope)
    {
        if (run.TimingMethod is not ("timy-device" or "camera-primary") || run.State != RunState.Confirmed || run.Start?.Source == "simulation" || run.ElapsedUs == null || run.Profile == null) return null;
        long score = run.Profile.Round(run.ElapsedUs.Value);
        return 1 + scope.Count(r => r.TimingMethod is "timy-device" or "camera-primary" && r.State == RunState.Confirmed && r.Start?.Source != "simulation" &&
            r.ElapsedUs.HasValue && r.Profile != null && r.Profile.Round(r.ElapsedUs.Value) < score);
    }
    public VideoReview SubmitReview(ReviewSubmission s)
    {
        var h = GetHeat(s.HeatId); var r = Get(s.RunId);
        if (h.DeviceId != s.DeviceId || r.HeatId != h.Id || s.RecordingId != h.RecordingId || h.RecordingId.Length == 0)
            throw new DomainException("录像或选手归属不符");
        var previous = Runs.SelectMany(run => run.Reviews).SingleOrDefault(v => v.Submission.RequestId == s.RequestId);
        if (previous != null)
        {
            if (previous.Submission != s) throw new DomainException("同一请求标识的内容不能改变");
            return previous;
        }
        if (s.ExpectedReviewRevision != r.Reviews.Count) throw new DomainException("判读版本已改变，请刷新后再提交");
        if (s.FramePtsUs < 0 || !double.IsFinite(s.FinishLine) || s.FinishLine is < 0 or > 1 || string.IsNullOrWhiteSpace(s.EvidenceId) ||
            Runs.SelectMany(run => run.Reviews).Any(v => v.Submission.EvidenceId == s.EvidenceId)) throw new DomainException("截图或帧信息无效");
        if(s.VisionEventId.Length>0)
        {
            var v=GetVision(s.VisionEventId);
            if(v.RunId!=r.Id)throw new DomainException("视觉事件与选手不符");
            v.ManualFramePtsUs=s.FramePtsUs;v.ManualReviewNote=s.Note;v.Revision++;EvaluateVision(v);
        }
        var review = new VideoReview(r.Reviews.Count + 1, s, DateTimeOffset.UtcNow); r.Reviews.Add(review); return review;
    }
    private void RecoverHeats()
    {
        foreach (var h in Heats.Where(h => h.State != HeatState.Closed))
        {
            h.State = HeatState.Pending; h.Interrupted = true; h.RecordingLive = false;
            h.Warning = "程序已重启，原始事件保留；继续匹配前请核验设备与录像";
            foreach (var f in h.Finishes.Where(f => f.RunId == null && !f.Ignored)) f.Issue = "程序已重启，需要人工核验";
            h.Version++;
        }
    }
}
