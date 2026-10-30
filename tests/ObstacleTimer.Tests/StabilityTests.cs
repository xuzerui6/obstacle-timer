using ObstacleTimer.Core;
internal static class StabilityTests
{
 public static void Run(Action<bool,string> check,Action<Action,string> reject){
  foreach(var status in new[]{RunState.Dns,RunState.Dnf,RunState.Dsq}){
   var e=new RaceEngine("s",()=>1000000){OnlinePhone="p",TimyOnline=true};e.Athletes.Add(new("a","1","测试",""));
   var h=e.PrepareHeat(["a"],"状态",new(10000,RoundingMode.Cut,true));e.ArmHeat(h.Id);
   e.OnImpulse(TimyParser.Parse("0001 C0 12:00:00.00 00","t","u",1000000)!);
   e.OnImpulse(TimyParser.Parse("0001 C1 12:00:01.00 00","t","u",2000000)!);
   var r=e.Members(h).Single();e.AssignFinish(h.Id,h.Finishes[0].Id,r.Id,h.Version,"匹配");
   var settings=new VisionSettings(FixedSide:true);e.RegisterRecording(new(h.Id,"clip","p","video/webm",30,640,480,1,"test","stability",settings));
   var visual=e.SubmitVision(new("request","visual",h.Id,"clip","p","track",RaceEngine.VisionModelVersion,"stability",settings,new("s","page","map","rvfc-presentationTime",200000,1500000,1500100,[]),166667,233333,33333,.9,false,[],[new('0',64),new('1',64),new('2',64)],new(320,240)),false);
   e.AssignVision(h.Id,visual.Id,r.Id,h.Version,"匹配视觉");
   e.SelectOfficialSource(h.Id,r.Id,h.Version,"photocell-backup","明确备份");int version=r.Revision;
   e.SelectOfficialSource(h.Id,r.Id,h.Version,"photocell-backup","重复点击");check(r.Revision==version,$"{status}: repeated source selection does not append duplicate scores");
   e.SetStatus(r.Id,status,"明确状态");
   int terminalVersion=r.Revision;e.SetStatus(r.Id,status,"重复点击状态");check(r.Revision==terminalVersion,$"{status}: repeated status command is idempotent");
   check(!h.Finishes[0].Ignored&&h.Finishes[0].RunId==null,$"{status}: athlete status never relabels a real impulse as a mistaken press");
   e.SetVisionEvidence(visual.Id,true);e.UnassignVision(h.Id,visual.Id,h.Version,"迟到证据与解绑",true);e.EvaluateVision(visual);
   check(r.State==status&&r.ElapsedUs==null&&r.VisionEventId==null,$"{status}: late evidence, unbinding and background evaluation cannot restore athlete");
   e.UnassignVision(h.Id,visual.Id,h.Version,"恢复待复核候选");e.RestoreRunner(h.Id,r.Id,h.Version,"裁判明确恢复");e.AssignVision(h.Id,visual.Id,r.Id,h.Version,"重新核对");
   check(r.State==RunState.Pending&&r.SourceReviewRequired&&r.ElapsedUs==null,$"{status}: explicit restore permits reassociation but never fabricates a score");
  }
  var flow=new RaceEngine("s",()=>1000000){OnlinePhone="p",TimyOnline=true};flow.Athletes.Add(new("a","1","连续组次",""));
  Heat? previous=null;
  for(int i=0;i<50;i++){
   var h=flow.PrepareHeat(["a"],"组"+i,new(10000,RoundingMode.Cut,true));flow.ArmHeat(h.Id);flow.OnImpulse(TimyParser.Parse($"{i+1:0000} C0 12:{i:00}:00.00 00","t","u",1000000+i)!);
   if(previous!=null){flow.RecordingUpdate(new(previous.Id,"clip"+(i-1),false,true,"保存完成",2),"p");check(previous.RecordingSaveConfirmed&&h.State==HeatState.Running,$"group {i}: old stop acknowledgment cannot stop new group");}
   if(i%2==0)flow.RegisterRecording(new(h.Id,"clip"+i,"p","video/webm",30,640,480,1));
   flow.CloseHeatForReview(h.Id,h.Version,"明确结束并保留");
   if(i%2==1)flow.RegisterRecording(new(h.Id,"clip"+i,"p","video/webm",30,640,480,1));
   check(h.StopRecordingRequested&&!h.RecordingSaveConfirmed&&h.State==HeatState.Closed,$"group {i}: stop request is distinct from saved confirmation, including late registration");
   previous=h;
  }
  flow.RecordingUpdate(new(previous!.Id,"clip49",false,true,"保存完成",2),"p");
  check(flow.Heats.Count==50&&flow.Heats.All(h=>h.State==HeatState.Closed&&h.RecordingSaveConfirmed),"50 continuous groups retain correct stop/save ownership and recovery");
 }
}
