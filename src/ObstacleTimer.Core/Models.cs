using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ObstacleTimer.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Pack(string type, object data) => JsonSerializer.Serialize(new { type, data }, Json);
}

public record Athlete(string Id, string Bib, string Name, string Team);
public enum RunState { Preparing, Ready, Armed, Running, Review, Confirmed, Dns, Dnf, Dsq, Pending, Cancelled }
public record TimingImpulse(string EventId, string DeviceId, string ConnectionId, string Raw,
    string Channel, long DeviceTimeUs, long HostUs, bool Manual, string Source, string DeviceNumber = "", int FractionDigits = 0);
public record ClockMap(string SessionId, double OffsetUs, double RttUs, double JitterUs,
    int Samples, long SampledClientUs)
{
    public long ToHost(long clientUs) => checked((long)Math.Round(clientUs + OffsetUs));
    public bool IsUsable(string sessionId, long hostNow) =>
        SessionId == sessionId && Samples >= 5 && double.IsFinite(OffsetUs) &&
        double.IsFinite(RttUs) && RttUs >= 0 && RttUs <= 2_000_000 &&
        double.IsFinite(JitterUs) && JitterUs >= 0 && SampledClientUs > 0 &&
        hostNow - ToHost(SampledClientUs) is >= -2_000_000 and <= 60_000_000;
}
public record PhoneReady(string RunId, string DeviceId, ClockMap Clock, long RecordingStartClientUs,
    string CameraTimebase, string Model, double Fps, int Width, int Height);
public record FinishSubmission(string RequestId, string RunId, string DeviceId, int ExpectedRevision,
    long FramePtsUs, long FrameClientUs, long RecordingEndClientUs, string EvidenceId,
    double FinishLine, string Note, bool CameraMappingVerified, string VideoMime = "video/mp4", string TimingMethod = "native");
public record ResultRevision(int Revision, string RequestId, string Status, long? ElapsedUs,
    long? FramePtsUs, long? FrameClientUs, string? EvidenceId, string Note, DateTimeOffset CreatedAt, string VideoMime = "video/mp4", string TimingMethod = "native");

public sealed class RaceRun
{
    public string? VisionEventId { get; set; }
    public long? BackupElapsedUs { get; set; }
    public bool BackupManual { get; set; }
    public string OfficialSource { get; set; } = "historical";
    public bool SourceReviewRequired { get; set; }
    public string VisionStatus { get; set; } = "";
    public string? HeatId { get; set; }
    public string TimingMethod { get; set; } = "legacy-estimate";
    public string? FinishEventId { get; set; }
    public TimingProfile? Profile { get; set; }
    public List<VideoReview> Reviews { get; set; } = [];
    public string Id { get; set; } = Wire.Id();
    public Athlete Athlete { get; set; } = new("", "", "", "");
    public RunState State { get; set; } = RunState.Preparing;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SessionId { get; set; } = "";
    public string? DeviceId { get; set; }
    public PhoneReady? Ready { get; set; }
    public TimingImpulse? Start { get; set; }
    public List<ResultRevision> Revisions { get; set; } = [];
    public FinishSubmission? LatestSubmission { get; set; }
    public int Revision => Revisions.Count;
    public long? ElapsedUs => Revisions.LastOrDefault()?.ElapsedUs;
    public string Quality => Start?.Source == "simulation" ? "模拟数据 · 不计入正式排名" :
        TimingMethod == "camera-primary" ? OfficialSource == "photocell-backup" ? (BackupManual ? "Timy3 手动触发备份" : "光电门备份")+" · 裁判明确启用" : OfficialSource == "camera" ? "视频躯干主成绩 · 已验证配置" : "视频主成绩待核验 · " + VisionStatus :
        TimingMethod == "timy-device" ? (Start?.Manual == true || Revisions.LastOrDefault()?.TimingMethod == "timy-manual" ? "Timy3 设备时间／手动触发" : "Timy3 设备时间／外部触发") :
        Ready?.CameraTimebase.StartsWith("browser", StringComparison.Ordinal) == true ? "浏览器估计 · 相机延迟未校准 · 训练参考" :
        Ready == null || !Ready.CameraTimebase.Contains("mapped") ? "时间映射待核验" :
        $"网络估计 · RTT {Ready.Clock.RttUs / 1000:F1} ms · 端到端精度未验证";
    public bool IsTerminal => State is RunState.Confirmed or RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled;
}

public sealed class DomainException(string message) : Exception(message);

public static partial class TimyParser
{
    // Timy number may be an impulse counter rather than an athlete bib. Athlete binding is host-owned.
    [GeneratedRegex(@"^\s*(?<info>[?mcCdintx]?)\s*(?<bib>\d{1,4})\s+(?<channel>[cC][0-8][mM]?)\s+(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})[.,:](?<fraction>\d{1,4})(?:\s.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static TimingImpulse? Parse(string raw, string device, string connection, long hostUs, string source = "timy")
    {
        var m = Pattern().Match(raw.TrimEnd('\r', '\n', '\0'));
        if (!m.Success || "mcCdin".Contains(m.Groups["info"].Value, StringComparison.Ordinal) && m.Groups["info"].Value != "") return null;
        int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
            min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
            s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (h > 23 || min > 59 || s > 59 || hostUs <= 0) return null;
        long tod = ((h * 60L + min) * 60 + s) * 1_000_000 +
            long.Parse(m.Groups["fraction"].Value.PadRight(6, '0'), CultureInfo.InvariantCulture);
        string channel = m.Groups["channel"].Value.ToUpperInvariant();
        return new TimingImpulse(Wire.Id(), device, connection, raw, channel, tod, hostUs,
            channel.EndsWith('M'), source, m.Groups["bib"].Value, m.Groups["fraction"].Value.Length);
    }
}

public record SyncSample(long T1, long T2, long T3, long T4)
{
    public double RttUs => (T4 - T1) - (T3 - T2);
    public double OffsetUs => ((double)(T2 - T1) + (T3 - T4)) / 2;
}
public static class SyncEstimator
{
    public static ClockMap Estimate(string sessionId, IEnumerable<SyncSample> samples)
    {
        var all = samples.Where(s => s.T4 >= s.T1 && s.T3 >= s.T2 && s.RttUs is >= 0 and <= 2_000_000).ToArray();
        if (all.Length < 5) throw new DomainException("至少需要 5 个有效同步样本");
        var best = all.OrderBy(s => s.RttUs).Take(5).ToArray();
        var offsets = best.Select(s => s.OffsetUs).Order().ToArray();
        double offset = offsets[offsets.Length / 2];
        return new ClockMap(sessionId, offset, best.Min(s => s.RttUs),
            offsets.Max() - offsets.Min(), all.Length, all.Max(s => s.T4));
    }
}
