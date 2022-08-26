using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal interface IFileSystem
{
    Stream Open(string filePath, FileMode fileMode, FileAccess fileAccess, FileShare fileShare);
}

