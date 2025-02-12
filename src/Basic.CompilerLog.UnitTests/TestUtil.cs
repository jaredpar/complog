
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Basic.CompilerLog.UnitTests;

internal static class TestUtil
{
    /// <summary>
    /// Internally a <see cref="IIncrementalGenerator" /> is wrapped in a type called IncrementalGeneratorWrapper. 
    /// This method will dig through that and return the original type.
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    internal static Type GetGeneratorType(object obj)
    {
        var type = obj.GetType();
        if (type.Name == "IncrementalGeneratorWrapper")
        {
            var prop = type.GetProperty(
                "Generator",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            obj = prop.GetMethod!.Invoke(obj, null)!;
        }

        return obj.GetType();
    }
}