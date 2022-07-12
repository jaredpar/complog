using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
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
}
