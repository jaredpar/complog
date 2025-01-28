using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

internal static class CommonUtil
{
    internal const string MetadataFileName = "metadata.txt";
    internal const string AssemblyInfoFileName = "assemblyinfo.txt";
    internal const string LogInfoFileName = "loginfo.txt";
    internal static readonly Encoding ContentEncoding = Encoding.UTF8;
    internal static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard.WithAllowAssemblyVersionMismatch(true);

    internal static string GetCompilerEntryName(int index) => $"compilations/{index}.txt";
    internal static string GetAssemblyEntryName(Guid mvid) => $"assembly/{mvid:N}";
    internal static string GetContentEntryName(string contentHash) => $"content/{contentHash}";

#if NET

    internal static AssemblyLoadContext GetAssemblyLoadContext(AssemblyLoadContext? context = null)
    {
        if (context is { })
        {
            return context;
        }

        // This code path is only valid in a runtime context so this will be non-null.
        return AssemblyLoadContext.GetLoadContext(typeof(CommonUtil).Assembly)!;
    }

#endif

    // write a function that can parse a command line string into a string array. It needs to 
    // consider quotes and escape characters. The function should be able to handle items 
    // like the following where "hello world" is a single argument without quotes
    // a b -c "hello world"
    internal static List<string> ParseCommandLine(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }

                continue;
            }

            currentArg.Append(c);
        }

        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args;
    }
}
