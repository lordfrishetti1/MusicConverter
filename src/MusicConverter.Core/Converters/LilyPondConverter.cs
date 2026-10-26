using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>
/// Imports LilyPond sources: header title/composer, \score, \new Staff/Voice, \relative, \time, \key,
/// \clef, \partial, \repeat volta, tuplets, ties, chords, rests and simple variables.
/// </summary>
public sealed class LilyPondConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".ly", ".ily" };

    public LilyPondConverter() : this(null) { }

    public LilyPondConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "LilyPond";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new LilyPondParser(ExpandIncludes(text, context), context).Parse();

    private static readonly System.Text.RegularExpressions.Regex IncludeRegex =
        new(@"^[ \t]*\\include[ \t]*""(?<file>[^""]+)""[ \t]*$", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Inlines <c>\include "file"</c> directives, resolving paths relative to the including file.</summary>
    private static string ExpandIncludes(string text, ConversionContext context)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(context.FilePath)) ?? string.Empty;
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(context.FilePath) };
        return Expand(text, baseDir, stack, context, 0);
    }

    private static string Expand(string text, string dir, HashSet<string> stack, ConversionContext context, int depth)
    {
        return IncludeRegex.Replace(text, match =>
        {
            context.ThrowIfCancelled();
            var name = match.Groups["file"].Value;
            var path = Path.GetFullPath(Path.Combine(dir, name));
            if (depth >= 16 || !stack.Add(path))
            {
                context.Warn($"Circular or too deeply nested \\include \"{name}\" skipped.");
                return string.Empty;
            }
            try
            {
                if (!File.Exists(path) && string.Equals(name, "english.ly", StringComparison.OrdinalIgnoreCase))
                {
                    // Built into LilyPond, so it is normally not next to the score.
                    return "\\language \"english\" ";
                }
                if (!File.Exists(path))
                {
                    context.Warn($"Included file '{name}' was not found and was skipped.");
                    return string.Empty;
                }
                var included = File.ReadAllText(path);
                return Expand(included, Path.GetDirectoryName(path) ?? dir, stack, context, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Warn($"Included file '{name}' could not be read: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                stack.Remove(path);
            }
        });
    }
}