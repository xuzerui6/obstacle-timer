using System.Text.Json;
using ObstacleTimer.Core;

namespace ObstacleTimer.Desktop;

// Test-only control through the owning process's stdin, never a LAN administrative endpoint.
internal static class HeadlessCommands
{
    public static void Run(TimingHub hub, Action shutdown)
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            string? requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                requestId = root.GetProperty("id").GetString();
                object result = root.GetProperty("type").GetString() switch
                {
                    "athlete" => hub.AddAthlete(root.GetProperty("bib").GetString()!, root.GetProperty("name").GetString()!, "自动测试"),
                    "prepare" => hub.Prepare(root.GetProperty("athleteId").GetString()!),
                    "prepareHeat" => hub.PrepareHeat(root.GetProperty("athleteIds").Deserialize<string[]>(Wire.Json)!, "网页测试组", new TimingProfile(10_000, RoundingMode.Cut, true)),
                    "armHeat" => ArmHeat(hub, root.GetProperty("heatId").GetString()!),
                    "assign" => Assign(hub, root),
                    "selectSource" => Source(hub,root),
                    "closeHeat" => CloseHeat(hub, root),
                    "closeForReview" => CloseForReview(hub,root),
                    "arm" => Arm(hub, root.GetProperty("runId").GetString()!),
                    "impulse" => Impulse(hub, root.GetProperty("raw").GetString()!),
                    "snapshot" => hub.Snapshot(),
                    "shutdown" => Close(shutdown),
                    _ => throw new DomainException("未知测试命令")
                };
                Console.WriteLine(JsonSerializer.Serialize(new { id = requestId, ok = true, result }, Wire.Json));
            }
            catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { id = requestId, ok = false, error = ex.Message }, Wire.Json)); }
            Console.Out.Flush();
        }
    }
    private static object Arm(TimingHub hub, string run) { hub.Arm(run); return hub.Snapshot(); }
    private static object ArmHeat(TimingHub hub, string heat) { hub.ArmHeat(heat); return hub.Snapshot(); }
    private static object Assign(TimingHub hub, JsonElement root)
    {
        string heat = root.GetProperty("heatId").GetString()!;
        hub.AssignFinish(heat, root.GetProperty("eventId").GetString()!, root.GetProperty("runId").GetString()!, hub.Engine.GetHeat(heat).Version, "自动化判读匹配");
        return hub.Snapshot();
    }
    private static object CloseHeat(TimingHub hub, JsonElement root)
    {
        string heat = root.GetProperty("heatId").GetString()!; hub.CloseHeat(heat, hub.Engine.GetHeat(heat).Version); return hub.Snapshot();
    }
    private static object CloseForReview(TimingHub hub,JsonElement root){string heat=root.GetProperty("heatId").GetString()!;hub.CloseHeatForReview(heat,hub.Engine.GetHeat(heat).Version,"自动化测试明确结束，未处理记录保留待复核");return hub.Snapshot();}
    private static object Source(TimingHub hub,JsonElement root)
    {
        string heat=root.GetProperty("heatId").GetString()!;
        hub.SelectOfficial(heat,root.GetProperty("runId").GetString()!,hub.Engine.GetHeat(heat).Version,root.GetProperty("source").GetString()!,"自动化测试明确确认备份");return hub.Snapshot();
    }
    private static object Impulse(TimingHub hub, string raw) { hub.Inject(raw); return hub.Snapshot(); }
    private static object Close(Action shutdown) { shutdown(); return new { closing = true }; }
}
