using System.Text;

namespace WorkHub.Infrastructure.Settings;

/// <summary>
/// The two file operations a settings save performs once writing has started: the temp-file write and the atomic replace.
/// A seam so tests can record them (and move a fake clock while one runs); production uses <see cref="Default"/>.
/// </summary>
internal class SettingsFileOps
{
    public static readonly SettingsFileOps Default = new();

    public virtual void WriteAllText(string path, string contents, Encoding encoding) => File.WriteAllText(path, contents, encoding);

    public virtual void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
}
