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

    private static void PruneLogs(string directory, int retentionDays, ILogger logger)
    {
        if (retentionDays <= 0) return;
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "musicconverter-*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning($"Old log files could not be removed: {ex.Message}");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (ConversionWorker.IsWorkerInvocation(e.Args))
        {
            Shutdown(RunWorker(e.Args));
            return;
        }

        var uiLogger = new UiLogger();
        var logger = new CompositeLogger(new DebugLogger(), uiLogger);

        var service = CreateService(logger);
        var registry = service.Registry;
        var serializer = service.Serializer;
        var settingsStore = new SettingsStore(logger);
        service.Settings.CopyFrom(settingsStore.Load());
        uiLogger.MinimumLevel = service.Settings.UiLogLevel;

        if (service.Settings.FileLoggingEnabled)
        {
            // The log folder is a setting, so the file logger is attached once settings are loaded.
            var logDirectory = string.IsNullOrWhiteSpace(service.Settings.LogDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicConverter", "logs")
                : service.Settings.LogDirectory;
            try
            {
                logger.Add(new FileLogger(Path.Combine(logDirectory, $"musicconverter-{DateTime.Now:yyyyMMdd}.log"),
                    service.Settings.FileLogLevel));
                PruneLogs(logDirectory, service.Settings.LogRetentionDays, logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                logger.Warning($"Log folder '{logDirectory}' is unusable; file logging is disabled: {ex.Message}");
            }
        }
        var notifier = new BatchNotifier(service.Settings);
        Exit += (_, _) => notifier.Dispose();
        var isolatedRunner = Environment.ProcessPath is { } exe ? new ProcessConversionRunner(exe, service.Settings, logger) : null;
        var batch = new BatchConversionService(registry, service, logger, isolatedRunner);
        var viewModel = new MainViewModel(service, batch, registry, serializer, new WpfFileDialogService(),         uiLogger, logger, notifier);

        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow { DataContext = viewModel, Settings = service.Settings, SaveSettings = s => { settingsStore.Save(s); uiLogger.MinimumLevel = s.UiLogLevel; } }.Show();
    }
}
