using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

internal static class CommonUtil
{
    internal const string MetadataFileName = "metadata.txt";
    internal const string AssemblyInfoFileName = "assemblyinfo.txt";
    internal const string SourceInfoFileName = "source.txt";
    internal static readonly Encoding ContentEncoding = Encoding.UTF8;

    internal static string GetCompilerEntryName(int index) => $"compilations/{index}.txt";
    internal static string GetAssemblyEntryName(Guid mvid) => $"assembly/{mvid:N}";
    internal static string GetContentEntryName(string contentHash) => $"content/{contentHash}";

#if NETCOREAPP

    internal static AssemblyLoadContext GetAssemblyLoadContext(AssemblyLoadContext? context)
    {
        if (context is { })
        {
            return context;
        }

        if (AssemblyLoadContext.GetLoadContext(typeof(CommonUtil).Assembly) is { } current)
        {
            return current;
        }

        return AssemblyLoadContext.Default;
    }

#endif
}
