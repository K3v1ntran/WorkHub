using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Workspace editors must not close while their write is pending. Each test holds the write in a gated store, tries to
/// close (Cancel/Escape run CancelCommand; title-bar close and Alt+F4 are refused by the window while CanClose is false),
/// then lets the write succeed (the tree updates) or fail (the error stays visible, nothing reports success).
/// </summary>
public sealed class WorkspaceDialogLifecycleTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly GatedWorkspaceStore _store;
    private readonly WorkspaceService _seed;
    private readonly WorkspaceService _workspace;
    private int _dialogsCompleted;

    public WorkspaceDialogLifecycleTests()
    {
        _root.CreateInitializer().Initialize();
        var real = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _seed = new WorkspaceService(real);
        _store = new GatedWorkspaceStore(real);
        _workspace = new WorkspaceService(_store);
    }

    public void Dispose() => _root.Dispose();

    private async Task<WorkspaceAreaViewModel> ActivatedArea()
    {
        var area = new WorkspaceAreaViewModel(_workspace, _dialogs, NullAppLog.Instance);
        area.IsTabSelected = true;
        await area.RootLoad;
        return area;
    }

    private static List<string> Names(IEnumerable<WorkspaceNodeViewModel> nodes) =>
        nodes.Where(n => !n.IsPlaceholder).Select(n => n.Name).ToList();

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => n.Name == name);

    private long Rows(string table)
    {
        using var connection = _root.Open();
        return Db.Count(connection, table);
    }

    /// <summary>
    /// Plays a modal editor whose write is held: closing is refused while pending; afterwards normal closing returns.
    /// Returns the dialog result the window would report.
    /// </summary>
    private bool PlayPendingWrite(
        StoreGate gate, bool succeed, AsyncRelayCommand save, Func<bool> canClose, RelayCommand cancel,
        Action<EventHandler<bool>> subscribe, Func<string?> error, string failurePrefix = "Could not save")
    {
        var closeRequests = new List<bool>();
        subscribe((_, ok) => closeRequests.Add(ok));

        var pending = save.ExecuteAsync();
        gate.WaitEntered();

        // Pending: Cancel/Escape are disabled and do nothing; the window refuses title-bar close and Alt+F4 (CanClose).
        Assert.False(canClose());
        Assert.False(cancel.CanExecute(null));
        Assert.False(save.CanExecute(null)); // no second write while one is pending
        cancel.Execute(null);
        Assert.Empty(closeRequests);
        Assert.False(pending.IsCompleted);

        if (succeed)
            gate.Release();
        else
            gate.Fail(new IOException("database is locked"));
        pending.GetAwaiter().GetResult();

        Assert.True(canClose());
        Assert.True(cancel.CanExecute(null));
        if (succeed)
        {
            Assert.Equal([true], closeRequests);
            Assert.Null(error());
            _dialogsCompleted++;
            return true;
        }

        Assert.Empty(closeRequests); // no success reported
        Assert.Equal($"{failurePrefix}: database is locked", error()); // the failure stays visible
        cancel.Execute(null); // closing works normally again
        Assert.Equal([false], closeRequests);
        _dialogsCompleted++;
        return false;
    }

    /// <summary>Assertions inside a dialog would be swallowed by the area command's error handler; prove the script ran to the end.</summary>
    private void AssertDialogCompleted() => Assert.Equal(1, _dialogsCompleted);

    private void NameEditorHeld(string key, string name, bool succeed) => _dialogs.OnWorkspaceNameEditor = editor =>
    {
        editor.Name = name;
        return PlayPendingWrite(_store.Hold(key), succeed, editor.SaveCommand, () => editor.CanClose, editor.CancelCommand,
            h => editor.CloseRequested += h, () => editor.Error);
    };

    private void ProjectEditorHeld(string key, Action<ProjectEditorViewModel> fill, bool succeed) => _dialogs.OnProjectEditor = editor =>
    {
        fill(editor);
        return PlayPendingWrite(_store.Hold(key), succeed, editor.SaveCommand, () => editor.CanClose, editor.CancelCommand,
            h => editor.CloseRequested += h, () => editor.GeneralError);
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateFolder(bool succeed)
    {
        await _seed.CreateFolderAsync(null, "Existing");
        var area = await ActivatedArea();
        NameEditorHeld("createFolder", "新規フォルダ", succeed);

        await area.NewFolderCommand.ExecuteAsync();

        AssertDialogCompleted();

        if (succeed)
        {
            Assert.Equal(["Existing", "新規フォルダ"], Names(area.Roots));
            Assert.Equal("新規フォルダ", area.SelectedNode!.Name);
            Assert.Equal(2, Rows("WorkspaceNodes"));
        }
        else
        {
            Assert.Equal(["Existing"], Names(area.Roots));
            Assert.Null(area.SelectedNode);
            Assert.Equal(1, Rows("WorkspaceNodes"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenameFolder(bool succeed)
    {
        await _seed.CreateFolderAsync(null, "Old");
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Old");
        NameEditorHeld("rename", "New", succeed);

        await area.RenameCommand.ExecuteAsync();

        AssertDialogCompleted();

        Assert.Equal([succeed ? "New" : "Old"], Names(area.Roots));
        Assert.Equal(succeed ? "New" : "Old", area.SelectedNode!.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenameProject(bool succeed)
    {
        await _seed.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active));
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Alpha");
        await area.CurrentDetailsLoad;
        NameEditorHeld("rename", "Beta", succeed);

        await area.RenameCommand.ExecuteAsync();

        AssertDialogCompleted();
        await area.CurrentDetailsLoad;

        Assert.Equal([succeed ? "Beta" : "Alpha"], Names(area.Roots));
        Assert.Equal(succeed ? "Beta" : "Alpha", area.SelectedProject!.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateProject(bool succeed)
    {
        var folder = (await _seed.CreateFolderAsync(null, "F")).Value!;
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "F");
        ProjectEditorHeld("createProject", e => { e.Name = "販売管理"; e.Owner = "佐藤"; }, succeed);

        await area.NewProjectCommand.ExecuteAsync();

        AssertDialogCompleted();
        await area.CurrentDetailsLoad;

        if (succeed)
        {
            Assert.Equal(["販売管理"], Names(Node(area.Roots, "F").Children));
            Assert.Equal(("販売管理", "佐藤"), (area.SelectedProject!.Name, area.SelectedProject.Owner));
            Assert.Equal(1, Rows("Projects"));
        }
        else
        {
            Assert.Empty(Node(area.Roots, "F").Children);
            Assert.Same(Node(area.Roots, "F"), area.SelectedNode);
            Assert.Equal(0, _store.Calls(GatedWorkspaceStore.Children(folder.Id))); // nothing to merge, nothing loaded
            Assert.Equal(0, Rows("Projects"));
            Assert.Equal(1, Rows("WorkspaceNodes"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditProject(bool succeed)
    {
        await _seed.CreateProjectAsync(null, new ProjectDraft("P", ProjectStatus.Planning));
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "P");
        await area.CurrentDetailsLoad;
        ProjectEditorHeld("update", e =>
        {
            e.Status = e.Statuses.Single(s => s.Status == ProjectStatus.Completed);
            e.Description = "done";
        }, succeed);

        await area.EditProjectCommand.ExecuteAsync();

        AssertDialogCompleted();

        var shown = area.SelectedProject!;
        var stored = (await _seed.GetProjectAsync(shown.NodeId))!;
        if (succeed)
            Assert.Equal((ProjectStatus.Completed, "done"), (shown.Status, shown.Description));
        else
            Assert.Equal((ProjectStatus.Planning, (string?)null), (shown.Status, shown.Description));
        Assert.Equal(stored, shown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Move(bool succeed)
    {
        var a = (await _seed.CreateFolderAsync(null, "A")).Value!;
        var b = (await _seed.CreateFolderAsync(null, "B")).Value!;
        await _seed.CreateProjectAsync(a.Id, new ProjectDraft("P", ProjectStatus.Active));
        var area = await ActivatedArea();
        Node(area.Roots, "A").IsExpanded = true;
        await area.EnsureChildrenLoadedAsync(Node(area.Roots, "A"));
        area.SelectedNode = Node(Node(area.Roots, "A").Children, "P");
        await area.CurrentDetailsLoad;
        _dialogs.OnWorkspaceMove = editor =>
        {
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == b.Id);
            return PlayPendingWrite(_store.Hold("move"), succeed, editor.MoveCommand, () => editor.CanClose, editor.CancelCommand,
                h => editor.CloseRequested += h, () => editor.Error, "Could not move");
        };

        await area.MoveCommand.ExecuteAsync();

        AssertDialogCompleted();

        if (succeed)
        {
            Assert.Empty(Names(Node(area.Roots, "A").Children));
            Assert.Equal(["P"], Names(Node(area.Roots, "B").Children));
        }
        else
        {
            Assert.Equal(["P"], Names(Node(area.Roots, "A").Children));
            Assert.Equal(a.Id, (await _seed.GetProjectAsync(area.SelectedNode!.Id))!.ParentId);
        }

        Assert.Equal("P", area.SelectedNode!.Name);
    }
}
