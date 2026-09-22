using Snook.Application;
using Xunit;

namespace Snook.Application.Tests;

public sealed class BoundedStreamTransferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(16384)]
    public async Task NonSeekableShortReadsPreserveExactBytesAtLimit(int size)
    {
        var data = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new NonSeekableShortStream(data);
        await using var target = new MemoryStream();
        Assert.Equal(size, await BoundedStreamTransfer.CopyAsync(source, target, size));
        Assert.Equal(data, target.ToArray());
        Assert.True(source.CanRead);
        Assert.True(target.CanWrite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    public async Task OversizeNeverWritesPastLimit(int limit)
    {
        await using var source = new NonSeekableShortStream(new byte[limit + 1]);
        await using var target = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => BoundedStreamTransfer.CopyAsync(source, target, limit));
        Assert.Equal(limit, target.Length);
    }

    [Fact]
    public async Task CancellationAndInvalidLimitDoNotWrite()
    {
        await using var source = new NonSeekableShortStream([1, 2, 3]);
        await using var target = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedStreamTransfer.CopyAsync(source, target, 3, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedStreamTransfer.CopyAsync(source, target, -1));
        Assert.Equal(0, target.Length);
    }

    private sealed class NonSeekableShortStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 37)], cancellationToken);
    }
}
