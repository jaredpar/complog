using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util;

internal static class CommonUtil
{
    internal const string MetadataFileName = "metadata.txt";
    internal const string AssemblyInfoFileName = "assemblyinfo.txt";
    internal static readonly Encoding ContentEncoding = Encoding.UTF8;
    internal static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard.WithAllowAssemblyVersionMismatch(true);

    internal static string GetCompilerEntryName(int index) => $"compilations/{index}.txt";
    internal static string GetAssemblyEntryName(Guid mvid) => $"assembly/{mvid:N}";
    internal static string GetContentEntryName(string contentHash) => $"content/{contentHash}";

#if NETCOREAPP

    internal static AssemblyLoadContext GetAssemblyLoadContext(AssemblyLoadContext? context)
    {
        if (context is { })
        {
            return context;
        }

        if (AssemblyLoadContext.GetLoadContext(typeof(CommonUtil).Assembly) is { } current)
        {
            return current;
        }

        return AssemblyLoadContext.Default;
    }

#endif

    internal static string GetAssemblyFileName(CommandLineArguments arguments)
    {
        if (arguments.OutputFileName is not null)
        {
            return arguments.OutputFileName;
        }

        string name = arguments.CompilationName ?? "app";
        return Path.GetExtension(name) switch
        {
            ".dll" => name,
            ".netmodule" => name,
            _ => $"{name}{GetStandardAssemblyExtension()}"
        };

        string GetStandardAssemblyExtension() => arguments.CompilationOptions.OutputKind switch
        {
            OutputKind.NetModule => ".netmodule",
            OutputKind.ConsoleApplication => ".exe",
            OutputKind.WindowsApplication => ".exe",
            _ => ".dll"
        };
    }
}
