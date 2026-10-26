using System.Text.Json;
using MusicConverter.Abstractions;

namespace MusicConverter.Services;

/// <summary>Entry point of a worker process: converts exactly one file and reports the outcome as JSON.</summary>
public static class ConversionWorker
{
    public const string Flag = "--worker";

    public static bool IsWorkerInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Flag;

    /// <summary>Command line for a worker: <c>--worker input output version settingsJson</c> (version may be empty).</summary>
    public static IEnumerable<string> BuildArguments(string input, string output, string? version, ConversionSettings settings) =>
        new[] { Flag, input, output, version ?? string.Empty, JsonSerializer.Serialize(settings) };

    /// <returns>0 on success, 1 for a reported conversion failure, 2 for bad arguments.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<ConversionService> createService, TextWriter output)
    {
        if (args.Count < 5 || args[0] != Flag)
        {
            await WriteAsync(output, new WorkerResponse(false, Message: "Invalid worker arguments.")).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var service = createService();
            var settings = JsonSerializer.Deserialize<ConversionSettings>(args[4]) ?? new ConversionSettings();
            settings.Clamp();
            service.Settings.CopyFrom(settings);
            var version = string.IsNullOrEmpty(args[3]) ? null : args[3];
            var result = await service.ConvertFileAsync(args[1], args[2], version).ConfigureAwait(false);
            await WriteAsync(output, new WorkerResponse(true, result)).ConfigureAwait(false);
            return 0;
        }
        catch (ConversionException ex)
        {
            await WriteAsync(output, new WorkerResponse(false, Category: ex.Category, Message: ex.Message, Line: ex.Line)).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await WriteAsync(output, new WorkerResponse(false, Message: ex.Message)).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WriteAsync(TextWriter output, WorkerResponse response)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
