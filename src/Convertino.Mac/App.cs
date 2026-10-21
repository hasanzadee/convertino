using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    /// <summary>"clip" or "capture" from "convertino menu --clipboard / --capture" when it started the app.</summary>
    public static string? StartupVerb;
    const string InputMonitoringPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent";

    MacInput? _input;
    OverlayController? _overlay;
    ClipShortcuts? _shortcuts;
    TrayIcon? _tray;
    // one menu for the app's lifetime, refilled by BuildTray: Avalonia.Native throws if a live status item gets another
    readonly NativeMenu _menu = new();
    TrayPanel? _panel;
    Socket? _server;

    public override void Initialize()
    {
        // Avalonia renames the process through LaunchServices; without a name Activity Monitor shows "Avalonia"
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
        Skin.Changed += () => RequestedThemeVariant = Skin.Current.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        var platform = (MacPlatform)Platform.Current;
        platform.Ui = new MacJobUi();
        MacPlatform.Editors = EditorHost.ById;
        Images.WarmUp();

        _input = new MacInput(a => Dispatcher.UIThread.Post(a));
        _input.Start();
        _overlay = new OverlayController(_input);
        MacPlatform.ClipboardHost = _overlay.Host;
        Toasts.ScreensHost = _overlay.Host;
        Toasts.Prepare = w => w.Opened += (_, _) => MacWindow.Float(w, MacWindow.StatusLevel);
        bool selftest = Environment.GetEnvironmentVariable("CONVERTINO_EDIT_SELFTEST") is { Length: > 0 };
        if (!selftest)
        {
            // no drag needed: the clipboard's flower (Control+Option+X) and an area capture (Control+Option+A)
            var overlay = _overlay;
            MacInput pointer = _input;
            _shortcuts = new ClipShortcuts(new CarbonHotkeys(), overlay.Open, () => MacPlatform.CaptureArea(() => pointer.Pointer));
        }
        if (!selftest)
        {
            MacInput input = _input;
            _panel = new TrayPanel { Pointer = () => input.Pointer, Thumbnail = MacPlatform.Thumbnail };
            _panel.SettingsChanged += BuildTray;
            // above every app and on every Space, like the toasts
            _panel.Opened += (_, _) =>
            {
                MacWindow.Float(_panel, MacWindow.StatusLevel);
                if (OverlayController.Diag) Log.Info("panel " + MacWindow.Describe(_panel));
            };
            // a press on the menu-bar item toggles the panel itself (below), or opens the menu, which closes it
            input.ButtonDown += p => { if (!StatusItem.Contains(p)) _panel.ClickAt(p); };
            input.Escape += () => _panel.HideAnimated();
            BuildTray();
            // a click on the menu-bar item opens the panel (right-click / Ctrl-click: the menu), like on Windows
            StatusItem.OnClick(() =>
            {
                if (OverlayController.Diag) Log.Info("status item clicked");
                _panel.Toggle();
            });
            _panel.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty) return;
                StatusItem.Highlight(_panel.IsVisible);
                if (OverlayController.Diag) Log.Info($"panel visible={_panel.IsVisible}");
            };
            platform.SetAutostart(Settings.Current.LaunchAtStartup);
            StartIpc();
            Watches.StartAll();
        }
        bool listen = ListenAccess();
        Log.Info($"Convertino {Cli.Version} (macOS {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}) started; input-monitoring={listen}");

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
        {
            // CONVERTINO_PIN_FILE=… blooms the pinned flower for that file (screenshots in CI)
            var pinFile = Environment.GetEnvironmentVariable("CONVERTINO_PIN_FILE");
            Dispatcher.UIThread.Post(() => _overlay.Pin(new ScreenPoint(int.Parse(px), int.Parse(py)),
                pinFile is { Length: > 0 } ? [Path.GetFullPath(pinFile)] : null), DispatcherPriority.Background);
        }
        // CONVERTINO_PANEL=1 opens the menu-bar panel at start, =demo with two weeks of made-up activity (screenshots)
        if (Environment.GetEnvironmentVariable("CONVERTINO_PANEL") is { Length: > 0 } panel)
            Dispatcher.UIThread.Post(() => _panel?.Open(panel == "demo" ? Stats.Demo() : null), DispatcherPriority.Background);
        if (StartupMenu.Length > 0) Dispatcher.UIThread.Post(() => _overlay.Menu(StartupMenu), DispatcherPriority.Background);
        else if (StartupVerb != null) Dispatcher.UIThread.Post(() => Verb(StartupVerb), DispatcherPriority.Background);
        else if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Current.Save();
            WelcomeCard(listen);
        }
        UpdateCards.Start();
        base.OnFrameworkInitializationCompleted();
    }

    // ---------- permissions ----------

    static bool ListenAccess()
    {
        try { return Quartz.CGPreflightListenEventAccess(); }
        catch (Exception ex) { Log.Error("input monitoring check", ex); return true; }
    }

    /// <summary>
    /// Polling the mouse buttons and Shift has worked without any permission so far (macOS 26 in CI), but macOS may
    /// still filter it. When Input Monitoring isn't granted, the one-time welcome card links to that pane
    /// (so does the menu-bar menu, later).
    /// </summary>
    static void WelcomeCard(bool listen)
    {
        var tap = Settings.Current.TriggerKey.Tap();
        if (listen) { Card("Convertino is running", $"Drag a file in Finder, {tap}"); return; }
        var t = Card($"Drag a file, {tap}", "Not working? Allow Input Monitoring", seconds: 12);
        t.OnClick = OpenInputMonitoring;
    }

    static void OpenInputMonitoring()
    {
        try { Quartz.CGRequestListenEventAccess(); } catch (Exception ex) { Log.Error("input monitoring request", ex); }
        Platform.Current.Open(InputMonitoringPane);
    }

    static ToastWindow Card(string title, string text, double? seconds = null)
    {
        var t = Toasts.Create(new ScreenPoint(0, 0));
        t.SetDone(title, text, AppIcon.Render(84), null, seconds: seconds);
        Toasts.Show(t);
        return t;
    }

    // ---------- menu bar ----------

    void BuildTray()
    {
        var s = Settings.Current;
        var menu = _menu;
        menu.Items.Clear();
        NativeMenuItem Check(string header, bool value, Action<bool> set)
        {
            var it = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };
            it.Click += (_, _) => { it.IsChecked = !it.IsChecked; set(it.IsChecked); s.Save(); };
            return it;
        }
        // right-click / Ctrl-click (and VoiceOver, which presses the item) open this menu; the panel stays first
        var open = new NativeMenuItem("Open panel");
        open.Click += (_, _) => _panel?.Open();
        menu.Items.Add(open);
        var clip = new NativeMenuItem("Flower for the clipboard" + (s.ClipboardHotkeyOn ? $"  ({s.ClipboardKey.Label})" : ""));
        clip.Click += (_, _) => _shortcuts?.Clipboard();
        menu.Items.Add(clip);
        // after the menu has closed, or the picker would start under it
        var area = new NativeMenuItem("Capture an area" + (s.CaptureHotkeyOn ? $"  ({s.CaptureKey.Label})" : ""));
        area.Click += (_, _) => _shortcuts?.Capture(delayMs: 250);
        menu.Items.Add(area);
        menu.Items.Add(new NativeMenuItem($"Convertino — drag a file, {s.TriggerKey.Tap()}") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Check("Enabled", s.Enabled, v => s.Enabled = v));
        var themes = new NativeMenu();
        foreach (var th in Skin.All)
        {
            var it = new NativeMenuItem(th.Name) { ToggleType = MenuItemToggleType.Radio, IsChecked = th.Name == Skin.Current.Name };
            it.Click += (_, _) => { Skin.Choose(th.Name); BuildTray(); };
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
        menu.Items.Add(Check("Tell me about new versions", s.CheckForUpdates == true, Updates.Answer));
        menu.Items.Add(Check("Open at login", s.LaunchAtStartup, v => { s.LaunchAtStartup = v; Platform.Current.SetAutostart(v); }));
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
        var perm = new NativeMenuItem("Input Monitoring…");
        perm.Click += (_, _) => OpenInputMonitoring();
        menu.Items.Add(perm);
        var updates = new NativeMenuItem("Check for updates…");
        updates.Click += (_, _) => UpdateCards.CheckNow();
        menu.Items.Add(updates);
        var log = new NativeMenuItem("Open log");
        log.Click += (_, _) => Platform.Current.Open(Paths.LogFile);
        menu.Items.Add(log);
        var quit = new NativeMenuItem("Quit Convertino");
        quit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Items.Add(quit);

        var tip = $"Convertino — drag a file, {s.TriggerKey.Tap()}";
        if (_tray == null)
        {
            using var ms = new MemoryStream();
            AppIcon.Render(44).Save(ms, PngBitmapEncoderOptions.Default);
            ms.Position = 0;
            // Avalonia.Native hands the tooltip to the status item as it attaches: a null one crashes
            _tray = new TrayIcon { Icon = new WindowIcon(ms), ToolTipText = tip, Menu = menu, IsVisible = true };
            TrayIcon.SetIcons(this, [_tray]);
            // the menu (right-click, Ctrl-click) takes the panel's place
            menu.Opening += (_, _) =>
            {
                if (OverlayController.Diag) Log.Info("tray menu opening");
                _panel?.HideAnimated();
            };
            return;
        }
        // later calls refresh the same status item (a new one could land somewhere else in the menu bar)
        _tray.ToolTipText = tip;
    }

    void Verb(string cmd)
    {
        if (cmd == "clip") _shortcuts?.Clipboard();
        else _shortcuts?.Capture(delayMs: 0);
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
                        else if (cmd is "clip" or "capture") Dispatcher.UIThread.Post(() => Verb(cmd));
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

sealed class MacJobUi : IJobUi
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
                thumb = await Task.Run(() => MacPlatform.Thumbnail(o, 42));
            }
            t.SetDone(title, summary, thumb, icon);
        }

        public void Failed(string title, string message, string? icon) => t.SetDone(title, message, null, icon, error: true);
    }
}
