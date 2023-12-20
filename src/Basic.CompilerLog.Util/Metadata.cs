using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

internal sealed class Metadata
{
    internal static readonly int LatestMetadataVersion = 2;

    internal int MetadataVersion { get; }
    internal int Count { get; }
    internal bool IsWindows { get; }

    private Metadata(
        int metadataVersion,
        int count, 
        bool isWindows)
    {
        MetadataVersion = metadataVersion;
        Count = count;
        IsWindows = isWindows;
    }

    internal static Metadata Create(int count) =>
        new Metadata(
            LatestMetadataVersion,
            count,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    internal static Metadata Read(TextReader reader)
    {
        try
        {
            var line = reader.ReadLineOrThrow();
            if (line.StartsWith("count", StringComparison.Ordinal))
            {
                // This is a version 0, there is just a count method
                var count = ParseLine(line, "count", int.Parse);
                return new Metadata(metadataVersion: 0, count, isWindows: true);
            }
            else
            {
                var metadataVersion = ParseLine(line, "version", int.Parse);
                var count = ParseLine(reader.ReadLineOrThrow(), "count", int.Parse);
                var isWindows = ParseLine(reader.ReadLineOrThrow(), "windows", bool.Parse);
                return new Metadata(
                    metadataVersion,
                    count,
                    isWindows);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot parse metadata", ex);
        }

        T ParseLine<T>(string line, string label, Func<string, T> func)
        {
            var items = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (items.Length != 2 || items[0] != label)
                throw new InvalidOperationException("Line has wrong format");

            return func(items[1]);
        }
    }

    internal void Write(StreamWriter writer)
    {
        writer.WriteLine($"version:{MetadataVersion}");
        writer.WriteLine($"count:{Count}");
        writer.WriteLine($"windows:{RuntimeInformation.IsOSPlatform(OSPlatform.Windows)}");
    }
}
