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
    /// a running process and deletes them. Ownership is tracked by a lock file in the sibling
    /// <c>locks/</c> directory that the owning <see cref="LogReaderState"/> holds open with
    /// <see cref="FileShare.None"/> for its lifetime.
    ///
    /// A directory is considered orphaned (and deleted) when either:
    /// <list type="bullet">
    /// <item>its lock file is missing — the owner disposed cleanly (deleting the file), or on Windows
    /// the kernel removed it via delete-on-close when the owner crashed; or</item>
    /// <item>its lock file exists but can be opened exclusively — the owner is gone but left the file
    /// behind (e.g. a hard crash on Unix, where the file is deleted by a managed step that does not
    /// run on abnormal termination). The OS releases the file's share lock when the owning process
    /// dies, so a successful exclusive open is a reliable liveness test on every platform.</item>
    /// </list>
    /// A lock file that cannot be opened exclusively is still held by a live owner and is left alone.
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

                if (File.Exists(lockFilePath) && !TryAcquireStaleLock(lockFilePath))
                {
                    // The lock file exists and is held by a live owner. Leave the directory alone.
                    continue;
                }

                // The lock file is missing, or we acquired it exclusively (owner is gone). The
                // directory is orphaned. Delete the working directory first, then the lock file so
                // that an interruption still leaves the "missing lock file" orphan signal intact.
                Directory.Delete(dir, recursive: true);
                if (File.Exists(lockFilePath))
                {
                    File.Delete(lockFilePath);
                }
            }
            catch
            {
                // Best effort. The directory may be mid-creation by another instance, or otherwise
                // in use; skip it and let a future cleanup pass handle it.
            }
        }

        // Deliberately do NOT delete the shared locks directory (or its parent). Both are
        // long-lived roots shared by every concurrent LogReaderState in this process tree.
        // Deleting them here races with another instance that is creating its lock file inside
        // locks/, producing UnauthorizedAccessException/DirectoryNotFoundException (issue #370).
        // Leaving these empty directories behind is harmless; only the per-owner working
        // directories and lock files are reclaimed above.
    }

    /// <summary>
    /// Attempts to open <paramref name="lockFilePath"/> exclusively. Success means no live owner
    /// holds the lock, so the associated directory is stale. The owning <see cref="LogReaderState"/>
    /// holds the file open with <see cref="FileShare.None"/>, and the OS releases that share lock
    /// when the owner terminates — including on a crash (on Windows the kernel enforces the lock; on
    /// Unix .NET uses an advisory <c>flock</c> that the kernel drops on process death). A successful
    /// exclusive open is therefore a reliable liveness probe. Returns <see langword="false"/> if the
    /// lock is still held by a live owner.
    ///
    /// Note: the owner deliberately does not combine <see cref="FileOptions.DeleteOnClose"/> with
    /// <see cref="FileShare.None"/> on Unix, because that disables share enforcement there
    /// (dotnet/runtime#59995) and would make this probe unreliable.
    /// </summary>
    private static bool TryAcquireStaleLock(string lockFilePath)
    {
        try
        {
            using var fs = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Held by a live owner.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Held by a live owner (some platforms surface a sharing conflict this way).
            return false;
        }
    }
}
