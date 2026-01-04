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
        DirectoryPath = TestUtil.CreateUniqueSubDirectory(Path.Combine(TestUtil.TestTempRoot, "temps"));
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

    public string NewFile(string fileName, Stream content)
    {
        var filePath = Path.Combine(DirectoryPath, fileName);
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        content.CopyTo(fileStream);
        return filePath;
    }

    public string NewDirectory(string? name = null)
    {
        name ??= Guid.NewGuid().ToString();
        var path = Path.Combine(DirectoryPath, name);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"Directory already exists: {path}");
        }

        _ = Directory.CreateDirectory(path);
        return path;
    }

    public string CopyFile(string filePath, bool overwrite = false)
    {
        var newFilePath = Path.Combine(DirectoryPath, Path.GetFileName(filePath));
        var fileInfo = new FileInfo(filePath);
        fileInfo.CopyTo(newFilePath, overwrite, clearReadOnly: true);
        return newFilePath;
    }

    public string CopyDirectory(string dir)
    {
        var newDir = NewDirectory(Path.GetFileName(dir));

        var info = new DirectoryInfo(dir);
        foreach (var item in info.GetFiles())
        {
            var tempPath = Path.Combine(newDir, item.Name);
            _ = item.CopyTo(tempPath, overwrite: true, clearReadOnly: true);
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
