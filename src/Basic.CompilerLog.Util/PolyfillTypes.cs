#if !NET
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullIfNotNullAttribute : Attribute
    {
        public NotNullIfNotNullAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        public string ParameterName { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue)
        {
            ReturnValue = returnValue;
        }

        public bool ReturnValue { get; }
    }
}
#endif
