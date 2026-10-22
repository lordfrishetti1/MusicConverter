using System.IO;
using System.Text.Json;
using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.App.Services;

/// <summary>Loads and saves <see cref="ConversionSettings"/> as JSON under %LOCALAPPDATA%\MusicConverter.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger _logger;

    public SettingsStore(ILogger logger)
    {
        _logger = logger;
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicConverter", "settings.json");
    }

    public ConversionSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<ConversionSettings>(File.ReadAllText(_path)) ?? new ConversionSettings();
                settings.Clamp();
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning($"Settings could not be loaded; defaults are used: {ex.Message}");
        }
        return new ConversionSettings();
    }

    public void Save(ConversionSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Settings could not be saved: {ex.Message}");
        }
    }
}
