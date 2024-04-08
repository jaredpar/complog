using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal static class ReflectionUtil
{
    internal static T ReadField<T>(object obj, string fieldName, BindingFlags? bindingFlags = null)
    {
        var type = obj.GetType();
        var fieldInfo = type.GetField(fieldName, bindingFlags ?? (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))!;
        var value = fieldInfo.GetValue(obj);
        return (T)value!;
    }

    internal static T ReadProperty<T>(object obj, string fieldName, BindingFlags? bindingFlags = null)
    {
        var type = obj.GetType();
        var propertyInfo = type.GetProperty(fieldName, bindingFlags ?? (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))!;
        var value = propertyInfo.GetValue(obj);
        return (T)value!;
    }
}
