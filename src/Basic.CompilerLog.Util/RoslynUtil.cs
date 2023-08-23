using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal static class RoslynUtil
{
    internal static SourceText GetSourceText(string filePath, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetSourceText(stream, checksumAlgorithm, canBeEmbedded);
    }

    /// <summary>
    /// Get a source text 
    /// </summary>
    /// <remarks>
    /// TODO: need to expose the real API for how the compiler reads source files. 
    /// move this comment to the rehydration code when we write it.
    /// </remarks>
    internal static SourceText GetSourceText(Stream stream, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded) =>
        SourceText.From(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);

    internal static SyntaxTree[] ParseAll(IReadOnlyList<(SourceText SourceText, string Path)> sourceTextList, ParseOptions parseOptions) =>
        parseOptions switch
        {
            CSharpParseOptions csharp => ParseAllCSharp(sourceTextList, csharp),
            VisualBasicParseOptions vb => ParseAllVisualBasic(sourceTextList, vb),
            _ => throw new ArgumentException(nameof(parseOptions)),
        };

    internal static VisualBasicSyntaxTree[] ParseAllVisualBasic(IReadOnlyList<(SourceText SourceText, string Path)> sourceTextList, VisualBasicParseOptions parseOptions)
    {
        if (sourceTextList.Count == 0)
        {
            return Array.Empty<VisualBasicSyntaxTree>();
        }

        var syntaxTrees = new VisualBasicSyntaxTree[sourceTextList.Count];
        Parallel.For(
            0,
            sourceTextList.Count,
            i =>
            {
                var t = sourceTextList[i];
                syntaxTrees[i] = (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
            });
        return syntaxTrees;
    }

    internal static CSharpSyntaxTree[] ParseAllCSharp(IReadOnlyList<(SourceText SourceText, string Path)> sourceTextList, CSharpParseOptions parseOptions)
    {
        if (sourceTextList.Count == 0)
        {
            return Array.Empty<CSharpSyntaxTree>();
        }

        var syntaxTrees = new CSharpSyntaxTree[sourceTextList.Count];
        Parallel.For(
            0,
            sourceTextList.Count,
            i =>
            {
                var t = sourceTextList[i];
                syntaxTrees[i] = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
            });
        return syntaxTrees;
    }
}
