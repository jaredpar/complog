using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal static class Extensions
{
    internal static ZipArchiveEntry GetEntryOrThrow(this ZipArchive zipArchive, string name)
    {
        var entry = zipArchive.GetEntry(name);
        if (entry is null)
            throw new InvalidOperationException($"Could not find entry with name {name}");
        return entry;
    }

    internal static Stream OpenEntryOrThrow(this ZipArchive zipArchive, string name)
    {
        var entry = GetEntryOrThrow(zipArchive, name);
        return entry.Open();
    }

    internal static byte[] ReadAllBytes(this ZipArchive zipArchive, string name)
    {
        var entry = GetEntryOrThrow(zipArchive, name);
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes.AsSpan());
        return bytes;
    }

    internal static string ReadLineOrThrow(this StreamReader reader)
    {
        if (reader.ReadLine() is { } line)
        {
            return line;
        }

        throw new InvalidOperationException();
    }

    internal static void AddRange<T>(this List<T> list, ReadOnlySpan<T> span)
    {
        foreach (var item in span)
        {
            list.Add(item);
        }
    }

    /// <summary>
    /// Creates a <see cref="MemoryStream"/> that is a simple wrapper around the array. The intent
    /// is for consumers to be able to access the underlying array via <see cref="MemoryStream.TryGetBuffer(out ArraySegment{byte})"/>
    /// and similar methods.
    /// </summary>
    internal static MemoryStream AsSimpleMemoryStream(this byte[] array) =>
        new MemoryStream(array, 0, count: array.Length, writable: true, publiclyVisible: true);

    internal static string GetResourceName(this ResourceDescription d) => ReflectionUtil.ReadField<string>(d, "ResourceName");
    internal static string? GetFileName(this ResourceDescription d) => ReflectionUtil.ReadField<string?>(d, "FileName");
    internal static bool IsPublic(this ResourceDescription d) => ReflectionUtil.ReadField<bool>(d, "IsPublic");
    internal static Func<Stream> GetDataProvider(this ResourceDescription d) => ReflectionUtil.ReadField<Func<Stream>>(d, "DataProvider");
}
