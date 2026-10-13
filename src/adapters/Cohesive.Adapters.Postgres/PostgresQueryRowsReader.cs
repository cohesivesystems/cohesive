using System.Globalization;
using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using System.Transactions;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Compilation;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Executes one compiled native query-row branch and reconstructs its canonical non-temporal scalar fields.</summary>
/// <remarks>Prepared once, safe for concurrent calls. Each call owns its parameters, connection, command and
/// result budget. The caller owns the data source. This is native statement execution, not a complete canonical
/// evaluation outcome or a paging API. It preserves outer-join absence separately from SQL null.</remarks>
public sealed class PostgresQueryRowsReader : IRelationQueryRowsReader
{
    readonly PostgresRelationQueryCompiledArtifact artifact;
    readonly PostgresNpgsqlRuntimeBinding runtime;
    readonly int maximumRows;
    readonly long maximumBytes;
    readonly PostgresRelationQueryScalarType[] scalarTypes;
    readonly PostgresRelationQueryScalarType?[] parameterTypes;
    readonly ImmutableArray<PostgresRelationQueryScalarType> resultTypes;
    readonly string projection;
    readonly int[][] presenceOrdinals;
    readonly PostgresNpgsqlCommandExecutor executeCommand;

    /// <summary>Prepares a bounded reader for an unpaged query-row artifact and its exact database binding.</summary>
    /// <param name="artifact">Validated native artifact prepared by the PostgreSQL compiler.</param>
    /// <param name="runtime">Explicit database affinity and caller-owned data source.</param>
    /// <param name="maximumRows">Complete-result bound, between one and Int32.MaxValue minus one.</param>
    /// <param name="maximumBytes">Positive budget for all retrieved scalar values, including presence columns; excludes object overhead.</param>
    /// <exception cref="ArgumentNullException">Artifact or runtime is null.</exception>
    /// <exception cref="ArgumentException">Database affinity differs, scalar decoding is unsupported, or the artifact is not an unpaged query-row branch.</exception>
    /// <exception cref="NotSupportedException">A result field requires temporal policy.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A result bound is not positive, or the row bound cannot accommodate an overflow row.</exception>
    public PostgresQueryRowsReader(PostgresRelationQueryCompiledArtifact artifact, PostgresNpgsqlRuntimeBinding runtime,
        int maximumRows, long maximumBytes)
        : this(artifact, runtime, maximumRows, maximumBytes,
            (command, token) => PostgresNpgsqlExecution.ExecuteAsync(runtime.DataSource, command, token)) { }

    internal PostgresQueryRowsReader(PostgresRelationQueryCompiledArtifact artifact, PostgresNpgsqlRuntimeBinding runtime,
        int maximumRows, long maximumBytes, PostgresNpgsqlCommandExecutor executeCommand)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(runtime);
        if (artifact.StorageBinding.Database != runtime.Database)
            throw new ArgumentException("The native artifact and runtime must name the same database.", nameof(runtime));
        if (artifact.Branch.Kind != RelationQueryNativeResultKind.QueryRows || artifact.Paging is not null
            || !artifact.SuppliedFields.IsEmpty || !artifact.Invariants.IsEmpty || artifact.RelationKey is not null)
            throw new ArgumentException("This reader requires an unpaged query-row branch without supplied roots or relation invariants.", nameof(artifact));
        if (maximumRows <= 0 || maximumRows == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.artifact = artifact;
        this.runtime = runtime;
        this.maximumRows = maximumRows;
        this.maximumBytes = maximumBytes;
        scalarTypes = artifact.ResultFields.Select(field =>
            PostgresRelationQueryScalarCatalog.TryFromSemanticType(field.ValueContract.Type, out var scalar)
                ? scalar : throw new ArgumentException("A result field has no exact PostgreSQL scalar decoder.", nameof(artifact))).ToArray();
        if (scalarTypes.Any(PostgresNpgsqlExecution.IsTemporal))
            throw new NotSupportedException("This native row reader does not admit temporal fields; use the source reader with explicit temporal policy.");
        this.executeCommand = executeCommand ?? throw new ArgumentNullException(nameof(executeCommand));
        parameterTypes = artifact.Statement.Parameters.Select(slot => slot.Kind == SqlParameterBindingKind.Runtime
            ? (PostgresRelationQueryScalarType?)RequireScalar(artifact.Parameters.Single(binding => binding.Position == slot.Position).ValueContract)
            : ConstantScalar(slot.Constant!.Kind)).ToArray();
        var presenceIndex = artifact.PresenceBindings.Select((binding, index) => (binding.Binding, Ordinal: scalarTypes.Length + index))
            .ToDictionary(static pair => pair.Binding, static pair => pair.Ordinal);
        presenceOrdinals = artifact.ResultFields.Select(field => field.PresenceDependencies
            .Select(binding => presenceIndex[binding]).ToArray()).ToArray();
        resultTypes = [.. scalarTypes, .. Enumerable.Repeat(PostgresRelationQueryScalarType.Boolean, artifact.PresenceBindings.Length)];
        using var quote = new NpgsqlCommandBuilder();
        projection = string.Join(", ", artifact.ResultFields.Select(field => "bounded_result." + quote.QuoteIdentifier(field.Alias))
            .Concat(artifact.PresenceBindings.Select(binding => "bounded_result." + quote.QuoteIdentifier(binding.Alias))));

    }

    /// <summary>Exact native artifact retained for inspection and attribution.</summary>
    public PostgresRelationQueryCompiledArtifact Artifact => artifact;
    /// <inheritdoc />
    public RelationQueryCompiledPlanReference Plan => artifact.Provenance.Plan;

    /// <summary>Executes exactly one parameterized SELECT, failing rather than returning a truncated result.</summary>
    /// <param name="parameters">Invocation values validated by the compiled artifact.</param>
    /// <param name="cancellationToken">Cancels connection, command and result reads.</param>
    /// <returns>Complete canonical row observations within the configured bounds.</returns>
    /// <exception cref="InvalidOperationException">An ambient transaction exists, a result bound is exceeded, or provider metadata is incompatible.</exception>
    /// <exception cref="ArgumentException">Invocation parameters fail the artifact binding contract.</exception>
    /// <exception cref="NotSupportedException">An invocation value requires temporal policy.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <remarks>Provider and cancellation failures propagate. No retry is performed. Row and decoded-value byte bounds
    /// protect result buffering; they do not impose a database CPU budget. Native command timeout still applies.</remarks>
    public async Task<ImmutableArray<ObservationValue>> ReadAsync(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Transaction.Current is not null) throw new InvalidOperationException("Native query execution does not enlist in an ambient transaction.");
        var statement = artifact.Bind(parameters);
        var bound = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(statement.Parameters.Length + 1);
        for (var index = 0; index < statement.Parameters.Length; index++)
            bound.Add(new(statement.Parameters[index].Value ?? DBNull.Value, parameterTypes[index], IsArray: false));
        bound.Add(new(maximumRows + 1, PostgresRelationQueryScalarType.Int32, IsArray: false));
        var command = new PostgresNpgsqlCommand(
            $"SELECT {projection} FROM ({statement.Text}) AS bounded_result LIMIT ${bound.Count.ToString(CultureInfo.InvariantCulture)}",
            bound.MoveToImmutable(), resultTypes, maximumBytes, maximumRows, PermitTemporalValues: false);
        var raw = await executeCommand(command, cancellationToken).ConfigureAwait(false);
        if (raw.Rows.Length > maximumRows) throw new InvalidOperationException("Native query exceeded its complete-result row bound.");
        var result = ImmutableArray.CreateBuilder<ObservationValue>(raw.Rows.Length);
        foreach (var row in raw.Rows)
        {
            if (row.Length != resultTypes.Length) throw new InvalidOperationException("The native row width differs from its compiled projection.");
            result.Add(ObservationValue.FromFieldAssignments(Fields(row)));
        }
        return result.MoveToImmutable();
    }

    IEnumerable<KeyValuePair<FieldPath, ObservationValue>> Fields(ImmutableArray<object?> row)
    {
        for (var index = 0; index < artifact.ResultFields.Length; index++)
        {
            var field = artifact.ResultFields[index];
            if (!IsPresent(row, presenceOrdinals[index])) continue;
            yield return new(field.Field.Path, row[index] is null ? ObservationValue.Null
                : PostgresRelationQueryScalarCatalog.ToObservationValue(row[index], scalarTypes[index]));
        }
    }

    static bool IsPresent(ImmutableArray<object?> row, int[] dependencies)
    {
        foreach (var ordinal in dependencies) if (row[ordinal] is not true) return false;
        return true;
    }

    static PostgresRelationQueryScalarType RequireScalar(ValueContract contract) =>
        PostgresRelationQueryScalarCatalog.TryFromSemanticType(contract.Type, out var scalar)
            ? scalar : throw new ArgumentException("A parameter has no exact PostgreSQL scalar encoding.");

    static PostgresRelationQueryScalarType? ConstantScalar(SqlConstantKind kind) => kind switch
    {
        SqlConstantKind.Null => null,
        SqlConstantKind.Boolean => PostgresRelationQueryScalarType.Boolean,
        SqlConstantKind.Int32 => PostgresRelationQueryScalarType.Int32,
        SqlConstantKind.Int64 => PostgresRelationQueryScalarType.Int64,
        SqlConstantKind.Decimal => PostgresRelationQueryScalarType.Numeric,
        SqlConstantKind.String => PostgresRelationQueryScalarType.Text,
        SqlConstantKind.Uuid => PostgresRelationQueryScalarType.Uuid,
        SqlConstantKind.Date => PostgresRelationQueryScalarType.Date,
        SqlConstantKind.Timestamp => PostgresRelationQueryScalarType.Timestamp,
        SqlConstantKind.TimestampWithTimeZone => PostgresRelationQueryScalarType.TimestampWithTimeZone,
        SqlConstantKind.Bytes => PostgresRelationQueryScalarType.Bytea,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
