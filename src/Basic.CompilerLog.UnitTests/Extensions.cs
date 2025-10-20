using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Runner.Common;
using Xunit.Sdk;
using AssemblyMetadata=Microsoft.CodeAnalysis.AssemblyMetadata;

namespace Basic.CompilerLog.UnitTests;

internal static class Extensions
{
    internal static Guid GetModuleVersionId(this MetadataReference reference)
    {
        if (reference is PortableExecutableReference peReference &&
            peReference.GetMetadata() is AssemblyMetadata metadata &&
            metadata.GetModules() is { Length: > 0 } modules)
        {
            var module = modules[0];
            return module.GetModuleVersionId();
        }

        throw new Exception($"Cannot get MVID from reference {reference.Display}");
    }

    internal static void OnDiagnosticMessage(this IMessageSink messageSink, string message)
    {
        messageSink.OnMessage(new DiagnosticMessage(message));
    }

    internal static void ForEach<T>(this IEnumerable<T> enumerable, Action<T> action)
    {
        foreach (var item in enumerable)
        {
            action(item);
        }
    }

    internal static CompilerCall WithOwner(this CompilerCall compilerCall, object? ownerState)
    {
        return new CompilerCall(
            compilerCall.ProjectFilePath,
            compilerCall.Kind,
            compilerCall.TargetFramework,
            compilerCall.IsCSharp,
            compilerCall.CompilerFilePath,
            ownerState);
    }

    internal static CompilationData WithBasicAnalyzerHost(this CompilationData compilationData, BasicAnalyzerHost basicAnalyzerHost) =>
        compilationData switch
        {
            CSharpCompilationData cs =>
                new CSharpCompilationData(
                    cs.CompilerCall,
                    cs.Compilation,
                    cs.ParseOptions,
                    cs.EmitOptions,
                    cs.EmitData,
                    cs.AdditionalTexts,
                    basicAnalyzerHost,
                    cs.AnalyzerConfigOptionsProvider,
                    cs.CreationDiagnostics),
            VisualBasicCompilationData vb =>
                new VisualBasicCompilationData(
                    vb.CompilerCall,
                    vb.Compilation,
                    vb.ParseOptions,
                    vb.EmitOptions,
                    vb.EmitData,
                    vb.AdditionalTexts,
                    basicAnalyzerHost,
                    vb.AnalyzerConfigOptionsProvider,
                    vb.CreationDiagnostics),
            _ => throw new NotSupportedException($"Unsupported compilation data type: {compilationData.GetType()}")
        };

    internal static List<byte> ReadAllBytes(this Stream stream)
    {
        var list = new List<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                list.AddRange(buffer.AsSpan(0, bytesRead));
            }
            return list;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
