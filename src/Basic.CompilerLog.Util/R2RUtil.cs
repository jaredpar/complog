using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Utilities for detecting and stripping ReadyToRun (R2R) native code from managed assemblies.
/// </summary>
internal static class R2RUtil
{
    /// <summary>
    /// Returns true if the provided assembly bytes represent a ReadyToRun image. A ReadyToRun
    /// assembly contains both IL bytecode and pre-compiled native code, and its COR header
    /// has a non-zero ManagedNativeHeader directory entry.
    /// </summary>
    internal static bool IsReadyToRun(byte[] assemblyBytes)
    {
        using var stream = assemblyBytes.AsSimpleMemoryStream(writable: false);
        using var peReader = new PEReader(stream);
        return IsReadyToRun(peReader);
    }

    /// <summary>
    /// Returns true if the provided <see cref="PEReader"/> represents a ReadyToRun image.
    /// ECMA-335 §II.25.3.3 defines the COR header ManagedNativeHeader field as zero for all
    /// managed PE files; the ReadyToRun format places its READYTORUN_HEADER structure at that RVA
    /// (<see href="https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-format.md"/>).
    /// A non-zero RVA is therefore the authoritative signal that the image contains R2R native code.
    /// </summary>
    internal static bool IsReadyToRun(PEReader peReader) =>
        peReader.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.RelativeVirtualAddress != 0;

    /// <summary>
    /// Returns true if the assembly should be stripped to IL-only before use. Stripping is needed
    /// only when the assembly contains R2R native code targeting a different architecture than the
    /// current process: on the matching architecture the runtime can execute the native code directly,
    /// while on a mismatched architecture the R2R sections are unusable and the assembly must be
    /// reduced to IL-only so the JIT can handle it.
    /// </summary>
    internal static bool NeedsStripping(byte[] assemblyBytes)
    {
        using var stream = assemblyBytes.AsSimpleMemoryStream(writable: false);
        using var peReader = new PEReader(stream);
        return NeedsStripping(peReader);
    }

    /// <summary>
    /// Returns true if the assembly represented by <paramref name="peReader"/> should be stripped
    /// to IL-only before use. See <see cref="NeedsStripping(byte[])"/> for details.
    /// </summary>
    internal static bool NeedsStripping(PEReader peReader)
    {
        if (!IsReadyToRun(peReader))
        {
            return false;
        }

        var machine = peReader.PEHeaders.CoffHeader.Machine;
        return !IsCurrentArchitecture(machine);
    }

    private static bool IsCurrentArchitecture(Machine machine) =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => machine == Machine.Amd64 || IsCurrentOsNativeR2R(machine),
            Architecture.X86 => machine == Machine.I386,
            Architecture.Arm64 => machine == Machine.Arm64 || IsCurrentOsNativeR2R(machine),
            Architecture.Arm => machine is Machine.Arm or Machine.ArmThumb2,
            _ => false,
        };

    /// <summary>
    /// Returns true when <paramref name="machine"/> matches the sentinel value that crossgen2 writes
    /// into the COFF Machine field for the current OS on non-Windows platforms. On Linux every R2R
    /// image carries <c>0xFD1D</c>, on macOS <c>0x4644</c>, and on FreeBSD <c>0xADC4</c>, regardless
    /// of CPU architecture. Because the value encodes only the target OS, the only safe inference is
    /// that the image was built for this host's CPU architecture—an assumption that holds because the
    /// .NET SDK always produces R2R code for the current host platform.
    /// </summary>
    private static bool IsCurrentOsNativeR2R(Machine machine)
    {
        var m = (ushort)machine;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return m == 0xFD1D;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return m == 0x4644;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
            return m == 0xADC4;
        return false;
    }

    /// <summary>
    /// Strips ReadyToRun native code from an assembly, producing an IL-only version with all
    /// managed metadata, IL method bodies, resources, and signatures preserved.
    /// </summary>
    internal static byte[] StripReadyToRun(byte[] assemblyBytes)
    {
        using var inputStream = assemblyBytes.AsSimpleMemoryStream(writable: false);
        using var peReader = new PEReader(inputStream);
        return StripReadyToRun(peReader);
    }

    /// <summary>
    /// Strips ReadyToRun native code from the assembly represented by <paramref name="peReader"/>,
    /// producing an IL-only version. See <see cref="StripReadyToRun(byte[])"/> for details.
    /// </summary>
    internal static byte[] StripReadyToRun(PEReader peReader)
    {
        if (!peReader.HasMetadata)
        {
            throw new InvalidOperationException("Input does not contain managed metadata");
        }

        var metadataReader = peReader.GetMetadataReader();

        var metadataBuilder = new MetadataBuilder();
        var ilStream = new BlobBuilder();
        var methodBodyEncoder = new MethodBodyStreamEncoder(ilStream);
        var mappedFieldData = new BlobBuilder();
        var managedResources = new BlobBuilder();

        new MetadataCopier(metadataReader, metadataBuilder, peReader, methodBodyEncoder, mappedFieldData, managedResources).CopyAll();

        var metadataRootBuilder = new MetadataRootBuilder(metadataBuilder);
        var sourcePEHeader = peReader.PEHeaders.PEHeader!;
        var sourceCoffHeader = peReader.PEHeaders.CoffHeader;
        var header = new PEHeaderBuilder(
            // R2R images are architecture-specific; the stripped output must be IL-only (AnyCPU).
            machine: Machine.Unknown,
            sectionAlignment: sourcePEHeader.SectionAlignment,
            fileAlignment: sourcePEHeader.FileAlignment,
            imageBase: sourcePEHeader.ImageBase,
            majorLinkerVersion: sourcePEHeader.MajorLinkerVersion,
            minorLinkerVersion: sourcePEHeader.MinorLinkerVersion,
            majorOperatingSystemVersion: sourcePEHeader.MajorOperatingSystemVersion,
            minorOperatingSystemVersion: sourcePEHeader.MinorOperatingSystemVersion,
            majorImageVersion: sourcePEHeader.MajorImageVersion,
            minorImageVersion: sourcePEHeader.MinorImageVersion,
            majorSubsystemVersion: sourcePEHeader.MajorSubsystemVersion,
            minorSubsystemVersion: sourcePEHeader.MinorSubsystemVersion,
            subsystem: sourcePEHeader.Subsystem,
            dllCharacteristics: sourcePEHeader.DllCharacteristics,
            imageCharacteristics: sourceCoffHeader.Characteristics,
            sizeOfStackReserve: sourcePEHeader.SizeOfStackReserve,
            sizeOfStackCommit: sourcePEHeader.SizeOfStackCommit,
            sizeOfHeapReserve: sourcePEHeader.SizeOfHeapReserve,
            sizeOfHeapCommit: sourcePEHeader.SizeOfHeapCommit);

        var entryPoint = default(MethodDefinitionHandle);
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader is not null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
        {
            var entityHandle = MetadataTokens.EntityHandle(corHeader.EntryPointTokenOrRelativeVirtualAddress);
            if (entityHandle.Kind == HandleKind.MethodDefinition)
            {
                entryPoint = (MethodDefinitionHandle)entityHandle;
            }
        }

        var managedPEBuilder = new ManagedPEBuilder(
            header,
            metadataRootBuilder,
            ilStream,
            mappedFieldData,
            managedResources,
            nativeResources: null,
            debugDirectoryBuilder: null,
            // strongNameSignatureSize: 0 — we keep the original public key in the assembly
            // definition above, but we cannot re-sign without the private key.  Setting this to 0
            // omits the signature placeholder section from the PE entirely.  The .NET (Core)
            // runtime does not enforce strong-name signature verification, so the assembly loads
            // normally and its public-key identity (used for InternalsVisibleTo, etc.) is intact.
            strongNameSignatureSize: 0,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: _ => new BlobContentId(Guid.NewGuid(), 0x04034B50));

        var outputBuilder = new BlobBuilder();
        managedPEBuilder.Serialize(outputBuilder);
        var outputStream = new MemoryStream();
        outputBuilder.WriteContentTo(outputStream);
        return outputStream.ToArray();
    }
}

/// <summary>
/// Copies all managed metadata from a ReadyToRun PE image into a new <see cref="MetadataBuilder"/>,
/// reconstructing IL method bodies and preserving all metadata tables, resources, and signatures
/// while discarding the native code sections.
/// </summary>
file sealed class MetadataCopier(
    MetadataReader reader,
    MetadataBuilder builder,
    PEReader peReader,
    MethodBodyStreamEncoder methodBodyEncoder,
    BlobBuilder mappedFieldData,
    BlobBuilder managedResources)
{
    public void CopyAll()
    {
        CopyStringHeap();
        CopyUserStringHeap();
        CopyModule();
        CopyAssembly();
        CopyAssemblyReferences();
        CopyModuleReferences();
        CopyTypeReferences();
        CopyTypeSpecifications();
        CopyStandaloneSignatures();
        CopyTypeDefinitions();
        CopyFieldDefinitions();
        CopyMethodDefinitions();
        CopyParameters();
        CopyInterfaceImplementations();
        CopyMemberReferences();
        CopyMethodSpecifications();
        CopyConstants();
        CopyCustomAttributes();
        CopyFieldMarshals();
        CopyDeclSecurities();
        CopyClassLayouts();
        CopyFieldLayouts();
        CopyFieldRvas();
        CopyImplMaps();
        CopyPropertyMaps();
        CopyProperties();
        CopyEventMaps();
        CopyEvents();
        CopyMethodSemantics();
        CopyMethodImpls();
        CopyGenericParameters();
        CopyGenericParamConstraints();
        CopyNestedClasses();
        CopyExportedTypes();
        CopyManifestResources();
    }

    private void CopyStringHeap()
    {
        var handle = default(StringHandle);
        while (true)
        {
            handle = reader.GetNextHandle(handle);
            if (handle.IsNil)
            {
                break;
            }

            builder.GetOrAddString(reader.GetString(handle));
        }
    }

    private void CopyUserStringHeap()
    {
        var heapSize = reader.GetHeapSize(HeapIndex.UserString);
        if (heapSize <= 1)
        {
            return;
        }

        var heapMetadataOffset = reader.GetHeapMetadataOffset(HeapIndex.UserString);
        var content = peReader.GetMetadata().GetContent();
        for (var i = 1; i < heapSize; i += GetLength(i) + GetLengthSize(i))
        {
            var length = GetLength(i);
            if (length > 0)
            {
                var handle = MetadataTokens.UserStringHandle(i);
                builder.GetOrAddUserString(reader.GetUserString(handle));
            }
        }

        int GetLength(int offset)
        {
            var b = content[heapMetadataOffset + offset];
            if ((b & 0x80) == 0) return b;
            if ((b & 0xC0) == 0x80) return ((b & 0x3F) << 8) | content[heapMetadataOffset + offset + 1];
            return ((b & 0x1F) << 24) | (content[heapMetadataOffset + offset + 1] << 16) | (content[heapMetadataOffset + offset + 2] << 8) | content[heapMetadataOffset + offset + 3];
        }

        int GetLengthSize(int offset)
        {
            var b = content[heapMetadataOffset + offset];
            if ((b & 0x80) == 0) return 1;
            if ((b & 0xC0) == 0x80) return 2;
            return 4;
        }
    }

    private void CopyModule()
    {
        var module = reader.GetModuleDefinition();
        builder.AddModule(
            module.Generation,
            builder.GetOrAddString(reader.GetString(module.Name)),
            builder.GetOrAddGuid(reader.GetGuid(module.Mvid)),
            default,
            default);
    }

    private void CopyAssembly()
    {
        var assembly = reader.GetAssemblyDefinition();
        // Preserve the public key so the stripped assembly keeps its original identity.
        // InternalsVisibleTo grants, type-equivalence checks, and assembly-identity comparisons
        // all depend on the public key token.  On .NET (Core), strong-name verification is not
        // enforced, so an assembly that carries a public key but has no valid signature loads
        // without error.  Re-signing is impossible here because the private key is not available.
        var publicKey = assembly.PublicKey.IsNil
            ? default
            : builder.GetOrAddBlob(reader.GetBlobBytes(assembly.PublicKey));
        builder.AddAssembly(
            builder.GetOrAddString(reader.GetString(assembly.Name)),
            assembly.Version,
            builder.GetOrAddString(reader.GetString(assembly.Culture)),
            publicKey,
            assembly.Flags,
            assembly.HashAlgorithm);
    }

    private void CopyAssemblyReferences()
    {
        foreach (var handle in reader.AssemblyReferences)
        {
            var assemblyRef = reader.GetAssemblyReference(handle);
            var publicKeyOrToken = assemblyRef.PublicKeyOrToken.IsNil
                ? default
                : builder.GetOrAddBlob(reader.GetBlobBytes(assemblyRef.PublicKeyOrToken));
            builder.AddAssemblyReference(
                builder.GetOrAddString(reader.GetString(assemblyRef.Name)),
                assemblyRef.Version,
                builder.GetOrAddString(reader.GetString(assemblyRef.Culture)),
                publicKeyOrToken,
                assemblyRef.Flags,
                default);
        }
    }

    private void CopyModuleReferences()
    {
        var count = reader.GetTableRowCount(TableIndex.ModuleRef);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.ModuleReferenceHandle(i);
            var moduleRef = reader.GetModuleReference(handle);
            builder.AddModuleReference(builder.GetOrAddString(reader.GetString(moduleRef.Name)));
        }
    }

    private void CopyTypeReferences()
    {
        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            builder.AddTypeReference(
                typeRef.ResolutionScope,
                builder.GetOrAddString(reader.GetString(typeRef.Namespace)),
                builder.GetOrAddString(reader.GetString(typeRef.Name)));
        }
    }

    private void CopyTypeSpecifications()
    {
        var count = reader.GetTableRowCount(TableIndex.TypeSpec);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.TypeSpecificationHandle(i);
            var typeSpec = reader.GetTypeSpecification(handle);
            builder.AddTypeSpecification(builder.GetOrAddBlob(reader.GetBlobBytes(typeSpec.Signature)));
        }
    }

    private void CopyStandaloneSignatures()
    {
        var count = reader.GetTableRowCount(TableIndex.StandAloneSig);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.StandaloneSignatureHandle(i);
            var sig = reader.GetStandaloneSignature(handle);
            builder.AddStandaloneSignature(builder.GetOrAddBlob(reader.GetBlobBytes(sig.Signature)));
        }
    }

    private void CopyTypeDefinitions()
    {
        var fieldIndex = 1;
        var methodIndex = 1;
        foreach (var handle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            builder.AddTypeDefinition(
                typeDef.Attributes,
                builder.GetOrAddString(reader.GetString(typeDef.Namespace)),
                builder.GetOrAddString(reader.GetString(typeDef.Name)),
                typeDef.BaseType,
                MetadataTokens.FieldDefinitionHandle(fieldIndex),
                MetadataTokens.MethodDefinitionHandle(methodIndex));
            fieldIndex += typeDef.GetFields().Count;
            methodIndex += typeDef.GetMethods().Count;
        }
    }

    private void CopyFieldDefinitions()
    {
        foreach (var handle in reader.FieldDefinitions)
        {
            var field = reader.GetFieldDefinition(handle);
            builder.AddFieldDefinition(
                field.Attributes,
                builder.GetOrAddString(reader.GetString(field.Name)),
                builder.GetOrAddBlob(reader.GetBlobBytes(field.Signature)));
        }
    }

    private void CopyMethodDefinitions()
    {
        var paramIndex = 1;
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            var bodyOffset = CopyMethodBody(method);
            builder.AddMethodDefinition(
                method.Attributes,
                method.ImplAttributes,
                builder.GetOrAddString(reader.GetString(method.Name)),
                builder.GetOrAddBlob(reader.GetBlobBytes(method.Signature)),
                bodyOffset,
                MetadataTokens.ParameterHandle(paramIndex));
            paramIndex += method.GetParameters().Count;
        }
    }

    private int CopyMethodBody(MethodDefinition method)
    {
        var rva = method.RelativeVirtualAddress;
        if (rva == 0)
        {
            return -1;
        }

        var methodBody = peReader.GetMethodBody(rva);
        var ilBytes = methodBody.GetILBytes();
        if (ilBytes is null || ilBytes.Length == 0)
        {
            return -1;
        }

        var localSig = methodBody.LocalSignature.IsNil ? default : methodBody.LocalSignature;
        var attrs = methodBody.LocalVariablesInitialized ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None;
        var encodedBody = methodBodyEncoder.AddMethodBody(
            codeSize: ilBytes.Length,
            maxStack: methodBody.MaxStack,
            exceptionRegionCount: methodBody.ExceptionRegions.Length,
            hasSmallExceptionRegions: false,
            localVariablesSignature: localSig,
            attributes: attrs);

        new BlobWriter(encodedBody.Instructions).WriteBytes(ilBytes);

        foreach (var region in methodBody.ExceptionRegions)
        {
            switch (region.Kind)
            {
                case ExceptionRegionKind.Catch:
                    encodedBody.ExceptionRegions.AddCatch(region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength, region.CatchType);
                    break;
                case ExceptionRegionKind.Filter:
                    encodedBody.ExceptionRegions.AddFilter(region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength, region.FilterOffset);
                    break;
                case ExceptionRegionKind.Finally:
                    encodedBody.ExceptionRegions.AddFinally(region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength);
                    break;
                case ExceptionRegionKind.Fault:
                    encodedBody.ExceptionRegions.AddFault(region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength);
                    break;
            }
        }

        return encodedBody.Offset;
    }

    private void CopyParameters()
    {
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            foreach (var paramHandle in reader.GetMethodDefinition(methodHandle).GetParameters())
            {
                var param = reader.GetParameter(paramHandle);
                builder.AddParameter(
                    param.Attributes,
                    param.Name.IsNil ? default : builder.GetOrAddString(reader.GetString(param.Name)),
                    param.SequenceNumber);
            }
        }
    }

    private void CopyInterfaceImplementations()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var implHandle in reader.GetTypeDefinition(typeHandle).GetInterfaceImplementations())
            {
                var impl = reader.GetInterfaceImplementation(implHandle);
                builder.AddInterfaceImplementation(typeHandle, impl.Interface);
            }
        }
    }

    private void CopyMemberReferences()
    {
        foreach (var handle in reader.MemberReferences)
        {
            var memberRef = reader.GetMemberReference(handle);
            builder.AddMemberReference(
                memberRef.Parent,
                builder.GetOrAddString(reader.GetString(memberRef.Name)),
                builder.GetOrAddBlob(reader.GetBlobBytes(memberRef.Signature)));
        }
    }

    private void CopyMethodSpecifications()
    {
        var count = reader.GetTableRowCount(TableIndex.MethodSpec);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.MethodSpecificationHandle(i);
            var methodSpec = reader.GetMethodSpecification(handle);
            builder.AddMethodSpecification(
                methodSpec.Method,
                builder.GetOrAddBlob(reader.GetBlobBytes(methodSpec.Signature)));
        }
    }

    private void CopyConstants()
    {
        var count = reader.GetTableRowCount(TableIndex.Constant);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.ConstantHandle(i);
            var constant = reader.GetConstant(handle);
            builder.AddConstant(constant.Parent, reader.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode));
        }
    }

    private void CopyCustomAttributes()
    {
        var count = reader.GetTableRowCount(TableIndex.CustomAttribute);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.CustomAttributeHandle(i);
            var attr = reader.GetCustomAttribute(handle);
            builder.AddCustomAttribute(
                attr.Parent,
                attr.Constructor,
                builder.GetOrAddBlob(reader.GetBlobBytes(attr.Value)));
        }
    }

    private void CopyFieldMarshals()
    {
        foreach (var fieldHandle in reader.FieldDefinitions)
        {
            var marshalDescriptor = reader.GetFieldDefinition(fieldHandle).GetMarshallingDescriptor();
            if (!marshalDescriptor.IsNil)
            {
                builder.AddMarshallingDescriptor(fieldHandle, builder.GetOrAddBlob(reader.GetBlobBytes(marshalDescriptor)));
            }
        }

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            foreach (var paramHandle in reader.GetMethodDefinition(methodHandle).GetParameters())
            {
                var marshalDescriptor = reader.GetParameter(paramHandle).GetMarshallingDescriptor();
                if (!marshalDescriptor.IsNil)
                {
                    builder.AddMarshallingDescriptor(paramHandle, builder.GetOrAddBlob(reader.GetBlobBytes(marshalDescriptor)));
                }
            }
        }
    }

    private void CopyDeclSecurities()
    {
        var count = reader.GetTableRowCount(TableIndex.DeclSecurity);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.DeclarativeSecurityAttributeHandle(i);
            var security = reader.GetDeclarativeSecurityAttribute(handle);
            builder.AddDeclarativeSecurityAttribute(
                security.Parent,
                security.Action,
                builder.GetOrAddBlob(reader.GetBlobBytes(security.PermissionSet)));
        }
    }

    private void CopyClassLayouts()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var layout = reader.GetTypeDefinition(typeHandle).GetLayout();
            if (!layout.IsDefault)
            {
                builder.AddTypeLayout(typeHandle, (ushort)layout.PackingSize, (uint)layout.Size);
            }
        }
    }

    private void CopyFieldLayouts()
    {
        foreach (var fieldHandle in reader.FieldDefinitions)
        {
            var offset = reader.GetFieldDefinition(fieldHandle).GetOffset();
            if (offset >= 0)
            {
                builder.AddFieldLayout(fieldHandle, offset);
            }
        }
    }

    private void CopyFieldRvas()
    {
        foreach (var fieldHandle in reader.FieldDefinitions)
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var rva = field.GetRelativeVirtualAddress();
            if (rva != 0)
            {
                var size = GetMappedFieldDataSize(field);
                if (size > 0)
                {
                    var content = peReader.GetSectionData(rva).GetContent(0, size);
                    var offset = mappedFieldData.Count;
                    mappedFieldData.WriteBytes(content);
                    builder.AddFieldRelativeVirtualAddress(fieldHandle, offset);
                }
            }
        }
    }

    private int GetMappedFieldDataSize(FieldDefinition field)
    {
        var sig = reader.GetBlobBytes(field.Signature);
        if (sig.Length >= 2 && sig[0] == 6)
        {
            var pos = 1;
            switch (sig[pos])
            {
                case 2: return 1;   // bool
                case 3: return 2;   // char
                case 4: return 1;   // sbyte
                case 5: return 1;   // byte
                case 6: return 2;   // short
                case 7: return 2;   // ushort
                case 8: return 4;   // int
                case 9: return 4;   // uint
                case 10: return 8;  // long
                case 11: return 8;  // ulong
                case 12: return 4;  // float
                case 13: return 8;  // double
                case 17: // ValueType
                    pos++;
                    var token = DecodeCompressedUInt(sig, ref pos);
                    var tag = token & 3;
                    var row = token >> 2;
                    if (tag == 0) // TypeDef
                    {
                        var layout = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row)).GetLayout();
                        if (!layout.IsDefault && layout.Size > 0)
                        {
                            return layout.Size;
                        }
                    }

                    return 0;
                default: return 0;
            }
        }

        return 0;
    }

    private static int DecodeCompressedUInt(byte[] data, ref int pos)
    {
        var b = data[pos];
        if ((b & 0x80) == 0) { pos++; return b; }
        if ((b & 0xC0) == 0x80) { var result = ((b & 0x3F) << 8) | data[pos + 1]; pos += 2; return result; }
        { var result = ((b & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]; pos += 4; return result; }
    }

    private void CopyImplMaps()
    {
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var import = reader.GetMethodDefinition(methodHandle).GetImport();
            if (!import.Module.IsNil)
            {
                builder.AddMethodImport(
                    methodHandle,
                    import.Attributes,
                    builder.GetOrAddString(reader.GetString(import.Name)),
                    import.Module);
            }
        }
    }

    private void CopyPropertyMaps()
    {
        var propertyIndex = 1;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var properties = reader.GetTypeDefinition(typeHandle).GetProperties();
            if (properties.Count > 0)
            {
                builder.AddPropertyMap(typeHandle, MetadataTokens.PropertyDefinitionHandle(propertyIndex));
                propertyIndex += properties.Count;
            }
        }
    }

    private void CopyProperties()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var propHandle in reader.GetTypeDefinition(typeHandle).GetProperties())
            {
                var prop = reader.GetPropertyDefinition(propHandle);
                builder.AddProperty(
                    prop.Attributes,
                    builder.GetOrAddString(reader.GetString(prop.Name)),
                    builder.GetOrAddBlob(reader.GetBlobBytes(prop.Signature)));
            }
        }
    }

    private void CopyEventMaps()
    {
        var eventIndex = 1;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var events = reader.GetTypeDefinition(typeHandle).GetEvents();
            if (events.Count > 0)
            {
                builder.AddEventMap(typeHandle, MetadataTokens.EventDefinitionHandle(eventIndex));
                eventIndex += events.Count;
            }
        }
    }

    private void CopyEvents()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var eventHandle in reader.GetTypeDefinition(typeHandle).GetEvents())
            {
                var evt = reader.GetEventDefinition(eventHandle);
                builder.AddEvent(
                    evt.Attributes,
                    builder.GetOrAddString(reader.GetString(evt.Name)),
                    evt.Type);
            }
        }
    }

    private void CopyMethodSemantics()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            foreach (var propHandle in typeDef.GetProperties())
            {
                var accessors = reader.GetPropertyDefinition(propHandle).GetAccessors();
                if (!accessors.Getter.IsNil) builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Getter, accessors.Getter);
                if (!accessors.Setter.IsNil) builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Setter, accessors.Setter);
                foreach (var other in accessors.Others) builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Other, other);
            }

            foreach (var eventHandle in typeDef.GetEvents())
            {
                var accessors = reader.GetEventDefinition(eventHandle).GetAccessors();
                if (!accessors.Adder.IsNil) builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Adder, accessors.Adder);
                if (!accessors.Remover.IsNil) builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Remover, accessors.Remover);
                if (!accessors.Raiser.IsNil) builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Raiser, accessors.Raiser);
                foreach (var other in accessors.Others) builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Other, other);
            }
        }
    }

    private void CopyMethodImpls()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var implHandle in reader.GetTypeDefinition(typeHandle).GetMethodImplementations())
            {
                var impl = reader.GetMethodImplementation(implHandle);
                builder.AddMethodImplementation(typeHandle, impl.MethodBody, impl.MethodDeclaration);
            }
        }
    }

    private void CopyGenericParameters()
    {
        // GenericParam table must be sorted by Owner (TypeOrMethodDef coded index).
        // Iterate the table directly by row to preserve the original sorted order
        // rather than grouping by type then method, which would break the sort.
        var count = reader.GetTableRowCount(TableIndex.GenericParam);
        for (var i = 1; i <= count; i++)
        {
            var gpHandle = MetadataTokens.GenericParameterHandle(i);
            var gp = reader.GetGenericParameter(gpHandle);
            builder.AddGenericParameter(
                gp.Parent,
                gp.Attributes,
                builder.GetOrAddString(reader.GetString(gp.Name)),
                gp.Index);
        }
    }

    private void CopyGenericParamConstraints()
    {
        var count = reader.GetTableRowCount(TableIndex.GenericParam);
        for (var i = 1; i <= count; i++)
        {
            var gpHandle = MetadataTokens.GenericParameterHandle(i);
            foreach (var constraintHandle in reader.GetGenericParameter(gpHandle).GetConstraints())
            {
                var constraint = reader.GetGenericParameterConstraint(constraintHandle);
                builder.AddGenericParameterConstraint(gpHandle, constraint.Type);
            }
        }
    }

    private void CopyNestedClasses()
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            var enclosingType = typeDef.GetDeclaringType();
            if (!enclosingType.IsNil)
            {
                builder.AddNestedType(typeHandle, enclosingType);
            }
        }
    }

    private void CopyExportedTypes()
    {
        foreach (var handle in reader.ExportedTypes)
        {
            var exportedType = reader.GetExportedType(handle);
            builder.AddExportedType(
                exportedType.Attributes,
                builder.GetOrAddString(reader.GetString(exportedType.Namespace)),
                builder.GetOrAddString(reader.GetString(exportedType.Name)),
                exportedType.Implementation,
                exportedType.GetTypeDefinitionId());
        }
    }

    private void CopyManifestResources()
    {
        foreach (var handle in reader.ManifestResources)
        {
            var resource = reader.GetManifestResource(handle);
            var offset = (uint)resource.Offset;
            if (resource.Implementation.IsNil)
            {
                var resourcesDir = peReader.PEHeaders.CorHeader!.ResourcesDirectory;
                var content = peReader.GetSectionData(resourcesDir.RelativeVirtualAddress).GetContent();
                var start = (int)resource.Offset;
                var length = content[start] | (content[start + 1] << 8) | (content[start + 2] << 16) | (content[start + 3] << 24);
                offset = (uint)managedResources.Count;
                managedResources.WriteInt32(length);
                managedResources.WriteBytes(content.AsSpan(start + 4, length).ToArray());
            }

            builder.AddManifestResource(
                resource.Attributes,
                builder.GetOrAddString(reader.GetString(resource.Name)),
                resource.Implementation,
                offset);
        }
    }
}
