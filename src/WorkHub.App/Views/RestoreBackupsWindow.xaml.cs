using System.Windows;

namespace WorkHub.App.Views;

/// <summary>Hosts the backups list with Restore… for the Failed shell. View only.</summary>
public partial class RestoreBackupsWindow : Window
{
    public RestoreBackupsWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
