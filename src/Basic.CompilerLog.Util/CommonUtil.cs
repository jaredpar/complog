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

    /// <summary>
    /// This is a _best effort_ attempt to delete a directory if it is empty. It returns true
    /// in the case that at some point in the execution if this method the directory did 
    /// not exist, false otherwise.
    /// </summary>
    internal static bool DeleteDirectoryIfEmpty(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return false;
            }

            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
