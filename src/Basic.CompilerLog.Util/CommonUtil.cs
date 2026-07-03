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
    /// Gets the parent directory used for compiler log temp working directories.
    /// </summary>
    internal static string GetCompilerLogTempDirectory() =>
        Path.Combine(Path.GetTempPath(), "Basic.CompilerLog");

    /// <summary>
    /// Gets the locks directory inside the compiler log temp directory.
    /// </summary>
    internal static string GetLocksDirectory() =>
        Path.Combine(GetCompilerLogTempDirectory(), "locks");

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
    /// a running process and deletes them. Ownership is determined by a lock file in the sibling
    /// <c>locks/</c> directory held open with <see cref="FileShare.None"/> by the owning
    /// <see cref="LogReaderState"/>.
    /// </summary>
    internal static void CleanupStaleTempDirectories(string parentDirectory)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return;
        }

        var locksDirectory = Path.Combine(parentDirectory, "locks");

        foreach (var dir in Directory.EnumerateDirectories(parentDirectory))
        {
            // Skip the locks directory itself
            if (Path.GetFileName(dir) == "locks")
            {
                continue;
            }

            try
            {
                var dirName = Path.GetFileName(dir);
                var lockFilePath = Path.Combine(locksDirectory, dirName + ".lock");

                if (File.Exists(lockFilePath))
                {
                    // Attempt to open the lock file exclusively. If it succeeds the owning process
                    // is no longer running and the directory is stale.
                    using var fs = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    fs.Dispose();

                    // Lock acquired — owning process is gone. Delete the lock file and directory.
                    File.Delete(lockFilePath);
                }
                else
                {
                    // No lock file means either old format or orphaned. Check for a legacy
                    // in-directory .lock file for backward compat.
                    var legacyLockPath = Path.Combine(dir, ".lock");
                    if (File.Exists(legacyLockPath))
                    {
                        using var fs = new FileStream(legacyLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        fs.Dispose();
                    }
                }

                // Either no lock file, acquired the external lock, or acquired legacy lock.
                // Delete the directory.
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Lock is held by another process, or we can't delete for another reason. Skip.
            }
        }

        // Try to clean up the locks dir if empty, then the parent
        DeleteDirectoryIfEmpty(locksDirectory);
        DeleteDirectoryIfEmpty(parentDirectory);
    }
}
