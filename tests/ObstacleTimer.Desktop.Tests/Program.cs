using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ObstacleTimer.Core;
using ObstacleTimer.Desktop;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);checks++;}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    [STAThread]
    private static int Main(){
        var data=Path.Combine(Path.GetTempPath(),"ObstacleTimer-native-"+Guid.NewGuid().ToString("N"));
        var output=Environment.GetEnvironmentVariable("E2E_OUTPUT")??Path.Combine(data,"screenshots");Directory.CreateDirectory(output);
        var app=new App();app.InitializeComponent();
        // No server, bridge, camera, or real competition database is opened.
        var hub=new TimingHub(data,38000,true);
        var e=hub.Engine;e.OnlinePhone="synthetic-phone";e.TimyOnline=true;
        var a=hub.AddAthlete("1","回归选手甲","测试");var b=hub.AddAthlete("2","回归选手乙","测试");
        var h=hub.PrepareHeat([a.Id,b.Id],"现场回归",new(10000,RoundingMode.Cut,true));hub.ArmHeat(h.Id);
        hub.Inject("0001 C0 12:00:00.00 00");hub.Inject("0001 C1 12:00:01.00 00");
        var window=new MainWindow(hub){Left=-10000,Top=-10000,ShowInTaskbar=false};window.Show();Pump();
        T Find<T>(string name)where T:FrameworkElement=>(T)window.FindName(name);
        var refresh=typeof(MainWindow).GetMethod("Refresh",BindingFlags.NonPublic|BindingFlags.Instance)!;
        void Refresh(){refresh.Invoke(window,null);Pump();}
        try{
            var members=Find<DataGrid>("MemberGrid");var finishes=Find<DataGrid>("FinishGrid");var heats=Find<ComboBox>("HeatList");
            members.SelectedIndex=1;finishes.SelectedIndex=0;var member=members.SelectedItem;var finish=finishes.SelectedItem;var heat=heats.SelectedItem;
            var note=Find<TextBox>("StatusNote");note.Text="正在输入的裁判原因";note.CaretIndex=4;
            for(int i=0;i<400;i++)Refresh();
            Check(ReferenceEquals(members.SelectedItem,member)&&ReferenceEquals(finishes.SelectedItem,finish)&&ReferenceEquals(heats.SelectedItem,heat),"400 heartbeat refreshes preserve selection and list item identities");
            Check(note.Text=="正在输入的裁判原因"&&note.CaretIndex==4,"heartbeat never resets typed judge reason or caret");
            Check(Find<Button>("AssignFinishButton").IsEnabled&&!Find<Button>("UndoFinishButton").IsEnabled,"finish actions reflect unbound selection");
            var run=e.Members(h).Last();hub.AssignFinish(h.Id,h.Finishes[0].Id,run.Id,h.Version,"核对号码");Refresh();
            Check(!Find<Button>("AssignFinishButton").IsEnabled&&Find<Button>("UndoFinishButton").IsEnabled,"binding changes atomically update action availability");
            hub.SetStatus(run.Id,RunState.Dnf,"明确未完成");Refresh();
            Check(!Find<Button>("AssignFinishButton").IsEnabled&&Find<Button>("RestoreButton").IsEnabled,"DNF cannot be rebound without explicit restoration");
            var tabs=Descendants<TabControl>(window).Single();tabs.SelectedIndex=1;Pump();window.UpdateLayout();Pump();
            Check(Find<TextBlock>("VisionEmptyState").Visibility==Visibility.Visible,"empty video candidate list has a visible actionable explanation");
            var bmp=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var file=File.Create(Path.Combine(output,"windows-v050.png")))png.Save(file);
            hub.CloseHeatForReview(h.Id,h.Version,"明确保留待复核");Refresh();
            Check(!Find<Button>("CloseGroupButton").IsEnabled&&!Find<Button>("BindVisionButton").IsEnabled&&Find<Button>("ReopenButton").IsEnabled,"closed group disables timing changes and enables explicit reopening");
            Check(Find<TextBlock>("VisionPipelineStatus").Text.Contains("等待保存确认"),"stop request is visibly distinct from phone save acknowledgment");
            Check(window.Title.Contains(Wire.AppVersion),"Windows title uses shared release version");
            Console.WriteLine($"{checks} native Windows UI checks passed; simulation only.");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{window.Close();hub.DisposeAsync().AsTask().GetAwaiter().GetResult();app.Shutdown();}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
}
