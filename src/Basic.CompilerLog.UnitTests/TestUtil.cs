
using System.Reflection;

namespace Basic.CompilerLog.UnitTests;

internal static class TestUtil
{
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