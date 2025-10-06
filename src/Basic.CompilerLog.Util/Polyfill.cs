﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util
{
    internal static class Polyfill
    {
        internal static StreamReader NewStreamReader(Stream stream, Encoding? encoding = null, bool detectEncodingFromByteOrderMarks = true, int bufferSize = -1, bool leaveOpen = false)
        {
#if !NET
            if (bufferSize < 0)
            {
                bufferSize = 1024;
            }
#endif
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks, bufferSize, leaveOpen);
        }

        internal static StreamWriter NewStreamWriter(Stream stream, Encoding? encoding = null, int bufferSize = -1, bool leaveOpen = false)
        {
#if !NET
            if (bufferSize < 0)
            {
                bufferSize = 1024;
            }
#endif
            return new StreamWriter(stream, encoding, bufferSize, leaveOpen);
        }

        internal static unsafe ref T GetNonNullPinnableReference<T>(Span<T> span) => 
            ref (span.Length != 0) 
                ? ref MemoryMarshal.GetReference(span)
                : ref Unsafe.AsRef<T>((void*)1);

        internal static unsafe ref T GetNonNullPinnableReference<T>(ReadOnlySpan<T> span) => 
            ref (span.Length != 0) 
                ? ref MemoryMarshal.GetReference(span)
                : ref Unsafe.AsRef<T>((void*)1);

#if !NET
        /// <summary>
        /// Gets a relative path from one directory to another for frameworks that don't have Path.GetRelativePath
        /// </summary>
        internal static string GetRelativePath(string relativeTo, string path)
        {
            // Normalize paths
            relativeTo = Path.GetFullPath(relativeTo);
            path = Path.GetFullPath(path);

            // If they're on different drives (Windows), can't create relative path
            if (Path.IsPathRooted(relativeTo) && Path.IsPathRooted(path))
            {
                var relativeToRoot = Path.GetPathRoot(relativeTo);
                var pathRoot = Path.GetPathRoot(path);
                if (!string.Equals(relativeToRoot, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            var relativeToParts = relativeTo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathParts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Find common prefix
            int commonLength = 0;
            int minLength = Math.Min(relativeToParts.Length, pathParts.Length);
            for (int i = 0; i < minLength; i++)
            {
                if (!string.Equals(relativeToParts[i], pathParts[i], StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                commonLength = i + 1;
            }

            // Build the relative path
            var result = new StringBuilder();
            
            // Add .. for each directory in relativeTo that's not in the common prefix
            for (int i = commonLength; i < relativeToParts.Length; i++)
            {
                if (result.Length > 0)
                {
                    result.Append(Path.DirectorySeparatorChar);
                }
                result.Append("..");
            }

            // Add the remaining parts from path
            for (int i = commonLength; i < pathParts.Length; i++)
            {
                if (result.Length > 0)
                {
                    result.Append(Path.DirectorySeparatorChar);
                }
                result.Append(pathParts[i]);
            }

            return result.Length == 0 ? "." : result.ToString();
        }
#else
        internal static string GetRelativePath(string relativeTo, string path) =>
            Path.GetRelativePath(relativeTo, path);
#endif
    }

#if !NET

    internal static partial class PolyfillExtensions
    {
        internal static string[] Split(this string @this, char separator, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, options);

        internal static string[] Split(this string @this, char separator, int count, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, count, options);

        internal static void Append(this StringBuilder @this, ReadOnlySpan<char> value)
        {
            foreach (var c in value)
            {
                @this.Append(c);
            }
        }

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

        internal static unsafe int GetByteCount(this Encoding @this, ReadOnlySpan<char> chars)
        {
            fixed (char* charsPtr = &Polyfill.GetNonNullPinnableReference(chars))
            {
                return @this.GetByteCount(charsPtr, chars.Length);
            }
        }

        internal static unsafe int GetBytes(this Encoding @this, ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            fixed (char* charsPtr = &Polyfill.GetNonNullPinnableReference(chars))
            fixed (byte* bytesPtr = &Polyfill.GetNonNullPinnableReference(bytes))
            {
                return @this.GetBytes(charsPtr, chars.Length, bytesPtr, bytes.Length);
            }
        }

        internal static bool IsMatch(this Regex @this, ReadOnlySpan<char> input) =>
            @this.IsMatch(input.ToString());
    }

#endif

    internal static partial class PolyfillExtensions
    {
#if !NET9_0_OR_GREATER
        internal static IEnumerable<(int Index, T Item)> Index<T>(this IEnumerable<T> @this) =>
            @this.Select((item, index) => (index, item));
#endif
    }

}

#if !NET

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Specifies that when a method returns <see cref="ReturnValue"/>, the parameter will not be null even if the corresponding type allows it.</summary>
    [ExcludeFromCodeCoverage]
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
    [ExcludeFromCodeCoverage]
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

