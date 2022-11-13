using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Tool to export compilations to disk for other uses
/// </summary>
public sealed class ExportUtil
{
    public CompilerLogReader Reader { get; }

    public ExportUtil(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public void ExportRsp(CompilerCall compilerCall, string destinationDir)
    {
        // TODO: add path map support

        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var commandLineList = new List<string>();
        var data = Reader.ReadRawCompilationData(compilerCall);
        Directory.CreateDirectory(destinationDir);
        WriteContent(destinationDir, compilerCall, data, commandLineList);
        WriteReferences(destinationDir, compilerCall, data, commandLineList);
        WriteAnalyzers(destinationDir, compilerCall, data, commandLineList);

        // TODO: analyzer configs
        // TODO: additional text
        // TODO: rewrite the RSP file
    }

    private void WriteReferences(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        var refDir = Path.Combine(destinationDir, "ref");
        Directory.CreateDirectory(refDir);

        foreach (var tuple in rawCompilationData.References)
        {
            var mvid = tuple.Mvid;
            var filePath = Path.Combine(refDir, Reader.GetMetadataReferenceFileName(mvid));

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyAssemblyBytes(mvid, fileStream);

            // TODO: need to support aliases here.
            var arg = $@"/reference:""{filePath}""";
            commandLineList.Add(arg);
        }
    }

    private void WriteAnalyzers(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        var analyzerDir = Path.Combine(destinationDir, "analyzers");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var analyzer in rawCompilationData.Analyzers)
        {
            // Group analyzers by the directory they came from. This will ensure it mimics the 
            // setup of the NuGet they came from as this is how the compiler groups them.
            var dir = Path.GetDirectoryName(analyzer.FilePath)!;
            if (!map.TryGetValue(dir, out var outDir))
            {
                outDir = Path.Combine(analyzerDir, $"group{map.Count}");
                Directory.CreateDirectory(outDir);
                map[dir] = outDir;
            }

            var filePath = Path.Combine(outDir, analyzer.FileName);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyAssemblyBytes(analyzer.Mvid, fileStream);

            var arg = $@"/analyzer:""{filePath}""";
            commandLineList.Add(arg);
        }
    }

    private void WriteContent(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        // TODO: add additionalText

        var projectDir = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;
        var srcDir = Path.Combine(destinationDir, "src");
        foreach (var tuple in rawCompilationData.Contents)
        {
            if (tuple.Kind != RawContentKind.SourceText)
            {
                continue;
            }

            if (!tuple.FilePath.StartsWith(projectDir, StringComparison.Ordinal))
            {
                // TODO: handle files outside the project cone
                throw new Exception();
            }

            var filePath = ReplacePrefix(tuple.FilePath, projectDir, srcDir);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyContentTo(tuple.ContentHash, fileStream);
            commandLineList.Add(filePath);
        }
    }

    /// <summary>
    /// Replace the <paramref name="oldStart"/> with <paramref name="newStart"/> inside of
    /// <paramref name="filePath"/>
    /// </summary>
    private static string ReplacePrefix(string filePath, string oldStart, string newStart)
    {
        var str = filePath.Substring(oldStart.Length);
        if (str.Length > 0 && str[0] == Path.DirectorySeparatorChar)
        {
            str = str.Substring(1);
        }

        return Path.Combine(newStart, str);
    }
}
