using System.IO;
using System.Windows;

namespace ObstacleTimer.Desktop;

public partial class App : Application
{
    private TimingHub? hub;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObstacleTimer");
            int port = 8765;
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i] == "--data") data = e.Args[++i];
                else if (e.Args[i] == "--port") port = int.Parse(e.Args[++i]);
            }
            hub = new TimingHub(data, port, e.Args.Contains("--simulate"));
            await hub.StartAsync();
            if (e.Args.Contains("--headless"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Console.InputEncoding = new System.Text.UTF8Encoding(false);
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                File.WriteAllText(Path.Combine(data, "headless-pair.json"), System.Text.Json.JsonSerializer.Serialize(new
                { server = $"http://127.0.0.1:{port}", https = $"https://127.0.0.1:{hub.HttpsPort}", secret = hub.PairSecret, sessionId = hub.Engine.SessionId }));
                if (hub.Simulation) _ = Task.Run(() => HeadlessCommands.Run(hub, () => Dispatcher.BeginInvoke(() => Shutdown())));
            }
            else
            {
                MainWindow = new MainWindow(hub); MainWindow.Show();
                int captureIndex = Array.IndexOf(e.Args, "--screenshot");
                if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
                {
                    var captureTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    captureTimer.Tick += (_, _) =>
                    {
                        captureTimer.Stop(); MainWindow.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)MainWindow.ActualWidth, (int)MainWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(MainWindow);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using (var output = File.Create(e.Args[captureIndex + 1])) encoder.Save(output);
                        MainWindow.Close();
                    };
                    captureTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "计时软件启动失败", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (hub != null) Task.Run(async () => await hub.DisposeAsync()).GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
