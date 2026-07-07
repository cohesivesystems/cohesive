using Cohesive.Prelude;

namespace Cohesive.Tests.Prelude;

public sealed class ReadOnlyListExtensionsTests
{
    [Fact]
    public void Slice_ReturnsRequestedWindow()
    {
        IReadOnlyList<int> source = [10, 20, 30, 40, 50];

        var slice = source.Slice(1, 3);

        Assert.Equal(3, slice.Count);
        Assert.Equal(20, slice[0]);
        Assert.Equal(30, slice[1]);
        Assert.Equal(40, slice[2]);
    }

    [Fact]
    public void Slice_IsReadOnlyViewOverSource()
    {
        var backing = new List<int> { 1, 2, 3, 4 };
        IReadOnlyList<int> source = backing;
        var slice = source.Slice(1, 2);

        backing[1] = 20;
        backing[2] = 30;

        Assert.Equal(20, slice[0]);
        Assert.Equal(30, slice[1]);
    }

    [Fact]
    public void Slice_InvalidRange_Throws()
    {
        IReadOnlyList<int> source = [1, 2, 3];

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Slice(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Slice(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Slice(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Slice(2, 2));
    }

    [Fact]
    public void Skip_ReturnsNonCopyingSlice()
    {
        var backing = new List<int> { 1, 2, 3, 4 };
        IReadOnlyList<int> source = backing;

        var skipped = source.Skip(2);

        Assert.Equal(2, skipped.Count);
        Assert.Equal(3, skipped[0]);
        Assert.Equal(4, skipped[1]);

        backing[2] = 30;
        Assert.Equal(30, skipped[0]);
    }

    [Fact]
    public void Skip_ClampsLikeLinq()
    {
        IReadOnlyList<int> source = [1, 2, 3];

        var negative = source.Skip(-5);
        var over = source.Skip(10);

        Assert.Equal(source.Count, negative.Count);
        Assert.Empty(over);
    }

    [Fact]
    public void Take_ReturnsNonCopyingSlice()
    {
        var backing = new List<int> { 1, 2, 3, 4 };
        IReadOnlyList<int> source = backing;

        var taken = source.Take(2);

        Assert.Equal(2, taken.Count);
        Assert.Equal(1, taken[0]);
        Assert.Equal(2, taken[1]);

        backing[0] = 10;
        Assert.Equal(10, taken[0]);
    }

    [Fact]
    public void Take_ClampsLikeLinq()
    {
        IReadOnlyList<int> source = [1, 2, 3];

        var negative = source.Take(-5);
        var over = source.Take(10);

        Assert.Empty(negative);
        Assert.Equal(source.Count, over.Count);
    }

    [Fact]
    public void NestedSlice_ComposesAgainstOriginalSource()
    {
        var backing = new List<int> { 1, 2, 3, 4, 5, 6 };
        IReadOnlyList<int> source = backing;

        var nested = source.Slice(1, 4).Slice(1, 2);

        Assert.Equal(2, nested.Count);
        Assert.Equal(3, nested[0]);
        Assert.Equal(4, nested[1]);

        backing[2] = 99;
        Assert.Equal(99, nested[0]);
    }
}
