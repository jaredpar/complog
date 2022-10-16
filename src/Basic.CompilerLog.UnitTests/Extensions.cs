using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
}
