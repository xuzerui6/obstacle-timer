using System.Text.Json;
using ObstacleTimer.Core;

internal static class VisionTests
{
    public static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        const long start=1_000_000_000,frame=start+2_000_000;
        var e=new RaceEngine("vision-session",()=>frame){OnlinePhone="phone",TimyOnline=true,RigFingerprint="test-rig"};
        e.Athletes.Add(new("a","1","躯干测试",""));e.Athletes.Add(new("b","2","第二人",""));
        var h=e.PrepareHeat(["a","b"],"视频主计时",new(10000,RoundingMode.Cut,true));
        e.ArmHeat(h.Id);e.OnImpulse(TimyParser.Parse("0001 C0 12:00:00.00 00","timy","usb",start)!);
        var settings=new VisionSettings(FixedSide:true);
        e.RegisterRecording(new(h.Id,"video","phone","video/webm",120,1280,720,1,"test","capture",settings));
        var samples=Enumerable.Range(0,30).Select(i=>new SignedSyncSample("s"+i,frame-100000-i*10000,frame-99500-i*10000,frame-99500-i*10000,frame-99000-i*10000,"test-proof")).ToArray();
        var submission=new VisionSubmission("request","visual",h.Id,"video","phone","track",RaceEngine.VisionModelVersion,"capture",settings,
            new("vision-session","page","map","rvfc-captureTime",200000,frame,frame+900000,samples),191667,208333,8333,.95,false,[],[new('0',64),new('1',64),new('2',64)],new FrameGeometry(320,180));
        var v=e.SubmitVision(submission,true);var r=e.Members(h).First();
        e.AssignVision(h.Id,v.Id,r.Id,h.Version,"号码核对");
        check(v.EstimatedElapsedUs==2000000&&r.ElapsedUs==null&&r.State==RunState.Pending,"vision uses pixel-bound frame clock, never inference completion or recorder start");
        check(!v.Eligible&&v.Reasons.Any(s=>s.Contains("100次")),"no measured profile means automatic video confirmation stays off");
        check(ReferenceEquals(v,e.SubmitVision(submission,true)),"visual event retry is idempotent");
        reject(()=>e.SubmitVision(submission with{Clock=submission.Clock! with{ClientUs=frame+1}},true),"visual retry cannot change its timestamp");
        reject(()=>e.SubmitVision(submission with{RequestId="other",EventId="other"},true),"same trajectory cannot create two finishes");
        reject(()=>e.SelectOfficialSource(h.Id,r.Id,h.Version,"photocell-backup","原因"),"video failure never fabricates a missing photocell backup");
        e.OnImpulse(TimyParser.Parse("0002 C1M 12:00:01.97 00","timy","usb",frame+5000000)!);
        e.AssignFinish(h.Id,h.Finishes[0].Id,r.Id,h.Version,"核对备份");
        check(r.BackupElapsedUs==1970000&&r.ElapsedUs==null,"photocell assignment preserves backup independently and cannot create official video score");
        reject(()=>e.SelectOfficialSource(h.Id,r.Id,h.Version,"photocell-backup",""),"using backup requires a judge reason");
        e.SelectOfficialSource(h.Id,r.Id,h.Version,"photocell-backup","视频未通过配置门槛，裁判确认备份");
        check(r.ElapsedUs==1970000&&r.OfficialSource=="photocell-backup"&&r.Revision==1,"explicit judge approval activates photocell backup with history");
        reject(()=>e.SelectOfficialSource(h.Id,r.Id,h.Version,"camera","切回"),"unvalidated video cannot be selected as official source");
        e.SetVisionEvidence(v.Id,true);check(r.ElapsedUs==1970000,"later video evidence never silently switches an explicit backup");
        var profile=new CameraValidationProfile{RigFingerprint="test-rig",CaptureFingerprint="capture",ModelVersion=RaceEngine.VisionModelVersion,
            ClockSource="rvfc-captureTime",SettingsJson=JsonSerializer.Serialize(settings,Wire.Json),ReviewedBy="UNIT TEST ONLY",RealMeasurements=true,
            ReferenceDescription="synthetic gate fixtures; not physical acceptance",UsbDelayCorrectionUs=300,CameraDelayCorrectionUs=100,
            Measurements=Enumerable.Range(0,100).Select(i=>new ValidationMeasurement("measurement"+i,new[]{"normal","congested","long","frame-gap"}[i%4],2000000,2000000,500,"TEST FIXTURE ONLY "+i)).ToList()};
        var invalid=JsonSerializer.Deserialize<CameraValidationProfile>(JsonSerializer.Serialize(profile,Wire.Json),Wire.Json)!;
        invalid.Measurements.RemoveAt(0);reject(()=>e.AddValidation(invalid),"99 measurements cannot pass the 100 comparison gate");
        invalid.Measurements.Add(new("over-limit","normal",2010001,2000000,500,"test"));reject(()=>e.AddValidation(invalid),"one error above 10ms rejects the entire profile");
        invalid.Measurements[^1]=new("over-limit","normal",2010000,2000000,1500,"test");reject(()=>e.AddValidation(invalid),"unreliable independent reference cannot validate a profile");
        e.AddValidation(profile);e.EvaluateVision(v);
        check(v.EstimatedElapsedUs==2000200&&!v.Eligible,"USB and camera corrections apply once but cannot bypass original frame correspondence");
        var proof=new FrameCorrespondence("proof",v.Id,100000,108333,116666,0,8,submission.FrameHashes,new('a',64));
        reject(()=>e.SetFrameCorrespondence(proof,new('b',64)),"frame proof must refer to SHA256-confirmed original");
        e.SetFrameCorrespondence(proof,new('a',64));check(v.Eligible&&r.ElapsedUs==1970000,"complete valid evidence still preserves explicitly selected backup");
        e.SelectOfficialSource(h.Id,r.Id,h.Version,"camera","裁判切回已核验视频");
        check(r.ElapsedUs==2000200&&r.Revision==2&&r.Revisions[0].ElapsedUs==1970000,"source switch appends version and preserves both independently measured moments");
        e.EvaluateVision(v);check(r.Revision==2&&v.EstimatedElapsedUs==2000200,"repeated background evaluation cannot duplicate score or calibration");
        v.ManualFramePtsUs=116666;e.EvaluateVision(v);
        check(r.State==RunState.Pending&&r.ElapsedUs==null&&r.Revision==3&&r.Revisions[1].ElapsedUs==2000200,"manual frame change revokes current video approval with append-only history");
        v.ManualFramePtsUs=null;e.SetVisionEvidence(v.Id,false);check(!v.Eligible,"missing evidence disables camera confirmation");
        e.SetVisionEvidence(v.Id,true);check(r.State==RunState.Confirmed&&r.ElapsedUs==2000200,"valid complete video can confirm without adopting optical finish time");
        // A second athlete has no optical finish at all.
        var s2=submission with{RequestId="request2",EventId="visual2",TrackId="track2",Clock=submission.Clock! with{ClientUs=frame+1000}};
        var v2=e.SubmitVision(s2,true);var r2=e.Members(h).Last();e.AssignVision(h.Id,v2.Id,r2.Id,h.Version,"第二人");
        e.SetVisionEvidence(v2.Id,true);e.SetFrameCorrespondence(proof with{RequestId="proof2",EventId=v2.Id},new('a',64));
        check(r2.State==RunState.Confirmed&&r2.ElapsedUs==2001200&&r2.FinishEventId==null,"validated video can independently confirm an athlete without any C1 event");
        v2.NetworkProofVerified=false;e.EvaluateVision(v2);check(!v2.Eligible&&r2.State==RunState.Pending,"invalid network proof revokes video instead of falling back");
        v2.NetworkProofVerified=true;e.ValidationProfiles.Clear();e.EvaluateVision(v2);check(!v2.Eligible,"low RTT alone never replaces end-to-end measured acceptance");
        e.UnassignVision(h.Id,v2.Id,h.Version,"号码复核");check(r2.VisionEventId==null&&v2.RunId==null,"visual unassignment frees both sides atomically");
        e.UnassignFinish(h.Id,h.Finishes[0].Id,h.Version,"撤销备份绑定");check(r.BackupElapsedUs==null,"backup unassignment cannot erase the saved optical impulse");
    }
}
