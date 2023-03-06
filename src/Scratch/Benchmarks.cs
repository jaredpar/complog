using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable

namespace Scratch;

internal class CompilerBenchmark
{
    public Compilation Compilation { get; set; }

    [Benchmark]
    public void ShadowCopy()
    {

    }

}
