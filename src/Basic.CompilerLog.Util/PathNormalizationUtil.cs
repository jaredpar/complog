
using System.Buffers;
using System.Configuration.Internal;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Basic.CompilerLog.Util;

/// <summary>
/// This is used to map paths from one format to another. This can be used to map between operating systems or
/// to change root paths within an operating system.
/// </summary>
/// <remarks>
/// The mapping methods in this type are idempotent.
/// </remarks>
internal abstract class PathNormalizationUtil
{
    internal const string WindowsRoot = @"c:\code\";
    internal const string UnixRoot = @"/code/";

    internal const int MaxPathLength = 520;
    internal static PathNormalizationUtil Empty { get; } = new EmptyNormalizationUtil();
    internal static PathNormalizationUtil WindowsToUnix { get; } = new WindowsToUnixNormalizationUtil(UnixRoot);
    internal static PathNormalizationUtil UnixToWindows { get; } = new UnixToWindowsNormalizationUtil(WindowsRoot);

    /// <summary>
    /// Is this the empty / no-op normalizer
    /// </summary>
    internal bool IsEmpty => this is EmptyNormalizationUtil;

    /// <summary>
    /// Is the path rooted in the "from" platform
    /// </summary>
    internal abstract bool IsPathRooted([NotNullWhen(true)] string? path);

    /// <summary>
    /// Normalize the path from the "from" platform to the "to" platform
    /// </summary>
    [return: NotNullIfNotNull("path")]
    internal abstract string? NormalizePath(string? path);

    /// <summary>
    /// Normalize the path from the "from" platform to the "to" platform
    /// </summary>
    [return: NotNullIfNotNull("path")]
    internal virtual string? NormalizePath(string? path, RawContentKind kind) => NormalizePath(path);

    /// <summary>
    /// Make the file name an absolute path by putting it under the root
    /// </summary>
    internal abstract string RootFileName(string fileName);
}

/// <summary>
/// This will normalize paths from Unix to Windows
/// </summary>
file sealed class WindowsToUnixNormalizationUtil(string root) : PathNormalizationUtil
{
    internal string Root { get; } = root;

    internal override bool IsPathRooted([NotNullWhen(true)] string? path) =>
        path != null &&
        path.Length >= 2 &&
        char.IsLetter(path[0]) &&
        ':' == path[1];

    [return: NotNullIfNotNull("path")]
    internal override string? NormalizePath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var array = ArrayPool<char>.Shared.Rent(MaxPathLength);
        int arrayIndex = 0;
        int pathIndex = 0;
        if (IsPathRooted(path))
        {
            Debug.Assert(Root[Root.Length-1]=='/');

            Root.AsSpan().CopyTo(array.AsSpan());
            arrayIndex += Root.Length;
            pathIndex += 2;

            // Move past any extra slashes after the c: portion of the path.
            while (pathIndex < path.Length && path[pathIndex] == '\\')
            {
                pathIndex++;
            }
        }

        while (pathIndex < path.Length)
        {
            if (path[pathIndex] == '\\')
            {
                array[arrayIndex++] = '/';
                pathIndex++;
                while (pathIndex < path.Length && path[pathIndex] == '\\')
                {
                    pathIndex++;
                }
            }
            else
            {
                array[arrayIndex++] = path[pathIndex++];
            }
        }

        var normalizedPath = new string(array, 0, arrayIndex);
        ArrayPool<char>.Shared.Return(array);
        return normalizedPath;
    }

    internal override string RootFileName(string fileName)=> Root + fileName;
}

file sealed class UnixToWindowsNormalizationUtil(string root) : PathNormalizationUtil
{
    internal string Root { get; } = root;

    internal override bool IsPathRooted([NotNullWhen(true)] string? path) =>
        path != null &&
        path.Length > 0 &&
        path[0] == '/';

    [return: NotNullIfNotNull("path")]
    internal override string? NormalizePath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var array = ArrayPool<char>.Shared.Rent(MaxPathLength);
        int arrayIndex = 0;
        int pathIndex = 0;
        if (IsPathRooted(path))
        {
            Debug.Assert(Root[Root.Length-1]=='\\');
            Root.AsSpan().CopyTo(array.AsSpan());
            arrayIndex += Root.Length;
            pathIndex += 1;
        }

        while (pathIndex < path.Length)
        {
            if (path[pathIndex] == '/')
            {
                array[arrayIndex++] = '\\';
                pathIndex++;
            }
            else
            {
                array[arrayIndex++] = path[pathIndex++];
            }
        }

        var normalizedPath = new string(array, 0, arrayIndex);
        ArrayPool<char>.Shared.Return(array);
        return normalizedPath;
    }

    internal override string RootFileName(string fileName)=> Root + fileName;
}

/// <summary>
/// This is used when the current platform is the same as the platform that generated the log
/// hence no normalization is needed.
/// </summary>
file sealed class EmptyNormalizationUtil : PathNormalizationUtil
{
    internal string Root { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsRoot : UnixRoot;

    internal override bool IsPathRooted(string? path) => Path.IsPathRooted(path);
    internal override string? NormalizePath(string? path) => path;

    internal override string RootFileName(string fileName)=> Root + fileName;
}
