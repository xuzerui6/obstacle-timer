namespace ObstacleTimer.Core;

// All calls are serialized by the host. Persistence is committed before notifications/acknowledgements.
public sealed partial class RaceEngine(string sessionId, Func<long>? now = null)
{
    private readonly Func<long> clock = now ?? Wire.NowUs;
    private readonly HashSet<string> impulses = [];
    public string SessionId { get; } = sessionId;
    public bool TimyOnline { get; set; }
    public string? OnlinePhone { get; set; }
    public List<Athlete> Athletes { get; } = [];
    public List<RaceRun> Runs { get; } = [];
    public RaceRun? Active => Runs.LastOrDefault(r => !r.IsTerminal);

    public RaceRun Prepare(string athleteId)
    {
        if (Active != null) throw new DomainException("请先完成或取消当前场次");
        if (OnlinePhone == null) throw new DomainException("请先连接终点手机");
        var athlete = Athletes.SingleOrDefault(a => a.Id == athleteId) ?? throw new DomainException("请选择选手");
        var run = new RaceRun { Athlete = athlete, SessionId = SessionId, DeviceId = OnlinePhone };
        Runs.Add(run);
        return run;
    }

    public void SetReady(PhoneReady ready)
    {
        var run = Get(ready.RunId);
        if (run.HeatId != null) throw new DomainException("多人组次必须使用 v2 整组录像接口");
        if (run.State is not (RunState.Preparing or RunState.Ready)) throw new DomainException("当前场次不接受准备消息");
        RequirePhone(run, ready.DeviceId);
        if (!ready.Clock.IsUsable(SessionId, clock())) throw new DomainException("时钟同步过期或无效，请重新同步");
        if (ready.RecordingStartClientUs <= 0 || ready.Width < 1 || ready.Height < 1 || ready.Fps < 1 ||
            !double.IsFinite(ready.Fps) || string.IsNullOrWhiteSpace(ready.CameraTimebase)) throw new DomainException("录像信息无效");
        if (Math.Abs(ready.Clock.ToHost(ready.RecordingStartClientUs) - clock()) > 120_000_000)
            throw new DomainException("录像开始时刻不在有效范围内");
        run.Ready = ready;
        run.State = RunState.Ready;
    }

    public void Arm(string runId)
    {
        var run = Get(runId);
        if (run.State != RunState.Ready || run.Ready == null) throw new DomainException("手机尚未开始录像");
        if (!TimyOnline) throw new DomainException("Timy3 未连接");
        if (OnlinePhone != run.DeviceId) throw new DomainException("终点手机已断开");
        if (!run.Ready.Clock.IsUsable(SessionId, clock())) throw new DomainException("同步已过期，请取消本场并重新准备");
        run.State = RunState.Armed;
    }

    public RaceRun? OnImpulse(TimingImpulse impulse)
    {
        if (DeviceImpulses.Any(i => ImpulseKey(i) == ImpulseKey(impulse))) return null;
        DeviceImpulses.Add(impulse);
        if (ActiveHeat != null) { OnHeatImpulse(impulse); return null; }
        // Duplicate physical rows retain the original USB arrival; duplicate delivery cannot start another run.
        string key = $"{impulse.Source}|{impulse.ConnectionId}|{impulse.DeviceId}|{impulse.Raw.Trim()}";
        if (!impulses.Add(key)) return null;
        var run = Active;
        if (run?.State != RunState.Armed || impulse.Channel is not ("C0" or "C0M")) return null;
        if (run.Ready == null || impulse.HostUs < run.Ready.Clock.ToHost(run.Ready.RecordingStartClientUs))
            throw new DomainException("起跑早于录像，场次不能计时");
        run.Start = impulse;
        run.State = RunState.Running;
        return run;
    }

    public void MarkReview(string runId, string deviceId)
    {
        var run = Get(runId);
        if (run.HeatId != null) throw new DomainException("多人组次录像判读不会改变计时状态，请使用 v2 接口");
        RequirePhone(run, deviceId);
        if (run.State is RunState.Running or RunState.Armed or RunState.Ready)
            run.State = run.Start == null ? RunState.Pending : RunState.Review;
    }

    public ResultRevision Submit(FinishSubmission finish)
    {
        var run = Get(finish.RunId);
        if (run.HeatId != null) throw new DomainException("本组采用 Timy3 硬件计时，手机不能提交估算成绩；请更新手机网页");
        RequirePhone(run, finish.DeviceId);
        var previous = run.Revisions.SingleOrDefault(r => r.RequestId == finish.RequestId);
        if (previous != null)
        {
            if (previous.FramePtsUs != finish.FramePtsUs || previous.FrameClientUs != finish.FrameClientUs ||
                previous.EvidenceId != finish.EvidenceId || previous.Note != finish.Note)
                throw new DomainException("同一请求标识的内容不能改变");
            return previous;
        }
        if (finish.ExpectedRevision != run.Revision) throw new DomainException("成绩版本已改变，请刷新后再提交");
        if (finish.VideoMime is not ("video/mp4" or "video/webm") || finish.TimingMethod is not ("native" or "browser-estimate"))
            throw new DomainException("录像格式或计时方式无效");
        string method = run.Ready?.CameraTimebase.StartsWith("browser", StringComparison.Ordinal) == true ? "browser-estimate" : "native";
        if (finish.TimingMethod != method) throw new DomainException("计时方式必须与本场录像来源一致");
        if (Runs.Any(r => r.Revisions.Any(v => v.EvidenceId == finish.EvidenceId)))
            throw new DomainException("证据已绑定过往判读，新结果需要新的证据标识");
        if (run.State is not (RunState.Running or RunState.Review or RunState.Pending or RunState.Confirmed))
            throw new DomainException("当前场次不能提交终点判读");
        if (finish.FramePtsUs < 0 || finish.FrameClientUs <= 0 || finish.RecordingEndClientUs < finish.FrameClientUs ||
            finish.FinishLine is < 0 or > 1 || !double.IsFinite(finish.FinishLine) ||
            string.IsNullOrWhiteSpace(finish.RequestId) || string.IsNullOrWhiteSpace(finish.EvidenceId))
            throw new DomainException("终点帧或证据信息无效");
        long? elapsed = null;
        if (run.Start != null && run.Ready != null && finish.CameraMappingVerified &&
            run.SessionId == SessionId && run.Ready.Clock.SessionId == run.SessionId)
        {
            if (finish.FrameClientUs < run.Ready.RecordingStartClientUs) throw new DomainException("终点帧早于录像");
            elapsed = checked(run.Ready.Clock.ToHost(finish.FrameClientUs) - run.Start.HostUs);
            if (elapsed <= 0 || elapsed > 86_400_000_000L) throw new DomainException("用时无效，请核对所选帧和时钟映射");
        }
        var result = new ResultRevision(run.Revision + 1, finish.RequestId,
            elapsed == null ? "pending" : "confirmed", elapsed, finish.FramePtsUs, finish.FrameClientUs,
            finish.EvidenceId, finish.Note, DateTimeOffset.UtcNow, finish.VideoMime, finish.TimingMethod);
        run.Revisions.Add(result);
        run.LatestSubmission = finish;
        run.State = elapsed == null ? RunState.Pending : RunState.Confirmed;
        return result;
    }

    public void SetStatus(string runId, RunState state, string note)
    {
        if (state is not (RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled)) throw new DomainException("状态不允许人工设置");
        var run = Get(runId);
        if (run.HeatId != null) { SetHeatRunStatus(run, state, note); return; }
        run.Revisions.Add(new ResultRevision(run.Revision + 1, Wire.Id(), state.ToString().ToLowerInvariant(),
            null, null, null, run.LatestSubmission?.EvidenceId, note, DateTimeOffset.UtcNow,
            run.LatestSubmission?.VideoMime ?? "video/mp4", run.LatestSubmission?.TimingMethod ?? "native"));
        run.State = state;
    }

    public void PhoneDisconnected(string deviceId)
    {
        if (OnlinePhone == deviceId) OnlinePhone = null;
        if (ActiveHeat is { } heat && heat.DeviceId == deviceId)
        {
            heat.RecordingLive = false;
            if (heat.RequireRecordingBeforeStart && heat.State == HeatState.Armed) heat.State = HeatState.Ready;
        }
        var run = Active;
        if (run?.DeviceId == deviceId && run.State == RunState.Armed) run.State = RunState.Ready;
        // A running camera must keep recording offline. Run/start/map stay intact for later submission.
    }

    public void RecoverAfterRestart()
    {
        RecoverHeats();
        foreach (var run in Runs.Where(r => !r.IsTerminal)) run.State = RunState.Pending;
        // Stopwatch epoch/session mappings from a previous host process must never be silently reused.
    }

    public IEnumerable<RaceRun> Ranking() => Runs.Where(r => (r.TimingMethod is "timy-device" or "camera-primary") && r.State == RunState.Confirmed &&
        r.ElapsedUs.HasValue && r.Start?.Source != "simulation").OrderBy(r => r.Profile!.Round(r.ElapsedUs!.Value));
    public RaceRun Get(string id) => Runs.SingleOrDefault(r => r.Id == id) ?? throw new DomainException("场次不存在");
    private static void RequirePhone(RaceRun run, string deviceId)
    {
        if (run.DeviceId != deviceId) throw new DomainException("该手机未绑定本场比赛");
    }
}
