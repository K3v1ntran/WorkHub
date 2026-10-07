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

        var services = WorkHubServices.Create(WorkHubPaths.ForCurrentUser());
        DispatcherUnhandledException += (_, args) =>
        {
            services.Log.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "WorkHub", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var dialogs = new DialogService();
        var repository = new RepositoryAreaViewModel(
            services.Systems,
            services.RepositoryOpener,
            services.Settings,
            dialogs,
            services.Log,
            () => TortoiseLocator.Detect());

        var owner = new OwnerAreaViewModel(services.Owners, dialogs, services.Log);
        var navigation = new RepositoryNavigationViewModel(repository, owner);

        var viewModel = new MainViewModel(services.Initializer, services.Settings, navigation, services.Log);
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        await viewModel.InitializeAsync();
    }
}
