#if NET

using System.Reflection;
using System.Runtime.Loader;
using Basic.CompilerLog.App;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CustomCompilerLoadContextTests : TestBase
{
    public CustomCompilerLoadContextTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : base(testOutputHelper, testContextAccessor, nameof(CustomCompilerLoadContextTests))
    {
    }

    [Fact]
    public void LoadOlderCompiler()
    {
        var context = new CustomCompilerLoadContext(
            [
                Path.GetDirectoryName(typeof(Compilation).Assembly.Location)!,
                Root.NewDirectory()
            ]);

        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new("Microsoft.CodeAnalysis, Version=25.0.0.0")));
    }

    [Fact]
    public void LoadCompilerNoVersion()
    {
        var context = new CustomCompilerLoadContext(
            [
                Path.GetDirectoryName(typeof(Compilation).Assembly.Location)!,
                Root.NewDirectory()
            ]);

        var assembly = context.LoadFromAssemblyName(new("Microsoft.CodeAnalysis"));
        Assert.NotNull(assembly);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
    }
}

#endif
