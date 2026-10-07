namespace WorkHub.Core.Owner;

/// <summary>
/// Kinds of user-defined Owner launcher items. Persisted by name; credentials are never stored on items.
/// </summary>
public enum OwnerItemType
{
    Web,
    File,
    Folder,
    Executable,
}
