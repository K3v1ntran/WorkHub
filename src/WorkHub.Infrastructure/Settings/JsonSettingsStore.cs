using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkHub.Core.Settings;

namespace WorkHub.Infrastructure.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c>. Writes go to a temp file first and are then moved into place,
/// so a crash never leaves a half-written settings file. Loading never writes: an unreadable file is left
/// untouched and an older format is upgraded in memory only (it is written in the new format on the next Save).
/// </summary>
public sealed class JsonSettingsStore(string settingsFile, TimeProvider? time = null) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SettingsLoadResult LoadOrCreate()
    {
        if (!File.Exists(settingsFile))
        {
            var defaults = AppSettings.CreateInitial(); // new file only: approved initial configuration
            if (TryWriteNew(defaults))
                return new SettingsLoadResult(defaults, SettingsLoadStatus.Created);
            // Another process created the file between the check and the write: read theirs.
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8), NodeOptions, DocumentOptions) is not JsonObject root)
                return Invalid("The file contains no settings object.");

            var upgradedFrom = SettingsUpgrader.UpgradeInPlace(root);
            var settings = root.Deserialize<AppSettings>(JsonOptions)!;
            return new SettingsLoadResult(settings.Normalize(), SettingsLoadStatus.Loaded, UpgradedFromVersion: upgradedFrom);
        }
        catch (JsonException ex)
        {
            return Invalid(ex.LineNumber is { } line ? $"Invalid JSON at line {line + 1}." : "Invalid JSON.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return Invalid("A setting has the wrong type.");
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
        PreserveUnreadableFile();
        File.Move(temp, settingsFile, overwrite: true);
    }

    /// <summary>An explicit save may replace a file we could not read; keep a copy so hand edits are not lost.</summary>
    private void PreserveUnreadableFile()
    {
        if (!File.Exists(settingsFile))
            return;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8), NodeOptions, DocumentOptions) is JsonObject)
                return;
        }
        catch (JsonException)
        {
        }

        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        File.Copy(settingsFile, $"{settingsFile}.invalid-{stamp}", overwrite: false);
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
