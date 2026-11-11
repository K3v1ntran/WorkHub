using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>One entry row of the purge confirmation: the same values the Trash list shows, plus "inside …" for a dependent.</summary>
public sealed class PurgeConfirmationRowViewModel(PurgeEntryView entry, string? containingName)
{
    public PurgeEntryView Entry { get; } = entry;

    public string Name => Entry.RootName;

    public string KindText => Entry.Kind switch
    {
        ContentKind.Folder => "Folder",
        ContentKind.Link => "Link",
        _ => "Note",
    };

    public string ItemsText => Entry.ItemCount == 1 ? "1 item" : $"{Entry.ItemCount} items";

    public string TrashedAtText => $"{Entry.TrashedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    public string FromText => Entry.FromTrashed ? $"from {Entry.From.Display} (in the Trash)" : $"from {Entry.From.Display}";

    /// <summary>"inside “Drafts”" for an entry trashed separately inside another entry's folder; null otherwise.</summary>
    public string? InsideText => containingName is null ? null : $"inside “{containingName}”";

    /// <summary>Nesting level for dependents (0 for a listed root).</summary>
    public int Depth => Entry.Depth;
}

/// <summary>
/// Phase 14 confirmation of a permanent deletion. It shows exactly ONE ready <see cref="PurgePreview"/>; the commit is checked
/// against that preview's token, so every value shown here is a value the token covers. Cancel is the default button (also
/// Enter and Esc); nothing is written by the dialog. A stale commit is never retried or reopened automatically.
/// </summary>
public sealed class PurgeConfirmationViewModel : ObservableObject
{
    public PurgeConfirmationViewModel(PurgePreview preview)
    {
        Preview = preview;
        var names = preview.Entries.ToDictionary(e => e.EntryId, e => e.RootName);
        Rows = preview.Entries
            .Select(e => new PurgeConfirmationRowViewModel(e, e.Depth > 0 && e.ContainingEntryId is { } outer && names.TryGetValue(outer, out var name) ? name : null))
            .ToList();
        ConfirmCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public PurgePreview Preview { get; }

    private bool IsEntireTrash => Preview.Request.Mode == PurgeMode.EntireTrash;

    public string Title => IsEntireTrash ? $"Empty the Trash of “{Preview.ProjectName}”?" : "Delete permanently?";

    public string ConfirmText => "Delete permanently";

    /// <summary>The Project, with "(archived)" when its effective state is Archived.</summary>
    public string ProjectText => Preview.EffectiveState == LifecycleState.Archived ? $"{Preview.ProjectName} (archived)" : Preview.ProjectName;

    public IReadOnlyList<PurgeConfirmationRowViewModel> Rows { get; }

    /// <summary>"3 entries: 2 folders, 1 link, 4 notes."</summary>
    public string TotalsText
    {
        get
        {
            var c = Preview.Counts;
            return $"{Plural(c.Entries, "entry", "entries")}: {Plural(c.Folders, "folder", "folders")}, {Plural(c.Links, "link", "links")}, {Plural(c.Notes, "note", "notes")}.";
        }
    }

    public bool HasDependents => Rows.Any(r => r.InsideText is not null);

    public string DependentsText => "Items that were moved to the Trash separately from inside a folder listed here are deleted with that folder.";

    /// <summary>What the deletion does and does not do (Revision 2 §5.4, §5.8). No secure-erasure wording.</summary>
    public string ExplanationText =>
        "These items will be removed from WorkHub's database. This cannot be undone in WorkHub. " +
        "WorkHub first makes a copy of the whole database in Backups\\PreDelete. That copy and your other backups still contain these items. " +
        "Restoring a backup returns the whole database to that moment, not just these items. " +
        "Files, folders and web pages that links point to are not touched.";

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
