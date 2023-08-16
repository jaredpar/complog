using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Abstraction for getting new file paths for original paths in the compilation.
/// </summary>
internal sealed class ResilientDirectory
{
    /// <summary>
    /// Content can exist outside the cone of the original project tree. That content 
    /// is mapped, by original directory name, to a new directory in the output. This
    /// holds the map from the old directory to the new one.
    /// </summary>
    private Dictionary<string, string> _map = new(PathUtil.Comparer);

    /// <summary>
    /// When doing flattening this holds the map of file name that was flattened to the 
    /// path that it was flattened from.
    /// </summary>
    private Dictionary<string, string>? _flattenedMap;

    internal string DirectoryPath { get; }

    /// <summary>
    /// When true will attempt to flatten the directory structure by writing files
    /// directly to the directory when possible.
    /// </summary>
    internal bool Flatten => _flattenedMap is not null;

    internal ResilientDirectory(string path, bool flatten = false)
    {
        DirectoryPath = path;
        Directory.CreateDirectory(DirectoryPath);
        if (flatten)
        {
            _flattenedMap = new(PathUtil.Comparer);
        }
    }

    internal string GetNewFilePath(string originalFilePath)
    {
        var fileName = Path.GetFileName(originalFilePath);
        if (_flattenedMap is not null)
        {
            if (!_flattenedMap.TryGetValue(fileName, out var sourcePath) ||
                PathUtil.Comparer.Equals(sourcePath, originalFilePath))
            {
                _flattenedMap[fileName] = originalFilePath;
                return Path.Combine(DirectoryPath, fileName);
            }
        }

        var key = Path.GetDirectoryName(originalFilePath)!;
        if (!_map.TryGetValue(key, out var dirPath))
        {
            dirPath = Path.Combine(DirectoryPath, $"group{_map.Count}");
            Directory.CreateDirectory(dirPath);
            _map[key] = dirPath;
        }

        return Path.Combine(dirPath, fileName);
    }

    internal string WriteContent(string originalFilePath, Stream stream)
    {
        var newFilePath = GetNewFilePath(originalFilePath);
        using var fileStream = new FileStream(newFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.CopyTo(fileStream);
        return newFilePath;
    }
}

