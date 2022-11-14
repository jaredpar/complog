using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class ProgramTests : IDisposable
{
    internal TempDir Root { get; }
    internal string RootDirectory { get; }

    public ProgramTests()
    {
        Root = new TempDir();
        RootDirectory = Root.DirectoryPath;
    }

    public int Run(params string[] args) => Run(args, RootDirectory);

    public int Run(string[] args, string currentDirectory)
    {
        Constants.CurrentDirectory = currentDirectory;
        var assembly = typeof(FilterOptionSet).Assembly;
        var program = assembly.GetType("Program", throwOnError: true);
        var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(main);
        var ret = main!.Invoke(null, new[] { args });
        return (int)ret!;
    }

    [Fact]
    public void CreateNoArgs()
    {
        Assert.True(DotnetUtil.New("console", RootDirectory).Succeeded);
        Assert.True(DotnetUtil.Build("-bl", RootDirectory).Succeeded);
        Assert.Equal(0, Run("create"));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ExportHelloWorld(string template)
    {
        using var consoleDir = new TempDir("program");
        using var exportDir = new TempDir();

        Assert.True(DotnetUtil.New(template, consoleDir.DirectoryPath).Succeeded);
        Assert.True(DotnetUtil.Build("-bl", consoleDir.DirectoryPath).Succeeded);
        Assert.Equal(0, Run("export", "-o", exportDir.DirectoryPath, consoleDir.DirectoryPath));

        // Now run the generated build.cmd and see if it succeeds;
        var exportPath = Path.Combine(exportDir.DirectoryPath, "program");
        var buildResult = ProcessUtil.RunBatchFile(
            Path.Combine(exportPath, "build.cmd"),
            args: "",
            workingDirectory: exportPath);
        Assert.True(buildResult.Succeeded);
    }

    public void Dispose()
    {
        Root.Dispose();
    }
}
