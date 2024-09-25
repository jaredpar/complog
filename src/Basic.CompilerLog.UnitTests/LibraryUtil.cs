
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

            [Guid("E8E4B023-7408-4A71-B3F6-ADEDE0A8FE11")]  // Unique identifier for the COM interface
            [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]  // IDispatch for late binding
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
            [ CSharpSyntaxTree.ParseText(content1), CSharpSyntaxTree.ParseText(content2) ],
            Net60.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            throw new Exception(GetMessage(emitResult.Diagnostics));
        }

        peStream.Position = 0;
        return ("SimplePia.dll", peStream);

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