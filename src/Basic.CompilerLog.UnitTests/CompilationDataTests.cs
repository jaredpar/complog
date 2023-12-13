
using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilationDataTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilationDataTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilationDataTests))
    {
        Fixture = fixture;
    }

    private void AssertHasData(MemoryStream? stream)
    {
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void EmitToMemoryCombinations()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value);
        var data = reader.ReadCompilationData(0);

        var emitResult = data.EmitToMemory();
        Assert.True(emitResult.Success);
        AssertHasData(emitResult.AssemblyStream);
        AssertHasData(emitResult.PdbStream);
        Assert.Null(emitResult.XmlStream);
        AssertHasData(emitResult.MetadataStream);

        emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream);
        Assert.True(emitResult.Success);
        AssertHasData(emitResult.AssemblyStream);
        AssertHasData(emitResult.PdbStream);
        Assert.Null(emitResult.XmlStream);
        Assert.Null(emitResult.MetadataStream);

        emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream | EmitFlags.IncludeXmlStream);
        Assert.True(emitResult.Success);
        AssertHasData(emitResult.AssemblyStream);
        AssertHasData(emitResult.PdbStream);
        AssertHasData(emitResult.XmlStream);
        Assert.Null(emitResult.MetadataStream);

        emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream | EmitFlags.IncludeXmlStream | EmitFlags.IncludeMetadataStream);
        Assert.True(emitResult.Success);
        AssertHasData(emitResult.AssemblyStream);
        AssertHasData(emitResult.PdbStream);
        AssertHasData(emitResult.XmlStream);
        AssertHasData(emitResult.MetadataStream);

        emitResult = data.EmitToMemory(EmitFlags.MetadataOnly);
        Assert.True(emitResult.Success);
        AssertHasData(emitResult.AssemblyStream);
        Assert.Null(emitResult.PdbStream);
        Assert.Null(emitResult.XmlStream);
        Assert.Null(emitResult.MetadataStream);
    }
}
