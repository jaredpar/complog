using System.Collections.Immutable;
using Basic.CompilerLog.Util.Impl;
using MessagePack.Formatters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

public sealed class BinaryLogReader(List<CompilerCall> compilerCalls) : ICompilerCallReader
{
    private List<CompilerCall> _compilerCalls = compilerCalls;

    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        return _compilerCalls.Where(predicate).ToList();
    }

    // TODO: implement
    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null)
    {
        throw null!;
    }

    // TODO:
    //  - CompilationName isn't a perfect match for CompilerLog. Need to fix that.
    public static CompilationData Convert(CompilerCall compilerCall)
    {
        var args = compilerCall.ParseArguments();

        var referenceList = GetReferences();
        var sourceTextList = GetSourceTexts();
        var additionalTextList = GetAdditionalTexts();
        var emitData = GetEmitData();
        var basicAnalyzerHost = CreateAnalyzerHost();

        throw null!;

        CompilationData GetCSharp()
        {

            // TODO: need to figure this out
            AnalyzerConfigOptionsProvider optionsProvider = null!;

            var csharpOptions = (CSharpParseOptions)args.ParseOptions;
            var compilationOptions = (CSharpCompilationOptions)args.CompilationOptions;
            var compilation = CSharpCompilation.Create(
                args.CompilationName,
                RoslynUtil.ParseAllCSharp(sourceTextList, csharpOptions),
                referenceList,
                compilationOptions);

            return new CSharpCompilationData(
                compilerCall,
                compilation,
                csharpOptions,
                args.EmitOptions,
                emitData,
                additionalTextList,
                basicAnalyzerHost,
                optionsProvider);
        }

        /*


        new CSharpCompilationData(
            compilerCall,
            compilation,
            args.ParseOptions,
            args.EmitOptions,
            emitData,
            args.AdditionalFiles,
            BasicAnalyzerHost,
            configOaptionsProvider
        */

        // TODO: this is tough. Existing hosts are way to tied to CompilerLogReader. Have to break that apart.
        BasicAnalyzerHost CreateAnalyzerHost()
        {
            throw null!;
        }

        List<MetadataReference> GetReferences()
        {
            var list = new List<MetadataReference>(capacity: args.MetadataReferences.Length);
            foreach (var reference in args.MetadataReferences)
            {
                // TODO: should cache this
                var mdref = MetadataReference.CreateFromFile(reference.Reference, reference.Properties);
                list.Add(mdref);
            }
            return list;
        }

        List<(SourceText SourceText, string Path)> GetSourceTexts()
        {
            var list = new List<(SourceText, string)>(capacity: args.SourceFiles.Length);
            foreach (var sourceFile in args.SourceFiles)
            {
                var sourceText = RoslynUtil.GetSourceText(sourceFile.Path, args.ChecksumAlgorithm, canBeEmbedded: false);
                list.Add((sourceText, sourceFile.Path));
            }
            return list;
        }

        ImmutableArray<AdditionalText> GetAdditionalTexts()
        {
            var builder = ImmutableArray.CreateBuilder<AdditionalText>(args.AdditionalFiles.Length);
            foreach (var additionalFile in args.AdditionalFiles)
            {
                var sourceText = RoslynUtil.GetSourceText(additionalFile.Path, args.ChecksumAlgorithm, canBeEmbedded: false);
                var additionalText = new BasicAdditionalTextFile(
                    additionalFile.Path,
                    sourceText);
                builder.Add(additionalText);
            }
            return builder.MoveToImmutable();
        }

        EmitData GetEmitData()
        {
            // TODO: need to actually pass the right args here.
            return new EmitData(
                args.CompilationName,
                args.DocumentationPath,
                win32ResourceStream: null,
                sourceLinkStream: null,
                resources: Array.Empty<ResourceDescription>(),
                embeddedTexts: Array.Empty<EmbeddedText>());
        }
    }
}