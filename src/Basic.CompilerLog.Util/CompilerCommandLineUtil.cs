using Microsoft.CodeAnalysis.Diagnostics;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.XPath;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Utility methods for parsing and manipulating compiler command line arguments.
/// </summary>
internal static partial class CompilerCommandLineUtil
{
    internal delegate string NormalizePathFunc(string path, ReadOnlySpan<char> optionName);

    internal enum OptionSuffix
    {
        None,
        Plus,
        Minus,
        Colon,
        PlusColon,
        MinusColon
    }

    internal readonly ref struct OptionParts(
        char prefix,
        ReadOnlySpan<char> name,
        OptionSuffix suffix,
        ReadOnlySpan<char> value)
    {
        internal char Prefix { get; } = prefix;
        internal ReadOnlySpan<char> Name { get; }= name;
        internal OptionSuffix Suffix { get; } = suffix;
        internal ReadOnlySpan<char> Value { get; }= value;
        internal bool HasColon => HasColon(Suffix);
    }

    internal static bool HasColon(OptionSuffix suffix) => suffix is OptionSuffix.Colon or OptionSuffix.PlusColon or OptionSuffix.MinusColon;

    /// <summary>
    /// Checks if an argument is a compiler option (starts with / and has valid option format)
    /// </summary>
    internal static bool IsOption(ReadOnlySpan<char> arg) => TryParseOption(arg, out _);

    internal static bool IsPathOption(ReadOnlySpan<char> arg) => TryParseOption(arg, out var option) && IsPathOption(option);

    /// <summary>
    /// Checks if the given option name takes a path argument.
    /// </summary>
    internal static bool IsPathOption(OptionParts option) =>
        option.Name switch
        {
            "reference" or "r" or
            "analyzer" or "a" or
            "additionalfile" or
            "analyzerconfig" or
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
            "embed" when option.Suffix == OptionSuffix.Colon => true,
            _ => false
        };

    /// <summary>
    /// Tries to parse an option argument into its name and value parts. This will always return
    /// a lower case option name.
    /// </summary>
    internal static bool TryParseOption(ReadOnlySpan<char> arg, out OptionParts optionParts)
    {
        arg = arg.TrimEnd();

        if (arg.Length < 3 || arg[0] is not ('/' or '-'))
        {
            optionParts = default;
            return false;
        }

        var prefix = arg[0];
        arg = arg[1..];
        if (!TryGetName(ref arg, out var name))
        {
            optionParts = default;
            return false;
        }

        if (arg.Length == 0)
        {
            optionParts = new(prefix, name, OptionSuffix.None, "");
            return true;
        }

        OptionSuffix suffix;
        if (arg[0] is '+' or '-')
        {
            if (arg.Length > 1 && arg[1] == ':')
            {
                suffix = arg[0] == '+' ? OptionSuffix.PlusColon : OptionSuffix.MinusColon;
                arg = arg[2..];
            }
            else
            {
                suffix = arg[0] == '+' ? OptionSuffix.Plus : OptionSuffix.Minus;
                arg = arg[1..];
            }
        }
        else if (arg[0] == ':')
        {
            suffix = OptionSuffix.Colon;
            arg = arg[1..];
        }
        else
        {
            suffix = OptionSuffix.None;
        }

        if (!HasColon(suffix) && arg.Length > 0)
        {
            optionParts = default;
            return false;
        }

        optionParts = new(prefix, name, suffix, arg);
        return true;

        static bool TryGetName(scoped ref ReadOnlySpan<char> buffer, out ReadOnlySpan<char> name)
        {
            var anyUpper = false;
            var index = 0;
            while (index < buffer.Length && char.IsLetterOrDigit(buffer[index]))
            {
                if (char.IsLetter(buffer[index]) && char.IsUpper(buffer[index]))
                {
                    anyUpper = true;
                }
                index++;
            }

            if (index == 0)
            {
                name = default;
                return false;
            }

            if (anyUpper)
            {
                var array = new char[index];
                for (int i = 0; i < index; i++)
                {
                    array[i] = char.ToLowerInvariant(buffer[i]);
                }
                name = array;
            }
            else
            {
                name = buffer[..index];
            }

            buffer = buffer[index..];
            return true;
        }
    }

    /// <summary>
    /// The error log option can have an optional suffix on the path.
    /// Handles both quoted and unquoted paths. When quoted, the format is:
    /// /errorlog:"path with spaces,version=2"
    /// </summary>
    internal static void ParseErrorLogArgument(OptionParts option, out ReadOnlySpan<char> path, out ReadOnlySpan<char> version)
    {
        var optionValue = option.Value;

        // If the entire value is quoted, strip the quotes first then look for comma
        if (IsQuoted(optionValue))
        {
            optionValue = RemoveQuotes(optionValue);
        }

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
        if (path.IndexOfAny([' ', '=', ',']) >= 0)
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
    /// provided <paramref name="normalizePathFunc"/>
    /// </summary>
    internal static string NormalizeArgument(string argument, NormalizePathFunc normalizePathFunc)
    {
        // If this not an option then it's a source file path that needs to be normalized
        if (!IsOption(argument))
        {
            return NormalizePath(argument, "", normalizePathFunc);
        }

        // If it's not a /option:value format then return as is as we don't need to normalize
        if (!TryParseOption(argument, out var option) || !IsPathOption(option))
        {
            return argument;
        }

        return NormalizePathOption(option, normalizePathFunc);
    }

    internal static string NormalizePathOption(OptionParts option, NormalizePathFunc normalizePathFunc)
    {
        Debug.Assert(IsPathOption(option));
        Debug.Assert(option.HasColon);

        return option.Name switch
        {
            "errorlog" => NormalizeErrorLogOption(option, normalizePathFunc),
            "reference" => NormalizeReference(option, normalizePathFunc),
            _ => NormalizeOptionWithPath(option, normalizePathFunc)
        };

        static string NormalizeErrorLogOption(OptionParts option, NormalizePathFunc normalizePathFunc)
        {
            ParseErrorLogArgument(option, out var path, out var version);
            var normalizedPath = normalizePathFunc(path.ToString(), option.Name);

            if (version.Length == 0)
            {
                return $"{option.Prefix}{option.Name.ToString()}:{MaybeQuotePath(normalizedPath)}";
            }

            // When there's a version suffix, quotes must wrap the entire value (path + version)
            var fullValue = $@"""{normalizedPath},{version.ToString()}""";
            return $"{option.Prefix}{option.Name.ToString()}:{fullValue}";
        }

        static string NormalizeReference(OptionParts option, NormalizePathFunc normalizePathFunc)
        {
            // Handle alias syntax: /reference:alias=path
            var value = option.Value;
            var equalsIndex = value.IndexOf('=');
            if (equalsIndex >= 0)
            {
                // TODO: when the path has a space where do the quotes go?
                var alias = value[..(equalsIndex + 1)];
                var path = value[(equalsIndex + 1)..];
                return $"{option.Prefix}{option.Name.ToString()}:{alias.ToString()}={NormalizePathList(path, option.Name, normalizePathFunc)}";
            }

            return $"{option.Prefix}{option.Name.ToString()}:{NormalizePathList(option.Value, option.Name, normalizePathFunc)}";
        }

        static string NormalizePathList(ReadOnlySpan<char> path, ReadOnlySpan<char> optionName, NormalizePathFunc normalizePathFunc)
        {
            var commaIndex = path.IndexOf(',');
            if (commaIndex < 0)
            {
                return NormalizePath(path.ToString(), optionName, normalizePathFunc);
            }

            var builder = new StringBuilder();
            do
            {
                builder.Append(NormalizePath(path[..commaIndex].ToString(), optionName, normalizePathFunc));
                builder.Append(',');
                path = path[(commaIndex + 1)..];
                commaIndex = path.IndexOf(",");
            } while (commaIndex >= 0);

            builder.Append(NormalizePath(path.ToString(), optionName, normalizePathFunc));
            return builder.ToString();
        }

        static string NormalizeOptionWithPath(OptionParts option, NormalizePathFunc normalizePathFunc)
        {
            return $"{option.Prefix}{option.Name.ToString()}:{NormalizePathList(option.Value.ToString(), option.Name, normalizePathFunc)}";
        }
    }

    private static string NormalizePath(string path, ReadOnlySpan<char> optionName, NormalizePathFunc normalizePathFunc)
    {
        path = MaybeRemoveQuotes(path);
        path = normalizePathFunc(path, optionName);
        return MaybeQuotePath(path);
    }
}
