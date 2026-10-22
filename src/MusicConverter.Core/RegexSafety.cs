using System.Runtime.CompilerServices;

namespace MusicConverter;

/// <summary>
/// Gives every regular expression without an explicit timeout a default one, so a pathological line fails the
/// file instead of hanging the process. Runs when the assembly loads, before any regex is created.
/// </summary>
internal static class RegexSafety
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (AppContext.GetData("REGEX_DEFAULT_MATCH_TIMEOUT") is null)
            AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", DefaultTimeout);
    }
}
