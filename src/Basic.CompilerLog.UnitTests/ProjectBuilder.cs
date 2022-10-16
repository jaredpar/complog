using Basic.CompilerLog.Util;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Basic.CompilerLog.UnitTests;

internal sealed class ProjectBuilder : IDisposable
{
    internal string RootDirectory { get; }
    internal string ReferenceDirectory { get; }
    internal string ProjectDirectory { get; }
    internal string ProjectFilePath { get; }
    internal List<string> SourceFilePaths { get; } = new();
    internal List<string> ReferenceFilePaths { get; } = new();

    internal static IEnumerable<Net60.ReferenceInfo> DefaultReferenceInfos => Net60.References.All;
    internal static IEnumerable<PortableExecutableReference> DefaultReferences => Net60.All;
    internal static string DefaultTargetFrameworkMoniker => "net6.0";

    internal ProjectBuilder(string projectFileName, bool includeReferences = true)
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString());
        ReferenceDirectory = CreateSubDir("reference");
        ProjectDirectory = CreateSubDir("project");
        ProjectFilePath = Path.Combine(ProjectDirectory, projectFileName);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ReferenceDirectory);

        File.WriteAllText(ProjectFilePath, "");

        if (includeReferences)
        {
            foreach (var info in DefaultReferenceInfos)
            {
                AddReference(info.FileName, info.ImageBytes);
            }
        }

        string CreateSubDir(string name)
        {
            var path = Path.Combine(RootDirectory, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    internal string AddSourceFile(string fileName, string content)
    {
        var filePath = Path.Combine(ProjectDirectory, fileName);
        File.WriteAllText(filePath, content);
        SourceFilePaths.Add(filePath);
        return filePath;
    }

    internal string AddReference(string fileName, byte[] imageBytes)
    {
        var filePath = Path.Combine(ReferenceDirectory, fileName);
        File.WriteAllBytes(filePath, imageBytes);
        ReferenceFilePaths.Add(filePath);
        return filePath;
    }

    internal CompilerCall CreateCSharpCall()
    {
        var args = new List<string>();

        foreach (var refPath in ReferenceFilePaths)
        {
            args.Add($"/r:{refPath}");
        }

        args.AddRange(SourceFilePaths);

        return new CompilerCall(
            ProjectFilePath,
            CompilerCallKind.Regular,
            DefaultTargetFrameworkMoniker,
            isCSharp: true,
            args.ToArray()); ;
    }

    internal CompilerLogReader GetCompilerLogReader()
    {
        var compilerCall = CreateCSharpCall();
        var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, new());
        builder.Add(compilerCall);
        if (builder.Diagnostics.Count > 0)
        {
            throw new Exception($"Diagnostics building log");
        }

        builder.Close();
        stream.Position = 0;
        return new CompilerLogReader(stream, leaveOpen: true);
    }

    public void Dispose()
    {
        Directory.Delete(RootDirectory, recursive: true);
    }
}
