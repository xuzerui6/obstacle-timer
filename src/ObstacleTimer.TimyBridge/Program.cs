using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ObstacleTimer.TimyBridge
{
    internal static class Program
    {
        private static readonly object OutputLock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly string Connection = Guid.NewGuid().ToString("N");
        private static long NowUs() { return (long)(Stopwatch.GetTimestamp() * (1000000d / Stopwatch.Frequency)); }
        internal static void Emit(string type, object data)
        {
            lock (OutputLock) { Console.WriteLine(Json.Serialize(new { type, data })); Console.Out.Flush(); }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            if (args.Length > 0 && args[0] == "--probe")
                AppDomain.CurrentDomain.FirstChanceException += delegate(object s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
                { Console.Error.WriteLine(e.Exception.GetType().Name + ": " + e.Exception.Message); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { return Run(args); }
            catch (Exception ex) { Emit("error", new { message = ex.ToString() }); return 1; }
        }
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            if (!args.Name.StartsWith("AlgeTimyUsb", StringComparison.OrdinalIgnoreCase)) return null;
            return Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AlgeTimyUsb.x64.dll"));
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            using (var window = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1, Height = 1 })
            {
                window.Load += delegate
                {
                    Process parent = null;
                    if (args.Length == 2 && args[0] == "--parent-pid")
                    {
                        int parentId;
                        if (!int.TryParse(args[1], out parentId)) { window.Close(); return; }
                        try { parent = Process.GetProcessById(parentId); parent.EnableRaisingEvents = true; }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                        { if (parent != null) parent.Dispose(); window.Close(); return; }
                        // Hold this process handle so PID reuse cannot keep an orphan alive.
                        var parentWatch = new Timer { Interval = 1000 };
                        parentWatch.Tick += delegate { if (parent.HasExited) window.Close(); };
                        window.FormClosed += delegate { parentWatch.Stop(); parentWatch.Dispose(); parent.Dispose(); };
                        parentWatch.Start();
                    }
                    var usb = new Alge.TimyUsb(window);
                    usb.Error += delegate(object s, Alge.ErrorEventArgs e) { Emit("error", new { message = e.ErrorMessage }); };
                    usb.DeviceConnected += delegate(object s, Alge.DeviceChangedEventArgs e)
                    { Emit("connected", new { deviceId = e.Device.Id.ToString(), connectionId = Connection, count = usb.ConnectedDevicesCount }); };
                    usb.DeviceDisconnected += delegate(object s, Alge.DeviceChangedEventArgs e)
                    { Emit("disconnected", new { deviceId = e.Device.Id.ToString(), connectionId = Connection, count = usb.ConnectedDevicesCount }); };
                    usb.LineReceived += delegate(object s, Alge.DataReceivedEventArgs e)
                    {
                        long received = NowUs();
                        Emit("line", new { deviceId = e.Device.Id.ToString(), connectionId = Connection, raw = e.Data, hostUs = received });
                    };
                    usb.HeartbeatReceived += delegate { Emit("heartbeat", new { hostUs = NowUs(), count = usb.ConnectedDevicesCount }); };
                    window.FormClosing += delegate { usb.Stop(); };
                    usb.Start();
                    Emit("bridgeReady", new { connectionId = Connection, hostUs = NowUs(), count = usb.ConnectedDevicesCount, attached = usb.AttachedDevicesCount });
                    var heartbeat = new Timer { Interval = 1000 };
                    heartbeat.Tick += delegate { Emit("status", new { count = usb.ConnectedDevicesCount, hostUs = NowUs() }); };
                    heartbeat.Start();
                    if (args.Length == 2 && args[0] == "--probe")
                    {
                        int seconds;
                        if (!int.TryParse(args[1], out seconds)) seconds = 10;
                        var end = new Timer { Interval = Math.Max(1, seconds) * 1000 };
                        end.Tick += delegate { end.Stop(); window.Close(); };
                        end.Start();
                    }
                    // No Send(), configuration commands, memory clearing or firmware updates.
                };
                Application.Run(window);
            }
            return 0;
        }
    }
}
