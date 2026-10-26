namespace MusicConverter.Abstractions;

/// <summary>Excludes a converter class from automatic discovery.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IgnoreConverterAttribute : Attribute
{
}