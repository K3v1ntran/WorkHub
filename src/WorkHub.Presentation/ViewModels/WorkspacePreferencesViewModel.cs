using WorkHub.Core.Settings;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Phase 13A Workspace preferences shown in the main window: the tree, selection and content toolbars, each shown or hidden
/// on its own. The visible state is always the SAVED value: a toggle is not shown as done until the save succeeded, and a
/// failed save is reported and changes nothing.
/// </summary>
public sealed class WorkspacePreferencesViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Action<string> _reportError;
    private readonly SynchronizationContext? _ui;
    private bool _isSaving;

    /// <param name="reportError">Shows a save failure (the Workspace banner).</param>
    public WorkspacePreferencesViewModel(SettingsService settings, Action<string> reportError)
    {
        _settings = settings;
        _reportError = reportError;
        _ui = SynchronizationContext.Current;
        ShowTreeToolbarCommand = ShowCommand(WorkspaceToolbar.Tree, true);
        HideTreeToolbarCommand = ShowCommand(WorkspaceToolbar.Tree, false);
        ShowSelectionToolbarCommand = ShowCommand(WorkspaceToolbar.Selection, true);
        HideSelectionToolbarCommand = ShowCommand(WorkspaceToolbar.Selection, false);
        ShowContentToolbarCommand = ShowCommand(WorkspaceToolbar.Content, true);
        HideContentToolbarCommand = ShowCommand(WorkspaceToolbar.Content, false);
        settings.Changed += (_, _) => OnUiThread(RaiseSaved);
    }

    /// <summary>The saved tree toolbar preference (default shown).</summary>
    public bool ShowTreeToolbar => _settings.Current.Ui.Workspace.ShowTreeToolbar;

    /// <summary>The saved selection toolbar preference (default shown).</summary>
    public bool ShowSelectionToolbar => _settings.Current.Ui.Workspace.ShowSelectionToolbar;

    /// <summary>The saved content toolbar preference (default shown).</summary>
    public bool ShowContentToolbar => _settings.Current.Ui.Workspace.ShowContentToolbar;

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
                RaiseCommands();
        }
    }

    /// <summary>The tree toolbar's right-click menu.</summary>
    public AsyncRelayCommand ShowTreeToolbarCommand { get; }

    public AsyncRelayCommand HideTreeToolbarCommand { get; }

    /// <summary>The selection strip's right-click menu.</summary>
    public AsyncRelayCommand ShowSelectionToolbarCommand { get; }

    public AsyncRelayCommand HideSelectionToolbarCommand { get; }

    /// <summary>The content header's right-click menu.</summary>
    public AsyncRelayCommand ShowContentToolbarCommand { get; }

    public AsyncRelayCommand HideContentToolbarCommand { get; }

    public bool IsShown(WorkspaceToolbar toolbar) => _settings.Current.Ui.Workspace.IsShown(toolbar);

    /// <summary>Saves only <paramref name="toolbar"/>'s preference; the other toolbars are not written.</summary>
    public async Task SetToolbarAsync(WorkspaceToolbar toolbar, bool show)
    {
        if (show == IsShown(toolbar))
            return;

        IsSaving = true;
        SettingsSaveResult result;
        try
        {
            result = await _settings.SaveWorkspaceToolbarAsync(toolbar, show);
        }
        finally
        {
            IsSaving = false;
        }

        if (!result.Saved)
            _reportError($"Couldn't save the toolbar setting. {result.Error}");
        RaiseSaved(); // the saved values either way
    }

    private AsyncRelayCommand ShowCommand(WorkspaceToolbar toolbar, bool show) =>
        new(() => SetToolbarAsync(toolbar, show), () => !IsSaving && IsShown(toolbar) != show);

    private void RaiseSaved()
    {
        OnPropertyChanged(nameof(ShowTreeToolbar));
        OnPropertyChanged(nameof(ShowSelectionToolbar));
        OnPropertyChanged(nameof(ShowContentToolbar));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[]
                 {
                     ShowTreeToolbarCommand, HideTreeToolbarCommand, ShowSelectionToolbarCommand, HideSelectionToolbarCommand,
                     ShowContentToolbarCommand, HideContentToolbarCommand,
                 })
            command.RaiseCanExecuteChanged();
    }

    private void OnUiThread(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }
}
