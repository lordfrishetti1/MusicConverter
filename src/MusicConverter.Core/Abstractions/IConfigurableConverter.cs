namespace MusicConverter.Abstractions;

/// <summary>Implemented by converters that honour <see cref="ConversionSettings"/>; applied by the conversion service.</summary>
public interface IConfigurableConverter
{
    void Configure(ConversionSettings settings);
}