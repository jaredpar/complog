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
    public Stream? Win32IconStream { get; }
    public Stream? SourceLinkStream { get; }

    public EmitData(
        Stream? win32ResourceStream,
        Stream? win32IconStream,
        Stream? sourceLinkStream
        )
    {
        Win32ResourceStream = win32ResourceStream;
        Win32IconStream = win32IconStream;
        SourceLinkStream = sourceLinkStream;
    }
}
