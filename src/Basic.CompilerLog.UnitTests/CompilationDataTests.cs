
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
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

    [Fact]
    public void EmitToMemoryRefOnly()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibRefOnlyComplogPath.Value);
        var data = reader.ReadCompilationData(0);
        var result = data.EmitToMemory();
        Assert.True(result.Success);
    }

    [Fact]
    public void GetAnalyzersNormal()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value);
        var data = reader.ReadCompilationData(0);
        Assert.NotEmpty(data.GetAnalyzers());
    }

    [Fact]
    public void GetAnalyzersNoHosting()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value, BasicAnalyzerHostOptions.None);
        var data = reader.ReadCompilationData(0);
        Assert.Empty(data.GetAnalyzers());
    }

    [Fact]
    public void GetDiagnostics()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value, BasicAnalyzerHostOptions.Default);
        var data = reader.ReadCompilationData(0);
        Assert.NotEmpty(data.GetDiagnostics());
    }

    [Fact]
    public async Task GetAllDiagnostics()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibComplogPath.Value, BasicAnalyzerHostOptions.Default);
        var data = reader.ReadCompilationData(0);
        Assert.NotEmpty(await data.GetAllDiagnosticsAsync());
    }

    [Fact]
    public void GetCompilationAfterGeneratorsDiagnostics()
    {
        var options = new BasicAnalyzerHostOptions(BasicAnalyzerKind.InMemory, cacheable: true);
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplogPath.Value, options);
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var analyzers = rawData.Analyzers
            .Where(x => x.FileName != "Microsoft.CodeAnalysis.NetAnalyzers.dll")
            .ToList();
        var host = new BasicAnalyzerHostInMemory(reader, analyzers, options);
        var data = (CSharpCompilationData)reader.ReadCompilationData(0);
        data = new CSharpCompilationData(
            data.CompilerCall,
            data.Compilation,
            data.ParseOptions,
            data.EmitOptions,
            data.EmitData,
            data.AdditionalTexts,
            host,
            data.AnalyzerConfigOptionsProvider);
        _ = data.GetCompilationAfterGenerators(out var diagnostics);
        Assert.NotEmpty(diagnostics);
    }
}
