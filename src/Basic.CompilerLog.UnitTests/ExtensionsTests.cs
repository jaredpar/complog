using System;
using System.Reflection;
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ExtensionsTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ExtensionsTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilationDataTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void CheckEmitFlags()
    {
        EmitFlags.Default.CheckEmitFlags();
        Assert.Throws<ArgumentException>(void () => (EmitFlags.IncludePdbStream | EmitFlags.MetadataOnly).CheckEmitFlags());
    }

    [Fact]
    public void AddRange()
    {
        var list = new List<int>();
        Span<int> span = new int[] { 42, 13 };
        list.AddRange(span);
        Assert.Equal([42, 13], list);
    }

#if NET

    [Fact]
    public void GetFailureString()
    {
        var ex = new Exception("Hello, world!", new Exception("Inner exception"));
        Assert.NotEmpty(ex.GetFailureString());
    }

#endif
}