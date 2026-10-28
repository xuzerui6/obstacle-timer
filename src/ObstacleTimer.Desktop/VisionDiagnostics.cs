using System.Text.Json;
using ObstacleTimer.Core;
namespace ObstacleTimer.Desktop;

public record VisionDiagnostic(string HeatId,string RecordingId,string Status,int Captured,int Analyzed,int People,int TorsoPixels,
    int Queued,int Generated,int Saved,string LastError,long UpdatedHostUs=0);
public sealed partial class TimingHub
{
    private readonly Dictionary<string,VisionDiagnostic> visionDiagnostics=[];
    private void ReceiveVisionDiagnostic(JsonElement data,string device)
    {
        var d=data.Deserialize<VisionDiagnostic>(Wire.Json)??throw new DomainException("检测状态无效");
        lock(gate){
            var h=Engine.GetHeat(d.HeatId);
            if(h.DeviceId!=device||d.RecordingId.Length>0&&d.RecordingId!=h.RecordingId)throw new DomainException("检测状态归属不符");
            if(d.Captured<0||d.Analyzed<0||d.Queued<0||d.Generated<0||d.Saved<0||d.Status.Length>1000||d.LastError.Length>1000)throw new DomainException("检测状态数值无效");
            visionDiagnostics[h.Id]=d with{UpdatedHostUs=Wire.NowUs()};
        }
        Notify();
    }
}
