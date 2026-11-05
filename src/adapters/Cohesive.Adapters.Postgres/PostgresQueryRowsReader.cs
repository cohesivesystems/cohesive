using System.Globalization;
using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using System.Transactions;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Diagnostics;
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
    readonly PostgresRelationQueryScalarType[] contributorTypes;
    readonly int[] contributorPresence;
    readonly string[] occurrencePrefixes;
    readonly string attributedProjection;
    readonly ImmutableArray<PostgresRelationQueryScalarType> attributedTypes;
    readonly ObservationLayout? ordinalLayout;
    readonly GraphShapeId outputShape;

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
        contributorTypes = artifact.Contributors.Select(contributor => artifact.StorageBinding.ResolveTable(contributor.PlacementBinding).Identity!.ScalarType).ToArray();
        if (contributorTypes.Any(PostgresNpgsqlExecution.IsTemporal))
            throw new NotSupportedException("Contributor identities require an explicit temporal policy.");
        contributorPresence = artifact.Contributors.Select(contributor => presenceIndex.GetValueOrDefault(contributor.Binding, -1)).ToArray();
        resultTypes = [.. scalarTypes, .. Enumerable.Repeat(PostgresRelationQueryScalarType.Boolean, artifact.PresenceBindings.Length), .. contributorTypes];
        occurrencePrefixes = artifact.Contributors.Select(contributor =>
            $"postgres/{Uri.EscapeDataString(contributor.PlacementBinding.Value)}/{Uri.EscapeDataString(contributor.Binding.Value)}/").ToArray();
        attributedTypes = [.. Enumerable.Repeat(PostgresRelationQueryScalarType.Boolean, artifact.PresenceBindings.Length), .. contributorTypes, .. scalarTypes];
        using var quote = new NpgsqlCommandBuilder();
        attributedProjection = string.Join(", ", artifact.PresenceBindings.Select(binding => "bounded_result." + quote.QuoteIdentifier(binding.Alias))
            .Concat(artifact.Contributors.Select(binding => "bounded_result." + quote.QuoteIdentifier(binding.Alias)))
            .Concat(artifact.ResultFields.Select(field => "bounded_result." + quote.QuoteIdentifier(field.Alias))));
        projection = string.Join(", ", artifact.ResultFields.Select(field => "bounded_result." + quote.QuoteIdentifier(field.Alias))
            .Concat(artifact.PresenceBindings.Select(binding => "bounded_result." + quote.QuoteIdentifier(binding.Alias)))
            .Concat(artifact.Contributors.Select(binding => "bounded_result." + quote.QuoteIdentifier(binding.Alias))));

    }

    /// <summary>Prepares native ordinal query-row admission retaining exact join contributors.</summary>
    /// <param name="artifact">Contributor-bearing unpaged query-row artifact.</param>
    /// <param name="runtime">Exact database binding and caller-owned data source.</param>
    /// <param name="plan">Exact compiled semantic plan retained by the artifact, including its shape snapshots.</param>
    /// <param name="maximumRows">Positive complete-result row bound below Int32.MaxValue.</param>
    /// <param name="maximumBytes">Positive cumulative provider scalar byte budget, including hidden metadata.</param>
    /// <exception cref="ArgumentException">The plan revision differs, contributors or direct fields are absent, or required output fields are not demanded.</exception>
    /// <exception cref="ArgumentNullException">A required artifact, runtime or plan reference is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    /// <exception cref="NotSupportedException">A scalar requires temporal policy.</exception>
    public PostgresQueryRowsReader(PostgresRelationQueryCompiledArtifact artifact, PostgresNpgsqlRuntimeBinding runtime,
        CompiledRelationQueryPlan plan, int maximumRows, long maximumBytes)
        : this(artifact, runtime, maximumRows, maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Equals(RelationQueryCompiledPlanReferenceFingerprinter.Compute(artifact.Provenance.Plan),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(plan))))
            throw new ArgumentException("Reader plan differs from the artifact's exact semantic revision.", nameof(plan));
        var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == artifact.Branch.Shape.GraphId).Graph;
        var shape = new GraphShapeId(graph, artifact.Branch.Shape.ShapeId);
        if (artifact.Contributors.IsEmpty || shape.QualifiedId != artifact.Branch.Shape
            || artifact.ResultFields.Any(field => !field.Field.Path.TryGetDirectFieldName(out _)))
            throw new ArgumentException("Ordinal query-row admission requires exact contributors and a matching direct-field shape.", nameof(plan));
        var names = artifact.ResultFields.Select(field => field.Field.Path.Segments[0].Segment!).ToHashSet(StringComparer.Ordinal);
        if (graph.GetShape(shape.ShapeId).Fields.Any(field => field.Presence == FieldPresence.Required && !names.Contains(field.Name.Value)))
            throw new ArgumentException("Complete-shape admission requires every required output field in the demand slice.", nameof(plan));
        outputShape = shape;
        ordinalLayout = ObservationLayout.Create(shape, artifact.ResultFields.Select(field => field.Field.Path.Segments[0].Segment!));
    }

    /// <summary>Bound ordinal output layout, or null for legacy value-only readers.</summary>
    public ObservationLayout? Layout => ordinalLayout;

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
        if (!artifact.Contributors.IsEmpty)
            throw new InvalidOperationException("Use ReadRowsAsync to retain contributor-bearing query results.");
        var command = CreateCommand(parameters, cancellationToken);
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

    PostgresNpgsqlCommand CreateCommand(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters,
        CancellationToken cancellationToken, bool attributed = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Transaction.Current is not null) throw new InvalidOperationException("Native query execution does not enlist in an ambient transaction.");
        var statement = artifact.Bind(parameters);
        var bound = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(statement.Parameters.Length + 1);
        for (var index = 0; index < statement.Parameters.Length; index++)
            bound.Add(new(statement.Parameters[index].Value ?? DBNull.Value, parameterTypes[index], IsArray: false));
        bound.Add(new(maximumRows + 1, PostgresRelationQueryScalarType.Int32, IsArray: false));
        return new PostgresNpgsqlCommand(
            $"SELECT {(attributed ? attributedProjection : projection)} FROM ({statement.Text}) AS bounded_result LIMIT ${bound.Count.ToString(CultureInfo.InvariantCulture)}",
            bound.MoveToImmutable(), attributed ? attributedTypes : resultTypes, maximumBytes, maximumRows, PermitTemporalValues: false);
    }

    /// <summary>Executes a bounded statement directly into validated ordinal values with exact source contributors.</summary>
    /// <param name="parameters">Canonical invocation parameters checked by the artifact.</param>
    /// <param name="cancellationToken">Cancels execution and provider reads.</param>
    /// <returns>Complete owned output rows, including contributing occurrence identities; no provider buffers escape.</returns>
    /// <remarks>Uses one statement snapshot. No retries or partial result publication. Hidden identity/presence columns
    /// consume the same byte budget as visible values. Pool acquisition and provider failures propagate.</remarks>
    /// <exception cref="InvalidOperationException">The reader lacks a bound shape, metadata is inconsistent, or a result bound is exceeded.</exception>
    /// <exception cref="ArgumentException">Invocation parameters or decoded values violate their governing contracts.</exception>
    /// <exception cref="PostgresNpgsqlResultByteLimitExceededException">Visible values and hidden metadata exceed the cumulative byte budget.</exception>
    /// <exception cref="OperationCanceledException">Execution is canceled.</exception>
    public Task<ImmutableArray<RelationQueryOutputRow>> ReadRowsAsync(
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters, CancellationToken cancellationToken = default)
        => ReadRowsCoreAsync(parameters, ordinalStorage: true, cancellationToken);

    internal async Task<ImmutableArray<RelationQueryOutputRow>> ReadRowsCoreAsync(
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters, bool ordinalStorage,
        CancellationToken cancellationToken = default)
    {
        if (ordinalLayout is null) throw new InvalidOperationException("Bind an output shape before reading attributed rows.");
        var command = CreateCommand(parameters, cancellationToken, attributed: true);
        return await PostgresNpgsqlExecution.ExecuteRowsAsync(runtime.DataSource, command, DecodeRow, cancellationToken).ConfigureAwait(false);

        async ValueTask<RelationQueryOutputRow> DecodeRow(NpgsqlDataReader reader, PostgresNpgsqlResultBudget budget, CancellationToken token)
        {
            // Metadata leads on the wire, so both storage realizations can admit fields directly without a staging vector.
            var present = new bool[artifact.PresenceBindings.Length];
            for (var index = 0; index < present.Length; index++)
            {
                var column = index;
                if (!await reader.IsDBNullAsync(column, token).ConfigureAwait(false))
                    present[index] = (bool)await PostgresRelationQueryScalarCatalog.ReadAsync(reader, column,
                        PostgresRelationQueryScalarType.Boolean, budget, token).ConfigureAwait(false);
            }
            ObservationValue? outputIdentity = null;
            var occurrences = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>(artifact.Contributors.Length);
            for (var index = 0; index < artifact.Contributors.Length; index++)
            {
                var contributor = artifact.Contributors[index];
                var column = present.Length + index;
                var absent = contributorPresence[index] >= 0 && !present[contributorPresence[index] - scalarTypes.Length];
                var isNull = await reader.IsDBNullAsync(column, token).ConfigureAwait(false);
                if (absent != isNull)
                    throw new InvalidOperationException("Contributor identity disagrees with source presence.");
                if (absent) continue;
                var raw = await PostgresRelationQueryScalarCatalog.ReadAsync(reader, column, contributorTypes[index], budget, token).ConfigureAwait(false);
                var identity = PostgresRelationQueryScalarCatalog.FormatKey(raw, contributorTypes[index]);
                if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("Contributor identity is empty.");
                if (contributor.Binding == artifact.Branch.Binding) outputIdentity = ObservationValue.FromString(identity);
                occurrences.Add(new(new(occurrencePrefixes[index] + Uri.EscapeDataString(identity)),
                    contributor.Binding, contributor.Shape, identity));
            }
            var values = ordinalStorage ? ImmutableArray.CreateBuilder<ObservationValue>(scalarTypes.Length) : null;
            var fields = ordinalStorage ? null : new Dictionary<string, ObservationValue>(scalarTypes.Length, StringComparer.Ordinal);
            for (var index = 0; index < scalarTypes.Length; index++)
            {
                var column = present.Length + contributorTypes.Length + index;
                var value = await reader.IsDBNullAsync(column, token).ConfigureAwait(false) ? ObservationValue.Null
                    : PostgresRelationQueryScalarCatalog.ToObservationValue(
                        await PostgresRelationQueryScalarCatalog.ReadAsync(reader, column, scalarTypes[index], budget, token).ConfigureAwait(false), scalarTypes[index]);
                var field = artifact.ResultFields[index];
                var available = true;
                foreach (var dependency in presenceOrdinals[index]) available &= present[dependency - scalarTypes.Length];
                if (!available)
                {
                    if (value.Kind != ObservationValueKind.Null)
                        throw new InvalidOperationException("An absent binding carries a visible payload.");
                    value = ObservationValue.Undefined;
                }
                else if (value.Kind == ObservationValueKind.Null && field.ValueContract.Nullability == FieldNullability.NonNullable)
                {
                    if (field.ValueContract.Presence == FieldPresence.Optional) value = ObservationValue.Undefined;
                    else throw new ArgumentException("Native result violates its compiled non-null contract.");
                }
                if (value.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
                    && ValueContractSemantics.Evaluate(field.ValueContract.Type, value) != ValueConstantCompatibility.Compatible)
                    throw new ArgumentException("Native result violates its compiled semantic value type.");
                if (values is not null) values.Add(value);
                else if (value.Kind != ObservationValueKind.Undefined) fields!.Add(ordinalLayout.FieldIdentities[index], value);
            }
            token.ThrowIfCancellationRequested();
            var observation = values is not null ? Observation.Create(outputShape, ordinalLayout, values.MoveToImmutable())
                : Observation.Create(outputShape, fields!);
            return new(outputShape.QualifiedId, observation.Value, identity: outputIdentity, root: null,
                occurrences.Count == occurrences.Capacity ? occurrences.MoveToImmutable() : occurrences.ToImmutable(), unresolvedGaps: []);
        }
    }

    IEnumerable<KeyValuePair<FieldPath, ObservationValue>> Fields(ImmutableArray<object?> row)
    {
        for (var index = 0; index < artifact.ResultFields.Length; index++)
        {
            var field = artifact.ResultFields[index];
            if (!IsPresent(row, presenceOrdinals[index])) continue;
            yield return new(field.Field.Path, row[index] is { } raw
                ? PostgresRelationQueryScalarCatalog.ToObservationValue(raw, scalarTypes[index]) : ObservationValue.Null);
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
