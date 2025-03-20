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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Basic.CompilerLog.Util;

internal static class RoslynUtil
{
    // GUIDs specified in https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md#document-table-0x30
    internal static readonly Guid HashAlgorithmSha1 = unchecked(new Guid((int)0xff1816ec, (short)0xaa5e, 0x4d10, 0x87, 0xf7, 0x6f, 0x49, 0x63, 0x83, 0x34, 0x60));
    internal static readonly Guid HashAlgorithmSha256 = unchecked(new Guid((int)0x8829d00f, 0x11b8, 0x4213, 0x87, 0x8b, 0x77, 0x0e, 0x85, 0x97, 0xac, 0x16));

    // https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md#embedded-source-c-and-vb-compilers
    internal static readonly Guid EmbeddedSourceGuid = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    internal static readonly Guid LanguageTypeCSharp = new Guid("{3f5162f8-07c6-11d3-9053-00c04fa302a1}");
    internal static readonly Guid LanguageTypeBasic = new Guid("{3a12d0b8-c26c-11d0-b442-00a0244a1dd2}");

    public static readonly DiagnosticDescriptor CannotReadGeneratedFilesDiagnosticDescriptor =
        new DiagnosticDescriptor(
            "BCLA0001",
            "Cannot read generated files",
            "Error reading generated files: {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotLoadTypesDiagnosticDescriptor =
        new DiagnosticDescriptor(
            "BCLA0002",
            "Failed to load analyzer",
            "Failed to load analyzer: {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotReadFileDiagnosticDescriptor =
        new DiagnosticDescriptor(
            "BCLA0003",
            "Cannot read file",
            "Cannot read file {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ErrorReadingGeneratedFilesDiagnosticDescriptor =
        new DiagnosticDescriptor(
            "BCLA0004",
            "Error reading generated files",
            "Error reading generated files: {0}",
            "BasicCompilerLog",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    internal delegate bool SourceTextLineFunc(ReadOnlySpan<char> line, ReadOnlySpan<char> newLine);

    /// <summary>
    /// Get a source text 
    /// </summary>
    /// <remarks>
    /// TODO: need to expose the real API for how the compiler reads source files. 
    /// move this comment to the rehydration code when we write it.
    /// </remarks>
    internal static SourceText GetSourceText(Stream stream, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded)
    {
        Debug.Assert(!stream.CanSeek || stream.Position == 0);
        return SourceText.From(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);
    }

    internal static SourceText GetSourceText(string filePath, SourceHashAlgorithm checksumAlgorithm, bool canBeEmbedded)
    {
        using var stream = OpenBuildFileForRead(filePath);
        return GetSourceText(stream, checksumAlgorithm: checksumAlgorithm, canBeEmbedded: canBeEmbedded);
    }

    /// <summary>
    /// This mimics the CommonCompiler.TryReadFileContent API.
    /// </summary>
    internal static SourceText? TryGetSourceText(
        string filePath,
        SourceHashAlgorithm checksumAlgorithm,
        bool canBeEmbedded,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        try
        {
            diagnostics = [];
            return GetSourceText(filePath, checksumAlgorithm, canBeEmbedded);
        }
        catch (Exception ex)
        {
            var diagnostic = Diagnostic.Create(CannotReadFileDiagnosticDescriptor, Location.None, filePath, ex.Message);
            diagnostics = [diagnostic];
            return null;
        }
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
        PathNormalizationUtil pathNormalizationUtil,
        ImmutableArray<Diagnostic> creationDiagnostics)
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
            analyzerProvider,
            creationDiagnostics);
    }

    internal static VisualBasicCompilationData CreateVisualBasicCompilationData(
        CompilerCall compilerCall,
        string? compilationName,
        VisualBasicParseOptions parseOptions,
        VisualBasicCompilationOptions compilationOptions,
        List<(SourceText SourceText, string Path)> sourceTexts,
        List<MetadataReference> references,
        List<(SourceText SourceText, string Path)> analyzerConfigs,
        ImmutableArray<AdditionalText> additionalTexts,
        EmitOptions emitOptions,
        EmitData emitData,
        BasicAnalyzerHost basicAnalyzerHost,
        PathNormalizationUtil pathNormalizationUtil,
        ImmutableArray<Diagnostic> creationDiagnostics)
    {
        var syntaxTrees = ParseAllVisualBasic(sourceTexts, parseOptions);
        var (syntaxProvider, analyzerProvider) = CreateOptionsProviders(analyzerConfigs, syntaxTrees, additionalTexts, pathNormalizationUtil);

        compilationOptions = compilationOptions
            .WithSyntaxTreeOptionsProvider(syntaxProvider)
            .WithStrongNameProvider(new DesktopStrongNameProvider());

        var compilation = VisualBasicCompilation.Create(
            compilationName,
            syntaxTrees,
            references,
            compilationOptions);

        return new VisualBasicCompilationData(
            compilerCall,
            compilation,
            parseOptions,
            emitOptions,
            emitData,
            additionalTexts.ToImmutableArray(),
            basicAnalyzerHost,
            analyzerProvider,
            creationDiagnostics);
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
                    builder.Append(EscapeEditorConfigSectionPath(mapped));
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

    /// <summary>
    /// The path used in a editor config section needs to be escaped on Windows.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    internal static string EscapeEditorConfigSectionPath(string path)
    {
        // The \ on windows need to be escaped when emitted as part of a section header
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return path.Replace(@"\", @"/");
        }

        return path;
    }

    internal static string GetMissingFileDiagnosticMessage(string filePath) => 
        $"Missing file, either build did not happen on this machine or the environment has changed: {filePath}";

    /// <summary>
    /// Open a file from a build on the current machine and add a diagonstic if it's missing.
    /// </summary>
    internal static FileStream OpenBuildFileForRead(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new Exception(GetMissingFileDiagnosticMessage(filePath));
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal static Guid? TryReadMvid(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return ReadMvid(filePath);
        }
        catch
        {
            return null;
        }
    }

    internal static Guid ReadMvid(string filePath)
    {
        using var stream = OpenBuildFileForRead(filePath);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var mdReader = reader.GetMetadataReader();
        return ReadMvid(mdReader);
    }

    internal static Guid ReadMvid(MetadataReader mdReader)
    {
        GuidHandle handle = mdReader.GetModuleDefinition().Mvid;
        return mdReader.GetGuid(handle);
    }

    internal static string GetAssemblyFileName(CommandLineArguments arguments)
    {
        if (arguments.OutputFileName is not null)
        {
            return arguments.OutputFileName;
        }

        string name = arguments.CompilationName ?? "app";
        return $"{name}{GetStandardAssemblyExtension()}";

        string GetStandardAssemblyExtension() => arguments.CompilationOptions.OutputKind switch
        {
            OutputKind.NetModule => ".netmodule",
            OutputKind.ConsoleApplication => ".exe",
            OutputKind.WindowsApplication => ".exe",
            _ => ".dll"
        };
    }

    internal static (string? AssemblyFilePath, string? RefAssemblyFilePath) GetAssemblyOutputFilePaths(CommandLineArguments arguments)
    {
        var assemblyFileName = RoslynUtil.GetAssemblyFileName(arguments);
        string? assemblyFilePath = null;
        if (arguments.OutputDirectory is { } outputDirectory)
        {
            assemblyFilePath = Path.Combine(outputDirectory, assemblyFileName);
        }

        string? refAssemblyFilePath = null;
        if (arguments.OutputRefFilePath is { })
        {
            refAssemblyFilePath = arguments.OutputRefFilePath;
        }

        return (assemblyFilePath, refAssemblyFilePath);
    }

    /// <summary>
    /// This checks determines if this compilation should have the files from source generators
    /// embedded in the PDB. This does not look at anything on disk, it makes a determination
    /// based on the command line arguments only.
    /// </summary>
    internal static bool HasGeneratedFilesInPdb(CommandLineArguments args)
    {
        return 
            args.EmitPdb &&
            (args.EmitOptions.DebugInformationFormat is DebugInformationFormat.PortablePdb or DebugInformationFormat.Embedded);
    }

    /// <summary>
    /// Attempt to add all the generated files from generators. When successful the generators
    /// don't need to be run when re-hydrating the compilation.
    /// </summary>
    internal static List<(string FilePath, MemoryStream Stream)> ReadGeneratedFilesFromPdb(
        CompilerCall compilerCall,
        CommandLineArguments args)
    {
        if (!HasGeneratedFilesInPdb(args))
        {
            throw new InvalidOperationException("The compilation is not using a PDB format that can store generated files");
        }

        Debug.Assert(args.EmitPdb);
        Debug.Assert(args.EmitOptions.DebugInformationFormat is DebugInformationFormat.Embedded or DebugInformationFormat.PortablePdb);

        var (languageGuid, languageExtension) = compilerCall.IsCSharp
            ? (LanguageTypeCSharp, ".cs")
            : (LanguageTypeBasic, ".vb");

        var assemblyFileName = GetAssemblyFileName(args);
        var assemblyFilePath = Path.Combine(args.OutputDirectory, assemblyFileName);

        MetadataReaderProvider? pdbReaderProvider = null;
        try
        {
            using var reader = OpenBuildFileForRead(assemblyFilePath);
            using var peReader = new PEReader(reader);
            if (!peReader.TryOpenAssociatedPortablePdb(assemblyFilePath, OpenPortablePdbFile, out pdbReaderProvider, out var pdbPath))
            {
                throw new InvalidOperationException("Can't find portable pdb file for {compilerCall.GetDiagnosticName()}");
            }

            var pdbReader = pdbReaderProvider!.GetMetadataReader();
            var generatedFiles = new List<(string FilePath, MemoryStream Stream)>();
            foreach (var documentHandle in pdbReader.Documents.Skip(args.SourceFiles.Length))
            {
                if (GetContentStream(languageGuid, languageExtension, pdbReader, documentHandle) is { } tuple)
                {
                    generatedFiles.Add(tuple);
                }
            }

            return generatedFiles;
        }
        finally
        {
            pdbReaderProvider?.Dispose();
        }

        static (string FilePath, MemoryStream Stream)? GetContentStream(
            Guid languageGuid,
            string languageExtension,
            MetadataReader pdbReader,
            DocumentHandle documentHandle)
        {
            var document = pdbReader.GetDocument(documentHandle);
            if (pdbReader.GetGuid(document.Language) != languageGuid)
            {
                return null;
            }

            var filePath = pdbReader.GetString(document.Name);

            // A #line directive can be used to embed a file into the PDB. There is no way to differentiate
            // between a file embedded this way and one generated from a source generator. For the moment
            // using a simple hueristic to detect a generated file vs. say a .xaml file that was embedded
            // https://github.com/jaredpar/basic-compilerlog/issues/45
            if (Path.GetExtension(filePath) != languageExtension)
            {
                return null;
            }

            foreach (var cdiHandle in pdbReader.GetCustomDebugInformation(documentHandle))
            {
                var cdi = pdbReader.GetCustomDebugInformation(cdiHandle);
                if (pdbReader.GetGuid(cdi.Kind) != EmbeddedSourceGuid)
                {
                    continue;
                }

                var hashAlgorithmGuid = pdbReader.GetGuid(document.HashAlgorithm);
                var hashAlgorithm =
                    hashAlgorithmGuid == HashAlgorithmSha1 ? SourceHashAlgorithm.Sha1
                    : hashAlgorithmGuid == HashAlgorithmSha256 ? SourceHashAlgorithm.Sha256
                    : SourceHashAlgorithm.None;
                if (hashAlgorithm == SourceHashAlgorithm.None)
                {
                    continue;
                }

                var bytes = pdbReader.GetBlobBytes(cdi.Value);
                if (bytes is null)
                {
                    continue;
                }

                int uncompressedSize = BitConverter.ToInt32(bytes, 0);
                var stream = new MemoryStream(bytes, sizeof(int), bytes.Length - sizeof(int));

                if (uncompressedSize != 0)
                {
                    var decompressed = new MemoryStream(uncompressedSize);
                    using (var deflateStream = new DeflateStream(stream, CompressionMode.Decompress))
                    {
                        deflateStream.CopyTo(decompressed);
                    }

                    if (decompressed.Length != uncompressedSize)
                    {
                        throw new InvalidOperationException("Stream did not decompress to expected size");
                    }

                    stream = decompressed;
                }

                stream.Position = 0;
                return (filePath, stream);
            }

            return null;
        }

        // Similar to OpenFileForRead but don't throw here on file missing as it's expected that some files 
        // will not have PDBs beside them.
        static Stream? OpenPortablePdbFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
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

    internal static string? ReadCompilerCommitHash(string assemblyFilePath)
    {
        return ReadStringAssemblyAttribute(assemblyFilePath, "CommitHashAttribute");
    }

    private static string? ReadStringAssemblyAttribute(string assemblyFilePath, string attributeName)
    {
        using var stream = new FileStream(assemblyFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        return ReadStringAssemblyAttribute(metadataReader, attributeName);
    }

    private static string? ReadStringAssemblyAttribute(MetadataReader metadataReader, string attributeName)
    {
        var attributes = metadataReader.GetAssemblyDefinition().GetCustomAttributes();
        foreach (var attributeHandle in attributes)
        {
            var attribute = metadataReader.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind is HandleKind.MemberReference)
            {
                var ctor = metadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (ctor.Parent.Kind is HandleKind.TypeReference)
                {
                    var typeNameHandle = metadataReader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name;
                    var typeName = metadataReader.GetString(typeNameHandle);
                    if (typeName.EndsWith(attributeName))
                    {
                        var value = metadataReader.GetBlobReader(attribute.Value);
                        ReadAndValidatePrologue(ref value);
                        return value.ReadSerializedString();
                    }
                }
            }
        }

        return null;
    }

    internal static string? ReadAssemblyName(string assemblyFilePath)
    {
        return MetadataReader.GetAssemblyName(assemblyFilePath).Name;
    }

    internal static AssemblyIdentityData ReadAssemblyIdentityData(string assemblyFilePath)
    {
        using var stream = OpenBuildFileForRead(assemblyFilePath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var def = metadataReader.GetAssemblyDefinition();
        var assemblyName = def.GetAssemblyName().Name;
        var mvid = ReadMvid(metadataReader);
        var assemblyInformationalVersion = ReadStringAssemblyAttribute(metadataReader, nameof(AssemblyInformationalVersionAttribute));
        return new(mvid, assemblyName, assemblyInformationalVersion);
    }

    /// <summary>
    /// This will return the full name of any type in the assembly that has at least one attribute
    /// applied to it.
    /// </summary>
    internal static IEnumerable<(TypeDefinition TypeDefinition, CustomAttribute CustomAttribute)> GetTypeDefinitions(
        MetadataReader metadataReader,
        string attributeNamespace,
        string attributeName,
        Func<TypeDefinition, CustomAttribute, bool> predicate)
    {
        foreach (var typeDefHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeDefHandle);
            foreach (var handle in typeDef.GetCustomAttributes())
            {
                var attribute = metadataReader.GetCustomAttribute(handle);
                if (IsMatchingAttribute(attribute) && predicate(typeDef, attribute))
                {
                    yield return (typeDef, attribute);
                }
            }
        }

        bool IsMatchingAttribute(CustomAttribute attribute)
        {
            var ctorHandle = attribute.Constructor;
            if (ctorHandle.Kind == HandleKind.MemberReference)
            {
                var memberRef = metadataReader.GetMemberReference((MemberReferenceHandle)ctorHandle);
                var attributeTypeHandle = memberRef.Parent;

                if (attributeTypeHandle.Kind != HandleKind.TypeReference)
                {
                    return false;
                }

                var attributeTypeRef = metadataReader.GetTypeReference((TypeReferenceHandle)attributeTypeHandle);
                string name = metadataReader.GetString(attributeTypeRef.Name);
                string @namespace = metadataReader.GetString(attributeTypeRef.Namespace);
                return attributeName == name && attributeNamespace == @namespace;
            }
            
            if (ctorHandle.Kind == HandleKind.MethodDefinition)
            {
                var memberDef = metadataReader.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
                var typeDef = metadataReader.GetTypeDefinition(memberDef.GetDeclaringType());
                string name = metadataReader.GetString(typeDef.Name);
                string @namespace = metadataReader.GetString(typeDef.Namespace);
                return attributeName == name && attributeNamespace == @namespace;
            }

            return false;
        }
    }

    internal static IEnumerable<(TypeDefinition TypeDefinition, CustomAttribute CustomAttribute)> GetAnalyzerTypeDefinitions(MetadataReader metadataReader, string? languageName = null)
    {
        var attributeType = typeof(DiagnosticAnalyzerAttribute);
        return GetTypeDefinitions(
            metadataReader,
            attributeType.Namespace!,
            attributeType.Name,
            (typeDef, attribute) => languageName is null || IsLanguageName(metadataReader, attribute, languageName));
    }

    internal static IEnumerable<(TypeDefinition TypeDefinition, CustomAttribute CustomAttribute)> GetGeneratorTypeDefinitions(MetadataReader metadataReader, string? languageName = null)
    {
        var attributeType = typeof(GeneratorAttribute);
        return GetTypeDefinitions(
            metadataReader,
            attributeType.Namespace!,
            attributeType.Name,
            (typeDef, attribute) =>
            {
                if (languageName is null)
                {
                    return true;
                }

                if (IsEmptyAttribute(metadataReader, attribute))
                {
                    // The empty attribute is an implicit C# 
                    return languageName == LanguageNames.CSharp;
                }

                return IsLanguageName(metadataReader, attribute, languageName);
            });
    }

    [ExcludeFromCodeCoverage]
    private static void ReadAndValidatePrologue(ref BlobReader valueReader)
    {
        // Ensure the blob starts with the correct prolog (0x0001)
        if (valueReader.ReadUInt16() != 0x0001)
        {
            throw new InvalidOperationException("Invalid CustomAttribute prolog.");
        }
    }

    /// <summary>
    /// Does the <see cref="DiagnosticAnalyzerAttribute"/> or <see cref="GeneratorAttribute"/> 
    /// attribute match the specified language name.
    /// </summary>
    internal static bool IsLanguageName(
        MetadataReader metadataReader,
        CustomAttribute attribute,
        string languageName)
    {
        Debug.Assert(!string.IsNullOrEmpty(languageName));

        var valueReader = metadataReader.GetBlobReader(attribute.Value);
        ReadAndValidatePrologue(ref valueReader);

        // Read first argument (string)
        string firstArgument = valueReader.ReadSerializedString()!;
        if (firstArgument == languageName)
        {
            return true;
        }

        // Read second argument (string array)
        int arrayLength = valueReader.ReadInt32();
        for (int i = 0; i < arrayLength; i++)
        {
            var current =  valueReader.ReadSerializedString()!;
            if (current == languageName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Count the language names present in a <see cref="DiagnosticAnalyzerAttribute"/> or 
    /// <see cref="GeneratorAttribute"/> attribute.
    /// </summary>
    internal static int CountLanguageNames(
        MetadataReader metadataReader,
        CustomAttribute attribute)
    {
        var valueReader = metadataReader.GetBlobReader(attribute.Value);
        ReadAndValidatePrologue(ref valueReader);

        _ = valueReader.ReadSerializedString()!;

        // Read second argument (string array)
        int arrayLength = valueReader.ReadInt32();

        return arrayLength + 1;
    }

    internal static bool IsEmptyAttribute(MetadataReader metadataReader, CustomAttribute attribute)
    {
        var valueReader = metadataReader.GetBlobReader(attribute.Value);
        ReadAndValidatePrologue(ref valueReader);
        // the remaining 2 bytes is named argument count
        return valueReader.RemainingBytes == 2;
    }

    internal static string GetFullyQualifiedName(MetadataReader reader, TypeDefinition typeDef)
    {
        string @namespace = reader.GetString(typeDef.Namespace);
        string name = reader.GetString(typeDef.Name);

        // Handle nested types
        if (typeDef.GetDeclaringType().IsNil)
        {
            return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
        }
        else
        {
            // Recursively get the declaring type's name
            var declaringType = reader.GetTypeDefinition(typeDef.GetDeclaringType());
            return $"{GetFullyQualifiedName(reader, declaringType)}+{name}";
        }
    }
}
