
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Testing.Platform.Extensions.Messages;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilationDataTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilationDataTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilationDataTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void EmitToMemoryCombinations()
    {
        RunInContext(Fixture.ClassLib.Value.CompilerLogPath, static (testOutputHelper, filePath, cancellationToken) =>
        {
            using var reader = CompilerLogReader.Create(filePath);
            var data = reader.ReadCompilationData(0);

            var emitResult = data.EmitToMemory(cancellationToken: cancellationToken);
            Assert.True(emitResult.Success);
            AssertEx.HasData(emitResult.AssemblyStream);
            AssertEx.HasData(emitResult.PdbStream);
            Assert.Null(emitResult.XmlStream);
            AssertEx.HasData(emitResult.MetadataStream);

            emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream, cancellationToken: cancellationToken);
            Assert.True(emitResult.Success);
            AssertEx.HasData(emitResult.AssemblyStream);
            AssertEx.HasData(emitResult.PdbStream);
            Assert.Null(emitResult.XmlStream);
            Assert.Null(emitResult.MetadataStream);

            emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream | EmitFlags.IncludeXmlStream, cancellationToken: cancellationToken);
            Assert.True(emitResult.Success);
            AssertEx.HasData(emitResult.AssemblyStream);
            AssertEx.HasData(emitResult.PdbStream);
            AssertEx.HasData(emitResult.XmlStream);
            Assert.Null(emitResult.MetadataStream);

            emitResult = data.EmitToMemory(EmitFlags.IncludePdbStream | EmitFlags.IncludeXmlStream | EmitFlags.IncludeMetadataStream, cancellationToken: cancellationToken);
            Assert.True(emitResult.Success);
            AssertEx.HasData(emitResult.AssemblyStream);
            AssertEx.HasData(emitResult.PdbStream);
            AssertEx.HasData(emitResult.XmlStream);
            AssertEx.HasData(emitResult.MetadataStream);

            emitResult = data.EmitToMemory(EmitFlags.MetadataOnly, cancellationToken: cancellationToken);
            Assert.True(emitResult.Success);
            AssertEx.HasData(emitResult.AssemblyStream);
            Assert.Null(emitResult.PdbStream);
            Assert.Null(emitResult.XmlStream);
            Assert.Null(emitResult.MetadataStream);
        });
    }

    [Fact]
    public void EmitToMemoryRefOnly()
    {
        RunInContext(Fixture.ClassLibRefOnly.Value.CompilerLogPath, static (testOutputHelper, filePath, cancellationToken) =>
        {
            using var reader = CompilerLogReader.Create(filePath);
            var data = reader.ReadCompilationData(0);
            var result = data.EmitToMemory(cancellationToken: cancellationToken);
            Assert.True(result.Success);
        });
    }

    [WindowsFact]
    public void EmitToMemoryGeneratorError()
    {
        // Can't use None with a native PDB which leads to generator error
        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithNativePdb!.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var result = data.EmitToMemory();
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
        Assert.True(result.Diagnostics.Any(x => x.Id == RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor.Id));
    }

    [Fact]
    public void EmitToMemorySatellite()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibWithResourceLibs.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(1);
        Assert.Equal(CompilerCallKind.Satellite, data.Kind);
        var result = data.EmitToMemory(cancellationToken: CancellationToken);
        Assert.True(result.Success);
    }

    [WindowsFact]
    public void EmitToDiskGeneratorError()
    {
        // Can't use None with a native PDB which leads to generator error
        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithNativePdb!.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        var result = data.EmitToDisk(Root.DirectoryPath);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
        Assert.True(result.Diagnostics.Any(x => x.Id == RoslynUtil.ErrorReadingGeneratedFilesDiagnosticDescriptor.Id));
    }

    [Fact]
    public void EmitToDiskMemorySatellite()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibWithResourceLibs.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(1);
        Assert.Equal(CompilerCallKind.Satellite, data.Kind);
        var result = data.EmitToDisk(Root.DirectoryPath, cancellationToken: CancellationToken);
        Assert.True(result.Success);
    }

    [Fact]
    public void GetAnalyzersNormal()
    {
        RunInContext(Fixture.ClassLib.Value.CompilerLogPath, static (testOtputHelper, filePath, _) =>
        {
            using var reader = CompilerLogReader.Create(filePath);
            var data = reader.ReadCompilationData(0);
            Assert.NotEmpty(data.GetAnalyzers());
        });
    }

    [Fact]
    public void GetAnalyzersNoHosting()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLib.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        Assert.Empty(data.GetAnalyzers());
    }

    [Fact]
    public void GetDiagnostics()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLib.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = reader.ReadCompilationData(0);
        Assert.NotEmpty(data.GetDiagnostics(CancellationToken));
    }

    [Theory]
    [MemberData(nameof(GetSupportedBasicAnalyzerKinds))]
    public void GetAllDiagnostics(BasicAnalyzerKind basicAnalyzerKind)
    {
        RunInContext((FilePath: Fixture.ClassLib.Value.CompilerLogPath, Kind: basicAnalyzerKind), static (testOutputHelper, state, cancellationToken) =>
        {
            using var reader = CompilerLogReader.Create(state.FilePath, state.Kind);
            var data = reader.ReadCompilationData(0);
            Assert.NotEmpty(data.GetAllDiagnosticsAsync(cancellationToken).Result);
        });
    }

    [Fact]
    public async Task GetAllDiagnosticsNoAnalyzers()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLib.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var data = (CSharpCompilationData)reader.ReadCompilationData(0);
        var host = new BasicAnalyzerHostOnDisk(reader, []);
        data = new CSharpCompilationData(
            data.CompilerCall,
            data.Compilation,
            data.ParseOptions,
            data.EmitOptions,
            data.EmitData,
            data.AdditionalTexts,
            host,
            data.AnalyzerConfigOptionsProvider,
            creationDiagnostics: []);
        Assert.NotEmpty(await data.GetAllDiagnosticsAsync(CancellationToken));
    }

    [Fact]
    public void GetCompilationAfterGeneratorsDiagnostics()
    {
        RunInContext(Fixture.Console.Value.CompilerLogPath, static (testOutputHelper, logFilePath, cancellationToken) =>
        {
            using var reader = CompilerLogReader.Create(
                logFilePath,
                BasicAnalyzerHost.DefaultKind);
            var rawData = reader.ReadAllAnalyzerData(0);
            var analyzers = rawData
                .Where(x => x.FileName != "Microsoft.CodeAnalysis.NetAnalyzers.dll")
                .ToList();
            BasicAnalyzerHost host = IsNetCore
                ? new BasicAnalyzerHostInMemory(reader, analyzers)
                : new BasicAnalyzerHostOnDisk(reader, analyzers);
            var data = (CSharpCompilationData)reader.ReadCompilationData(0);
            data = new CSharpCompilationData(
                data.CompilerCall,
                data.Compilation,
                data.ParseOptions,
                data.EmitOptions,
                data.EmitData,
                data.AdditionalTexts,
                host,
                data.AnalyzerConfigOptionsProvider,
                creationDiagnostics: []);
            _ = data.GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
            Assert.NotEmpty(diagnostics);
        });
    }

    [Theory]
    [MemberData(nameof(GetSimpleBasicAnalyzerKinds))]
    public void GetGeneratedSyntaxTrees(BasicAnalyzerKind basicAnalyzerKind)
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, basicAnalyzerKind);
        var data = reader.ReadAllCompilationData().Single();
        var trees = data.GetGeneratedSyntaxTrees(CancellationToken);
        Assert.Single(trees);

        trees = data.GetGeneratedSyntaxTrees(out var diagnostics, CancellationToken);
        Assert.Single(trees);
        Assert.Empty(diagnostics);
    }

#if NET

    [Theory]
    [InlineData(true)]
    // https://github.com/jaredpar/complog/issues/241
    // [InlineData(false)]
    public void GetContentHashBadAnalyzer(bool inMemory)
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLib.Value.CompilerLogPath, BasicAnalyzerKind.None);
        using BasicAnalyzerHost host = inMemory
            ? new BasicAnalyzerHostInMemory(LibraryUtil.GetUnloadableAnalyzers())
            : new BasicAnalyzerHostOnDisk(State, LibraryUtil.GetUnloadableAnalyzers());
        var compilationData = reader
            .ReadCompilationData(0)
            .WithBasicAnalyzerHost(host);
        var content = compilationData.GetContentHash();
        Assert.Contains(RoslynUtil.CannotLoadTypesDiagnosticDescriptor.Id, content);
    }

#endif
}
