using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal sealed class BasicAdditionalTextFile : AdditionalText
{
    internal SourceText SourceText { get; }
    public override string Path { get; }

    internal BasicAdditionalTextFile(string filePath, SourceText sourceText)
    {
        Path = filePath;
        SourceText = sourceText;
    }

    public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText;
}
