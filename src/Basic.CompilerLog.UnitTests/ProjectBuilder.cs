using Basic.Reference.Assemblies;
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
            foreach (var info in Net60.References.All)
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

    public void Dispose()
    {
        Directory.Delete(RootDirectory, recursive: true);
    }
}
