namespace WorkHub.Core.Repositories;

public sealed record SaveSystemResult(long? Id, IReadOnlyList<ValidationError> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public static SaveSystemResult Ok(long id) => new(id, []);

    public static SaveSystemResult Failed(IReadOnlyList<ValidationError> errors) => new(null, errors);

    public static SaveSystemResult Failed(string field, string message) => new(null, [new ValidationError(field, message)]);
}

/// <summary>
/// Application use cases for Systems. The UI calls this; it never builds SQL or touches the store directly.
/// </summary>
public sealed class SystemService(ISystemStore store)
{
    public Task<SystemQueryResult> QueryAsync(SystemQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, SystemQuery.MaxLimit);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        return store.QueryAsync(query with { Limit = limit, Offset = Math.Max(0, query.Offset), Search = search }, cancellationToken);
    }

    public Task<SystemDetails?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        store.GetAsync(id, cancellationToken);

    public async Task<SaveSystemResult> CreateAsync(SystemDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, errors) = SystemDraftValidator.Validate(draft);
        if (normalized is null)
            return SaveSystemResult.Failed(errors);

        try
        {
            return SaveSystemResult.Ok(await store.CreateAsync(normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (DuplicateSystemCodeException)
        {
            return DuplicateCode(normalized.SystemCode);
        }
    }

    public async Task<SaveSystemResult> UpdateAsync(long id, SystemDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, errors) = SystemDraftValidator.Validate(draft);
        if (normalized is null)
            return SaveSystemResult.Failed(errors);

        try
        {
            return await store.UpdateAsync(id, normalized, cancellationToken).ConfigureAwait(false)
                ? SaveSystemResult.Ok(id)
                : SaveSystemResult.Failed(string.Empty, "This System no longer exists.");
        }
        catch (DuplicateSystemCodeException)
        {
            return DuplicateCode(normalized.SystemCode);
        }
    }

    /// <summary>Normal "remove" action: the System stays stored but disappears from default lists.</summary>
    public Task<bool> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default) =>
        store.SetActiveAsync(id, isActive, cancellationToken);

    /// <summary>Secondary action for correcting mistakes. Removes only WorkHub's own rows.</summary>
    public Task<bool> DeletePermanentlyAsync(long id, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(id, cancellationToken);

    public Task<RepositoryUsage> GetUsageAsync(CancellationToken cancellationToken = default) =>
        store.GetUsageAsync(cancellationToken);

    private static SaveSystemResult DuplicateCode(string code) =>
        SaveSystemResult.Failed(SystemDraftValidator.Fields.SystemCode,
            $"System code '{code}' already exists (codes are compared ignoring case).");
}
