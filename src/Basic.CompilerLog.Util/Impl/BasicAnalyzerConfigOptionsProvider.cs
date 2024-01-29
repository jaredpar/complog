using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

internal sealed class BasicAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    internal sealed class BasicAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        internal static readonly ImmutableDictionary<string, string> EmptyDictionary = ImmutableDictionary.Create<string, string>(KeyComparer);

        public static BasicAnalyzerConfigOptions Empty { get; } = new BasicAnalyzerConfigOptions(EmptyDictionary);

        // Note: Do not rename. Older versions of analyzers access this field via reflection.
        // https://github.com/dotnet/roslyn/blob/8e3d62a30b833631baaa4e84c5892298f16a8c9e/src/Workspaces/SharedUtilitiesAndExtensions/Compiler/Core/Options/EditorConfig/EditorConfigStorageLocationExtensions.cs#L21
        internal readonly ImmutableDictionary<string, string> Options;

        public BasicAnalyzerConfigOptions(ImmutableDictionary<string, string> options)
            => Options = options;

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
            => Options.TryGetValue(key, out value);
    }

    private readonly Dictionary<object, BasicAnalyzerConfigOptions> _optionMap;

    public bool IsEmpty => _optionMap.Count == 0;
    public override AnalyzerConfigOptions GlobalOptions { get; }

    internal BasicAnalyzerConfigOptionsProvider(
        bool isConfigEmpty,
        AnalyzerConfigOptionsResult globalOptions,
        List<(object, AnalyzerConfigOptionsResult)> resultList)
    {
        GlobalOptions = isConfigEmpty
            ? BasicAnalyzerConfigOptions.Empty
            : new BasicAnalyzerConfigOptions(globalOptions.AnalyzerOptions);

        _optionMap = new();
        if (!isConfigEmpty)
        {
            foreach (var tuple in resultList)
            {
                var options = tuple.Item2.AnalyzerOptions;
                if (options.Count > 0)
                {
                    _optionMap[tuple.Item1] = new BasicAnalyzerConfigOptions(tuple.Item2.AnalyzerOptions);
                }
            }
        }
    }

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        _optionMap.TryGetValue(tree, out var options) ? options : BasicAnalyzerConfigOptions.Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        _optionMap.TryGetValue(textFile, out var options) ? options : BasicAnalyzerConfigOptions.Empty;
}
