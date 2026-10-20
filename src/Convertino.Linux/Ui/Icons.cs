using Avalonia;
using Avalonia.Media;

namespace Convertino.Ui;

/// <summary>Stroke icons on a 24×24 grid (Feather-style), keyed like the core's PetalAction.Icon.</summary>
public static class Icons
{
    static readonly Dictionary<string, string> Paths = new()
    {
        ["compress"] = "M4 14h6v6 M20 10h-6V4 M14 10l7-7 M3 21l7-7",
        ["crop"] = "M6 2v14a2 2 0 0 0 2 2h14 M18 22V8a2 2 0 0 0-2-2H2",
        ["addbg"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M21 15l-5-5L5 21 M8.5 7a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z",
        ["annotate"] = "M12 20h9 M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z",
        ["redact"] = "M3 3l18 18 M10.6 10.6a2 2 0 0 0 2.8 2.8 M9.9 4.2A9 9 0 0 1 12 4c7 0 10 8 10 8a17 17 0 0 1-2.2 3.4 M6.6 6.6C3.9 8.4 2 12 2 12s3 8 10 8a9.5 9.5 0 0 0 5.4-1.6",
        ["adjust"] = "M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M1 14h6 M9 8h6 M17 16h6",
        ["tag"] = "M20.6 13.4l-7.2 7.2a2 2 0 0 1-2.8 0L2 12V2h10l8.6 8.6a2 2 0 0 1 0 2.8z M7 7h.01",
        ["ocr"] = "M4 7V4h16v3 M9 20h6 M12 4v16",
        ["zip"] = "M21 8v13H3V8 M1 3h22v5H1z M10 12h4",
        ["link"] = "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7 M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7",
        ["hash"] = "M4 9h16 M4 15h16 M10 3L8 21 M16 3l-2 18",
        ["code"] = "M16 18l6-6-6-6 M8 6l-6 6 6 6",
        ["rotate"] = "M21 12a9 9 0 1 1-2.6-6.4L21 8 M21 3v5h-5",
        ["cut"] = "M6 3a3 3 0 1 0 0 6a3 3 0 1 0 0-6z M6 15a3 3 0 1 0 0 6a3 3 0 1 0 0-6z M20 4L8.1 15.9 M14.5 14.5L20 20 M8.1 8.1L12 12",
        ["merge"] = "M12 2L2 7l10 5 10-5-10-5z M2 17l10 5 10-5 M2 12l10 5 10-5",
        ["mute"] = "M11 5L6 9H2v6h4l5 4V5z M23 9l-6 6 M17 9l6 6",
        ["camera"] = "M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z M12 9a4 4 0 1 0 0 8a4 4 0 1 0 0-8z",
        ["resize"] = "M15 3h6v6 M9 21H3v-6 M21 3l-7 7 M3 21l7-7",
        ["volume"] = "M11 5L6 9H2v6h4l5 4V5z M15.5 8.5a5 5 0 0 1 0 7 M19 5a10 10 0 0 1 0 14",
        ["folder"] = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z",
        ["copy"] = "M11 9h9a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-9a2 2 0 0 1-2-2v-9a2 2 0 0 1 2-2z M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1",
        ["cutout"] = "M12 3a4 4 0 1 0 0 8a4 4 0 1 0 0-8z M4 21v-1a8 8 0 0 1 16 0v1 M2 2l3 3 M22 2l-3 3",
        ["check"] = "M20 6L9 17l-5-5",
        ["update"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3",
        ["x"] = "M18 6L6 18 M6 6l12 12",
        ["alert"] = "M12 9v4 M12 17h.01 M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z",
        ["file"] = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6",
        ["undo"] = "M3 7v6h6 M21 17a9 9 0 0 0-15-6.7L3 13",
        ["redo"] = "M21 7v6h-6 M3 17a9 9 0 0 1 15-6.7L21 13",
        ["arrow"] = "M7 17L17 7 M7 7h10v10",
        ["pen"] = "M12 20h9 M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z",
        ["marker"] = "M9 11l-6 6v3h9l3-3 M22 12l-4.6 4.6a2 2 0 0 1-2.8 0l-5.2-5.2a2 2 0 0 1 0-2.8L14 4",
        ["square"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
        ["circle"] = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z",
        ["text"] = "M4 7V4h16v3 M9 20h6 M12 4v16",
        ["step"] = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z M10 8l2-1v10",
        ["flip"] = "M12 3v18 M16 7l4 5-4 5 M8 7l-4 5 4 5",
    };

    static readonly Dictionary<string, Geometry> Cache = [];

    public static Geometry? Get(string? key)
    {
        if (key == null || !Paths.TryGetValue(key, out var d)) return null;
        if (!Cache.TryGetValue(key, out var g)) Cache[key] = g = Geometry.Parse(d);
        return g;
    }

    /// <summary>Draw an icon centred on <paramref name="c"/> at <paramref name="size"/> px.</summary>
    public static void Draw(DrawingContext dc, string? key, Point c, double size, IBrush brush, double weight = 2)
    {
        var g = Get(key);
        if (g == null) return;
        double s = size / 24.0;
        var m = Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(c.X, c.Y);
        using (dc.PushTransform(m))
            dc.DrawGeometry(null, new Pen(brush, weight, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
    }
}

/// <summary>Icon as a control (toolbars, toasts).</summary>
public sealed class IconView : Avalonia.Controls.Control
{
    public string? Key { get; set; }
    public IBrush Brush { get; set; } = Brushes.White;
    public double Weight { get; set; } = 2;

    public override void Render(DrawingContext dc)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        Icons.Draw(dc, Key, new Point(Bounds.Width / 2, Bounds.Height / 2), size, Brush, Weight);
    }
}
