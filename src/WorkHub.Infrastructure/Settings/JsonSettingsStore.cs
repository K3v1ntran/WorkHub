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
public sealed class JsonSettingsStore(string settingsFile, TimeProvider? time = null, TimeSpan? lockTimeout = null) : ISettingsStore
{
    /// <summary>How long a save waits for another WorkHub process that holds the settings lock.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(25);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

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

    /// <summary>The cross-process settings lock (held exclusively while a save re-reads, merges and replaces the file).</summary>
    public string LockFile => settingsFile + ".lock";

    /// <summary>The temp-file write and the atomic replace of <see cref="Update(SettingsEdit, AppSettings)"/> (a test seam).</summary>
    internal SettingsFileOps Files { get; init; } = SettingsFileOps.Default;

    /// <summary>
    /// Test seam (budgeted saves only): runs inside the write preparation (after the JSON text is serialized, before the
    /// folder and temp path are prepared) and so before the last budget check, e.g. to move a fake clock past the budget
    /// while the write is being prepared.
    /// </summary>
    internal Action? DuringWritePreparation { get; init; }

    /// <summary>Test seam (budgeted saves only): runs immediately before the last budget check, which directly precedes the
    /// temp-file write.</summary>
    internal Action? BeforeLastBudgetCheck { get; init; }

    public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback) => UpdateCore(edit, fallback, budget: null);

    /// <inheritdoc />
    public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback, SaveBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return UpdateCore(edit, fallback, budget);
    }

    /// <summary>
    /// Phase 16: reads only <c>ui</c> for the start-up placement and tab. Opens the file for reading with full sharing and
    /// never creates, locks or writes anything; a missing, unreadable or mistyped file gives null.
    /// </summary>
    public UiSettings? PeekUi()
    {
        try
        {
            if (!File.Exists(settingsFile))
                return null;
            string text;
            using (var stream = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();
            if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject root)
                return null;
            SettingsUpgrader.UpgradeInPlace(root);
            return root.Deserialize<AppSettings>(JsonOptions)?.Normalize().Ui;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private SettingsUpdateResult UpdateCore(SettingsEdit edit, AppSettings fallback, SaveBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(fallback);
        var expired = false;
        var acquired = budget is not null ? AcquireLock(budget, out expired) : AcquireLock();
        using var settingsLock = acquired;
        if (settingsLock is null)
            return new SettingsUpdateResult(expired ? SettingsUpdateStatus.DeadlineExpired : SettingsUpdateStatus.LockTimeout);

        // 1. The document as it is now: what other processes saved meanwhile is kept, not overwritten from our snapshot.
        var root = TryReadDocument();
        var replacingUnreadable = false;
        if (root is null)
        {
            replacingUnreadable = File.Exists(settingsFile);
            if (replacingUnreadable && !edit.AllowReplacingUnreadableFile)
                return new SettingsUpdateResult(SettingsUpdateStatus.FileUnreadable);
            root = ToObject(JsonSerializer.SerializeToNode(fallback, JsonOptions)); // missing file, or an explicit replacement
        }

        // 2. Only the requested members change.
        if (edit.Repository is { } repository)
            MergeInto(Child(root, "repository"), ToObject(JsonSerializer.SerializeToNode(repository, JsonOptions)));
        if (edit.Toolbars is { } toolbars)
        {
            var workspace = Child(Child(root, "ui"), "workspace");
            if (toolbars.Tree is { } tree)
                workspace["showTreeToolbar"] = tree;
            if (toolbars.Selection is { } selection)
                workspace["showSelectionToolbar"] = selection;
            if (toolbars.Content is { } content)
                workspace["showContentToolbar"] = content;
        }
        if (edit.Ui is { } ui)
        {
            // Phase 16: only these members; unknown members inside ui.window keep their values.
            var uiObject = Child(root, "ui");
            var window = Child(uiObject, "window");
            window["left"] = ui.Left;
            window["top"] = ui.Top;
            window["width"] = ui.Width;
            window["height"] = ui.Height;
            window["isMaximized"] = ui.IsMaximized;
            if (ui.LastPrimaryTab is { } tab)
                uiObject["lastPrimaryTab"] = tab;
        }
        if (edit.NotesAutoSave is { } autoSave)
        {
            // Phase 18: only this member; ui.notes is created when missing and its unknown members keep their values.
            Child(Child(root, "ui"), "notes")["autosave"] = autoSave;
        }
        var version = root["settingsVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 0;
        root["settingsVersion"] = Math.Max(version, AppSettings.CurrentSettingsVersion); // a newer file's version is kept

        // 3. The saved snapshot is the merged document itself (it must deserialize, or nothing is written).
        var saved = root.Deserialize<AppSettings>(JsonOptions)!.Normalize();

        // 4. Everything the write needs is prepared first: the JSON text, the folder and the temp path. Nothing here
        // creates the temp file or touches settings.json.
        var json = root.ToJsonString(JsonOptions);
        if (budget is not null)
            DuringWritePreparation?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var temp = $"{settingsFile}.{Guid.NewGuid():N}.tmp";

        // Phase 16 (D12): the last check, immediately before the temp file is written, on the budget's monotonic timestamp
        // (a wall-clock change never moves it). Past the budget nothing is written. After this point the write (temp file,
        // then atomic replace) runs to completion.
        if (budget is not null)
        {
            BeforeLastBudgetCheck?.Invoke();
            if (budget.IsExpired)
                return new SettingsUpdateResult(SettingsUpdateStatus.DeadlineExpired);
        }

        // 5. Atomic replace. Only this operation's own temp file is ever removed.
        try
        {
            Files.WriteAllText(temp, json, Utf8NoBom);
            if (replacingUnreadable)
                PreserveUnreadableFile(force: true); // the dialog-save recovery: a copy of the unreadable file is kept
            Files.Move(temp, settingsFile, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new SettingsUpdateResult(SettingsUpdateStatus.Saved, saved);
    }

    /// <summary>The file as a JSON object (upgraded in memory), or null when it is missing or cannot be read as settings.</summary>
    private JsonObject? TryReadDocument()
    {
        if (!File.Exists(settingsFile))
            return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8), NodeOptions, DocumentOptions) is not JsonObject root)
                return null;
            SettingsUpgrader.UpgradeInPlace(root);
            _ = root.Deserialize<AppSettings>(JsonOptions); // wrong types make the file unreadable, as on load
            return root;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private FileStream? AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var timeout = lockTimeout ?? DefaultLockTimeout;
        var started = _time.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (_time.GetElapsedTime(started) < timeout)
            {
                Thread.Sleep(25); // held by another WorkHub process
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Phase 16 (D12): the lock wait for a save with a budget. It lasts at most the lock ceiling (the store's lock timeout,
    /// 2 s by default) or what is left of <paramref name="budget"/>, whichever is shorter. Both are measured on the budget's
    /// monotonic timestamp, the same source the saver started the budget on; the wall clock is never read. Null with
    /// <paramref name="expired"/> = true when the budget, not the ceiling, ended the wait.
    /// </summary>
    private FileStream? AcquireLock(SaveBudget budget, out bool expired)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var time = budget.Time;
        var timeout = lockTimeout ?? DefaultLockTimeout;
        var started = time.GetTimestamp();
        while (true)
        {
            if (budget.IsExpired)
            {
                expired = true;
                return null;
            }

            try
            {
                expired = false;
                return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (time.GetElapsedTime(started) < timeout)
            {
                // Held by another WorkHub process. Never sleep past the budget or the ceiling.
                var left = Min(budget.Remaining, timeout - time.GetElapsedTime(started));
                Thread.Sleep(left <= TimeSpan.Zero ? TimeSpan.Zero : Min(left, LockPollInterval));
            }
            catch (IOException)
            {
                expired = budget.IsExpired; // the budget and the ceiling ran out together: the budget wins
                return null;
            }
        }
    }

    /// <summary>The named object member (case-insensitive), created when missing or not an object.</summary>
    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject(NodeOptions);
        parent[name] = created;
        return created;
    }

    /// <summary>Deep merge: values from <paramref name="source"/> replace or extend <paramref name="target"/>; members that
    /// exist only in <paramref name="target"/> (for example ones this build does not know) are kept.</summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source.ToList())
        {
            if (value is JsonObject sourceChild && target[name] is JsonObject targetChild)
                MergeInto(targetChild, sourceChild);
            else
                target[name] = value?.DeepClone();
        }
    }

    private static JsonObject ToObject(JsonNode? node) =>
        JsonNode.Parse(node!.ToJsonString(), NodeOptions, DocumentOptions)!.AsObject(); // case-insensitive copy

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover temp file never affects settings.json
        }
    }

    /// <summary>An explicit save may replace a file we could not read; keep a copy so hand edits are not lost.</summary>
    /// <param name="force">Keep a copy even when the file parses as JSON (it was found unreadable as settings, e.g. wrong types).</param>
    private void PreserveUnreadableFile(bool force = false)
    {
        if (!File.Exists(settingsFile))
            return;

        try
        {
            if (!force && JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8), NodeOptions, DocumentOptions) is JsonObject)
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
