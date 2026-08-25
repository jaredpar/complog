using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

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
    private Lazy<(ImmutableArray<DiagnosticAnalyzer> Analyzers, ImmutableArray<Diagnostic> AnalyzerDiagnostics, ImmutableArray<ISourceGenerator> Generators, ImmutableArray<Diagnostic> GeneratorDiagnostics)> _lazyAnalyzers;
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
        _lazyAnalyzers = new(LoadAnalyzers, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(out ImmutableArray<Diagnostic> diagnostics)
    {
        var value = _lazyAnalyzers.Value;
        diagnostics = value.AnalyzerDiagnostics;
        return value.Analyzers;
    }

    public ImmutableArray<ISourceGenerator> GetGenerators(out ImmutableArray<Diagnostic> diagnostics)
    {
        var value = _lazyAnalyzers.Value;
        diagnostics = value.GeneratorDiagnostics;
        return value.Generators;
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
            var driver = CreateGeneratorDriver(GetGenerators(out var creationDiagnostics));
            driver.RunGeneratorsAndUpdateCompilation(Compilation, out compilation, out var executionDiagnostics, cancellationToken);
            diagnostics = creationDiagnostics.AddRange(executionDiagnostics);
            _afterGenerators = (compilation, diagnostics);
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

    private (ImmutableArray<DiagnosticAnalyzer> Analyzers, ImmutableArray<Diagnostic> AnalyzerDiagnostics, ImmutableArray<ISourceGenerator> Generators, ImmutableArray<Diagnostic> GeneratorDiagnostics) LoadAnalyzers()
    {
        var language = IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic;
        var analyzerBuilder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var generatorBuilder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var analyzerDiagnostics = new List<Diagnostic>();
        var generatorDiagnostics = new List<Diagnostic>();
        foreach (var analyzerReference in AnalyzerReferences)
        {
            var bar = analyzerReference.AsBasicAnalyzerReference();
            analyzerBuilder.AddRange(bar.GetAnalyzers(language, analyzerDiagnostics));
            generatorBuilder.AddRange(bar.GetGenerators(language, generatorDiagnostics));
        }

        return (
            analyzerBuilder.ToImmutableArray(),
            analyzerDiagnostics.ToImmutableArray(),
            generatorBuilder.ToImmutableArray(),
            generatorDiagnostics.ToImmutableArray());
    }

    protected abstract GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators);

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
        var analyzers = GetAnalyzers(out var analyzerDiagnostics);
        ImmutableArray<Diagnostic> diagnostics;
        if (analyzers.IsDefaultOrEmpty)
        {
            diagnostics = compilation.GetDiagnostics(cancellationToken);
        }
        else
        {
            var cwa = new CompilationWithAnalyzers(compilation, analyzers, AnalyzerOptions);
            diagnostics = await cwa.GetAllDiagnosticsAsync().ConfigureAwait(false);
        }

        foreach (var additionalText in AdditionalTexts)
        {
            if (additionalText is BasicAdditionalText { Diagnostics.Length: > 0 } basicAdditionalText)
            {
                diagnostics = diagnostics.AddRange(basicAdditionalText.Diagnostics);
            }
        }

        return diagnostics.AddRange(hostDiagnostics).AddRange(analyzerDiagnostics);
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

            if ((emitFlags & EmitFlags.IncludePdbStream) != 0 && emitOptions.DebugInformationFormat != DebugInformationFormat.Embedded)
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
                GetWin32Resources(compilation),
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

    /// <summary>
    /// The Win32 resources to pass to emit. When the original build passed /win32res that
    /// content is used directly. Otherwise mirror the command line compiler: it synthesizes
    /// the version resource (plus any /win32icon and /win32manifest content) from the
    /// compilation rather than emitting no resources at all.
    /// </summary>
    private Stream? GetWin32Resources(Compilation compilation)
    {
        if (EmitData.Win32ResourceStream is { } win32ResourceStream)
        {
            win32ResourceStream.Position = 0;
            return win32ResourceStream;
        }

        if (compilation.Options.OutputKind == OutputKind.NetModule)
        {
            return null;
        }

        if (EmitData.Win32ManifestStream is { } manifestStream)
        {
            manifestStream.Position = 0;
        }
        if (EmitData.Win32IconStream is { } iconStream)
        {
            iconStream.Position = 0;
        }

        return compilation.CreateDefaultWin32Resources(
            versionResource: true,
            noManifest: false,
            manifestContents: EmitData.Win32ManifestStream,
            iconInIcoFormat: EmitData.Win32IconStream);
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
            win32ResourceStream: GetWin32Resources(compilation),
            manifestResources: EmitData.Resources,
            emitOptions: emitOptions ?? EmitOptions,
            sourceLinkStream: EmitData.SourceLinkStream,
            embeddedTexts: EmitData.EmbeddedTexts,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// This returns the compilation as a content string. Two compilations that are equal will have the
    /// same content text. This can be checksum'd to produce concise compilation ids
    /// </summary>
    /// <returns></returns>
    public string GetContentHash()
    {
        var assembly = typeof(Compilation).Assembly;
        var type = assembly.GetType( "Microsoft.CodeAnalysis.DeterministicKey", throwOnError: true)!;
        var method = type
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(x => IsMethod(x))
            .Single();

        // This removes our implementation of the syntax tree options provider. Leaving that in means
        // that the content text will be different every time compiler log is updated and that is not
        // desirable.
        var options = CompilationOptions.WithSyntaxTreeOptionsProvider(null);

        var (analyzers, analyzerDiagnostics, generators, generatorDiagnostics) = _lazyAnalyzers.Value;

        // This removes file full paths and tool versions from the content text.
        int flags = 0b11;

        // The signature of DeterministicKey.GetDeterministicKey has grown over time. For example Roslyn
        // 5.6.0 inserted the sourceLinkStream and resources parameters. Rather than hard code a positional
        // argument array (which throws TargetParameterCountException when the parameter count changes) build
        // the arguments by matching parameter names. Any parameter we don't explicitly supply falls back to
        // its type default, which keeps us resilient to future optional parameters being added.
        var argValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["compilationOptions"] = options,
            ["syntaxTrees"] = Compilation.SyntaxTrees.ToImmutableArray(),
            ["references"] = Compilation.References.ToImmutableArray(),
            ["publicKey"] = ImmutableArray<byte>.Empty,
            ["additionalTexts"] = AdditionalTexts,
            ["analyzers"] = analyzers,
            ["generators"] = generators,
            ["pathMap"] = ImmutableArray<KeyValuePair<string, string>>.Empty,
            ["emitOptions"] = EmitOptions,
            ["options"] = flags,
            ["cancellationToken"] = (CancellationToken)default,
        };

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (argValues.TryGetValue(parameter.Name!, out var value))
            {
                args[i] = value;
            }
            else
            {
                var parameterType = parameter.ParameterType;
                args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
            }
        }

        var result = method.Invoke(null, args)!;
        var contentHash = (string)result;
        var diagnostics = analyzerDiagnostics.AddRange(generatorDiagnostics);
        if (diagnostics.Length > 0)
        {
            contentHash += string.Join("", diagnostics.Select(x => x.ToString()));
        }

        return contentHash;

        static bool IsMethod(MethodInfo method)
        {
            if (method.Name != "GetDeterministicKey")
            {
                return false;
            }

            var parameters = method.GetParameters();
            if (parameters.Length < 2)
            {
                return false;
            }

            return parameters[1].ParameterType == typeof(ImmutableArray<SyntaxTree>);
        }
    }

    /// <summary>
    /// This produces the content hash from <see cref="GetContentHash"/> as well as the identity hash
    /// which is just a checksum of the content hash.
    /// </summary>
    /// <returns></returns>
    public (string ContentHash, string IdentityHash) GetContentAndIdentityHash()
    {
        var contentHash = GetContentHash();
        var identityHash = GetIdentityHash(contentHash);
        return (contentHash, identityHash);
    }

    public string GetIdentityHash() =>
        GetIdentityHash(GetContentHash());

    private static string GetIdentityHash(string contentHash)
    {
        var sum = SHA256.Create();
        var bytes = sum.ComputeHash(Encoding.UTF8.GetBytes(contentHash));
        return bytes.AsHexString();
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

    protected override GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators) =>
        CSharpGeneratorDriver.Create(generators, AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
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

    protected override GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators) =>
        VisualBasicGeneratorDriver.Create(generators, AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}
