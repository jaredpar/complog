
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CodeAnalysisExtensionsTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CodeAnalysisExtensionsTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilationDataTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void EmitToMemory()
    {
        var data = GetCompilationData(Fixture.ClassLib.Value.CompilerLogPath);
        var compilation = data.GetCompilationAfterGenerators();
        var result = compilation.EmitToMemory(EmitFlags.Default);
        AssertEx.Success(TestOutputHelper, result);
        AssertEx.HasData(result.AssemblyStream);
        Assert.Null(result.PdbStream);
        Assert.Null(result.XmlStream);
        Assert.Null(result.MetadataStream);

        result = compilation.EmitToMemory(EmitFlags.IncludePdbStream);
        AssertEx.Success(TestOutputHelper, result);
        AssertEx.HasData(result.AssemblyStream);
        AssertEx.HasData(result.PdbStream);
        Assert.Null(result.XmlStream);
        Assert.Null(result.MetadataStream);
    }
}