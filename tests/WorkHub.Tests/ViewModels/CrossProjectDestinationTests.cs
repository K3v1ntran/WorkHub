using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 15 Open destination in the Workspace area (acceptance U5): the copy is found by its NEW identity and revealed at its
/// CURRENT location through the area's fresh resolve; a copy trashed meanwhile is reported and nothing else is opened.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class CrossProjectDestinationTests : IDisposable
{
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly GatedProjectContentStore _contentStore;
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _otherContent;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly SingleThreadContext _context = new();

    public CrossProjectDestinationTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var real = new SqliteWorkspaceStore(_connections);
        _treeStore = new GatedWorkspaceStore(real);
        _other = new WorkspaceService(real);
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, () => { _treeStore.ReleaseAll(); _contentStore.ReleaseAll(); },
            _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() => Tracked(area, $"area{i}."))).ToList(), _root.Dispose, _root.Paths.Root, TeardownBound);

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private static IReadOnlyList<(string Name, Task Task)> Tracked(WorkspaceAreaViewModel area, string prefix)
    {
        var tasks = new List<(string Name, Task Task)>
        {
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentReveal", area.CurrentReveal), ("CurrentDestinationReveal", area.CurrentDestinationReveal),
            ("CurrentLifecycleOperation", area.CurrentLifecycleOperation), ("CurrentReconciliation", area.CurrentReconciliation),
            ("Trash.CurrentCount", area.Trash.CurrentCount), ("Archived.CurrentCount", area.Archived.CurrentCount),
        };
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentPreviewLoad", content.CurrentPreviewLoad),
                ("Content.CurrentCrossProject", content.CurrentCrossProject), ("Content.CurrentCountReconciliation", content.CurrentCountReconciliation)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    private async Task<WorkspaceAreaViewModel> Area()
    {
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default)), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    /// <summary>A modal dialog's nested loop: pumps the context until the dialog's own read completed.</summary>
    private static void Pump(Task task) => Assert.True(SingleThreadContext.WaitOrPump(() => task.IsCompleted, t => task.Wait(t), Wait), "the dialog read did not complete");

    private static Func<CrossProjectDialogViewModel, bool> Choose(long project) => d =>
    {
        d.SelectedProject = d.Projects.Single(p => p.ProjectNodeId == project);
        Pump(d.CurrentLoad);
        Pump(d.CurrentPreview);
        Assert.True(d.CanConfirm, d.Message);
        d.ConfirmCommand.Execute(null);
        return true;
    };

    /// <summary>P with a root link L; Q with a folder QF. Copies L from P to Q's root through the area; returns the copy's Id.</summary>
    private async Task<(WorkspaceAreaViewModel Area, ProjectContentViewModel View, long Q, long QF, long Copy)> CopyLinkToQ()
    {
        var p = (await _other.CreateProjectAsync(null, new ProjectDraft("P", ProjectStatus.Active))).Value!.Id;
        var q = (await _other.CreateProjectAsync(null, new ProjectDraft("Q", ProjectStatus.Active))).Value!.Id;
        var link = (await _otherContent.CreateLinkAsync(p, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://example.test/l"))).Value!.Id;
        var qf = (await _otherContent.CreateFolderAsync(q, null, "QF")).Value!.Id;
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single(i => i.Kind == ProjectContentItemKind.Link && i.Id == link);
        _dialogs.OnCrossProject = Choose(q);

        await view.CopyToProjectCommand.ExecuteAsync();

        Assert.True(view.HasDestination);
        var copy = view.DestinationRoot!.Value;
        Assert.Equal(ContentKind.Link, copy.Kind);
        return (area, view, q, qf, copy.Id);
    }

    [Fact]
    public Task U5_OpenDestination_RevealsTheCopyWhereItIsNow_EvenAfterItWasMovedElsewhere() => InContext(async () =>
    {
        var (area, view, q, qf, copy) = await CopyLinkToQ();
        Assert.Equal(ContentMoveOutcome.Moved, (await _otherContent.MoveLinkAsync(copy, null, qf)).Outcome); // another window moves the copy

        view.OpenDestinationCommand.Execute(null);
        await area.CurrentDestinationReveal;

        var destination = area.SelectedContent!;
        Assert.Equal(q, destination.ProjectNodeId);
        Assert.Equal(qf, destination.CurrentFolderId);
        Assert.Equal((ProjectContentItemKind.Link, copy), (destination.SelectedItem!.Kind, destination.SelectedItem.Id));
    });

    [Fact]
    public Task U5_OpenDestination_OnACopyTrashedMeanwhile_SaysSo_AndOpensNothingElse() => InContext(async () =>
    {
        var (area, view, _, _, copy) = await CopyLinkToQ();
        Assert.Equal(TrashOutcome.Trashed, (await _otherContent.TrashAsync(new ContentRef(ContentKind.Link, copy))).Outcome);

        view.OpenDestinationCommand.Execute(null);
        await area.CurrentDestinationReveal;

        Assert.Equal("The copy is in the Trash.", area.BannerMessage);
        Assert.Same(view, area.SelectedContent); // the source view stays; nothing else was opened
    });
}
