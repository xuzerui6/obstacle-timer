using ObstacleTimer.Core;
internal static class CloseReviewTests
{
 public static void Run(Action<bool,string> check,Action<Action,string> reject){
  long now=1_000_000;var e=new RaceEngine("session",()=>now){OnlinePhone="phone",TimyOnline=true};e.Athletes.Add(new("a","1","选手",""));
  var h=e.PrepareHeat(["a"],"结束回归",new(10000,RoundingMode.Cut,true));e.ArmHeat(h.Id);
  e.OnImpulse(TimyParser.Parse("0001 C0M 12:00:00.00 00","timy","usb",now)!);
  for(int i=0;i<9;i++)e.OnImpulse(TimyParser.Parse($"{i+1:0000} C1M 12:00:{i+1:00}.00 00","timy","usb",now+i+1)!);
  var r=e.Members(h).Single();e.SetStatus(r.Id,RunState.Dnf,"裁判记录DNF");
  reject(()=>e.CloseHeat(h.Id,h.Version),"strict close reproduces DNF plus 9 unmatched events blocker");
  reject(()=>e.CloseHeatForReview(h.Id,h.Version,""),"close with pending requires a recorded reason");
  reject(()=>e.CloseHeatForReview(h.Id,h.Version-1,"过期"),"stale close cannot discard intervening judge changes");
  string[] original=h.Finishes.Select(f=>f.Impulse.Raw).ToArray();e.CloseHeatForReview(h.Id,h.Version,"裁判明确结束并保留未处理记录");
  check(h.State==HeatState.Closed&&h.ClosedWithPending&&e.ActiveHeat==null,"DNF group can close with unprocessed records and free the next group");
  check(h.Finishes.Count==9&&h.Finishes.All(f=>!f.Ignored&&f.RunId==null)&&h.Finishes.Select(f=>f.Impulse.Raw).SequenceEqual(original),"all 9 unmatched raw impulses survive without being relabelled as mistakes");
  check(r.State==RunState.Dnf&&r.ElapsedUs==null&&h.RecordingStopped&&h.ClosureNote.Contains("9"),"existing DNF remains DNF and closure records pending counts");
  check(e.BindingHistory.Last().Action=="closedWithPending","explicit pending closure has an immutable audit entry");
  var next=e.PrepareHeat(["a"],"下一组",new(10000,RoundingMode.Cut,true));check(next.Id!=h.Id,"next group starts without resolving archived extra events");
  e.CloseHeatForReview(next.Id,next.Version,"未发令测试结束");var nr=e.Members(next).Single();
  check(nr.State==RunState.Pending&&nr.ElapsedUs==null&&nr.SourceReviewRequired,"unfinished athlete is pending, never silently classified as DNF or given a score");
  e.ReopenHeat(h.Id,h.Version,"恢复人工复核");check(h.State==HeatState.Pending&&h.Finishes.Count==9,"archived pending group can reopen for full review without losing events");
 }
}
