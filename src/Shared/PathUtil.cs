using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal static class PathUtil
{
    internal static readonly StringComparer Comparer = StringComparer.Ordinal;
    internal static readonly StringComparison Comparison = StringComparison.Ordinal;

    /// <summary>
    /// Replace the <paramref name="oldStart"/> with <paramref name="newStart"/> inside of
    /// <paramref name="filePath"/>
    /// </summary>
    internal static string ReplacePathStart(string filePath, string oldStart, string newStart)
    {
        var str = RemovePathStart(filePath, oldStart);
        return Path.Combine(newStart, str);
    }

    internal static string RemovePathStart(string filePath, string start)
    {
        var str = filePath.Substring(start.Length);
        if (str.Length > 0 && str[0] == Path.DirectorySeparatorChar)
        {
            str = str.Substring(1);
        }

        return str;
    }
}
