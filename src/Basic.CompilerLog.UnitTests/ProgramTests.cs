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

public sealed class ProgramTests : TestBase
{
    public ProgramTests(ITestOutputHelper testOutputHelper) 
        : base(testOutputHelper, nameof(ProgramTests))
    {

    }

    public int RunCompLog(params string[] args) => RunCompLog(args, RootDirectory);

    public int RunCompLog(string[] args, string currentDirectory)
    {
        Constants.CurrentDirectory = currentDirectory;
        var assembly = typeof(FilterOptionSet).Assembly;
        var program = assembly.GetType("Program", throwOnError: true);
        var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(main);
        var ret = main!.Invoke(null, new[] { args });
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
        Assert.Equal(0, RunCompLog(("create " + extra).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToArray()));
        var complogPath = Path.Combine(RootDirectory, fileName);
        Assert.True(File.Exists(complogPath));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ExportHelloWorld(string template)
    {
        using var exportDir = new TempDir();

        RunDotNet($"new {template} --name example --output .");
        RunDotNet("build -bl");
        Assert.Equal(0, RunCompLog("export", "-o", exportDir.DirectoryPath, RootDirectory));

        // Now run the generated build.cmd and see if it succeeds;
        var exportPath = Path.Combine(exportDir.DirectoryPath, "example", "export");
        var buildResult = RunBuildCmd(exportPath);
        Assert.True(buildResult.Succeeded);
    }
}
