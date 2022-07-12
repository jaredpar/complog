using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLogger;

internal sealed class CompilerLogBuilder : IDisposable
{
    private readonly Dictionary<Guid, string> _mvidToRefNameMap = new();
    private readonly Dictionary<string, Guid> _refPathToMvidMap = new(StringComparer.Ordinal);
    private int _compilationCount;

    internal ZipArchive ZipArchive { get; set;  }
    internal List<string> Diagnostics { get; set; }

    internal CompilerLogBuilder(Stream stream, List<string> diagnostics)
    {
        ZipArchive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Diagnostics = diagnostics;
    }

    internal void Add(CompilerInvocation invocation)
    {
        var memoryStream = new MemoryStream();
        using var compilerWriter = new StreamWriter(memoryStream, Encoding.UTF8, leaveOpen: true);

        AddReferences(compilerWriter, invocation.CommandLineArguments);

        compilerWriter.Flush();
        var entry = ZipArchive.CreateEntry($"compilations/{_compilationCount}.txt", CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        memoryStream.Position = 0;
        memoryStream.CopyTo(entryStream);
        entryStream.Close();
        _compilationCount++;
    }

    private void AddReferences(StreamWriter compilationWriter, CommandLineArguments args)
    {
        foreach (var reference in args.MetadataReferences)
        {
            var filePath = reference.Reference;
            if (_refPathToMvidMap.TryGetValue(filePath, out var mvid))
            {
                Write(mvid);
                continue;
            }

            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new PEReader(file);
            var mdReader = reader.GetMetadataReader();
            GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
            mvid = mdReader.GetGuid(handle);
            Write(mvid);

            if (_mvidToRefNameMap.TryGetValue(mvid, out var name))
            {
                _refPathToMvidMap[filePath] = mvid;
            }

            var entry = ZipArchive.CreateEntry($"ref/{mvid:N}", CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            file.CopyTo(entryStream);

            _mvidToRefNameMap[mvid] = Path.GetFileName(filePath);
            _refPathToMvidMap[filePath] = mvid;

            void Write(Guid mvid)
            {
                compilationWriter.Write($"m:{mvid}:");
                compilationWriter.Write((int)reference.Properties.Kind);
                compilationWriter.Write(":");
                compilationWriter.Write(reference.Properties.EmbedInteropTypes);
                compilationWriter.Write(":");

                var any = false;
                foreach (var alias in reference.Properties.Aliases)
                {
                    if (any)
                        compilationWriter.Write(",");
                    compilationWriter.Write(alias);
                    any = true;
                }
                compilationWriter.WriteLine();
            }
        }
    }

    public void Dispose()
    {
        ZipArchive.Dispose();
        ZipArchive = null!;
    }
}
