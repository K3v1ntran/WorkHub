using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.FileSystem;

public sealed class LocalStorage(WorkHubPaths paths) : ILocalStorage
{
    public void EnsureRequiredDirectories()
    {
        foreach (var directory in paths.RequiredDirectories)
            Directory.CreateDirectory(directory);
    }
}
