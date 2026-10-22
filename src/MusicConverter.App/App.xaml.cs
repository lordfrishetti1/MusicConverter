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
    private static ConversionService CreateService(ILogger logger)
    {
        var registry = new ConverterRegistry(logger);
        registry.DiscoverConverters();
        registry.LoadPluginsFrom(Path.Combine(AppContext.BaseDirectory, "plugins"));
        return new ConversionService(registry, new MusicXmlSerializer(logger), logger);
    }

    /// <summary>Runs as a headless worker: converts one file, prints a JSON result and exits.</summary>
    private static int RunWorker(string[] args)
    {
        var exitCode = 2;
        // A large stack gives deeply nested input more room before the nesting limit applies.
        var thread = new Thread(() =>
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            exitCode = ConversionWorker.RunAsync(args, () => CreateService(NullLogger.Instance), stdout).GetAwaiter().GetResult();
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (ConversionWorker.IsWorkerInvocation(e.Args))
        {
            Shutdown(RunWorker(e.Args));
            return;
        }

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicConverter", "logs");
        var uiLogger = new UiLogger();
        var logger = new CompositeLogger(
            new DebugLogger(),
            new FileLogger(Path.Combine(logDirectory, $"musicconverter-{DateTime.Now:yyyyMMdd}.log")),
            uiLogger);

        var service = CreateService(logger);
        var registry = service.Registry;
        var serializer = service.Serializer;
        var settingsStore = new SettingsStore(logger);
        service.Settings.CopyFrom(settingsStore.Load());
        var isolatedRunner = Environment.ProcessPath is { } exe ? new ProcessConversionRunner(exe, service.Settings, logger) : null;
        var batch = new BatchConversionService(registry, service, logger, isolatedRunner);
        var viewModel = new MainViewModel(service, batch, registry, serializer, new WpfFileDialogService(), uiLogger, logger);

        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow { DataContext = viewModel, Settings = service.Settings, SaveSettings = settingsStore.Save }.Show();
    }
}
