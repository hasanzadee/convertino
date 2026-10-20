using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Convertino.Ui;

namespace Convertino.Overlay;

/// <summary>Damped spring — gives the bloom its slightly bouncy, physical feel.</summary>
public struct Spring
{
    public double X, V, Target, K, D;

    public Spring(double k, double d, double x = 0) { K = k; D = d; X = x; Target = x; V = 0; }

    public bool Step(double dt)
    {
        const double h = 1 / 240.0;
        while (dt > 0)
        {
            double s = Math.Min(h, dt);
            double a = -K * (X - Target) - D * V;
            V += a * s;
            X += V * s;
            dt -= s;
        }
        if (Math.Abs(X - Target) < 0.0008 && Math.Abs(V) < 0.002) { X = Target; V = 0; return false; }
        return true;
    }

    public void Snap(double x) { X = Target = x; V = 0; }
}

public enum HubMode { Seed, Info, Busy, Done, Fail }

sealed class PetalVisual
{
    public required PetalAction Action;
    public int Index, Count;
    public double Dir;
    public Spring Bloom = new(170, 17);
    public Spring Hover = new(520, 38);
    public Spring Press = new(700, 22);
    public double Delay; // seconds before bloom starts
    public bool Leaving;
}

public sealed class FlowerView : FrameworkElement
{
    // geometry (DIP)
    public const double HubR = 54, R1 = 64, R2 = 170, Gap = 6, Corner = 15;

    static readonly FontFamily UiFont = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Typeface LabelFace = new(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface SmallFace = new(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface HintFace = new(TextFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface IconFace = new(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    readonly List<PetalVisual> _petals = [];
    Spring _appear = new(260, 24);
    Spring _hub = new(300, 20);
    Spring _nudge = new(900, 14);
    Spring _hubPulse = new(400, 18);
    double _time;
    TimeSpan _lastTick;
    bool _ticking;

    int _hover = -1;
    int _pageCount = 1, _page;
    string _ringName = "";
    double _ringUntil;

    /// <summary>How long the hub shows a ring's name after a flip, in seconds.</summary>
    const double RingNameTime = 1.1;

    public BitmapSource? Backdrop { get; set; }
    public HubMode Mode { get; private set; } = HubMode.Seed;
    public string HubTitle { get; set; } = "";
    public string HubSub { get; set; } = "";
    public string HubBadge { get; set; } = "";
    public BitmapSource? Thumb { get; set; }
    public double Progress { get; set; } = double.NaN;

    /// <summary>Petal results worked out ahead (live preview), or null.</summary>
    public Preview? Ahead { get; set; }

    // the hub turns into a lens showing the hovered petal's result
    Spring _lens = new(240, 26);
    Spring _swap = new(260, 28);
    BitmapSource? _lensImg, _lensPrev;
    bool _lensAlpha, _prevAlpha, _working;
    readonly Dictionary<string, (Preview.Look Look, BitmapSource Bmp)> _lensCache = [];

    Theme T => Theme.Current;

    public FlowerView()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        Theme.Changed += () => { _textCache.Clear(); BuildBrushes(); InvalidateVisual(); };
        BuildBrushes();
    }

    #region public API

    public int HoverIndex => _hover;
    public IReadOnlyList<PetalAction> CurrentActions => _petals.Where(p => !p.Leaving).Select(p => p.Action).ToList();
    public bool IsEmpty => _appear.Target <= 0 && _appear.X <= 0.001;

    public void Reset()
    {
        _petals.Clear();
        _appear.Snap(0);
        _hub.Snap(0);
        _hover = -1;
        Thumb = null;
        HubTitle = HubSub = HubBadge = "";
        Progress = double.NaN;
        Mode = HubMode.Seed;
        Ahead = null;
        _lensCache.Clear();
        _lensImg = _lensPrev = null;
        _lens.Snap(0); _swap.Snap(1);
        _working = false;
        _pageCount = 1; _page = 0; _ringName = ""; _ringUntil = 0;
        InvalidateVisual();
    }

    public void Appear()
    {
        _appear.Target = 1;
        _hub.Target = 1;
        Kick();
    }

    public void SetMode(HubMode m)
    {
        Mode = m;
        if (m is HubMode.Done or HubMode.Fail) { _hubPulse.X = 0.0; _hubPulse.V = 9; _hubPulse.Target = 0; }
        Kick();
    }

    /// <summary>Page dots; with a <paramref name="name"/> the hub also shows the ring's name for a moment (after a flip).</summary>
    public void SetPages(int count, int page, string? name = null)
    {
        _pageCount = count; _page = page;
        if (!string.IsNullOrEmpty(name)) { _ringName = name; _ringUntil = _time + RingNameTime; }
        Kick();
    }

    /// <summary>Swap the ring. dir = +1 rotates in clockwise, -1 counter-clockwise.</summary>
    public void SetPetals(IReadOnlyList<PetalAction> actions, double dir)
    {
        foreach (var p in _petals)
        {
            p.Leaving = true;
            p.Bloom.Target = 0;
            p.Bloom.K = 420; p.Bloom.D = 34;
            p.Delay = 0;
            p.Dir = -dir;
        }
        int n = actions.Count;
        for (int i = 0; i < n; i++)
        {
            _petals.Add(new PetalVisual
            {
                Action = actions[i],
                Index = i,
                Count = n,
                Dir = dir,
                Delay = 0.022 * i + (_petals.Count > 0 ? 0.05 : 0),
                Bloom = new Spring(190, 16) { Target = 1 },
            });
        }
        _hover = -1;
        Kick();
    }

    /// <summary>Jump every animation to its end state (used for static snapshots).</summary>
    public void Settle(double time = 1.3)
    {
        _appear.Snap(_appear.Target);
        _hub.Snap(_hub.Target);
        _time = time;
        _petals.RemoveAll(p => p.Leaving);
        foreach (var p in _petals) { p.Bloom.Snap(p.Bloom.Target); p.Hover.Snap(p.Hover.Target); p.Delay = 0; }
        _lens.Snap(_lens.Target); _swap.Snap(1); _lensPrev = null;
        InvalidateVisual();
    }

    public void Nudge()
    {
        _nudge.V = 6;
        Kick();
    }

    public void Collapse()
    {
        foreach (var p in _petals) { p.Bloom.Target = 0; p.Bloom.K = 380; p.Bloom.D = 30; p.Delay = 0; }
        _appear.Target = 0;
        _appear.K = 300; _appear.D = 30;
        Kick();
    }

    /// <summary>Launch animation for a chosen petal: it pops, the rest fold away.</summary>
    public void Launch(int index)
    {
        var live = _petals.Where(p => !p.Leaving).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var p = live[i];
            if (i == index) { p.Press.V = 14; p.Hover.Target = 1; }
            else { p.Bloom.Target = 0; p.Bloom.K = 300; p.Bloom.D = 26; p.Delay = 0.02 * Math.Abs(i - index); }
        }
        Kick();
    }

    /// <summary>Which live petal is at a point (DIP, relative to this element). -1 = hub / nothing.</summary>
    public int HitTestPetal(Point pt)
    {
        var c = Center;
        double dx = pt.X - c.X, dy = pt.Y - c.Y;
        double r = Math.Sqrt(dx * dx + dy * dy);
        var live = _petals.Where(p => !p.Leaving).ToList();
        if (live.Count == 0 || r < R1 - 4) return -1;
        if (r > R2 + 40) return -1;
        double ang = Math.Atan2(dy, dx) + Math.PI / 2; // 0 at top, clockwise
        int n = live.Count;
        double span = 2 * Math.PI / n;
        ang = (ang + span / 2) % (2 * Math.PI);
        if (ang < 0) ang += 2 * Math.PI;
        return Math.Clamp((int)(ang / span), 0, n - 1);
    }

    public bool IsOverHub(Point pt)
    {
        var c = Center;
        return (pt - c).Length <= HubR + 6;
    }

    public void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        var live = _petals.Where(p => !p.Leaving).ToList();
        for (int i = 0; i < live.Count; i++) live[i].Hover.Target = i == index ? 1 : 0;
        UpdateLens();
        Kick();
    }

    /// <summary>The preview worked something out: new sizes on the petals, maybe a picture for the lens.</summary>
    public void AheadChanged()
    {
        UpdateLens();
        Kick();
    }

    PetalAction? Hovered => _hover >= 0 && _hover < CurrentActions.Count ? CurrentActions[_hover] : null;

    void UpdateLens()
    {
        var a = Hovered;
        var look = a == null ? null : Ahead?.Get(a);
        if (a == null || look?.Bgra == null) { _lens.Target = 0; return; } // the last picture stays while it fades
        var bmp = LensBitmap(a.Id, look);
        if (!ReferenceEquals(bmp, _lensImg))
        {
            // another petal: cross-fade; the same petal's sharper picture (its full run finished): just swap
            if (_lensImg != null && _lensId != a.Id && _lens.X > 0.05) { _lensPrev = _lensImg; _prevAlpha = _lensAlpha; _swap.Snap(0); _swap.Target = 1; }
            _lensImg = bmp;
            _lensAlpha = look.Alpha;
            _lensId = a.Id;
        }
        _lens.Target = 1;
    }

    string? _lensId;

    BitmapSource LensBitmap(string id, Preview.Look look)
    {
        if (_lensCache.TryGetValue(id, out var c) && ReferenceEquals(c.Look, look)) return c.Bmp;
        var bmp = BitmapSource.Create(look.W, look.H, 96, 96, PixelFormats.Bgra32, null, look.Bgra!, look.W * 4);
        bmp.Freeze();
        _lensCache[id] = (look, bmp);
        return bmp;
    }

    #endregion

    #region animation loop

    void Kick()
    {
        if (_ticking) return;
        _ticking = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
        InvalidateVisual();
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastTick == TimeSpan.Zero ? 1 / 60.0 : (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0) return;
        dt = Math.Min(dt, 1 / 24.0);
        _time += dt;

        bool busy = false;
        busy |= _appear.Step(dt);
        busy |= _hub.Step(dt);
        busy |= _nudge.Step(dt);
        busy |= _hubPulse.Step(dt);
        busy |= _lens.Step(dt);
        busy |= _swap.Step(dt);
        if (_lens.X <= 0.002 && _lens.Target == 0) _lensImg = _lensPrev = null;
        _working = Mode == HubMode.Info && Hovered is { } ha && Ahead?.Working(ha) == true;
        if (_working) busy = true; // spinner on the hub's rim
        foreach (var p in _petals)
        {
            if (p.Delay > 0) { p.Delay -= dt; busy = true; continue; }
            busy |= p.Bloom.Step(dt);
            busy |= p.Hover.Step(dt);
            busy |= p.Press.Step(dt);
        }
        _petals.RemoveAll(p => p.Leaving && p.Bloom.X <= 0.002 && p.Bloom.Target == 0);
        if (Mode is HubMode.Seed or HubMode.Busy) busy = true; // continuous pulse / spinner
        if (_time < _ringUntil) busy = true; // repaint once the ring's name gives way

        InvalidateVisual();
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            _ticking = false;
        }
    }

    #endregion

    #region rendering

    Point Center => new(ActualWidth / 2, ActualHeight / 2);

    Brush _tint = Brushes.Transparent, _glass = Brushes.Transparent, _accent = Brushes.Transparent, _hubFill = Brushes.Transparent;
    Pen _stroke = new(Brushes.Transparent, 1);
    Brush _catcher = Brushes.Transparent;

    void BuildBrushes()
    {
        var t = T;
        _tint = Frozen(new SolidColorBrush(t.Tint));
        var glass = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 230 - R2), EndPoint = new Point(0, 230 + R2) };
        glass.GradientStops.Add(new GradientStop(t.PetalTop, 0));
        glass.GradientStops.Add(new GradientStop(t.PetalBottom, 1));
        _glass = Frozen(glass);
        var acc = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(230 - R2, 230 - R2), EndPoint = new Point(230 + R2, 230 + R2) };
        acc.GradientStops.Add(new GradientStop(t.AccentA, 0));
        acc.GradientStops.Add(new GradientStop(t.AccentB, 1));
        _accent = Frozen(acc);
        var hub = new LinearGradientBrush(t.Hub, WithA(t.Hub, t.Hub.A / 255.0 * .82), 90);
        _hubFill = Frozen(hub);
        var sb = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 230 - R2), EndPoint = new Point(0, 230 + R2) };
        sb.GradientStops.Add(new GradientStop(t.Stroke, 0));
        sb.GradientStops.Add(new GradientStop(WithA(t.Stroke, t.Stroke.A / 255.0 * .25), 1));
        _stroke = new Pen(Frozen(sb), 1);
        _stroke.Freeze();
        _catcher = Frozen(new SolidColorBrush(Color.FromArgb(3, 0, 0, 0)));
    }

    static T2 Frozen<T2>(T2 f) where T2 : Freezable { f.Freeze(); return f; }
    static Color WithA(Color c, double a) { c.A = (byte)Math.Clamp(a * 255, 0, 255); return c; }

    protected override void OnRender(DrawingContext dc)
    {
        double a = Math.Clamp(_appear.X, 0, 1.2);
        if (a <= 0.002) return;
        var c = Center;
        double g = 0.86 + 0.14 * Math.Min(a, 1.05);
        double nudge = _nudge.X * 0.06; // radians of shake

        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        double bloomMax = _petals.Count == 0 ? 0 : _petals.Max(p => Math.Clamp(p.Bloom.X, 0, 1));
        double ringR = bloomMax > 0 ? R2 * (0.55 + 0.45 * bloomMax) * g : HubR * g;

        dc.PushOpacity(Math.Clamp(a * 1.3, 0, 1));

        // soft drop shadow
        {
            double sr = ringR + 30;
            var sh = new RadialGradientBrush { GradientOrigin = new Point(.5, .5) };
            sh.GradientStops.Add(new GradientStop(T.Shadow, 0));
            sh.GradientStops.Add(new GradientStop(WithA(T.Shadow, T.Shadow.A / 255.0 * .85), Math.Max(0, (ringR - 22) / sr)));
            sh.GradientStops.Add(new GradientStop(WithA(T.Shadow, 0), 1));
            dc.DrawEllipse(sh, null, new Point(c.X, c.Y + 12), sr, sr);
        }

        // invisible disc so OLE hit-testing doesn't fall through the gaps between petals
        if (bloomMax > 0.05) dc.DrawEllipse(_catcher, null, c, ringR + 10, ringR + 10);

        foreach (var p in _petals.Where(p => p.Leaving)) DrawPetal(dc, p, c, g, nudge, full);
        foreach (var p in _petals.Where(p => !p.Leaving)) DrawPetal(dc, p, c, g, nudge, full);

        DrawHub(dc, c, g, full);

        dc.Pop();
    }

    void DrawPetal(DrawingContext dc, PetalVisual p, Point c, double g, double nudge, Rect full)
    {
        double s = p.Bloom.X;
        if (s <= 0.003) return;
        double h = Math.Clamp(p.Hover.X, 0, 1.2);
        double pr = p.Press.X;
        int n = p.Count;
        double span = 2 * Math.PI / n;
        double mid = -Math.PI / 2 + p.Index * span;
        double rot = (1 - s) * 0.5 * p.Dir + nudge;
        double scale = (0.5 + 0.5 * s) * g;
        double r1 = (R1 + 2 * h) * scale;
        double r2 = (R2 + 10 * h + 8 * pr) * scale;
        double a0 = mid - span / 2 + rot, a1 = mid + span / 2 + rot;
        var geo = PetalGeometry(c, r1, r2, a0, a1, Gap, Corner * scale);

        dc.PushOpacity(Math.Clamp(s * 1.7, 0, 1));

        if (h > 0.01)
        {
            var glowC = WithA(T.AccentA, .20 * Math.Min(h, 1));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(glowC), 14) { LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(WithA(T.AccentB, .28 * Math.Min(h, 1))), 6) { LineJoin = PenLineJoin.Round }, geo);
        }

        if (Backdrop != null)
        {
            dc.PushClip(geo);
            dc.DrawImage(Backdrop, full);
            dc.Pop();
        }
        dc.DrawGeometry(_tint, null, geo);
        dc.DrawGeometry(_glass, _stroke, geo);
        if (h > 0.01)
        {
            dc.PushOpacity(Math.Min(h, 1));
            dc.DrawGeometry(_accent, null, geo);
            dc.Pop();
        }

        // content
        double lr = (r1 + r2) / 2 + (n <= 2 ? 0 : 2);
        var lc = new Point(c.X + Math.Cos(mid + rot) * lr, c.Y + Math.Sin(mid + rot) * lr);
        double k = Math.Min(h, 1);
        var act = p.Action;
        if (act.Icon != null)
        {
            DrawText(dc, Ui.Icons.Glyph(act.Icon) ?? "", IconFace, 19 * scale / g, lc.X, lc.Y - 9 * scale, k);
            DrawText(dc, act.Label.ToUpperInvariant(), SmallFace, 9.2 * scale / g, lc.X, lc.Y + 12 * scale, k, spacing: true);
        }
        else
        {
            bool hint = !string.IsNullOrEmpty(act.Hint);
            // live preview: what this format would weigh, in accent when it beats the source
            var look = Ahead?.Get(act);
            string? size = look?.Bytes is long b ? (look.Exact ? "" : "≈") + Paths.HumanSize(b) : null;
            double lift = size != null ? 6 : 0;
            DrawText(dc, act.Label, LabelFace, (act.Label.Length > 4 ? 14 : 16) * scale / g, lc.X, lc.Y - ((hint ? 6 : 0) + lift) * scale, k);
            if (hint) DrawText(dc, act.Hint, HintFace, 9.5 * scale / g, lc.X, lc.Y + (11 - lift) * scale, k, sub: true);
            if (size != null)
                DrawText(dc, size, SmallFace, 9.5 * scale / g, lc.X, lc.Y + (hint ? 18 : 12) * scale, k, normal: Ahead!.Smaller(look!) ? T.AccentA : T.SubText);
        }

        dc.Pop();
    }

    readonly Dictionary<(string, double, int), FormattedText> _textCache = [];

    void DrawText(DrawingContext dc, string text, Typeface face, double size, double cx, double cy, double accent, bool spacing = false, bool sub = false, Color? normal = null)
    {
        size = Math.Round(size * 4) / 4;
        if (size < 4) return;
        var rest = normal ?? (sub ? T.SubText : T.Text);
        var hot = T.AccentText;
        if (accent < 0.99) Draw(rest, 1 - accent);
        if (accent > 0.01) Draw(hot, accent);

        void Draw(Color col, double op)
        {
            var key = (text + "|" + face.FontFamily.Source + face.Weight, size, col.GetHashCode());
            if (!_textCache.TryGetValue(key, out var ft))
            {
                ft = new FormattedText(spacing ? Spaced(text) : text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size,
                    new SolidColorBrush(col), VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { TextAlignment = TextAlignment.Left };
                if (_textCache.Count > 400) _textCache.Clear();
                _textCache[key] = ft;
            }
            dc.PushOpacity(op * (sub ? 0.85 : 1));
            dc.DrawText(ft, new Point(cx - ft.WidthIncludingTrailingWhitespace / 2, cy - ft.Height / 2));
            dc.Pop();
        }
    }

    static string Spaced(string s) => string.Join(" ", s.ToCharArray());

    void DrawHub(DrawingContext dc, Point c, double g, Rect full)
    {
        double hs = Math.Clamp(_hub.X, 0, 1.3);
        if (hs <= 0.003) return;
        double pulse = _hubPulse.X;
        double r = HubR * (0.55 + 0.45 * hs) * g * (1 + pulse * 0.08);
        if (Mode == HubMode.Seed) r *= 0.62 + 0.03 * Math.Sin(_time * 5);

        var circle = new EllipseGeometry(c, r, r);
        if (Backdrop != null)
        {
            dc.PushClip(circle);
            dc.DrawImage(Backdrop, full);
            dc.Pop();
        }
        dc.DrawGeometry(_tint, null, circle);
        dc.DrawGeometry(_hubFill, _stroke, circle);

        switch (Mode)
        {
            case HubMode.Seed:
            {
                // breathing accent ring
                double rr = r * (0.55 + 0.08 * Math.Sin(_time * 5));
                var pen = new Pen(_accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.PushOpacity(0.85);
                dc.DrawGeometry(null, pen, Arc(c, rr, _time * 4, _time * 4 + 4.2));
                dc.Pop();
                break;
            }
            case HubMode.Info:
            {
                double op = Math.Clamp((hs - 0.4) / 0.6, 0, 1);
                dc.PushOpacity(op);
                double lens = DrawLens(dc, c, r);
                dc.PushOpacity(1 - lens);
                double ts = 40;
                var tr = new Rect(c.X - ts / 2, c.Y - 33, ts, ts);
                if (Thumb != null)
                {
                    double sc = Math.Min(ts / Thumb.Width, ts / Thumb.Height);
                    double w = Thumb.Width * sc, hgt = Thumb.Height * sc;
                    var ir = new Rect(c.X - w / 2, tr.Top + (ts - hgt) / 2, w, hgt);
                    dc.PushClip(new RectangleGeometry(ir, 6, 6));
                    dc.DrawImage(Thumb, ir);
                    dc.Pop();
                }
                else if (!string.IsNullOrEmpty(HubBadge))
                {
                    var br = new Rect(c.X - 18, tr.Top + 6, 36, 28);
                    dc.DrawRoundedRectangle(_accent, null, br, 7, 7);
                    DrawText(dc, HubBadge, SmallFace, 10, br.X + br.Width / 2, br.Y + br.Height / 2, 1);
                }
                dc.Pop();
                var acts = CurrentActions;
                bool hot = _hover >= 0 && _hover < acts.Count;
                string title = hot ? acts[_hover].Short : HubTitle;
                string sub = HubSub;
                if (hot)
                {
                    var a = acts[_hover];
                    sub = a.Icon != null ? a.Verb : (string.IsNullOrEmpty(HubBadge) ? a.Hint : $"{HubBadge} → {a.Label}");
                    // live preview: the real (or estimated) size, or why there's nothing to make
                    if (Ahead?.Get(a) is { } look && Ahead.SizeLine(look) is { } line) sub = line;
                }
                bool named = _time < _ringUntil; // just flipped: say which ring this is
                if (named) { title = _ringName; sub = ""; hot = true; }
                // over the lens the text sits on a dark scrim: white, whatever the theme
                Color? onLens = lens > 0.5 ? Colors.White : null;
                DrawTextFit(dc, title, LabelFace, hot ? 13 : 12, c.X, c.Y + 17, r * 1.7, hot && onLens == null, color: onLens);
                DrawTextFit(dc, sub, HintFace, 9.5, c.X, c.Y + 31, r * 1.6, false, sub: true, color: onLens != null ? Color.FromArgb(225, 255, 255, 255) : null);
                if (_working) // the hovered petal is being worked out and there's nothing to show yet
                {
                    var pen = new Pen(_accent, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawGeometry(null, pen, Arc(c, r - 3.5, _time * 5, _time * 5 + 1.3));
                }
                if (_pageCount > 1)
                {
                    for (int i = 0; i < _pageCount; i++)
                    {
                        bool on = i == _page;
                        double x = c.X + (i - (_pageCount - 1) / 2.0) * 9;
                        dc.DrawEllipse(on ? _accent : new SolidColorBrush(WithA(T.SubText, .45)), null, new Point(x, c.Y + r - 9), on ? 2.6 : 2.1, on ? 2.6 : 2.1);
                    }
                }
                dc.Pop();
                break;
            }
            case HubMode.Busy:
            {
                var track = new Pen(new SolidColorBrush(WithA(T.SubText, .2)), 3.5);
                dc.DrawEllipse(null, track, c, r - 10, r - 10);
                var pen = new Pen(_accent, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (double.IsNaN(Progress))
                    dc.DrawGeometry(null, pen, Arc(c, r - 10, _time * 6, _time * 6 + 1.6 + Math.Sin(_time * 3) * 0.7));
                else
                    dc.DrawGeometry(null, pen, Arc(c, r - 10, -Math.PI / 2, -Math.PI / 2 + 2 * Math.PI * Math.Clamp(Progress, 0.01, 0.999)));
                break;
            }
            case HubMode.Done:
            case HubMode.Fail:
            {
                bool ok = Mode == HubMode.Done;
                Brush fill = ok ? _accent : new SolidColorBrush(T.Danger);
                double cr = r * 0.52;
                dc.DrawEllipse(fill, null, c, cr, cr);
                var pen = new Pen(new SolidColorBrush(ok ? T.AccentText : Colors.White), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    if (ok)
                    {
                        ctx.BeginFigure(new Point(c.X - cr * .42, c.Y + cr * .02), false, false);
                        ctx.LineTo(new Point(c.X - cr * .1, c.Y + cr * .32), true, true);
                        ctx.LineTo(new Point(c.X + cr * .45, c.Y - cr * .3), true, true);
                    }
                    else
                    {
                        ctx.BeginFigure(new Point(c.X - cr * .3, c.Y - cr * .3), false, false);
                        ctx.LineTo(new Point(c.X + cr * .3, c.Y + cr * .3), true, true);
                        ctx.BeginFigure(new Point(c.X + cr * .3, c.Y - cr * .3), false, false);
                        ctx.LineTo(new Point(c.X - cr * .3, c.Y + cr * .3), true, true);
                    }
                }
                dc.DrawGeometry(null, pen, sg);
                break;
            }
        }
    }

    /// <summary>The hovered petal's result filling the hub, a dark scrim at the bottom for the caption. Returns how visible it is.</summary>
    double DrawLens(DrawingContext dc, Point c, double r)
    {
        double k = Math.Clamp(_lens.X, 0, 1);
        if (k <= 0.01 || _lensImg == null) return 0;
        var circle = new EllipseGeometry(c, r - 1, r - 1);
        dc.PushClip(circle);
        dc.PushOpacity(k);
        dc.DrawEllipse(new SolidColorBrush(T.IsDark ? Color.FromRgb(24, 24, 37) : Color.FromRgb(230, 233, 239)), null, c, r, r);
        double sw = Math.Clamp(_swap.X, 0, 1);
        if (_lensPrev != null && sw < 0.99) Picture(_lensPrev, _prevAlpha, 1 - sw);
        Picture(_lensImg, _lensAlpha, _lensPrev != null ? sw : 1);
        // scrim under the caption
        var scrim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.38));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.72));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(190, 0, 0, 0), 1));
        dc.DrawRectangle(scrim, null, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
        dc.Pop();
        dc.Pop();
        // a fine ring so the lens reads as glass, not a hole
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(WithA(T.AccentA, .55 * k)), 1.4), c, r - 0.7, r - 0.7);
        return k;

        void Picture(BitmapSource img, bool alpha, double op)
        {
            if (op <= 0.01) return;
            // cover the circle, easing in from a slight zoom
            double side = 2 * r, sc = Math.Max(side / img.PixelWidth, side / img.PixelHeight) * (1 + 0.06 * (1 - k));
            double w = img.PixelWidth * sc, h = img.PixelHeight * sc;
            var rect = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
            dc.PushOpacity(op);
            if (alpha) dc.DrawRectangle(CheckerBrush, null, rect);
            dc.DrawImage(img, rect);
            dc.Pop();
        }
    }

    // transparency shows as the usual grey checkerboard
    static readonly Brush CheckerBrush = MakeChecker();

    static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(236, 236, 240)), null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        var dark = new SolidColorBrush(Color.FromRgb(200, 200, 208));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(6, 6, 6, 6))));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    void DrawTextFit(DrawingContext dc, string text, Typeface face, double size, double cx, double cy, double maxW, bool accent, bool sub = false, Color? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var col = color ?? (accent ? T.AccentA : sub ? T.SubText : T.Text);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // a little smaller rather than cut off ("3840×3840 · 1.04 MB" in the hub), then the ellipsis
        double natural = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, dip).WidthIncludingTrailingWhitespace;
        if (natural > maxW) size = Math.Max(size * 0.8, size * maxW / natural);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size,
            new SolidColorBrush(col), dip)
        {
            MaxTextWidth = Math.Max(10, maxW),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(ft, new Point(cx - ft.MaxTextWidth / 2, cy - ft.Height / 2));
    }

    static Geometry Arc(Point c, double r, double from, double to)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            var p0 = new Point(c.X + r * Math.Cos(from), c.Y + r * Math.Sin(from));
            var p1 = new Point(c.X + r * Math.Cos(to), c.Y + r * Math.Sin(to));
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, to - from > Math.PI, SweepDirection.Clockwise, true, true);
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>Annular sector with a constant-width gap to its neighbours and softly rounded corners.</summary>
    public static Geometry PetalGeometry(Point c, double r1, double r2, double a0, double a1, double gap, double k)
    {
        double g = gap / 2;
        var u0 = new Vector(Math.Cos(a0), Math.Sin(a0));
        var n0 = new Vector(-Math.Sin(a0), Math.Cos(a0));
        var u1 = new Vector(Math.Cos(a1), Math.Sin(a1));
        var n1 = new Vector(-Math.Sin(a1), Math.Cos(a1));
        double t2 = Math.Sqrt(Math.Max(0, r2 * r2 - g * g));
        double t1 = Math.Sqrt(Math.Max(0, r1 * r1 - g * g));
        Point Po0 = c + u0 * t2 + n0 * g, Po1 = c + u1 * t2 - n1 * g;
        Point Pi0 = c + u0 * t1 + n0 * g, Pi1 = c + u1 * t1 - n1 * g;
        double fo0 = Math.Atan2(Po0.Y - c.Y, Po0.X - c.X), fo1 = Math.Atan2(Po1.Y - c.Y, Po1.X - c.X);
        double fi0 = Math.Atan2(Pi0.Y - c.Y, Pi0.X - c.X), fi1 = Math.Atan2(Pi1.Y - c.Y, Pi1.X - c.X);
        double spanO = Norm(fo1 - fo0), spanI = Norm(fi1 - fi0);
        k = Math.Min(k, (r2 - r1) / 2 - 0.5);
        k = Math.Min(k, spanO * r2 / 2.2);
        double ki = Math.Min(k, spanI * r1 / 2.2);
        if (k < 0) k = 0;
        if (ki < 0) ki = 0;

        Point Polar(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));

        var sg = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = sg.Open())
        {
            var start = Pi0 + u0 * ki;
            ctx.BeginFigure(start, true, true);
            ctx.LineTo(Po0 - u0 * k, true, true);
            ctx.QuadraticBezierTo(Po0, Polar(r2, fo0 + k / r2), true, true);
            var oEnd = Polar(r2, fo0 + spanO - k / r2);
            ctx.ArcTo(oEnd, new Size(r2, r2), 0, spanO - 2 * k / r2 > Math.PI, SweepDirection.Clockwise, true, true);
            ctx.QuadraticBezierTo(Po1, Po1 - u1 * k, true, true);
            ctx.LineTo(Pi1 + u1 * ki, true, true);
            ctx.QuadraticBezierTo(Pi1, Polar(r1, fi0 + spanI - ki / r1), true, true);
            ctx.ArcTo(Polar(r1, fi0 + ki / r1), new Size(r1, r1), 0, spanI - 2 * ki / r1 > Math.PI, SweepDirection.Counterclockwise, true, true);
            ctx.QuadraticBezierTo(Pi0, start, true, true);
        }
        sg.Freeze();
        return sg;
    }

    static double Norm(double a)
    {
        while (a < 0) a += 2 * Math.PI;
        while (a >= 2 * Math.PI) a -= 2 * Math.PI;
        return a;
    }

    #endregion
}
