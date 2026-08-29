using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Basic.CompilerLog.Util;

/// <summary>
/// A <see cref="PortableExecutableReference"/> that retains the PE image(s) it was created
/// from. The references materialized by <see cref="CompilerLogReader"/> are stream backed:
/// their file path is just the original file name and typically does not exist on the
/// current machine. Retaining the images is what lets a workspace loaded from a compiler
/// log be serialized back into a compiler log.
/// </summary>
internal sealed class BasicMetadataReference : PortableExecutableReference
{
    private readonly Microsoft.CodeAnalysis.Metadata _metadata;

    /// <summary>
    /// The MVID and PE image of the assembly followed by any netmodules it includes. For a
    /// reference of kind <see cref="MetadataImageKind.Module"/> this is the single module image.
    /// </summary>
    internal ImmutableArray<(Guid Mvid, byte[] Image)> Modules { get; }

    internal Guid Mvid => Modules[0].Mvid;
    internal byte[] ImageBytes => Modules[0].Image;

    private BasicMetadataReference(
        ImmutableArray<(Guid Mvid, byte[] Image)> modules,
        Microsoft.CodeAnalysis.Metadata metadata,
        MetadataReferenceProperties properties,
        string? filePath)
        : base(properties, filePath)
    {
        Modules = modules;
        _metadata = metadata;
    }

    internal static BasicMetadataReference Create(
        ImmutableArray<(Guid Mvid, byte[] Image)> modules,
        MetadataReferenceProperties properties,
        string? filePath)
    {
        // The metadata kind must match the reference kind: the compiler casts the metadata of a
        // module reference to ModuleMetadata.
        Microsoft.CodeAnalysis.Metadata metadata = properties.Kind == MetadataImageKind.Module
            ? ModuleMetadata.CreateFromImage(ImmutableArray.Create(modules[0].Image))
            : AssemblyMetadata.Create(modules
                .Select(x => ModuleMetadata.CreateFromImage(ImmutableArray.Create(x.Image)))
                .ToImmutableArray());
        return new(modules, metadata, properties, filePath);
    }

    protected override DocumentationProvider CreateDocumentationProvider() => DocumentationProvider.Default;

    protected override Microsoft.CodeAnalysis.Metadata GetMetadataImpl() => _metadata switch
    {
        AssemblyMetadata assemblyMetadata => assemblyMetadata.Copy(),
        _ => ((ModuleMetadata)_metadata).Copy(),
    };

    protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties) =>
        new BasicMetadataReference(Modules, _metadata, properties, FilePath);
}
