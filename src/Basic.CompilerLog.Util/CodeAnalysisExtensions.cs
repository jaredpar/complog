using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

public static class CodeAnalysisExtensions
{
    internal static readonly EmitOptions DefaultEmitOptions = new EmitOptions(
        metadataOnly: false,
        debugInformationFormat: DebugInformationFormat.PortablePdb);

    public static EmitOptions WithEmitFlags(this EmitOptions emitOptions, EmitFlags emitFlags)
    {
        emitFlags.CheckEmitFlags();
        if ((emitFlags & EmitFlags.MetadataOnly) != 0)
        {
            return emitOptions.WithEmitMetadataOnly(true);
        }

        return emitOptions;
    }

    public static EmitMemoryResult EmitToMemory(
        this Compilation compilation,
        EmitFlags emitFlags = EmitFlags.Default,
        Stream? win32ResourceStream = null,
        IEnumerable<ResourceDescription>? manifestResources = null,
        EmitOptions? emitOptions = null,
        IMethodSymbol? debugEntryPoint = null,
        Stream? sourceLinkStream = null,
        IEnumerable<EmbeddedText>? embeddedTexts = null,
        CancellationToken cancellationToken = default)
    {
        emitFlags.CheckEmitFlags();
        emitOptions ??= DefaultEmitOptions;
        MemoryStream assemblyStream = new MemoryStream();
        MemoryStream? pdbStream = null;
        MemoryStream? xmlStream = null;
        MemoryStream? metadataStream = null;

        if ((emitFlags & EmitFlags.IncludePdbStream) != 0 && emitOptions.DebugInformationFormat != DebugInformationFormat.Embedded)
        {
            pdbStream = new MemoryStream();
        }

        if ((emitFlags & EmitFlags.IncludeXmlStream) != 0)
        {
            xmlStream = new MemoryStream();
        }

        if ((emitFlags & EmitFlags.IncludeMetadataStream) != 0)
        {
            metadataStream = new MemoryStream();
        }

        emitOptions = emitOptions.WithEmitFlags(emitFlags);
        var result = compilation.Emit(
            assemblyStream,
            pdbStream,
            xmlStream,
            win32ResourceStream,
            manifestResources,
            emitOptions,
            debugEntryPoint: debugEntryPoint,
            sourceLinkStream,
            embeddedTexts,
            metadataPEStream: metadataStream,
            cancellationToken: cancellationToken);
        return new EmitMemoryResult(
            result.Success,
            assemblyStream,
            pdbStream,
            xmlStream,
            metadataStream,
            result.Diagnostics);
    }
}