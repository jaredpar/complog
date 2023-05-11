using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ProgramTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ProgramTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture) 
        : base(testOutputHelper, nameof(ProgramTests))
    {
        Fixture = fixture;
    }

    public int RunCompLog(string args, string? currentDirectory = null)
    {
        currentDirectory ??= RootDirectory;
        Constants.CurrentDirectory = currentDirectory;
        var assembly = typeof(FilterOptionSet).Assembly;
        var program = assembly.GetType("Program", throwOnError: true);
        var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(main);
        var ret = main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
        return (int)ret!;
    }

    [Theory]
    [InlineData("", "msbuild.complog")]
    [InlineData("--out custom.complog", "custom.complog")]
    [InlineData("-o custom.complog", "custom.complog")]
    public void Create(string extra, string fileName)
    {
        RunDotNet("new console");
        RunDotNet("build -bl");
        Assert.Equal(0, RunCompLog($"create {extra}"));
        var complogPath = Path.Combine(RootDirectory, fileName);
        Assert.True(File.Exists(complogPath));
    }

    [Fact]
    public void CreateFullPath()
    {
        using var exportDir = new TempDir();

        RunDotNet($"new console --name example --output .");
        RunDotNet("build -bl");
        Assert.Equal(0, RunCompLog($"create {GetBinaryLogFullPath()}", RootDirectory));
    }

    [Fact]
    public void References()
    {
        Assert.Equal(0, RunCompLog($"ref -o {RootDirectory} {Fixture.ComplogDirectory}"));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "example", "refs"), "*.dll"));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(RootDirectory, "example", "analyzers"), "*.dll", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ExportHelloWorld(string template)
    {
        using var exportDir = new TempDir();

        RunDotNet($"new {template} --name example --output .");
        RunDotNet("build -bl");
        Assert.Equal(0, RunCompLog($"export -o {exportDir.DirectoryPath}", RootDirectory));

        // Now run the generated build.cmd and see if it succeeds;
        var exportPath = Path.Combine(exportDir.DirectoryPath, "example", "export");
        var buildResult = RunBuildCmd(exportPath);
        Assert.True(buildResult.Succeeded);
    }
}
