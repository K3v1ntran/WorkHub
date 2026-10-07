using System.Text;
using WorkHub.Core.Repositories;

namespace WorkHub.Core.Owner;

/// <summary>Owner group name rules.</summary>
public static class OwnerGroupName
{
    public const int MaxLength = 100;

    public static bool TryNormalize(string? raw, out string name, out string? error)
    {
        name = (raw ?? string.Empty).Trim();
        error = null;
        if (name.Length == 0)
            error = "Group name is required.";
        else if (name.Length > MaxLength)
            error = $"Group name must be at most {MaxLength} characters.";
        else if (name.Any(char.IsControl))
            error = "Group name must not contain control characters.";
        return error is null;
    }

    /// <summary>
    /// Uniqueness key stored in <c>OwnerGroups.NameKey</c> (UNIQUE). SQLite NOCASE folds ASCII only, so the key is
    /// computed here: NFKC (folds full-width / half-width forms) then invariant upper-casing (all scripts).
    /// "Tools", "TOOLS" and "ＴＯＯＬＳ" therefore collide; the stored display name keeps the user's spelling.
    /// </summary>
    public static string Key(string normalizedName) =>
        normalizedName.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}

/// <summary>Target syntax rules per action type. Existence is never checked here (targets may be temporarily offline).</summary>
public static class OwnerTargetRules
{
    public const int MaxTargetLength = 2048;

    /// <summary>http/https only, absolute, with a host, no embedded user name or password, no whitespace.</summary>
    public static bool IsSafeWebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
               && string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// A fully-qualified local or UNC path (C:\x, \\server\share\x) with no characters Windows forbids in paths.
    /// URLs and shell/protocol strings (cmd:, powershell:, file://) are not paths and are rejected.
    /// </summary>
    public static bool IsLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal))
            return false;
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        if (!Path.IsPathFullyQualified(value))
            return false;

        // ':' is only legal as the drive separator (C:\...).
        var afterRoot = value.Length > 2 && value[1] == ':' ? value[2..] : value;
        return afterRoot.IndexOfAny(['<', '>', '"', '|', '*', '?', ':']) < 0;
    }

    public static bool IsExecutablePath(string? value) =>
        IsLocalPath(value) && value!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Validates and normalizes an <see cref="OwnerItemDraft"/>. Notes and arguments are kept as typed.</summary>
public static class OwnerItemValidator
{
    public const int MaxNameLength = 200;
    public const int MaxArgumentsLength = 2000;
    public const int MaxUsernameLength = 200;
    public const int MaxNoteLength = 4000;

    public static class Fields
    {
        public const string Name = nameof(Name);
        public const string ActionType = nameof(ActionType);
        public const string Target = nameof(Target);
        public const string Arguments = nameof(Arguments);
        public const string WorkingDirectory = nameof(WorkingDirectory);
        public const string Username = nameof(Username);
        public const string Note = nameof(Note);
    }

    public static (OwnerItemDraft? Draft, IReadOnlyList<ValidationError> Errors) Validate(OwnerItemDraft draft)
    {
        var errors = new List<ValidationError>();

        var name = (draft.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            errors.Add(new(Fields.Name, "Name is required."));
        else if (name.Length > MaxNameLength)
            errors.Add(new(Fields.Name, $"Name must be at most {MaxNameLength} characters."));

        var type = draft.ActionType;
        if (!Enum.IsDefined(type))
            errors.Add(new(Fields.ActionType, "Choose a type: Web, File, Folder or Executable."));

        var target = (draft.Target ?? string.Empty).Trim();
        if (target.Length == 0)
            errors.Add(new(Fields.Target, "Target is required."));
        else if (target.Length > OwnerTargetRules.MaxTargetLength)
            errors.Add(new(Fields.Target, $"Target must be at most {OwnerTargetRules.MaxTargetLength} characters."));
        else if (Enum.IsDefined(type) && TargetError(type, target) is { } targetError)
            errors.Add(new(Fields.Target, targetError));

        string? arguments = null, workingDirectory = null;
        if (type == OwnerActionType.Executable)
        {
            arguments = string.IsNullOrWhiteSpace(draft.Arguments) ? null : draft.Arguments.Trim();
            if (arguments is not null && arguments.Any(char.IsControl))
                errors.Add(new(Fields.Arguments, "Arguments must be a single line."));
            else if (arguments?.Length > MaxArgumentsLength)
                errors.Add(new(Fields.Arguments, $"Arguments must be at most {MaxArgumentsLength} characters."));

            workingDirectory = string.IsNullOrWhiteSpace(draft.WorkingDirectory) ? null : draft.WorkingDirectory.Trim();
            if (workingDirectory is not null && !OwnerTargetRules.IsLocalPath(workingDirectory))
                errors.Add(new(Fields.WorkingDirectory, @"Working directory must be a full folder path, e.g. C:\Tools."));
        }

        var username = string.IsNullOrWhiteSpace(draft.Username) ? null : draft.Username.Trim();
        if (username is not null && username.Any(char.IsControl))
            errors.Add(new(Fields.Username, "User name must be a single line."));
        else if (username?.Length > MaxUsernameLength)
            errors.Add(new(Fields.Username, $"User name must be at most {MaxUsernameLength} characters."));

        var note = string.IsNullOrWhiteSpace(draft.Note) ? null : draft.Note;
        if (note?.Length > MaxNoteLength)
            errors.Add(new(Fields.Note, $"Note must be at most {MaxNoteLength} characters."));

        return errors.Count > 0
            ? (null, errors)
            : (new OwnerItemDraft(name, type, target, arguments, workingDirectory, username, note), errors);
    }

    private static string? TargetError(OwnerActionType type, string target) => type switch
    {
        OwnerActionType.Web when !OwnerTargetRules.IsSafeWebUrl(target) =>
            "Enter a full web address starting with http:// or https:// (without a user name or password in it).",
        OwnerActionType.File when !OwnerTargetRules.IsLocalPath(target) =>
            @"Enter the full path of a file, e.g. C:\Docs\report.xlsx or \\server\share\report.xlsx.",
        OwnerActionType.Folder when !OwnerTargetRules.IsLocalPath(target) =>
            @"Enter the full path of a folder, e.g. C:\Projects or \\server\share\Projects.",
        OwnerActionType.Executable when !OwnerTargetRules.IsExecutablePath(target) =>
            @"Enter the full path of a program (.exe), e.g. C:\Tools\tool.exe.",
        _ => null,
    };
}
