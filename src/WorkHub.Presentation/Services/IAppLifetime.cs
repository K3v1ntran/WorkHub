namespace WorkHub.Presentation.Services;

/// <summary>Phase 10: restart-time Restore and Put back relaunch WorkHub on the same data root and then exit.</summary>
public interface IAppLifetime
{
    /// <summary>Starts WorkHub again (same <c>--data-root</c>); with <paramref name="restoreOp"/> also <c>--restore-continue</c>.</summary>
    /// <returns>False when the new process could not be started.</returns>
    bool Relaunch(string? restoreOp);

    /// <summary>Closes this window and ends the process (never another instance).</summary>
    void Shutdown();
}
