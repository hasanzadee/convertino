using Avalonia;

namespace Convertino;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Platform.Current = new LinuxPlatform();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        if (Cli.IsCommand(args))
        {
            Settings.Load();
            return Cli.Run(args).GetAwaiter().GetResult();
        }

        if (args is ["menu", "--clipboard" or "--capture"])
        {
            // the shortcuts' actions, for desktops where only the desktop itself can bind keys (Wayland)
            var verb = args[1] == "--clipboard" ? "clip" : "capture";
            if (Ipc.Send(verb, [])) return 0;
            App.StartupVerb = verb;
        }
        else if (args.Length > 0 && args[0] == "menu")
        {
            var files = args.Skip(1).Select(Path.GetFullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (files.Length == 0) { Console.Error.WriteLine("usage: convertino menu <files…>"); return 2; }
            if (Ipc.Send("menu", files)) return 0;
            App.StartupMenu = files;
        }
        else if (Environment.GetEnvironmentVariable("CONVERTINO_EDIT_SELFTEST") == null && Ipc.Send("ping", []))
        {
            Console.WriteLine("Convertino is already running (look for the flower in your tray).");
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            Console.Error.WriteLine("Convertino couldn't start a desktop session: " + ex.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new X11PlatformOptions { WmClass = "convertino" })
        .LogToTrace();
}
