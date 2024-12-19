
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
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
        Assert.Null(result.PdbStream);
        Assert.Null(result.XmlStream);
        Assert.Null(result.MetadataStream);

        result = compilation.EmitToMemory(EmitFlags.IncludePdbStream, cancellationToken: CancellationToken);
        AssertEx.Success(TestOutputHelper, result);
        AssertEx.HasData(result.AssemblyStream);
        AssertEx.HasData(result.PdbStream);
        Assert.Null(result.XmlStream);
        Assert.Null(result.MetadataStream);
    }
}