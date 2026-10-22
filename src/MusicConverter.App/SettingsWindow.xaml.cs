using System.Windows;
using MusicConverter.Abstractions;

namespace MusicConverter.App;

public partial class SettingsWindow : Window
{
    private readonly ConversionSettings _target;
    private readonly Action<ConversionSettings> _persist;
    private readonly ConversionSettings _edit;

    public SettingsWindow(ConversionSettings target, Action<ConversionSettings> persist)
    {
        InitializeComponent();
        var levels = Enum.GetValues<MusicConverter.Logging.LogLevel>();
        FileLevelBox.ItemsSource = levels;
        UiLevelBox.ItemsSource = levels;
        _target = target;
        _persist = persist;
        _edit = target.Clone();
        DataContext = _edit;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _target.CopyFrom(_edit);
        _persist(_target);
        DialogResult = true;
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        _edit.CopyFrom(new ConversionSettings());
        DataContext = null;
        DataContext = _edit;
    }
}
