using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.Tests.TestSupport;

/// <summary>Scriptable stand-in for WPF dialogs. Each Show* delegate plays the user's part and returns the dialog result.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public Func<SystemEditorViewModel, bool> OnSystemEditor { get; set; } = _ => false;

    public Func<OwnerGroupEditorViewModel, bool> OnOwnerGroupEditor { get; set; } = _ => false;

    public Func<OwnerItemEditorViewModel, bool> OnOwnerItemEditor { get; set; } = _ => false;

    public Func<MoveOwnerItemViewModel, bool> OnMoveOwnerItem { get; set; } = _ => false;

    public bool ConfirmAnswer { get; set; } = true;

    public List<(string Title, string Message, string ConfirmText)> Confirmations { get; } = [];

    public string? Clipboard { get; private set; }

    public bool ShowSystemEditor(SystemEditorViewModel viewModel) => OnSystemEditor(viewModel);

    public Func<SettingsViewModel, bool> OnSettings { get; set; } = _ => false;

    public bool ShowSettings(SettingsViewModel viewModel) => OnSettings(viewModel);

    public bool ShowOwnerGroupEditor(OwnerGroupEditorViewModel viewModel) => OnOwnerGroupEditor(viewModel);

    public bool ShowOwnerItemEditor(OwnerItemEditorViewModel viewModel) => OnOwnerItemEditor(viewModel);

    public bool ShowMoveOwnerItem(MoveOwnerItemViewModel viewModel) => OnMoveOwnerItem(viewModel);

    public Func<WorkspaceNameEditorViewModel, bool> OnWorkspaceNameEditor { get; set; } = _ => false;

    public Func<ProjectEditorViewModel, bool> OnProjectEditor { get; set; } = _ => false;

    public Func<WorkspaceMoveViewModel, bool> OnWorkspaceMove { get; set; } = _ => false;

    public bool ShowWorkspaceNameEditor(WorkspaceNameEditorViewModel viewModel) => OnWorkspaceNameEditor(viewModel);

    public bool ShowProjectEditor(ProjectEditorViewModel viewModel) => OnProjectEditor(viewModel);

    public bool ShowWorkspaceMove(WorkspaceMoveViewModel viewModel) => OnWorkspaceMove(viewModel);

    public bool Confirm(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return ConfirmAnswer;
    }

    public string? PickExecutable(string title, string? initialPath) => null;

    /// <summary>What the file picker "chooses" (null = cancelled).</summary>
    public string? PickFileResult { get; set; }

    public string? LastPickFileFilter { get; private set; }

    public string? PickFile(string title, string? initialPath, string? filter = null)
    {
        LastPickFileFilter = filter;
        return PickFileResult;
    }

    public string? PickFolder(string title, string? initialPath) => null;

    public void CopyToClipboard(string text) => Clipboard = text;
}
