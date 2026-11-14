using System.Windows;
using System.Windows.Input;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

public partial class ProjectEditorWindow : Window
{
    public ProjectEditorWindow()
    {
        InitializeComponent();
    }

    private ProjectEditorViewModel? ViewModel => DataContext as ProjectEditorViewModel;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.LoadSuggestionsAsync(); // a failure only means no suggestions (handled in the view model)
    }

    /// <summary>Enter adds the typed tag instead of pressing Save; Backspace in an empty box removes the last chip.</summary>
    private void TagBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && vm.TagInput.Trim().Length > 0)
        {
            vm.AddPendingTag();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && vm.TagInput.Length == 0)
        {
            vm.RemoveLastTag();
            e.Handled = true;
        }
    }

    /// <summary>A typed comma adds the tag (it is not part of the name).</summary>
    private void TagBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "," && ViewModel is { } vm)
        {
            vm.AddPendingTag();
            e.Handled = true;
        }
    }
}
