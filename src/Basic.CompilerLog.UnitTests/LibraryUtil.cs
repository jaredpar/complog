
using System.Drawing.Text;
using System.Text;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// Class that builds helper libraries that are useful in tests
/// </summary>
internal static class LibraryUtil
{
    internal static (string FileName, MemoryStream Image) GetSimplePia()
    {
        var content1 = """
            using System.Runtime.InteropServices;

            [Guid("E8E4B023-7408-4A71-B3F6-ADEDE0A8FE11")]
            [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
            public interface ICalculator
            {
                [DispId(1)]  // COM method identifiers
                int Add(int x, int y);

                [DispId(2)]
                int Subtract(int x, int y);
            }

            """;

        var content2 = """
            using System.Reflection;
            using System.Runtime.InteropServices;

            [assembly: ComVisible(true)]
            [assembly: Guid("12345678-90AB-CDEF-1234-567890ABCDEF")]
            [assembly: PrimaryInteropAssembly(1, 0)]
            """;

        var compilation = CSharpCompilation.Create(
            "SimplePia",
            [CSharpSyntaxTree.ParseText(content1), CSharpSyntaxTree.ParseText(content2)],
            Net80.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Compile(compilation, "SimplePia.dll");
    }

    /// <summary>
    /// Returns an assembly that has a set of well defined analyzers and generators and ones
    /// with definition errors.
    /// </summary>
    internal static (string FileName, MemoryStream Image) GetAnalyzersWithBadMetadata()
    {
        var code = """
            using System.Collections.Immutable;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public class BadAnalyzer
            {
            }

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public class GoodAnalyzer : DiagnosticAnalyzer
            {
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
                public override void Initialize(AnalysisContext context) { }
            }

            [Generator(LanguageNames.CSharp)]
            public class BadGenerator { }

            [Generator(LanguageNames.CSharp)]
            public class GoodGenerator : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) { }
            }
            """;

        var roslynReference = MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "TestAnalyzers",
            [CSharpSyntaxTree.ParseText(code)],
            [.. Net80.References.All, roslynReference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return Compile(compilation, "TestAnalyzers.dll");
    }

    /// <summary>
    /// Returns an assembly that has a set of well defined analyzers and generators and ones
    /// with definition errors.
    /// </summary>
    internal static (string FileName, MemoryStream Image) GetAnalyzersWithDiffAttribtueCombinations()
    {
        var code = """
            using System.Collections.Immutable;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public class Analyzer1 : DiagnosticAnalyzer
            {
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
                public override void Initialize(AnalysisContext context) { }
            }

            [DiagnosticAnalyzer(LanguageNames.VisualBasic)]
            public class Analyzer2 : DiagnosticAnalyzer
            {
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
                public override void Initialize(AnalysisContext context) { }
            }

            [Generator(LanguageNames.CSharp)]
            public class Generator1 : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) { }
            }

            [Generator]
            public class Generator2 : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) { }
            }

            [Generator(LanguageNames.VisualBasic)]
            public class Generator3 : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) { }
            }
            """;

        var roslynReference = MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "TestAnalyzers",
            [CSharpSyntaxTree.ParseText(code)],
            [.. Net80.References.All, roslynReference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return Compile(compilation, "TestAnalyzers.dll");
    }

    private static (string FileName, MemoryStream Image) Compile(Compilation compilation, string fileName)
    {
        var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            throw new Exception(GetMessage(emitResult.Diagnostics));
        }

        peStream.Position = 0;
        return (fileName, peStream);

        string GetMessage(IEnumerable<Diagnostic> diagnostics)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Compilation failed with the following errors:");
            foreach (var d in diagnostics)
            {
                builder.AppendLine(d.ToString());
            }
            return builder.ToString();
        }
    }
}