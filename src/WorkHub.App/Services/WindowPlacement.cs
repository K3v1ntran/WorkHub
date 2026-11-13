using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WorkHub.Core.Settings;

namespace WorkHub.App.Services;

/// <summary>
/// Phase 16A (§4.4, §4.6): applies a saved placement before the main window is shown, and reads the window's state for the
/// close-time save. The validation itself is <see cref="WindowPlacementPolicy"/>; this class only talks to Win32 and WPF.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Validates <paramref name="saved"/> against the monitors' work areas (converted to the window's DIPs) and applies it.
    /// Invalid or not visible → the XAML defaults stay (1100×720, centered, not maximized). Call before <c>Show()</c>.
    /// </summary>
    public static PlacementDecision Apply(Window window, WindowPlacementSettings? saved)
    {
        var decision = WindowPlacementPolicy.Decide(saved, WorkAreas(window));
        if (!decision.UseSaved)
            return decision;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = decision.Bounds.Left;
        window.Top = decision.Bounds.Top;
        window.Width = decision.Bounds.Width;
        window.Height = decision.Bounds.Height;
        if (decision.Maximize)
            window.WindowState = WindowState.Maximized; // after the bounds: maximizes on that monitor; Restore returns to them
        return decision;
    }

    /// <summary>
    /// The restore bounds and maximized flag at this moment (§4.2 point 2): <c>RestoreBounds</c> when maximized or minimized,
    /// otherwise Left/Top/Width/Height. Minimized counts as maximized when the window was maximized before minimizing.
    /// </summary>
    public static (PlacementRect Bounds, bool IsMaximized) Read(Window window, WindowState lastNonMinimizedState)
    {
        var maximized = window.WindowState == WindowState.Maximized
                        || (window.WindowState == WindowState.Minimized && lastNonMinimizedState == WindowState.Maximized);
        if (window.WindowState != WindowState.Normal && window.RestoreBounds is { IsEmpty: false } restore)
            return (new PlacementRect(restore.Left, restore.Top, restore.Width, restore.Height), maximized);
        return (new PlacementRect(window.Left, window.Top, window.Width, window.Height), maximized);
    }

    /// <summary>Every monitor's work area (Win32 <c>rcWork</c>, device pixels) in the window's DIPs.</summary>
    public static IReadOnlyList<PlacementRect> WorkAreas(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice;
        var scaleX = transform?.M11 ?? 1.0;
        var scaleY = transform?.M22 ?? 1.0;
        var areas = new List<PlacementRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(PlacementRect.FromDevice(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, scaleX, scaleY));
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
