using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Basic.CompilerLogger.CommonUtil;

namespace Basic.CompilerLogger;

internal sealed class CompilerLogReader : IDisposable
{
    internal ZipArchive ZipArchive { get; set; }
    internal int CompilationCount { get; }

    public CompilerLogReader(Stream stream)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(MetadataFileName), ContentEncoding, leaveOpen: false);
        var line = reader.ReadLine();
        if (line is null)
            throw new InvalidOperationException();
        var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (items.Length != 2 || !int.TryParse(items[1], out var count))
            throw new InvalidOperationException();
        CompilationCount = count;
    }

    internal CompilationData ReadCompilation(int index)
    {
        if (index >= CompilationCount)
            throw new InvalidOperationException();

        using var reader = new StreamReader(ZipArchive.OpenEntryOrThrow(GetCompilerEntryName(index)), ContentEncoding, leaveOpen: false);

        // TODO: need to encode what type of compilation this is
        bool isCSharp = true;
        // TODO: need these availebl, have to parse command line first
        ParseOptions parseOptions = null!;

        var syntaxTreeList = new List<SyntaxTree>();
        var metadataReferenceList = new List<MetadataReference>();
        var analyzerBuilder = ImmutableArray.CreateBuilder<AnalyzerReference>();
        var additionalTextBuilder = ImmutableArray.CreateBuilder<AdditionalText>();

        var done = false;
        while (!done && reader.ReadLine() is string line)
        {
            switch (line[0])
            {
                case 'm':
                    ParseMetadataReference(line);
                    break;
                case 'a':
                    ParseAnalyzer(line);
                    break;
                case 's':
                    ParseSyntaxTree(line);
                    break;
                case 't':
                    ParseAdditionalText(line);
                    break;
                case '#':
                    done = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized line: {line}");
            }
        }

        var args = new List<string>();

        // Got this far
        throw null!;

        void ParseMetadataReference(string line)
        {
            var items = line.Split(':');
            if (items.Length == 5 &&
                Guid.TryParse(items[1], out var mvid) &&
                int.TryParse(items[2], out var kind))
            {
                var reference = GetMetadataReference(mvid);
                if (items[3] == "1")
                    reference = reference.WithEmbedInteropTypes(true);

                if (!string.IsNullOrEmpty(items[4]))
                {
                    var aliases = items[4].Split(',');
                    reference = reference.WithAliases(aliases);
                }

                metadataReferenceList.Add(reference);
            }

            throw new InvalidOperationException();
        }

        void ParseSyntaxTree(string line)
        {
            var items = line.Split(':');
            if (items.Length == 3)
            {
                var sourceText = GetSourceText(items[1]);
                SyntaxTree syntaxTree;
                if (isCSharp)
                    syntaxTree = CSharpSyntaxTree.ParseText(sourceText, (CSharpParseOptions)parseOptions, items[2]);
                else
                    syntaxTree = VisualBasicSyntaxTree.ParseText(sourceText, (VisualBasicParseOptions)parseOptions, items[2]);

                syntaxTreeList.Add(syntaxTree);
            }

            throw new InvalidOperationException();
        }

        void ParseAdditionalText(string line)
        {
            var items = line.Split(':');
            if (items.Length == 3)
            {
                var sourceText = GetSourceText(items[1]);
                var text = new BasicAdditionalTextFile(items[2], sourceText);
                additionalTextBuilder.Add(text);
            }

            throw new InvalidOperationException();
        }

        void ParseAnalyzer(string line)
        {
            // TODO: figure out how we want to represent this
        }
    }

    internal MetadataReference GetMetadataReference(Guid mvid)
    {
        // cache
        throw null!;
    }

    internal Stream GetContentStream(string contentFileName)
    {
        // cache
        throw null!;
    }

    internal SourceText GetSourceText(string contentFileName)
    {
        var stream = GetContentStream(contentFileName);

        // TODO: need to expose the real API for how the compiler reads source files. 
        // move this comment to the rehydration code when we write it.
        //
        // TODO: need to use the hash algorithm from the command line arguments. Burn it
        // into this line
        return SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
    }

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
