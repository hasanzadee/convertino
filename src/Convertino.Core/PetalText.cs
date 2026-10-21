namespace Convertino;

/// <summary>Where a petal's caption fits, shared by the flower views: translated labels run longer than the English ones.</summary>
public static class PetalText
{
    static readonly Dictionary<(int, int), double> Rooms = [];

    /// <summary>
    /// How wide a caption centred in petal <paramref name="index"/> of <paramref name="n"/> may be at rest: the horizontal
    /// run through the label point that stays inside the petal (wide on the side petals, narrow at the top and bottom).
    /// </summary>
    public static double Room(int n, int index, double r1, double r2, double gap)
    {
        lock (Rooms)
            if (Rooms.TryGetValue((n, index), out var known)) return known;
        double span = 2 * Math.PI / Math.Max(1, n), mid = -Math.PI / 2 + index * span;
        double lr = (r1 + r2) / 2 + (n <= 2 ? 0 : 2), pad = 7;
        double cx = Math.Cos(mid) * lr, cy = Math.Sin(mid) * lr;
        bool Inside(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (r < r1 + pad || r > r2 - pad) return false;
            if (n == 1) return true;
            double d = Math.Abs(Math.IEEERemainder(Math.Atan2(y, x) - mid, 2 * Math.PI));
            return d <= span / 2 - (gap / 2 + pad) / r;
        }
        double Run(int dir)
        {
            double x = 0;
            while (x < r2 && Inside(cx + dir * (x + 1), cy)) x++;
            return x;
        }
        double room = 2 * Math.Min(Run(-1), Run(+1));
        lock (Rooms) Rooms[(n, index)] = room;
        return room;
    }

    /// <summary>Tool captions are drawn in capitals: in the UI language's rules (Turkish and Azerbaijani i → İ).</summary>
    public static string Caps(string label) => label.ToUpper(L.Culture);

    /// <summary>Letter-spaced capitals suit alphabets; CJK text reads wrong with gaps between its characters.</summary>
    public static bool Spaceable(string text) => text.All(c => c < '⺀');
}
