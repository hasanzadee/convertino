using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Convertino.Ui;

/// <summary>Avalonia colours for the shared palette.</summary>
public sealed class Skin
{
    public Palette P { get; }
    public string Name => P.Name;
    public bool IsDark => P.IsDark;
    public Color Tint, PetalTop, PetalBottom, Stroke, Text, SubText, AccentA, AccentB, AccentText, Hub, Shadow, Window, Surface, Danger;

    static Color U(uint argb) => Color.FromUInt32(argb);

    Skin(Palette p)
    {
        P = p;
        Tint = U(p.Tint); PetalTop = U(p.PetalTop); PetalBottom = U(p.PetalBottom); Stroke = U(p.Stroke);
        Text = U(p.Text); SubText = U(p.SubText); AccentA = U(p.AccentA); AccentB = U(p.AccentB); AccentText = U(p.AccentText);
        Hub = U(p.Hub); Shadow = U(p.Shadow); Window = U(p.Window); Surface = U(p.Surface); Danger = U(p.Danger);
    }

    public static readonly Skin[] All = Palette.All.Select(p => new Skin(p)).ToArray();
    public static Skin Current { get; private set; } = All[0];
    public static event Action? Changed;

    public static void Apply(string name)
    {
        Current = All.FirstOrDefault(t => t.Name == name) ?? All[0];
        Changed?.Invoke();
    }

    /// <summary>The user picked a theme (tray menu, panel): remember it and restyle.</summary>
    public static void Choose(string name)
    {
        Settings.Current.Theme = name;
        Settings.Current.Save();
        Apply(name);
    }

    public static Color WithA(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Denser glass for text-heavy surfaces (toasts, panels) so they read on any wallpaper.</summary>
    public Color Solid(Color tint) => Color.FromArgb((byte)Math.Max(tint.A, IsDark ? 215 : 195), tint.R, tint.G, tint.B);

    public IBrush AccentGradient(Rect? absolute = null)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = absolute is { } r ? new RelativePoint(r.Left, r.Top, RelativeUnit.Absolute) : new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = absolute is { } r2 ? new RelativePoint(r2.Right, r2.Bottom, RelativeUnit.Absolute) : new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        g.GradientStops.Add(new GradientStop(AccentA, 0));
        g.GradientStops.Add(new GradientStop(AccentB, 1));
        return g;
    }

    public static IBrush Solid_(Color c) => new ImmutableSolidColorBrush(c);
}
