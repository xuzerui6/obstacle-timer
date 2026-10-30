using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ObstacleTimer.Core;
using QRCoder;
namespace ObstacleTimer.Desktop;
public partial class MainWindow : Window
{
    private readonly TimingHub hub;
    private List<RaceRun> runs = [];
    private List<Heat> heats = [];
    private List<AthletePick> picks = [];
    private string? selectedHeat;
    private bool refreshing;
    private int simulatedNumber;
    private Heat? Current => heats.FirstOrDefault(h => h.Id == selectedHeat);
    public MainWindow(TimingHub hub)
    {
        this.hub = hub; InitializeComponent();Title="障碍竞速 · "+Wire.AppVersion;
        PrecisionList.ItemsSource = new[] { new PrecisionChoice("0.01 秒", 10_000), new PrecisionChoice("0.001 秒", 1000), new PrecisionChoice("0.0001 秒", 100), new PrecisionChoice("0.1 秒", 100_000), new PrecisionChoice("1 秒", 1_000_000) };
        PrecisionList.SelectedIndex = 0; RoundingList.ItemsSource = Enum.GetValues<RoundingMode>(); RoundingList.SelectedIndex = 0;
        AddressList.ItemsSource = TimingHub.Addresses().Append("127.0.0.1").ToArray(); AddressList.SelectedIndex = 0;
        SimulationControls.Visibility = hub.Simulation ? Visibility.Visible : Visibility.Collapsed;
        hub.Changed += OnChanged; Closed += (_, _) => hub.Changed -= OnChanged; Refresh();
    }
    private void OnChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh); }
    private void Refresh()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            var snapshot = JsonSerializer.SerializeToElement(hub.Snapshot(), Wire.Json);
            runs = snapshot.GetProperty("runs").Deserialize<List<RaceRun>>(Wire.Json)!; heats = snapshot.GetProperty("heats").Deserialize<List<Heat>>(Wire.Json)!;
            var athletes = snapshot.GetProperty("athletes").Deserialize<List<Athlete>>(Wire.Json)!;
            if (athletes.Count != picks.Count)
            {
                var marked = picks.Where(p => p.Selected).Select(p => p.Athlete.Id).ToHashSet();
                picks = athletes.Select(a => new AthletePick(a) { Selected = marked.Contains(a.Id) }).ToList(); AthleteGrid.ItemsSource = picks;
            }
            selectedHeat ??= heats.LastOrDefault()?.Id; UpdateHeatList(HeatList);UpdateHeatList(VisionHeatList);
            bool online = snapshot.GetProperty("timyOnline").GetBoolean(), phone = snapshot.GetProperty("phoneOnline").ValueKind != JsonValueKind.Null;
            ConnectionLabel.Text = $"Timy3 {(online ? "● 已连接" : "○ 未连接")}     终点手机 {(phone ? "● 在线" : "○ 未连接")}";
            UsbStatus.Text = online ? "Timy3 USB · 已连接" : "Timy3 USB · 等待连接"; RawLine.Text = hub.LastDeviceLine;
            var h = Current;
            HeatLabel.Text = h == null ? "勾选多人，准备共同起跑" : $"{h.Name} · {h.RunIds.Count} 人 · {HeatNameOf(h.State)}";
            RunStatus.Text = h == null ? "先连接手机；核对 Timy3 精度及舍入后创建组次" :
                $"起跑：{(h.Start == null ? "等待 START" : TimeOfDay(h.Start.DeviceTimeUs) + " [设备号" + h.Start.DeviceNumber + "]")} · 待匹配 {h.Finishes.Count(f => !f.Ignored && f.RunId == null)} · {h.Profile.UnitUs / 1_000_000m} 秒 / {h.Profile.Rounding.ToString().ToUpperInvariant()}" +
                (h.ClosedWithPending ? " · 已结束，未处理记录保留待复核" : "") + (h.EvidenceIncomplete ? " · 录像证据不完整" : "") + (h.Warning.Length > 0 ? "\n" + h.Warning : "");
            if (h != null) RunStatus.Text += " · 相机：" + (h.StopRecordingRequested&&!h.RecordingSaveConfirmed ? "已请求停止，等待手机保存确认" : h.RecordingStopped ? "已结束/缺失已记录" : h.RecordingId.Length == 0 ? "未开启（可先发令）" : h.RecordingLive ? "录像中" : "离线/状态未知");
            ArmButton.IsEnabled = h != null && (h.State is HeatState.Preparing or HeatState.Ready) && online && (!h.RequireRecordingBeforeStart || h.RecordingLive && phone);
            ArmButton.Content = h?.RequireRecordingBeforeStart == true ? "旧组：录像就绪 → 待发令" : "进入待发令（无需先录像）";
            QualityLabel.Text = hub.Simulation ? "模拟模式 · 模拟数据不进入排名" : "视频候选不受精度配置限制 · 达标配置只控制成绩自动确认 · 光电门备份须裁判确认";
            var members = h == null ? [] : runs.Where(r => r.HeatId == h.Id).ToArray();
            var selectedMembers = MemberGrid.SelectedItems.Cast<ResultRow>().Select(r => r.Id).ToHashSet();
            var memberRows = Rows(members); var memberChanged=UpdateRows(MemberGrid,memberRows);
            if(memberChanged)foreach (var row in memberRows.Where(r => selectedMembers.Contains(r.Id))) MemberGrid.SelectedItems.Add(row);
            string? selectedEvent = (FinishGrid.SelectedItem as FinishRow)?.Id;
            var finishes = h?.Finishes.Select((f, i) => new FinishRow(f.Id, i + 1, TimeOfDay(f.Impulse.DeviceTimeUs),
                f.Ignored ? "误按（已保留）" : f.RunId != null ? "→ " + runs.First(r => r.Id == f.RunId).Athlete.Bib :
                f.ReviewNote.Length > 0 ? "已核验 · 待匹配" : f.Issue.Length > 0 ? f.Issue : h.Start != null && f.Impulse.DeviceTimeUs < h.Start.DeviceTimeUs ? "待核验跨午夜/回拨" : "待匹配")).ToArray() ?? [];
            if(UpdateRows(FinishGrid,finishes))FinishGrid.SelectedItem = finishes.FirstOrDefault(f => f.Id == selectedEvent) ?? finishes.FirstOrDefault(f => h!.Finishes.First(x => x.Id == f.Id).RunId == null && !h.Finishes.First(x => x.Id == f.Id).Ignored);
            UpdateRows(GroupResults,memberRows.OrderBy(r=>r.SortUs).ToArray());
            string? chosenRun=(VisionRunner.SelectedItem as ResultRow)?.Id;if(UpdateRows(VisionRunner,memberRows))VisionRunner.SelectedItem=memberRows.FirstOrDefault(r=>r.Id==chosenRun)??memberRows.FirstOrDefault();
            string? chosenVision=(VisionGrid.SelectedItem as VisionRow)?.Id;
            var visionRows=h?.VisionEvents.Select(v=>new VisionRow(v.Id,v.Submission.TrackId.Split('-').Last(),v.RunId==null?"待匹配":runs.First(r=>r.Id==v.RunId).Athlete.Bib,
                v.EstimatedElapsedUs.HasValue?(v.EstimatedElapsedUs.Value/1_000_000m).ToString("F4"):"—",v.Ignored?"已忽略":v.Eligible?"已通过核验":(v.Submission.Origin=="live"?"实时候选 · ":"原片/人工候选 · ")+string.Join("；",v.Reasons),v.Revision)).ToArray()??[];
            if(UpdateRows(VisionGrid,visionRows))VisionGrid.SelectedItem=visionRows.FirstOrDefault(v=>v.Id==chosenVision)??visionRows.FirstOrDefault();
            var diagnostic=snapshot.GetProperty("visionDiagnostics").Deserialize<List<VisionDiagnostic>>(Wire.Json)?.FirstOrDefault(d=>d.HeatId==h?.Id);
            VisionPipelineStatus.Text=h==null?"尚无组次，请配对手机并创建组次":$"{h.Name} · {(h.ClosedWithPending?"已结束，保留待复核":HeatNameOf(h.State))} · {(h.StopRecordingRequested&&!h.RecordingSaveConfirmed?"已请求停录，等待保存确认":h.RecordingStopped?"相机已停止":h.RecordingId.Length==0?"尚未开启录像":h.RecordingLive?"整组录像中":"相机离线/状态未知")} · 电脑已收候选 {visionRows.Length}";
            if(diagnostic!=null)VisionPipelineStatus.Text+=$"\n{diagnostic.Status} · 采集 {diagnostic.Captured} 帧 / 分析 {diagnostic.Analyzed} 帧 · 可见目标 {diagnostic.People} / 躯干像素 {diagnostic.TorsoPixels} · 本机候选 {diagnostic.Generated} / 已保存 {diagnostic.Saved} · 排队 {diagnostic.Queued}"+(diagnostic.LastError.Length>0?"\n检测错误："+diagnostic.LastError:"");
            VisionEmptyState.Visibility=visionRows.Length==0?Visibility.Visible:Visibility.Collapsed;
            VisionEmptyState.Text=h?.FinishAuthority=="timy-device"?"这是旧版设备计时组，本组没有视频检测记录。请新建视频主计时组。":h?.RecordingId.Length==0?"还未开始录像。手机点击开启相机并录像后，人员躯干按设置方向通过参考线才会产生候选。":diagnostic?.LastError.Length>0?"手机检测已报错，原片保留。请刷新手机网页使用修复版；尚无达标配置不会阻止候选显示。":h?.RecordingStopped==true?"本组录像已停止，电脑未收到过线候选。原片仍保留在手机；可恢复录像逐帧复核，未处理记录可随本组结束保留待核验。":"尚未收到过线候选。请确认手机显示检测中、参考线及方向正确。没有实测配置只影响自动确认，不会隐藏候选。";
            ExportDraftButton.IsEnabled=visionRows.Length>0;
            int profiles=snapshot.GetProperty("validationProfiles").GetArrayLength();
            ValidationLabel.Text=$"本机 USB 配置标识：{snapshot.GetProperty("rigFingerprint").GetString()}\n已审核配置：{profiles}。完整配置≥100次、最大绝对误差≤10ms；每个事件仍需同步、帧对应、画面和证据核验。";
            string? selectedResult = (ResultsGrid.SelectedItem as ResultRow)?.Id;
            var all = Rows(runs); if(UpdateRows(ResultsGrid,all))ResultsGrid.SelectedItem = all.FirstOrDefault(r => r.Id == selectedResult);
            UpdateActionButtons();VisionSelected(this,new SelectionChangedEventArgs(Selector.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));
            if (hub.LastError.Length > 0) MessageLabel.Text = hub.LastError;
        }
        finally { refreshing = false; }
    }
    private static bool UpdateRows<T>(ItemsControl control,T[] rows){
        if(control.ItemsSource is IEnumerable<T> current&&current.SequenceEqual(rows))return false;
        control.ItemsSource=rows;return true;
    }
    private void ActionSelectionChanged(object sender,SelectionChangedEventArgs e){if(!refreshing)UpdateActionButtons();}
    private void UpdateActionButtons(){
        if(UseCameraButton==null)return;
        var h=Current;bool editable=h!=null&&h.State!=HeatState.Closed;
        var member=runs.FirstOrDefault(r=>r.Id==(MemberGrid.SelectedItem as ResultRow)?.Id);
        var runner=runs.FirstOrDefault(r=>r.Id==(VisionRunner.SelectedItem as ResultRow)?.Id);
        var finish=h?.Finishes.FirstOrDefault(f=>f.Id==(FinishGrid.SelectedItem as FinishRow)?.Id);
        var visual=h?.VisionEvents.FirstOrDefault(v=>v.Id==(VisionGrid.SelectedItem as VisionRow)?.Id);
        bool available=runner!=null&&runner.State is not (RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled);
        PrepareButton.IsEnabled=hub.Engine.ActiveHeat==null;
        CloseGroupButton.IsEnabled=CloseGroupButton2.IsEnabled=editable;
        CancelGroupButton.IsEnabled=editable&&h!.Start==null;
        BindVisionButton.IsEnabled=editable&&available&&visual is {Ignored:false,RunId:null}&&runner!.VisionEventId==null;
        UndoVisionButton.IsEnabled=editable&&visual?.RunId!=null;
        IgnoreVisionButton.IsEnabled=editable&&visual is {Ignored:false};
        UseBackupButton.IsEnabled=editable&&available&&runner!.BackupElapsedUs.HasValue;
        UseCameraButton.IsEnabled=editable&&available&&runner!.VisionEventId is {} id&&h!.VisionEvents.Any(v=>v.Id==id&&v.Eligible);
        AssignFinishButton.IsEnabled=editable&&finish is {Ignored:false,RunId:null}&&member!=null&&member.FinishEventId==null&&member.State is not (RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled);
        UndoFinishButton.IsEnabled=editable&&finish?.RunId!=null;
        IgnoreFinishButton.IsEnabled=editable&&finish is {RunId:null};
        ReviewFinishButton.IsEnabled=editable&&finish is {RunId:null,Ignored:false};
        DnsButton.IsEnabled=DnfButton.IsEnabled=editable&&member!=null;
        DsqButton.IsEnabled=member!=null&&(editable||member.State==RunState.Confirmed);
        RestoreButton.IsEnabled=editable&&member!=null&&member.State is RunState.Dns or RunState.Dnf or RunState.Dsq or RunState.Cancelled;
        SwapButton.IsEnabled=editable&&MemberGrid.SelectedItems.Count==2;
        ReopenButton.IsEnabled=h?.State==HeatState.Closed&&hub.Engine.ActiveHeat==null;
        RecordingLostButton.IsEnabled=editable;
    }
    private void UpdateHeatList(ComboBox box){
        if(!box.Items.OfType<Heat>().Select(h=>(h.Id,h.Name)).SequenceEqual(heats.Select(h=>(h.Id,h.Name))))box.ItemsSource=heats;
        var target=box.Items.OfType<Heat>().FirstOrDefault(h=>h.Id==selectedHeat);if(!ReferenceEquals(box.SelectedItem,target))box.SelectedItem=target;
    }
    private ResultRow[] Rows(IEnumerable<RaceRun> source)
    {
        var data = source.ToArray();
        return data.Select(r =>
        {
            long? score = r.ElapsedUs.HasValue ? r.Profile?.Round(r.ElapsedUs.Value) ?? r.ElapsedUs.Value : null;
            string rank = RaceEngine.RankOf(r, data)?.ToString(CultureInfo.InvariantCulture) ?? "—";
            return new ResultRow(r.Id, r.HeatId == null ? "旧版估算" : heats.First(h => h.Id == r.HeatId).Name, rank, r.Athlete.Bib, r.Athlete.Name,
                StateName(r.State), r.ElapsedUs.HasValue ? r.Profile?.Format(r.ElapsedUs.Value) ?? (r.ElapsedUs.Value / 1_000_000m).ToString("F2") : "—",
                r.Revision, r.Quality, r.Reviews.Count.ToString(), score ?? long.MaxValue,
                VideoTime(r), r.BackupElapsedUs.HasValue?(r.BackupElapsedUs.Value/1_000_000m).ToString("F4"):"—", DeltaTime(r),
                r.OfficialSource switch { "camera"=>"视频主成绩", "photocell-backup"=>"裁判确认备份", _=>r.TimingMethod=="timy-device"?"历史设备成绩":"待核验" });
        }).ToArray();
    }
    private VisionEvent? VisionOf(RaceRun r)=>heats.SelectMany(h=>h.VisionEvents).FirstOrDefault(v=>v.Id==r.VisionEventId);
    private string VideoTime(RaceRun r)=>VisionOf(r)?.EstimatedElapsedUs is long us?(us/1_000_000m).ToString("F4"):"—";
    private string DeltaTime(RaceRun r)=>VisionOf(r)?.EstimatedElapsedUs is long us&&r.BackupElapsedUs is long b?((us-b)/1_000_000m).ToString("+0.0000;-0.0000;0.0000"):"—";
    private void VisionSelected(object s,SelectionChangedEventArgs e){var v=Current?.VisionEvents.FirstOrDefault(v=>v.Id==(VisionGrid.SelectedItem as VisionRow)?.Id);VisionDetails.Text=v==null?"":$"事件 {v.Id} · 帧时钟 {v.Submission.Clock?.Source??"无采集时钟"} · 截图/掩膜接收={v.EvidenceReceived} · 原片接收={v.OriginalReceived} · 帧核验={v.FileVerificationState} · {v.VerificationNote} · {string.Join("；",v.Reasons)}";if(!refreshing)UpdateActionButtons();}
    private string ChosenVision()=>(VisionGrid.SelectedItem as VisionRow)?.Id??throw new DomainException("请选择视频候选");
    private string ChosenRunner()=>(VisionRunner.SelectedItem as ResultRow)?.Id??throw new DomainException("请选择选手号码");
    private void BindVision(object s,RoutedEventArgs e)=>Act(()=>{var h=RequireHeat();hub.AssignVision(h.Id,ChosenVision(),ChosenRunner(),h.Version,VisionNote.Text);});
    private void UndoVision(object s,RoutedEventArgs e)=>Act(()=>{var h=RequireHeat();hub.UnassignVision(h.Id,ChosenVision(),h.Version,VisionNote.Text);});
    private void IgnoreVision(object s,RoutedEventArgs e)=>Act(()=>{var h=RequireHeat();hub.UnassignVision(h.Id,ChosenVision(),h.Version,VisionNote.Text,true);});
    private void UseBackup(object s,RoutedEventArgs e)=>Act(()=>{var h=RequireHeat();hub.SelectOfficial(h.Id,ChosenRunner(),h.Version,"photocell-backup",VisionNote.Text);});
    private void UseCamera(object s,RoutedEventArgs e)=>Act(()=>{var h=RequireHeat();hub.SelectOfficial(h.Id,ChosenRunner(),h.Version,"camera",VisionNote.Text);});
    private void ImportValidation(object s,RoutedEventArgs e){var dialog=new OpenFileDialog{Filter="已审核配置 JSON|*.json"};if(dialog.ShowDialog()==true)Act(()=>hub.ImportValidation(dialog.FileName));}
    private void ExportValidationDraft(object s,RoutedEventArgs e)=>Act(()=>{
        var v=RequireHeat().VisionEvents.Single(x=>x.Id==ChosenVision());
        var draft=new CameraValidationProfile{RigFingerprint=hub.Engine.RigFingerprint,CaptureFingerprint=v.Submission.CaptureFingerprint,
            ModelVersion=v.Submission.ModelVersion,ClockSource=v.Submission.Clock?.Source??"无采集时钟",SettingsJson=JsonSerializer.Serialize(v.Submission.Settings,Wire.Json),
            ReferenceDescription="待填写独立参考测量方法；本草稿未经验证，不能启用自动确认"};
        var dialog=new SaveFileDialog{Filter="配置草稿 JSON|*.json",FileName="camera-validation-DRAFT.json"};
        if(dialog.ShowDialog()==true)File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(draft,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
    });
    private static string TimeOfDay(long us) => TimeSpan.FromTicks(us * 10).ToString(@"hh\:mm\:ss\.ffff", CultureInfo.InvariantCulture);
    private static string HeatNameOf(HeatState s) => s switch { HeatState.Preparing => "组次已建", HeatState.Ready => "已有录像", HeatState.Armed => "待发令", HeatState.Running => "正在比赛", HeatState.Pending => "待核查", _ => "已结束" };
    private static string StateName(RunState s) => s switch { RunState.Preparing or RunState.Ready => "待起跑", RunState.Running => "在赛", RunState.Confirmed => "已完成", RunState.Dns => "DNS", RunState.Dnf => "DNF", RunState.Dsq => "DSQ", RunState.Pending => "待处理", RunState.Cancelled => "已取消", _ => s.ToString() };
    private Heat RequireHeat() => Current ?? throw new DomainException("请选择组次");
    private string RequireMember() => (MemberGrid.SelectedItem as ResultRow)?.Id ?? throw new DomainException("请在本组人员中选择选手");
    private string RequireFinish() => (FinishGrid.SelectedItem as FinishRow)?.Id ?? throw new DomainException("请选择终点事件");
    private void Act(Action action) { try { action(); MessageLabel.Text = "已保存"; Refresh(); } catch (Exception ex) { MessageLabel.Text = ex.Message; } }
    private void AddAthlete(object s, RoutedEventArgs e) => Act(() => { hub.AddAthlete(BibInput.Text, NameInput.Text, TeamInput.Text); NameInput.Clear(); if (int.TryParse(BibInput.Text, out int bib)) BibInput.Text = (bib + 1).ToString(); });
    private void PrepareGroup(object s, RoutedEventArgs e) => Act(() => { selectedHeat = hub.PrepareHeat(picks.Where(p => p.Selected).Select(p => p.Athlete.Id).ToArray(), HeatName.Text,
        new TimingProfile(((PrecisionChoice)PrecisionList.SelectedItem).Unit, (RoundingMode)RoundingList.SelectedItem, ProfileConfirmed.IsChecked == true)).Id; });
    private void HeatSelected(object s, SelectionChangedEventArgs e) { if (!refreshing && HeatList.SelectedItem is Heat h) { selectedHeat = h.Id; Refresh(); } }
    private void VisionHeatSelected(object s,SelectionChangedEventArgs e){if(!refreshing&&VisionHeatList.SelectedItem is Heat h){selectedHeat=h.Id;Refresh();}}
    private void ArmGroup(object s, RoutedEventArgs e) => Act(() => hub.ArmHeat(RequireHeat().Id));
    private void CloseGroup(object s, RoutedEventArgs e) => Act(() => {
        var h=RequireHeat();if(h.State==HeatState.Closed){MessageLabel.Text="本组已经结束";return;}
        int athletes=runs.Count(r=>r.HeatId==h.Id&&!r.IsTerminal),optical=h.Finishes.Count(f=>!f.Ignored&&f.RunId==null),visual=h.VisionEvents.Count(v=>!v.Ignored&&v.RunId==null);
        if(athletes==0&&optical==0&&visual==0&&h.RecordingStopped){hub.CloseHeat(h.Id,h.Version);return;}
        string summary=$"本组还有待处理选手 {athletes} 人、光电事件 {optical} 条、视频候选 {visual} 条。\n\n确认结束后，这些记录全部保留待复核，不自动匹配号码、不生成有效成绩；已有成绩与录像保留，可以开始下一组。录像若仍在进行，将请求手机停止并保留原片。\n\n是否结束本组并保留待复核？";
        if(MessageBox.Show(this,summary,"结束本组并保留待复核",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        hub.CloseHeatForReview(h.Id,h.Version,string.IsNullOrWhiteSpace(StatusNote.Text)?"裁判确认结束，未处理记录保留待复核":StatusNote.Text);
    });
    private void CancelGroup(object s, RoutedEventArgs e) => Act(() =>
    {
        var h = RequireHeat(); if (h.Start != null) throw new DomainException("已发令，请逐人记录 DNF 等状态");
        if (string.IsNullOrWhiteSpace(StatusNote.Text)) throw new DomainException("请填写取消原因");
        foreach (var r in runs.Where(r => r.HeatId == h.Id && !r.IsTerminal)) hub.SetStatus(r.Id, RunState.Cancelled, StatusNote.Text);
        var fresh = JsonSerializer.SerializeToElement(hub.Snapshot(), Wire.Json).GetProperty("heats").Deserialize<List<Heat>>(Wire.Json)!.First(x => x.Id == h.Id);
        hub.DeclareRecordingLost(h.Id, fresh.Version, StatusNote.Text);
    });
    private void AssignEvent(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.AssignFinish(h.Id, RequireFinish(), RequireMember(), h.Version, StatusNote.Text); });
    private void UnassignEvent(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.UnassignFinish(h.Id, RequireFinish(), h.Version, StatusNote.Text); });
    private void IgnoreEvent(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); string id = RequireFinish(); hub.IgnoreFinish(h.Id, id, h.Version, !h.Finishes.First(f => f.Id == id).Ignored, StatusNote.Text); });
    private void ReviewEvent(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.ReviewEvent(h.Id, RequireFinish(), h.Version, MidnightCheck.IsChecked == true, StatusNote.Text); });
    private void SwapRuns(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); var selected = MemberGrid.SelectedItems.Cast<ResultRow>().ToArray(); if (selected.Length != 2) throw new DomainException("按住 Ctrl 选择两个已匹配选手"); hub.SwapFinishes(h.Id, selected[0].Id, selected[1].Id, h.Version, StatusNote.Text); });
    private void DnsRun(object s, RoutedEventArgs e) => Status(RunState.Dns);
    private void DnfRun(object s, RoutedEventArgs e) => Status(RunState.Dnf);
    private void DsqRun(object s, RoutedEventArgs e) => Status(RunState.Dsq);
    private void Status(RunState state) => Act(() => hub.SetStatus(RequireMember(), state, StatusNote.Text));
    private void RestoreRun(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.RestoreRunner(h.Id, RequireMember(), h.Version, StatusNote.Text); });
    private void ReopenGroup(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.ReopenHeat(h.Id, h.Version, StatusNote.Text); });
    private void RecordingLost(object s, RoutedEventArgs e) => Act(() => { var h = RequireHeat(); hub.DeclareRecordingLost(h.Id, h.Version, StatusNote.Text); });
    private void SimulateStart(object s, RoutedEventArgs e) => Act(() => hub.Inject($"{++simulatedNumber:0000} C0 {DateTime.Now:HH:mm:ss.ffff} 00"));
    private void SimulateFinish(object s, RoutedEventArgs e) => Act(() => hub.Inject($"{++simulatedNumber:0000} C1 {DateTime.Now:HH:mm:ss.ffff} 00"));
    private void Export(object s, RoutedEventArgs e) { var dialog = new SaveFileDialog { FileName = "Timy3多人计时.csv", Filter = "CSV|*.csv" }; if (dialog.ShowDialog() == true) Act(() => hub.ExportCsv(dialog.FileName)); }
    private void OpenData(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(hub.DataDirectory) { UseShellExecute = true });
    private void ResultSelected(object s, SelectionChangedEventArgs e)
    {
        var r = runs.FirstOrDefault(r => r.Id == (ResultsGrid.SelectedItem as ResultRow)?.Id);
        RevisionDetails.Text = r == null ? "" : string.Join("\n", r.Revisions.Select(v => $"成绩 v{v.Revision} · {v.CreatedAt.LocalDateTime:g} · {v.ElapsedUs?.ToString() ?? "—"} μs · {v.Note}")) + $"\n独立录像判读版本：{r.Reviews.Count}；原始起终点、绑定与修改均保留。";
    }
    private void AddressChanged(object s, SelectionChangedEventArgs e)
    {
        if (PairText == null || AddressList.SelectedItem == null) return;
        string payload = $"http://{AddressList.SelectedItem}:{hub.Port}/setup#secret={hub.PairSecret}"; PairText.Text = payload;
        using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data); using var stream = new MemoryStream(qr.GetGraphic(8));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); PairQr.Source = image;
    }
    private sealed class AthletePick(Athlete athlete) { public Athlete Athlete { get; } = athlete; public bool Selected { get; set; } }
    private sealed record PrecisionChoice(string Name, long Unit);
    private sealed record ResultRow(string Id, string Heat, string Rank, string Bib, string Name, string Status, string Time, int Revision, string Quality, string Reviews, long SortUs, string Video, string Backup, string Delta, string Source) { public string Label => Bib+" · "+Name; }
    private sealed record VisionRow(string Id,string Track,string Bib,string Time,string Status,int Revision);
    private sealed record FinishRow(string Id, int Number, string DeviceTime, string Status);
}
