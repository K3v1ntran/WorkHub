using WorkHub.Core.Owner;

namespace WorkHub.Core.Workspace.Content;

/// <summary>
/// Validates and normalizes a <see cref="ProjectLinkDraft"/>. Name follows <see cref="WorkspaceName"/>. Target: only
/// outer whitespace is trimmed; nothing else is rewritten (no case folding, no path resolution, no separator or URL
/// re-encoding). Syntax rules are the existing Owner target rules; existence is never checked here.
/// </summary>
public static class ProjectLinkValidator
{
    public const int MaxTargetLength = OwnerTargetRules.MaxTargetLength;

    public static class Fields
    {
        public const string Name = nameof(Name);
        public const string Type = nameof(Type);
        public const string Target = nameof(Target);
    }

    public static (ProjectLinkDraft? Draft, string? Field, string? Error) Validate(ProjectLinkDraft draft)
    {
        if (!WorkspaceName.TryNormalize(draft.Name, out var name, out var nameError))
            return (null, Fields.Name, nameError);

        if (!Enum.IsDefined(draft.Type))
            return (null, Fields.Type, "Choose a type: Folder, File or Web.");

        var target = (draft.Target ?? string.Empty).Trim();
        if (target.Length == 0)
            return (null, Fields.Target, "Target is required.");
        if (target.Length > MaxTargetLength)
            return (null, Fields.Target, $"Target must be at most {MaxTargetLength} characters.");
        if (TargetError(draft.Type, target) is { } targetError)
            return (null, Fields.Target, targetError);

        return (new ProjectLinkDraft(name, draft.Type, target), null, null);
    }

    /// <summary>Null when the (already trimmed) target is syntactically valid for its type.</summary>
    public static string? TargetError(ProjectLinkType type, string target) => type switch
    {
        ProjectLinkType.Web when !OwnerTargetRules.IsSafeWebUrl(target) =>
            "Enter a full web address starting with http:// or https:// (without a user name or password in it).",
        ProjectLinkType.File when !OwnerTargetRules.IsLocalPath(target) =>
            @"Enter the full path of a file, e.g. C:\Docs\report.xlsx or \\server\share\report.xlsx.",
        ProjectLinkType.Folder when !OwnerTargetRules.IsLocalPath(target) =>
            @"Enter the full path of a folder, e.g. C:\Projects or \\server\share\Projects.",
        _ => null,
    };
}
