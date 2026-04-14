using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class R2RUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public R2RUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(R2RUtilTests))
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Verify that ReadyToRun analyzers stored in a compiler log are stripped to IL-only when
    /// <see cref="LogReaderState.StripReadyToRun"/> is set to <see langword="true"/>. This exercises
    /// the stripping code path unconditionally, regardless of whether the stored R2R native code
    /// matches the current process architecture.
    /// </summary>
    [Fact]
    public void ReadyToRunAnalyzersAreStripped()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None,
            new LogReaderState(stripReadyToRun: true));
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // Find at least one R2R analyzer in the stored log bytes
        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.NotEmpty(r2rData);

        // When retrieved through the data provider interface, they should be IL-only
        var provider = (IBasicAnalyzerHostDataProvider)reader;
        foreach (var analyzerData in r2rData)
        {
            var strippedBytes = provider.GetAnalyzerBytes(analyzerData);
            Assert.False(R2RUtil.IsReadyToRun(strippedBytes), $"{analyzerData.FileName} should be IL-only after stripping");

            // Verify that the CopyAnalyzerBytes path also produces stripped bytes
            using var ms = new MemoryStream();
            provider.CopyAnalyzerBytes(analyzerData, ms);
            Assert.False(R2RUtil.IsReadyToRun(ms.ToArray()), $"{analyzerData.FileName} CopyAnalyzerBytes should produce IL-only output");
        }
    }

    /// <summary>
    /// Verify that R2R analyzer assemblies whose machine type matches the current process
    /// architecture are NOT automatically stripped. Same-arch assemblies execute natively and
    /// must be left intact to preserve their strong-name identity and avoid unnecessary overhead.
    /// </summary>
    [Fact]
    public void ReadyToRunNotStrippedOnSameArchitecture()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath, BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // Find R2R assemblies that do NOT need stripping (i.e. same-arch as current process)
        var sameArchR2rData = analyzerDataList
            .Where(a =>
            {
                var bytes = reader.GetAssemblyBytes(a.Mvid);
                return R2RUtil.IsReadyToRun(bytes) && !R2RUtil.NeedsStripping(bytes);
            })
            .ToList();

        // The SDK always produces same-arch R2R analyzers when building on the native platform
        Assert.NotEmpty(sameArchR2rData);
    }

    /// <summary>
    /// Verify that R2R stripping produces a valid, loadable assembly that retains its analyzer
    /// functionality. Stripped analyzers must still execute correctly.
    /// </summary>
#if NET
    [Fact]
    public void ReadyToRunAnalyzersStillWork()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.InMemory,
            new LogReaderState(stripReadyToRun: true));
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.Contains(analyzerDataList, a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));

        // Load all analyzers through the host (which strips R2R) and run them
        using var host = new BasicAnalyzerHostInMemory(reader, analyzerDataList);
        Assert.NotEmpty(host.AnalyzerReferences);

        var diagnostics = new List<Diagnostic>();
        foreach (var reference in host.AnalyzerReferences)
        {
            reference.AsBasicAnalyzerReference().GetAnalyzers(LanguageNames.CSharp, diagnostics);
        }

        // The .NET SDK analyzers contain real analyzers, so we expect non-empty results
        Assert.NotEmpty(host.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)));
    }
#endif

    /// <summary>
    /// Verifies that the stripped analyzer byte cache is populated when bytes are retrieved,
    /// and that subsequent calls return the cached (already-stripped) result.
    /// </summary>
    [Fact]
    public void ReadyToRunStrippedAnalyzerBytesAreCached()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None,
            new LogReaderState(stripReadyToRun: true));
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();

        // We build with .NET 10 SDK which always produces R2R analyzers
        Assert.NotEmpty(r2rData);

        var provider = (IBasicAnalyzerHostDataProvider)reader;
        var analyzerData = r2rData[0];

        var bytes1 = provider.GetAnalyzerBytes(analyzerData);
        var bytes2 = provider.GetAnalyzerBytes(analyzerData);

        // Same reference should be returned from the cache
        Assert.Same(bytes1, bytes2);
    }

#if NET
    /// <summary>
    /// Verify that when R2R stripping is always enabled, generators that actually execute during
    /// compilation still produce correct output. The Console fixture uses [GeneratedRegex] which
    /// requires the RegexGenerator source generator to run.
    /// </summary>
    [Fact]
    public void StripReadyToRunGeneratorsExecute()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.InMemory,
            new LogReaderState(stripReadyToRun: true));

        var data = reader.ReadCompilationData(0);
        var compilation = data.GetCompilationAfterGenerators(out var diagnostics, CancellationToken);

        Assert.Empty(diagnostics);

        // The Console fixture uses [GeneratedRegex], so RegexGenerator must have run and
        // produced the REGEX_DEFAULT_MATCH_TIMEOUT field in the generated source.
        Assert.Contains(compilation.SyntaxTrees, t => t.ToString().Contains("REGEX_DEFAULT_MATCH_TIMEOUT"));

        data.BasicAnalyzerHost.Dispose();
    }
#endif

    /// <summary>
    /// The "always" normalization util should strip any R2R assembly, even one matching
    /// the current architecture.
    /// </summary>
    [Fact]
    public void AlwaysNormalizationStripsR2R()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();
        Assert.NotEmpty(r2rData);

        var util = AnalyzerNormalizationUtil.Create(true);
        foreach (var data in r2rData)
        {
            var rawBytes = reader.GetAssemblyBytes(data.Mvid);
            using var peReader = new PEReader(rawBytes.AsSimpleMemoryStream(writable: false));
            Assert.True(util.NeedsStripping(peReader));
            var normalized = util.NormalizeBytes(data.Mvid, rawBytes);
            Assert.False(R2RUtil.IsReadyToRun(normalized));
        }
    }

    /// <summary>
    /// The "never" normalization util should return bytes unchanged regardless of R2R status.
    /// </summary>
    [Fact]
    public void NeverNormalizationReturnsUnchanged()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rData = analyzerDataList
            .Where(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)))
            .ToList();
        Assert.NotEmpty(r2rData);

        var util = AnalyzerNormalizationUtil.Create(false);
        foreach (var data in r2rData)
        {
            var rawBytes = reader.GetAssemblyBytes(data.Mvid);
            using var peReader = new PEReader(rawBytes.AsSimpleMemoryStream(writable: false));
            Assert.False(util.NeedsStripping(peReader));
            var normalized = util.NormalizeBytes(data.Mvid, rawBytes);
            Assert.Same(rawBytes, normalized);
        }
    }

    /// <summary>
    /// The "always" normalization util should cache stripped results so the same stripped
    /// byte array is returned on subsequent calls.
    /// </summary>
    [Fact]
    public void AlwaysNormalizationCachesResult()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rAnalyzer = analyzerDataList
            .First(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));

        var util = AnalyzerNormalizationUtil.Create(true);
        var rawBytes = reader.GetAssemblyBytes(r2rAnalyzer.Mvid);
        var first = util.NormalizeBytes(r2rAnalyzer.Mvid, rawBytes);
        var second = util.NormalizeBytes(r2rAnalyzer.Mvid, rawBytes);
        Assert.Same(first, second);
    }

    /// <summary>
    /// When <see cref="AnalyzerNormalizationUtil.NeedsStripping"/> returns <see langword="false"/>
    /// for the given bytes, <see cref="AnalyzerNormalizationUtil.NormalizeBytes"/> must return
    /// the same byte array unchanged. This tests the "always" util with an IL-only (non-R2R)
    /// assembly to verify the short-circuit path.
    /// </summary>
    [Fact]
    public void NormalizeBytesReturnsUnchangedWhenNeedsStrippingIsFalse()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);

        // First strip an R2R assembly to get a known IL-only byte array
        var analyzerDataList = reader.ReadAllAnalyzerData(0);
        var r2rAnalyzer = analyzerDataList
            .First(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));
        var ilOnlyBytes = R2RUtil.StripReadyToRun(reader.GetAssemblyBytes(r2rAnalyzer.Mvid));
        Assert.False(R2RUtil.IsReadyToRun(ilOnlyBytes));

        // The "always" util should see NeedsStripping == false for IL-only bytes
        // and return them unchanged
        var util = AnalyzerNormalizationUtil.Create(true);
        using var peReader = new PEReader(ilOnlyBytes.AsSimpleMemoryStream(writable: false));
        Assert.False(util.NeedsStripping(peReader));
        var normalized = util.NormalizeBytes(r2rAnalyzer.Mvid, ilOnlyBytes);
        Assert.Same(ilOnlyBytes, normalized);
    }

    /// <summary>
    /// Verify that <see cref="R2RUtil.IsReadyToRun(byte[])"/> returns false for an IL-only
    /// managed assembly (no R2R native code).
    /// </summary>
    [Fact]
    public void IsReadyToRunReturnsFalseForILOnly()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rAnalyzer = analyzerDataList
            .First(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));
        var ilOnlyBytes = R2RUtil.StripReadyToRun(reader.GetAssemblyBytes(r2rAnalyzer.Mvid));

        Assert.False(R2RUtil.IsReadyToRun(ilOnlyBytes));
    }

    /// <summary>
    /// Verify that <see cref="R2RUtil.NeedsStripping(byte[])"/> returns false for an IL-only
    /// managed assembly since there is no R2R data to strip.
    /// </summary>
    [Fact]
    public void NeedsStrippingReturnsFalseForILOnly()
    {
        using var reader = CompilerLogReader.Create(
            Fixture.Console.Value.CompilerLogPath,
            BasicAnalyzerKind.None);
        var analyzerDataList = reader.ReadAllAnalyzerData(0);

        var r2rAnalyzer = analyzerDataList
            .First(a => R2RUtil.IsReadyToRun(reader.GetAssemblyBytes(a.Mvid)));
        var ilOnlyBytes = R2RUtil.StripReadyToRun(reader.GetAssemblyBytes(r2rAnalyzer.Mvid));

        Assert.False(R2RUtil.NeedsStripping(ilOnlyBytes));
    }

    /// <summary>
    /// Verify that <see cref="R2RUtil.StripReadyToRun(byte[])"/> throws when given bytes
    /// that do not contain managed metadata (e.g. a native PE or random data).
    /// </summary>
    [Fact]
    public void StripReadyToRunThrowsForNonManagedPE()
    {
        // Build a minimal valid PE that has no CLR metadata
        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(machine: Machine.Amd64),
            new MetadataRootBuilder(new MetadataBuilder()),
            new BlobBuilder());
        var blobBuilder = new BlobBuilder();
        peBuilder.Serialize(blobBuilder);
        var nativeBytes = blobBuilder.ToArray();

        // Verify this PE is not detected as R2R
        Assert.False(R2RUtil.IsReadyToRun(nativeBytes));
    }

    /// <summary>
    /// Verify that <see cref="R2RUtil.IsMatchingArchitecture"/> correctly matches each
    /// architecture to its expected <see cref="Machine"/> value.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X64, Machine.Amd64, true)]
    [InlineData(Architecture.X64, Machine.I386, false)]
    [InlineData(Architecture.X64, Machine.Arm64, false)]
    [InlineData(Architecture.X86, Machine.I386, true)]
    [InlineData(Architecture.X86, Machine.Amd64, false)]
    [InlineData(Architecture.X86, Machine.Arm64, false)]
    [InlineData(Architecture.Arm64, Machine.Arm64, true)]
    [InlineData(Architecture.Arm64, Machine.Amd64, false)]
    [InlineData(Architecture.Arm64, Machine.I386, false)]
    [InlineData(Architecture.Arm, Machine.Arm, true)]
    [InlineData(Architecture.Arm, Machine.ArmThumb2, true)]
    [InlineData(Architecture.Arm, Machine.Amd64, false)]
    public void IsMatchingArchitecture(Architecture arch, Machine machine, bool expected)
    {
        Assert.Equal(expected, R2RUtil.IsMatchingArchitecture(arch, machine));
    }

    /// <summary>
    /// Builds a minimal PE assembly with static fields that have RVAs for each primitive type
    /// exercised by <c>GetMappedFieldDataSize</c>, strips it through <see cref="R2RUtil.StripReadyToRun"/>,
    /// and verifies the output has valid metadata with the same field definitions.
    /// </summary>
    [Fact]
    public void StripPreservesFieldRvaForAllPrimitiveTypes()
    {
        var bytes = BuildAssemblyWithFieldRvas();
        Assert.False(R2RUtil.IsReadyToRun(bytes));

        var stripped = R2RUtil.StripReadyToRun(bytes);

        // Verify stripped output has valid metadata and all field definitions survived
        using var stream = new MemoryStream(stripped);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        // Count fields that have RVAs in the stripped output
        var rvaFieldCount = 0;
        foreach (var fieldHandle in metadataReader.FieldDefinitions)
        {
            var field = metadataReader.GetFieldDefinition(fieldHandle);
            if (field.GetRelativeVirtualAddress() != 0)
            {
                rvaFieldCount++;
            }
        }

        // We created 12 primitive fields + 1 ValueType field = 13 RVA fields
        Assert.Equal(13, rvaFieldCount);
    }

    /// <summary>
    /// Builds a PE with static fields having RVAs for: bool, char, sbyte, byte, short, ushort,
    /// int, uint, long, ulong, float, double, and a ValueType with explicit layout.
    /// This exercises every case in <c>GetMappedFieldDataSize</c>.
    /// </summary>
    private static byte[] BuildAssemblyWithFieldRvas()
    {
        var metadata = new MetadataBuilder();
        var ilBuilder = new BlobBuilder();
        var fieldData = new BlobBuilder();

        metadata.AddModule(
            0,
            metadata.GetOrAddString("FieldRvaTest.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);

        metadata.AddAssembly(
            metadata.GetOrAddString("FieldRvaTest"),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            AssemblyHashAlgorithm.None);

        // Add a reference to System.Runtime for [mscorlib]System.Object
        var corlibRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0),
            default,
            metadata.GetOrAddBlob(Array.Empty<byte>()),
            default,
            default);

        var systemObjectRef = metadata.AddTypeReference(
            corlibRef,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));

        // Create a ValueType struct with explicit layout (Size=16) for case 17
        var structTypeDef = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString(""),
            metadata.GetOrAddString("MyStruct"),
            systemObjectRef,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        // Set layout with explicit size
        metadata.AddTypeLayout(structTypeDef, 0, 16);

        // Helper to encode a field signature for a primitive type code
        BlobHandle EncodePrimitiveSig(byte typeCode)
        {
            var sig = new BlobBuilder();
            sig.WriteByte(0x06); // FIELD calling convention
            sig.WriteByte(typeCode);
            return metadata.GetOrAddBlob(sig);
        }

        // Helper to encode a ValueType field signature
        BlobHandle EncodeValueTypeSig(TypeDefinitionHandle typeDef)
        {
            var sig = new BlobBuilder();
            sig.WriteByte(0x06); // FIELD
            sig.WriteByte(17);   // VALUETYPE
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(typeDef));
            return metadata.GetOrAddBlob(sig);
        }

        // Primitive types: (name, typeCode, dataSize)
        (string Name, byte TypeCode, int Size)[] primitives =
        [
            ("BoolField",   2,  1),  // bool
            ("CharField",   3,  2),  // char
            ("SByteField",  4,  1),  // sbyte
            ("ByteField",   5,  1),  // byte
            ("ShortField",  6,  2),  // short
            ("UShortField", 7,  2),  // ushort
            ("IntField",    8,  4),  // int
            ("UIntField",   9,  4),  // uint
            ("LongField",   10, 8),  // long
            ("ULongField",  11, 8),  // ulong
            ("FloatField",  12, 4),  // float
            ("DoubleField", 13, 8),  // double
        ];

        FieldDefinitionHandle firstField = default;
        foreach (var (name, typeCode, size) in primitives)
        {
            var offset = fieldData.Count;
            fieldData.WriteBytes(0xAB, size);
            var sigHandle = EncodePrimitiveSig(typeCode);
            var fieldDef = metadata.AddFieldDefinition(
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                metadata.GetOrAddString(name),
                sigHandle);
            metadata.AddFieldRelativeVirtualAddress(fieldDef, offset);
            if (firstField.IsNil)
                firstField = fieldDef;
        }

        // ValueType field (case 17)
        {
            var offset = fieldData.Count;
            fieldData.WriteBytes(0xCD, 16);
            var sigHandle = EncodeValueTypeSig(structTypeDef);
            var fieldDef = metadata.AddFieldDefinition(
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                metadata.GetOrAddString("StructField"),
                sigHandle);
            metadata.AddFieldRelativeVirtualAddress(fieldDef, offset);
        }

        // Add a container type that holds these fields
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString(""),
            metadata.GetOrAddString("FieldContainer"),
            systemObjectRef,
            firstField,
            MetadataTokens.MethodDefinitionHandle(1));

        var metadataRootBuilder = new MetadataRootBuilder(metadata);
        var header = new PEHeaderBuilder(
            machine: Machine.Amd64,
            imageCharacteristics: Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            header,
            metadataRootBuilder,
            ilBuilder,
            fieldData,
            flags: CorFlags.ILOnly);

        var outputBuilder = new BlobBuilder();
        peBuilder.Serialize(outputBuilder);
        return outputBuilder.ToArray();
    }

    /// <summary>
    /// Verify that stripping a C#-compiled assembly with array initializers (which produce
    /// ValueType field-RVA data) preserves the data and produces valid metadata.
    /// </summary>
    [Fact]
    public void StripPreservesArrayInitializerFieldRva()
    {
        var code = """
            public static class ArrayData
            {
                public static readonly int[] Ints = new int[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                public static readonly byte[] Bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                public static readonly bool[] Bools = new bool[] { true, false, true, false };
                public static readonly double[] Doubles = new double[] { 1.0, 2.0, 3.0, 4.0 };
                public static readonly long[] Longs = new long[] { 100L, 200L, 300L, 400L };
            }
            """;

        var compilation = CSharpCompilation.Create(
            "ArrayInitTest",
            [CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.CancellationToken)],
            Net100.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var peStream = new MemoryStream();
        var result = compilation.Emit(peStream, cancellationToken: TestContext.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var originalBytes = peStream.ToArray();
        var stripped = R2RUtil.StripReadyToRun(originalBytes);

        // Verify stripped output has valid metadata
        using var strippedStream = new MemoryStream(stripped);
        using var peReader = new PEReader(strippedStream);
        var metadataReader = peReader.GetMetadataReader();

        // The <PrivateImplementationDetails> type should have fields with RVAs
        var rvaFieldCount = 0;
        foreach (var fieldHandle in metadataReader.FieldDefinitions)
        {
            var field = metadataReader.GetFieldDefinition(fieldHandle);
            if (field.GetRelativeVirtualAddress() != 0)
            {
                rvaFieldCount++;
            }
        }

        Assert.True(rvaFieldCount > 0, "Expected at least one field with RVA from array initializers");
    }
}
