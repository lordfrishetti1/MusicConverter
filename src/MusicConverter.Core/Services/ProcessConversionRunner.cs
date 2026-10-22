using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.Services;

/// <summary>
/// Converts each file in a separate worker process. A stack overflow, out-of-memory crash or hang then only
/// fails that file: cancelling or timing out kills the whole worker process tree.
/// </summary>
public sealed class ProcessConversionRunner : IFileConverter
{
    private readonly string _workerExecutable;
    private readonly ConversionSettings _settings;
    private readonly ILogger _logger;

    public ProcessConversionRunner(string workerExecutable, ConversionSettings settings, ILogger? logger = null)
    {
        _workerExecutable = workerExecutable;
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ConversionResult> ConvertFileAsync(
        string inputPath, string outputPath, string? musicXmlVersion = null,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(_workerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in ConversionWorker.BuildArguments(inputPath, outputPath, musicXmlVersion, _settings))
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)
            ?? throw Failure(inputPath, "The worker process could not be started.");
        if (_settings.LowPriority) TrySetPriority(process);

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        var response = ParseResponse(output);
        if (response is { Ok: true, Result: not null }) return response.Result;
        if (response is { Ok: false } && process.ExitCode == 1)
        {
            throw new ConversionException(response.Message ?? "Conversion failed.", response.Category)
            {
                FilePath = inputPath,
                Line = response.Line
            };
        }

        var detail = string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error.Trim()}";
        throw Failure(inputPath, $"The worker process crashed (exit code 0x{process.ExitCode:X}).{detail}");
    }

    private ConversionException Failure(string input, string message)
    {
        _logger.Error($"{message} File: '{input}'.");
        return new ConversionException(message, ConversionErrorCategory.WorkerFailure) { FilePath = input };
    }

    private static WorkerResponse? ParseResponse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (line is null) return null;
        try { return JsonSerializer.Deserialize<WorkerResponse>(line); }
        catch (JsonException) { return null; }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void TrySetPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
