using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// One row of the Project content list: a ProjectFolder, a Link or a Note (summary only). Rows are replaced/updated only
/// from persisted state.
/// </summary>
public sealed class ProjectContentItemViewModel : ObservableObject
{
    private ProjectFolder? _folder;
    private ProjectLink? _link;
    private ProjectNoteSummary? _note;
    private LinkHealthStatus _status = LinkHealthStatus.NotChecked;
    private LinkHealthStatus _statusBeforeCheck = LinkHealthStatus.NotChecked;
    private DateTimeOffset? _checkedAt;

    internal ProjectContentItemViewModel(ProjectFolder folder) => _folder = folder;

    internal ProjectContentItemViewModel(ProjectLink link) => _link = link;

    internal ProjectContentItemViewModel(ProjectNoteSummary note) => _note = note;

    public bool IsFolder => _folder is not null;

    public bool IsLink => _link is not null;

    public bool IsNote => _note is not null;

    public ProjectContentItemKind Kind => IsFolder ? ProjectContentItemKind.Folder : IsLink ? ProjectContentItemKind.Link : ProjectContentItemKind.Note;

    public long Id => _folder?.Id ?? _link?.Id ?? _note!.Id;

    /// <summary>Typed identity (Kind, Id): Folder, Link and Note Ids are independent.</summary>
    public ContentRef Ref => new(Kind switch
    {
        ProjectContentItemKind.Folder => ContentKind.Folder,
        ProjectContentItemKind.Link => ContentKind.Link,
        _ => ContentKind.Note,
    }, Id);

    public string Name => _folder?.Name ?? _link?.Name ?? _note!.Title;

    /// <summary>The container this row was listed in (null = Project root): the "observed" source for Move.</summary>
    public long? ContainerId => _folder is not null ? _folder.ParentFolderId : _link is not null ? _link.FolderId : _note!.FolderId;

    public ProjectFolder? Folder => _folder;

    public ProjectLink? Link => _link;

    public ProjectNoteSummary? Note => _note;

    public ProjectLinkType? LinkType => _link?.Type;

    public string? Target => _link?.Target;

    /// <summary>Second column: a link's target, or a note's last update time.</summary>
    public string? DetailText => _note is { } note ? $"Updated {note.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}" : _link?.Target;

    public string KindText => _note is not null ? "Note" : _link?.Type switch
    {
        ProjectLinkType.Web => "Web",
        ProjectLinkType.File => "File",
        ProjectLinkType.Folder => "Folder link",
        _ => "Folder",
    };

    public LinkHealthStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
                return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsChecking));
            OnPropertyChanged(nameof(IsStatusProblem));
            OnPropertyChanged(nameof(IsStatusUnverified));
        }
    }

    public DateTimeOffset? CheckedAt
    {
        get => _checkedAt;
        private set
        {
            if (SetProperty(ref _checkedAt, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsChecking => Status == LinkHealthStatus.Checking;

    /// <summary>A confirmed problem (Missing, Wrong type, Invalid target).</summary>
    public bool IsStatusProblem => Status is LinkHealthStatus.Missing or LinkHealthStatus.WrongType or LinkHealthStatus.InvalidTarget;

    /// <summary>The target could not be verified (not "missing").</summary>
    public bool IsStatusUnverified => LinkHealthStatuses.IsUnverified(Status);

    public string StatusText
    {
        get
        {
            if (!IsLink)
                return string.Empty;
            var text = LinkHealthStatuses.DisplayName(Status);
            return CheckedAt is { } at && Status is not (LinkHealthStatus.Checking or LinkHealthStatus.NotChecked)
                ? $"{text} · {at.LocalDateTime:HH:mm}"
                : text;
        }
    }

    internal (WorkspaceNodeType, string, long) SortKey => (WorkspaceNodeType.Folder, Name, Id);

    /// <summary>Display order of kinds: folders, then links, then notes.</summary>
    internal int KindRank => (int)Kind;

    internal void ApplyFolder(ProjectFolder folder)
    {
        _folder = folder;
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>Applies a freshly read note summary (title, revision, updated time).</summary>
    internal void ApplyNote(ProjectNoteSummary note)
    {
        _note = note;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(DetailText));
    }

    /// <summary>Applies a stored link. A Type or Target change invalidates the previous check status; a name-only edit keeps it.</summary>
    internal void ApplyLink(ProjectLink link)
    {
        var targetChanged = _link is null || _link.Type != link.Type || !string.Equals(_link.Target, link.Target, StringComparison.Ordinal);
        _link = link;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(LinkType));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(KindText));
        if (targetChanged)
            SetStatus(LinkHealthStatus.NotChecked, null);
    }

    internal void BeginCheck()
    {
        _statusBeforeCheck = Status;
        Status = LinkHealthStatus.Checking;
    }

    /// <summary>Stop: back to the last accepted status.</summary>
    internal void RevertCheck()
    {
        if (Status == LinkHealthStatus.Checking)
            Status = _statusBeforeCheck;
    }

    internal void SetStatus(LinkHealthStatus status, DateTimeOffset? checkedAt)
    {
        Status = status;
        CheckedAt = checkedAt;
    }

    public override string ToString() => Name;
}

/// <summary>Row kinds in display order.</summary>
public enum ProjectContentItemKind
{
    Folder,
    Link,
    Note,
}
