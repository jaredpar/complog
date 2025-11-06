namespace Basic.CompilerLog.Util;

public sealed class CompilerLogException(string message, List<string>? diagnostics = null) : Exception(message + (diagnostics is [..] ? ("\n" + string.Join("\n", diagnostics)) : ""))
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics ?? (IReadOnlyList<string>)Array.Empty<string>();
}
