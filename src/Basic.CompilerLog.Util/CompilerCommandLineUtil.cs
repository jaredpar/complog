using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Options;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Utility methods for parsing and manipulating compiler command line arguments.
/// </summary>
internal static partial class CompilerCommandLineUtil
{
    internal readonly ref struct OptionParts(
        char prefix,
        bool hasColon,
        ReadOnlySpan<char> namme,
        ReadOnlySpan<char> value)
    {
        internal char Prefix { get; } = prefix;
        internal bool HasColon { get; }= hasColon;
        internal ReadOnlySpan<char> Name { get; }= namme;
        internal ReadOnlySpan<char> Value { get; }= value;
    }

    /* lang=regex */
    private const string OptionRegexContent = @"^[/-][a-z0-9]+(:|[+-]?$)";

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
    /// Tries to parse an option argument into its name and value parts. This will always return
    /// a lower case option name.
    /// </summary>
    internal static bool TryParseOption(ReadOnlySpan<char> arg, out OptionParts optionParts)
    {
        if (!IsOption(arg))
        {
            optionParts = default;
            return false;
        }

        var prefix = arg[0];
        arg = arg[1..];
        var colonIndex = arg.IndexOf(':');
        if (colonIndex > 0)
        {
            var optionName = ToLowerInvariant(arg[..colonIndex]);
            var optionValue = arg[(colonIndex + 1)..];
            optionParts = new OptionParts(prefix, hasColon: true, optionName, optionValue);
        }
        else if (arg[^1] is '+' or '-')
        {
            var optionName = arg[..^1];
            var optionValue = arg[^1..];
            optionParts = new OptionParts(prefix, hasColon: false, optionName, optionValue);
        }
        else
        {
            optionParts = new OptionParts(prefix, hasColon: false, arg, "");
        }

        return true;

        static ReadOnlySpan<char> ToLowerInvariant(ReadOnlySpan<char> value)
        {
            var anyUpper = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsUpper(value[i]))
                {
                    anyUpper = true;
                    break;
                }
            }

            if (!anyUpper)
            {
                return value;
            }

            var array = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                array[i] = char.ToLowerInvariant(value[i]);
            }
            return array;
        }
    }

    /// <summary>
    /// The error log option can have an optional suffix on the path.
    /// </summary>
    internal static void ParseErrorLogArgument(OptionParts option, out ReadOnlySpan<char> path, out ReadOnlySpan<char> version)
    {
        var optionValue = option.Value;
        var commaIndex = optionValue.IndexOf(',');
        if (commaIndex < 0)
        {
            path = optionValue;
            version = "";
        }
        else
        {
            path = optionValue[..commaIndex];
            version = optionValue[(commaIndex + 1)..];
        }
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

    internal static bool IsQuoted(ReadOnlySpan<char> value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"';

    internal static ReadOnlySpan<char> RemoveQuotes(ReadOnlySpan<char> value)
    {
        Debug.Assert(IsQuoted(value));
        return value[1..^1];
    }

    internal static ReadOnlySpan<char> MaybeRemoveQuotes(ReadOnlySpan<char> value) =>
        IsQuoted(value) ? RemoveQuotes(value) : value;

    internal static string MaybeRemoveQuotes(string value) =>
        IsQuoted(value) ? RemoveQuotes(value).ToString() : value;

    /// <summary>
    /// This normalizes a single compiler commmand line argument according to the
    /// provided <paramref name="pathNormalizationUtil"/>
    /// </summary>
    internal static string NormalizeArgument(string argument, PathNormalizationUtil pathNormalizationUtil)
    {
        // If this not an option then it's a source file path that needs to be normalized
        if (!IsOption(argument))
        {
            return NormalizePath(argument, pathNormalizationUtil);
        }

        // If it's not a /option:value format then return as is as we don't need to normalize
        if (!TryParseOption(argument, out var option) || !IsPathOption(option.Name))
        {
            return argument;
        }

        return NormalizePathOption(option, pathNormalizationUtil);
    }

    internal static string NormalizePathOption(OptionParts option, PathNormalizationUtil pathNormalizationUtil)
    {
        Debug.Assert(!IsPathOption(option.Name));
        Debug.Assert(option.HasColon);

        return option.Name switch
        {
            "errorlog" => NormalizeErrorLogOption(option, pathNormalizationUtil),
            "reference" => NormalizeReference(option, pathNormalizationUtil),
            _ => NormalizeOptionWithPath(option, pathNormalizationUtil)
        };

        static string NormalizeErrorLogOption(OptionParts option, PathNormalizationUtil pathNormalizationUtil)
        {
            ParseErrorLogArgument(option, out var path, out var version);
            if (version.Length == 0)
            {
                return NormalizeOptionWithPath(option, pathNormalizationUtil);
            }

            return $"{option.Prefix}{option.Name.ToString()}:{NormalizePath(path.ToString(), pathNormalizationUtil)},{version.ToString()}";
        }

        static string NormalizeReference(OptionParts option, PathNormalizationUtil pathNormalizationUtil)
        {
            // Handle alias syntax: /reference:alias=path
            var value = option.Value;
            var equalsIndex = value.IndexOf('=');
            if (equalsIndex >= 0)
            {
                // TODO: when the path has a space where do the quotes go?
                var alias = value[..(equalsIndex + 1)];
                var path = value[(equalsIndex + 1)..];
                return $"{option.Prefix}{option.Name.ToString()}:{alias.ToString()}={NormalizePath(path.ToString(), pathNormalizationUtil)}";
            }

            return NormalizeOptionWithPath(option, pathNormalizationUtil);
        }

        static string NormalizeOptionWithPath(OptionParts option, PathNormalizationUtil pathNormalizationUtil)
        {
            return $"{option.Prefix}{option.Name.ToString()}:{NormalizePath(option.Value.ToString(), pathNormalizationUtil)}";
        }
    }

    private static string NormalizePath(string path, PathNormalizationUtil pathNormalizationUtil)
    {
        path = CompilerCommandLineUtil.MaybeRemoveQuotes(path);
        path = pathNormalizationUtil.NormalizePath(path);
        return CompilerCommandLineUtil.MaybeQuotePath(path);
    }
}
