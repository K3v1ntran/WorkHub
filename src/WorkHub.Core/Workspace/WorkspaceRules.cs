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

    private static bool IsWellFormedUtf16(string value)
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

        return (new ProjectDraft(name, draft.Status, description, owner, manager), null, null);
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
