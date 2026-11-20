using System.Globalization;
using System.Text;

namespace WorkHub.Core.Workspace;

/// <summary>
/// Names of Workspace Folders and Projects (one shared sibling namespace).
/// The display name is stored as typed after trimming; nothing else is changed (Unicode, casing and spelling kept).
/// </summary>
public static class WorkspaceName
{
    public const int MaxLength = 200;

    public static bool TryNormalize(string? raw, out string name, out string? error)
    {
        name = (raw ?? string.Empty).Trim();
        error = null;
        if (name.Length == 0)
            error = "Name is required.";
        else if (name.Length > MaxLength)
            error = $"Name must be at most {MaxLength} characters.";
        else if (name.Any(char.IsControl))
            error = "Name must not contain control characters.";
        else if (!IsWellFormedUtf16(name))
            error = "Name contains invalid characters.";
        return error is null;
    }

    /// <summary>
    /// Sibling uniqueness key stored in <c>WorkspaceNodes.NameKey</c>: NFKC, then invariant upper case.
    /// "Alpha", "ALPHA" and "ＡＬＰＨＡ" collide; hiragana and katakana stay distinct. NFKC may expand text
    /// (U+337F "㍿" becomes "株式会社"), so the key has no length limit of its own.
    /// </summary>
    public static string Key(string normalizedName) =>
        normalizedName.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    internal static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                i++;
            else if (char.IsSurrogate(value[i]))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Project tag names (Phase 17, proposal R2b §4.2): trimmed with <see cref="string.Trim()"/> (all Unicode white space),
/// 1–40 UTF-16 characters, well-formed UTF-16 and no control character (<see cref="char.IsControl(char)"/>: C0, DEL, C1 —
/// the same set the V010 CHECKs reject). Spelling, case and Unicode text are kept. The key is <see cref="WorkspaceName.Key"/>.
/// </summary>
public static class ProjectTagName
{
    public const int MaxLength = 40;

    public static bool TryNormalize(string? raw, out string name, out string? error)
    {
        name = (raw ?? string.Empty).Trim();
        error = null;
        if (name.Length == 0)
            error = "A tag needs a name.";
        else if (name.Length > MaxLength)
            error = $"A tag must be at most {MaxLength} characters.";
        else if (name.Any(char.IsControl))
            error = "A tag must not contain control characters.";
        else if (!WorkspaceName.IsWellFormedUtf16(name))
            error = "A tag contains invalid characters.";
        return error is null;
    }

    /// <summary>The tag key stored in <c>ProjectTags.NameKey</c> (same as the Workspace name key).</summary>
    public static string Key(string normalizedName) => WorkspaceName.Key(normalizedName);
}

/// <summary>Rules for a Project's set of tags (Phase 17).</summary>
public static class ProjectTagRules
{
    public const int MaxPerProject = 20;

    public const string TooManyMessage = "At most 20 tags.";

    /// <summary>Display order: key ordinal, then spelling ordinal.</summary>
    public static int Compare(string nameX, string keyX, string nameY, string keyY)
    {
        var byKey = string.CompareOrdinal(keyX, keyY);
        return byKey != 0 ? byKey : string.CompareOrdinal(nameX, nameY);
    }

    /// <summary>
    /// Normalizes a complete set for storage: every name valid, no two with the same key, at most <see cref="MaxPerProject"/>.
    /// The editor merges duplicates before it sends a set, so a duplicate here is refused (a programming error), never merged.
    /// Returns null on success with the (Name, NameKey) pairs in display order.
    /// </summary>
    public static string? NormalizeSet(IReadOnlyList<string> names, out IReadOnlyList<(string Name, string NameKey)> normalized)
    {
        normalized = [];
        var result = new List<(string Name, string NameKey)>(names.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            if (!ProjectTagName.TryNormalize(raw, out var name, out var error))
                return error;
            var key = ProjectTagName.Key(name);
            if (!keys.Add(key))
                return $"The tag “{name}” is listed twice.";
            result.Add((name, key));
        }

        if (result.Count > MaxPerProject)
            return TooManyMessage;
        result.Sort((x, y) => Compare(x.Name, x.NameKey, y.Name, y.NameKey));
        normalized = result;
        return null;
    }
}

/// <summary>Validates and normalizes a <see cref="ProjectDraft"/>. Blank optional text becomes NULL.</summary>
public static class ProjectDraftValidator
{
    public const int MaxDescriptionLength = 4000;
    public const int MaxPersonLength = 100;

    public static class Fields
    {
        public const string Name = nameof(Name);
        public const string Status = nameof(Status);
        public const string Description = nameof(Description);
        public const string Owner = nameof(Owner);
        public const string ProjectManager = nameof(ProjectManager);
        public const string Tags = nameof(Tags);
    }

    public static (ProjectDraft? Draft, string? Field, string? Error) Validate(ProjectDraft draft)
    {
        if (!WorkspaceName.TryNormalize(draft.Name, out var name, out var nameError))
            return (null, Fields.Name, nameError);

        if (!ProjectStatuses.IsEditable(draft.Status))
            return (null, Fields.Status, "Choose a status: Planning, Active, Maintenance, On Hold or Completed.");

        // Description keeps its line breaks and inner spacing; only blank text becomes NULL.
        var description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description;
        if (description?.Length > MaxDescriptionLength)
            return (null, Fields.Description, $"Description must be at most {MaxDescriptionLength} characters.");

        if (PersonError(draft.Owner, "Owner", out var owner) is { } ownerError)
            return (null, Fields.Owner, ownerError);
        if (PersonError(draft.ProjectManager, "Project manager", out var manager) is { } managerError)
            return (null, Fields.ProjectManager, managerError);

        IReadOnlyList<string>? tags = null;
        if (draft.Tags is not null)
        {
            if (ProjectTagRules.NormalizeSet(draft.Tags, out var normalized) is { } tagError)
                return (null, Fields.Tags, tagError);
            tags = normalized.Select(t => t.Name).ToList();
        }

        // Phase 21 R2a: the baseline travels with the tags it guards (without tags there is nothing to guard).
        var baseline = tags is null ? null : draft.TagsBaseline;
        return (new ProjectDraft(name, draft.Status, description, owner, manager, tags, baseline), null, null);
    }

    private static string? PersonError(string? raw, string label, out string? value)
    {
        value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (value is null)
            return null;
        if (value.Length > MaxPersonLength)
            return $"{label} must be at most {MaxPersonLength} characters.";
        if (value.Any(char.IsControl))
            return $"{label} must be a single line.";
        return null;
    }
}

/// <summary>Tree order: Folders before Projects, then name (culture-aware, ignoring case and width), then Id.</summary>
public sealed class WorkspaceNodeOrder : IComparer<(WorkspaceNodeType Type, string Name, long Id)>
{
    public static readonly WorkspaceNodeOrder Instance = new();

    private static readonly CompareInfo Culture = CultureInfo.InvariantCulture.CompareInfo;

    public int Compare((WorkspaceNodeType Type, string Name, long Id) x, (WorkspaceNodeType Type, string Name, long Id) y)
    {
        var byType = x.Type.CompareTo(y.Type);
        if (byType != 0)
            return byType;

        var byName = Culture.Compare(x.Name, y.Name, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);
        if (byName != 0)
            return byName;

        var ordinal = string.CompareOrdinal(x.Name, y.Name);
        return ordinal != 0 ? ordinal : x.Id.CompareTo(y.Id);
    }
}
