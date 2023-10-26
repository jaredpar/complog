using System.Collections.Immutable;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using CSharpLanguageVersion=Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using BasicLanguageVersion=Microsoft.CodeAnalysis.VisualBasic.LanguageVersion;
using Microsoft.CodeAnalysis.Emit;

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
    public ImmutableArray<GlobalImport> GlobalImports { get; set; }
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
    public CSharpLanguageVersion LanguageVersion { get; set; }
    [Key(1)]
    public CSharpLanguageVersion SpecifiedLanguageVersion { get; set; }
    [Key(2)]
    public IEnumerable<string>? PreprocessorSymbols { get; set; }
    [Key(3)]
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