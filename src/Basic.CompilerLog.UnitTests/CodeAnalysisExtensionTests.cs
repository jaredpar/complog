
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CodeAnalysisExtensionsTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CodeAnalysisExtensionsTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilationDataTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void EmitToMemory()
    {
        var data = GetCompilationData(Fixture.ClassLib.Value.CompilerLogPath, basicAnalyzerKind: BasicAnalyzerKind.None);
        var compilation = data.GetCompilationAfterGenerators(CancellationToken);
        var result = compilation.EmitToMemory(EmitFlags.Default, cancellationToken: CancellationToken);
        AssertEx.Success(TestOutputHelper, result);
        AssertEx.HasData(result.AssemblyStream);
        Assert.Equal(0, result.AssemblyStream.Position);
        Assert.Null(result.PdbStream);
        Assert.Null(result.XmlStream);
        Assert.Null(result.MetadataStream);

        result = compilation.EmitToMemory(
            EmitFlags.IncludePdbStream | EmitFlags.IncludeXmlStream | EmitFlags.IncludeMetadataStream,
            cancellationToken: CancellationToken);
        AssertEx.Success(TestOutputHelper, result);
        AssertEx.HasData(result.AssemblyStream);
        AssertEx.HasData(result.PdbStream);
        AssertEx.HasData(result.XmlStream);
        AssertEx.HasData(result.MetadataStream);
        Assert.Equal(0, result.AssemblyStream.Position);
        Assert.Equal(0, result.PdbStream.Position);
        Assert.Equal(0, result.XmlStream.Position);
        Assert.Equal(0, result.MetadataStream.Position);
    }
}