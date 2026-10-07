namespace WorkHub.Core.Owner;

/// <summary>
/// Owner Manager use cases. The UI calls only this; it never touches SQL, paths or processes directly.
/// Owner is independent from Repository: targets are used as entered, never resolved from host/prefix rules.
/// </summary>
public sealed class OwnerService(IOwnerStore store, IOwnerLauncher launcher)
{
    public const string GroupNameField = "Name";

    public Task<IReadOnlyList<OwnerGroup>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        store.ListGroupsAsync(cancellationToken);

    public async Task<(OwnerGroup? Group, string? Error)> CreateGroupAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (!OwnerGroupName.TryNormalize(name, out var normalized, out var error))
            return (null, error);

        try
        {
            return (await store.CreateGroupAsync(normalized, cancellationToken).ConfigureAwait(false), null);
        }
        catch (DuplicateOwnerGroupNameException)
        {
            return (null, DuplicateName(normalized));
        }
    }

    /// <returns>Null on success, otherwise a user-facing error.</returns>
    public async Task<string?> RenameGroupAsync(long groupId, string? name, CancellationToken cancellationToken = default)
    {
        if (!OwnerGroupName.TryNormalize(name, out var normalized, out var error))
            return error;

        try
        {
            return await store.RenameGroupAsync(groupId, normalized, cancellationToken).ConfigureAwait(false)
                ? null
                : "This group no longer exists.";
        }
        catch (DuplicateOwnerGroupNameException)
        {
            return DuplicateName(normalized);
        }
    }

    public Task<int> CountItemsAsync(long groupId, CancellationToken cancellationToken = default) =>
        store.CountItemsAsync(groupId, cancellationToken);

    /// <summary>Removes the group and its launcher entries from WorkHub only; external targets are untouched.</summary>
    public Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default) =>
        store.DeleteGroupAsync(groupId, cancellationToken);

    public Task<bool> MoveGroupAsync(long groupId, int offset, CancellationToken cancellationToken = default) =>
        store.MoveGroupAsync(groupId, offset, cancellationToken);

    public Task<IReadOnlyList<OwnerItemListItem>> ListItemsAsync(long groupId, CancellationToken cancellationToken = default) =>
        store.ListItemsAsync(groupId, cancellationToken);

    public Task<OwnerItemDetails?> GetItemAsync(long itemId, CancellationToken cancellationToken = default) =>
        store.GetItemAsync(itemId, cancellationToken);

    public async Task<OwnerSaveResult> CreateItemAsync(long groupId, OwnerItemDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, errors) = OwnerItemValidator.Validate(draft);
        if (normalized is null)
            return OwnerSaveResult.Failed(errors);

        try
        {
            return OwnerSaveResult.Ok(await store.CreateItemAsync(groupId, normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (OwnerGroupNotFoundException)
        {
            return OwnerSaveResult.Failed(string.Empty, "The group no longer exists.");
        }
    }

    public async Task<OwnerSaveResult> UpdateItemAsync(long itemId, OwnerItemDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, errors) = OwnerItemValidator.Validate(draft);
        if (normalized is null)
            return OwnerSaveResult.Failed(errors);

        return await store.UpdateItemAsync(itemId, normalized, cancellationToken).ConfigureAwait(false)
            ? OwnerSaveResult.Ok(itemId)
            : OwnerSaveResult.Failed(string.Empty, "This launcher entry no longer exists.");
    }

    /// <summary>Removes only the WorkHub entry; the file/folder/program/website is never touched.</summary>
    public Task<bool> DeleteItemAsync(long itemId, CancellationToken cancellationToken = default) =>
        store.DeleteItemAsync(itemId, cancellationToken);

    public async Task<bool> MoveItemToGroupAsync(long itemId, long targetGroupId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.MoveItemToGroupAsync(itemId, targetGroupId, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerGroupNotFoundException)
        {
            return false;
        }
    }

    public Task<bool> MoveItemAsync(long itemId, int offset, CancellationToken cancellationToken = default) =>
        store.MoveItemAsync(itemId, offset, cancellationToken);

    /// <summary>Loads the current item and launches it; existence checks and process start run off the caller's thread.</summary>
    public async Task<OwnerLaunchResult> OpenAsync(long itemId, CancellationToken cancellationToken = default)
    {
        var item = await store.GetItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return new OwnerLaunchResult(OwnerLaunchProblem.ItemNotFound, "This launcher entry no longer exists.");

        return await Task.Run(() => launcher.Launch(item), cancellationToken).ConfigureAwait(false);
    }

    private static string DuplicateName(string name) =>
        $"A group named '{name}' already exists (names are compared ignoring case).";
}
