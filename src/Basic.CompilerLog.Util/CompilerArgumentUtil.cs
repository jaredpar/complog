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
    internal static bool IsPathOption(ReadOnlySpan<char> optionName)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        return
            optionName.Equals("reference".AsSpan(), comparison) ||
            optionName.Equals("r".AsSpan(), comparison) ||
            optionName.Equals("analyzer".AsSpan(), comparison) ||
            optionName.Equals("a".AsSpan(), comparison) ||
            optionName.Equals("additionalfile".AsSpan(), comparison) ||
            optionName.Equals("analyzerconfig".AsSpan(), comparison) ||
            optionName.Equals("embed".AsSpan(), comparison) ||
            optionName.Equals("resource".AsSpan(), comparison) ||
            optionName.Equals("res".AsSpan(), comparison) ||
            optionName.Equals("linkresource".AsSpan(), comparison) ||
            optionName.Equals("linkres".AsSpan(), comparison) ||
            optionName.Equals("sourcelink".AsSpan(), comparison) ||
            optionName.Equals("ruleset".AsSpan(), comparison) ||
            optionName.Equals("keyfile".AsSpan(), comparison) ||
            optionName.Equals("link".AsSpan(), comparison) ||
            optionName.Equals("l".AsSpan(), comparison) ||
            optionName.Equals("out".AsSpan(), comparison) ||
            optionName.Equals("refout".AsSpan(), comparison) ||
            optionName.Equals("doc".AsSpan(), comparison) ||
            optionName.Equals("generatedfilesout".AsSpan(), comparison) ||
            optionName.Equals("pdb".AsSpan(), comparison) ||
            optionName.Equals("errorlog".AsSpan(), comparison) ||
            optionName.Equals("win32manifest".AsSpan(), comparison) ||
            optionName.Equals("win32res".AsSpan(), comparison) ||
            optionName.Equals("win32icon".AsSpan(), comparison) ||
            optionName.Equals("addmodule".AsSpan(), comparison) ||
            optionName.Equals("appconfig".AsSpan(), comparison) ||
            optionName.Equals("lib".AsSpan(), comparison);
    }

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
