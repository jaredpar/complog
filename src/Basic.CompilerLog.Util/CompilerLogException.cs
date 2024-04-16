using Microsoft.Build.Logging.StructuredLogger;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogException(string message, List<string>? diagnostics = null) : Exception(message)
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics ?? (IReadOnlyList<string>)Array.Empty<string>();
}
