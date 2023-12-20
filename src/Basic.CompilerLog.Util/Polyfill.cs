using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util
{
    internal static class Polyfill
    {
        internal static StreamReader NewStreamReader(Stream stream, Encoding? encoding = null, bool detectEncodingFromByteOrderMarks = true, int bufferSize = -1, bool leaveOpen = false)
        {
#if !NETCOREAPP
            if (bufferSize < 0)
            {
                bufferSize = 1024;
            }
#endif
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks, bufferSize, leaveOpen);
        }

        internal static StreamWriter NewStreamWriter(Stream stream, Encoding? encoding = null, int bufferSize = -1, bool leaveOpen = false)
        {
#if !NETCOREAPP
            if (bufferSize < 0)
            {
                bufferSize = 1024;
            }
#endif
            return new StreamWriter(stream, encoding, bufferSize, leaveOpen);
        }
    }

#if !NETCOREAPP

    internal static class PolyfillExtensions
    {
        internal static string[] Split(this string @this, char separator, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, options);

        internal static string[] Split(this string @this, char separator, int count, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, count, options);

        internal static bool StartsWith(this ReadOnlySpan<char> @this, string value, StringComparison comparisonType) =>
            @this.StartsWith(value.AsSpan(), comparisonType);

        internal static bool Contains(this string @this, string value, StringComparison comparisonType) =>
            @this.IndexOf(value, comparisonType) >= 0;

        internal static bool Contains(this ReadOnlySpan<char> @this, char value) =>
            @this.IndexOf(value) >= 0;

        internal static void ReadExactly(this Stream @this, Span<byte> buffer)
        {
            var bytes = new byte[1024];
            while (buffer.Length > 0)
            {
                var read = @this.Read(bytes, 0, Math.Min(bytes.Length, buffer.Length));
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                bytes.AsSpan(0, read).CopyTo(buffer);
                buffer = buffer.Slice(read);
            }
        }

        internal static void Write(this TextWriter @this, ReadOnlySpan<char> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                @this.Write(buffer[i]);
            }
        }

        internal static void WriteLine(this TextWriter @this, ReadOnlySpan<char> buffer)
        {
            Write(@this, buffer);
            @this.WriteLine();
        }
    }

#endif
}

#if !NETCOREAPP

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Specifies that when a method returns <see cref="ReturnValue"/>, the parameter will not be null even if the corresponding type allows it.</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class NotNullWhenAttribute : Attribute
    {
        /// <summary>Initializes the attribute with the specified return value condition.</summary>
        /// <param name="returnValue">
        /// The return value condition. If the method returns this value, the associated parameter will not be null.
        /// </param>
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        /// <summary>Gets the return value condition.</summary>
        public bool ReturnValue { get; }
    }

    /// <summary>Specifies that the output will be non-null if the named parameter is non-null.</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class NotNullIfNotNullAttribute : Attribute
    {
        /// <summary>Initializes the attribute with the associated parameter name.</summary>
        /// <param name="parameterName">
        /// The associated parameter name.  The output will be non-null if the argument to the parameter specified is non-null.
        /// </param>
        public NotNullIfNotNullAttribute(string parameterName) => ParameterName = parameterName;
    
        /// <summary>Gets the associated parameter name.</summary>
        public string ParameterName { get; }
    }
}

#endif

