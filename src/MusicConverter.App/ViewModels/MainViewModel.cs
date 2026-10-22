using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MusicConverter.Abstractions;
using MusicConverter.App.Services;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Services;

namespace MusicConverter.App.ViewModels;

public enum ConversionStatus
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

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

public sealed class MainViewModel : ViewModelBase
{
    private readonly ConversionService _service;
    private readonly BatchConversionService _batchService;
    private readonly ConverterRegistry _registry;
    private readonly IFileDialogService _dialogs;
    private readonly ILogger _logger;
    private readonly IBatchNotifier? _notifier;
    private readonly AsyncRelayCommand _convertCommand;
    private readonly RelayCommand _cancelCommand;
    private CancellationTokenSource? _cts;

    private bool _isBatchMode, _recursive = true, _overwrite, _resume;
    private IReadOnlyCollection<string> _failedFiles = Array.Empty<string>();
    private IReadOnlyCollection<string>? _retryOnly;
    private readonly AsyncRelayCommand _retryCommand;
    private int _batchIndex = 1, _batchTotal = 1;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _detectedFormat = "No file selected";
    private string _selectedVersion;
    private double _overallPercent, _parsingPercent, _convertingPercent, _serializingPercent;
    private string _progressMessage = "Ready";
    private string _statusText = "Idle";
    private string? _errorMessage;
    private ConversionStatus _status = ConversionStatus.Idle;

    public MainViewModel(
        ConversionService service, BatchConversionService batchService, ConverterRegistry registry, MusicXmlSerializer serializer,
        IFileDialogService dialogs, UiLogger uiLogger, ILogger logger, IBatchNotifier? notifier = null)
    {
        _notifier = notifier;
        _service = service;
        _batchService = batchService;
        _registry = registry;
        _dialogs = dialogs;
        _logger = logger;
        LogEntries = uiLogger.Entries;
        AvailableVersions = serializer.SupportedVersions.ToArray();
        _selectedVersion = serializer.DefaultVersion;
        foreach (var ext in batchService.SupportedExtensions)
            ExtensionFilters.Add(new ExtensionFilter(ext, OnFiltersChanged));

        BrowseInputCommand = new RelayCommand(BrowseInput, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ClearLogCommand = new RelayCommand(LogEntries.Clear);
        _convertCommand = new AsyncRelayCommand(ConvertAsync, () => !IsBusy && InputPath.Length > 0
            && (IsBatchMode ? ExtensionFilters.Any(f => f.IsSelected) : OutputPath.Length > 0));
        _cancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        _retryCommand = new AsyncRelayCommand(RetryFailedAsync, () => HasFailedFiles && IsBatchMode);
    }

    private async Task RetryFailedAsync()
    {
        _retryOnly = _failedFiles;
        try { await ConvertAsync(); }
        finally { _retryOnly = null; }
    }

    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (!Set(ref _isBatchMode, value)) return;
            _inputPath = _outputPath = string.Empty;
            Raise(nameof(InputPath));
            Raise(nameof(OutputPath));
            Raise(nameof(InputLabel));
            Raise(nameof(OutputLabel));
            DetectedFormat = value ? "Directory" : "No file selected";
            RefreshCommands();
        }
    }

    public bool Recursive { get => _recursive; set => Set(ref _recursive, value); }

    public ObservableCollection<ExtensionFilter> ExtensionFilters { get; } = new();

    /// <summary>True when every extension is selected; setting it selects or clears them all.</summary>
    public bool? AllExtensionsSelected
    {
        get
        {
            var selected = ExtensionFilters.Count(f => f.IsSelected);
            return selected == 0 ? false : selected == ExtensionFilters.Count ? true : null;
        }
        set
        {
            var select = value ?? false;
            _updatingFilters = true;
            foreach (var f in ExtensionFilters) f.IsSelected = select;
            _updatingFilters = false;
            OnFiltersChanged();
        }
    }

    private bool _updatingFilters;

    private void OnFiltersChanged()
    {
        if (_updatingFilters) return;
        Raise(nameof(AllExtensionsSelected));
        RefreshCommands();
    }
    public bool Overwrite { get => _overwrite; set => Set(ref _overwrite, value); }
    public bool Resume { get => _resume; set => Set(ref _resume, value); }
    public bool HasFailedFiles => _failedFiles.Count > 0 && !IsBusy;
    public ICommand RetryFailedCommand => _retryCommand;
    public string InputLabel => IsBatchMode ? "Input folder" : "Input file";
    public string OutputLabel => IsBatchMode ? "Output folder" : "Output file";

    public ObservableCollection<string> LogEntries { get; }
    public IReadOnlyList<string> AvailableVersions { get; }

    public ICommand BrowseInputCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ConvertCommand => _convertCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand ClearLogCommand { get; }

    public bool IsBusy => Status == ConversionStatus.Running;
    public bool CanEditSettings => !IsBusy;

    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (!Set(ref _inputPath, value.Trim())) return;
            if (IsBatchMode) { RefreshCommands(); return; }
            var converter = string.IsNullOrEmpty(_inputPath) ? null : _registry.FindConverter(_inputPath);
            DetectedFormat = string.IsNullOrEmpty(_inputPath) ? "No file selected"
                : converter?.FormatName ?? "Unsupported format";
            if (converter != null && _inputPath.Length > 0)
                OutputPath = Path.ChangeExtension(_inputPath, ".musicxml");
            RefreshCommands();
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (Set(ref _outputPath, value.Trim())) RefreshCommands();
        }
    }

    public string DetectedFormat { get => _detectedFormat; private set => Set(ref _detectedFormat, value); }

    public string SelectedVersion { get => _selectedVersion; set => Set(ref _selectedVersion, value); }

    public double OverallPercent { get => _overallPercent; private set => Set(ref _overallPercent, value); }
    public double ParsingPercent { get => _parsingPercent; private set => Set(ref _parsingPercent, value); }
    public double ConvertingPercent { get => _convertingPercent; private set => Set(ref _convertingPercent, value); }
    public double SerializingPercent { get => _serializingPercent; private set => Set(ref _serializingPercent, value); }

    public string ProgressMessage { get => _progressMessage; private set => Set(ref _progressMessage, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ConversionStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsBusy));
            Raise(nameof(CanEditSettings));
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        _convertCommand.RaiseCanExecuteChanged();
        _retryCommand.RaiseCanExecuteChanged();
        Raise(nameof(HasFailedFiles));
        _cancelCommand.RaiseCanExecuteChanged();
        (BrowseInputCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseOutputCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void BrowseInput()
    {
        var path = IsBatchMode ? _dialogs.PickFolder("Select a folder of music files") : _dialogs.PickInputFile(_registry.FileDialogFilter);
        if (path != null) InputPath = path;
    }

    private void BrowseOutput()
    {
        if (IsBatchMode)
        {
            var folder = _dialogs.PickFolder("Select the output folder");
            if (folder != null) OutputPath = folder;
            return;
        }
        var path = _dialogs.PickOutputFile(string.IsNullOrEmpty(OutputPath) ? "output.musicxml" : OutputPath);
        if (path != null) OutputPath = path;
    }

    private void OnProgress(ConversionProgress p)
    {
        OverallPercent = IsBatchMode ? 100.0 * (_batchIndex - 1 + p.Percent / 100.0) / _batchTotal : p.Percent;
        if (!IsBatchMode) ProgressMessage = p.Message;
        ParsingPercent = p.Stage > ConversionStage.Parsing ? 100 : p.Stage == ConversionStage.Parsing ? p.StagePercent : 0;
        ConvertingPercent = p.Stage > ConversionStage.Converting ? 100 : p.Stage == ConversionStage.Converting ? p.StagePercent : 0;
        SerializingPercent = p.Stage == ConversionStage.Completed ? 100 : p.Stage == ConversionStage.Serializing ? p.StagePercent : 0;
        StatusText = p.Stage switch
        {
            ConversionStage.Parsing => "Parsing…",
            ConversionStage.Converting => "Converting…",
            ConversionStage.Serializing => "Serializing…",
            _ => StatusText
        };
    }

    private async Task ConvertBatchAsync(IProgress<ConversionProgress> fileProgress)
    {
        var options = new BatchOptions
        {
            Recursive = Recursive,
            Overwrite = Overwrite,
            Resume = Resume || _retryOnly != null,
            OnlyFiles = _retryOnly,
            MusicXmlVersion = SelectedVersion,
            OutputDirectory = string.IsNullOrWhiteSpace(OutputPath) ? null : OutputPath,
            Extensions = ExtensionFilters.Where(f => f.IsSelected).Select(f => f.Extension).ToArray()
        };
        var batchProgress = new Progress<BatchProgress>(b =>
        {
            _batchIndex = Math.Max(1, b.Index);
            _batchTotal = Math.Max(1, b.Total);
            ProgressMessage = b.Message;
            StatusText = $"Converting {b.Index}/{b.Total}";
            if (b.Message == "Batch complete") OverallPercent = 100;
        });

        _batchIndex = _batchTotal = 1;
        var result = await _batchService.ConvertDirectoryAsync(InputPath, options, batchProgress, fileProgress, _cts!.Token);

        var failures = result.Items.Where(i => i.Status == BatchItemStatus.Failed).ToList();
        _failedFiles = failures.Select(f => f.InputPath).ToArray();
        if (failures.Count > 0)
            ErrorMessage = string.Join(Environment.NewLine,
                failures.Take(10).Select(f => $"{Path.GetFileName(f.InputPath)}: {f.Message}")) +
                (failures.Count > 10 ? $"{Environment.NewLine}…and {failures.Count - 10} more (see log)" : "");

        ProgressMessage = result.Items.Count == 0
            ? "No supported files found"
            : $"{result.Succeeded} converted ({result.WithWarnings.Count} with warnings), {result.Failed} failed" +
              (result.WithMismatches.Count > 0 ? $" ({result.WithMismatches.Count} with output mismatches, see log)" : "") +
              (result.StoppedBySafeguards > 0 ? $" ({result.StoppedBySafeguards} stopped by safety limits)" : "") +
              $", {result.Skipped} skipped";
        StatusText = ProgressMessage;
        Status = result.Failed > 0 ? ConversionStatus.Failed : ConversionStatus.Succeeded;
        _notifier?.BatchCompleted(ProgressMessage, result.Failed > 0);
    }

    private async Task ConvertAsync()
    {
        _cts = new CancellationTokenSource();
        if (_retryOnly == null) _failedFiles = Array.Empty<string>();
        ErrorMessage = null;
        OverallPercent = ParsingPercent = ConvertingPercent = SerializingPercent = 0;
        Status = ConversionStatus.Running;
        StatusText = "Starting…";

        // Created on the UI thread so reports are marshalled back to it.
        var progress = new Progress<ConversionProgress>(OnProgress);

        try
        {
            if (IsBatchMode)
            {
                await ConvertBatchAsync(progress);
                return;
            }

            var result = await _service.ConvertFileAsync(
                InputPath, OutputPath, SelectedVersion, progress, _cts.Token);
            OnProgress(ConversionProgress.ForStage(ConversionStage.Completed, 1, "Conversion complete"));
            StatusText = result.HasMismatch
                ? $"Completed, but verification found {result.VerificationIssues!.Count} difference(s)"
                : result.WarningCount > 0
                    ? $"Completed with {result.WarningCount} warning(s)"
                    : result.Verified ? "Completed and verified" : "Completed";
            if (result.HasMismatch) ErrorMessage = string.Join(Environment.NewLine, result.VerificationIssues!);
            Status = ConversionStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled";
            StatusText = "Cancelled";
            Status = ConversionStatus.Cancelled;
        }
        catch (ConversionException ex)
        {
            ErrorMessage = $"{ex.Category}: {ex.Message}";
            StatusText = "Failed";
            ProgressMessage = "Conversion failed";
            Status = ConversionStatus.Failed;
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error during conversion.", ex);
            ErrorMessage = $"Unexpected error: {ex.Message}";
            StatusText = "Failed";
            ProgressMessage = "Conversion failed";
            Status = ConversionStatus.Failed;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }
}
