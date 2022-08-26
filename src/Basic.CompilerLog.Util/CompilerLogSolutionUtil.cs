using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

public class CompilerLogSolutionUtil
{
    internal CompilerLogReader Reader { get; }

    public CompilerLogSolutionUtil(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public ProjectInfo GetProjectInfo(int index)
    {
        ProjectInfo.Create(DocumentInfo)
        // Reader.ReadCompilationData()



    }
}
