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

    /// <summary>
    /// Waits inside a dialog callback the way a WPF modal dialog does: on a <see cref="SingleThreadContext"/> it keeps
    /// dispatching (nested pump) until the condition holds; elsewhere it is the plain blocking wait tests always used.
    /// </summary>
    public bool PumpWhileModal(Func<bool> condition, TimeSpan timeout) =>
        SingleThreadContext.WaitOrPump(condition, t => SpinWait.SpinUntil(condition, t), timeout);

    /// <summary>Completes <paramref name="task"/> from inside a dialog callback (see <see cref="PumpWhileModal"/>) and rethrows its fault.</summary>
    public void WaitModal(Task task, TimeSpan timeout)
    {
        Assert.True(PumpWhileModal(() => task.IsCompleted, timeout), "the operation started inside the dialog did not complete");
        task.GetAwaiter().GetResult();
    }

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

    public Func<ProjectFolderNameEditorViewModel, bool> OnProjectFolderNameEditor { get; set; } = _ => false;

    public Func<ProjectLinkEditorViewModel, bool> OnProjectLinkEditor { get; set; } = _ => false;

    public Func<ProjectContentMoveViewModel, bool> OnProjectContentMove { get; set; } = _ => false;

    public bool ShowProjectFolderNameEditor(ProjectFolderNameEditorViewModel viewModel) => OnProjectFolderNameEditor(viewModel);

    public bool ShowProjectLinkEditor(ProjectLinkEditorViewModel viewModel) => OnProjectLinkEditor(viewModel);

    public bool ShowProjectContentMove(ProjectContentMoveViewModel viewModel) => OnProjectContentMove(viewModel);

    /// <summary>Plays the note editor. The default closes it through the normal gate (RequestClose).</summary>
    public Func<NoteEditorViewModel, bool> OnNoteEditor { get; set; } = editor =>
    {
        editor.RequestClose();
        return editor.SavedAny;
    };

    public bool ShowNoteEditor(NoteEditorViewModel viewModel) => OnNoteEditor(viewModel);

    /// <summary>What the unsaved-changes prompt answers (default: Cancel, as when the prompt window is closed).</summary>
    public Func<string, UnsavedChangesChoice> OnUnsavedChanges { get; set; } = _ => UnsavedChangesChoice.Cancel;

    public int UnsavedChangesPrompts { get; private set; }

    public UnsavedChangesChoice AskUnsavedChanges(string title, string message)
    {
        UnsavedChangesPrompts++;
        return OnUnsavedChanges(message);
    }

    /// <summary>Runs while the confirmation is "open" (the user reading it), before <see cref="ConfirmAnswer"/> is returned.</summary>
    public Action<string>? OnConfirm { get; set; }

    public bool Confirm(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        OnConfirm?.Invoke(message);
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

    /// <summary>Plays the restore confirmation (default: Cancel).</summary>
    public Func<RestoreConfirmationViewModel, bool> OnRestoreConfirmation { get; set; } = _ => false;

    public bool ShowRestoreConfirmation(RestoreConfirmationViewModel viewModel) => OnRestoreConfirmation(viewModel);

    public List<BackupsViewModel> RestoreBackupsShown { get; } = [];

    public void ShowRestoreBackups(BackupsViewModel viewModel) => RestoreBackupsShown.Add(viewModel);

    public List<(string Title, string Message)> Messages { get; } = [];

    public void ShowMessage(string title, string message) => Messages.Add((title, message));
}
