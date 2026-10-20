using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

public enum HubMode { Seed, Info }

sealed class PetalVisual
{
    public required PetalAction Action;
    public int Index, Count;
    public double Dir;
    public Spring Bloom = new(170, 17);
    public Spring Hover = new(520, 38);
    public Spring Press = new(700, 22);
    public double Delay;
    public bool Leaving;
}

/// <summary>The flower itself — same geometry and motion as the Windows build.</summary>
public sealed class FlowerView : Control
{
    public const double Size = 460, HubR = 54, R1 = 64, R2 = 170, Gap = 6, Corner = 15;

    static readonly FontFamily UiFont = new("Inter, Noto Sans, DejaVu Sans, sans-serif");
    static readonly Typeface LabelFace = new(UiFont, FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface SmallFace = new(UiFont, FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface HintFace = new(UiFont, FontStyle.Normal, FontWeight.Normal);

    readonly List<PetalVisual> _petals = [];
    Spring _appear = new(260, 24);
    Spring _hub = new(300, 20);
    Spring _nudge = new(900, 14);
    double _time;
    TimeSpan _last;
    bool _ticking;
    int _hover = -1;
    int _pageCount = 1, _page;
    string _ringName = "";
    double _ringUntil;

    /// <summary>How long the hub shows a ring's name after a flip, in seconds.</summary>
    const double RingNameTime = 1.1;

    public Bitmap? Backdrop { get; set; }
    public HubMode Mode { get; private set; } = HubMode.Seed;
    public string HubTitle { get; set; } = "";
    public string HubSub { get; set; } = "";
    public string HubBadge { get; set; } = "";
    public Bitmap? Thumb { get; set; }

    Skin T => Skin.Current;

    public FlowerView()
    {
        Width = Height = Size;
        IsHitTestVisible = false;
        ClipToBounds = false;
        Skin.Changed += () => InvalidateVisual();
    }

    public int HoverIndex => _hover;
    public IReadOnlyList<PetalAction> CurrentActions => _petals.Where(p => !p.Leaving).Select(p => p.Action).ToList();

    public void Reset()
    {
        _petals.Clear();
        _appear.Snap(0);
        _hub.Snap(0);
        _hover = -1;
        Thumb = null;
        HubTitle = HubSub = HubBadge = "";
        Mode = HubMode.Seed;
        _pageCount = 1; _page = 0; _ringName = ""; _ringUntil = 0;
        InvalidateVisual();
    }

    public void Appear() { _appear.Target = 1; _hub.Target = 1; Kick(); }
    public void SetMode(HubMode m) { Mode = m; Kick(); }
    /// <summary>Page dots; with a <paramref name="name"/> the hub also shows the ring's name for a moment (after a flip).</summary>
    public void SetPages(int count, int page, string? name = null)
    {
        _pageCount = count; _page = page;
        if (!string.IsNullOrEmpty(name)) { _ringName = name; _ringUntil = _time + RingNameTime; }
        Kick();
    }

    public void SetPetals(IReadOnlyList<PetalAction> actions, double dir)
    {
        foreach (var p in _petals)
        {
            p.Leaving = true;
            p.Bloom.Target = 0; p.Bloom.K = 420; p.Bloom.D = 34;
            p.Delay = 0; p.Dir = -dir;
        }
        int n = actions.Count;
        for (int i = 0; i < n; i++)
            _petals.Add(new PetalVisual
            {
                Action = actions[i], Index = i, Count = n, Dir = dir,
                Delay = 0.022 * i + (_petals.Count > 0 ? 0.05 : 0),
                Bloom = new Spring(190, 16) { Target = 1 },
            });
        _hover = -1;
        Kick();
    }

    public void Nudge() { _nudge.V = 6; Kick(); }

    public void Collapse()
    {
        foreach (var p in _petals) { p.Bloom.Target = 0; p.Bloom.K = 380; p.Bloom.D = 30; p.Delay = 0; }
        _appear.Target = 0; _appear.K = 300; _appear.D = 30;
        Kick();
    }

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

    public int HitTestPetal(Point pt)
    {
        var c = Center;
        double dx = pt.X - c.X, dy = pt.Y - c.Y;
        double r = Math.Sqrt(dx * dx + dy * dy);
        var live = _petals.Where(p => !p.Leaving).ToList();
        if (live.Count == 0 || r < R1 - 4 || r > R2 + 40) return -1;
        double ang = Math.Atan2(dy, dx) + Math.PI / 2;
        int n = live.Count;
        double span = 2 * Math.PI / n;
        ang = (ang + span / 2) % (2 * Math.PI);
        if (ang < 0) ang += 2 * Math.PI;
        return Math.Clamp((int)(ang / span), 0, n - 1);
    }

    public bool IsOverHub(Point pt) => Distance(pt, Center) <= HubR + 6;

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        var live = _petals.Where(p => !p.Leaving).ToList();
        for (int i = 0; i < live.Count; i++) live[i].Hover.Target = i == index ? 1 : 0;
        Kick();
    }

    /// <summary>Jump to the end state (static snapshots).</summary>
    public void Settle(double time = 1.3)
    {
        _appear.Snap(_appear.Target);
        _hub.Snap(_hub.Target);
        _time = time;
        _petals.RemoveAll(p => p.Leaving);
        foreach (var p in _petals) { p.Bloom.Snap(p.Bloom.Target); p.Hover.Snap(p.Hover.Target); p.Delay = 0; }
        InvalidateVisual();
    }

    // ---------- animation ----------

    void Kick()
    {
        InvalidateVisual();
        if (_ticking) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        _ticking = true;
        _last = TimeSpan.Zero;
        top.RequestAnimationFrame(OnFrame);
    }

    void OnFrame(TimeSpan now)
    {
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        _last = now;
        dt = Math.Clamp(dt, 0, 1 / 24.0);
        _time += dt;
        bool busy = false;
        busy |= _appear.Step(dt);
        busy |= _hub.Step(dt);
        busy |= _nudge.Step(dt);
        foreach (var p in _petals)
        {
            if (p.Delay > 0) { p.Delay -= dt; busy = true; continue; }
            busy |= p.Bloom.Step(dt);
            busy |= p.Hover.Step(dt);
            busy |= p.Press.Step(dt);
        }
        _petals.RemoveAll(p => p.Leaving && p.Bloom.X <= 0.002 && p.Bloom.Target == 0);
        if (Mode == HubMode.Seed && _appear.Target > 0) busy = true;
        if (_time < _ringUntil) busy = true; // repaint once the ring's name gives way
        InvalidateVisual();
        var top = TopLevel.GetTopLevel(this);
        if (busy && top != null) top.RequestAnimationFrame(OnFrame);
        else _ticking = false;
    }

    // ---------- rendering ----------

    Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    static IBrush B(Color c) => new ImmutableSolidColorBrush(c);
    static Color A(Color c, double a) => Skin.WithA(c, a);

    public override void Render(DrawingContext dc)
    {
        double a = Math.Clamp(_appear.X, 0, 1.2);
        if (a <= 0.002) return;
        var c = Center;
        double g = 0.86 + 0.14 * Math.Min(a, 1.05);
        double nudge = _nudge.X * 0.06;
        var full = new Rect(0, 0, Bounds.Width, Bounds.Height);
        double bloomMax = _petals.Count == 0 ? 0 : _petals.Max(p => Math.Clamp(p.Bloom.X, 0, 1));
        double ringR = bloomMax > 0 ? R2 * (0.55 + 0.45 * bloomMax) * g : HubR * g;

        using (dc.PushOpacity(Math.Clamp(a * 1.3, 0, 1)))
        {
            // soft drop shadow
            double sr = ringR + 30;
            var sh = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            };
            sh.GradientStops.Add(new GradientStop(T.Shadow, 0));
            sh.GradientStops.Add(new GradientStop(A(T.Shadow, T.Shadow.A / 255.0 * .85), Math.Max(0, (ringR - 22) / sr)));
            sh.GradientStops.Add(new GradientStop(A(T.Shadow, 0), 1));
            dc.DrawEllipse(sh, null, new Point(c.X, c.Y + 12), sr, sr);

            foreach (var p in _petals.Where(p => p.Leaving)) DrawPetal(dc, p, c, g, nudge, full);
            foreach (var p in _petals.Where(p => !p.Leaving)) DrawPetal(dc, p, c, g, nudge, full);
            DrawHub(dc, c, g, full);
        }
    }

    IBrush Glass(Point c)
    {
        var glass = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, c.Y - R2, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, c.Y + R2, RelativeUnit.Absolute),
        };
        glass.GradientStops.Add(new GradientStop(T.PetalTop, 0));
        glass.GradientStops.Add(new GradientStop(T.PetalBottom, 1));
        return glass;
    }

    IPen StrokePen(Point c)
    {
        var sb = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, c.Y - R2, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, c.Y + R2, RelativeUnit.Absolute),
        };
        sb.GradientStops.Add(new GradientStop(T.Stroke, 0));
        sb.GradientStops.Add(new GradientStop(A(T.Stroke, T.Stroke.A / 255.0 * .25), 1));
        return new Pen(sb, 1);
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
        var geo = PetalGeometry(c, r1, r2, mid - span / 2 + rot, mid + span / 2 + rot, Gap, Corner * scale);
        double k = Math.Min(h, 1);

        using (dc.PushOpacity(Math.Clamp(s * 1.7, 0, 1)))
        {
            if (h > 0.01)
            {
                dc.DrawGeometry(null, new Pen(B(A(T.AccentA, .20 * k)), 14, lineJoin: PenLineJoin.Round), geo);
                dc.DrawGeometry(null, new Pen(B(A(T.AccentB, .28 * k)), 6, lineJoin: PenLineJoin.Round), geo);
            }
            if (Backdrop != null)
                using (dc.PushGeometryClip(geo))
                    dc.DrawImage(Backdrop, full);
            dc.DrawGeometry(B(T.Tint), null, geo);
            dc.DrawGeometry(Glass(c), StrokePen(c), geo);
            if (h > 0.01)
                using (dc.PushOpacity(k))
                    dc.DrawGeometry(T.AccentGradient(new Rect(c.X - R2, c.Y - R2, R2 * 2, R2 * 2)), null, geo);

            double lr = (r1 + r2) / 2 + (n <= 2 ? 0 : 2);
            var lc = new Point(c.X + Math.Cos(mid + rot) * lr, c.Y + Math.Sin(mid + rot) * lr);
            var act = p.Action;
            double bs = scale / g;
            if (act.Icon != null)
            {
                DrawIcon(dc, act.Icon, new Point(lc.X, lc.Y - 9 * scale), 20 * bs, k);
                DrawText(dc, Spaced(act.Label.ToUpperInvariant()), SmallFace, 9.2 * bs, lc.X, lc.Y + 12 * scale, k);
            }
            else
            {
                bool hint = !string.IsNullOrEmpty(act.Hint);
                DrawText(dc, act.Label, LabelFace, (act.Label.Length > 4 ? 14 : 16) * bs, lc.X, lc.Y - (hint ? 6 : 0) * scale, k);
                if (hint) DrawText(dc, act.Hint, HintFace, 9.5 * bs, lc.X, lc.Y + 11 * scale, k, sub: true);
            }
        }
    }

    void DrawIcon(DrawingContext dc, string key, Point at, double size, double accent)
    {
        if (accent < 0.99) using (dc.PushOpacity(1 - accent)) Icons.Draw(dc, key, at, size, B(T.Text), 1.8);
        if (accent > 0.01) using (dc.PushOpacity(accent)) Icons.Draw(dc, key, at, size, B(T.AccentText), 1.8);
    }

    void DrawText(DrawingContext dc, string text, Typeface face, double size, double cx, double cy, double accent, bool sub = false)
    {
        if (size < 4) return;
        void One(Color col, double op)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, B(col));
            using (dc.PushOpacity(op * (sub ? 0.85 : 1)))
                dc.DrawText(ft, new Point(cx - ft.WidthIncludingTrailingWhitespace / 2, cy - ft.Height / 2));
        }
        if (accent < 0.99) One(sub ? T.SubText : T.Text, 1 - accent);
        if (accent > 0.01) One(T.AccentText, accent);
    }

    static string Spaced(string s) => string.Join(" ", s.ToCharArray());

    void DrawHub(DrawingContext dc, Point c, double g, Rect full)
    {
        double hs = Math.Clamp(_hub.X, 0, 1.3);
        if (hs <= 0.003) return;
        double r = HubR * (0.55 + 0.45 * hs) * g;
        if (Mode == HubMode.Seed) r *= 0.62 + 0.03 * Math.Sin(_time * 5);
        var circle = new EllipseGeometry(new Rect(c.X - r, c.Y - r, r * 2, r * 2));
        if (Backdrop != null)
            using (dc.PushGeometryClip(circle))
                dc.DrawImage(Backdrop, full);
        dc.DrawGeometry(B(T.Tint), null, circle);
        dc.DrawGeometry(B(T.Hub), StrokePen(c), circle);

        if (Mode == HubMode.Seed)
        {
            double rr = r * (0.55 + 0.08 * Math.Sin(_time * 5));
            using (dc.PushOpacity(0.85))
                dc.DrawGeometry(null, new Pen(T.AccentGradient(), 3, lineCap: PenLineCap.Round), Arc(c, rr, _time * 4, _time * 4 + 4.2));
            return;
        }

        double op = Math.Clamp((hs - 0.4) / 0.6, 0, 1);
        using (dc.PushOpacity(op))
        {
            double ts = 40;
            var tr = new Rect(c.X - ts / 2, c.Y - 33, ts, ts);
            if (Thumb != null)
            {
                double sc = Math.Min(ts / Thumb.Size.Width, ts / Thumb.Size.Height);
                double w = Thumb.Size.Width * sc, hh = Thumb.Size.Height * sc;
                var ir = new Rect(c.X - w / 2, tr.Top + (ts - hh) / 2, w, hh);
                using (dc.PushClip(new RoundedRect(ir, 6)))
                    dc.DrawImage(Thumb, ir);
            }
            else if (!string.IsNullOrEmpty(HubBadge))
            {
                var br = new Rect(c.X - 18, tr.Top + 6, 36, 28);
                dc.DrawRectangle(T.AccentGradient(), null, new RoundedRect(br, 7));
                DrawText(dc, HubBadge, SmallFace, 10, br.X + br.Width / 2, br.Y + br.Height / 2, 1);
            }
            var acts = CurrentActions;
            bool hot = _hover >= 0 && _hover < acts.Count;
            string title = hot ? acts[_hover].Short : HubTitle;
            string sub = HubSub;
            if (hot)
            {
                var act = acts[_hover];
                sub = act.Icon != null ? act.Verb : (string.IsNullOrEmpty(HubBadge) ? act.Hint : $"{HubBadge} → {act.Label}");
            }
            if (_time < _ringUntil) { title = _ringName; sub = ""; hot = true; } // just flipped: say which ring this is
            Fit(dc, title, LabelFace, hot ? 13 : 12, c.X, c.Y + 17, r * 1.7, hot ? T.AccentA : T.Text);
            Fit(dc, sub, HintFace, 9.5, c.X, c.Y + 31, r * 1.6, T.SubText);
            if (_pageCount > 1)
                for (int i = 0; i < _pageCount; i++)
                {
                    bool on = i == _page;
                    double x = c.X + (i - (_pageCount - 1) / 2.0) * 9;
                    dc.DrawEllipse(on ? T.AccentGradient() : B(A(T.SubText, .45)), null, new Point(x, c.Y + r - 9), on ? 2.6 : 2.1, on ? 2.6 : 2.1);
                }
        }
    }

    static void Fit(DrawingContext dc, string text, Typeface face, double size, double cx, double cy, double maxW, Color col)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, B(col))
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
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(from), c.Y + r * Math.Sin(from)), false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(to), c.Y + r * Math.Sin(to)), new Size(r, r), 0, to - from > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
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

        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            var start = Pi0 + u0 * ki;
            ctx.BeginFigure(start, true);
            ctx.LineTo(Po0 - u0 * k);
            ctx.QuadraticBezierTo(Po0, Polar(r2, fo0 + k / r2));
            ctx.ArcTo(Polar(r2, fo0 + spanO - k / r2), new Size(r2, r2), 0, spanO - 2 * k / r2 > Math.PI, SweepDirection.Clockwise);
            ctx.QuadraticBezierTo(Po1, Po1 - u1 * k);
            ctx.LineTo(Pi1 + u1 * ki);
            ctx.QuadraticBezierTo(Pi1, Polar(r1, fi0 + spanI - ki / r1));
            ctx.ArcTo(Polar(r1, fi0 + ki / r1), new Size(r1, r1), 0, spanI - 2 * ki / r1 > Math.PI, SweepDirection.CounterClockwise);
            ctx.QuadraticBezierTo(Pi0, start);
            ctx.EndFigure(true);
        }
        return sg;
    }

    static double Norm(double a)
    {
        while (a < 0) a += 2 * Math.PI;
        while (a >= 2 * Math.PI) a -= 2 * Math.PI;
        return a;
    }
}
