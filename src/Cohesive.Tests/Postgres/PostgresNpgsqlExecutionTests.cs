using Cohesive.Adapters.Postgres;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresNpgsqlExecutionTests
{
    [Fact]
    public async Task BoundedReadersShareOneCumulativeProviderResultBudget()
    {
        var budget = new PostgresNpgsqlResultBudget(maximumBytes: 6);
        using var text = new ChunkedTextReader("A😀", maximumCharactersPerRead: 1);

        var textValue = await PostgresNpgsqlBoundedResult.ReadTextAsync(
            text,
            budget,
            CancellationToken.None);
        var bytesValue = await PostgresNpgsqlBoundedResult.ReadBytesAsync(
            new MemoryStream([0xff]),
            budget,
            CancellationToken.None);

        Assert.Equal("A😀", textValue);
        Assert.Equal([0xff], bytesValue);
        Assert.Equal(6, budget.ConsumedBytes);

        var exception = await Assert.ThrowsAsync<PostgresNpgsqlResultByteLimitExceededException>(async () =>
            await PostgresNpgsqlBoundedResult.ReadBytesAsync(
                new MemoryStream([0x00]),
                budget,
                CancellationToken.None));
        Assert.Equal(6, exception.MaximumBytes);
        Assert.Equal(6, budget.ConsumedBytes);
    }

    [Fact]
    public async Task TextStreamingRejectsOneValueBeforeItCanExceedTheUtf8Budget()
    {
        var budget = new PostgresNpgsqlResultBudget(maximumBytes: 4);
        using var text = new ChunkedTextReader("A😀", maximumCharactersPerRead: 1);

        await Assert.ThrowsAsync<PostgresNpgsqlResultByteLimitExceededException>(async () =>
            await PostgresNpgsqlBoundedResult.ReadTextAsync(
                text,
                budget,
                CancellationToken.None));

        Assert.Equal(1, budget.ConsumedBytes);
    }

    [Fact]
    public async Task ByteStreamingRejectsOneValueWithoutRetainingItsOverBudgetChunk()
    {
        var budget = new PostgresNpgsqlResultBudget(maximumBytes: 4);

        await Assert.ThrowsAsync<PostgresNpgsqlResultByteLimitExceededException>(async () =>
            await PostgresNpgsqlBoundedResult.ReadBytesAsync(
                new MemoryStream([0, 1, 2, 3, 4]),
                budget,
                CancellationToken.None));

        Assert.Equal(0, budget.ConsumedBytes);
    }

    [Fact]
    public async Task StreamingReadPassesCancellationToTheProviderStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellationObservingStream();
        var read = PostgresNpgsqlBoundedResult.ReadBytesAsync(
            stream,
            new PostgresNpgsqlResultBudget(maximumBytes: 10),
            cancellation.Token).AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        Assert.True(stream.ReceivedCancelableToken);
    }

    [Fact]
    public void SourcePolicyBoundsKeysAndRepresentableProviderResults()
    {
        Assert.Equal(256, PostgresRelationQuerySourcePolicy.Default.MaximumKeyBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresRelationQuerySourcePolicy(
            maximumBatchKeys: 1,
            maximumRowsPerRead: 1,
            maximumPageItems: 1,
            maximumPageBytes: (long)Array.MaxLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresRelationQuerySourcePolicy(
            maximumBatchKeys: 1,
            maximumRowsPerRead: 1,
            maximumPageItems: 1,
            maximumPageBytes: 10,
            maximumKeyBytes: 11));
    }

    sealed class ChunkedTextReader(string value, int maximumCharactersPerRead) : TextReader
    {
        int offset;

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == value.Length)
                return ValueTask.FromResult(0);
            var length = Math.Min(
                Math.Min(maximumCharactersPerRead, buffer.Length),
                value.Length - offset);
            value.AsSpan(offset, length).CopyTo(buffer.Span);
            offset += length;
            return ValueTask.FromResult(length);
        }
    }

    sealed class CancellationObservingStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ReceivedCancelableToken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            Started.TrySetResult();
            return AwaitCancellation(cancellationToken);
        }

        static async ValueTask<int> AwaitCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
