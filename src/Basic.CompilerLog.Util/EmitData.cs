using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Data about a compilation that is only interesting at Emit time
/// </summary>
public sealed class EmitData
{
    public Stream? Win32ResourceStream { get; }
    public Stream? SourceLinkStream { get; }
    public IEnumerable<ResourceDescription>? Resources { get; }
    public IEnumerable<EmbeddedText>? EmbeddedTexts { get; }

    public EmitData(
        Stream? win32ResourceStream,
        Stream? sourceLinkStream,
        IEnumerable<ResourceDescription>? resources,
        IEnumerable<EmbeddedText>? embeddedTexts)
    {
        Win32ResourceStream = win32ResourceStream;
        SourceLinkStream = sourceLinkStream;
        Resources = resources;
        EmbeddedTexts = embeddedTexts;
    }
}
