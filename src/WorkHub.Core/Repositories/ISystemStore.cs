namespace WorkHub.Core.Repositories;

/// <summary>
/// Persistence for Systems and their repository memberships. Implementations run their I/O off the caller's
/// thread and apply every multi-row change in a single transaction.
/// </summary>
public interface ISystemStore
{
    Task<SystemQueryResult> QueryAsync(SystemQuery query, CancellationToken cancellationToken = default);

    Task<SystemDetails?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Inserts the System and all memberships atomically.</summary>
    /// <exception cref="DuplicateSystemCodeException">The code already exists (case-insensitively).</exception>
    Task<long> CreateAsync(SystemDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Updates the System and replaces its memberships atomically. Returns false when the System no longer exists.</summary>
    /// <exception cref="DuplicateSystemCodeException">The new code belongs to another System.</exception>
    Task<bool> UpdateAsync(long id, SystemDraft draft, CancellationToken cancellationToken = default);

    Task<bool> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes the System; its memberships cascade. External repositories are untouched.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    Task<RepositoryUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}

public sealed class DuplicateSystemCodeException(string systemCode, Exception? inner = null)
    : Exception($"System code '{systemCode}' already exists.", inner)
{
    public string SystemCode { get; } = systemCode;
}
