using System.ComponentModel;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Repository-tab sidebar: the five fixed Repository groups and the user-defined Owner groups are two simple lists
/// with one active selection between them. Selecting a Repository group shows the System list; selecting an Owner
/// group shows that group's launcher list. The two areas keep their own data and never reload each other.
/// </summary>
public sealed class RepositoryNavigationViewModel : ObservableObject
{
    private readonly RepositoryAreaViewModel _repository;
    private readonly OwnerAreaViewModel _owner;
    private RepositoryCategoryViewModel? _selectedCategory;
    private bool _isOwnerActive;

    public RepositoryNavigationViewModel(RepositoryAreaViewModel repository, OwnerAreaViewModel owner)
    {
        _repository = repository;
        _owner = owner;
        _selectedCategory = repository.SelectedCategory;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        owner.Groups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOwnerGroups));
    }

    public RepositoryAreaViewModel Repository => _repository;

    public OwnerAreaViewModel Owner => _owner;

    /// <summary>Selected fixed group, or null while an Owner group is active.</summary>
    public RepositoryCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null)
            {
                // The list clears its selection when an Owner group becomes active; nothing to do.
                SetProperty(ref _selectedCategory, null);
                return;
            }

            SetProperty(ref _selectedCategory, value);
            IsOwnerActive = false;
            _owner.SelectedGroup = null;
            _repository.SelectedCategory = value;
        }
    }

    public bool IsOwnerActive
    {
        get => _isOwnerActive;
        private set
        {
            if (SetProperty(ref _isOwnerActive, value))
                OnPropertyChanged(nameof(IsRepositoryActive));
        }
    }

    public bool IsRepositoryActive => !IsOwnerActive;

    public bool HasOwnerGroups => _owner.Groups.Count > 0;

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OwnerAreaViewModel.SelectedGroup))
            return;

        if (_owner.SelectedGroup is not null)
        {
            IsOwnerActive = true;
            SelectedCategory = null;
        }
        else if (IsOwnerActive)
        {
            // The active Owner group went away (e.g. the last group was deleted): fall back to the Repository group.
            IsOwnerActive = false;
            SetProperty(ref _selectedCategory, _repository.SelectedCategory, nameof(SelectedCategory));
        }
    }
}
