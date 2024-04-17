using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Basic.CompilerLog.Util;

internal static class RoslynUtil
{
    internal delegate bool SourceTextLineFunc(ReadOnlySpan<char> line, ReadOnlySpan<char> newLine);

    /// <summary>
    /// Get a source text 
    /// </summary>
    /// <remarks>
    /// TODO: need to expose the real API for how the compiler reads source files. 
    /// move this comment to the rehydration code when we write it.
    /// </remarks>
    internal static SourceText GetSourceText(Stream stream, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded) =>
        SourceText.From(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);

    internal static SourceText GetSourceText(string filePath, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetSourceText(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);
    }

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


    internal static (SyntaxTreeOptionsProvider, AnalyzerConfigOptionsProvider) CreateOptionsProviders(
        List<(SourceText SourceText, string Path)> analyzerConfigList,
        IEnumerable<SyntaxTree> syntaxTrees,
        IEnumerable<AdditionalText> additionalTexts,
        PathNormalizationUtil? pathNormalizationUtil = null)
    {
        pathNormalizationUtil ??= PathNormalizationUtil.Empty;

        AnalyzerConfigOptionsResult globalConfigOptions = default;
        AnalyzerConfigSet? analyzerConfigSet = null;
        var resultList = new List<(object, AnalyzerConfigOptionsResult)>();

        if (analyzerConfigList.Count > 0)
        {
            var list = new List<AnalyzerConfig>();
            foreach (var tuple in analyzerConfigList)
            {
                var configText = tuple.SourceText;
                if (IsGlobalEditorConfigWithSection(configText))
                {
                    var configTextContent = RewriteGlobalEditorConfigSections(
                        configText,
                        path => pathNormalizationUtil.NormalizePath(path));
                    configText = SourceText.From(configTextContent, configText.Encoding);
                }

                list.Add(AnalyzerConfig.Parse(configText, tuple.Path));
            }

            analyzerConfigSet = AnalyzerConfigSet.Create(list);
            globalConfigOptions = analyzerConfigSet.GlobalConfigOptions;
        }

        foreach (var syntaxTree in syntaxTrees)
        {
            resultList.Add((syntaxTree, analyzerConfigSet?.GetOptionsForSourcePath(syntaxTree.FilePath) ?? default));
        }

        foreach (var additionalText in additionalTexts)
        {
            resultList.Add((additionalText, analyzerConfigSet?.GetOptionsForSourcePath(additionalText.Path) ?? default));
        }

        var syntaxOptionsProvider = new BasicSyntaxTreeOptionsProvider(
            isConfigEmpty: analyzerConfigList.Count == 0,
            globalConfigOptions,
            resultList);
        var analyzerConfigOptionsProvider = new BasicAnalyzerConfigOptionsProvider(
            isConfigEmpty: analyzerConfigList.Count == 0,
            globalConfigOptions,
            resultList);
        return (syntaxOptionsProvider, analyzerConfigOptionsProvider);
    }

    internal static CSharpCompilationData CreateCSharpCompilationData(
        CompilerCall compilerCall,
        string? compilationName,
        CSharpParseOptions parseOptions,
        CSharpCompilationOptions compilationOptions,
        List<(SourceText SourceText, string Path)> sourceTexts,
        List<MetadataReference> references,
        List<(SourceText SourceText, string Path)> analyzerConfigs,
        ImmutableArray<AdditionalText> additionalTexts,
        EmitOptions emitOptions,
        EmitData emitData,
        BasicAnalyzerHost basicAnalyzerHost,
        PathNormalizationUtil pathNormalizationUtil)
    {
        var syntaxTrees = ParseAllCSharp(sourceTexts, parseOptions);
        var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(analyzerConfigs, syntaxTrees, additionalTexts, pathNormalizationUtil);

        compilationOptions = compilationOptions
            .WithSyntaxTreeOptionsProvider(syntaxProvider)
            .WithStrongNameProvider(new DesktopStrongNameProvider());

        var compilation = CSharpCompilation.Create(
            compilationName,
            syntaxTrees,
            references,
            compilationOptions);

        return new CSharpCompilationData(
            compilerCall,
            compilation,
            parseOptions,
            emitOptions,
            emitData,
            additionalTexts,
            basicAnalyzerHost,
            analyzerProvider);
    }

    internal static string RewriteGlobalEditorConfigSections(SourceText sourceText, Func<string, string> pathMapFunc)
    {
        var builder = new StringBuilder();
        ForEachLine(sourceText, (line, newLine) =>
        {
            if (line.Length == 0)
            {
                builder.Append(newLine);
                return true;
            }

            if (line[0] == '[')
            {
                var index = line.IndexOf(']');
                if (index > 0)
                {
                    var mapped = pathMapFunc(line.Slice(1, index - 1).ToString());
                    builder.Append('[');
                    builder.Append(mapped);
                    builder.Append(line.Slice(index));
                }
            }
            else
            {
                builder.Append(line);
            }

            builder.Append(newLine);
            return true;
        });

        return builder.ToString();
    }

    internal static Guid GetMvid(string filePath)
    {
        using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetMvid(file);
    }

    internal static Guid GetMvid(Stream stream)
    {
        using var reader = new PEReader(stream);
        var mdReader = reader.GetMetadataReader();
        GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
        return mdReader.GetGuid(handle);
    }

    internal static bool IsGlobalEditorConfigWithSection(SourceText sourceText)
    {
        var isGlobal = false;
        var hasSection = false;
        ForEachLine(sourceText, (line, _) =>
        {
            SkipWhiteSpace(ref line);
            if (line.Length == 0)
            {
                return true;
            }

            if (IsGlobalConfigEntry(line))
            {
                isGlobal = true;
                return true;
            }

            if (IsSectionStart(line))
            {
                hasSection = true;
                return false;
            }

            return true;
        });

        return isGlobal && hasSection;

        static bool IsGlobalConfigEntry(ReadOnlySpan<char> span) => 
            IsMatch(ref span, "is_global") &&
            IsMatch(ref span, "=") &&
            IsMatch(ref span, "true");

        static bool IsSectionStart(ReadOnlySpan<char> span) => 
            IsMatch(ref span, "[");

        static bool IsMatch(ref ReadOnlySpan<char> span, string value)
        {
            SkipWhiteSpace(ref span);
            if (span.Length < value.Length)
            {
                return false;
            }

            if (span.Slice(0, value.Length).SequenceEqual(value.AsSpan()))
            {
                span = span.Slice(value.Length);
                return true;
            }

            return false;
        }

        static void SkipWhiteSpace(ref ReadOnlySpan<char> span)
        {
            while (span.Length > 0 && char.IsWhiteSpace(span[0]))
            {
                span = span.Slice(1);
            }
        }
    }

    internal static void ForEachLine(SourceText sourceText, SourceTextLineFunc func)
    {
        var pool = ArrayPool<char>.Shared;
        var buffer = pool.Rent(256);
        int sourceIndex = 0;

        if (sourceText.Length == 0)
        {
            return;
        }

        Span<char> line;
        Span<char> newLine;
        do
        {
            var (newLineIndex, newLineLength) = ReadNextLine();
            line = buffer.AsSpan(0, newLineIndex);
            newLine = buffer.AsSpan(newLineIndex, newLineLength);
            if (!func(line, newLine))
            {
                return;
            }

            sourceIndex += newLineIndex + newLineLength;
        } while (newLine.Length > 0);

        pool.Return(buffer);

        (int NewLineIndex, int NewLineLength) ReadNextLine()
        {
            while (true)
            {
                var count = Math.Min(sourceText.Length - sourceIndex, buffer.Length);
                sourceText.CopyTo(sourceIndex, buffer, 0, count);

                var (newLineIndex, newLineLength) = FindNewLineInBuffer(count);
                if (newLineIndex < 0)
                {
                    // Read the entire source text so there are no more new lines. The newline is the end 
                    // of the buffer with length 0.
                    if (count < buffer.Length)
                    {
                        return (count, 0);
                    }

                    var size = buffer.Length * 2;
                    pool.Return(buffer);
                    buffer = pool.Rent(size);
                }
                else
                {
                    return (newLineIndex, newLineLength);
                }
            }
        }

        (int Index, int Length) FindNewLineInBuffer(int count)
        {
            int index = 0;

            // The +1 is to account for the fact that newlines can be 2 characters long.
            while (index + 1 < count)
            {
                var span = buffer.AsSpan(index, count - index);
                var length = GetNewlineLength(span);
                if (length > 0)
                {
                    return (index, length);
                }

                index++;
            }

            return (-1, -1);
        }


        static int GetNewlineLength(Span<char> span) => 
            span[0] switch
            {
                '\r' => span.Length > 1 && span[1] == '\n' ? 2 : 1,
                '\n' => 1,
                '\u2028' => 1,
                '\u2029' => 1,
                (char)(0x85) => 1,
                _ => 0
            };
    }
}
