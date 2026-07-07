using System.Buffers;
using Cohesive.Prelude;

namespace Cohesive.Tests.Prelude;

public sealed class ValueStringBuilderTests
{
    [Fact]
    public void Append_ComposesCharsStringsAndSpans()
    {
        Span<char> initialBuffer = stackalloc char[32];
        var builder = new ValueStringBuilder(initialBuffer);

        builder.Append('a');
        builder.Append("bc");
        builder.Append("def".AsSpan());

        Assert.Equal(6, builder.Length);
        Assert.Equal("abcdef", builder.AsSpan().ToString());
    }

    [Fact]
    public void Append_WhenCapacityExceeded_RentsAndReturnsPooledBufferOnDispose()
    {
        var pool = new TrackingArrayPool();
        Span<char> initialBuffer = stackalloc char[2];
        var builder = new ValueStringBuilder(initialBuffer, pool);

        builder.Append("abcd");

        Assert.NotNull(pool.LastRented);
        Assert.Equal("abcd", builder.AsSpan().ToString());

        builder.Dispose();

        Assert.Equal(1, pool.ReturnCount);
        Assert.Same(pool.LastRented, pool.LastReturned);
    }

    [Fact]
    public void ToString_ReturnsContentAndDisposesBuilder()
    {
        var pool = new TrackingArrayPool();
        Span<char> initialBuffer = stackalloc char[1];
        var builder = new ValueStringBuilder(initialBuffer, pool);

        builder.Append("hello");

        var text = builder.ToString();

        Assert.Equal("hello", text);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Same(pool.LastRented, pool.LastReturned);
    }

    [Fact]
    public void Clear_ResetsLengthAndAllowsReuse()
    {
        Span<char> initialBuffer = stackalloc char[8];
        var builder = new ValueStringBuilder(initialBuffer);

        builder.Append("hello");
        builder.Clear();
        builder.Append("ok");

        Assert.Equal(2, builder.Length);
        Assert.Equal("ok", builder.AsSpan().ToString());
    }

    [Fact]
    public void CopyTo_CopiesWrittenCharacters()
    {
        Span<char> initialBuffer = stackalloc char[8];
        var builder = new ValueStringBuilder(initialBuffer);
        builder.Append("copy");
        Span<char> destination = stackalloc char[8];
        destination.Fill('_');

        builder.CopyTo(destination);

        Assert.Equal("copy", destination[..builder.Length].ToString());
        Assert.Equal("____", destination[builder.Length..].ToString());
    }

    sealed class TrackingArrayPool : ArrayPool<char>
    {
        public char[]? LastRented { get; private set; }

        public char[]? LastReturned { get; private set; }

        public int ReturnCount { get; private set; }

        public override char[] Rent(int minimumLength)
        {
            LastRented = new char[minimumLength];
            return LastRented;
        }

        public override void Return(char[] array, bool clearArray = false)
        {
            LastReturned = array;
            ReturnCount++;

            if (clearArray)
                Array.Clear(array);
        }
    }
}
