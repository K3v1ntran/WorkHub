using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using WorkHub.Core.Startup;
using WorkHub.Presentation.Services;

namespace WorkHub.App.Services;

/// <summary>
/// Phase 10 relaunch: starts this executable again on the same data root (<c>--data-root</c> only when one was given)
/// and, after a confirmed Restore or Put back, with <c>--restore-continue &lt;op&gt;</c>. Never touches other instances.
/// </summary>
public sealed class AppLifetime(StartupOptions options) : IAppLifetime
{
    public bool Relaunch(string? restoreOp)
    {
        if (Environment.ProcessPath is not { } exe)
            return false;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var argument in options.RelaunchArguments(restoreOp))
            info.ArgumentList.Add(argument);
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Shutdown() => Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
}
