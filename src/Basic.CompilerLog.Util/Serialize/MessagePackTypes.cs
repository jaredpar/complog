using System.Collections.Immutable;
using System.Collections.Generic;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using CSharpLanguageVersion=Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using BasicLanguageVersion=Microsoft.CodeAnalysis.VisualBasic.LanguageVersion;
using Microsoft.CodeAnalysis.Emit;
using System.Globalization;
using Microsoft.CodeAnalysis.Text;

// This is a file for serialization types, the nullable state here records the intent of
// the fields. It's not possible to make these completely nullable safe. 
#nullable disable warnings

namespace Basic.CompilerLog.Util.Serialize;

[MessagePackObject]
public class CompilationOptionsPack
{
    [Key(0)]
    public OutputKind OutputKind { get; set; }
    [Key(1)]
    public string? ModuleName { get; set; }
    [Key(2)]
    public string? ScriptClassName { get; set; }
    [Key(3)]
    public string? MainTypeName { get; set; }
    [Key(4)]
    public ImmutableArray<byte> CryptoPublicKey { get; set; }
    [Key(5)]
    public string? CryptoKeyFile { get; set; }
    [Key(6)]
    public string? CryptoKeyContainer { get; set; }
    [Key(7)]
    public bool? DelaySign { get; set; }
    [Key(8)]
    public bool PublicSign { get; set; }
    [Key(9)]
    public bool CheckOverflow { get; set; }
    [Key(10)]
    public Platform Platform { get; set; }
    [Key(11)]
    public OptimizationLevel OptimizationLevel { get; set; }
    [Key(12)]
    public ReportDiagnostic GeneralDiagnosticOption { get; set; }
    [Key(13)]
    public int WarningLevel { get; set; }
    [Key(14)]
    public bool ConcurrentBuild { get; set; }
    [Key(15)]
    public bool Deterministic { get; set; }
    [Key(16)]
    public MetadataImportOptions MetadataImportOptions { get; set; }
    [Key(17)]
    public ImmutableDictionary<string, ReportDiagnostic>? SpecificDiagnosticOptions { get; set; }
    [Key(18)]
    public bool ReportSuppressedDiagnostics { get; set; }
    [Key(19)]
    public bool DebugPlusMode { get; set; }
} 

[MessagePackObject]
public class CSharpCompilationOptionsPack
{
    [Key(1)]
    public bool AllowUnsafe { get; set; }
    [Key(2)]
    public ImmutableArray<string> Usings { get; set; }
    [Key(3)]
    public NullableContextOptions NullableContextOptions { get; set; }
}

[MessagePackObject]
public class VisualBasicCompilationOptionsPack
{
    [Key(1)]
    public string[] GlobalImports { get; set; }
    [Key(2)]
    public string? RootNamespace { get; set; }
    [Key(3)]
    public OptionStrict OptionStrict { get; set; }
    [Key(4)]
    public bool OptionInfer { get; set; }
    [Key(5)]
    public bool OptionExplicit { get; set;}
    [Key(6)]
    public bool OptionCompareText { get; set; }
    [Key(7)]
    public bool EmbedVbCoreRuntime { get; set; }
}

[MessagePackObject]
public class ParseOptionsPack
{
    [Key(0)]
    public SourceCodeKind Kind { get; set; }
    [Key(1)]
    public SourceCodeKind SpecifiedKind { get; set; }
    [Key(2)]
    public DocumentationMode DocumentationMode { get; set; }
}

[MessagePackObject]
public class CSharpParseOptionsPack
{
    [Key(0)]
    public CSharpLanguageVersion SpecifiedLanguageVersion { get; set; }
    [Key(1)]
    public IEnumerable<string>? PreprocessorSymbols { get; set; }
    [Key(2)]
    public IReadOnlyDictionary<string, string>? Features { get; set; }
}

[MessagePackObject]
public class VisualBasicParseOptionsPack
{
    [Key(0)]
    public BasicLanguageVersion SpecifiedLanguageVersion { get; set; }
    [Key(1)]
    public BasicLanguageVersion LanguageVersion { get; set; }
    [Key(2)]
    public ImmutableArray<KeyValuePair<string, object>> PreprocessorSymbols { get; set; }
    [Key(3)]
    public IReadOnlyDictionary<string, string>? Features { get; set; }
}

[MessagePackObject]
public class EmitOptionsPack
{
    [Key(0)]
    public bool EmitMetadataOnly { get; set; }
    [Key(1)]
    public bool TolerateErrors { get; set; }
    [Key(2)]
    public bool IncludePrivateMembers { get; set; }
    [Key(3)]
    public ImmutableArray<InstrumentationKind> InstrumentationKinds { get; set; }
    [Key(4)]
    public (int, int) SubsystemVersion { get; set; }
    [Key(5)]
    public int FileAlignment { get; set; }
    [Key(6)]
    public bool HighEntropyVirtualAddressSpace { get; set; }
    [Key(7)]
    public ulong BaseAddress { get; set; }
    [Key(8)]
    public DebugInformationFormat DebugInformationFormat { get; set; }
    [Key(9)]
    public string? OutputNameOverride { get; set; }
    [Key(10)]
    public string? PdbFilePath { get; set; }
    [Key(11)]
    public string? PdbChecksumAlgorithm { get; set; }
    [Key(12)]
    public string? RuntimeMetadataVersion { get; set; }
    [Key(13)]
    public int? DefaultSourceFileEncoding { get; set; }
    [Key(14)]
    public int? FallbackSourceFileEncoding { get; set; }
}

[MessagePackObject]
public class ContentPack
{
    [Key(0)]
    public string ContentHash { get; set; }
    [Key(1)]
    public string FilePath { get; set; }

    public ContentPack()
    {

    }

    public ContentPack(string contentHash, string filePath = null)
    {
        FilePath = filePath;
        ContentHash = contentHash;
    }
}

[MessagePackObject]
public class ReferencePack
{
    [Key(0)]
    public Guid Mvid { get; set; }
    [Key(1)]
    public MetadataImageKind Kind { get; set; }
    [Key(2)]
    public bool EmbedInteropTypes { get; set; }
    [Key(3)]
    public ImmutableArray<string> Aliases { get; set; }
}

[MessagePackObject]
public class AnalyzerPack
{
    [Key(0)]
    public Guid Mvid { get; set; }
    [Key(1)]
    public string FilePath { get; set; }
}

[MessagePackObject]
public class ResourcePack
{
    [Key(0)]
    public string ContentHash { get; set; }
    [Key(1)]
    public string? FileName { get; set; }
    [Key(2)]
    public string Name { get; set; }
    [Key(3)]
    public bool IsPublic { get; set; }
}

[MessagePackObject]
public class CompilationInfoPack
{
    [Key(0)]
    public string ProjectFilePath { get; set; }
    [Key(1)]
    public bool IsCSharp { get; set; }
    [Key(2)]
    public string? TargetFramework { get; set; }
    [Key(3)]
    public CompilerCallKind CompilerCallKind { get; set; }
    [Key(4)]
    public string CommandLineArgsHash { get; set; }
    [Key(5)]
    public string CompilationDataPackHash { get; set; }
    [Key(6)]
    public string EmitOptionsHash { get; set; }
    [Key(7)]
    public string ParseOptionsHash { get; set; }
    [Key(8)]
    public string CompilationOptionsHash { get; set; }
}

[MessagePackObject]
public class CompilationDataPack
{
    [Key(0)]
    public List<(int, ContentPack)> ContentList { get; set; }
    [Key(1)]
    public Dictionary<string, string?> ValueMap { get; set; }
    [Key(2)]
    public List<ReferencePack> References { get; set; }
    [Key(3)]
    public List<AnalyzerPack> Analyzers { get; set; }
    [Key(4)]
    public List<ResourcePack> Resources { get; set; }
    [Key(5)]
    public bool IncludesGeneratedText { get; set; }
    [Key(6)]
    public SourceHashAlgorithm ChecksumAlgorithm { get; set; }
}