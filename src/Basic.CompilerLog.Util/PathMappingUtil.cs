using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

internal enum PathMapKind
{
    ProjectFile,
    CompilerExecutableFile,
}

/// <summary>
/// Maps host-format paths to the locations where they should be exposed or materialized.
/// </summary>
/// <remarks>
/// These input paths must be normalized to the current OS path format.
/// </remarks>
internal abstract class PathMappingUtil
{
    /// <summary>
    /// Whether contextual paths and compiler option paths are returned unchanged.
    /// </summary>
    internal abstract bool IsEmpty { get; }

    [return: NotNullIfNotNull("path")]
    internal virtual string? MapPath(string? path, PathMapKind kind) => path;

    [return: NotNullIfNotNull("path")]
    internal virtual string? MapPath(string? path, RawContentKind kind) => path;

    /// <summary>
    /// Maps a path associated with a compiler option. An empty option name represents a source
    /// file specified directly on the command line.
    /// </summary>
    internal virtual string MapPath(string path, ReadOnlySpan<char> optionName) => path;

    internal static PathMappingUtil CreateDefault(LogReaderState state) =>
        new DefaultPathMappingUtil(state);
}

file sealed class DefaultPathMappingUtil(LogReaderState state) : PathMappingUtil
{
    private readonly Dictionary<string, string> _cryptoKeyPathMap = new(PathUtil.Comparer);
    private readonly HashSet<string> _cryptoKeyFilePathSet = new(PathUtil.Comparer);

    internal override bool IsEmpty => false;

    [return: NotNullIfNotNull("path")]
    internal override string? MapPath(string? path, RawContentKind kind)
    {
        return path is not null && kind == RawContentKind.CryptoKeyFile
            ? GetOrCreateCryptoKeyFilePath(path)
            : path;
    }

    internal override string MapPath(string path, ReadOnlySpan<char> optionName)
    {
        return optionName is "keyfile"
            ? GetOrCreateCryptoKeyFilePath(path)
            : path;
    }

    private string GetOrCreateCryptoKeyFilePath(string path)
    {
        if (_cryptoKeyPathMap.TryGetValue(path, out var mappedPath))
        {
            return mappedPath;
        }

        var fileName = Path.GetFileName(path);
        mappedPath = Path.Combine(state.CryptoKeyFileDirectory, fileName);
        if (!_cryptoKeyFilePathSet.Add(mappedPath))
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var suffix = 1;
            do
            {
                mappedPath = Path.Combine(
                    state.CryptoKeyFileDirectory,
                    $"{name}.{suffix++}{extension}");
            }
            while (!_cryptoKeyFilePathSet.Add(mappedPath));
        }

        _cryptoKeyPathMap.Add(path, mappedPath);
        return mappedPath;
    }
}
