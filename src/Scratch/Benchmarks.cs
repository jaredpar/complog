using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable

namespace Scratch;

public  class CompilerBenchmark
{
    public string TempDirectory { get; set; }
    public CompilerLogReader Reader { get; set; }

    [GlobalSetup]
    public void GenerateLog()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerBenchmark), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);
        DotnetUtil.Command($"new console --name example --output .", TempDirectory);
        DotnetUtil.Command($"build -bl", TempDirectory);
        Reader = CompilerLogReader.Create(Path.Combine(TempDirectory, "msbuild.binlog"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Reader.Dispose();
        Directory.Delete(TempDirectory, recursive: true);
    }

    [ParamsAllValues]
    public BasicAnalyzersOptions Options { get; set; }

    [Benchmark]
    public void Emit()
    {
        var data = Reader.ReadCompilationData(0, Options);
        var compilation = data.GetCompilationAfterGenerators();
        var stream = new MemoryStream();
        var result = compilation.Emit(
            stream,
            options: data.EmitOptions);
        if (!result.Success)
        {
            throw new Exception("compilation failed");
        }
        data.BasicAnalyzers.Dispose();
    }
}
