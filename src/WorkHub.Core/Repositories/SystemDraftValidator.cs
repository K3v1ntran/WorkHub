namespace WorkHub.Core.Repositories;

public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Normalizes and validates a <see cref="SystemDraft"/> before persistence. The database constraints remain the
/// final guarantee; this produces friendly, field-specific messages.
/// </summary>
public static class SystemDraftValidator
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;

    public static class Fields
    {
        public const string SystemCode = nameof(SystemCode);
        public const string SystemName = nameof(SystemName);
        public const string Description = nameof(Description);
        public const string Memberships = nameof(Memberships);

        public static string CustomTarget(RepositoryType type) => $"CustomTarget.{type}";
    }

    public static (SystemDraft? Draft, IReadOnlyList<ValidationError> Errors) Validate(SystemDraft draft)
    {
        var errors = new List<ValidationError>();

        if (!SystemCode.TryNormalize(draft.SystemCode, out var code, out var codeError))
            errors.Add(new(Fields.SystemCode, codeError!));

        var name = (draft.SystemName ?? string.Empty).Trim();
        if (name.Length == 0)
            errors.Add(new(Fields.SystemName, "System name is required."));
        else if (name.Length > MaxNameLength)
            errors.Add(new(Fields.SystemName, $"System name must be at most {MaxNameLength} characters."));

        var description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        if (description?.Length > MaxDescriptionLength)
            errors.Add(new(Fields.Description, $"Description must be at most {MaxDescriptionLength} characters."));

        var memberships = new List<MembershipDraft>();
        foreach (var group in draft.Memberships.GroupBy(m => m.Type))
        {
            if (group.Count() > 1)
            {
                errors.Add(new(Fields.Memberships, $"{RepositoryCatalog.Get(group.Key).DisplayName} is listed more than once."));
                continue;
            }

            var membership = group.Single();
            var info = RepositoryCatalog.Get(membership.Type);
            var custom = string.IsNullOrWhiteSpace(membership.CustomTarget) ? null : membership.CustomTarget.Trim();
            if (custom is not null && !RepositoryUrlRules.IsValidTarget(custom, info.VersionControl))
            {
                errors.Add(new(Fields.CustomTarget(membership.Type),
                    $"Custom target for {info.DisplayName} must be a full URL ({RepositoryUrlRules.DescribeSchemes(info.VersionControl)})."));
            }

            memberships.Add(membership with { CustomTarget = custom });
        }

        if (!memberships.Any(m => m.IsEnabled))
            errors.Add(new(Fields.Memberships, "Select at least one repository."));

        if (errors.Count > 0)
            return (null, errors);

        var ordered = memberships.OrderBy(m => m.Type).ToList();
        return (new SystemDraft(code, name, description, draft.IsActive, ordered), errors);
    }
}
