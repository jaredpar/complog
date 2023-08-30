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

public abstract class CompilationData
{
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private ImmutableArray<ISourceGenerator> _generators;
    private (Compilation, ImmutableArray<Diagnostic>)? _afterGenerators;
    private readonly CommandLineArguments _commandLineArguments;

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

    public CompilationOptions CompilationOptions => Compilation.Options;
    public EmitOptions EmitOptions => _commandLineArguments.EmitOptions;
    public ParseOptions ParseOptions => _commandLineArguments.ParseOptions;
    public bool IsCSharp => Compilation is CSharpCompilation;
    public bool VisualBasic => !IsCSharp;
    public CompilerCallKind Kind => CompilerCall.Kind;

    private protected CompilationData(
        CompilerCall compilerCall,
        Compilation compilation,
        EmitData emitData,
        CommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
    {
        CompilerCall = compilerCall;
        Compilation = compilation;
        EmitData = emitData;
        _commandLineArguments = commandLineArguments;
        AdditionalTexts = additionalTexts;
        BasicAnalyzerHost = basicAnalyzerHost;
        AnalyzerReferences = basicAnalyzerHost.AnalyzerReferences;
        AnalyzerConfigOptionsProvider = analyzerConfigOptionsProvider;
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

    public Compilation GetCompilationAfterGenerators(out ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken = default)
    {
        if (_afterGenerators is { } tuple)
        {
            diagnostics = tuple.Item2;
            return tuple.Item1;
        }

        var driver = CreateGeneratorDriver();
        driver.RunGeneratorsAndUpdateCompilation(Compilation, out tuple.Item1, out tuple.Item2, cancellationToken);
        _afterGenerators = tuple;
        diagnostics = tuple.Item2;

        // Now that analyzers have completed running add any diagnostics the host has captured
        if (BasicAnalyzerHost.GetDiagnostics() is { Count: > 0} list)
        {
            diagnostics = diagnostics.AddRange(list);
        }

        return tuple.Item1;
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

    public EmitDiskResult EmitToDisk(string directory, CancellationToken cancellationToken = default)
    {
        var compilation = GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
        var assemblyName = CommonUtil.GetAssemblyFileName(_commandLineArguments);
        string assemblyFilePath = Path.Combine(directory, assemblyName);
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

            if (IncludePdbStream())
            {
                pdbFilePath = Path.Combine(directory, Path.ChangeExtension(assemblyName, ".pdb"));
                pdbStream = OpenFile(pdbFilePath);
            }

            if (_commandLineArguments.DocumentationPath is not null)
            {
                xmlFilePath = Path.Combine(directory, Path.ChangeExtension(assemblyName, ".xml"));
                xmlStream = OpenFile(xmlFilePath);
            }

            if (IncludeMetadataStream())
            {
                metadataFilePath = Path.Combine(directory, "ref", assemblyName);
                metadataStream = OpenFile(metadataFilePath);
            }

            var result = compilation.Emit(
                peStream,
                pdbStream,
                xmlStream,
                EmitData.Win32ResourceStream,
                EmitData.Resources,
                EmitOptions,
                debugEntryPoint: null,
                EmitData.SourceLinkStream,
                EmitData.EmbeddedTexts,
                cancellationToken);
            diagnostics = diagnostics.Concat(result.Diagnostics).ToImmutableArray();
            return new EmitDiskResult(
                result.Success,
                directory,
                assemblyName,
                assemblyFilePath,
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

    public EmitMemoryResult EmitToMemory(CancellationToken cancellationToken = default)
    {
        var compilation = GetCompilationAfterGenerators(out var diagnostics, cancellationToken);
        MemoryStream assemblyStream = new MemoryStream();
        MemoryStream? pdbStream = null;
        MemoryStream? xmlStream = null;
        MemoryStream? metadataStream = null;

        if (IncludePdbStream())
        {
            pdbStream = new MemoryStream();
        }

        if (_commandLineArguments.DocumentationPath is not null)
        {
            xmlStream = new MemoryStream();
        }

        if (IncludeMetadataStream())
        {
            metadataStream = new MemoryStream();
        }

        var result = compilation.Emit(
            assemblyStream,
            pdbStream,
            xmlStream,
            EmitData.Win32ResourceStream,
            EmitData.Resources,
            EmitOptions,
            debugEntryPoint: null,
            EmitData.SourceLinkStream,
            EmitData.EmbeddedTexts,
            cancellationToken);
        diagnostics = diagnostics.Concat(result.Diagnostics).ToImmutableArray();
        return new EmitMemoryResult(
            result.Success,
            assemblyStream,
            pdbStream,
            xmlStream,
            metadataStream,
            diagnostics);
    }

    private bool IncludePdbStream() =>
        EmitOptions.DebugInformationFormat != DebugInformationFormat.Embedded &&
        !EmitOptions.EmitMetadataOnly;

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
        EmitData emitData,
        CommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        :base(compilerCall, compilation, emitData, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpParseOptions>
{
    internal CSharpCompilationData(
        CompilerCall compilerCall,
        CSharpCompilation compilation,
        EmitData emitData,
        CSharpCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        :base(compilerCall, compilation, emitData, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
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
        EmitData emitData,
        VisualBasicCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        : base(compilerCall, compilation, emitData, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
    {
    }

    protected override GeneratorDriver CreateGeneratorDriver() =>
        VisualBasicGeneratorDriver.Create(GetGenerators(), AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}
