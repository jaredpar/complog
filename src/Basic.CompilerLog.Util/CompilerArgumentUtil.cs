using System.Text.RegularExpressions;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Utility methods for parsing and manipulating compiler command line arguments.
/// </summary>
internal static partial class CompilerArgumentUtil
{
    /// <summary>
    /// Options start with a slash, then contain a colon and continue with a path,
    /// or end with +/- or nothing. Examples:
    /// <list type="bullet">
    /// <item><c>/ref:...</c></item>
    /// <item><c>/unsafe+</c></item>
    /// <item><c>/checked-</c></item>
    /// <item><c>/noconfig</c></item>
    /// </list>
    /// </summary>
    /* lang=regex */
    private const string OptionRegexContent = @"^/[a-z0-9]+(:|[+-]?$)";

#if NET
    [GeneratedRegex(OptionRegexContent, RegexOptions.IgnoreCase)]
    private static partial Regex GetOptionRegex();
    private static Regex OptionsRegex => GetOptionRegex();
#else
    private static Regex OptionsRegex { get; } = new Regex(OptionRegexContent, RegexOptions.IgnoreCase);
#endif

    /// <summary>
    /// Checks if an argument is a compiler option (starts with / and has valid option format)
    /// </summary>
    internal static bool IsOption(ReadOnlySpan<char> arg) => OptionsRegex.IsMatch(arg);

    /// <summary>
    /// Checks if the given option name takes a path argument.
    /// </summary>
    internal static bool IsPathOption(ReadOnlySpan<char> optionName) =>
        optionName switch
        {
            "reference" or "r" or
            "analyzer" or "a" or
            "additionalfile" or
            "analyzerconfig" or
            "embed" or
            "resource" or
            "res" or
            "linkresource" or "linkres" or
            "sourcelink" or
            "ruleset" or
            "keyfile" or
            "link" or "l" or
            "out" or
            "refout" or
            "doc" or
            "generatedfilesout" or
            "pdb" or
            "errorlog" or
            "win32manifest" or
            "win32res" or
            "win32icon" or
            "addmodule" or
            "appconfig" or
            "lib" => true,
            _ => false
        };

    /// <summary>
    /// Tries to parse an option argument into its name and value parts.
    /// </summary>
    /// <param name="arg">The argument to parse (e.g., "/reference:path")</param>
    /// <param name="optionName">The option name without the leading slash (e.g., "reference")</param>
    /// <param name="optionValue">The value after the colon (e.g., "path")</param>
    /// <returns>True if the argument is an option with a value, false otherwise</returns>
    internal static bool TryParseOption(string arg, out ReadOnlySpan<char> optionName, out string optionValue)
    {
        optionName = default;
        optionValue = "";

        if (!IsOption(arg.AsSpan()))
        {
            return false;
        }

        var span = arg.AsSpan()[1..];
        var colonIndex = span.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        optionName = span[..colonIndex];
        optionValue = span[(colonIndex + 1)..].ToString();
        return true;
    }

    /// <summary>
    /// Adds quotes around a path if it contains spaces.
    /// </summary>
    internal static string MaybeQuotePath(string path)
    {
        if (path.Contains(' '))
        {
            return $"\"{path}\"";
        }
        return path;
    }

    /// <summary>
    /// Removes surrounding quotes from a string if present.
    /// </summary>
    internal static string RemoveQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }
        return value;
    }
}
