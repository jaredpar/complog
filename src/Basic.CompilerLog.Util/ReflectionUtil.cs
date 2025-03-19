using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal static class ReflectionUtil
{
#pragma warning disable IL2075
    internal static T ReadField<T>(object obj, string fieldName, BindingFlags? bindingFlags = null)
    {
        var type = obj.GetType();
        var fieldInfo = type.GetField(fieldName, bindingFlags ?? (BindingFlags.Instance | BindingFlags.NonPublic))!;
        var value = fieldInfo.GetValue(obj);
        return (T)value!;
    }
#pragma warning restore IL2075
}
