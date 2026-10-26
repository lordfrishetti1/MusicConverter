using MusicConverter.Abstractions;
using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports MusiXTeX sources (.tex / .mtx) that use the core note, rest and signature macros.</summary>
public sealed class MusiXTeXConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".tex", ".mtx" };

    private static readonly Regex Signature = new(
        @"musixtex|\\startpiece|\\startextract|\\begin\s*\{music\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MusiXTeXConverter() : this(null) { }

    public MusiXTeXConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MusiXTeX";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    public override bool CanRead(string filePath)
    {
        if (!base.CanRead(filePath)) return false;
        if (string.Equals(Path.GetExtension(filePath), ".mtx", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var reader = new StreamReader(filePath);
            var buffer = new char[65536];
            var read = reader.Read(buffer, 0, buffer.Length);
            return Signature.IsMatch(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MusiXTeXParser(text, context).Parse();
}