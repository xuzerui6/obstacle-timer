using Microsoft.Data.Sqlite;
using ObstacleTimer.Core;
using ObstacleTimer.Desktop;
internal static class MigrationV4Tests
{
 public static void Run(Action<bool,string> check){
  var path=Path.Combine(Path.GetTempPath(),"ObstacleTimer-v3-migration-"+Wire.Id());Directory.CreateDirectory(path);
  var original=Path.Combine(path,"retained-original.bin");File.WriteAllText(original,"unchanged original recording fragments");
  var e=new RaceEngine("v3-session",()=>1000000){OnlinePhone="phone",TimyOnline=true};e.Athletes.Add(new("a","7","旧硬件成绩",""));
  var h=e.PrepareHeat(["a"],"旧组",new(10000,RoundingMode.Cut,true),false,"timy-device");e.ArmHeat(h.Id);
  e.OnImpulse(TimyParser.Parse("0001 C0M 12:00:00.00 00","timy","usb",1000000)!);e.OnImpulse(TimyParser.Parse("0001 C1M 12:00:03.57 00","timy","usb",2000000)!);
  var r=e.Members(h).Single();e.AssignFinish(h.Id,h.Finishes[0].Id,r.Id,h.Version,"旧版绑定");h.RecordingStopped=true;e.CloseHeat(h.Id,h.Version);
  using(var store=new Store(path))store.Save(e,"fixture","v3 source");
  using(var db=new SqliteConnection("Data Source="+Path.Combine(path,"timing.sqlite"))){db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA user_version=3";command.ExecuteNonQuery();}
  using(var store=new Store(path)){
   var restored=new RaceEngine("v4-session");store.Load(restored);var result=restored.Get(r.Id);
   check(result.ElapsedUs==3570000&&result.TimingMethod=="timy-device"&&result.Revision==1,"v3 to v4 preserves original hardware score/source and revisions");
   check(restored.GetHeat(h.Id).State==HeatState.Closed&&restored.DeviceImpulses.Count==2&&restored.BindingHistory.Count==2,"v4 migration preserves raw impulses, closed state and binding history");
   store.Save(restored,"upgraded","v4");
  }
  var backup=Directory.GetFiles(path,"timing.pre-v4-*.sqlite").Single();
  using(var db=new SqliteConnection("Data Source="+backup)){db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA user_version";check((long)command.ExecuteScalar()! ==3,"SQLite backup retains pre-migration schema before any v4 changes");command.CommandText="SELECT COUNT(*) FROM device_impulses";check((long)command.ExecuteScalar()! ==2,"SQLite backup includes all original device events");}
  using(var store=new Store(path))check(Directory.GetFiles(path,"timing.pre-v4-*.sqlite").Length==1,"reopening v4 never overwrites or duplicates migration backup");
  check(File.ReadAllText(original)=="unchanged original recording fragments","migration leaves existing original fragments untouched");
 }
}
