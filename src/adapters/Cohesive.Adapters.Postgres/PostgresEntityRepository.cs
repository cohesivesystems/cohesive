using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Maps one canonical entity field to one PostgreSQL column and exact scalar encoding.</summary>
public sealed record PostgresEntityRepositoryFieldBinding
{
    /// <summary>Creates one entity-field binding.</summary>
    /// <param name="fieldName">Canonical field name owned by the entity definition.</param>
    /// <param name="columnName">Physical PostgreSQL column name.</param>
    /// <param name="scalarType">Exact PostgreSQL scalar encoding.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="fieldName"/> is empty, or <paramref name="columnName"/> is not a valid PostgreSQL identifier.
    /// </exception>
    public PostgresEntityRepositoryFieldBinding(
        string fieldName,
        string columnName,
        PostgresRelationQueryScalarType scalarType)
    {
        FieldName = Guard.RequireNotNullOrWhiteSpace(fieldName);
        Column = PostgresSqlDialect.Identifier(columnName);
        if (!Enum.IsDefined(scalarType))
            throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.");
        ScalarType = scalarType;
    }

    /// <summary>Canonical field name owned by the entity definition.</summary>
    public string FieldName { get; }

    /// <summary>Physical PostgreSQL column identifier.</summary>
    public SqlIdentifier Column { get; }

    /// <summary>Exact PostgreSQL scalar encoding.</summary>
    public PostgresRelationQueryScalarType ScalarType { get; }
}

/// <summary>
/// Explicit physical realization of one canonical entity definition in one PostgreSQL table.
/// </summary>
/// <remarks>
/// This mapping owns physical names and encodings only. The supplied <see cref="EntityDefinition"/> remains the
/// semantic authority for the field set and value contracts. Schema creation and migration remain explicit
/// lifecycle operations outside the repository.
/// </remarks>
public sealed record PostgresEntityRepositoryMapping
{
    /// <summary>Creates a normalized PostgreSQL entity mapping.</summary>
    /// <param name="table">Physical table containing the entity rows.</param>
    /// <param name="fields">Complete one-to-one bindings for every canonical entity field.</param>
    /// <param name="identityField">Canonical text field whose value must equal the observation identity.</param>
    /// <param name="partitionField">Canonical text field used for physical partition scoping.</param>
    /// <param name="versionColumn">Column storing the semantic observation version.</param>
    /// <param name="maximumBatchItems">Maximum writes accepted by one repository batch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> or <paramref name="fields"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A field or column is duplicated, an identity is empty, the version column overlaps a field column, or a
    /// physical identifier is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBatchItems"/> is not positive.</exception>
    public PostgresEntityRepositoryMapping(
        SqlQualifiedTable table,
        IReadOnlyList<PostgresEntityRepositoryFieldBinding> fields,
        string identityField,
        string partitionField,
        string versionColumn = "observation_version",
        int maximumBatchItems = 1_000)
    {
        Table = Guard.RequireNotNull(table);
        ArgumentNullException.ThrowIfNull(fields);
        IdentityField = Guard.RequireNotNullOrWhiteSpace(identityField);
        PartitionField = Guard.RequireNotNullOrWhiteSpace(partitionField);
        VersionColumn = PostgresSqlDialect.Identifier(versionColumn);
        if (maximumBatchItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchItems), maximumBatchItems, "Maximum batch items must be positive.");
        MaximumBatchItems = maximumBatchItems;

        Dictionary<string, PostgresEntityRepositoryFieldBinding> byField = new(fields.Count, StringComparer.Ordinal);
        HashSet<string> columns = new(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!byField.TryAdd(field.FieldName, field))
                throw new ArgumentException($"Entity field '{field.FieldName}' has more than one PostgreSQL binding.", nameof(fields));
            if (!columns.Add(field.Column.Value))
                throw new ArgumentException($"PostgreSQL column '{field.Column.Value}' is bound more than once.", nameof(fields));
        }
        if (columns.Contains(VersionColumn.Value))
            throw new ArgumentException($"Version column '{VersionColumn.Value}' overlaps an entity field column.", nameof(versionColumn));

        Fields = [.. fields];
        FieldByName = byField.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Physical entity table.</summary>
    public SqlQualifiedTable Table { get; }

    /// <summary>Ordered complete field bindings.</summary>
    public ImmutableArray<PostgresEntityRepositoryFieldBinding> Fields { get; }

    /// <summary>Canonical observation-identity field.</summary>
    public string IdentityField { get; }

    /// <summary>Canonical physical-partition field.</summary>
    public string PartitionField { get; }

    /// <summary>Column storing the semantic observation version.</summary>
    public SqlIdentifier VersionColumn { get; }

    /// <summary>Maximum number of writes accepted in one batch.</summary>
    public int MaximumBatchItems { get; }

    internal ImmutableDictionary<string, PostgresEntityRepositoryFieldBinding> FieldByName { get; }
}

/// <summary>
/// PostgreSQL observation repository backed by a normalized relational table.
/// </summary>
/// <remarks>
/// The repository validates writes through the canonical entity definition, uses PostgreSQL <c>xmin</c> as an
/// opaque optimistic-concurrency token, and stores the semantic observation version separately. Batches execute in
/// one database transaction and therefore support same-partition and cross-partition all-or-nothing semantics.
/// The caller owns the bound <see cref="NpgsqlDataSource"/> and must keep it alive for the repository lifetime.
/// </remarks>
public sealed class PostgresEntityRepository : IEntityRepository
{
    readonly PostgresNpgsqlRuntimeBinding runtime;
    readonly PostgresEntityRepositoryMapping mapping;
    readonly PostgresEntityRepositorySql sql;
    readonly ObservationLayout layout;
    readonly int identityOrdinal;
    readonly int partitionOrdinal;

    /// <summary>Creates a repository for one canonical entity definition and physical table mapping.</summary>
    /// <param name="entityDefinition">Canonical entity definition used to validate every read and write.</param>
    /// <param name="runtime">Exact attested Npgsql runtime that owns database access.</param>
    /// <param name="mapping">Explicit table, column, identity, partition, version, and batch mapping.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The mapping does not bind every semantic field exactly once, contains an extra field, uses an incompatible
    /// scalar encoding, or does not use text identity and partition fields.
    /// </exception>
    public PostgresEntityRepository(
        EntityDefinition entityDefinition,
        PostgresNpgsqlRuntimeBinding runtime,
        PostgresEntityRepositoryMapping mapping)
    {
        EntityDefinition = Guard.RequireNotNull(entityDefinition);
        this.runtime = Guard.RequireNotNull(runtime);
        this.mapping = Guard.RequireNotNull(mapping);
        layout = ObservationLayout.Create(entityDefinition.StateShape, mapping.Fields.Select(static field => field.FieldName));
        identityOrdinal = layout.GetOrdinal(mapping.IdentityField);
        partitionOrdinal = layout.GetOrdinal(mapping.PartitionField);
        ValidateMapping(entityDefinition, mapping);
        sql = PostgresEntityRepositorySql.Create(mapping);
        BatchCapabilities = new(
            SupportsNativeBatching: true,
            SupportsSamePartitionAtomicity: true,
            SupportsAllOrNothingAtomicity: true,
            MaxItemsPerBatch: mapping.MaximumBatchItems);
    }

    /// <inheritdoc />
    public EntityDefinition EntityDefinition { get; }

    /// <inheritdoc />
    public string EntityType => EntityDefinition.Shape.Id.Value;

    /// <inheritdoc />
    public EntityBatchCapabilities BatchCapabilities { get; }

    /// <inheritdoc />
    public async Task<EntitySnapshot?> TryGet(
        OperationContext context,
        string id,
        EntityReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        context.ThrowIfCancellationRequested();

        var template = options?.PartitionKey is null ? sql.ReadByIdentity : sql.ReadByIdentityAndPartition;
        await using var command = runtime.DataSource.CreateCommand(template.Text);
        AddReadParameters(command, template, id, options?.PartitionKey);

        await using var reader = await command.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            return null;

        var snapshot = ReadSnapshot(reader, options?.Fields);
        if (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Observation '{EntityType}:{id}' exists in multiple partitions and cannot be loaded by id alone.");
        }

        ValidateReadPreconditions(id, snapshot, options);
        return snapshot;
    }

    /// <inheritdoc />
    public async Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        context.ThrowIfCancellationRequested();
        ValidateWrite(write);

        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        return await UpsertCore(context, connection, transaction: null, write).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntityBatchWriteResult> UpsertBatch(
        OperationContext context,
        EntityBatchWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var writes = request.Writes ?? throw new ArgumentException("Batch write request must include writes.", nameof(request));
        if (!Enum.IsDefined(request.Atomicity))
            throw new ArgumentOutOfRangeException(nameof(request), request.Atomicity, "Unsupported entity batch atomicity.");
        if (!BatchCapabilities.SupportsAtomicity(request.Atomicity))
            throw new NotSupportedException($"Repository '{EntityType}' does not support requested batch atomicity '{request.Atomicity}'.");
        if (writes.Count > mapping.MaximumBatchItems)
        {
            throw new NotSupportedException(
                $"Repository '{EntityType}' accepts at most {mapping.MaximumBatchItems.ToString(CultureInfo.InvariantCulture)} writes per batch.");
        }
        if (writes.Count == 0)
            return new([], request.Atomicity);

        string? requiredPartition = null;
        foreach (var write in writes)
        {
            ValidateWrite(write);
            if (request.Atomicity != EntityBatchAtomicity.SamePartition)
                continue;
            var partition = GetPartitionKey(write.Entity);
            requiredPartition ??= partition;
            if (!string.Equals(requiredPartition, partition, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Repository '{EntityType}' cannot satisfy same-partition atomicity for writes spanning multiple partitions.");
            }
        }

        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);
        EntitySnapshot[] snapshots = new EntitySnapshot[writes.Count];
        try
        {
            for (var index = 0; index < writes.Count; index++)
            {
                snapshots[index] = await UpsertCore(context, connection, transaction, writes[index])
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the write or commit failure; disposal still releases the failed transaction.
            }
            throw;
        }

        return new(snapshots, request.Atomicity);
    }

    async Task<EntitySnapshot> UpsertCore(
        OperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        EntityWriteRequest write)
    {
        var template = write.ExpectedConcurrencyToken is null ? sql.Upsert : sql.Replace;
        await using var command = new NpgsqlCommand(
            template.Text,
            connection,
            transaction);
        AddWriteParameters(command, template, write);

        var token = await command.ExecuteScalarAsync(context.CancellationToken).ConfigureAwait(false);
        if (token is not uint transactionId)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{write.Entity.EntityId.Value}' failed optimistic concurrency validation.");
        }

        return new(
            write.Entity,
            GetPartitionKey(write.Entity),
            new(transactionId.ToString(CultureInfo.InvariantCulture)));
    }

    static void AddReadParameters(
        NpgsqlCommand command,
        SqlCommandTemplate template,
        string identity,
        string? partition)
    {
        foreach (var slot in template.Parameters)
        {
            switch (slot.Binding)
            {
                case PostgresEntityRepositorySql.IdentityBinding:
                    AddTextParameter(command, binding: slot.Binding, value: identity);
                    break;
                case PostgresEntityRepositorySql.PartitionBinding when partition is not null:
                    AddTextParameter(command, binding: slot.Binding, value: partition);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"PostgreSQL entity read template contains unsupported binding '{slot.Binding}'.");
            }
        }
    }

    void AddWriteParameters(
        NpgsqlCommand command,
        SqlCommandTemplate template,
        EntityWriteRequest write)
    {
        foreach (var slot in template.Parameters)
        {
            var bindingName = slot.Binding
                ?? throw new InvalidOperationException("PostgreSQL entity write templates cannot contain constant parameters.");
            if (sql.FieldIndexByBinding.TryGetValue(bindingName, out var fieldIndex))
            {
                var binding = mapping.Fields[fieldIndex];
                var observed = write.Entity.Observation.GetField(binding.FieldName);
                command.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = PostgresRelationQueryScalarCatalog.ToNpgsqlDbType(binding.ScalarType, array: false),
                    Value = ToPostgresValue(observed, binding)
                });
                continue;
            }

            switch (bindingName)
            {
                case PostgresEntityRepositorySql.ObservationVersionBinding:
                    command.Parameters.Add(new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
                        Value = write.Entity.Version
                    });
                    break;
                case PostgresEntityRepositorySql.ExpectedConcurrencyBinding
                    when write.ExpectedConcurrencyToken is { } expected:
                    AddXidParameter(command, binding: bindingName, value: expected.Value);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"PostgreSQL entity write template contains unsupported binding '{bindingName}'.");
            }
        }
    }

    EntitySnapshot ReadSnapshot(NpgsqlDataReader reader, IReadOnlySet<string>? selectedFields)
    {
        var fields = ImmutableArray.CreateBuilder<ObservationValue>(mapping.Fields.Length);
        for (var index = 0; index < mapping.Fields.Length; index++)
        {
            if (reader.IsDBNull(index))
            {
                fields.Add(ObservationValue.Null);
                continue;
            }
            var value = ReadPostgresValue(reader, index, mapping.Fields[index].ScalarType);
            fields.Add(value);
        }

        var version = reader.GetInt64(mapping.Fields.Length);
        var token = reader.GetFieldValue<uint>(mapping.Fields.Length + 1)
            .ToString(CultureInfo.InvariantCulture);
        var identity = fields[identityOrdinal].GetRequiredString();
        var partition = fields[partitionOrdinal].GetRequiredString();
        var observation = Observation.Create(EntityDefinition.StateShape, layout, fields.MoveToImmutable());
        EntityObservationSnapshot complete = new(new(identity), version, observation);
        return CreateValidatedReadSnapshot(
            complete: complete,
            partition: partition,
            concurrencyToken: new(token),
            selectedFields: selectedFields);
    }

    internal EntitySnapshot CreateValidatedReadSnapshot(
        EntityObservationSnapshot complete,
        string partition,
        EntityConcurrencyToken concurrencyToken,
        IReadOnlySet<string>? selectedFields)
    {
        ArgumentNullException.ThrowIfNull(complete);
        _ = EntityDefinition.CreateState(complete);

        if (selectedFields is null)
            return new(complete, partition, concurrencyToken);

        return new(complete, partition, concurrencyToken, selectedFields);
    }

    void ValidateWrite(EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Entity);
        _ = EntityDefinition.CreateState(write.Entity);
        var identity = write.Entity.Observation.GetField(mapping.IdentityField).GetRequiredString();
        if (!string.Equals(identity, write.Entity.EntityId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Observation '{EntityType}:{write.Entity.EntityId.Value}' identity field '{mapping.IdentityField}' contains '{identity}'.");
        }
        _ = GetPartitionKey(write.Entity);
    }

    string GetPartitionKey(EntityObservationSnapshot observation)
    {
        var partition = observation.Observation.GetField(mapping.PartitionField).GetRequiredString();
        return Guard.RequireNotNullOrWhiteSpace(partition);
    }

    void ValidateReadPreconditions(string id, EntitySnapshot snapshot, EntityReadOptions? options)
    {
        if (options?.ExpectedVersion is { } version && snapshot.Entity.Version != version)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{id}' expected version '{version}' but found '{snapshot.Entity.Version}'.");
        }
        if (options?.ExpectedConcurrencyToken is { } token && token != snapshot.ConcurrencyToken)
        {
            throw new ObservationConcurrencyConflictException(
                $"Observation '{EntityType}:{id}' expected concurrency token '{token.Value}' but found '{snapshot.ConcurrencyToken.Value}'.");
        }
    }

    static void ValidateMapping(EntityDefinition definition, PostgresEntityRepositoryMapping mapping)
    {
        HashSet<string> expected = definition.Fields
            .Select(static field => field.Name.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var binding in mapping.Fields)
        {
            if (!expected.Remove(binding.FieldName))
                throw new ArgumentException($"PostgreSQL mapping contains unknown or repeated entity field '{binding.FieldName}'.", nameof(mapping));
            var field = definition.Fields.Single(candidate => candidate.MatchesName(binding.FieldName));
            if (field.Presence != FieldPresence.Required)
            {
                throw new ArgumentException(
                    $"Entity field '{binding.FieldName}' is optional, but the PostgreSQL entity repository does not declare a missing-value encoding.",
                    nameof(mapping));
            }
            if (field.Cardinality != FieldCardinality.Single
                || !PostgresRelationQueryScalarCatalog.TryFromSemanticType(field.Type, out var semanticScalar)
                || semanticScalar != binding.ScalarType)
            {
                throw new ArgumentException(
                    $"Entity field '{binding.FieldName}' cannot be preserved by PostgreSQL scalar '{binding.ScalarType}'.",
                    nameof(mapping));
            }
        }
        if (expected.Count > 0)
            throw new ArgumentException($"PostgreSQL mapping is missing entity field(s): {string.Join(", ", expected.Order(StringComparer.Ordinal))}.", nameof(mapping));

        RequireTextKey(mapping.IdentityField, "identity", definition, mapping);
        RequireTextKey(mapping.PartitionField, "partition", definition, mapping);

        static void RequireTextKey(
            string fieldName,
            string role,
            EntityDefinition definition,
            PostgresEntityRepositoryMapping mapping)
        {
            if (!mapping.FieldByName.TryGetValue(fieldName, out var binding))
                throw new ArgumentException($"PostgreSQL {role} field '{fieldName}' is not bound.", nameof(mapping));
            var field = definition.Fields.Single(candidate => candidate.MatchesName(fieldName));
            if (binding.ScalarType != PostgresRelationQueryScalarType.Text
                || field.Presence != FieldPresence.Required
                || field.Nullability != FieldNullability.NonNullable)
            {
                throw new ArgumentException($"PostgreSQL {role} field '{fieldName}' must be required non-null text.", nameof(mapping));
            }
        }
    }

    static object ToPostgresValue(
        ObservationValue value,
        PostgresEntityRepositoryFieldBinding binding)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return DBNull.Value;
        try
        {
            return binding.ScalarType switch
            {
                PostgresRelationQueryScalarType.Boolean => value.GetBoolean(),
                PostgresRelationQueryScalarType.Int32 => value.GetInt32(),
                PostgresRelationQueryScalarType.Int64 => value.GetInt64(),
                PostgresRelationQueryScalarType.Numeric => value.GetDecimal(),
                PostgresRelationQueryScalarType.Text => SqlUtf8.RequireText(value.GetRequiredString(), binding.FieldName),
                PostgresRelationQueryScalarType.Uuid => Guid.ParseExact(value.GetRequiredString(), "D"),
                PostgresRelationQueryScalarType.Date => value.GetDateOnly(),
                PostgresRelationQueryScalarType.Timestamp => DateTime.SpecifyKind(
                    DateTime.ParseExact(value.GetRequiredString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateTimeKind.Unspecified),
                PostgresRelationQueryScalarType.TimestampWithTimeZone => value.GetDateTimeOffset().ToUniversalTime(),
                PostgresRelationQueryScalarType.Bytea when value.Kind == ObservationValueKind.Bytes => value.Bytes.ToArray(),
                PostgresRelationQueryScalarType.Bytea => throw new InvalidOperationException("A PostgreSQL bytea field requires a bytes observation value."),
                _ => throw new ArgumentOutOfRangeException(nameof(binding), binding.ScalarType, "Unsupported PostgreSQL scalar type.")
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Entity field '{binding.FieldName}' cannot be encoded as PostgreSQL scalar '{binding.ScalarType}'.",
                exception);
        }
    }

    static ObservationValue ReadPostgresValue(
        NpgsqlDataReader reader,
        int ordinal,
        PostgresRelationQueryScalarType scalarType)
    {
        object value = scalarType switch
        {
            PostgresRelationQueryScalarType.Boolean => reader.GetBoolean(ordinal),
            PostgresRelationQueryScalarType.Int32 => reader.GetInt32(ordinal),
            PostgresRelationQueryScalarType.Int64 => reader.GetInt64(ordinal),
            PostgresRelationQueryScalarType.Numeric => reader.GetDecimal(ordinal),
            PostgresRelationQueryScalarType.Text => reader.GetString(ordinal),
            PostgresRelationQueryScalarType.Uuid => reader.GetGuid(ordinal),
            PostgresRelationQueryScalarType.Date => reader.GetFieldValue<DateOnly>(ordinal),
            PostgresRelationQueryScalarType.Timestamp => reader.GetDateTime(ordinal),
            PostgresRelationQueryScalarType.TimestampWithTimeZone => reader.GetFieldValue<DateTime>(ordinal),
            PostgresRelationQueryScalarType.Bytea => reader.GetFieldValue<byte[]>(ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.")
        };
        return PostgresRelationQueryScalarCatalog.ToObservationValue(value, scalarType);
    }

    static void AddTextParameter(NpgsqlCommand command, string binding, string value)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = SqlUtf8.RequireText(value, binding)
        });
    }

    static void AddXidParameter(NpgsqlCommand command, string binding, string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var transactionId))
        {
            throw new ObservationConcurrencyConflictException(
                $"PostgreSQL concurrency binding '{binding}' contains invalid transaction identifier '{value}'.");
        }
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Xid,
            Value = transactionId
        });
    }
}

internal sealed record PostgresEntityRepositorySql(
    SqlCommandTemplate ReadByIdentity,
    SqlCommandTemplate ReadByIdentityAndPartition,
    SqlCommandTemplate Upsert,
    SqlCommandTemplate Replace,
    ImmutableDictionary<string, int> FieldIndexByBinding)
{
    const string SourceAlias = "entity";
    internal const string IdentityBinding = "identity";
    internal const string PartitionBinding = "partition";
    internal const string ObservationVersionBinding = "observation_version";
    internal const string ExpectedConcurrencyBinding = "expected_concurrency";
    const string ConcurrencyColumn = "xmin";
    const string ConcurrencyResultAlias = "concurrency_token";

    internal static PostgresEntityRepositorySql Create(PostgresEntityRepositoryMapping mapping)
    {
        var identity = mapping.FieldByName[mapping.IdentityField];
        var partition = mapping.FieldByName[mapping.PartitionField];
        var readByIdentity = CreateRead(mapping, identity, partition: null);
        var readByIdentityAndPartition = CreateRead(mapping, identity, partition);

        SqlInsertBuilder insert = new(mapping.Table);
        for (var index = 0; index < mapping.Fields.Length; index++)
        {
            insert.Value(
                columnName: mapping.Fields[index].Column.Value,
                value: SqlExpression.RuntimeParameter(FieldBinding(index)));
        }
        insert.Value(
            columnName: mapping.VersionColumn.Value,
            value: SqlExpression.RuntimeParameter(ObservationVersionBinding));
        var conflictColumns = partition.Column == identity.Column
            ? new[] { partition.Column.Value }
            : [partition.Column.Value, identity.Column.Value];
        insert.OnConflictDoUpdate(
            conflictColumns: conflictColumns,
            excludedUpdateColumns:
            [
                .. mapping.Fields.Select(static field => field.Column.Value),
                mapping.VersionColumn.Value
            ]);
        insert.Returning(
            expression: SqlExpression.UnqualifiedColumn(ConcurrencyColumn),
            alias: ConcurrencyResultAlias);

        SqlUpdateBuilder replace = new(mapping.Table);
        for (var index = 0; index < mapping.Fields.Length; index++)
        {
            replace.Set(
                columnName: mapping.Fields[index].Column.Value,
                value: SqlExpression.RuntimeParameter(FieldBinding(index)));
        }
        replace.Set(
            columnName: mapping.VersionColumn.Value,
            value: SqlExpression.RuntimeParameter(ObservationVersionBinding));
        replace.Where(SqlExpression.Binary(
            @operator: SqlBinaryOperator.Equal,
            left: SqlExpression.UnqualifiedColumn(identity.Column.Value),
            right: SqlExpression.RuntimeParameter(FieldBinding(mapping.Fields.IndexOf(identity)))));
        if (partition != identity)
        {
            replace.Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.UnqualifiedColumn(partition.Column.Value),
                right: SqlExpression.RuntimeParameter(FieldBinding(mapping.Fields.IndexOf(partition)))));
        }
        replace.Where(SqlExpression.Binary(
            @operator: SqlBinaryOperator.Equal,
            left: SqlExpression.UnqualifiedColumn(ConcurrencyColumn),
            right: SqlExpression.RuntimeParameter(ExpectedConcurrencyBinding)));
        replace.Returning(
            expression: SqlExpression.UnqualifiedColumn(ConcurrencyColumn),
            alias: ConcurrencyResultAlias);

        return new(
            readByIdentity,
            readByIdentityAndPartition,
            insert.BuildTemplate(PostgresSqlDialect.Instance),
            replace.BuildTemplate(PostgresSqlDialect.Instance),
            mapping.Fields
                .Select(static (_, index) => KeyValuePair.Create(FieldBinding(index), index))
                .ToImmutableDictionary(StringComparer.Ordinal));
    }

    static SqlCommandTemplate CreateRead(
        PostgresEntityRepositoryMapping mapping,
        PostgresEntityRepositoryFieldBinding identity,
        PostgresEntityRepositoryFieldBinding? partition)
    {
        PostgresSqlAliasAllocator aliases = new();
        SqlSelectBuilder selected = new(mapping.Table, SourceAlias);
        foreach (var field in mapping.Fields)
        {
            selected.Select(
                expression: SqlExpression.Column(SourceAlias, field.Column.Value),
                alias: aliases.Allocate(field.Column.Value, $"field:{field.FieldName}", "field"));
        }
        selected.Select(
            expression: SqlExpression.Column(SourceAlias, mapping.VersionColumn.Value),
            alias: aliases.Allocate(mapping.VersionColumn.Value, "observation-version", "version"));
        selected.Select(
            expression: SqlExpression.Column(SourceAlias, ConcurrencyColumn),
            alias: aliases.Allocate(ConcurrencyResultAlias, "concurrency-token", "concurrency"));
        selected.Where(SqlExpression.Binary(
            @operator: SqlBinaryOperator.Equal,
            left: SqlExpression.Column(SourceAlias, identity.Column.Value),
            right: SqlExpression.RuntimeParameter(IdentityBinding)));
        if (partition is not null)
        {
            selected.Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.Column(SourceAlias, partition.Column.Value),
                right: SqlExpression.RuntimeParameter(PartitionBinding)));
        }
        return selected.Limit(2).BuildTemplate(PostgresSqlDialect.Instance);
    }

    internal static string FieldBinding(int index) =>
        $"field_{index.ToString(CultureInfo.InvariantCulture)}";
}
