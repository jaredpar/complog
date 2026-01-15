using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// This type houses polyfills that can't be expressed with extension everything.
/// </summary>
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
}

/// <summary>
/// This type houses polyfill .NET behavior with extension methods.
/// </summary>
internal static class PolyfillExtensions
{
#if !NET
    extension (string @this)
    {
        public static string Concat(ReadOnlySpan<char> arg1, ReadOnlySpan<char> arg2) =>
            Concat(arg1, arg2, "");

        public static string Concat(ReadOnlySpan<char> arg1, ReadOnlySpan<char> arg2, ReadOnlySpan<char> arg3)
        {
            var array = ArrayPool<char>.Shared.Rent(arg1.Length + arg2.Length + arg3.Length);
            var span = array.AsSpan();
            arg1.CopyTo(span[0..arg1.Length]);
            arg2.CopyTo(span[arg1.Length..arg2.Length]);
            arg3.CopyTo(span[(arg1.Length + arg2.Length)..arg3.Length]);
            var result = new string(array);
            ArrayPool<char>.Shared.Return(array);
            return result;
        }

        public string[] Split(char separator, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, options);

        public string[] Split(char separator, int count, StringSplitOptions options = StringSplitOptions.None) =>
            @this.Split(new char[] { separator }, count, options);
    }

    extension (MemoryMarshal)
    {
        public static unsafe ref T GetNonNullPinnableReference<T>(Span<T> span) =>
            ref (span.Length != 0)
                ? ref MemoryMarshal.GetReference(span)
                : ref Unsafe.AsRef<T>((void*)1);

        public static unsafe ref T GetNonNullPinnableReference<T>(ReadOnlySpan<T> span) =>
            ref (span.Length != 0)
                ? ref MemoryMarshal.GetReference(span)
                : ref Unsafe.AsRef<T>((void*)1);
    }

    extension  (StringBuilder @this)
    {
        public void Append(ReadOnlySpan<char> value)
        {
            foreach (var c in value)
            {
                @this.Append(c);
            }
        }
    }

    extension (ReadOnlySpan<char> @this)
    {
        internal bool StartsWith(string value, StringComparison comparisonType) =>
            @this.StartsWith(value.AsSpan(), comparisonType);

        internal bool Contains(string value, StringComparison comparisonType) =>
            @this.IndexOf(value, comparisonType) >= 0;

        internal bool Contains(char value) =>
            @this.IndexOf(value) >= 0;
    }

    extension (Stream @this)
    {
        public void ReadExactly(Span<byte> buffer)
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
    }

    extension (TextWriter @this)
    {
        public void Write(ReadOnlySpan<char> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                @this.Write(buffer[i]);
            }
        }

        public void WriteLine(ReadOnlySpan<char> buffer)
        {
            Write(@this, buffer);
            @this.WriteLine();
        }
    }

    extension (Encoding @this)
    {
        public unsafe int GetByteCount(ReadOnlySpan<char> chars)
        {
            fixed (char* charsPtr = &MemoryMarshal.GetNonNullPinnableReference(chars))
            {
                return @this.GetByteCount(charsPtr, chars.Length);
            }
        }

        public unsafe int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            fixed (char* charsPtr = &MemoryMarshal.GetNonNullPinnableReference(chars))
            fixed (byte* bytesPtr = &MemoryMarshal.GetNonNullPinnableReference(bytes))
            {
                return @this.GetBytes(charsPtr, chars.Length, bytesPtr, bytes.Length);
            }
        }
    }

    extension (Regex @this)
    {
        public bool IsMatch(ReadOnlySpan<char> input) =>
            @this.IsMatch(input.ToString());
    }

#endif
}
