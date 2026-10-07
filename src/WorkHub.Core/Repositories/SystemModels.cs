namespace WorkHub.Core.Repositories;

/// <summary>
/// Bounded list query for one fixed repository group. Paging fields exist so later infinite scroll / paging
/// needs no API change.
/// </summary>
public sealed record SystemQuery(
    RepositoryType Type,
    string? Search = null,
    bool IncludeInactive = false,
    int Limit = SystemQuery.DefaultLimit,
    int Offset = 0)
{
    public const int DefaultLimit = 1000;
    public const int MaxLimit = 5000;
}

/// <summary>One row of a repository group list. Immutable and flat: no object graph.</summary>
public sealed record SystemListItem(
    long Id,
    string SystemCode,
    string SystemName,
    bool IsActive,
    string? CustomTarget)
{
    public bool HasCustomTarget => CustomTarget is not null;
}

public sealed record SystemQueryResult(IReadOnlyList<SystemListItem> Items, bool HasMore);

public sealed record MembershipDetails(RepositoryType Type, bool IsEnabled, string? CustomTarget);

public sealed record SystemDetails(
    long Id,
    string SystemCode,
    string SystemName,
    string? Description,
    bool IsActive,
    IReadOnlyList<MembershipDetails> Memberships,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One membership row to persist. A disabled row is kept (rather than deleted) so its custom target survives
/// being unticked; types absent from the draft have their rows removed.
/// </summary>
public sealed record MembershipDraft(RepositoryType Type, bool IsEnabled, string? CustomTarget = null);

/// <summary>Complete desired state of a System and its memberships.</summary>
public sealed record SystemDraft(
    string SystemCode,
    string SystemName,
    string? Description,
    bool IsActive,
    IReadOnlyList<MembershipDraft> Memberships);

/// <summary>
/// Which repository types are in use by active Systems with enabled memberships.
/// Used to decide which settings are actually required.
/// </summary>
public sealed record RepositoryUsage(
    IReadOnlySet<RepositoryType> TypesInUse,
    IReadOnlySet<RepositoryType> TypesUsingGeneratedTargets)
{
    public static RepositoryUsage None { get; } = new(new HashSet<RepositoryType>(), new HashSet<RepositoryType>());
}
