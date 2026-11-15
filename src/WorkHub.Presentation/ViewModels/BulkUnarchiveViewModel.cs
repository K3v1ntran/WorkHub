using System.ComponentModel;
using System.Globalization;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Phase 20 "Unarchive projects…" checklist (proposal Revision 2 + R2a, §4.1): the archived Project roots the Archived panel lists,
/// read when the command starts, each carrying the <c>ArchiveSerial</c> read with it (D9(a)). Nothing is ticked (D3); one status
/// for every ticked Project, Completed preselected (D4); at most <see cref="BulkUnarchiveCandidates.MaxRows"/> rows (D8). Cancel is
/// the default; Enter and Esc never unarchive. Nothing is written by the dialog itself.
/// </summary>
public sealed class BulkUnarchiveViewModel : ObservableObject
{
    private ProjectStatusOption _status;

    public BulkUnarchiveViewModel(IReadOnlyList<BulkUnarchiveCandidate> projects, bool truncated)
    {
        Rows = projects.Select(p => new BulkUnarchiveRowViewModel(p)).ToList();
        foreach (var row in Rows)
            row.PropertyChanged += OnRowChanged;
        TruncatedText = truncated
            ? $"Only the first {BulkUnarchiveCandidates.MaxRows} archived projects are listed. Unarchive these, then open the dialog again for the rest."
            : null;
        _status = Statuses.First(s => s.Status == ProjectStatus.Completed);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        UnarchiveCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true), () => CheckedCount > 0);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public string Title => "Unarchive projects";

    public string Message =>
        "Unarchive the ticked projects. Each returns to its current folder with the status you choose. A project that was unarchived or archived again elsewhere is left as it is; nothing on disk is touched.";

    public IReadOnlyList<BulkUnarchiveRowViewModel> Rows { get; }

    /// <summary>Shown when more archived Projects exist than the row limit (D8).</summary>
    public string? TruncatedText { get; }

    /// <summary>The five editable statuses (never Archived), as in the single Unarchive dialog.</summary>
    public IReadOnlyList<ProjectStatusOption> Statuses { get; } =
        ProjectStatuses.Editable.Select(s => new ProjectStatusOption(s, ProjectStatuses.DisplayName(s))).ToList();

    /// <summary>The one status every ticked Project gets (D4).</summary>
    public ProjectStatusOption Status { get => _status; set => SetProperty(ref _status, value); }

    public int CheckedCount => Rows.Count(r => r.IsChecked);

    /// <summary>The live count on the action button: "Unarchive 3 projects".</summary>
    public string UnarchiveText => CheckedCount == 1 ? "Unarchive 1 project" : $"Unarchive {CheckedCount} projects";

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand UnarchiveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>The ticked rows in list order, each bound to the serial read with it (R2-1).</summary>
    public IReadOnlyList<BulkUnarchiveTarget> ConfirmedTargets =>
        Rows.Where(r => r.IsChecked).Select(r => new BulkUnarchiveTarget(r.NodeId, r.ArchiveSerial)).ToList();

    /// <summary>The names as read when the dialog opened (the summary uses them).</summary>
    public IReadOnlyDictionary<long, string> NamesById => Rows.ToDictionary(r => r.NodeId, r => r.Name);

    /// <summary>Space on the focused row (the window forwards it).</summary>
    public void Toggle(BulkUnarchiveRowViewModel? row)
    {
        if (row is not null)
            row.IsChecked = !row.IsChecked;
    }

    private void SetAll(bool value)
    {
        foreach (var row in Rows)
            row.IsChecked = value;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BulkUnarchiveRowViewModel.IsChecked))
            return;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(UnarchiveText));
        UnarchiveCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One row of the Bulk Unarchive checklist. The serial is carried, never shown.</summary>
public sealed class BulkUnarchiveRowViewModel(BulkUnarchiveCandidate project) : ObservableObject
{
    private bool _isChecked; // D3: nothing is pre-ticked

    public long NodeId { get; } = project.NodeId;

    public string Name { get; } = project.Name;

    /// <summary>The archive event the user sees in this row (R2-1).</summary>
    public long ArchiveSerial { get; } = project.ArchiveSerial;

    /// <summary>"in Clients / Tokyo" (the parent's current path) or the top level.</summary>
    public string LocationText { get; } = project.ParentPath is { } path ? $"in {path}" : "in Workspace (top level)";

    public string UpdatedText { get; } = project.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    /// <summary>The accessible name of the row and its checkbox.</summary>
    public string AccessibleName => $"{Name}, {LocationText}";
}
