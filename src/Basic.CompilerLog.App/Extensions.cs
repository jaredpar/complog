using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.App;
internal static class Extensions
{
    internal static string GetFailureString(this Exception ex)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ex.Message);
        builder.AppendLine(ex.StackTrace);

        while (ex.InnerException is { } inner)
        {
            builder.AppendLine();
            builder.AppendLine("Inner exception:");
            builder.AppendLine(inner.Message);
            builder.AppendLine(inner.StackTrace);
            ex = inner;
        }

        return builder.ToString();
    }
}
