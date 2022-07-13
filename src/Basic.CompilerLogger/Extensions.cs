using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLogger;

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

    internal static string ReadLineOrThrow(this StreamReader reader)
    {
        if (reader.ReadLine() is { } line)
        {
            return line;
        }

        throw new InvalidOperationException();
    }

    internal static void AddRange<T>(this ImmutableArray<T>.Builder builder, ReadOnlySpan<T> span)
    {
        foreach (var item in span)
        {
            builder.Add(item);
        }
    }

    internal static ImmutableArray<byte> ReadAll(this Stream stream)
    {
        var builder = ImmutableArray.CreateBuilder<byte>();
        Span<byte> buffer = stackalloc byte[256];
        do
        {
            var length = stream.Read(buffer);
            if (length == 0)
                break;

            builder.AddRange(buffer);
        } while (true);

        return builder.ToImmutable();
    }
}
