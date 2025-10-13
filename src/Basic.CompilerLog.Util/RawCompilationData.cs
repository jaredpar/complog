using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Configuration.Internal;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal enum RawContentKind
{
    SourceText,
    GeneratedText,
    AdditionalText,
    AnalyzerConfig,
    Embed,

    /// <summary>
    /// This represents a #line directive target in a file that was embedded. These are different
    /// than normal line directives in that they are embedded into the compilation as well so the
    /// file is read from disk.
    /// </summary>
    EmbedLine,
    SourceLink,
    RuleSet,
    AppConfig,
    Win32Manifest,
    Win32Resource,
    Win32Icon,
    CryptoKeyFile,
    RuleSetInclude,
}

/// <summary>
/// This represents the raw content of a file that is embedded in the reader. It does not
/// go through any path normalization.
/// </summary>
internal readonly struct RawContent(string filePath, string? contentHash, RawContentKind kind)
{
    /// <summary>
    /// The original file path of the content.
    /// </summary>
    /// <remarks>
    /// This will be the original file path from when the log was created. It will not go through
    /// <see cref="PathNormalizationUtil"/>
    /// </remarks>
    internal string FilePath { get; } = filePath;

    /// <summary>
    /// A hash of the content if available. This will be null if the content was not available when
    /// the log was created.
    /// </summary>
    internal string? ContentHash { get; } = contentHash;

    internal RawContentKind Kind { get; } = kind;

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{Path.GetFileName(FilePath)} {Kind}";
}

