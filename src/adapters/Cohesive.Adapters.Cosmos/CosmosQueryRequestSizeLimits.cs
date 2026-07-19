using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Explicit UTF-8 size boundaries for one Cosmos SQL request.</summary>
public sealed record CosmosQueryRequestSizeLimits
{
    /// <summary>Conventional maximum Cosmos SQL query-text length in UTF-8 bytes.</summary>
    public const int DefaultMaximumSqlQueryUtf8Bytes = 512 * 1024;

    /// <summary>Conventional maximum complete Cosmos request size in UTF-8 bytes.</summary>
    public const int DefaultMaximumRequestUtf8Bytes = 2 * 1024 * 1024;

    /// <summary>Creates explicit request-size boundaries.</summary>
    /// <param name="maximumSqlQueryUtf8Bytes">Positive maximum UTF-8 byte length of SQL query text.</param>
    /// <param name="maximumRequestUtf8Bytes">
    /// Positive maximum conservative UTF-8 request estimate, including query text and serialized parameters.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied boundary is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="maximumRequestUtf8Bytes"/> is smaller than
    /// <paramref name="maximumSqlQueryUtf8Bytes"/>.
    /// </exception>
    public CosmosQueryRequestSizeLimits(
        int maximumSqlQueryUtf8Bytes = DefaultMaximumSqlQueryUtf8Bytes,
        int maximumRequestUtf8Bytes = DefaultMaximumRequestUtf8Bytes)
    {
        if (maximumSqlQueryUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSqlQueryUtf8Bytes),
                maximumSqlQueryUtf8Bytes,
                "A Cosmos SQL query-text boundary must be positive.");
        }
        if (maximumRequestUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRequestUtf8Bytes),
                maximumRequestUtf8Bytes,
                "A Cosmos request-size boundary must be positive.");
        }
        if (maximumRequestUtf8Bytes < maximumSqlQueryUtf8Bytes)
        {
            throw new ArgumentException(
                "A Cosmos request-size boundary cannot be smaller than its SQL query-text boundary.",
                nameof(maximumRequestUtf8Bytes));
        }

        MaximumSqlQueryUtf8Bytes = maximumSqlQueryUtf8Bytes;
        MaximumRequestUtf8Bytes = maximumRequestUtf8Bytes;
    }

    /// <summary>Maximum UTF-8 byte length of SQL query text.</summary>
    public int MaximumSqlQueryUtf8Bytes { get; }

    /// <summary>Maximum conservative UTF-8 request estimate.</summary>
    public int MaximumRequestUtf8Bytes { get; }
}

/// <summary>Shared pre-I/O validation of bounded Cosmos SQL command size.</summary>
internal static class CosmosQueryRequestSizeValidator
{
    const int RequestEnvelopeReserveBytes = 4 * 1024;
    const int ParameterEnvelopeReserveBytes = 64;

    /// <summary>Validates a bound SDK query against explicit UTF-8 request limits.</summary>
    /// <param name="query">Bound Cosmos SDK query definition.</param>
    /// <param name="limits">Explicit SQL-text and complete-request boundaries.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="query"/> or <paramref name="limits"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CosmosQueryRequestSizeLimitException">
    /// Query text or the conservative complete-request estimate exceeds its configured boundary, or a parameter
    /// cannot be measured deterministically before I/O.
    /// </exception>
    internal static void RequireWithin(
        QueryDefinition query,
        CosmosQueryRequestSizeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(limits);
        var queryBytes = Encoding.UTF8.GetByteCount(query.QueryText);
        if (queryBytes > limits.MaximumSqlQueryUtf8Bytes)
        {
            throw new CosmosQueryRequestSizeLimitException(
                "sql-query-text-boundary-exceeded",
                $"The Cosmos SQL query requires {queryBytes} UTF-8 bytes, exceeding the configured {limits.MaximumSqlQueryUtf8Bytes}-byte query-text boundary.");
        }

        long requestBytes = RequestEnvelopeReserveBytes;
        try
        {
            requestBytes = AddSerializedValue(
                requestBytes,
                query.QueryText,
                limits.MaximumRequestUtf8Bytes);
        }
        catch (SerializationBoundaryExceededException)
        {
            ThrowRequestBoundary(
                limits.MaximumRequestUtf8Bytes + 1L,
                limits.MaximumRequestUtf8Bytes);
        }
        foreach (var parameter in query.GetQueryParameters())
        {
            requestBytes += ParameterEnvelopeReserveBytes;
            try
            {
                requestBytes = AddSerializedValue(
                    requestBytes,
                    parameter.Name,
                    limits.MaximumRequestUtf8Bytes);
                requestBytes = AddSerializedValue(
                    requestBytes,
                    parameter.Value,
                    limits.MaximumRequestUtf8Bytes);
            }
            catch (SerializationBoundaryExceededException)
            {
                ThrowRequestBoundary(
                    limits.MaximumRequestUtf8Bytes + 1L,
                    limits.MaximumRequestUtf8Bytes);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new CosmosQueryRequestSizeLimitException(
                    "query-parameter-size-unavailable",
                    $"Cosmos query parameter '{parameter.Name}' cannot be measured deterministically before I/O.",
                    exception);
            }

            if (requestBytes > limits.MaximumRequestUtf8Bytes)
                ThrowRequestBoundary(requestBytes, limits.MaximumRequestUtf8Bytes);
        }

        if (requestBytes > limits.MaximumRequestUtf8Bytes)
            ThrowRequestBoundary(requestBytes, limits.MaximumRequestUtf8Bytes);
    }

    static long AddSerializedValue(long accumulatedBytes, object? value, int maximumRequestBytes)
    {
        var remaining = maximumRequestBytes - accumulatedBytes;
        if (remaining <= 0)
            ThrowRequestBoundary(accumulatedBytes, maximumRequestBytes);
        return accumulatedBytes + MeasureParameter(value, checked((int)remaining));
    }

    static int MeasureParameter(object? value, int maximumBytes)
    {
        using BoundedCountingBufferWriter buffer = new(maximumBytes);
        using Utf8JsonWriter writer = new(buffer);
        if (value is null)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value, value.GetType());
        writer.Flush();
        return buffer.WrittenCount;
    }

    [DoesNotReturn]
    static void ThrowRequestBoundary(long requestBytes, int maximumRequestBytes) =>
        throw new CosmosQueryRequestSizeLimitException(
            "query-request-boundary-exceeded",
            $"The conservative Cosmos query request estimate is {requestBytes} UTF-8 bytes, exceeding the configured {maximumRequestBytes}-byte request boundary.");

    sealed class BoundedCountingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        readonly int maximumBytes;
        byte[] buffer;
        int available;

        public BoundedCountingBufferWriter(int maximumBytes)
        {
            if (maximumBytes <= 0)
                throw new SerializationBoundaryExceededException();
            this.maximumBytes = maximumBytes;
            buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes, 256));
        }

        public int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || count > available || WrittenCount > maximumBytes - count)
                throw new SerializationBoundaryExceededException();
            WrittenCount += count;
            available = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var requested = RequiredCapacity(sizeHint);
            EnsureBuffer(requested);
            available = requested;
            return buffer.AsMemory(0, requested);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var requested = RequiredCapacity(sizeHint);
            EnsureBuffer(requested);
            available = requested;
            return buffer.AsSpan(0, requested);
        }

        public void Dispose()
        {
            var rented = buffer;
            buffer = [];
            available = 0;
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        int RequiredCapacity(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "A buffer size hint cannot be negative.");
            var remaining = maximumBytes - WrittenCount;
            var requested = sizeHint == 0 ? Math.Min(remaining, 256) : sizeHint;
            if (requested <= 0 || requested > remaining)
                throw new SerializationBoundaryExceededException();
            return requested;
        }

        void EnsureBuffer(int requested)
        {
            if (buffer.Length >= requested)
                return;
            var previous = buffer;
            buffer = ArrayPool<byte>.Shared.Rent(requested);
            ArrayPool<byte>.Shared.Return(previous, clearArray: true);
        }
    }

    sealed class SerializationBoundaryExceededException : Exception
    {
    }
}

/// <summary>Internal pre-I/O Cosmos request-size boundary failure.</summary>
internal sealed class CosmosQueryRequestSizeLimitException : ArgumentException
{
    /// <summary>Creates a request-size boundary failure.</summary>
    /// <param name="reason">Stable non-sensitive boundary reason.</param>
    /// <param name="message">Human-readable boundary explanation.</param>
    /// <param name="innerException">Optional deterministic measurement failure.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="reason"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> or <paramref name="message"/> is empty or white space.
    /// </exception>
    internal CosmosQueryRequestSizeLimitException(
        string reason,
        string message,
        Exception? innerException = null)
        : base(Guard.RequireNotNullOrWhiteSpace(message), innerException)
    {
        Reason = Guard.RequireNotNullOrWhiteSpace(reason);
    }

    /// <summary>Stable non-sensitive boundary reason.</summary>
    internal string Reason { get; }
}
