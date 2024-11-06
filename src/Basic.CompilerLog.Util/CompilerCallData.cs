
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Basic.CompilerLog.Util;

public sealed class CompilerCallData(
    CompilerCall compilerCall,
    string assemblyFileName,
    string? outputDirectory,
    ParseOptions parseOptions,
    CompilationOptions compilationOptions,
    EmitOptions emitOptions)
{
    public CompilerCall CompilerCall { get; } = compilerCall;
    public string AssemblyFileName { get; } = assemblyFileName;
    public string? OutputDirectory { get; } = outputDirectory;
    public ParseOptions ParseOptions { get; } = parseOptions;
    public CompilationOptions CompilationOptions { get; } = compilationOptions;
    public EmitOptions EmitOptions { get; } = emitOptions;

    [ExcludeFromCodeCoverage]
    public override string ToString() => CompilerCall.ToString();
}
