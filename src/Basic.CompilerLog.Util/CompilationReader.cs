using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

public sealed class CompilationReader : IDisposable
{
    internal CompilerLogReader Reader { get; }

    public int CompilationCount => Reader.CompilationCount;

    private CompilationReader(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public static CompilationReader Create(Stream stream, bool leaveOpen = false) => new (new CompilerLogReader(stream, leaveOpen));

    public static CompilationReader Create(string filePath)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new(new CompilerLogReader(stream, leaveOpen: false));
    }

    public void Dispose()
    {
        Reader.Dispose();
    }

    public CompilerCall ReadCompilerCall(int index) =>
        Reader.ReadCompilerCall(index);

    public List<CompilerCall> ReadCompilerCalls(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        for (int i = 0; i < CompilationCount; i++)
        {
            var compilerCall = ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                list.Add(compilerCall);
            }
        }

        return list;
    }

    public List<CompilationData> ReadCompilationDatas(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        var list = new List<CompilationData>();
        for (int i = 0; i < CompilationCount; i++)
        {
            var compilerCall = ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                list.Add(ReadCompilationData(i));
            }
        }

        return list;
    }

    internal CompilationData ReadCompilationData(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        var (compilerCall, rawCompilerData) = Reader.ReadRawCompilationData(index);

        var sourceTextList = new List<(SourceText SourceText, string Path)>();
        var analyzerConfigList = new List<(SourceText SourceText, string Path)>();
        var metadataReferenceList = new List<MetadataReference>();
        var additionalTextList = new List<AdditionalText>();

        foreach (var tuple in rawCompilerData.Contents)
        {
            switch (tuple.Kind)
            {
                case RawContentKind.SourceText:
                    sourceTextList.Add((Reader.GetSourceText(tuple.ContentHash, tuple.HashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.AnalyzerConfig:
                    analyzerConfigList.Add((Reader.GetSourceText(tuple.ContentHash, tuple.HashAlgorithm), tuple.FilePath));
                    break;
                case RawContentKind.AdditionalText:
                    additionalTextList.Add(new BasicAdditionalTextFile(
                        tuple.FilePath,
                        Reader.GetSourceText(tuple.ContentHash, tuple.HashAlgorithm)));
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        return compilerCall.IsCSharp
            ? CreateCSharp()
            : CreateVisualBasic();

        BasicAssemblyLoadContext CreateAssemblyLoadContext()
        {
            var loadContext = new BasicAssemblyLoadContext(compilerCall.ProjectFilePath);

            foreach (var mvid in rawCompilerData.Analyzers)
            {
                var analyzerBytes = Reader.GetAssemblyBytes(mvid);
                loadContext.LoadFromStream(new MemoryStream(analyzerBytes.ToArray()));
            }

            return loadContext;
        }

        (SyntaxTreeOptionsProvider, AnalyzerConfigOptionsProvider) CreateOptionsProviders(IEnumerable<SyntaxTree> syntaxTrees, IEnumerable<AdditionalText> additionalTexts)
        {
            AnalyzerConfigOptionsResult globalConfigOptions = default;
            AnalyzerConfigSet? analyzerConfigSet = null;
            var resultList = new List<(object, AnalyzerConfigOptionsResult)>();

            if (analyzerConfigList.Count > 0)
            {
                var list = new List<AnalyzerConfig>();
                foreach (var tuple in analyzerConfigList)
                {
                    list.Add(AnalyzerConfig.Parse(tuple.SourceText, tuple.Path));
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

        CSharpCompilationData CreateCSharp()
        {
            var csharpArgs = (CSharpCommandLineArguments)rawCompilerData.Arguments;
            var parseOptions = csharpArgs.ParseOptions;

            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = CSharpSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);
            var compilation = CSharpCompilation.Create(
                rawCompilerData.Arguments.CompilationName,
                syntaxTrees,
                metadataReferenceList,
                csharpArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                csharpArgs,
                additionalTextList.ToImmutableArray(),
                CreateAssemblyLoadContext(),
                analyzerProvider);
        }

        VisualBasicCompilationData CreateVisualBasic()
        {
            var basicArgs = (VisualBasicCommandLineArguments)rawCompilerData.Arguments;
            var parseOptions = basicArgs.ParseOptions;
            var syntaxTrees = new SyntaxTree[sourceTextList.Count];
            Parallel.For(
                0,
                sourceTextList.Count,
                i =>
                {
                    var t = sourceTextList[i];
                    syntaxTrees[i] = VisualBasicSyntaxTree.ParseText(t.SourceText, parseOptions, t.Path);
                });

            var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(syntaxTrees, additionalTextList);

            var compilation = VisualBasicCompilation.Create(
                rawCompilerData.Arguments.CompilationName,
                syntaxTrees,
                metadataReferenceList,
                basicArgs.CompilationOptions.WithSyntaxTreeOptionsProvider(syntaxProvider));

            return new VisualBasicCompilationData(
                compilerCall,
                compilation,
                basicArgs,
                additionalTextList.ToImmutableArray(),
                CreateAssemblyLoadContext(),
                analyzerProvider);
        }
    }
}
