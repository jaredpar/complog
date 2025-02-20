using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

internal abstract class BasicAdditionalText(string filePath) : AdditionalText
{
    public override string Path { get; } = filePath;
    public abstract ImmutableArray<Diagnostic> Diagnostics { get; }

    public abstract override SourceText? GetText(CancellationToken cancellationToken = default);
}

internal sealed class BasicAdditionalSourceText : BasicAdditionalText
{
    private ImmutableArray<Diagnostic> _coreDiagnostics = [];
    public SourceText? SourceText { get; }
    public override ImmutableArray<Diagnostic> Diagnostics => _coreDiagnostics;

    internal BasicAdditionalSourceText(string filePath, SourceText? sourceText)
        : base(filePath)
    {
        SourceText = sourceText;
    }

    public override SourceText? GetText(CancellationToken cancellationToken = default)
    {
        if (SourceText is null && _coreDiagnostics.Length == 0)
        {
            _coreDiagnostics = [Diagnostic.Create(RoslynUtil.CannotReadFileDiagnosticDescriptor, Location.None, Path)];
        }

        return SourceText;
    }
}

internal sealed class BasicAdditionalTextFile(string filePath, SourceHashAlgorithm checksumAlgorithm) 
    : BasicAdditionalText(filePath)
{
    private ImmutableArray<Diagnostic> _coreDiagnostics = [];
    public SourceHashAlgorithm ChecksumAlgorithm { get; } = checksumAlgorithm;
    public override ImmutableArray<Diagnostic> Diagnostics => _coreDiagnostics;

    public override SourceText? GetText(CancellationToken cancellationToken = default) =>
        _coreDiagnostics.Length == 0
            ? RoslynUtil.TryGetSourceText(Path, ChecksumAlgorithm, canBeEmbedded: false, out _coreDiagnostics)
            : null;
}