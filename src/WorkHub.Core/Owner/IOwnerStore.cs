namespace WorkHub.Core.Owner;

/// <summary>
/// Persistence for Owner groups and items. Implementations run I/O off the caller's thread and make every
/// multi-row change (create with ordering, move, reorder, delete with items) atomic.
/// Nothing here ever touches the external targets the items point to.
/// </summary>
public interface IOwnerStore
{
    Task<IReadOnlyList<OwnerGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateOwnerGroupNameException"/>
    Task<OwnerGroup> CreateGroupAsync(string name, CancellationToken cancellationToken = default);

    /// <returns>False when the group no longer exists.</returns>
    /// <exception cref="DuplicateOwnerGroupNameException"/>
    Task<bool> RenameGroupAsync(long groupId, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the group and (by ownership cascade) its items, in one statement.</summary>
    Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default);

    Task<int> CountItemsAsync(long groupId, CancellationToken cancellationToken = default);

    /// <summary>Moves a group by <paramref name="offset"/> positions (clamped). Returns false if nothing moved.</summary>
    Task<bool> MoveGroupAsync(long groupId, int offset, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerItemListItem>> ListItemsAsync(long groupId, CancellationToken cancellationToken = default);

    Task<OwnerItemDetails?> GetItemAsync(long itemId, CancellationToken cancellationToken = default);

    /// <summary>Appends the item at the end of the group. CredentialId is NULL and IsFavorite is false.</summary>
    /// <exception cref="OwnerGroupNotFoundException"/>
    Task<long> CreateItemAsync(long groupId, OwnerItemDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Updates the user-editable fields only (not group, order, favorite flag or CredentialId).</summary>
    Task<bool> UpdateItemAsync(long itemId, OwnerItemDraft draft, CancellationToken cancellationToken = default);

    Task<bool> DeleteItemAsync(long itemId, CancellationToken cancellationToken = default);

    /// <summary>Moves the item (same Id) to the end of another group.</summary>
    /// <exception cref="OwnerGroupNotFoundException"/>
    Task<bool> MoveItemToGroupAsync(long itemId, long targetGroupId, CancellationToken cancellationToken = default);

    /// <summary>Moves an item within its group by <paramref name="offset"/> positions (clamped).</summary>
    Task<bool> MoveItemAsync(long itemId, int offset, CancellationToken cancellationToken = default);
}

public sealed class DuplicateOwnerGroupNameException(string name, Exception? inner = null)
    : Exception($"An Owner group named '{name}' already exists.", inner)
{
    public string Name { get; } = name;
}

public sealed class OwnerGroupNotFoundException(long groupId)
    : Exception($"Owner group {groupId} does not exist.")
{
    public long GroupId { get; } = groupId;
}
