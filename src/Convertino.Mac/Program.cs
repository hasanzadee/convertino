using Avalonia;

namespace Convertino;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        MacPlatform.ExtendPath();
        Platform.Current = new MacPlatform();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        if (Cli.IsCommand(args))
        {
            Settings.Load();
            return Cli.Run(args).GetAwaiter().GetResult();
        }

        if (args.Length == 2 && args[0] == "--browser-drop-selftest")
            return Native.MacDrag.SelfTest(Path.GetFullPath(args[1]));

        if (args.Length > 0 && args[0] == "menu")
        {
            var files = args.Skip(1).Select(Path.GetFullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (files.Length == 0) { Console.Error.WriteLine("usage: convertino menu <files…>"); return 2; }
            if (Ipc.Send("menu", files) || (StartBundle() && Retry(() => Ipc.Send("menu", files)))) return 0;
            App.StartupMenu = files;
        }
        else if (Environment.GetEnvironmentVariable("CONVERTINO_EDIT_SELFTEST") == null && Ipc.Send("ping", []))
        {
            Console.WriteLine("Convertino is already running (look for the flower in the menu bar).");
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            Console.Error.WriteLine("Convertino couldn't start: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// "convertino menu" from Finder's Quick Action or a terminal: start the installed app the normal way (so it isn't
    /// tied to this short-lived process) instead of running the whole UI here.
    /// </summary>
    static bool StartBundle()
    {
        var exe = Environment.ProcessPath ?? "";
        int i = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        if (i < 0) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(exe[..(i + 4)]);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch (Exception ex) { Log.Error("open bundle", ex); return false; }
    }

    static bool Retry(Func<bool> send)
    {
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            if (send()) return true;
        }
        return false;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        // a menu-bar app: no Dock icon, no app menu (Info.plist also sets LSUIElement for the bundle)
        .With(new MacOSPlatformOptions { ShowInDock = false, DisableDefaultApplicationMenuItems = true })
        .LogToTrace();
}
