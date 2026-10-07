using System.Windows;
using WorkHub.App.ViewModels;
using WorkHub.App.Views;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure;

namespace WorkHub.App;

/// <summary>Composition root. Shows the window immediately and initializes storage in the background.</summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = WorkHubServices.Create(WorkHubPaths.ForCurrentUser());
        DispatcherUnhandledException += (_, args) => services.Log.Error("Unhandled UI exception.", args.Exception);

        var viewModel = new MainViewModel(services.Initializer, services.Log);
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        await viewModel.InitializeAsync();
    }
}
