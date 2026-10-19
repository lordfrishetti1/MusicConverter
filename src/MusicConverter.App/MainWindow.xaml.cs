using System.Collections.Specialized;
using System.Windows;

namespace MusicConverter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (LogList.Items is INotifyCollectionChanged changes)
            {
                changes.CollectionChanged += (_, _) =>
                {
                    if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
                };
            }
        };
    }
}
