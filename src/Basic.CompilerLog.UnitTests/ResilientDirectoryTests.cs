using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class ResilientDirectoryTests
{
    public string RootPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @"c:\"
        : "/";

    [Fact]
    public void GetNewFilePathFlatten1()
    {
        using var tempDir = new TempDir();
        var dir = new ResilientDirectory(tempDir.DirectoryPath, flatten: true);
        var path1 = dir.GetNewFilePath(Path.Combine(RootPath, "temp1", "resource.txt"));
        var path2 = dir.GetNewFilePath(Path.Combine(RootPath, "temp2", "resource.txt"));
        Assert.NotEqual(path1, path2);
        Assert.Equal(Path.Combine(tempDir.DirectoryPath, "resource.txt"), path1);
        Assert.Equal("resource.txt", Path.GetFileName(path2));
    }

    [Fact]
    public void GetNewFilePathFlatten2()
    {
        using var tempDir = new TempDir();
        var dir = new ResilientDirectory(tempDir.DirectoryPath, flatten: true);
        var originalPath = Path.Combine(RootPath, "temp", "resource.txt");
        var path1 = dir.GetNewFilePath(originalPath);
        var path2 = dir.GetNewFilePath(originalPath);
        Assert.Equal(path1, path2);
        Assert.Equal(Path.Combine(tempDir.DirectoryPath, "resource.txt"), path1);
    }

    [Fact]
    public void GetNewFilePath1()
    {
        using var tempDir = new TempDir();
        var dir = new ResilientDirectory(tempDir.DirectoryPath, flatten: false);
        var path1 = dir.GetNewFilePath(Path.Combine(RootPath, "temp1", "resource.txt"));
        var path2 = dir.GetNewFilePath(Path.Combine(RootPath, "temp2", "resource.txt"));
        Assert.NotEqual(path1, path2);
        Assert.NotEqual(Path.Combine(tempDir.DirectoryPath, "resource.txt"), path1);
        Assert.NotEqual(Path.Combine(tempDir.DirectoryPath, "resource.txt"), path2);
        Assert.Equal("resource.txt", Path.GetFileName(path1));
        Assert.Equal("resource.txt", Path.GetFileName(path2));
    }

    [Fact]
    public void GetNewFilePath2()
    {
        using var tempDir = new TempDir();
        var dir = new ResilientDirectory(tempDir.DirectoryPath, flatten: false);
        var originalPath = Path.Combine(RootPath, "temp", "resource.txt");
        var path1 = dir.GetNewFilePath(originalPath);
        var path2 = dir.GetNewFilePath(originalPath);
        Assert.Equal(path1, path2);
        Assert.NotEqual(Path.Combine(tempDir.DirectoryPath, "resource.txt"), path1);
        Assert.Equal("resource.txt", Path.GetFileName(path1));
    }
}
