using WorkHub.App.Mvvm;
using WorkHub.Core.Repositories;

namespace WorkHub.App.ViewModels;

/// <summary>
/// Repository tab. Built-in categories come from <see cref="RepositoryCatalog"/>, not the database.
/// System lists and Owner groups are loaded in later phases.
/// </summary>
public sealed class RepositoryAreaViewModel : ObservableObject
{
    private RepositoryCategoryViewModel? _selectedCategory;

    public RepositoryAreaViewModel()
    {
        Categories = RepositoryCatalog.All.Select(info => new RepositoryCategoryViewModel(info)).ToList();
        _selectedCategory = Categories[0];
    }

    public IReadOnlyList<RepositoryCategoryViewModel> Categories { get; }

    public RepositoryCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }
}

public sealed class RepositoryCategoryViewModel(RepositoryTypeInfo info)
{
    public RepositoryType Type => info.Type;

    public string DisplayName => info.DisplayName;

    public string VersionControl => info.VersionControl.ToString().ToUpperInvariant();

    public string EmptyStateText => $"No systems in {info.DisplayName} yet.";
}
