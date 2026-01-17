using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.UnitTests;

public class PolyfillTests
{
    [Fact]
    public void ReadExactlyTooMany()
    {
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3], 0, 3);

        byte[] buffer = new byte[10];
        Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(buffer.AsSpan()));
    }

    [Fact]
    public void WriteSimple()
    {
        var writer = new StringWriter();
        ReadOnlySpan<char> span = "hello".AsSpan();
        writer.Write(span);
        Assert.Equal("hello", writer.ToString());
    }

    [Fact]
    public void WriteLineSimple()
    {
        var writer = new StringWriter();
        ReadOnlySpan<char> span = "hello".AsSpan();
        writer.WriteLine(span);
        Assert.Equal("hello" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void ContainsCharSimple()
    {
        var span = "test".AsSpan();
        Assert.True(span.Contains('e'));
        Assert.False(span.Contains('f'));
    }

    [Fact]
    public void GetByteCountEmpty()
    {
        Assert.Equal(0, TestBase.DefaultEncoding.GetByteCount(ReadOnlySpan<char>.Empty));
        Assert.Equal(0, TestBase.DefaultEncoding.GetByteCount((ReadOnlySpan<char>)default));
    }

    [Fact]
    public void GetBytesCountEmpty()
    {
        var buffer = new byte[10];
        Assert.Equal(0, TestBase.DefaultEncoding.GetBytes(ReadOnlySpan<char>.Empty, buffer.AsSpan()));
        Assert.Throws<ArgumentException>(() => TestBase.DefaultEncoding.GetBytes("hello".AsSpan(), buffer.AsSpan(0, 0)));
    }

    [Fact]
    public void ConcatTwoSpans()
    {
        ReadOnlySpan<char> span1 = "hello".AsSpan();
        ReadOnlySpan<char> span2 = " world".AsSpan();
        var result = string.Concat(span1, span2);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ConcatTwoSpansEmpty()
    {
        ReadOnlySpan<char> span1 = ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span2 = ReadOnlySpan<char>.Empty;
        var result = string.Concat(span1, span2);
        Assert.Equal("", result);
    }

    [Fact]
    public void ConcatThreeSpans()
    {
        ReadOnlySpan<char> span1 = "hello".AsSpan();
        ReadOnlySpan<char> span2 = " ".AsSpan();
        ReadOnlySpan<char> span3 = "world".AsSpan();
        var result = string.Concat(span1, span2, span3);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ConcatThreeSpansWithEmpty()
    {
        ReadOnlySpan<char> span1 = "foo".AsSpan();
        ReadOnlySpan<char> span2 = ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span3 = "bar".AsSpan();
        var result = string.Concat(span1, span2, span3);
        Assert.Equal("foobar", result);
    }

    [Fact]
    public void ConcatThreeSpansAllEmpty()
    {
        ReadOnlySpan<char> span1 = ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span2 = ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> span3 = ReadOnlySpan<char>.Empty;
        var result = string.Concat(span1, span2, span3);
        Assert.Equal("", result);
    }
}
