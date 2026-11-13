namespace WorkHub.Core.Settings;

/// <summary>
/// A rectangle in WPF device-independent pixels (DIPs) of the virtual screen: origin at the primary monitor's top-left,
/// X to the right, Y down; monitors left of or above the primary have negative coordinates.
/// </summary>
public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    /// <summary>
    /// A Win32 work area (<c>rcWork</c>, device pixels) converted with the window's device-to-DIP transform. With WPF's
    /// default DPI awareness that transform is a pure scale (<paramref name="scaleX"/>, <paramref name="scaleY"/> =
    /// <c>TransformFromDevice.M11</c>, <c>M22</c>).
    /// </summary>
    public static PlacementRect FromDevice(int left, int top, int right, int bottom, double scaleX, double scaleY) =>
        new(left * scaleX, top * scaleY, (right - left) * scaleX, (bottom - top) * scaleY);
}

/// <summary>What the window does at start: the saved bounds (possibly fitted), or the XAML defaults.</summary>
/// <param name="UseSaved">False: keep the defaults (1100×720, centered, not maximized).</param>
public sealed record PlacementDecision(bool UseSaved, PlacementRect Bounds, bool Maximize)
{
    public static readonly PlacementDecision Defaults = new(false, new PlacementRect(0, 0, WindowPlacementPolicy.DefaultWidth, WindowPlacementPolicy.DefaultHeight), false);
}

/// <summary>
/// Phase 16A (§4.4): validates a saved window placement against the current monitors' work areas. Pure and WPF-free.
/// <list type="bullet">
/// <item><c>Left</c>/<c>Top</c>: any finite value, negative and 0 included, with |v| ≤ 100,000; null = never saved.</item>
/// <item><c>Width</c> ≥ 720 and <c>Height</c> ≥ 460 (the window's minimum size), both finite and ≤ 32,000.</item>
/// <item>Title strip (top 32 DIP): at least 120 × 32 DIP of it inside <b>one</b> work area (D13: per monitor, not summed).</item>
/// <item>A rectangle larger than that work area is shrunk to it and moved inside it; otherwise it is used as saved.</item>
/// <item>Any invalid value → defaults for the whole placement, and IsMaximized is ignored (D7).</item>
/// </list>
/// </summary>
public static class WindowPlacementPolicy
{
    public const double DefaultWidth = 1100;
    public const double DefaultHeight = 720;
    public const double MinWidth = 720;
    public const double MinHeight = 460;
    public const double MaxSize = 32_000;
    public const double MaxCoordinate = 100_000;
    public const double TitleStripHeight = 32;
    public const double MinVisibleStripWidth = 120;

    public static PlacementDecision Decide(WindowPlacementSettings? saved, IReadOnlyList<PlacementRect> workAreas)
    {
        if (saved is not { Left: { } left, Top: { } top })
            return PlacementDecision.Defaults;
        if (!IsCoordinate(left) || !IsCoordinate(top)
            || !IsSize(saved.Width, MinWidth) || !IsSize(saved.Height, MinHeight))
            return PlacementDecision.Defaults;

        var rect = new PlacementRect(left, top, saved.Width, saved.Height);
        foreach (var area in workAreas)
        {
            if (!ShowsTitleStrip(rect, area))
                continue;
            return new PlacementDecision(true, FitInto(rect, area), saved.IsMaximized);
        }

        return PlacementDecision.Defaults;
    }

    private static bool IsCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;

    private static bool IsSize(double value, double min) => double.IsFinite(value) && value >= min && value <= MaxSize;

    /// <summary>At least 120 DIP of the strip's width and its full 32 DIP height lie inside this one work area.</summary>
    private static bool ShowsTitleStrip(PlacementRect rect, PlacementRect area)
    {
        var visibleWidth = Math.Min(rect.Right, area.Right) - Math.Max(rect.Left, area.Left);
        var visibleHeight = Math.Min(rect.Top + TitleStripHeight, area.Bottom) - Math.Max(rect.Top, area.Top);
        return visibleWidth >= MinVisibleStripWidth && visibleHeight >= TitleStripHeight;
    }

    private static PlacementRect FitInto(PlacementRect rect, PlacementRect area)
    {
        if (rect.Width <= area.Width && rect.Height <= area.Height)
            return rect; // used as saved: the body may extend off-screen
        var width = Math.Min(rect.Width, area.Width);
        var height = Math.Min(rect.Height, area.Height);
        var left = Math.Clamp(rect.Left, area.Left, area.Right - width);
        var top = Math.Clamp(rect.Top, area.Top, area.Bottom - height);
        return new PlacementRect(left, top, width, height);
    }
}
