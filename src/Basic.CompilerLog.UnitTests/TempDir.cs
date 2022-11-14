using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.UnitTests;

internal sealed class TempDir : IDisposable
{
    internal string DirectoryPath { get; }

    public TempDir(string? name = null)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "Basic.CompilerLog", Guid.NewGuid().ToString());
        if (name != null)
        {
            DirectoryPath = Path.Combine(DirectoryPath, name);
        }

        Directory.CreateDirectory(DirectoryPath);
    }

    public void Dispose()
    {
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
