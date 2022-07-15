using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AnalyzerOptions = System.Collections.Immutable.ImmutableDictionary<string, string>;

namespace Basic.CompilerLog.Util.Impl;

internal sealed class BasicAnalyzerConfigOptions : AnalyzerConfigOptions
{
    internal static BasicAnalyzerConfigOptions Empty = new(AnalyzerOptions.Empty);

    public AnalyzerOptions AnalyzerOptions { get; }

    internal BasicAnalyzerConfigOptions(AnalyzerOptions options)
    {
        AnalyzerOptions = options;
    }

    public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        => AnalyzerOptions.TryGetValue(key, out value);
}
