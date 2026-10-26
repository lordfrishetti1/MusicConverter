namespace MusicConverter.Abstractions;

public class ConversionException : Exception
{
    public ConversionException(string message, ConversionErrorCategory category, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
    }

    public ConversionErrorCategory Category { get; }

    public string? FilePath { get; set; }

    public string? ConverterName { get; set; }

    public int? Line { get; set; }
}