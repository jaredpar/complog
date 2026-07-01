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

    /// <summary>
    /// Gets the parent directory used for compiler log temp working directories. When
    /// <paramref name="basePath"/> is <c>null</c>, uses <see cref="Path.GetTempPath"/>.
    /// </summary>
    internal static string GetCompilerLogTempDirectory(string? basePath = null) =>
        Path.Combine(basePath ?? Path.GetTempPath(), "Basic.CompilerLog");

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

    /// <summary>
    /// Enumerates <paramref name="parentDirectory"/> for subdirectories that are no longer owned by
    /// a running process and deletes them. Ownership is determined by a <c>.lock</c> file held open
    /// with <see cref="FileShare.None"/> by the owning <see cref="LogReaderState"/>.
    /// </summary>
    internal static void CleanupStaleTempDirectories(string parentDirectory)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(parentDirectory))
        {
            try
            {
                var lockFilePath = Path.Combine(dir, ".lock");
                if (File.Exists(lockFilePath))
                {
                    // Attempt to open the lock file exclusively. If it succeeds the owning process
                    // is no longer running and the directory is stale.
                    using var fs = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    fs.Dispose();
                }

                // Either no lock file (old format) or we successfully acquired the lock (stale).
                // Delete the directory.
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Lock is held by another process, or we can't delete for another reason. Skip.
            }
        }

        // Try to clean up the parent if it's now empty
        DeleteDirectoryIfEmpty(parentDirectory);
    }
}
