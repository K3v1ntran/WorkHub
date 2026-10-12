using System.Collections.ObjectModel;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// One Folder or Project in the Workspace tree. A Folder's children are loaded on first expand; until then a
/// placeholder child gives the tree its expander. Projects never have tree children.
/// The same instance is reused for a node Id wherever it appears, so changes merge by Id and appear exactly once.
/// </summary>
public sealed class WorkspaceNodeViewModel : ObservableObject
{
    private readonly WorkspaceAreaViewModel? _area;
    private string _name;
    private bool _hasChildren;
    private bool _isExpanded;
    private bool _isSelected;
    private WorkspaceNodeViewModel? _parent;

    internal WorkspaceNodeViewModel(WorkspaceAreaViewModel area, WorkspaceNode node, WorkspaceNodeViewModel? parent)
    {
        _area = area;
        Id = node.Id;
        Type = node.Type;
        _name = node.Name;
        _parent = parent;
        Children.CollectionChanged += (_, _) => BranchStamp++;
        SetHasChildren(node.HasChildren);
    }

    private WorkspaceNodeViewModel(WorkspaceNodeViewModel parent)
    {
        _parent = parent;
        _name = "Loading…";
        IsPlaceholder = true;
    }

    /// <summary>Stand-in child shown under an unloaded Folder so it can be expanded.</summary>
    internal static WorkspaceNodeViewModel CreatePlaceholder(WorkspaceNodeViewModel parent) => new(parent);

    public long Id { get; }

    public WorkspaceNodeType Type { get; }

    public bool IsFolder => !IsPlaceholder && Type == WorkspaceNodeType.Folder;

    public bool IsProject => !IsPlaceholder && Type == WorkspaceNodeType.Project;

    public bool IsPlaceholder { get; }

    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    /// <summary>Loaded parent Folder, or null at the root.</summary>
    public WorkspaceNodeViewModel? Parent
    {
        get => _parent;
        internal set
        {
            if (ReferenceEquals(_parent, value))
                return;
            _parent = value;
            OnDepthChanged();
        }
    }

    /// <summary>0 at the root. The tree view indents rows by depth inside a full-width row.</summary>
    public int Depth => _parent is null ? 0 : _parent.Depth + 1;

    public ObservableCollection<WorkspaceNodeViewModel> Children { get; } = [];

    internal bool IsChildrenLoaded { get; set; }

    /// <summary>Incremented by every change to <see cref="Children"/> (lazy loads, local writes, reconciliation).</summary>
    internal int BranchStamp { get; private set; }

    /// <summary>
    /// Incremented when a reveal reconciled <see cref="Children"/> to a fresh snapshot. A lazy load that started before
    /// such a reconciliation discards its (older) result.
    /// </summary>
    internal int ReconcileStamp { get; set; }

    /// <summary>The child load in flight, if any (reused by every caller until it finishes).</summary>
    internal Task<bool>? ChildrenLoad { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value) || !value || !IsFolder)
                return;
            _area?.OnExpanded(this);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsPlaceholder || !SetProperty(ref _isSelected, value))
                return;
            _area?.OnSelectionChanged(this, value);
        }
    }

    /// <summary>"Clients / Tokyo / Sales" for display in editors.</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path} / {Name}";

    internal (WorkspaceNodeType, string, long) SortKey => (Type, Name, Id);

    /// <summary>Whether the Folder has children in the database (drives the expander before loading).</summary>
    internal void SetHasChildren(bool hasChildren)
    {
        _hasChildren = hasChildren;
        if (IsChildrenLoaded || !IsFolder)
            return;

        var hasPlaceholder = Children.Count == 1 && Children[0].IsPlaceholder;
        if (hasChildren && !hasPlaceholder)
            Children.Add(CreatePlaceholder(this));
        else if (!hasChildren && hasPlaceholder)
            Children.Clear();
    }

    internal bool HasChildren => IsChildrenLoaded ? Children.Count > 0 : _hasChildren;

    private void OnDepthChanged()
    {
        OnPropertyChanged(nameof(Depth));
        OnPropertyChanged(nameof(Path));
        foreach (var child in Children)
            child.OnDepthChanged();
    }

    internal void CollapseAfterFailedLoad() => SetProperty(ref _isExpanded, false, nameof(IsExpanded));

    public override string ToString() => Name;
}
