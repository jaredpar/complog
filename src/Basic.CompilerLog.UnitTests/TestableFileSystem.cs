using Basic.CompilerLog.Util;
using System.Text;
using static Basic.Reference.Assemblies.Net60;

namespace Basic.CompilerLog.UnitTests;

internal sealed class TestableFileSystem : IFileSystem
{
    internal readonly Dictionary<string, byte[]> FileContentMap = new();
    internal readonly Encoding Encoding = Encoding.UTF8;

    public Stream Open(string filePath, FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        var content = FileContentMap[filePath];
        return new MemoryStream(content);
    }

    public void AddSourceFile(string filePath, string content)
    {
        FileContentMap.Add(filePath, Encoding.GetBytes(content));
    }

    public void AddReference(string referenceName, byte[] imageBytes)
    {
        FileContentMap.Add(referenceName, imageBytes);
    }
}
