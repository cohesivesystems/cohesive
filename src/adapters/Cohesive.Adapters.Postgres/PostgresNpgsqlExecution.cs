using Cohesive.Adapters.Sql;
using System.Buffers;
using System.Collections.Immutable;
using System.Data;
using System.Globalization;
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
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(sourceCommand.Text);
        return await ExecuteCommandAsync(
            command,
            sourceCommand,
            cancellationToken).ConfigureAwait(false);
    }

    internal static PostgresNpgsqlCommandExecutor CreateTransactionExecutor(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        RequireActiveTransaction(connection, transaction);
        return ExecuteInTransactionAsync;

        async ValueTask<PostgresNpgsqlCommandResult> ExecuteInTransactionAsync(
            PostgresNpgsqlCommand sourceCommand,
            CancellationToken cancellationToken)
        {
            RequireActiveTransaction(connection, transaction);
            await using var command = connection.CreateCommand();
            command.CommandText = sourceCommand.Text;
            command.Transaction = transaction;
            return await ExecuteCommandAsync(
                command,
                sourceCommand,
                cancellationToken).ConfigureAwait(false);
        }
    }

    static async ValueTask<PostgresNpgsqlCommandResult> ExecuteCommandAsync(
        NpgsqlCommand command,
        PostgresNpgsqlCommand sourceCommand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(sourceCommand);
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

    static void RequireActiveTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The PostgreSQL transaction must be active on the supplied connection.",
                nameof(transaction));
        }
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "A transaction-bound PostgreSQL executor requires an open caller-owned connection.");
        }
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
            SqlUtf8.GetMaximumByteCount(CharacterSegmentLength));
        var currentLength = 0;
        var totalLength = 0;
        var encoder = SqlUtf8.CreateEncoder();
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

            return SqlUtf8.RequireText(
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
    const uint BooleanTypeId = 16;
    const uint ByteaTypeId = 17;
    const uint Int64TypeId = 20;
    const uint Int32TypeId = 23;
    const uint TextTypeId = 25;
    const uint CharacterTypeId = 1042;
    const uint CharacterVaryingTypeId = 1043;
    const uint DateTypeId = 1082;
    const uint TimestampTypeId = 1114;
    const uint TimestampWithTimeZoneTypeId = 1184;
    const uint NumericTypeId = 1700;
    const uint UuidTypeId = 2950;

    internal static bool MayUseUnchangedToast(
        PostgresRelationQueryScalarType scalarType) => scalarType switch
        {
            PostgresRelationQueryScalarType.Numeric
                or PostgresRelationQueryScalarType.Text
                or PostgresRelationQueryScalarType.Bytea => true,
            PostgresRelationQueryScalarType.Boolean
                or PostgresRelationQueryScalarType.Int32
                or PostgresRelationQueryScalarType.Int64
                or PostgresRelationQueryScalarType.Uuid
                or PostgresRelationQueryScalarType.Date
                or PostgresRelationQueryScalarType.Timestamp
                or PostgresRelationQueryScalarType.TimestampWithTimeZone => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarType),
                scalarType,
                "Unsupported PostgreSQL scalar type.")
        };

    internal static bool HasProjectedPayloadThatMayUseUnchangedToast(
        PostgresRelationQueryTableBinding table)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (var field in table.Fields)
        {
            if (MayUseUnchangedToast(field.ScalarType))
                return true;
        }
        foreach (var reference in table.RelationshipReferences)
        {
            if (MayUseUnchangedToast(reference.ScalarType))
                return true;
        }
        return false;
    }

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

    internal static bool AcceptsPostgresType(
        PostgresRelationQueryScalarType scalarType,
        uint effectiveDataTypeId) => scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => effectiveDataTypeId == BooleanTypeId,
            PostgresRelationQueryScalarType.Int32 => effectiveDataTypeId == Int32TypeId,
            PostgresRelationQueryScalarType.Int64 => effectiveDataTypeId == Int64TypeId,
            PostgresRelationQueryScalarType.Numeric => effectiveDataTypeId == NumericTypeId,
            PostgresRelationQueryScalarType.Text => effectiveDataTypeId is
                TextTypeId or CharacterTypeId or CharacterVaryingTypeId,
            PostgresRelationQueryScalarType.Uuid => effectiveDataTypeId == UuidTypeId,
            PostgresRelationQueryScalarType.Date => effectiveDataTypeId == DateTypeId,
            PostgresRelationQueryScalarType.Timestamp => effectiveDataTypeId == TimestampTypeId,
            PostgresRelationQueryScalarType.TimestampWithTimeZone =>
                effectiveDataTypeId == TimestampWithTimeZoneTypeId,
            PostgresRelationQueryScalarType.Bytea => effectiveDataTypeId == ByteaTypeId,
            _ => false
        };

    internal static async ValueTask<object> ReadPgOutputAsync(
        Npgsql.Replication.PgOutput.ReplicationValue value,
        PostgresRelationQueryScalarType scalarType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var text = await value.Get<string>(cancellationToken).ConfigureAwait(false);
        return ParsePgOutputText(text, scalarType);
    }

    internal static object ParsePgOutputText(
        string value,
        PostgresRelationQueryScalarType scalarType)
    {
        ArgumentNullException.ThrowIfNull(value);
        return scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => value switch
            {
                "t" => true,
                "f" => false,
                _ => throw InvalidPgOutputText(scalarType)
            },
            PostgresRelationQueryScalarType.Int32 => ParseCanonicalInt32(value, scalarType),
            PostgresRelationQueryScalarType.Int64 => ParseCanonicalInt64(value, scalarType),
            PostgresRelationQueryScalarType.Numeric => ParseCanonicalNumeric(value, scalarType),
            PostgresRelationQueryScalarType.Text => SqlUtf8.RequireText(value, nameof(value)),
            PostgresRelationQueryScalarType.Uuid => ParseCanonicalUuid(value, scalarType),
            PostgresRelationQueryScalarType.Date => ParseCanonicalDate(value, scalarType),
            PostgresRelationQueryScalarType.Timestamp =>
                ParseCanonicalTimestamp(value.AsSpan(), scalarType),
            PostgresRelationQueryScalarType.TimestampWithTimeZone =>
                ParseCanonicalTimestampWithTimeZone(value, scalarType),
            PostgresRelationQueryScalarType.Bytea => ParseCanonicalBytea(value, scalarType),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scalarType),
                scalarType,
                "Unsupported PostgreSQL scalar type.")
        };
    }

    static int ParseCanonicalInt32(
        string value,
        PostgresRelationQueryScalarType scalarType) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
        && string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
            ? parsed
            : throw InvalidPgOutputText(scalarType);

    static long ParseCanonicalInt64(
        string value,
        PostgresRelationQueryScalarType scalarType) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
        && string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
            ? parsed
            : throw InvalidPgOutputText(scalarType);

    static decimal ParseCanonicalNumeric(
        string value,
        PostgresRelationQueryScalarType scalarType)
    {
        if (!IsCanonicalNumeric(value)
            || !decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed)
            || !string.Equals(
                parsed.ToString(CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal))
        {
            throw InvalidPgOutputText(scalarType);
        }
        return parsed;
    }

    static bool IsCanonicalNumeric(string value)
    {
        if (value.Length == 0 || value[0] == '+')
            return false;
        var index = value[0] == '-' ? 1 : 0;
        if (index == value.Length)
            return false;
        if (value[index] == '0' && index + 1 < value.Length && value[index + 1] != '.')
            return false;
        var integralDigits = 0;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            integralDigits++;
            index++;
        }
        if (integralDigits == 0)
            return false;
        if (index == value.Length)
            return value[0] != '-' || value != "-0";
        if (value[index++] != '.' || index == value.Length)
            return false;
        while (index < value.Length)
        {
            if (value[index] is < '0' or > '9')
                return false;
            index++;
        }
        return value[0] != '-' || decimal.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed != decimal.Zero;
    }

    static Guid ParseCanonicalUuid(
        string value,
        PostgresRelationQueryScalarType scalarType) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && string.Equals(parsed.ToString("D", CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
            ? parsed
            : throw InvalidPgOutputText(scalarType);

    static DateOnly ParseCanonicalDate(
        string value,
        PostgresRelationQueryScalarType scalarType) =>
        value.Length == 10
        && DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
        && string.Equals(parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value, StringComparison.Ordinal)
            ? parsed
            : throw InvalidPgOutputText(scalarType);

    static DateTime ParseCanonicalTimestamp(
        ReadOnlySpan<char> value,
        PostgresRelationQueryScalarType scalarType)
    {
        if (value.Length < 19
            || !DateTime.TryParseExact(
                value[..19],
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw InvalidPgOutputText(scalarType);
        }
        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (value.Length == 19)
            return parsed;
        if (value[19] != '.' || value.Length is < 21 or > 26 || value[^1] == '0')
            throw InvalidPgOutputText(scalarType);
        var microseconds = 0;
        for (var index = 20; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
                throw InvalidPgOutputText(scalarType);
            microseconds = checked((microseconds * 10) + value[index] - '0');
        }
        for (var index = value.Length - 20; index < 6; index++)
            microseconds *= 10;
        return parsed.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
    }

    static DateTimeOffset ParseCanonicalTimestampWithTimeZone(
        string value,
        PostgresRelationQueryScalarType scalarType)
    {
        var offsetStart = -1;
        for (var index = 19; index < value.Length; index++)
        {
            if (value[index] is '+' or '-')
            {
                offsetStart = index;
                break;
            }
        }
        if (offsetStart < 0)
            throw InvalidPgOutputText(scalarType);
        var timestamp = ParseCanonicalTimestamp(value.AsSpan(0, offsetStart), scalarType);
        var offset = ParseCanonicalOffset(value.AsSpan(offsetStart), scalarType);
        try
        {
            return RequireInstant(new DateTimeOffset(timestamp, offset).ToUniversalTime());
        }
        catch (ArgumentException)
        {
            throw InvalidPgOutputText(scalarType);
        }
    }

    static TimeSpan ParseCanonicalOffset(
        ReadOnlySpan<char> value,
        PostgresRelationQueryScalarType scalarType)
    {
        if (value.Length is not (3 or 6 or 9)
            || value[0] is not ('+' or '-')
            || !TryParseTwoDigits(value[1..3], out var hours)
            || value.Length >= 6
                && (value[3] != ':' || !TryParseTwoDigits(value[4..6], out _))
            || value.Length == 9
                && (value[6] != ':' || !TryParseTwoDigits(value[7..9], out _)))
        {
            throw InvalidPgOutputText(scalarType);
        }
        var minutes = value.Length >= 6
            ? ((value[4] - '0') * 10) + value[5] - '0'
            : 0;
        var seconds = value.Length == 9
            ? ((value[7] - '0') * 10) + value[8] - '0'
            : 0;
        if (minutes >= 60
            || seconds >= 60
            || value[0] == '-' && hours == 0 && minutes == 0 && seconds == 0
            || value.Length == 6 && minutes == 0
            || value.Length == 9 && seconds == 0)
        {
            throw InvalidPgOutputText(scalarType);
        }
        try
        {
            var offset = new TimeSpan(hours, minutes, seconds);
            return value[0] == '-' ? -offset : offset;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidPgOutputText(scalarType);
        }
    }

    static bool TryParseTwoDigits(ReadOnlySpan<char> value, out int result)
    {
        result = 0;
        if (value.Length != 2
            || value[0] is < '0' or > '9'
            || value[1] is < '0' or > '9')
        {
            return false;
        }
        result = ((value[0] - '0') * 10) + value[1] - '0';
        return true;
    }

    static byte[] ParseCanonicalBytea(
        string value,
        PostgresRelationQueryScalarType scalarType)
    {
        if (value.StartsWith("\\x", StringComparison.Ordinal))
        {
            if ((value.Length & 1) != 0)
                throw InvalidPgOutputText(scalarType);
            for (var index = 2; index < value.Length; index++)
            {
                if (value[index] is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))
                {
                    throw InvalidPgOutputText(scalarType);
                }
            }
            return Convert.FromHexString(value.AsSpan(2));
        }

        var result = new byte[value.Length];
        var outputIndex = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                if (character is < (char)0x20 or > (char)0x7e)
                    throw InvalidPgOutputText(scalarType);
                result[outputIndex++] = checked((byte)character);
                continue;
            }
            if (++index >= value.Length)
                throw InvalidPgOutputText(scalarType);
            if (value[index] == '\\')
            {
                result[outputIndex++] = (byte)'\\';
                continue;
            }
            if (index + 2 >= value.Length
                || value[index] is < '0' or > '3'
                || value[index + 1] is < '0' or > '7'
                || value[index + 2] is < '0' or > '7')
            {
                throw InvalidPgOutputText(scalarType);
            }
            result[outputIndex++] = checked((byte)(
                ((value[index] - '0') << 6)
                | ((value[index + 1] - '0') << 3)
                | value[index + 2] - '0'));
            index += 2;
        }
        return outputIndex == result.Length
            ? result
            : result.AsSpan(0, outputIndex).ToArray();
    }

    static FormatException InvalidPgOutputText(
        PostgresRelationQueryScalarType scalarType) => new(
        $"pgoutput text is not canonical for PostgreSQL scalar '{scalarType}'.");

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
                PostgresRelationQueryScalarType.Text => SqlUtf8.RequireText(value, nameof(value)),
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

    internal static SqlExpression ApplyTextCollation(
        SqlExpression expression,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryTextSemantics? textSemantics) =>
        scalarType == PostgresRelationQueryScalarType.Text && textSemantics is not null
            ? SqlExpression.Collate(expression, textSemantics.Collation)
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
