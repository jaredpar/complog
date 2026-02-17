using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Abstraction for getting new file paths for original paths in the compilation that existed
/// outside the cone of the project. For paths like that it's important to keep the original
/// directory structure. There are many parts of compilation that are hierarchical like 
/// editorconfig that require this.
/// </summary>
internal sealed class MiscDirectory(string baseDirectory)
{
    private string BaseDirectory { get; } = baseDirectory;
    private Dictionary<string, string> Map { get; } = new(PathUtil.Comparer);

    public string GetNewFilePath(string path)
    {
        if (Map.TryGetValue(path, out var newPath))
        {
            return newPath;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return BaseDirectory;
        }

        var newParent = GetNewFilePath(parent);
        newPath = Path.Combine(newParent, Path.GetFileName(path));
        Map.Add(path, newPath);
        return newPath;
    }
}