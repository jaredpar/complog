using Basic.CompilerLog.Util;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
public sealed class CompilerLogBuilderTests : TestBase
{
    public SolutionFixture Fixture { get; }

    public CompilerLogBuilderTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, SolutionFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogBuilderTests))
    {
        Fixture = fixture;
    }

    private void WithCompilerCall(Action<CompilerLogBuilder, CompilerCall, IReadOnlyCollection<string>> action)
    {
        using var stream = new MemoryStream();
        using var builder = new CompilerLogBuilder(stream, new());
        using var binlogReader = BinaryLogReader.Create(Fixture.SolutionBinaryLogPath);

        var compilerCall = binlogReader
            .ReadAllCompilerCalls(x => x.ProjectFileName == Fixture.ConsoleProjectName)
            .Single();
        action(builder, compilerCall, binlogReader.ReadArguments(compilerCall));
    }

    /// <summary>
    /// We should be able to create log files that are resilient to artifacts missing on disk. Basically we can create
    /// a <see cref="CompilationData"/> for this scenario, it will have diagnostics.
    /// </summary>
    [Fact]
    public void MissingFileSourceLink()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a source link that doesn't exist
            builder.AddFromDisk(compilerCall, ["/sourcelink:does-not-exist.txt"]);
            Assert.NotEmpty(builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetMissing()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a ruleset that doesn't exist
            builder.AddFromDisk(compilerCall, ["/ruleset:does-not-exist.ruleset"]);
            Assert.NotEmpty(builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetInvalidXml()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            // Add a ruleset with invalid XML
            var filePath = Path.Combine(RootDirectory, "invalid.ruleset");
            File.WriteAllText(filePath, "not valid xml");
            builder.AddFromDisk(compilerCall, [$"/ruleset:{filePath}"]);
            Assert.Equal([RoslynUtil.GetDiagnosticCannotReadRulset(filePath)], builder.Diagnostics);
        });
    }

    [Fact]
    public void RulesetMissingInclude()
    {
        WithCompilerCall((builder, compilerCall, _) =>
        {
            var filePath = Path.Combine(RootDirectory, "example.ruleset");
            File.WriteAllText(filePath, """
                <RuleSet Name="Rules for Hello World project" Description="These rules focus on critical issues for the Hello World app." ToolsVersion="10.0">
                    <Localization ResourceAssembly="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.dll" ResourceBaseName="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.Localized">
                        <Name Resource="HelloWorldRules_Name" />
                        <Description Resource="HelloWorldRules_Description" />
                    </Localization>
                    <Rules AnalyzerId="Microsoft.Analyzers.ManagedCodeAnalysis" RuleNamespace="Microsoft.Rules.Managed">
                        <Rule Id="CA1001" Action="Warning" />
                        <Rule Id="CA1009" Action="Warning" />
                        <Rule Id="CA1016" Action="Warning" />
                        <Rule Id="CA1033" Action="Warning" />
                    </Rules>
                    <Include Path="nested.ruleset" Action="Default" />
                </RuleSet>
                """);

            // Add a ruleset that doesn't exist
            builder.AddFromDisk(compilerCall, [$"/ruleset:{filePath}"]);
            Assert.Equal([RoslynUtil.GetDiagnosticMissingFile(Path.Combine(RootDirectory, "nested.ruleset"))], builder.Diagnostics);
        });
    }

    [Fact]
    public void PortablePdbMissing()
    {
        RunDotNet("new console -o .");
        RunDotNet("build -bl:msbuild.binlog");

        Directory
            .EnumerateFiles(RootDirectory, "*.pdb", SearchOption.AllDirectories)
            .ForEach(File.Delete);

        using var complogStream = new MemoryStream();
        using var binlogStream = new FileStream(Path.Combine(RootDirectory, "msbuild.binlog"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream);
        Assert.Contains(diagnostics, x => x.Contains("Can't find portable pdb"));
    }

    [Fact]
    public void CloseTwice()
    {
        var builder = new CompilerLogBuilder(new MemoryStream(), []);
        builder.Close();
        Assert.Throws<InvalidOperationException>(() => builder.Close());
    }

    [Fact]
    public void CompilerFilePathMissingCommitHash()
    {
        WithCompilerCall((builder, compilerCall, arguments) =>
        {
            compilerCall = new CompilerCall(
                compilerCall.ProjectFilePath,
                compilerFilePath: typeof(CompilerLogBuilderTests).Assembly.Location);
            builder.AddFromDisk(compilerCall, arguments);
            Assert.Equal([RoslynUtil.GetDiagnosticMissingCommitHash(compilerCall.CompilerFilePath!)], builder.Diagnostics);
        });
    }

    [Fact]
    public void ImplicitReferencesDiscovered()
    {
        var refDir = Path.Combine(RootDirectory, "refs");
        Directory.CreateDirectory(refDir);
        CreateFakeAssembly(refDir, "System.EnterpriseServices.dll");
        CreateFakeAssembly(refDir, "System.EnterpriseServices.Wrapper.dll");
        CreateFakeAssembly(refDir, "System.EnterpriseServices.Thunk.dll");

        var explicitPath = Path.Combine(refDir, "System.EnterpriseServices.dll");

        WithCompilerCall((builder, compilerCall, arguments) =>
        {
            var args = arguments.Append($"/reference:{explicitPath}").ToArray();
            builder.AddFromDisk(compilerCall, args);
        }, out var complogStream);

        complogStream.Position = 0;
        using var reader = CompilerLogReader.Create(complogStream, leaveOpen: false);
        var call = reader.ReadCompilerCall(0);
        var refs = reader.ReadAllReferenceData(call);

        var enterpriseRefs = refs.Where(r => Path.GetFileName(r.FilePath).StartsWith("System.EnterpriseServices", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, enterpriseRefs.Count);

        var explicitRef = enterpriseRefs.Single(r => Path.GetFileName(r.FilePath) == "System.EnterpriseServices.dll");
        Assert.False(explicitRef.IsImplicit);

        var implicitRefs = enterpriseRefs.Where(r => r.IsImplicit).ToList();
        Assert.Equal(2, implicitRefs.Count);
        Assert.All(implicitRefs, r => Assert.True(r.IsImplicit));
    }

    [Fact]
    public void ImplicitReferencesNotInCompilation()
    {
        var refDir = Path.Combine(RootDirectory, "refs");
        Directory.CreateDirectory(refDir);
        CreateFakeAssembly(refDir, "System.EnterpriseServices.dll");
        CreateFakeAssembly(refDir, "System.EnterpriseServices.Wrapper.dll");

        var explicitPath = Path.Combine(refDir, "System.EnterpriseServices.dll");

        WithCompilerCall((builder, compilerCall, arguments) =>
        {
            var args = arguments.Append($"/reference:{explicitPath}").ToArray();
            builder.AddFromDisk(compilerCall, args);
        }, out var complogStream);

        complogStream.Position = 0;
        using var reader = CompilerLogReader.Create(complogStream, leaveOpen: false);
        var call = reader.ReadCompilerCall(0);
        var compilationData = reader.ReadCompilationData(call);
        var compilation = compilationData.GetCompilationAfterGenerators(CancellationToken);

        // The implicit reference should NOT be in the compilation's references
        var compilationRefNames = compilation
            .References
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileName(r.FilePath))
            .ToList();

        Assert.Contains("System.EnterpriseServices.dll", compilationRefNames);
        Assert.DoesNotContain("System.EnterpriseServices.Wrapper.dll", compilationRefNames);
    }

    [Fact]
    public void NoImplicitReferencesWithoutEnterpriseServices()
    {
        WithCompilerCall((builder, compilerCall, arguments) =>
        {
            builder.AddFromDisk(compilerCall, arguments.ToArray());
        }, out var complogStream);

        complogStream.Position = 0;
        using var reader = CompilerLogReader.Create(complogStream, leaveOpen: false);
        var call = reader.ReadCompilerCall(0);
        var refs = reader.ReadAllReferenceData(call);
        Assert.All(refs, r => Assert.False(r.IsImplicit));
    }

    private void WithCompilerCall(Action<CompilerLogBuilder, CompilerCall, IReadOnlyCollection<string>> action, out MemoryStream complogStream)
    {
        complogStream = new MemoryStream();
        using var builder = new CompilerLogBuilder(complogStream, new());
        using var binlogReader = BinaryLogReader.Create(Fixture.SolutionBinaryLogPath);

        var compilerCall = binlogReader
            .ReadAllCompilerCalls(x => x.ProjectFileName == Fixture.ConsoleProjectName)
            .Single();
        action(builder, compilerCall, binlogReader.ReadArguments(compilerCall));
    }

    private static void CreateFakeAssembly(string directory, string fileName)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(fileName);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText($"// {assemblyName}")],
            Net90.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var filePath = Path.Combine(directory, fileName);
        var result = compilation.Emit(filePath);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
