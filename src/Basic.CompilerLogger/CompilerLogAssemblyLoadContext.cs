using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Basic.CompilerLogger;

/// <summary>
/// This is used to load the analyzers from the compiler log file
///
/// TODO: consider handling the nested load context scenarios
/// </summary>
internal sealed class CompilerLogAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// The set of assembly names passed via /analyzer
    /// </summary>
    internal List<AssemblyName> Analyzers { get; }

    internal CompilerLogAssemblyLoadContext(
        string name,
        List<AssemblyName> analyzers)
        :base(name, isCollectible: true)
    {
        Analyzers = analyzers;
    }
}
