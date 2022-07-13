using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
    internal CompilerLogAssemblyLoadContext(string name)
        :base(name, isCollectible: true)
    {
    }

    internal (List<DiagnosticAnalyzer> Analyzers, List<ISourceGenerator> Generators) LoadAnalyzers(string languageName)
    {
        var analyzers = new List<DiagnosticAnalyzer>();
        var generators = new List<ISourceGenerator>();

        foreach (var assembly in Assemblies)
        {
            Load(assembly!);
        }

        return (analyzers, generators);

        void Load(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttributes(inherit: false) is { Length: > 0 } attributes)
                {
                    foreach (var attribute in attributes)
                    {
                        if (attribute is DiagnosticAnalyzerAttribute d &&
                            d.Languages.Contains(languageName))
                        {
                            analyzers.Add((DiagnosticAnalyzer)CreateInstance(type));
                            break;
                        }

                        if (attribute is GeneratorAttribute g &&
                            g.Languages.Contains(languageName))
                        {
                            var generator = CreateInstance(type);
                            if (generator is ISourceGenerator sg)
                            {
                                generators.Add(sg);
                            }
                            else
                            {
                                IIncrementalGenerator ig = (IIncrementalGenerator)generator;
                                generators.Add(ig.AsSourceGenerator());
                            }

                            break;
                        }
                    }
                }
            }
        }

        object CreateInstance(Type type)
        {
            var ctor = type.GetConstructors()
                .Where(c => c.GetParameters().Length == 0)
                .Single();
            return ctor.Invoke(null);
        }
    }
}
