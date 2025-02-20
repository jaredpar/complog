using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Basic.CompilerLog.Util;

[Flags]
public enum EmitFlags
{
    Default = 0,
    IncludePdbStream = 0b0001,
    IncludeMetadataStream = 0b0010,
    IncludeXmlStream = 0b0100,
    MetadataOnly = 0b1000,
}

public abstract class CompilationData
{
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private ImmutableArray<ISourceGenerator> _generators;
    private (Compilation, ImmutableArray<Diagnostic>)? _afterGenerators;

    public CompilerCall CompilerCall { get; } 
    public Compilation Compilation { get; }

    /// <summary>
    /// The <see cref="BasicAnalyzerHost"/> for the analyzers and generators.
    /// </summary>
    /// <remarks>
    /// This is *not* owned by this instance and should not be disposed from here. The creator
    /// of this <see cref="CompilationData"/> is responsible for managing the lifetime of this
    /// instance.
    /// </remarks>
    public BasicAnalyzerHost BasicAnalyzerHost { get; }

    public EmitData EmitData { get; }

    public ImmutableArray<AdditionalText> AdditionalTexts { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }
    public AnalyzerConfigOptionsProvider AnalyzerConfigOptionsProvider { get; }
    public EmitOptions EmitOptions { get; }
    public ParseOptions ParseOptions { get; }

    /// <summary>
    /// Diagnostics that resulted from rehydrating the compilation.
    /// </summary>
    public ImmutableArray<Diagnostic> CreationDiagnostics { get; }

    public CompilationOptions CompilationOptions => Compilation.Options;
    public bool IsCSharp => Compilation is CSharpCompilation;
    public bool IsVisualBasic => !IsCSharp;
    public CompilerCallKind Kind => CompilerCall.Kind;
    public AnalyzerOptions AnalyzerOptions => new AnalyzerOptions(AdditionalTexts, AnalyzerConfigOptionsProvider);

    public EmitFlags EmitFlags
    {
        get
        {
            var flags = EmitFlags.Default;
            if (EmitData.EmitPdb)
            {
                Debug.Assert(!EmitOptions.EmitMetadataOnly);
                flags |= EmitFlags.IncludePdbStream;
            }

            if (EmitData.XmlFilePath is not null)
            {
                flags |= EmitFlags.IncludeXmlStream;
            }

            if (IncludeMetadataStream())
            {
                flags |= EmitFlags.IncludeMetadataStream;
            }

            if (EmitOptions.EmitMetadataOnly)
            {
                flags |= EmitFlags.MetadataOnly;
            }

            return flags;
        }
    }

    private protected CompilationData(
        CompilerCall compilerCall,
        Compilation compilation,
        ParseOptions parseOptions,
        EmitOptions emitOptions,
        EmitData emitData,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider,
        ImmutableArray<Diagnostic> creationDiagnostics)
    {
        CompilerCall = compilerCall;
        Compilation = compilation;
        ParseOptions = parseOptions;
        EmitOptions = emitOptions;
        EmitData = emitData;
        AdditionalTexts = additionalTexts;
        BasicAnalyzerHost = basicAnalyzerHost;
        AnalyzerReferences = basicAnalyzerHost.AnalyzerReferences;
        AnalyzerConfigOptionsProvider = analyzerConfigOptionsProvider;
        CreationDiagnostics = creationDiagnostics;
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers()
    {
        EnsureAnalyzersLoaded();
        return _analyzers;
    }

    public ImmutableArray<ISourceGenerator> GetGenerators()
    {
        EnsureAnalyzersLoaded();
        return _generators;
    }

    public Compilation GetCompilationAfterGenerators(CancellationToken cancellationToken = default) =>
        GetCompilationAfterGenerators(out _, cancellationToken);

    /// <summary>
    /// Gets the <see cref="Compilation"/> after generators execute.
    /// </summary>
    /// <param name="diagnostics">The collection of <see cref="Diagnostic"/> that result from running generators</param>
    /// <param name="cancellationToken">Token to cancel generators</param>
    /// <returns></returns>
    public Compilation GetCompilationAfterGenerators(
        out ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        Compilation compilation;
        if (_afterGenerators is { } tuple)
        {
            (compilation, diagnostics) = tuple;
        }
        else
        {
            var driver = CreateGeneratorDriver();
            driver.RunGeneratorsAndUpdateCompilation(Compilation, out compilation, out diagnostics, cancellationToken);
            _afterGenerators = (compilation, diagnostics);
        }

        // Now that analyzers have completed running add any diagnostics the host has captured
        if (BasicAnalyzerHost.GetDiagnostics() is { Count: > 0 } list)
        {
            diagnostics = diagnostics.AddRange(list);
        }

        if (CreationDiagnostics.Length > 0)
        {
            diagnostics = diagnostics.AddRange(CreationDiagnostics);
        }

        return compilation;
    }

    public List<SyntaxTree> GetGeneratedSyntaxTrees(CancellationToken cancellationToken = default) =>
        GetGeneratedSyntaxTrees(out _, cancellationToken);

    public List<SyntaxTree> GetGeneratedSyntaxTrees(
        out ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        var afterCompilation = GetCompilationAfterGenerators(out diagnostics, cancellationToken);

        // Generated syntax trees are always added to the end of the list. This is an
        // implementation detail of the compiler, but one that is unlikely to ever
        // change. Doing so would represent a breaking change as file ordering impacts 
        // semantics.
        var originalCount = Compilation.SyntaxTrees.Count();
        return afterCompilation.SyntaxTrees.Skip(originalCount).ToList();
    }

    private void EnsureAnalyzersLoaded()
    {
        if (!_analyzers.IsDefault)
        {
            Debug.Assert(!_generators.IsDefault);
            return;
        }

        var languageName = IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic;
        var analyzerBuilder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var generatorBuilder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        foreach (var analyzerReference in AnalyzerReferences)
        {
            analyzerBuilder.AddRange(analyzerReference.GetAnalyzers(languageName));
            generatorBuilder.AddRange(analyzerReference.GetGenerators(languageName));
        }

        _analyzers = analyzerBuilder.ToImmutableArray();
        _generators = generatorBuilder.ToImmutableArray();
    }

    protected abstract GeneratorDriver CreateGeneratorDriver();

    /// <summary>
    /// This gets diagnostics from the compiler (does not include analyzers)
    /// </summary>
    public ImmutableArray<Diagnostic> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        var diagnostics = GetCompilationAfterGenerators(out var hostDiagnostics, cancellationToken).GetDiagnostics();
        return diagnostics.AddRange(hostDiagnostics);
    }

    /// <summary>
    /// This gets diagnostics from the compiler and any attached analyzers.
    /// </summary>
    public async Task<ImmutableArray<Diagnostic>> GetAllDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var compilation = GetCompilationAfterGenerators(out var hostDiagnostics, cancellationToken);
        var analyzers = GetAnalyzers();
        ImmutableArray<Diagnostic> diagnostics;
        if (analyzers.IsDefaultOrEmpty)
        {
            diagnostics = compilation.GetDiagnostics(cancellationToken);
        }
        else
        {
            var cwa = new CompilationWithAnalyzers(compilation, GetAnalyzers(), AnalyzerOptions);
            diagnostics = await cwa.GetAllDiagnosticsAsync().ConfigureAwait(false);
        }

        foreach (var additionalText in AdditionalTexts)
        {
            if (additionalText is BasicAdditionalText { Diagnostics.Length: > 0 } basicAdditionalText)
            {
                diagnostics = diagnostics.AddRange(basicAdditionalText.Diagnostics);
            }
        }

        return diagnostics.AddRange(hostDiagnostics);
    }

    public EmitDiskResult EmitToDisk(string directory, CancellationToken cancellationToken = default) =>
        EmitToDisk(directory, EmitFlags, EmitOptions, cancellationToken);

    public EmitDiskResult EmitToDisk(string directory, EmitFlags emitFlags, EmitOptions? emitOptions = null, CancellationToken cancellationToken = default)
    {
        var compilation = GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
        var assemblyName = EmitData.AssemblyFileName;
        if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
        {
            return new EmitDiskResult(
                success: false,
                directory,
                EmitData.AssemblyFileName,
                null,
                null,
                null,
                diagnostics);
        }

        string assemblyFilePath = Path.Combine(directory, assemblyName);
        emitOptions ??= EmitOptions;
        Stream? peStream = null;
        Stream? pdbStream = null;
        string? pdbFilePath = null;
        Stream? xmlStream = null;
        string? xmlFilePath = null;
        Stream? metadataStream = null;
        string? metadataFilePath = null;

        try
        { 
            peStream = OpenFile(assemblyFilePath);

            if ((emitFlags & EmitFlags.IncludePdbStream) != 0)
            {
                pdbFilePath = Path.Combine(directory, Path.ChangeExtension(assemblyName, ".pdb"));
                pdbStream = OpenFile(pdbFilePath);
            }

            if ((emitFlags & EmitFlags.IncludeXmlStream) != 0)
            {
                xmlFilePath = Path.Combine(directory, Path.ChangeExtension(assemblyName, ".xml"));
                xmlStream = OpenFile(xmlFilePath);
            }

            if ((emitFlags & EmitFlags.IncludeMetadataStream) != 0)
            {
                metadataFilePath = Path.Combine(directory, "ref", assemblyName);
                metadataStream = OpenFile(metadataFilePath);
            }

            if ((emitFlags & EmitFlags.MetadataOnly) != 0)
            {
                emitOptions = EmitOptions.WithEmitMetadataOnly(true);
            }

            var result = compilation.Emit(
                peStream,
                pdbStream,
                xmlStream,
                EmitData.Win32ResourceStream,
                EmitData.Resources,
                emitOptions,
                debugEntryPoint: null,
                EmitData.SourceLinkStream,
                EmitData.EmbeddedTexts,
                cancellationToken);
            diagnostics = diagnostics.Concat(result.Diagnostics).ToImmutableArray();
            return new EmitDiskResult(
                result.Success,
                directory,
                assemblyName,
                pdbFilePath,
                xmlFilePath,
                metadataFilePath,
                diagnostics);
        }
        finally
        {
            peStream?.Dispose();
            pdbStream?.Dispose();
            xmlStream?.Dispose();
            metadataStream?.Dispose();
        }

        Stream OpenFile(string filePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            return new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
    }

    public EmitMemoryResult EmitToMemory(
        EmitFlags? emitFlags = null,
        EmitOptions? emitOptions = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
        if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
        {
            return new EmitMemoryResult(
                success: false,
                assemblyStream: new MemoryStream(),
                pdbStream: null,
                xmlStream: null,
                metadataStream: null,
                diagnostics);
        }

        return compilation.EmitToMemory(
            emitFlags ?? EmitFlags,
            win32ResourceStream: EmitData.Win32ResourceStream,
            manifestResources: EmitData.Resources,
            emitOptions: emitOptions ?? EmitOptions,
            sourceLinkStream: EmitData.SourceLinkStream,
            embeddedTexts: EmitData.EmbeddedTexts,
            cancellationToken: cancellationToken);
    }

    private bool IncludeMetadataStream() =>
        !EmitOptions.EmitMetadataOnly &&
        !EmitOptions.IncludePrivateMembers &&
        CompilationOptions.OutputKind != OutputKind.NetModule;
}

public abstract class CompilationData<TCompilation, TParseOptions> : CompilationData
    where TCompilation : Compilation
    where TParseOptions : ParseOptions
{
    public new TCompilation Compilation => (TCompilation)base.Compilation;
    public new TParseOptions ParseOptions => (TParseOptions)base.ParseOptions;

    private protected CompilationData(
        CompilerCall compilerCall,
        TCompilation compilation,
        TParseOptions parseOptions,
        EmitOptions emitOptions,
        EmitData emitData,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider,
        ImmutableArray<Diagnostic> creationDiagnostics)
        :base(compilerCall, compilation, parseOptions, emitOptions, emitData, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider, creationDiagnostics)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpParseOptions>
{
    internal CSharpCompilationData(
        CompilerCall compilerCall,
        CSharpCompilation compilation,
        CSharpParseOptions parseOptions,
        EmitOptions emitOptions,
        EmitData emitData,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider,
        ImmutableArray<Diagnostic> creationDiagnostics)
        :base(compilerCall, compilation, parseOptions, emitOptions, emitData, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider, creationDiagnostics)
    {

    }

    protected override GeneratorDriver CreateGeneratorDriver() =>
        CSharpGeneratorDriver.Create(GetGenerators(), AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}

public sealed class VisualBasicCompilationData : CompilationData<VisualBasicCompilation, VisualBasicParseOptions>
{
    internal VisualBasicCompilationData(
        CompilerCall compilerCall,
        VisualBasicCompilation compilation,
        VisualBasicParseOptions parseOptions,
        EmitOptions emitOptions,
        EmitData emitData,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider,
        ImmutableArray<Diagnostic> creationDiagnostics)
        : base(compilerCall, compilation, parseOptions, emitOptions, emitData, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider, creationDiagnostics)
    {
    }

    protected override GeneratorDriver CreateGeneratorDriver() =>
        VisualBasicGeneratorDriver.Create(GetGenerators(), AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}
