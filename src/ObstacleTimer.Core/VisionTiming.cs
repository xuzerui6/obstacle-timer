using System.Globalization;

namespace ObstacleTimer.Core;

public sealed record VisionSettings(double LineX = .5, double Top = .1, double Bottom = .95, double Left = .05, double Right = .95, int Direction = 1, bool FixedSide = false)
{
    public void Validate()
    {
        if (!new[]{LineX,Top,Bottom,Left,Right}.All(double.IsFinite) || Left < 0 || Right > 1 || Left >= LineX || Right <= LineX ||
            Top < 0 || Bottom > 1 || Bottom <= Top || Direction is not (-1 or 1)) throw new DomainException("终点线、区域或通过方向无效");
    }
}
public record SignedSyncSample(string Id, long T1, long T2, long T3, long T4, string Proof);
public record FrameClockEvidence(string SessionId, string PageClockId, string MapId, string Source, long MediaUs, long ClientUs,
    long CallbackClientUs, SignedSyncSample[] Samples);
public record VisionSubmission(string RequestId, string EventId, string HeatId, string RecordingId, string DeviceId, string TrackId,
    string ModelVersion, string CaptureFingerprint, VisionSettings Settings, FrameClockEvidence Clock, long BeforeMediaUs, long AfterMediaUs,
    long MaxFrameGapUs, double Confidence, bool Ambiguous, string[] Issues, string[] FrameHashes);
public sealed class VisionEvent
{
    public VisionSubmission Submission { get; set; } = null!;
    public string Id => Submission.EventId;
    public string? RunId { get; set; }
    public bool Ignored { get; set; }
    public bool NetworkProofVerified { get; set; }
    public bool EvidenceReceived { get; set; }
    public FrameCorrespondence? Correspondence { get; set; }
    public List<FrameCorrespondence> CorrespondenceHistory { get; set; } = [];
    public long? ManualFramePtsUs { get; set; }
    public string ManualReviewNote { get; set; } = "";
    public long? EstimatedElapsedUs { get; set; }
    public List<string> Reasons { get; set; } = [];
    public string? ValidationId { get; set; }
    public bool Eligible { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public record FrameCorrespondence(string RequestId, string EventId, long BeforePtsUs, long FramePtsUs, long AfterPtsUs,
    int MaxHashDistance, int UniquenessMargin, string[] Hashes, string RecordingSha256);
public record ValidationMeasurement(string Id, string Condition, long EstimatedUs, long ReferenceUs, long ReferenceUncertaintyUs, string Evidence);
public sealed class CameraValidationProfile
{
    public string Id { get; set; } = Wire.Id();
    public string RigFingerprint { get; set; } = "";
    public string CaptureFingerprint { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string ClockSource { get; set; } = "";
    public string SettingsJson { get; set; } = "";
    public string ReferenceDescription { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public bool RealMeasurements { get; set; }
    public long UsbDelayCorrectionUs { get; set; }
    public long CameraDelayCorrectionUs { get; set; }
    public long MaxRttUs { get; set; } = 5000;
    public long MaxJitterUs { get; set; } = 2000;
    public long MaxClockAgeUs { get; set; } = 5_000_000;
    public long MaxFrameGapUs { get; set; } = 10_000;
    public double MinimumConfidence { get; set; } = .8;
    public bool AllowManualStart { get; set; }
    public List<ValidationMeasurement> Measurements { get; set; } = [];
    public double? BiasUs => Measurements.Count==0?null:Measurements.Average(m=>(double)m.EstimatedUs-m.ReferenceUs);
    public long? P95AbsoluteUs => Measurements.Count==0?null:Measurements.Select(m=>Math.Abs(m.EstimatedUs-m.ReferenceUs)).Order().ElementAt((int)Math.Ceiling(Measurements.Count*.95)-1);
    public long? MaxAbsoluteUs => Measurements.Count==0?null:Measurements.Max(m=>Math.Abs(m.EstimatedUs-m.ReferenceUs));
    public bool Passes => RealMeasurements && ReviewedBy.Length > 0 && ReferenceDescription.Length > 0 &&
        RigFingerprint.Length > 0 && CaptureFingerprint.Length > 0 && ModelVersion.Length > 0 && ClockSource.Length > 0 && SettingsJson.Length > 0 &&
        MaxRttUs is > 0 and <= 20_000 && MaxJitterUs is >= 0 and <= 10_000 && MaxClockAgeUs is > 0 and <= 10_000_000 &&
        MaxFrameGapUs is > 0 and <= 20_000 && MinimumConfidence is >= .7 and <= 1 &&
        Math.Abs(UsbDelayCorrectionUs) <= 2_000_000 && Math.Abs(CameraDelayCorrectionUs) <= 2_000_000 &&
        Measurements.Count >= 100 && Measurements.Select(m=>m.Id).Distinct().Count() == Measurements.Count &&
        Measurements.All(m=>m.Evidence.Length > 0 && m.ReferenceUncertaintyUs is >= 0 and <= 1000 && m.EstimatedUs > 0 && m.ReferenceUs > 0 && Math.Abs(m.EstimatedUs-m.ReferenceUs) <= 10_000) &&
        new[]{"normal","congested","long","frame-gap"}.All(c=>Measurements.Any(m=>m.Condition==c));
}

public sealed partial class RaceEngine
{
    public const string VisionModelVersion = "bodypix-2.2.1-m075-s8-q2-torso-v1";
    public string RigFingerprint { get; set; } = "unverified-rig";
    public List<CameraValidationProfile> ValidationProfiles { get; } = [];
    public List<VisionEvent> VisionEvents => Heats.SelectMany(h=>h.VisionEvents).ToList();
    public VisionEvent GetVision(string id) => VisionEvents.SingleOrDefault(v=>v.Id==id) ?? throw new DomainException("视觉事件不存在");
    public void AddValidation(CameraValidationProfile p)
    {
        if (!p.Passes || p.RigFingerprint != RigFingerprint || p.ModelVersion != VisionModelVersion) throw new DomainException("配置未满足本机/模型和100次实测10ms门槛");
        if (ValidationProfiles.Any(v=>v.Id==p.Id)) throw new DomainException("配置标识已存在，不能覆盖测量报告");
        ValidationProfiles.Add(p);
        foreach(var v in VisionEvents.Where(v=>v.RunId==null||Get(v.RunId).State!=RunState.Confirmed))EvaluateVision(v);
    }
    public VisionEvent SubmitVision(VisionSubmission s, bool networkProofVerified)
    {
        var h=GetHeat(s.HeatId);
        if (h.FinishAuthority!="camera-primary" || h.DeviceId!=s.DeviceId || h.RecordingId!=s.RecordingId || h.RecordingId.Length==0)
            throw new DomainException("视觉事件与组次/手机/原片不符");
        var old=VisionEvents.SingleOrDefault(v=>v.Submission.RequestId==s.RequestId || v.Id==s.EventId);
        if(old!=null)
        {
            if(System.Text.Json.JsonSerializer.Serialize(old.Submission,Wire.Json)!=System.Text.Json.JsonSerializer.Serialize(s,Wire.Json)) throw new DomainException("视觉事件重试内容不能改变");
            return old;
        }
        s.Settings.Validate();
        if(s.Settings!=h.Vision || s.TrackId.Length==0 || s.ModelVersion!=VisionModelVersion || s.CaptureFingerprint!=h.Capture?.CaptureFingerprint ||
            s.Clock.Samples.Length>120 || s.Clock.ClientUs<=0 || s.Clock.PageClockId.Length==0 || s.Clock.MapId.Length==0 || s.BeforeMediaUs<0 || s.BeforeMediaUs>=s.Clock.MediaUs || s.AfterMediaUs<=s.Clock.MediaUs ||
            s.MaxFrameGapUs<0 || !double.IsFinite(s.Confidence) || s.Confidence is < 0 or >1 || s.FrameHashes.Length!=3 ||
            s.FrameHashes.Any(hash=>hash.Length!=64 || !hash.All(Uri.IsHexDigit))) throw new DomainException("视觉帧、时钟或掩膜信息无效");
        if(h.VisionEvents.Any(v=>v.Submission.TrackId==s.TrackId)) throw new DomainException("同一轨迹已记录过线，不重复计时");
        var v=new VisionEvent{Submission=s,NetworkProofVerified=networkProofVerified,Revision=1};h.VisionEvents.Add(v);h.Version++;
        EvaluateVision(v);return v;
    }
    public void EvaluateVision(VisionEvent v)
    {
        var s=v.Submission;var h=GetHeat(s.HeatId);var reasons=new List<string>();v.Eligible=false;v.ValidationId=null;
        if(h.Start==null)reasons.Add("缺少共同起跑");
        if(h.SessionId!=SessionId || s.Clock.SessionId!=SessionId)reasons.Add("时钟会话已变化");
        if(!v.NetworkProofVerified)reasons.Add("网络采样证据无法核验");
        if(!s.Settings.FixedSide)reasons.Add("未确认固定侧拍");
        if(h.EvidenceIncomplete)reasons.Add("录像证据标记不完整");
        if(s.Ambiguous)reasons.Add("遮挡或轨迹身份不明确");
        reasons.AddRange(s.Issues.Where(i=>!string.IsNullOrWhiteSpace(i)));
        ClockMap? map=null;double driftPpm=0;
        try
        {
            var samples=s.Clock.Samples.Select(t=>new SyncSample(t.T1,t.T2,t.T3,t.T4)).ToArray();map=SyncEstimator.Estimate(SessionId,samples);
            var best=samples.OrderBy(t=>t.RttUs).Take(Math.Max(5,samples.Length/3)).ToArray();
            double center=best.Average(t=>(double)t.T4),mean=best.Average(t=>t.OffsetUs),variance=best.Sum(t=>Math.Pow(t.T4-center,2));
            double slope=variance>25e12?best.Sum(t=>(t.T4-center)*(t.OffsetUs-mean))/variance:0;
            driftPpm=slope*1e6;
            if(Math.Abs(driftPpm)>500){reasons.Add("时钟漂移异常");slope=0;}
            double offset=map.OffsetUs+slope*(s.Clock.ClientUs-map.SampledClientUs);
            v.EstimatedElapsedUs=h.Start==null?null:checked((long)Math.Round(s.Clock.ClientUs+offset)-h.Start.HostUs);
            if(v.EstimatedElapsedUs is <=0 or >=86_400_000_000L){v.EstimatedElapsedUs=null;reasons.Add("视频估计用时无效");}
        }
        catch(Exception ex)when(ex is DomainException or OverflowException){reasons.Add("同步样本不足或无效");v.EstimatedElapsedUs=null;}
        if(!v.EvidenceReceived)reasons.Add("过线前后帧/躯干掩膜证据未完整接收");
        if(v.Correspondence==null)reasons.Add("原片帧对应关系未核验");
        if(v.ManualFramePtsUs.HasValue && v.ManualFramePtsUs!=v.Correspondence?.FramePtsUs)reasons.Add("人工更正帧尚未取得有效采集时间映射");
        var p=ValidationProfiles.FirstOrDefault(p=>p.Passes && p.RigFingerprint==RigFingerprint && p.CaptureFingerprint==s.CaptureFingerprint &&
            p.ModelVersion==s.ModelVersion && p.ClockSource==s.Clock.Source && p.SettingsJson==System.Text.Json.JsonSerializer.Serialize(s.Settings,Wire.Json));
        if(p==null)reasons.Add("无匹配的100次实测10ms达标配置");
        else if(map!=null)
        {
            v.ValidationId=p.Id;
            if(map.RttUs>p.MaxRttUs||map.JitterUs>p.MaxJitterUs||Math.Abs(s.Clock.ClientUs-map.SampledClientUs)>p.MaxClockAgeUs)reasons.Add("网络同步超出已验证条件");
            long gap=Math.Max(s.MaxFrameGapUs,Math.Max(s.Clock.MediaUs-s.BeforeMediaUs,s.AfterMediaUs-s.Clock.MediaUs));
            if(v.Correspondence is {} c)gap=Math.Max(gap,Math.Max(c.FramePtsUs-c.BeforePtsUs,c.AfterPtsUs-c.FramePtsUs));
            if(gap>p.MaxFrameGapUs||s.Confidence<p.MinimumConfidence)reasons.Add("帧间隔或识别质量超出已验证条件");
            if(h.Start?.Manual==true&&!p.AllowManualStart)reasons.Add("本配置未验证手动起跑");
            if(v.EstimatedElapsedUs.HasValue) v.EstimatedElapsedUs+=p.UsbDelayCorrectionUs-p.CameraDelayCorrectionUs;
        }
        v.Reasons=reasons.Distinct().ToList();v.Eligible=v.Reasons.Count==0&&v.EstimatedElapsedUs>0;
        if(v.RunId is { } runId)
        {
            var r=Get(runId);r.VisionStatus=string.Join("；",v.Reasons);
            if(r.OfficialSource=="camera" && !v.Eligible)
            {
                r.Revisions.Add(new ResultRevision(r.Revision+1,Wire.Id(),"pending",null,null,null,v.Id,"视频条件变化："+r.VisionStatus,DateTimeOffset.UtcNow,"","camera-pending"));
                r.State=RunState.Pending;r.OfficialSource="pending";
            }
            if(r.OfficialSource!="photocell-backup" && !r.SourceReviewRequired && !r.IsTerminal)
            {
                if(v.Eligible) ConfirmCamera(r,v,"系统自动核验通过");else r.State=RunState.Pending;
            }
        }
    }
    public void AssignVision(string heatId,string eventId,string runId,int version,string note)
    {
        var h=EditableHeat(heatId,version);var v=GetVision(eventId);var r=Get(runId);
        if(v.Submission.HeatId!=h.Id||r.HeatId!=h.Id||h.State==HeatState.Closed||v.Ignored||v.RunId!=null||r.VisionEventId!=null||
            r.State is RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled)throw new DomainException("视觉事件或选手已绑定/状态不符");
        v.RunId=r.Id;r.VisionEventId=v.Id;h.Version++;Audit(h,"visionAssigned",note);EvaluateVision(v);
    }
    public void SetVisionEvidence(string id,bool received)
    {
        var v=GetVision(id);if(v.EvidenceReceived==received)return;v.EvidenceReceived=received;EvaluateVision(v);
    }
    public void SetFrameCorrespondence(FrameCorrespondence proof,string videoHash)
    {
        var v=GetVision(proof.EventId);
        var old=v.CorrespondenceHistory.SingleOrDefault(p=>p.RequestId==proof.RequestId);
        if(old!=null){if(System.Text.Json.JsonSerializer.Serialize(old,Wire.Json)!=System.Text.Json.JsonSerializer.Serialize(proof,Wire.Json))throw new DomainException("帧核验重试不能改变");return;}
        if(proof.RecordingSha256!=videoHash || proof.BeforePtsUs<0 || proof.FramePtsUs<=proof.BeforePtsUs || proof.AfterPtsUs<=proof.FramePtsUs ||
            proof.MaxHashDistance is <0 or >16 || proof.UniquenessMargin<4 || proof.Hashes.Length!=3 || proof.Hashes.Any(h=>h.Length!=64||!h.All(Uri.IsHexDigit)))
            throw new DomainException("原片哈希、帧顺序或唯一对应证据无效");
        int maximum=0;
        for(int i=0;i<3;i++)maximum=Math.Max(maximum,HashDistance(v.Submission.FrameHashes[i],proof.Hashes[i]));
        if(maximum!=proof.MaxHashDistance)throw new DomainException("帧图像哈希距离不符");
        v.Correspondence=proof;v.CorrespondenceHistory.Add(proof);v.Revision++;EvaluateVision(v);
    }
    private static int HashDistance(string a,string b)
    {
        var aa=Convert.FromHexString(a);var bb=Convert.FromHexString(b);int d=0;
        for(int i=0;i<aa.Length;i++)d+=System.Numerics.BitOperations.PopCount((uint)(aa[i]^bb[i]));
        return d;
    }
    private static void ConfirmCamera(RaceRun r,VisionEvent v,string note)
    {
        if(!v.Eligible||v.EstimatedElapsedUs==null)throw new DomainException("视频未通过0.01秒实测/证据门槛");
        r.Revisions.Add(new ResultRevision(r.Revision+1,Wire.Id(),"confirmed",v.EstimatedElapsedUs,v.Submission.Clock.MediaUs,v.Submission.Clock.ClientUs,v.Id,note,DateTimeOffset.UtcNow,"","camera-validated"));
        r.State=RunState.Confirmed;r.OfficialSource="camera";
    }
    public void SelectOfficialSource(string heatId,string runId,int version,string source,string note)
    {
        var h=EditableHeat(heatId,version);var r=Get(runId);RequireNote(note);
        if(r.HeatId!=h.Id||h.FinishAuthority!="camera-primary"||r.State is RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled)throw new DomainException("选手或计时来源不符");
        if(source=="photocell-backup")
        {
            if(r.BackupElapsedUs==null||r.FinishEventId==null)throw new DomainException("请先匹配并核验光电门备份事件");
            r.Revisions.Add(new ResultRevision(r.Revision+1,Wire.Id(),"confirmed",r.BackupElapsedUs,null,null,null,note,DateTimeOffset.UtcNow,"","photocell-backup"));
            r.State=RunState.Confirmed;r.OfficialSource="photocell-backup";
            r.SourceReviewRequired=true;
        }
        else if(source=="camera")
        {
            if(r.VisionEventId==null)throw new DomainException("请先绑定视频过线事件");
            var v=GetVision(r.VisionEventId);EvaluateVision(v);ConfirmCamera(r,v,note);r.SourceReviewRequired=false;
        }
        else throw new DomainException("来源无效");
        h.Version++;Audit(h,"officialSourceChanged",note);
    }
    public void UnassignVision(string heatId,string eventId,int version,string note,bool ignore=false)
    {
        var h=EditableHeat(heatId,version);var v=GetVision(eventId);RequireNote(note);
        if(v.Submission.HeatId!=h.Id||h.State==HeatState.Closed)throw new DomainException("请先打开本组");
        if(v.RunId!=null)
        {
            var r=Get(v.RunId);r.VisionEventId=null;r.VisionStatus="视觉绑定已撤销";
            if(r.OfficialSource=="camera"){r.Revisions.Add(new ResultRevision(r.Revision+1,Wire.Id(),"pending",null,null,null,null,note,DateTimeOffset.UtcNow,"","camera-pending"));r.State=RunState.Pending;r.OfficialSource="pending";}
        }
        v.RunId=null;v.Ignored=ignore;h.Version++;Audit(h,ignore?"visionIgnored":"visionUnassigned",note);
    }
}
