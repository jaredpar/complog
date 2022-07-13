using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLogger;

internal static class CommonUtil
{
    internal const string MetadataFileName = "metadat.txt";
    internal static readonly Encoding ContentEncoding = Encoding.UTF8;

    internal static string GetCompilerEntryName(int index) => $"compilations/{index}.txt";
    internal static string GetAssemblyEntryName(Guid mvid) => $"assembly/{mvid:N}";
    internal static string GetContentEntryName(string contentHash) => $"content/{contentHash}";
}
