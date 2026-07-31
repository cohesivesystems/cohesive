using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Cohesive.Adapters.Elastic;

/// <summary>Builds the bounded line-oriented wire representation required by the Elasticsearch bulk API.</summary>
internal static class ElasticBulkNdjson
{
    internal static ReadOnlyMemory<byte> Build(
        ImmutableArray<ElasticBulkOperation> operations,
        long maximumWireBytes)
    {
        if (operations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An Elasticsearch bulk request requires at least one operation.", nameof(operations));
        }
        if (maximumWireBytes <= 0 || maximumWireBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWireBytes),
                maximumWireBytes,
                $"A bulk wire bound must be between 1 and {Array.MaxLength} bytes.");
        }

        ArrayBufferWriter<byte> buffer = new(Math.Min((int)maximumWireBytes, 16 * 1024));
        using Utf8JsonWriter writer = new(buffer);
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            var operation = operations[ordinal];
            ArgumentNullException.ThrowIfNull(operation);
            if (ordinal > 0)
            {
                writer.Reset(buffer);
            }

            WriteAction(writer, operation);
            writer.Flush();
            var newline = buffer.GetSpan(1);
            newline[0] = (byte)'\n';
            buffer.Advance(1);
            RequireWireBound(buffer.WrittenCount, maximumWireBytes);

            if (operation.Kind != ElasticBulkOperationKind.Index)
            {
                continue;
            }

            var source = operation.Source
                ?? throw new InvalidOperationException("An Elasticsearch bulk index operation omitted its JSON source.");
            var projectedLength = checked((long)buffer.WrittenCount + source.Length + 1L);
            RequireWireBound(projectedLength, maximumWireBytes);
            var destination = buffer.GetSpan(checked(source.Length + 1));
            source.Bytes.Span.CopyTo(destination);
            destination[source.Length] = (byte)'\n';
            buffer.Advance(source.Length + 1);
        }

        return buffer.WrittenMemory;
    }

    static void WriteAction(Utf8JsonWriter writer, ElasticBulkOperation operation)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(operation.Kind == ElasticBulkOperationKind.Index ? "index" : "delete");
        writer.WriteStartObject();
        writer.WriteString("_index", operation.Index);
        writer.WriteString("_id", operation.Id);
        writer.WriteNumber("version", operation.ExternalVersion);
        writer.WriteString("version_type", "external");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void RequireWireBound(long observed, long maximum)
    {
        if (observed > maximum)
        {
            throw new ArgumentException(
                $"Elasticsearch bulk NDJSON exceeded its declared {maximum.ToString(CultureInfo.InvariantCulture)}-byte wire bound.");
        }
    }
}
