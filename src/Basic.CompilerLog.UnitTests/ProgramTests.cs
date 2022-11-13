using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class ProgramTests : IDisposable
{
    internal string RootDirectory { get; }

    public ProgramTests()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString());
        Directory.CreateDirectory(RootDirectory);
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
        Assert.Equal(0, DotnetUtil.New("console", RootDirectory));
        Assert.Equal(0, DotnetUtil.Build("-bl", RootDirectory));
        Assert.Equal(0, Run("create"));
    }

    public void Dispose()
    {
        Directory.Delete(RootDirectory, recursive: true);
    }
}
