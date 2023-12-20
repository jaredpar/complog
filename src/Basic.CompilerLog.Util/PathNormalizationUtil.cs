
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

internal abstract class PathNormalizationUtil
{
    internal const int MaxPathLength = 520;
    internal static PathNormalizationUtil Empty { get; } = new EmtpyNormalizationUtil();
    internal static PathNormalizationUtil WindowsToUnix { get; } = new WindowsToUnixNormalizationUtil(@"/code");
    internal static PathNormalizationUtil UnixToWindows { get; } = new UnixToWindowsNormalizationUtil(@"c:\code\");

    /// <summary>
    /// Is the path rooted in the "from" platform
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    internal abstract bool IsPathRooted([NotNullWhen(true)] string? path);

    /// <summary>
    /// Normalize the path from the "from" platform to the "to" platform
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    [return: NotNullIfNotNull("path")]
    internal abstract string? NormalizePath(string? path);
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
            Root.AsSpan().CopyTo(array.AsSpan());
            arrayIndex += Root.Length;
            pathIndex += 2;
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
}

/// <summary>
/// This is used when the current platform is the same as the platform that generated the log
/// hence no normalization is needed.
/// </summary>
file sealed class EmtpyNormalizationUtil : PathNormalizationUtil
{
    internal override bool IsPathRooted(string? path) => Path.IsPathRooted(path);
    internal override string? NormalizePath(string? path) => path;
}