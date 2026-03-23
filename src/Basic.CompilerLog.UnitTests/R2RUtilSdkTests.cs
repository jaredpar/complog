#if NET

using Basic.CompilerLog.Util;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// Tests for <see cref="R2RUtil"/> that operate directly on ReadyToRun assemblies from the
/// .NET SDK compiler directory. These tests exercise the stripping and loading code paths
/// using real-world R2R DLLs rather than the analyzer assemblies stored inside a .complog file.
/// </summary>
public sealed class R2RUtilSdkTests
{
    /// <summary>
    /// Returns the path to the Roslyn bincore directory in any available .NET SDK installation, or
    /// <see langword="null"/> if no SDK compiler directory can be located.
    /// </summary>
    private static string? FindSdkCompilerDirectory()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is null)
        {
            return null;
        }

        var sdkDir = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDir))
        {
            return null;
        }

        return Directory.GetDirectories(sdkDir)
            .Select(d => Path.Combine(d, "Roslyn", "bincore"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "csc.dll")));
    }

    /// <summary>
    /// Verifies that the core Roslyn compiler assemblies shipped with the .NET SDK are ReadyToRun
    /// images. These are the primary source of R2R DLLs used as analyzers in practice.
    /// </summary>
    [Fact]
    public void SdkCompilerDllsAreReadyToRun()
    {
        var compilerDir = FindSdkCompilerDirectory();
        if (compilerDir is null)
        {
            Assert.Skip("DOTNET_ROOT environment variable is not set or no SDK with a Roslyn/bincore directory was found");
        }

        string[] coreCompilerDlls = ["csc.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"];
        foreach (var dllName in coreCompilerDlls)
        {
            var path = Path.Combine(compilerDir, dllName);
            Assert.True(File.Exists(path), $"{dllName} should exist in the SDK compiler directory");
            Assert.True(R2RUtil.IsReadyToRun(File.ReadAllBytes(path)), $"{dllName} should be ReadyToRun");
        }
    }

    /// <summary>
    /// Verifies that <see cref="R2RUtil.StripReadyToRun"/> produces IL-only output when applied to
    /// every ReadyToRun assembly in the SDK's Roslyn compiler directory, and that
    /// <see cref="R2RUtil.IsReadyToRun"/> returns <see langword="false"/> for the stripped bytes.
    /// </summary>
    [Fact]
    public void StripReadyToRunSdkDllsProducesILOnly()
    {
        var compilerDir = FindSdkCompilerDirectory();
        if (compilerDir is null)
        {
            Assert.Skip("DOTNET_ROOT environment variable is not set or no SDK with a Roslyn/bincore directory was found");
        }

        var r2rCount = 0;
        foreach (var dllPath in Directory.GetFiles(compilerDir, "*.dll"))
        {
            var bytes = File.ReadAllBytes(dllPath);
            if (!R2RUtil.IsReadyToRun(bytes))
            {
                continue;
            }

            r2rCount++;
            var stripped = R2RUtil.StripReadyToRun(bytes);
            Assert.False(R2RUtil.IsReadyToRun(stripped), $"{Path.GetFileName(dllPath)} should be IL-only after stripping");
            Assert.True(stripped.Length > 0, $"Stripped {Path.GetFileName(dllPath)} should be non-empty");
        }

        // Sanity check: the SDK should always ship at least the core Roslyn DLLs as R2R
        Assert.True(r2rCount >= 1, "Expected at least one R2R DLL in the SDK compiler directory");
    }

    /// <summary>
    /// Verifies that SDK Roslyn assemblies stripped of ReadyToRun native code are still loadable
    /// into an <see cref="AssemblyLoadContext"/> and that their IL executes correctly. The test
    /// uses reflection to call <c>CSharpSyntaxTree.ParseText</c> on the loaded stripped assembly
    /// and confirms the returned tree matches the input source text.
    /// </summary>
    [Fact]
    public void StrippedSdkAssemblyIsLoadableAndFunctional()
    {
        var compilerDir = FindSdkCompilerDirectory();
        if (compilerDir is null)
        {
            Assert.Skip("DOTNET_ROOT environment variable is not set or no SDK with a Roslyn/bincore directory was found");
        }

        var alc = new AssemblyLoadContext("StrippedCompilerTest", isCollectible: true);
        try
        {
            string[] coreDlls = ["Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"];
            foreach (var dllName in coreDlls)
            {
                var bytes = File.ReadAllBytes(Path.Combine(compilerDir, dllName));
                var loadBytes = R2RUtil.IsReadyToRun(bytes) ? R2RUtil.StripReadyToRun(bytes) : bytes;
                alc.LoadFromStream(new MemoryStream(loadBytes));
            }

            var csAssembly = alc.Assemblies.First(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");

            // Verify the assembly loaded and its types are accessible
            Assert.NotEmpty(csAssembly.GetExportedTypes());

            // Call CSharpSyntaxTree.ParseText via reflection and verify the IL actually executes
            var syntaxTreeType = csAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            Assert.NotNull(syntaxTreeType);

            // Locate the overload: ParseText(string text, CSharpParseOptions, string path, Encoding, CancellationToken)
            var parseTextMethod = syntaxTreeType!
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "ParseText" &&
                       m.GetParameters().Length == 5 &&
                       m.GetParameters()[0].ParameterType == typeof(string));
            Assert.NotNull(parseTextMethod);

            const string source = "class C {}";
            var tree = parseTextMethod!.Invoke(null, [source, null, null, null, CancellationToken.None]);

            Assert.NotNull(tree);
            Assert.Equal(source, tree!.ToString());
        }
        finally
        {
            alc.Unload();
        }
    }
}

#endif
