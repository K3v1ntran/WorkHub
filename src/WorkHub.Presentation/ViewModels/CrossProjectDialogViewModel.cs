using System.Collections.ObjectModel;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>A destination Project as listed in the dialog ("Shelf › Gamma (archived)").</summary>
public sealed record CrossProjectTargetItem(long ProjectNodeId, string Path, bool Archived)
{
    public string Display => Archived ? $"{Path} (archived)" : Path;

    public override string ToString() => Display;
}

/// <summary>A destination folder of the chosen Project (null Id = the Project root).</summary>
public sealed record CrossProjectFolderItem(long? FolderId, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// Phase 15 dialog: choose a destination Project and folder, (folders) confirm the root name, read the preview, then confirm.
/// It never writes: Confirm closes with the request that produced the CURRENT Ready preview and that preview's token, and the
/// Project view commits them.
/// <list type="bullet">
/// <item>One snapshot: the title (the source's name), the Move note (the source Project's name), every confirmation line and the
/// token all come from the same applied preview. The names passed in when the dialog opens are shown only until the first
/// preview is applied.</item>
/// <item>Every Project, folder or name change starts a new preview; while it loads, Confirm is disabled; a result is applied only
/// while it is the newest one, so a late older result never replaces the shown preview or its token.</item>
/// <item>Folders keep two names apart: the source's current name (from the preview) and the name the copy will have. Until the
/// user edits it, the copy's name follows the source's current name (the request leaves it unset); once edited, a refresh keeps
/// the user's text. A suggested name is only offered (Use suggested name fills the field like an edit).</item>
/// </list>
/// Cancel is the default.
/// </summary>
public sealed class CrossProjectDialogViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private CrossProjectTargetItem? _selectedProject;
    private CrossProjectFolderItem? _selectedFolder;
    private string _rootName;
    private bool _rootNameEdited;
    private string _sourceName;
    private string _sourceProjectName;
    private CrossProjectPreview? _preview;
    private CrossProjectRequest? _previewRequest;
    private bool _isBusy;
    private string? _message;
    private int _version;

    public CrossProjectDialogViewModel(ProjectContentService content, CrossProjectMode mode, ContentRef source, string sourceName, string sourceProjectName,
        IEnumerable<CrossProjectTarget> targets)
    {
        _content = content;
        Mode = mode;
        Source = source;
        _sourceName = sourceName;
        _sourceProjectName = sourceProjectName;
        _rootName = sourceName;
        Projects = new ObservableCollection<CrossProjectTargetItem>(targets.Select(t => new CrossProjectTargetItem(t.ProjectNodeId, t.Path, t.Archived)));
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
        UseSuggestedNameCommand = new RelayCommand(() => RootName = _preview!.SuggestedName!, () => _preview?.SuggestedName is not null);
    }

    public event EventHandler<bool>? CloseRequested;

    public CrossProjectMode Mode { get; }

    public ContentRef Source { get; }

    /// <summary>The source item's name as of the applied preview (the opening name until the first preview).</summary>
    public string SourceName => _sourceName;

    /// <summary>The source Project's name as of the applied preview (the opening name until the first preview).</summary>
    public string SourceProjectName => _sourceProjectName;

    public bool IsFolder => Source.Kind == ContentKind.Folder;

    public string Title => Mode == CrossProjectMode.Move ? $"Move “{SourceName}” to another project" : $"Copy “{SourceName}” to another project";

    public string ConfirmText => Mode == CrossProjectMode.Move ? "Move" : "Copy";

    public string? MoveNote => Mode == CrossProjectMode.Move
        ? $"The original goes to the Trash of {SourceProjectName} and can be restored there. Restoring it does not change the copy."
        : null;

    public ObservableCollection<CrossProjectTargetItem> Projects { get; }

    public ObservableCollection<CrossProjectFolderItem> Folders { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    public CrossProjectTargetItem? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
                return;
            CurrentLoad = LoadFoldersAsync(value);
        }
    }

    public CrossProjectFolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
                Refresh();
        }
    }

    /// <summary>The name the copied folder root will have at the destination (folders only). Setting it is a user edit.</summary>
    public string RootName
    {
        get => _rootName;
        set
        {
            value ??= string.Empty;
            if (_rootNameEdited && _rootName == value)
                return;
            _rootNameEdited = true;
            _rootName = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>True once the user typed or accepted a name for the copy; refreshes then keep that text.</summary>
    public bool IsRootNameEdited => _rootNameEdited;

    public CrossProjectPreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value))
                return;
            OnPropertyChanged(nameof(CountsText));
            OnPropertyChanged(nameof(SkippedText));
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(HasSuggestion));
            OnPropertyChanged(nameof(SuggestionText));
            ConfirmCommand.RaiseCanExecuteChanged();
            UseSuggestedNameCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Why the current choice cannot be confirmed (a typed refusal), or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public string? CountsText => Preview is { IsReady: true } p ? $"{Describe(p)} will be {(Mode == CrossProjectMode.Move ? "moved" : "copied")}." : null;

    /// <summary>D-15.7: skipped items as the main number, entries as secondary text.</summary>
    public string? SkippedText => Preview is { IsReady: true, SkippedItems: > 0 } p
        ? $"{Items(p.SkippedItems)} in the Trash inside it {(p.SkippedItems == 1 ? "is" : "are")} not included ({Plural(p.SkippedEntries, "Trash entry", "Trash entries")})."
        : null;

    public string? PathText => Preview is { IsReady: true } p ? $"From {p.SourcePath} to {p.TargetPath}{(p.TargetArchived ? " (archived)" : string.Empty)}." : null;

    public bool HasSuggestion => Preview?.SuggestedName is not null;

    public string? SuggestionText => Preview?.SuggestedName is { } name ? $"Suggested name: “{name}”" : null;

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand UseSuggestedNameCommand { get; }

    public bool CanConfirm => !IsBusy && Preview is { IsReady: true, Token: not null } && _previewRequest is not null;

    /// <summary>Set when the dialog closes with Confirm: exactly the request whose preview (and token) was shown.</summary>
    public CrossProjectRequest? ConfirmedRequest { get; private set; }

    public string? ConfirmedToken { get; private set; }

    /// <summary>The latest folder load; lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The latest preview; lets tests await it.</summary>
    internal Task CurrentPreview { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The request for the current choice. A folder's name is sent only after the user edited it; otherwise the store uses the
    /// source's CURRENT name in the same snapshot as everything else.
    /// </summary>
    private CrossProjectRequest? Request() =>
        SelectedProject is { } project && SelectedFolder is { } folder
            ? new CrossProjectRequest(Mode, Source, project.ProjectNodeId, folder.FolderId, IsFolder && _rootNameEdited ? _rootName : null)
            : null;

    private async Task LoadFoldersAsync(CrossProjectTargetItem? project)
    {
        var version = ++_version;
        Folders.Clear();
        _selectedFolder = null;
        OnPropertyChanged(nameof(SelectedFolder));
        ClearPreview();
        Message = null;
        if (project is null)
            return;
        IsBusy = true;
        try
        {
            var folders = await _content.ListFolderMoveTargetsAsync(project.ProjectNodeId, null);
            if (version != _version)
                return;
            Folders.Add(new CrossProjectFolderItem(null, $"{project.Path} (project root)"));
            foreach (var folder in folders)
                Folders.Add(new CrossProjectFolderItem(folder.Id, $"{project.Path} › {folder.Path.Replace(" / ", " › ", StringComparison.Ordinal)}"));
            SelectedFolder = Folders[0];
        }
        catch (Exception)
        {
            if (version == _version)
                Message = "The project's folders could not be read. Try again.";
        }
        finally
        {
            if (version == _version)
                IsBusy = false;
        }
    }

    private void Refresh()
    {
        if (Request() is not { } request)
            return;
        CurrentPreview = PreviewAsync(request, ++_version);
    }

    private void ClearPreview()
    {
        _previewRequest = null;
        Preview = null;
    }

    private async Task PreviewAsync(CrossProjectRequest request, int version)
    {
        IsBusy = true; // Confirm is disabled until THIS preview is applied
        ClearPreview();
        Message = null;
        try
        {
            var preview = await _content.PrepareCrossProjectAsync(request);
            if (version != _version)
                return; // a newer choice owns the dialog; a late older result changes nothing
            ApplySnapshot(preview);
            _previewRequest = request;
            Preview = preview;
            Message = preview.IsReady ? null : ProjectContentService.CrossProjectRefusalText(preview.Status, preview.RootName);
        }
        catch (Exception)
        {
            if (version == _version)
                Message = "The preview could not be read. Nothing was copied. Try again.";
        }
        finally
        {
            if (version == _version)
                IsBusy = false;
        }
    }

    /// <summary>The names shown come from the applied preview's snapshot (the one its token binds).</summary>
    private void ApplySnapshot(CrossProjectPreview preview)
    {
        if (preview.SourceName.Length > 0 && preview.SourceName != _sourceName)
        {
            _sourceName = preview.SourceName;
            OnPropertyChanged(nameof(SourceName));
            OnPropertyChanged(nameof(Title));
        }

        if (preview.SourceProjectName.Length > 0 && preview.SourceProjectName != _sourceProjectName)
        {
            _sourceProjectName = preview.SourceProjectName;
            OnPropertyChanged(nameof(SourceProjectName));
            OnPropertyChanged(nameof(MoveNote));
        }

        if (IsFolder && !_rootNameEdited && preview.RootName.Length > 0 && preview.RootName != _rootName)
        {
            _rootName = preview.RootName; // follows the source until the user edits it; not an edit, no new preview
            OnPropertyChanged(nameof(RootName));
        }
    }

    private void Confirm()
    {
        if (!CanConfirm)
            return;
        ConfirmedRequest = _previewRequest;
        ConfirmedToken = Preview!.Token;
        CloseRequested?.Invoke(this, true);
    }

    private static string Describe(CrossProjectPreview p)
    {
        var parts = new List<string>();
        if (p.Folders > 0)
            parts.Add(Plural(p.Folders, "folder", "folders"));
        if (p.Links > 0)
            parts.Add(Plural(p.Links, "link", "links"));
        if (p.Notes > 0)
            parts.Add(Plural(p.Notes, "note", "notes"));
        return string.Join(", ", parts);
    }

    private static string Items(int n) => n == 1 ? "1 item" : $"{n} items";

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
