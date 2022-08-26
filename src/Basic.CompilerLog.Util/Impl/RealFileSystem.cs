using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

internal sealed class RealFileSystem : IFileSystem
{
    internal static readonly RealFileSystem Instance = new RealFileSystem();

    private RealFileSystem()
    {

    }

    public Stream Open(string filePath, FileMode fileMode, FileAccess fileAccess, FileShare fileShare) =>
        new FileStream(filePath, fileMode, fileAccess, fileShare);
}
