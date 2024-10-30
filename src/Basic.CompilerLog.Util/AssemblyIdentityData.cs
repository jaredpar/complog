using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

public sealed class AssemblyIdentityData(Guid mvid, string? assemblyName, string? assemblyInformationalVersion)
{
    public Guid Mvid { get; } = mvid;
    public string? AssemblyName { get; } = assemblyName;
    public string? AssemblyInformationalVersion { get; } = assemblyInformationalVersion;
}
