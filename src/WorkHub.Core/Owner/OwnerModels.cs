using WorkHub.Core.Repositories;

namespace WorkHub.Core.Owner;

/// <summary>
/// How an Owner launcher entry is opened. Fixed application behaviour, persisted by name
/// (no lookup table). Owner targets are used directly; they never go through repository target resolution.
/// </summary>
public enum OwnerActionType
{
    Web,
    File,
    Folder,
    Executable,
}

/// <summary>A user-defined Owner group as shown in the sidebar.</summary>
public sealed record OwnerGroup(long Id, string Name, int SortOrder);

/// <summary>One row of an Owner group's launcher list. Flat and small: details are loaded only for edit/open.</summary>
public sealed record OwnerItemListItem(long Id, long GroupId, string Name, OwnerActionType ActionType, string Target);

public sealed record OwnerItemDetails(
    long Id,
    long GroupId,
    string Name,
    OwnerActionType ActionType,
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? Username,
    string? CredentialId,
    string? Note,
    int SortOrder,
    bool IsFavorite,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// User-editable fields of an Owner item. There is deliberately no password, CredentialId or IsFavorite here:
/// CredentialId belongs to the future vault phase and is never written by Phase 3 code paths.
/// </summary>
public sealed record OwnerItemDraft(
    string Name,
    OwnerActionType ActionType,
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null,
    string? Username = null,
    string? Note = null);

public sealed record OwnerSaveResult(long? Id, IReadOnlyList<ValidationError> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public static OwnerSaveResult Ok(long id) => new(id, []);

    public static OwnerSaveResult Failed(IReadOnlyList<ValidationError> errors) => new(null, errors);

    public static OwnerSaveResult Failed(string field, string message) => new(null, [new ValidationError(field, message)]);
}
