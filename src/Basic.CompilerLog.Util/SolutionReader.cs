using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

public class SolutionReader
{
    internal CompilerLogReader Reader { get; }

    internal SolutionReader(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public static SolutionReader Create(Stream stream, bool leaveOpen = false) => new (new CompilerLogReader(stream, leaveOpen));

    public static SolutionReader Create(string filePath)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new(new CompilerLogReader(stream, leaveOpen: false));
    }

    public ProjectInfo GetProjectInfo(int index)
    {
        throw null;
        // ProjectInfo.Create(DocumentInfo)
        // Reader.ReadCompilationData()



    }
}
