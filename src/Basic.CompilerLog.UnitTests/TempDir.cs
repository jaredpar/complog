using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.UnitTests;

internal sealed class TempDir : IDisposable
{
    internal string DirectoryPath { get; }

    public TempDir(string? name = null)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString());
        if (name != null)
        {
            DirectoryPath = Path.Combine(DirectoryPath, name);
        }

        Directory.CreateDirectory(DirectoryPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    public string NewFile(string fileName, string content)
    {
        var filePath = Path.Combine(DirectoryPath, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    public string NewDirectory(string? name = null)
    {
        name ??= Guid.NewGuid().ToString();
        var path = Path.Combine(DirectoryPath, name);
        _ = Directory.CreateDirectory(path);
        return path;
    }

    public string CopyDirectory(string dir)
    {
        var newDir = NewDirectory();

        var info = new DirectoryInfo(dir);
        foreach (var item in info.GetFiles())
        {
            var tempPath = Path.Combine(newDir, item.Name);
            item.CopyTo(tempPath, overwrite: true);
        }

        return newDir;
    }

    public void EmptyDirectory()
    {
        var d = new DirectoryInfo(DirectoryPath);
        foreach(System.IO.FileInfo file in d.GetFiles()) file.Delete();
        foreach(System.IO.DirectoryInfo subDirectory in d.GetDirectories()) subDirectory.Delete(true);
    }
}
