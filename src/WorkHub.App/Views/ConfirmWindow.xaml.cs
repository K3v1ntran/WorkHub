using System.Windows;

namespace WorkHub.App.Views;

/// <summary>Confirmation dialog with an explicit action button (e.g. "Delete") instead of a generic OK.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
