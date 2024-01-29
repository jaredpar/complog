using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

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
}
