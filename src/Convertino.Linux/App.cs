using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Convertino.Editors;
using Convertino.Native;
using Convertino.Overlay;
using Convertino.Services;
using Convertino.Ui;

namespace Convertino;

public sealed class App : Application
{
    public static string[] StartupMenu = [];
    X11Input? _input;
    OverlayController? _overlay;
    TrayIcon? _tray;
    TrayPanel? _panel;
    Socket? _server;

    public override void Initialize()
    {
        // platform integrations read the application name from here (macOS showed "Avalonia" without it)
        Name = "Convertino";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => { _input?.Dispose(); _server?.Dispose(); try { File.Delete(Ipc.SocketPath); } catch { } };
        }

        Settings.Load();
        Skin.Apply(Settings.Current.Theme);
        RequestedThemeVariant = Skin.Current.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        var platform = (LinuxPlatform)Platform.Current;
        platform.Ui = new LinuxJobUi();
        LinuxPlatform.Editors = EditorHost.ById;
        Images.WarmUp();

        _input = new X11Input(a => Dispatcher.UIThread.Post(a));
        bool x11 = _input.Start();
        if (!x11) { _input = null; Log.Info("no X display — drag+Shift disabled, menu mode only"); }
        Action<Window>? unmanaged = null;
        if (_input != null)
        {
            var display = _input.Display;
            // toasts and the panel: no window-manager frame or focus, exact position, above everything
            unmanaged = w => { if (w.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero) X11.OverrideRedirect(display, h); };
            Toasts.Prepare = unmanaged;
        }
        _overlay = new OverlayController(_input);
        LinuxPlatform.ClipboardHost = _overlay.Host;
        Toasts.ScreensHost = _overlay.Host;
        bool selftest = Environment.GetEnvironmentVariable("CONVERTINO_EDIT_SELFTEST") is { Length: > 0 };
        if (!selftest)
        {
            var input = _input;
            _panel = new TrayPanel { Pointer = input != null ? () => input.Pointer : null };
            unmanaged?.Invoke(_panel);
            if (input != null)
            {
                // it never has focus (override-redirect), so a click outside and Esc are seen globally
                input.ButtonDown += p => _panel.ClickAt(p);
                input.Escape += () => _panel.HideAnimated();
            }
            BuildTray();
            platform.SetAutostart(Settings.Current.LaunchAtStartup);
            StartIpc();
        }
        Log.Info($"Convertino {Cli.Version} (Linux) started; session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} x11={x11}");

        if (Environment.GetEnvironmentVariable("CONVERTINO_EDIT_SELFTEST") is { Length: > 0 } st)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var r = await EditorHost.DevSaveAll(st);
                Log.Info("DEV editors: " + string.Join(" | ", r));
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            });
            base.OnFrameworkInitializationCompleted();
            return;
        }
        if (Environment.GetEnvironmentVariable("CONVERTINO_PIN") is { } pin && pin.Split(',') is [var px, var py])
            Dispatcher.UIThread.Post(() => _overlay.Pin(new ScreenPoint(int.Parse(px), int.Parse(py))), DispatcherPriority.Background);
        // CONVERTINO_PANEL=1 opens the tray panel at start, =demo with two weeks of made-up activity (screenshots)
        if (Environment.GetEnvironmentVariable("CONVERTINO_PANEL") is { Length: > 0 } panel)
            Dispatcher.UIThread.Post(() => _panel?.Open(panel == "demo" ? Stats.Demo() : null), DispatcherPriority.Background);
        if (StartupMenu.Length > 0) Dispatcher.UIThread.Post(() => _overlay.Menu(StartupMenu), DispatcherPriority.Background);
        else if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Current.Save();
            bool wayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";
            var key = Settings.Current.TriggerKey;
            var t = Toasts.Create(new ScreenPoint(0, 0));
            t.SetDone("Convertino is running",
                wayland ? $"Wayland: drag + {key.KeyName()} works over X11 apps; right-click files → Convertino works everywhere."
                        : $"Drag any file and {key.Tap()} — the flower opens under your cursor.",
                AppIcon.Render(84), null);
            Toasts.Show(t);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // ---------- tray ----------

    void BuildTray()
    {
        _tray?.Dispose();
        var s = Settings.Current;
        var menu = new NativeMenu();
        NativeMenuItem Check(string header, bool value, Action<bool> set)
        {
            var it = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };
            it.Click += (_, _) => { it.IsChecked = !it.IsChecked; set(it.IsChecked); s.Save(); };
            return it;
        }
        menu.Items.Add(new NativeMenuItem($"Convertino — drag a file, {s.TriggerKey.Tap()}") { IsEnabled = false });
        var open = new NativeMenuItem("Open panel");
        open.Click += (_, _) => _panel?.Open();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Check("Enabled", s.Enabled, v => s.Enabled = v));
        var themes = new NativeMenu();
        foreach (var th in Skin.All)
        {
            var it = new NativeMenuItem(th.Name) { ToggleType = MenuItemToggleType.Radio, IsChecked = th.Name == Skin.Current.Name };
            it.Click += (_, _) => { s.Theme = th.Name; s.Save(); Skin.Apply(th.Name); BuildTray(); };
            themes.Items.Add(it);
        }
        menu.Items.Add(new NativeMenuItem("Theme") { Menu = themes });
        var keys = new NativeMenu();
        foreach (var k in TriggerKeys.All)
        {
            var it = new NativeMenuItem(k.Label()) { ToggleType = MenuItemToggleType.Radio, IsChecked = k == s.TriggerKey };
            it.Click += (_, _) => { s.TriggerKey = k; s.Save(); BuildTray(); };
            keys.Items.Add(it);
        }
        menu.Items.Add(new NativeMenuItem("Open with") { Menu = keys });
        menu.Items.Add(Check("Notifications", s.Notifications, v => s.Notifications = v));
        menu.Items.Add(Check("Start at login", s.LaunchAtStartup, v => { s.LaunchAtStartup = v; Platform.Current.SetAutostart(v); }));
        var recent = s.Recent.Where(r => File.Exists(r.Path) || Directory.Exists(r.Path)).Take(5).ToList();
        if (recent.Count > 0)
        {
            var rm = new NativeMenu();
            foreach (var r in recent)
            {
                var it = new NativeMenuItem(Path.GetFileName(r.Path));
                it.Click += (_, _) => Platform.Current.Reveal(r.Path);
                rm.Items.Add(it);
            }
            menu.Items.Add(new NativeMenuItem("Recent") { Menu = rm });
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        var log = new NativeMenuItem("Open log");
        log.Click += (_, _) => Platform.Current.Open(Paths.LogFile);
        menu.Items.Add(log);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Items.Add(quit);

        using var ms = new MemoryStream();
        AppIcon.Render(64).Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        _tray = new TrayIcon { Icon = new WindowIcon(ms), ToolTipText = $"Convertino — drag a file, {s.TriggerKey.Tap()}", Menu = menu, IsVisible = true };
        // a left click opens the panel where the desktop reports it (KDE, XFCE, GNOME's AppIndicator extension …)
        _tray.Clicked += (_, _) => _panel?.Toggle();
        TrayIcon.SetIcons(this, [_tray]);
    }

    // ---------- single instance ----------

    void StartIpc()
    {
        try
        {
            if (File.Exists(Ipc.SocketPath)) File.Delete(Ipc.SocketPath);
            _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _server.Bind(new UnixDomainSocketEndPoint(Ipc.SocketPath));
            _server.Listen(4);
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        // async on purpose: on macOS, Dispose() never returns while another thread sits in a blocking Accept(),
                        // so Quit froze the app; a pending AcceptAsync is cancelled instead
                        using var c = _server.AcceptAsync().GetAwaiter().GetResult();
                        using var ns = new NetworkStream(c);
                        using var r = new StreamReader(ns, Encoding.UTF8);
                        var cmd = r.ReadLine();
                        var files = new List<string>();
                        string? line;
                        while (!string.IsNullOrEmpty(line = r.ReadLine())) files.Add(line);
                        if (cmd == "menu") Dispatcher.UIThread.Post(() => _overlay?.Menu(files));
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { return; }
                    catch (Exception ex) { Log.Error("ipc", ex); }
                }
            }) { IsBackground = true, Name = "Convertino IPC" }.Start();
        }
        catch (Exception ex) { Log.Error("ipc listen", ex); }
    }
}

sealed class LinuxJobUi : IJobUi
{
    public void Post(Action a) => Dispatcher.UIThread.Post(a);

    public IJobView Show(PetalAction action, IReadOnlyList<string> files, ScreenPoint where)
    {
        var t = Toasts.Create(where);
        t.SetRunning(action.Verb + "…", files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files", action.Icon);
        Toasts.Show(t);
        return new View(t);
    }

    sealed class View(ToastWindow t) : IJobView
    {
        public Action? OnCancel { set => t.OnCancel = value; }
        public void Progress(double p) => t.SetProgress(p);
        public void Dismiss() => t.Dismiss();

        public async void Done(string title, string summary, IReadOnlyList<string> outputs, string? icon)
        {
            t.Outputs.AddRange(outputs);
            Bitmap? thumb = null;
            if (outputs.Count > 0)
            {
                var o = outputs[0];
                thumb = await Task.Run(() => LinuxPlatform.Thumbnail(o, 42));
            }
            t.SetDone(title, summary, thumb, icon);
        }

        public void Failed(string title, string message, string? icon) => t.SetDone(title, message, null, icon, error: true);
    }
}
