using System.Windows;
using WorkHub.Presentation.Services;

namespace WorkHub.App.Views;

/// <summary>Save / Don't save / Cancel. Cancel, Esc and closing the window all mean Cancel.</summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Loaded += (_, _) => SaveButton.Focus();
    }

    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsavedChangesChoice.Save;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsavedChangesChoice.Discard;
        DialogResult = true;
    }
}
