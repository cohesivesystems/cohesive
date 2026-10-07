using System.Collections.Immutable;
using System.Transactions;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Compilation;
using Npgsql;
using NpgsqlTypes;

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

    /// <summary>Prepares a bounded reader for an unpaged query-row artifact and its exact database binding.</summary>
    /// <param name="artifact">Validated native artifact prepared by the PostgreSQL compiler.</param>
    /// <param name="runtime">Explicit database affinity and caller-owned data source.</param>
    /// <param name="maximumRows">Complete-result bound, between one and Int32.MaxValue minus one.</param>
    /// <param name="maximumBytes">Positive decoded scalar-value byte budget; excludes object overhead.</param>
    /// <exception cref="ArgumentNullException">Artifact or runtime is null.</exception>
    /// <exception cref="ArgumentException">Database affinity differs, scalar decoding is unsupported, or the artifact is not an unpaged query-row branch.</exception>
    /// <exception cref="NotSupportedException">A result field requires temporal policy.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A result bound is not positive, or the row bound cannot accommodate an overflow row.</exception>
    public PostgresQueryRowsReader(PostgresRelationQueryCompiledArtifact artifact, PostgresNpgsqlRuntimeBinding runtime,
        int maximumRows, long maximumBytes)
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
        if (statement.Parameters.Any(parameter => parameter.Value is DateOnly or DateTime or DateTimeOffset))
            throw new NotSupportedException("This native row reader does not admit temporal parameters.");
        // The outer limit obtains one overflow row without changing the canonical query's semantics.
        await using var command = runtime.DataSource.CreateCommand($"SELECT * FROM ({statement.Text}) AS bounded_result LIMIT {maximumRows + 1}");
        foreach (var parameter in statement.Parameters)
        {
            var value = parameter.Value;
            command.Parameters.Add(value is null
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = DBNull.Value }
                : new NpgsqlParameter { Value = value });
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ordinals = artifact.ResultFields.Select(field => reader.GetOrdinal(field.Alias)).ToArray();
        var presenceOrdinals = artifact.PresenceBindings.ToDictionary(binding => binding.Binding, binding => reader.GetOrdinal(binding.Alias));
        var budget = new PostgresNpgsqlResultBudget(maximumBytes);
        var result = ImmutableArray.CreateBuilder<ObservationValue>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (result.Count == maximumRows) throw new InvalidOperationException("Native query exceeded its complete-result row bound.");
            var row = ObservationValue.FromObject(new Dictionary<string, ObservationValue>());
            for (var index = 0; index < artifact.ResultFields.Length; index++)
            {
                var field = artifact.ResultFields[index];
                if (field.PresenceDependencies.Any(binding => reader.IsDBNull(presenceOrdinals[binding])
                    || !reader.GetBoolean(presenceOrdinals[binding]))) continue;
                var ordinal = ordinals[index];
                var value = await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
                    ? ObservationValue.Null
                    : PostgresRelationQueryScalarCatalog.ToObservationValue(
                        await PostgresRelationQueryScalarCatalog.ReadAsync(reader, ordinal, scalarTypes[index], budget, cancellationToken).ConfigureAwait(false),
                        scalarTypes[index]);
                row = row.WithField(field.Field.Path, value);
            }
            result.Add(row);
        }
        return result.ToImmutable();
    }
}
