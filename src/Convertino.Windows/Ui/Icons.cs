namespace Convertino.Ui;

/// <summary>Core icon keys → Segoe Fluent Icons glyphs.</summary>
public static class Icons
{
    static readonly Dictionary<string, string> Map = new()
    {
        ["compress"] = "", ["crop"] = "", ["addbg"] = "", ["annotate"] = "", ["redact"] = "",
        ["adjust"] = "", ["tag"] = "", ["ocr"] = "", ["zip"] = "", ["link"] = "",
        ["hash"] = "", ["code"] = "", ["rotate"] = "", ["cut"] = "", ["merge"] = "",
        ["mute"] = "", ["camera"] = "", ["resize"] = "", ["volume"] = "", ["folder"] = "",
        ["copy"] = "", ["update"] = "\uE896", ["cutout"] = "",
    };

    public static string? Glyph(string? key) => key != null && Map.TryGetValue(key, out var g) ? g : null;
}
