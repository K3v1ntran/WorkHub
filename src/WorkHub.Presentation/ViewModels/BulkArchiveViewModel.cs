using System.ComponentModel;
using System.Globalization;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Phase 19 "Archive projects…" checklist (proposal Revision 2 + R2a–R2d, §4.1): the live Projects directly in ONE place (a
/// Workspace Folder, or the top level), read when the dialog opens. Rows with status Completed start ticked (D5); at most
/// <see cref="BulkArchiveCandidates.MaxRows"/> rows (D6). Cancel is the default; Enter and Esc never archive. Nothing is
/// written by the dialog itself: the Workspace area archives the confirmed targets, one write transaction per Project.
/// </summary>
public sealed class BulkArchiveViewModel : ObservableObject
{
    private readonly long? _placeId;

    public BulkArchiveViewModel(long? placeId, string? placePath, IReadOnlyList<BulkArchiveCandidate> projects, bool truncated)
    {
        _placeId = placeId;
        PlaceText = placeId is null ? "at the top level" : $"in “{placePath}”";
        Rows = projects.Select(p => new BulkArchiveRowViewModel(p)).ToList();
        foreach (var row in Rows)
            row.PropertyChanged += OnRowChanged;
        TruncatedText = truncated
            ? $"Only the first {BulkArchiveCandidates.MaxRows} projects are listed. Archive these, then open the dialog again for the rest."
            : null;
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        ArchiveCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true), () => CheckedCount > 0);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public string Title => "Archive projects";

    /// <summary>"in “Clients › 2025”" or "at the top level".</summary>
    public string PlaceText { get; }

    public string Message =>
        $"Archive the ticked projects {PlaceText}. Each leaves the tree and is listed under Archived. Their details, folders, links and notes are kept exactly as they are; nothing on disk is touched.";

    public IReadOnlyList<BulkArchiveRowViewModel> Rows { get; }

    /// <summary>Shown when the place holds more than the row limit (D6).</summary>
    public string? TruncatedText { get; }

    public int CheckedCount => Rows.Count(r => r.IsChecked);

    /// <summary>The live count on the action button: "Archive 4 projects".</summary>
    public string ArchiveText => CheckedCount == 1 ? "Archive 1 project" : $"Archive {CheckedCount} projects";

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand ArchiveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>The ticked rows in list order, each bound to the confirmed place (R2-1).</summary>
    public IReadOnlyList<BulkArchiveTarget> ConfirmedTargets =>
        Rows.Where(r => r.IsChecked).Select(r => new BulkArchiveTarget(r.NodeId, _placeId)).ToList();

    /// <summary>The names as read when the dialog opened (the summary uses them).</summary>
    public IReadOnlyDictionary<long, string> NamesById => Rows.ToDictionary(r => r.NodeId, r => r.Name);

    /// <summary>Space on the focused row (the window forwards it).</summary>
    public void Toggle(BulkArchiveRowViewModel? row)
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
        if (e.PropertyName != nameof(BulkArchiveRowViewModel.IsChecked))
            return;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ArchiveText));
        ArchiveCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One row of the Bulk Archive checklist.</summary>
public sealed class BulkArchiveRowViewModel : ObservableObject
{
    private bool _isChecked;

    public BulkArchiveRowViewModel(BulkArchiveCandidate project)
    {
        NodeId = project.NodeId;
        Name = project.Name;
        Status = project.Status;
        StatusText = ProjectStatuses.DisplayName(project.Status);
        Owner = project.Owner;
        UpdatedText = project.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _isChecked = project.Status == ProjectStatus.Completed; // D5
    }

    public long NodeId { get; }

    public string Name { get; }

    public ProjectStatus Status { get; }

    public string StatusText { get; }

    public string? Owner { get; }

    public string UpdatedText { get; }

    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    /// <summary>The accessible name of the row's checkbox.</summary>
    public string AccessibleName => $"{Name}, {StatusText}";
}
