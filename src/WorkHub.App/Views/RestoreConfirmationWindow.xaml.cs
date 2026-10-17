using System.Windows;

namespace WorkHub.App.Views;

/// <summary>Phase 10 restore confirmation. Cancel is focused and is the Enter/Esc default.</summary>
public partial class RestoreConfirmationWindow : Window
{
    public RestoreConfirmationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }
}
