using System.Buffers;
using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text;
using Cohesive.Model;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.Adapters.Postgres;

internal readonly record struct PostgresNpgsqlParameter(
    object Value,
    PostgresRelationQueryScalarType ScalarType,
    bool IsArray);

internal sealed record PostgresNpgsqlCommand(
    string Text,
    ImmutableArray<PostgresNpgsqlParameter> Parameters,
    ImmutableArray<PostgresRelationQueryScalarType> ResultTypes,
    long MaximumResultBytes);

internal sealed record PostgresNpgsqlCommandResult(
    ImmutableArray<ImmutableArray<object?>> Rows);

internal delegate ValueTask<PostgresNpgsqlCommandResult> PostgresNpgsqlCommandExecutor(
    PostgresNpgsqlCommand command,
    CancellationToken cancellationToken);

internal static class PostgresNpgsqlExecution
{
    internal const string DisableInfinityConversionsSwitch = "Npgsql.DisableDateTimeInfinityConversions";

    internal static async ValueTask<PostgresNpgsqlCommandResult> ExecuteAsync(
        NpgsqlDataSource dataSource,
        PostgresNpgsqlCommand sourceCommand,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceCommand.MaximumResultBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceCommand),
                sourceCommand.MaximumResultBytes,
                "A PostgreSQL command result byte bound must be positive.");
        }
        if (sourceCommand.Parameters.Any(static parameter => IsTemporal(parameter.ScalarType))
            || sourceCommand.ResultTypes.Any(IsTemporal))
        {
            RequireExactTemporalSwitch();
        }
        await using var command = dataSource.CreateCommand(sourceCommand.Text);
        foreach (var parameter in sourceCommand.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = PostgresRelationQueryScalarCatalog.ToNpgsqlDbType(
                    parameter.ScalarType,
                    parameter.IsArray),
                Value = parameter.Value
            });
        }

        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);
        if (reader.FieldCount != sourceCommand.ResultTypes.Length)
        {
            throw new InvalidOperationException(
                $"PostgreSQL returned {reader.FieldCount} columns for a projection of {sourceCommand.ResultTypes.Length} columns.");
        }

        var budget = new PostgresNpgsqlResultBudget(sourceCommand.MaximumResultBytes);
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ImmutableArray.CreateBuilder<object?>(sourceCommand.ResultTypes.Length);
            for (var ordinal = 0; ordinal < sourceCommand.ResultTypes.Length; ordinal++)
            {
                row.Add(await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await PostgresRelationQueryScalarCatalog.ReadAsync(
                        reader,
                        ordinal,
                        sourceCommand.ResultTypes[ordinal],
                        budget,
                        cancellationToken).ConfigureAwait(false));
            }
            rows.Add(row.MoveToImmutable());
        }

        return new(rows.ToImmutable());
    }

    internal static void RequireExactTemporalSemantics(PostgresNpgsqlTemporalSemantics temporalSemantics)
    {
        if (temporalSemantics != PostgresNpgsqlTemporalSemantics.InfinityConversionsDisabledBeforeInitialization)
        {
            throw new InvalidOperationException(
                "Exact PostgreSQL temporal reads require explicit caller evidence that infinity conversions were disabled before every Npgsql operation in the process.");
        }
        RequireExactTemporalSwitch();
    }

    static void RequireExactTemporalSwitch()
    {
        if (!AppContext.TryGetSwitch(DisableInfinityConversionsSwitch, out var disabled) || !disabled)
        {
            throw new InvalidOperationException(
                $"Exact PostgreSQL temporal reads require AppContext switch '{DisableInfinityConversionsSwitch}' to remain enabled so finite CLR endpoints cannot be conflated with PostgreSQL infinity.");
        }
    }

    internal static bool IsTemporal(PostgresRelationQueryScalarType scalarType) => scalarType is
        PostgresRelationQueryScalarType.Date
        or PostgresRelationQueryScalarType.Timestamp
        or PostgresRelationQueryScalarType.TimestampWithTimeZone;
}

internal sealed class PostgresNpgsqlResultByteLimitExceededException : InvalidOperationException
{
    internal PostgresNpgsqlResultByteLimitExceededException(long maximumBytes)
        : base($"The PostgreSQL provider result exceeds its physical {maximumBytes.ToString(CultureInfo.InvariantCulture)}-byte retention bound.")
    {
        MaximumBytes = maximumBytes;
    }

    internal long MaximumBytes { get; }
}

internal sealed class PostgresNpgsqlResultBudget
{
    internal PostgresNpgsqlResultBudget(long maximumBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "A PostgreSQL provider result byte bound must be positive.");
        MaximumBytes = maximumBytes;
    }

    internal long MaximumBytes { get; }

    internal long ConsumedBytes { get; private set; }

    internal void Consume(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "A retained byte count cannot be negative.");
        if (byteCount > MaximumBytes - ConsumedBytes)
            throw new PostgresNpgsqlResultByteLimitExceededException(MaximumBytes);
        ConsumedBytes += byteCount;
    }
}

internal static class PostgresNpgsqlBoundedResult
{
    const int ByteSegmentLength = 8 * 1024;
    const int CharacterSegmentLength = 4 * 1024;

    internal static async ValueTask<byte[]> ReadBytesAsync(
        Stream source,
        PostgresNpgsqlResultBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        List<ByteSegment> segments = [];
        byte[]? current = null;
        var currentLength = 0;
        var totalLength = 0;
        try
        {
            while (true)
            {
                current ??= ArrayPool<byte>.Shared.Rent(ByteSegmentLength);
                var read = await source.ReadAsync(
                    current.AsMemory(currentLength, ByteSegmentLength - currentLength),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (currentLength > 0)
                    {
                        segments.Add(new(current, currentLength));
                        current = null;
                    }
                    break;
                }

                budget.Consume(read);
                currentLength = checked(currentLength + read);
                totalLength = checked(totalLength + read);
                if (currentLength == ByteSegmentLength)
                {
                    segments.Add(new(current, currentLength));
                    current = null;
                    currentLength = 0;
                }
            }

            var result = GC.AllocateUninitializedArray<byte>(totalLength);
            var offset = 0;
            foreach (var segment in segments)
            {
                segment.Buffer.AsSpan(0, segment.Length).CopyTo(result.AsSpan(offset));
                offset += segment.Length;
            }
            return result;
        }
        finally
        {
            if (current is not null)
                ArrayPool<byte>.Shared.Return(current);
            foreach (var segment in segments)
                ArrayPool<byte>.Shared.Return(segment.Buffer);
        }
    }

    internal static async ValueTask<string> ReadTextAsync(
        TextReader source,
        PostgresNpgsqlResultBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        List<CharacterSegment> segments = [];
        char[]? current = null;
        var encoded = ArrayPool<byte>.Shared.Rent(
            PostgresSqlUtf8.GetMaximumByteCount(CharacterSegmentLength));
        var currentLength = 0;
        var totalLength = 0;
        var encoder = PostgresSqlUtf8.CreateEncoder();
        try
        {
            while (true)
            {
                current ??= ArrayPool<char>.Shared.Rent(CharacterSegmentLength);
                var read = await source.ReadAsync(
                    current.AsMemory(currentLength, CharacterSegmentLength - currentLength),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    encoder.Convert(
                        ReadOnlySpan<char>.Empty,
                        encoded,
                        flush: true,
                        out _,
                        out var terminalBytes,
                        out var terminalComplete);
                    if (!terminalComplete)
                        throw new InvalidOperationException("The bounded UTF-8 encoder did not flush into its maximum-sized buffer.");
                    budget.Consume(terminalBytes);
                    if (currentLength > 0)
                    {
                        segments.Add(new(current, currentLength));
                        current = null;
                    }
                    break;
                }

                encoder.Convert(
                    current.AsSpan(currentLength, read),
                    encoded,
                    flush: false,
                    out var charactersEncoded,
                    out var encodedBytes,
                    out var encodingComplete);
                if (charactersEncoded != read || !encodingComplete)
                    throw new InvalidOperationException("The bounded UTF-8 encoder did not consume its maximum-sized input buffer.");
                budget.Consume(encodedBytes);
                currentLength = checked(currentLength + read);
                totalLength = checked(totalLength + read);
                if (currentLength == CharacterSegmentLength)
                {
                    segments.Add(new(current, currentLength));
                    current = null;
                    currentLength = 0;
                }
            }

            return PostgresSqlUtf8.RequireText(
                string.Create(totalLength, segments, static (destination, sourceSegments) =>
                {
                    var offset = 0;
                    foreach (var segment in sourceSegments)
                    {
                        segment.Buffer.AsSpan(0, segment.Length).CopyTo(destination[offset..]);
                        offset += segment.Length;
                    }
                }),
                nameof(source));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded);
            if (current is not null)
                ArrayPool<char>.Shared.Return(current);
            foreach (var segment in segments)
                ArrayPool<char>.Shared.Return(segment.Buffer);
        }
    }

    readonly record struct ByteSegment(byte[] Buffer, int Length);

    readonly record struct CharacterSegment(char[] Buffer, int Length);
}

internal static class PostgresRelationQueryScalarCatalog
{
    internal static bool TryFromSemanticType(
        TypeRef? type,
        out PostgresRelationQueryScalarType scalarType)
    {
        scalarType = type switch
        {
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } => PostgresRelationQueryScalarType.Boolean,
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 } => PostgresRelationQueryScalarType.Int32,
            ScalarTypeRef { Kind: ScalarTypeKind.Int64 } => PostgresRelationQueryScalarType.Int64,
            ScalarTypeRef { Kind: ScalarTypeKind.Decimal } => PostgresRelationQueryScalarType.Numeric,
            ScalarTypeRef { Kind: ScalarTypeKind.String } => PostgresRelationQueryScalarType.Text,
            ScalarTypeRef { Kind: ScalarTypeKind.Guid } => PostgresRelationQueryScalarType.Uuid,
            ScalarTypeRef { Kind: ScalarTypeKind.Date } => PostgresRelationQueryScalarType.Date,
            ScalarTypeRef { Kind: ScalarTypeKind.DateTime } => PostgresRelationQueryScalarType.Timestamp,
            ScalarTypeRef { Kind: ScalarTypeKind.Instant } => PostgresRelationQueryScalarType.TimestampWithTimeZone,
            ScalarTypeRef { Kind: ScalarTypeKind.Bytes } => PostgresRelationQueryScalarType.Bytea,
            EntityReferenceTypeRef => PostgresRelationQueryScalarType.Text,
            EnumTypeRef => PostgresRelationQueryScalarType.Text,
            _ => default
        };
        return type is ScalarTypeRef or EntityReferenceTypeRef or EnumTypeRef;
    }

    internal static PostgresRelationQueryValueEncoding ToValueEncoding(
        PostgresRelationQueryScalarType scalarType) => scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => PostgresRelationQueryValueEncoding.Boolean,
            PostgresRelationQueryScalarType.Int32 => PostgresRelationQueryValueEncoding.Int32,
            PostgresRelationQueryScalarType.Int64 => PostgresRelationQueryValueEncoding.Int64,
            PostgresRelationQueryScalarType.Numeric => PostgresRelationQueryValueEncoding.Numeric,
            PostgresRelationQueryScalarType.Text => PostgresRelationQueryValueEncoding.Text,
            PostgresRelationQueryScalarType.Uuid => PostgresRelationQueryValueEncoding.Uuid,
            PostgresRelationQueryScalarType.Date => PostgresRelationQueryValueEncoding.Date,
            PostgresRelationQueryScalarType.Timestamp => PostgresRelationQueryValueEncoding.Timestamp,
            PostgresRelationQueryScalarType.TimestampWithTimeZone => PostgresRelationQueryValueEncoding.TimestampWithTimeZone,
            PostgresRelationQueryScalarType.Bytea => PostgresRelationQueryValueEncoding.Bytea,
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };

    internal static NpgsqlDbType ToNpgsqlDbType(
        PostgresRelationQueryScalarType scalarType,
        bool array) => (array ? NpgsqlDbType.Array : 0) | scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => NpgsqlDbType.Boolean,
            PostgresRelationQueryScalarType.Int32 => NpgsqlDbType.Integer,
            PostgresRelationQueryScalarType.Int64 => NpgsqlDbType.Bigint,
            PostgresRelationQueryScalarType.Numeric => NpgsqlDbType.Numeric,
            PostgresRelationQueryScalarType.Text => NpgsqlDbType.Text,
            PostgresRelationQueryScalarType.Uuid => NpgsqlDbType.Uuid,
            PostgresRelationQueryScalarType.Date => NpgsqlDbType.Date,
            PostgresRelationQueryScalarType.Timestamp => NpgsqlDbType.Timestamp,
            PostgresRelationQueryScalarType.TimestampWithTimeZone => NpgsqlDbType.TimestampTz,
            PostgresRelationQueryScalarType.Bytea => NpgsqlDbType.Bytea,
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };

    internal static async ValueTask<object> ReadAsync(
        NpgsqlDataReader reader,
        int ordinal,
        PostgresRelationQueryScalarType scalarType,
        PostgresNpgsqlResultBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        switch (scalarType)
        {
            case PostgresRelationQueryScalarType.Text:
                using (var text = await reader.GetTextReaderAsync(ordinal, cancellationToken).ConfigureAwait(false))
                {
                    return await PostgresNpgsqlBoundedResult
                        .ReadTextAsync(text, budget, cancellationToken)
                        .ConfigureAwait(false);
                }
            case PostgresRelationQueryScalarType.Bytea:
                await using (var bytes = await reader.GetStreamAsync(ordinal, cancellationToken).ConfigureAwait(false))
                {
                    return await PostgresNpgsqlBoundedResult
                        .ReadBytesAsync(bytes, budget, cancellationToken)
                        .ConfigureAwait(false);
                }
            case PostgresRelationQueryScalarType.Boolean:
                budget.Consume(sizeof(bool));
                return await reader.GetFieldValueAsync<bool>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Int32:
                budget.Consume(sizeof(int));
                return await reader.GetFieldValueAsync<int>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Int64:
                budget.Consume(sizeof(long));
                return await reader.GetFieldValueAsync<long>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Numeric:
                budget.Consume(sizeof(decimal));
                return await reader.GetFieldValueAsync<decimal>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Uuid:
                budget.Consume(16);
                return await reader.GetFieldValueAsync<Guid>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Date:
                budget.Consume(sizeof(int));
                return await reader.GetFieldValueAsync<DateOnly>(ordinal, cancellationToken).ConfigureAwait(false);
            case PostgresRelationQueryScalarType.Timestamp:
                budget.Consume(sizeof(long));
                return RequireCivilTimestamp(
                    await reader.GetFieldValueAsync<DateTime>(ordinal, cancellationToken).ConfigureAwait(false));
            case PostgresRelationQueryScalarType.TimestampWithTimeZone:
                budget.Consume(sizeof(long));
                return RequireInstant(
                    await reader.GetFieldValueAsync<DateTime>(ordinal, cancellationToken).ConfigureAwait(false));
            default:
                throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.");
        }
    }

    internal static ObservationValue ToObservationValue(
        object value,
        PostgresRelationQueryScalarType scalarType) => scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => ObservationValue.FromBool((bool)value),
            PostgresRelationQueryScalarType.Int32 => ObservationValue.FromInt64((int)value),
            PostgresRelationQueryScalarType.Int64 => ObservationValue.FromInt64((long)value),
            PostgresRelationQueryScalarType.Numeric => ObservationValue.FromDecimal((decimal)value),
            PostgresRelationQueryScalarType.Text => ObservationValue.FromString((string)value),
            PostgresRelationQueryScalarType.Uuid => ObservationValue.FromString(((Guid)value).ToString("D", CultureInfo.InvariantCulture)),
            PostgresRelationQueryScalarType.Date => ObservationValue.FromDateOnly((DateOnly)value),
            PostgresRelationQueryScalarType.Timestamp => ObservationValue.FromString(((DateTime)value).ToString("O", CultureInfo.InvariantCulture)),
            PostgresRelationQueryScalarType.TimestampWithTimeZone => ObservationValue.FromDateTimeOffset(ToInstant(value)),
            PostgresRelationQueryScalarType.Bytea => ObservationValue.FromBytes((byte[])value),
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };

    internal static string FormatKey(
        object value,
        PostgresRelationQueryScalarType scalarType) => scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => (bool)value ? "true" : "false",
            PostgresRelationQueryScalarType.Int32 => ((int)value).ToString(CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Int64 => ((long)value).ToString(CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Numeric => ((decimal)value).ToString("G29", CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Text => (string)value,
            PostgresRelationQueryScalarType.Uuid => ((Guid)value).ToString("D", CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Date => ((DateOnly)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Timestamp => ((DateTime)value).ToString("O", CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.TimestampWithTimeZone => ToInstant(value).ToString("O", CultureInfo.InvariantCulture),
            PostgresRelationQueryScalarType.Bytea => Convert.ToBase64String((byte[])value),
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };

    internal static object ParseKey(
        string value,
        PostgresRelationQueryScalarType scalarType)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return scalarType switch
            {
                PostgresRelationQueryScalarType.Boolean => bool.Parse(value),
                PostgresRelationQueryScalarType.Int32 => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                PostgresRelationQueryScalarType.Int64 => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                PostgresRelationQueryScalarType.Numeric => decimal.Parse(
                    value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture),
                PostgresRelationQueryScalarType.Text => PostgresSqlUtf8.RequireText(value, nameof(value)),
                PostgresRelationQueryScalarType.Uuid => Guid.Parse(value),
                PostgresRelationQueryScalarType.Date => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                PostgresRelationQueryScalarType.Timestamp => RequireCivilTimestamp(
                    DateTime.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                PostgresRelationQueryScalarType.TimestampWithTimeZone => RequireInstant(
                    DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                PostgresRelationQueryScalarType.Bytea => Convert.FromBase64String(value),
                _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Key '{scalarType}' has no exact canonical PostgreSQL representation.",
                nameof(value),
                exception);
        }
    }

    internal static Array CreateArray(
        ImmutableArray<object> values,
        PostgresRelationQueryScalarType scalarType) => scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => CopyArray<bool>(values),
            PostgresRelationQueryScalarType.Int32 => CopyArray<int>(values),
            PostgresRelationQueryScalarType.Int64 => CopyArray<long>(values),
            PostgresRelationQueryScalarType.Numeric => CopyArray<decimal>(values),
            PostgresRelationQueryScalarType.Text => CopyArray<string>(values),
            PostgresRelationQueryScalarType.Uuid => CopyArray<Guid>(values),
            PostgresRelationQueryScalarType.Date => CopyArray<DateOnly>(values),
            PostgresRelationQueryScalarType.Timestamp => CopyArray<DateTime>(values),
            PostgresRelationQueryScalarType.TimestampWithTimeZone => CopyInstantArray(values),
            PostgresRelationQueryScalarType.Bytea => CopyArray<byte[]>(values),
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };

    static T[] CopyArray<T>(ImmutableArray<object> values)
    {
        var result = new T[values.Length];
        for (var index = 0; index < values.Length; index++)
            result[index] = (T)values[index];
        return result;
    }

    static DateTimeOffset[] CopyInstantArray(ImmutableArray<object> values)
    {
        var result = new DateTimeOffset[values.Length];
        for (var index = 0; index < values.Length; index++)
            result[index] = ToInstant(values[index]);
        return result;
    }

    internal static PostgresSqlExpression ApplyTextCollation(
        PostgresSqlExpression expression,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryTextSemantics? textSemantics) =>
        scalarType == PostgresRelationQueryScalarType.Text && textSemantics is not null
            ? PostgresSqlExpression.Collate(expression, textSemantics.Collation)
            : expression;

    internal static bool SupportsDurableKeyset(PostgresRelationQueryIdentityBinding identity) =>
        identity.ScalarType == PostgresRelationQueryScalarType.Uuid
        || identity is
        {
            ScalarType: PostgresRelationQueryScalarType.Text,
            TextSemantics.Equality: PostgresRelationQueryTextEqualitySemantics.Ordinal,
            TextSemantics.Ordering: PostgresRelationQueryTextOrderingSemantics.Ordinal,
            TextSemantics.OrderingDomain: not null
        };

    static DateTime RequireCivilTimestamp(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified
            || value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new InvalidOperationException(
                "PostgreSQL civil timestamp is outside the finite, unspecified-kind, microsecond canonical domain.");
        }
        return value;
    }

    static DateTimeOffset RequireInstant(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc
            || value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new InvalidOperationException(
                "PostgreSQL instant is outside the finite UTC, microsecond canonical domain.");
        }
        return new DateTimeOffset(value);
    }

    static DateTimeOffset RequireInstant(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero
            || value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new InvalidOperationException(
                "PostgreSQL instant is outside the finite UTC, microsecond canonical domain.");
        }
        return value;
    }

    static DateTimeOffset ToInstant(object value) => value switch
    {
        DateTime timestamp => RequireInstant(timestamp),
        DateTimeOffset instant => RequireInstant(instant),
        _ => throw new InvalidOperationException("A PostgreSQL instant requires a DateTime or DateTimeOffset value.")
    };
}
