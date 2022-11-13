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
        // TODO: add generator support

        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var commandLineList = new List<string>();
        var data = Reader.ReadRawCompilationData(compilerCall);
        Directory.CreateDirectory(destinationDir);
        WriteContent(destinationDir, compilerCall, data, commandLineList);
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

        static string ReplacePrefix(string filePath, string oldStart, string newStart)
        {
            var str = filePath.Substring(oldStart.Length);
            if (str.Length > 0 && str[0] == Path.DirectorySeparatorChar)
            {
                str = str.Substring(1);
            }

            return Path.Combine(newStart, str);
        }
    }
}
