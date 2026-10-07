using System.Text;
using System.Text.Json;
using WorkHub.Core.Settings;

namespace WorkHub.Infrastructure.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c>. Writes go to a temp file first and are then moved into place,
/// so a crash never leaves a half-written settings file. An unreadable file is never overwritten on load.
/// </summary>
public sealed class JsonSettingsStore(string settingsFile) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public SettingsLoadResult LoadOrCreate()
    {
        if (!File.Exists(settingsFile))
        {
            var defaults = AppSettings.CreateDefault();
            if (TryWriteNew(defaults))
                return new SettingsLoadResult(defaults, SettingsLoadStatus.Created);
            // Another process created the file between the check and the write: read theirs.
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile, Encoding.UTF8), JsonOptions);
            return settings is null
                ? Invalid("The file contains no settings object.")
                : new SettingsLoadResult(settings.Normalize(), SettingsLoadStatus.Loaded);
        }
        catch (JsonException ex)
        {
            return Invalid($"Invalid JSON at line {ex.LineNumber + 1}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(ex.GetType().Name);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = WriteTemp(settings);
        File.Move(temp, settingsFile, overwrite: true);
    }

    private bool TryWriteNew(AppSettings settings)
    {
        var temp = WriteTemp(settings);
        try
        {
            File.Move(temp, settingsFile, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(settingsFile))
        {
            File.Delete(temp);
            return false;
        }
    }

    private string WriteTemp(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var temp = $"{settingsFile}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), Utf8NoBom);
        return temp;
    }

    private static SettingsLoadResult Invalid(string problem) =>
        new(AppSettings.CreateDefault(), SettingsLoadStatus.InvalidFileUsingDefaults, problem);
}
