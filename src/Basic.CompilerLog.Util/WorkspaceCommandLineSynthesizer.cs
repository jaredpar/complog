using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Synthesizes a csc/vbc-compatible command line from a Roslyn workspace <see cref="Project"/>.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's <see cref="Workspace"/> API is the IDE/analysis surface — it exposes everything
/// needed for semantic analysis (sources, references, analyzers, options) but deliberately
/// not the emit-time inputs (embedded resources, Win32 manifest/icon/resource, source link,
/// app.config). Those live on <c>Compilation.Emit()</c> parameters and are supplied by the
/// caller, not surfaced on the <see cref="Project"/>.
/// </para>
/// <para>
/// As a result, an rsp synthesized from a workspace is partial: it round-trips the analysis
/// surface faithfully but cannot reproduce a bit-exact assembly for any project that uses
/// emit-time inputs. <see cref="CompilerLogUtil.CreateFromWorkspace(Workspace, string, Func{Project, bool}?, CancellationToken)"/>
/// intentionally does not call into this type — workspace-derived complogs leave their
/// stored command-line empty so <c>complog rsp</c>/<c>replay</c>/<c>export</c> fail visibly
/// rather than misleadingly. Callers willing to accept the partial-fidelity tradeoff can
/// invoke <see cref="Synthesize(Project, Compilation)"/> directly.
/// </para>
/// </remarks>
public static class WorkspaceCommandLineSynthesizer
{
    public static string[] Synthesize(Project project, Compilation compilation)
    {
        var args = new List<string>();
        AddCommonArgs(args, project, compilation);

        if (project.Language == LanguageNames.CSharp)
        {
            AddCSharpArgs(args, project, compilation);
        }
        else
        {
            AddVisualBasicArgs(args, project, compilation);
        }

        AddReferences(args, project);
        AddAnalyzers(args, project);
        AddAdditionalAndConfigFiles(args, project);
        AddSourceFiles(args, project);

        return args.ToArray();
    }

    private static void AddCommonArgs(List<string> args, Project project, Compilation compilation)
    {
        args.Add("/noconfig");

        if (project.OutputFilePath is { Length: > 0 } outputPath)
        {
            args.Add($"/out:{Quote(outputPath)}");
        }

        var target = compilation.Options.OutputKind switch
        {
            OutputKind.ConsoleApplication => "exe",
            OutputKind.WindowsApplication => "winexe",
            OutputKind.DynamicallyLinkedLibrary => "library",
            OutputKind.NetModule => "module",
            OutputKind.WindowsRuntimeMetadata => "winmdobj",
            OutputKind.WindowsRuntimeApplication => "appcontainerexe",
            _ => "library",
        };
        args.Add($"/target:{target}");

        if (compilation.Options.MainTypeName is { Length: > 0 } mainType)
        {
            args.Add($"/main:{mainType}");
        }

        if (compilation.Options.Platform != Platform.AnyCpu)
        {
            args.Add($"/platform:{compilation.Options.Platform.ToString().ToLowerInvariant()}");
        }

        args.Add(compilation.Options.OptimizationLevel == OptimizationLevel.Release ? "/optimize+" : "/optimize-");
        args.Add(compilation.Options.CheckOverflow ? "/checked+" : "/checked-");
        args.Add(compilation.Options.Deterministic ? "/deterministic+" : "/deterministic-");
        args.Add($"/warn:{compilation.Options.WarningLevel}");

        switch (compilation.Options.GeneralDiagnosticOption)
        {
            case ReportDiagnostic.Error:
                args.Add("/warnaserror+");
                break;
            case ReportDiagnostic.Suppress:
                args.Add("/nowarn");
                break;
        }

        AddSpecificDiagnosticOptions(args, compilation.Options.SpecificDiagnosticOptions);

        if (compilation.Options.CryptoKeyFile is { Length: > 0 } keyFile)
        {
            args.Add($"/keyfile:{Quote(keyFile)}");
        }
        if (compilation.Options.CryptoKeyContainer is { Length: > 0 } keyContainer)
        {
            args.Add($"/keycontainer:{keyContainer}");
        }
        if (compilation.Options.DelaySign == true)
        {
            args.Add("/delaysign+");
        }
        if (compilation.Options.PublicSign)
        {
            args.Add("/publicsign+");
        }
    }

    private static void AddSpecificDiagnosticOptions(List<string> args, IReadOnlyDictionary<string, ReportDiagnostic> specifics)
    {
        if (specifics.Count == 0)
        {
            return;
        }

        var nowarn = new List<string>();
        var warnAsError = new List<string>();
        var warnAsWarning = new List<string>();
        foreach (var kvp in specifics)
        {
            switch (kvp.Value)
            {
                case ReportDiagnostic.Suppress:
                    nowarn.Add(kvp.Key);
                    break;
                case ReportDiagnostic.Error:
                    warnAsError.Add(kvp.Key);
                    break;
                case ReportDiagnostic.Warn:
                    warnAsWarning.Add(kvp.Key);
                    break;
            }
        }

        if (nowarn.Count > 0)
        {
            args.Add($"/nowarn:{string.Join(",", nowarn)}");
        }
        if (warnAsError.Count > 0)
        {
            args.Add($"/warnaserror+:{string.Join(",", warnAsError)}");
        }
        if (warnAsWarning.Count > 0)
        {
            args.Add($"/warnaserror-:{string.Join(",", warnAsWarning)}");
        }
    }

    private static void AddCSharpArgs(List<string> args, Project project, Compilation compilation)
    {
        var parseOptions = (project.ParseOptions as CSharpParseOptions) ?? CSharpParseOptions.Default;
        var compilationOptions = (CSharpCompilationOptions)compilation.Options;

        args.Add($"/langversion:{LanguageVersionToFlag(parseOptions.LanguageVersion)}");

        if (parseOptions.PreprocessorSymbolNames.Any())
        {
            args.Add($"/define:{string.Join(";", parseOptions.PreprocessorSymbolNames)}");
        }

        if (compilationOptions.AllowUnsafe)
        {
            args.Add("/unsafe+");
        }

        var nullable = compilationOptions.NullableContextOptions switch
        {
            NullableContextOptions.Enable => "enable",
            NullableContextOptions.Warnings => "warnings",
            NullableContextOptions.Annotations => "annotations",
            NullableContextOptions.Disable => "disable",
            _ => null,
        };
        if (nullable is not null)
        {
            args.Add($"/nullable:{nullable}");
        }
    }

    private static void AddVisualBasicArgs(List<string> args, Project project, Compilation compilation)
    {
        var parseOptions = (project.ParseOptions as VisualBasicParseOptions) ?? VisualBasicParseOptions.Default;
        var compilationOptions = (VisualBasicCompilationOptions)compilation.Options;

        args.Add($"/langversion:{parseOptions.LanguageVersion}");

        if (parseOptions.PreprocessorSymbols.Any())
        {
            var defines = string.Join(",", parseOptions.PreprocessorSymbols.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            args.Add($"/define:{defines}");
        }

        if (compilationOptions.RootNamespace is { Length: > 0 } root)
        {
            args.Add($"/rootnamespace:{root}");
        }

        args.Add($"/optionstrict{(compilationOptions.OptionStrict == Microsoft.CodeAnalysis.VisualBasic.OptionStrict.On ? "+" : "-")}");
        args.Add($"/optionexplicit{(compilationOptions.OptionExplicit ? "+" : "-")}");
        args.Add($"/optioninfer{(compilationOptions.OptionInfer ? "+" : "-")}");
        args.Add($"/optioncompare:{(compilationOptions.OptionCompareText ? "text" : "binary")}");
    }

    private static void AddReferences(List<string> args, Project project)
    {
        foreach (var reference in project.MetadataReferences)
        {
            if (reference is not PortableExecutableReference peRef || peRef.FilePath is null)
            {
                continue;
            }

            var path = Quote(peRef.FilePath);
            var optionName = peRef.Properties.EmbedInteropTypes ? "link" : "reference";
            if (peRef.Properties.Aliases.Length > 0)
            {
                foreach (var alias in peRef.Properties.Aliases)
                {
                    args.Add($"/{optionName}:{alias}={path}");
                }
            }
            else
            {
                args.Add($"/{optionName}:{path}");
            }
        }

        // Project references resolve to on-disk PEs in AddFromWorkspace; capture them here too so
        // that csc replay/rsp/export sees them as plain /reference: arguments rather than vanishing.
        foreach (var projectRef in project.ProjectReferences)
        {
            var dep = project.Solution.GetProject(projectRef.ProjectId);
            if (dep?.OutputFilePath is { Length: > 0 } depOutput)
            {
                args.Add($"/reference:{Quote(depOutput)}");
            }
        }
    }

    private static void AddAnalyzers(List<string> args, Project project)
    {
        foreach (var analyzer in project.AnalyzerReferences)
        {
            if (analyzer is AnalyzerFileReference fileRef)
            {
                args.Add($"/analyzer:{Quote(fileRef.FullPath)}");
            }
        }
    }

    private static void AddAdditionalAndConfigFiles(List<string> args, Project project)
    {
        foreach (var doc in project.AdditionalDocuments)
        {
            if (doc.FilePath is { Length: > 0 } path)
            {
                args.Add($"/additionalfile:{Quote(path)}");
            }
        }

        foreach (var doc in project.AnalyzerConfigDocuments)
        {
            if (doc.FilePath is { Length: > 0 } path)
            {
                args.Add($"/analyzerconfig:{Quote(path)}");
            }
        }
    }

    private static void AddSourceFiles(List<string> args, Project project)
    {
        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is { Length: > 0 } path)
            {
                args.Add(Quote(path));
            }
        }
    }

    /// <summary>
    /// Quote a path for use in a csc/vbc command line if it contains whitespace. Paths without
    /// whitespace are emitted bare to match the typical MSBuild-emitted style.
    /// </summary>
    private static string Quote(string path) =>
        path.IndexOfAny([' ', '\t']) >= 0 ? $"\"{path}\"" : path;

    private static string LanguageVersionToFlag(Microsoft.CodeAnalysis.CSharp.LanguageVersion version) => version switch
    {
        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest => "latest",
        Microsoft.CodeAnalysis.CSharp.LanguageVersion.LatestMajor => "latestmajor",
        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview => "preview",
        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Default => "default",
        _ => version.ToDisplayString(),
    };
}
