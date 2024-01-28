using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

internal class BasicSyntaxTreeOptionsProvider : SyntaxTreeOptionsProvider
{
    internal static readonly ImmutableDictionary<string, ReportDiagnostic> EmptyDiagnosticOptions =
        ImmutableDictionary.Create<string, ReportDiagnostic>(CaseInsensitiveComparison.Comparer);

    internal readonly struct Options
    {
        public readonly GeneratedKind IsGenerated;
        public readonly ImmutableDictionary<string, ReportDiagnostic> DiagnosticOptions;

        public Options(AnalyzerConfigOptionsResult? result)
        {
            if (result is AnalyzerConfigOptionsResult r)
            {
                DiagnosticOptions = r.TreeOptions;
                IsGenerated = GetIsGeneratedCodeFromOptions(r.AnalyzerOptions);
            }
            else
            {
                DiagnosticOptions = EmptyDiagnosticOptions;
                IsGenerated = GeneratedKind.Unknown;
            }
        }

        internal static GeneratedKind GetIsGeneratedCodeFromOptions(ImmutableDictionary<string, string> options)
        {
            // Check for explicit user configuration for generated code.
            //     generated_code = true | false
            if (options.TryGetValue("generated_code", out string? optionValue) &&
                bool.TryParse(optionValue, out var boolValue))
            {
                return boolValue ? GeneratedKind.MarkedGenerated : GeneratedKind.NotGenerated;
            }
 
            // Either no explicit user configuration or we don't recognize the option value.
            return GeneratedKind.Unknown;
        }
    }

    private readonly ImmutableDictionary<SyntaxTree, Options> _options;

    private readonly AnalyzerConfigOptionsResult _globalOptions;

    internal bool IsEmpty => _options.IsEmpty;

    internal BasicSyntaxTreeOptionsProvider(
        bool isConfigEmpty,
        AnalyzerConfigOptionsResult globalOptions,
        List<(object, AnalyzerConfigOptionsResult)> resultList)
    {
        var builder = ImmutableDictionary.CreateBuilder<SyntaxTree, Options>();
        foreach (var tuple in resultList)
        {
            if (tuple.Item1 is SyntaxTree syntaxTree)
            {
                builder.Add(syntaxTree, new Options(isConfigEmpty ? null : tuple.Item2));
            }
        }

        _options = builder.ToImmutableDictionary();
        _globalOptions = globalOptions;
    }

    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken _)
        => _options.TryGetValue(tree, out var value) ? value.IsGenerated : GeneratedKind.Unknown;

    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken _, out ReportDiagnostic severity)
    {
        if (_options.TryGetValue(tree, out var value))
        {
            return value.DiagnosticOptions.TryGetValue(diagnosticId, out severity);
        }
        severity = ReportDiagnostic.Default;
        return false;
    }

    public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken _, out ReportDiagnostic severity)
    {
        if (_globalOptions.TreeOptions is object)
        {
            return _globalOptions.TreeOptions.TryGetValue(diagnosticId, out severity);
        }
        severity = ReportDiagnostic.Default;
        return false;
    }
}
