using System.Reflection;
using MusicConverter.Logging;

namespace MusicConverter.Abstractions;

/// <summary>Reflection-based registry of <see cref="IMusicFormatConverter"/> implementations.</summary>
public sealed class ConverterRegistry
{
    private readonly List<IMusicFormatConverter> _converters = new();
    private readonly object _gate = new();
    private readonly ILogger _logger;

    public ConverterRegistry(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    public IReadOnlyList<IMusicFormatConverter> Converters
    {
        get
        {
            lock (_gate) return _converters.ToArray();
        }
    }

    /// <summary>File dialog filter covering every registered format.</summary>
    public string FileDialogFilter
    {
        get
        {
            var converters = Converters;
            var all = string.Join(";", converters.SelectMany(c => c.FileExtensions).Distinct().Select(e => "*" + e));
            var parts = new List<string> { $"All supported ({all})|{all}" };
            parts.AddRange(converters.Select(c =>
            {
                var pattern = string.Join(";", c.FileExtensions.Select(e => "*" + e));
                return $"{c.FormatName} ({pattern})|{pattern}";
            }));
            parts.Add("All files (*.*)|*.*");
            return string.Join("|", parts);
        }
    }

    public void Register(IMusicFormatConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        lock (_gate)
        {
            if (_converters.Any(c => c.GetType() == converter.GetType()))
            {
                _logger.Warning($"Converter {converter.GetType().Name} is already registered; skipping.");
                return;
            }
            _converters.Add(converter);
        }
        _logger.Info($"Registered converter '{converter.FormatName}' ({converter.GetType().FullName}).");
    }

    /// <summary>Discovers converters in the assembly containing the built-in converters.</summary>
    public int DiscoverConverters() => DiscoverConverters(typeof(ConverterRegistry).Assembly);

    public int DiscoverConverters(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var count = 0;
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;
            if (!typeof(IMusicFormatConverter).IsAssignableFrom(type)) continue;
            if (type.GetCustomAttribute<IgnoreConverterAttribute>() != null) continue;

            var converter = TryCreate(type);
            if (converter == null) continue;

            var before = Converters.Count;
            Register(converter);
            if (Converters.Count > before) count++;
        }
        return count;
    }

    /// <summary>Loads every *.dll in <paramref name="directory"/> and discovers converters in each.</summary>
    public int LoadPluginsFrom(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var count = 0;
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                count += DiscoverConverters(Assembly.LoadFrom(dll));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                _logger.Warning($"Skipping plugin '{dll}': {ex.Message}");
            }
        }
        return count;
    }

    public IMusicFormatConverter? FindConverter(string filePath)
    {
        foreach (var converter in Converters)
        {
            try
            {
                if (converter.CanRead(filePath)) return converter;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Converter '{converter.FormatName}' failed in CanRead: {ex.Message}");
            }
        }
        return null;
    }

    private IMusicFormatConverter? TryCreate(Type type)
    {
        try
        {
            var withLogger = type.GetConstructor(new[] { typeof(ILogger) });
            if (withLogger != null) return (IMusicFormatConverter)withLogger.Invoke(new object[] { _logger });

            var parameterless = type.GetConstructor(Type.EmptyTypes);
            if (parameterless != null) return (IMusicFormatConverter)parameterless.Invoke(null);

            _logger.Warning($"Converter {type.FullName} has no (ILogger) or parameterless constructor; skipping.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to instantiate converter {type.FullName}.", ex);
        }
        return null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
