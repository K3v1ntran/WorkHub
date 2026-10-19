using System.Windows;
using WorkHub.App.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.App.Views;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.App;

/// <summary>Composition root. Shows the window immediately and initializes storage in the background.</summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Arguments are validated before any folder, log or database exists. An invalid explicit --data-root
        // stops here: WorkHub never falls back to the real profile when an override was requested.
        var options = StartupOptions.Parse(e.Args, WorkHubPaths.ForCurrentUser().Root);
        if (!options.IsValid)
        {
            MessageBox.Show(options.Error, "WorkHub — invalid startup option", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var services = WorkHubServices.Create(options.Options!.Paths, options.Options.RestoreContinueOp);
        Exit += (_, _) => services.Dispose();
        if (options.Options.IsCustomDataRoot)
            services.Log.Info($"Using custom data root ({StartupOptions.DataRootOption}): {services.Paths.Root}");
        DispatcherUnhandledException += (_, args) =>
        {
            services.Log.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "WorkHub", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var dialogs = new DialogService();
        var lifetime = new AppLifetime(options.Options);
        MainViewModel? main = null;
        // Phase 13A: the Backups page shows this start's technical status (formerly in the header).
        BackupsViewModel Backups() => new(services.Backups, services.RestoreRequester, dialogs, lifetime, services.Log)
        {
            StatusLine = main?.TechnicalStatus,
        };
        var repository = new RepositoryAreaViewModel(
            services.Systems,
            services.RepositoryOpener,
            services.Settings,
            services.LegacyImport,
            dialogs,
            services.Log,
            () => TortoiseLocator.Detect(),
            backups: Backups);

        var owner = new OwnerAreaViewModel(services.Owners, dialogs, services.Log);
        var navigation = new RepositoryNavigationViewModel(repository, owner);
        var workspace = new WorkspaceAreaViewModel(services.Workspace, dialogs, services.Log, services.ProjectContent, services.LinkChecker, services.WorkspaceSearch,
            settings: services.Settings);

        var viewModel = main = new MainViewModel(services.Initializer, services.Settings, navigation, workspace, services.Log, services.RestoreOwner, lifetime, dialogs, Backups)
        {
            CustomDataRoot = options.Options.IsCustomDataRoot ? services.Paths.Root : null,
            AppVersion = AppVersionText(),
        };
        if (options.Options.RestoreContinueOp is { } op)
            services.Log.Info($"Started to continue restore operation {op}.");
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        await viewModel.InitializeAsync();
    }

    /// <summary>"1.1.0 (a194be4)" from the informational version stamped at build time.</summary>
    private static string AppVersionText()
    {
        var info = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? string.Empty;
        var plus = info.IndexOf('+');
        return plus < 0 ? info : $"{info[..plus]} ({info[(plus + 1)..Math.Min(info.Length, plus + 8)]})";
    }
}
