using Quill.Core.Live;
using Xunit;

namespace Quill.Core.Tests.Live;

public sealed class AudioRingBufferTests
{
    [Fact]
    public void KeepsSamplesInOrder()
    {
        var buffer = new AudioRingBuffer(8);

        buffer.Write([1, 2, 3]);
        buffer.Write([4, 5]);

        Assert.Equal(5, buffer.Count);
        Assert.Equal(new float[] { 1, 2, 3, 4, 5 }, buffer.ToArray());
    }

    [Fact]
    public void OverwritesOldestWhenFullAndCountsIt()
    {
        var buffer = new AudioRingBuffer(4);

        buffer.Write([1, 2, 3]);
        buffer.Write([4, 5, 6]);

        Assert.Equal(4, buffer.Count);
        Assert.Equal(2, buffer.OverwrittenSamples);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, buffer.ToArray());
    }

    [Fact]
    public void WriteLargerThanCapacityKeepsTail()
    {
        var buffer = new AudioRingBuffer(3);

        buffer.Write([1, 2, 3, 4, 5]);

        Assert.Equal(new float[] { 3, 4, 5 }, buffer.ToArray());
        Assert.Equal(2, buffer.OverwrittenSamples);
    }

    [Fact]
    public void DiscardOldestWrapsCorrectly()
    {
        var buffer = new AudioRingBuffer(4);
        buffer.Write([1, 2, 3, 4]);
        buffer.DiscardOldest(3);
        buffer.Write([5, 6]);

        Assert.Equal(new float[] { 4, 5, 6 }, buffer.ToArray());

        buffer.DiscardOldest(10);
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.ToArray());
    }

    [Fact]
    public void ClearEmptiesBuffer()
    {
        var buffer = new AudioRingBuffer(4);
        buffer.Write([1, 2]);

        buffer.Clear();
        buffer.Write([9]);

        Assert.Equal(new float[] { 9 }, buffer.ToArray());
    }

    [Fact]
    public void RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRingBuffer(0));
    }
}
