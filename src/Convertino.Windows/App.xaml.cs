using System.Windows;
using System.Windows.Threading;
using Convertino.Input;
using Convertino.Native;
using Convertino.Overlay;
using Convertino.Services;
using Convertino.Ui;

namespace Convertino;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _wake;
    GlobalInput? _input;
    OverlayController? _overlay;
    TrayIcon? _tray;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Platform.Current = new WinPlatform();

        if (Cli.IsCommand(e.Args))
        {
            // print into the terminal that launched us (we are a GUI-subsystem exe)
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Settings.Load();
            Console.WriteLine();
            int code = await Cli.Run(e.Args);
            Shutdown(code);
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--write-pngs")
        {
            System.IO.Directory.CreateDirectory(e.Args[1]);
            foreach (var px in new[] { 64, 128, 256, 512 })
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(e.Args[1], $"convertino-{px}.png"), AppIcon.Png(px));
            // a soft "Sonoma" wallpaper for demo recordings
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawDrawing(Convertino.Editors.BackgroundEditor.Backdrops[0].Paint(new Rect(0, 0, 1600, 1000), null));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1600, 1000, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(e.Args[1], "wallpaper.png"))) enc.Save(fs);
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--write-icon")
        {
            AppIcon.WriteIco(e.Args[1]);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            Settings.Load();
            Theme.Apply(e.Args.Length > 2 ? e.Args[2] : "Mocha");
            Dev.Snapshot(e.Args[1], e.Args.Length > 3 ? e.Args[3] : "img", e.Args.Length > 4 ? int.Parse(e.Args[4]) : 2);
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--panel")
        {
            Settings.Load();
            Theme.Apply(e.Args.Length > 2 ? e.Args[2] : Settings.Current.Theme);
            Flyout.Snapshot(e.Args[1], e.Args.Length > 3 && e.Args[3] == "demo" ? Stats.Demo() : Stats.Load());
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--run")
        {
            Settings.Load();
            Theme.Apply(Settings.Current.Theme);
            Settings.Current.Recent.Clear();
            var id = e.Args[1];
            var files = e.Args.Skip(2).ToArray();
            Dev.Run(id, files).ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--edit-selftest")
        {
            Settings.Load();
            Theme.Apply(Settings.Current.Theme);
            Convertino.Editors.EditorHost.DevSaveAll(System.IO.Path.GetFullPath(e.Args[1])).ContinueWith(t =>
            {
                Log.Info("DEV editors: " + string.Join(" | ", t.Result));
                Dispatcher.BeginInvoke(() => Shutdown());
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--edit")
        {
            Settings.Load();
            Theme.Apply(e.Args.Length > 3 ? e.Args[3] : Settings.Current.Theme);
            var pages = Catalog.For([System.IO.Path.GetFullPath(e.Args[2])]);
            var act = pages.Tools.First(a => a.Id == e.Args[1]);
            var c = Win32.Cursor;
            var (work, _, _) = Win32.MonitorAt(c);
            var mid = new POINT((work.Left + work.Right) / 2, (work.Top + work.Bottom) / 2);
            Jobs.Start(act, [System.IO.Path.GetFullPath(e.Args[2])], new ScreenPoint(mid.X, mid.Y))
                .ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));
            return;
        }

        _mutex = new Mutex(true, @"Local\Convertino.Singleton", out bool first);
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Convertino.Wake");
        string[] menuFiles = e.Args.Length > 0 && e.Args[0] == "menu"
            ? e.Args.Skip(1).Select(System.IO.Path.GetFullPath).Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToArray()
            : [];
        if (!first && menuFiles.Length > 0)
        {
            MenuPipe.Send(menuFiles);
            Shutdown();
            return;
        }
        if (!first)
        {
            _wake.Set(); // already running: just pop the flyout there
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("ui", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("domain", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        Settings.Load();
        Theme.Apply(Settings.Current.Theme);
        Log.Info($"Convertino {Cli.Version} starting");
        Images.WarmUp();

        _input = new GlobalInput(Dispatcher);
        _input.Start();
        _overlay = new OverlayController(_input);
        MenuPipe.Listen(files => Dispatcher.BeginInvoke(() => _overlay?.Menu(files)));
        if (menuFiles.Length > 0) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _overlay.Menu(menuFiles));
        _tray = new TrayIcon();
        _tray.Attach(_input);
        AutoStart.Apply(Settings.Current.LaunchAtStartup);

        var wakeThread = new Thread(() =>
        {
            while (_wake.WaitOne())
                Dispatcher.BeginInvoke(() => _tray?.ShowFlyout());
        }) { IsBackground = true };
        wakeThread.Start();

        if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Current.Save();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var t = Toasts.Create(Win32.Cursor);
                t.SetDone("Convertino is running", $"Drag any file and {Settings.Current.TriggerKey.Tap()} — the flower opens under your cursor.", AppIcon.Render(84), "");
                Toasts.Show(t);
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _input?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Hands "menu" requests from a second instance to the running one.</summary>
static class MenuPipe
{
    static string Name => $"Convertino.Menu.{Environment.UserName}";

    public static void Send(string[] files)
    {
        try
        {
            using var c = new System.IO.Pipes.NamedPipeClientStream(".", Name, System.IO.Pipes.PipeDirection.Out);
            c.Connect(1500);
            using var w = new System.IO.StreamWriter(c);
            foreach (var f in files) w.WriteLine(f);
        }
        catch (Exception ex) { Log.Error("menu pipe send", ex); }
    }

    public static void Listen(Action<List<string>> onMenu) => new Thread(() =>
    {
        while (true)
        {
            try
            {
                using var s = new System.IO.Pipes.NamedPipeServerStream(Name, System.IO.Pipes.PipeDirection.In);
                s.WaitForConnection();
                using var r = new System.IO.StreamReader(s);
                var files = new List<string>();
                string? line;
                while ((line = r.ReadLine()) != null) if (line.Length > 0) files.Add(line);
                if (files.Count > 0) onMenu(files);
            }
            catch (Exception ex) { Log.Error("menu pipe", ex); Thread.Sleep(500); }
        }
    }) { IsBackground = true, Name = "Convertino menu pipe" }.Start();
}
