using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// The Folder Archive / Folder Trash confirmation (Phase 13 proposal revision 4 §7.4). It shows exactly one computed
/// <see cref="LifecycleConfirmation"/>: the Folder, where it is (and is restored to), bounded counts of everything below it, up to
/// three child names and "and N more" — never a subtree listing. Nothing is written by the dialog: the Workspace area commits
/// with this confirmation's token after it closes with the action button. A stale commit shows a NEW dialog of this kind
/// (<see cref="IsStale"/>), which again needs an explicit click; Cancel (the default, also Enter and Esc) discards it.
/// </summary>
public sealed class FolderLifecycleConfirmationViewModel : ObservableObject
{
    public FolderLifecycleConfirmationViewModel(LifecycleConfirmation confirmation, bool isStale)
    {
        Confirmation = confirmation;
        IsStale = isStale;
        ConfirmCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public LifecycleConfirmation Confirmation { get; }

    private bool IsArchive => Confirmation.Kind == FolderLifecycleKind.Archive;

    /// <summary>The subtree changed since the user confirmed: this is the fresh confirmation, and it needs a new click.</summary>
    public bool IsStale { get; }

    public string Title => IsArchive ? "Archive Folder" : "Move Folder to Trash";

    public string ConfirmText => IsArchive ? "Archive" : "Move to Trash";

    public string StaleText => "This folder changed since you confirmed. Review it again; nothing was changed yet.";

    public string Message => IsArchive
        ? $"Archive folder “{Confirmation.Name}” and everything in it? It leaves the tree and is listed under Archived. Projects, links and notes inside keep their own states and stay writable; open note editors stay open. Nothing on disk is touched."
        : $"Move folder “{Confirmation.Name}” and everything in it to the Trash? Everything below it comes back exactly as it is when you restore the folder. Nothing on disk is touched.";

    /// <summary>Where the Folder is now, which is also where Restore and Unarchive put it back (strict: never elsewhere).</summary>
    public string LocationText => $"{(IsArchive ? "In" : "Restores to")}: {Confirmation.Location ?? "Workspace (top level)"}";

    /// <summary>"Contains 2 folders, 5 projects (1 archived, 1 in the Trash), 140 links and 3 notes."</summary>
    public string CountsText
    {
        get
        {
            var counts = Confirmation.Counts;
            if (counts.Folders + counts.Projects == 0)
                return "The folder is empty.";
            var projectStates = new List<string>();
            if (counts.ArchivedProjects > 0)
                projectStates.Add($"{counts.ArchivedProjects} archived");
            if (counts.TrashedProjects > 0)
                projectStates.Add($"{counts.TrashedProjects} in the Trash");
            var projects = Plural(counts.Projects, "project") + (projectStates.Count > 0 ? $" ({string.Join(", ", projectStates)})" : string.Empty);
            var text = $"Contains {Plural(counts.Folders, "folder")}, {projects}, {Plural(counts.Links, "link")} and {Plural(counts.Notes, "note")}.";
            return counts.ItemsInProjectTrash > 0
                ? $"{text} {Plural(counts.ItemsInProjectTrash, "item")} of those projects {(counts.ItemsInProjectTrash == 1 ? "is" : "are")} in their project's Trash."
                : text;
        }
    }

    /// <summary>Up to three direct children, in tree order.</summary>
    public IReadOnlyList<string> ShownChildren => Confirmation.ShownChildren;

    public bool HasShownChildren => Confirmation.ShownChildren.Count > 0;

    /// <summary>"and 4 more" (null when every direct child is shown).</summary>
    public string? MoreText => Confirmation.MoreChildren > 0 ? $"and {Confirmation.MoreChildren} more" : null;

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
