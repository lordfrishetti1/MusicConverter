using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

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
                var scrollQueued = false;
                changes.CollectionChanged += (_, _) =>
                {
                    // Coalesce bursts of changes into one scroll after layout.
                    if (scrollQueued) return;
                    scrollQueued = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        scrollQueued = false;
                        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
                    });
                };
            }
        };
    }
}
