namespace MusicConverter.App.ViewModels;

public sealed class ExtensionFilter : ViewModelBase
{
    private readonly Action _changed;
    private bool _isSelected = true;

    public ExtensionFilter(string extension, Action changed)
    {
        Extension = extension;
        _changed = changed;
    }

    public string Extension { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _changed();
        }
    }
}