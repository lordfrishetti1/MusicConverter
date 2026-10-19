using System.IO;
using System.Windows;
using MusicConverter.Abstractions;
using MusicConverter.App.Services;
using MusicConverter.App.ViewModels;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Services;

namespace MusicConverter.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicConverter", "logs");
        var uiLogger = new UiLogger();
        var logger = new CompositeLogger(
            new DebugLogger(),
            new FileLogger(Path.Combine(logDirectory, $"musicconverter-{DateTime.Now:yyyyMMdd}.log")),
            uiLogger);

        var registry = new ConverterRegistry(logger);
        registry.DiscoverConverters();
        registry.LoadPluginsFrom(Path.Combine(AppContext.BaseDirectory, "plugins"));

        var serializer = new MusicXmlSerializer(logger);
        var service = new ConversionService(registry, serializer, logger);
        var batch = new BatchConversionService(registry, service, logger);
        var viewModel = new MainViewModel(service, batch, registry, serializer, new WpfFileDialogService(), uiLogger, logger);

        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow { DataContext = viewModel }.Show();
    }
}
