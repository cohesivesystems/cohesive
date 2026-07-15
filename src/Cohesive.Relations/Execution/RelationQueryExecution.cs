using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>Completion state of one canonical relation/query interpretation.</summary>
public enum RelationQueryExecutionStatus
{
    /// <summary>Every demanded output was evaluated conclusively under the selected policy.</summary>
    Succeeded = 0,

    /// <summary>
    /// Execution produced attributable partial results, but partial evidence or an unresolved impact
    /// prevents the result from being treated as conclusive.
    /// </summary>
    Incomplete = 1,

    /// <summary>Invalid runtime evidence or an execution-contract violation prevented trustworthy results.</summary>
    Failed = 2
}

/// <summary>Semantic kind of one named query result branch.</summary>
public enum RelationQueryExecutionResultKind
{
    /// <summary>The branch returns logical row values.</summary>
    Rows = 0,

    /// <summary>The branch returns shaped aggregation values.</summary>
    Aggregation = 1
}

/// <summary>Disposition state of one demanded relation or named-query terminal.</summary>
public enum RelationQueryExecutionOutputState
{
    /// <summary>
    /// Requirement-gap policy evaluation for the terminal is conclusive. Overall execution may still fail
    /// validation; consumers must also inspect <see cref="RelationQueryExecutionResult.Status"/>.
    /// </summary>
    Complete = 0,

    /// <summary>The selected requirement-gap policy explicitly suppressed the terminal or all affected rows.</summary>
    Suppressed = 1,

    /// <summary>The terminal retains attributable rows but one or more requirement gaps remain unresolved.</summary>
    Incomplete = 2
}

/// <summary>Request for target-independent interpretation of one compiled relation or query.</summary>
public sealed class RelationQueryExecutionRequest
{
    /// <summary>Creates an execution request.</summary>
    /// <param name="plan">Successful target-independent compiled plan to interpret.</param>
    /// <param name="evidence">Materialized runtime inputs attributed to <paramref name="plan"/>.</param>
    /// <param name="requirementGapPolicy">
    /// Policy applied to runtime requirement-gap impacts, or <see langword="null"/> to use
    /// <see cref="RelationRequirementGapPolicy.Conventional"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    public RelationQueryExecutionRequest(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence,
        IRelationRequirementGapPolicy? requirementGapPolicy = null
        )
    {
        Plan = Guard.RequireNotNull(plan);
        Evidence = Guard.RequireNotNull(evidence);
        RequirementGapPolicy = requirementGapPolicy ?? RelationRequirementGapPolicy.Conventional;
    }

    /// <summary>Successful compiled plan whose demand-scoped semantics will be interpreted.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Materialized, plan-attributed runtime input snapshot.</summary>
    public RelationQueryRuntimeEvidence Evidence { get; }

    /// <summary>Explicit or convention-derived requirement-gap policy used for this execution.</summary>
    public IRelationRequirementGapPolicy RequirementGapPolicy { get; }
}

/// <summary>One shaped output row produced by canonical relation/query interpretation.</summary>
public sealed record RelationQueryOutputRow
{
    /// <summary>Creates one shaped, provenance-attributed execution row.</summary>
    /// <param name="shape">Graph-qualified semantic shape of <paramref name="value"/>.</param>
    /// <param name="value">Object-shaped output value containing the retained fields.</param>
    /// <param name="identity">Concrete scalar output identity, or <see langword="null"/>.</param>
    /// <param name="root">Relation root occurrence, or <see langword="null"/> for query and set outputs.</param>
    /// <param name="inputOccurrences">Exact source occurrences that contributed to the row.</param>
    /// <param name="unresolvedGaps">Unresolved requirement gaps still affecting this row.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is incomplete, <paramref name="value"/> is not object-shaped,
    /// <paramref name="identity"/> is not a concrete scalar, or occurrence definitions conflict.
    /// </exception>
    public RelationQueryOutputRow(
        QualifiedShapeId shape,
        ObservationValue value,
        ObservationValue? identity,
        RelationQueryObservationOccurrence? root,
        ImmutableArray<RelationQueryObservationOccurrence> inputOccurrences,
        ImmutableArray<RelationRequirementGapId> unresolvedGaps)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An output row requires a graph-qualified shape.", nameof(shape));
        if (value.Kind != ObservationValueKind.Object)
            throw new ArgumentException("A shaped output row requires an object value.", nameof(value));
        if (identity is { } outputIdentity
            && outputIdentity.Kind is ObservationValueKind.Undefined
                or ObservationValueKind.Null
                or ObservationValueKind.Array
                or ObservationValueKind.Object)
        {
            throw new ArgumentException("An output identity must be a concrete scalar value.", nameof(identity));
        }
        var normalizedOccurrences = inputOccurrences.IsDefault ? [] : inputOccurrences;
        if (normalizedOccurrences.Any(static occurrence => occurrence is null))
            throw new ArgumentException("Input occurrences cannot contain null entries.", nameof(inputOccurrences));
        if (normalizedOccurrences
            .GroupBy(static occurrence => occurrence.Id)
            .Any(static group => group.Skip(1).Any(candidate => !Equals(candidate, group.First()))))
        {
            throw new ArgumentException(
                "Input occurrences cannot contain conflicting definitions for one occurrence identity.",
                nameof(inputOccurrences));
        }
        if (root is not null
            && !normalizedOccurrences.Any(occurrence =>
                occurrence.Id == root.Id && Equals(occurrence, root)))
        {
            throw new ArgumentException(
                "A relation root must be present in the row's contributing occurrences.",
                nameof(root));
        }
        var normalizedGaps = unresolvedGaps.IsDefault ? [] : unresolvedGaps;
        if (normalizedGaps.Any(static gap => string.IsNullOrWhiteSpace(gap.Value)))
            throw new ArgumentException("Unresolved gap identities cannot be empty.", nameof(unresolvedGaps));

        Shape = shape;
        Value = value;
        Identity = identity;
        Root = root;
        InputOccurrences =
        [
            .. normalizedOccurrences
                .GroupBy(static occurrence => occurrence.Id)
                .Select(static group => group.First())
                .OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal)
        ];
        UnresolvedGaps =
        [
            .. normalizedGaps
                .Distinct()
                .OrderBy(static gap => gap.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Graph-qualified semantic shape of <see cref="Value"/>.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Object-shaped semantic value containing exactly the fields retained for this output.</summary>
    public ObservationValue Value { get; }

    /// <summary>Declared relation output identity or preserved source identity, when one exists.</summary>
    public ObservationValue? Identity { get; }

    /// <summary>Relation root occurrence from which this row was derived, or <see langword="null"/>.</summary>
    public RelationQueryObservationOccurrence? Root { get; }

    /// <summary>
    /// Source and traversal occurrences that contributed to the row, in deterministic occurrence order.
    /// </summary>
    public ImmutableArray<RelationQueryObservationOccurrence> InputOccurrences { get; }

    /// <summary>Unresolved requirement gaps that still affect this row under the selected policy.</summary>
    public ImmutableArray<RelationRequirementGapId> UnresolvedGaps { get; }

    /// <summary>
    /// Whether every row-attributed requirement-gap impact was resolved or dispositioned. Global partial
    /// evidence or another execution diagnostic can still make the containing terminal incomplete or failed.
    /// </summary>
    public bool IsComplete => UnresolvedGaps.IsDefaultOrEmpty;
}

/// <summary>Demanded output of a canonical relation definition.</summary>
public sealed class RelationQueryRelationResult
{
    /// <summary>Creates the demanded terminal result of a canonical relation.</summary>
    /// <param name="relation">Canonical relation identity.</param>
    /// <param name="shape">Graph-qualified output shape.</param>
    /// <param name="mode">Declared output cardinality relative to relation roots.</param>
    /// <param name="state">Gap-policy disposition state of the terminal.</param>
    /// <param name="rows">Deterministically ordered output rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> or <paramref name="state"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="relation"/> or <paramref name="shape"/> is invalid, a row is null or has another shape,
    /// or row root attribution conflicts with <paramref name="mode"/>.
    /// </exception>
    public RelationQueryRelationResult(
        RelationId relation,
        QualifiedShapeId shape,
        RelationOutputMode mode,
        RelationQueryExecutionOutputState state,
        ImmutableArray<RelationQueryOutputRow> rows)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported relation output mode.");
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported execution output state.");
        if (string.IsNullOrWhiteSpace(relation.Value))
            throw new ArgumentException("A relation result requires a relation identity.", nameof(relation));
        RequireShape(shape, nameof(shape));
        var normalizedRows = NormalizeRows(rows, shape, nameof(rows));
        if (mode == RelationOutputMode.Set
            && normalizedRows.Any(static row => row.Root is not null))
        {
            throw new ArgumentException("Set relation rows cannot carry per-root attribution.", nameof(rows));
        }
        if (mode != RelationOutputMode.Set
            && normalizedRows.Any(static row => row.Root is null))
        {
            throw new ArgumentException("Rooted relation rows require relation-root attribution.", nameof(rows));
        }
        Relation = relation;
        Shape = shape;
        Mode = mode;
        State = state;
        Rows = normalizedRows;
    }

    /// <summary>Canonical relation whose output was interpreted.</summary>
    public RelationId Relation { get; }

    /// <summary>Graph-qualified output shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Declared output cardinality relative to relation roots.</summary>
    public RelationOutputMode Mode { get; }

    /// <summary>
    /// Gap-policy disposition of this terminal. This state does not supersede the enclosing execution status.
    /// </summary>
    public RelationQueryExecutionOutputState State { get; }

    /// <summary>Output rows in deterministic interpreter order.</summary>
    public ImmutableArray<RelationQueryOutputRow> Rows { get; }

    internal static void RequireShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("An execution result requires a graph-qualified shape.", parameterName);
        }
    }

    internal static ImmutableArray<RelationQueryOutputRow> NormalizeRows(
        ImmutableArray<RelationQueryOutputRow> rows,
        QualifiedShapeId shape,
        string parameterName)
    {
        var normalized = rows.IsDefault ? [] : rows;
        if (normalized.Any(static row => row is null))
            throw new ArgumentException("Execution rows cannot contain null entries.", parameterName);
        if (normalized.Any(row => row.Shape != shape))
            throw new ArgumentException("Every execution row must match the terminal shape.", parameterName);
        return normalized;
    }
}

/// <summary>One demanded named result branch of a canonical query definition.</summary>
public sealed class RelationQueryNamedResult
{
    /// <summary>Creates one demanded named query-result branch.</summary>
    /// <param name="result">Stable canonical result identity.</param>
    /// <param name="kind">Rows or aggregation branch kind.</param>
    /// <param name="shape">Graph-qualified shape of every row.</param>
    /// <param name="state">Gap-policy disposition state of the branch.</param>
    /// <param name="rows">Deterministically ordered output rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> or <paramref name="state"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> or <paramref name="shape"/> is invalid, or a row is null or has another shape.
    /// </exception>
    public RelationQueryNamedResult(
        QueryResultId result,
        RelationQueryExecutionResultKind kind,
        QualifiedShapeId shape,
        RelationQueryExecutionOutputState state,
        ImmutableArray<RelationQueryOutputRow> rows)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported query execution result kind.");
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported execution output state.");
        if (string.IsNullOrWhiteSpace(result.Value))
            throw new ArgumentException("A named query result requires a result identity.", nameof(result));
        RelationQueryRelationResult.RequireShape(shape, nameof(shape));
        Result = result;
        Kind = kind;
        Shape = shape;
        State = state;
        Rows = RelationQueryRelationResult.NormalizeRows(rows, shape, nameof(rows));
    }

    /// <summary>Stable canonical result identifier.</summary>
    public QueryResultId Result { get; }

    /// <summary>Whether this branch represents logical rows or aggregation values.</summary>
    public RelationQueryExecutionResultKind Kind { get; }

    /// <summary>Graph-qualified shape of every returned row.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>
    /// Gap-policy disposition of this branch. This state does not supersede the enclosing execution status.
    /// </summary>
    public RelationQueryExecutionOutputState State { get; }

    /// <summary>Result rows in deterministic interpreter order.</summary>
    public ImmutableArray<RelationQueryOutputRow> Rows { get; }
}

/// <summary>Immutable result of one canonical in-memory relation/query interpretation.</summary>
public sealed class RelationQueryExecutionResult
{
    /// <summary>Creates the immutable result of one canonical interpretation.</summary>
    /// <param name="status">Overall execution status.</param>
    /// <param name="evidence">Exact materialized evidence interpreted by this execution.</param>
    /// <param name="requirementGapAnalysis">Gap analysis and policy decisions for <paramref name="evidence"/>.</param>
    /// <param name="relation">
    /// Relation terminal result, or <see langword="null"/> for a query or an execution that failed before
    /// relation-terminal materialization.
    /// </param>
    /// <param name="queryResults">
    /// Demanded named query results, or an empty array for a relation or an execution that failed before
    /// query-terminal materialization.
    /// </param>
    /// <param name="diagnostics">Combined evidence, gap-policy, and execution diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evidence"/> or <paramref name="requirementGapAnalysis"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Query results or diagnostics contain null or duplicate entries, relation and query terminals are supplied
    /// together, or a non-failed result contains no terminal.
    /// </exception>
    public RelationQueryExecutionResult(
        RelationQueryExecutionStatus status,
        RelationQueryRuntimeEvidence evidence,
        RelationRequirementGapAnalysisResult requirementGapAnalysis,
        RelationQueryRelationResult? relation,
        ImmutableArray<RelationQueryNamedResult> queryResults,
        ImmutableArray<RelationRuntimeDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported execution status.");
        Status = status;
        Evidence = Guard.RequireNotNull(evidence);
        RequirementGapAnalysis = Guard.RequireNotNull(requirementGapAnalysis);
        var normalizedResults = queryResults.IsDefault ? [] : queryResults;
        if (normalizedResults.Any(static result => result is null))
            throw new ArgumentException("Query results cannot contain null entries.", nameof(queryResults));
        if (normalizedResults.GroupBy(static result => result.Result).Any(static group => group.Count() > 1))
            throw new ArgumentException("Query results cannot repeat a result identity.", nameof(queryResults));
        if (relation is not null && !normalizedResults.IsDefaultOrEmpty)
            throw new ArgumentException("An execution result cannot contain relation and query terminals together.", nameof(queryResults));
        if (status != RelationQueryExecutionStatus.Failed
            && relation is null
            && normalizedResults.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A non-failed execution result requires one relation or query terminal.", nameof(queryResults));
        }
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Execution diagnostics cannot contain null entries.", nameof(diagnostics));
        if (normalizedDiagnostics.GroupBy(static diagnostic => diagnostic).Any(static group => group.Count() > 1))
            throw new ArgumentException("Execution diagnostics cannot contain duplicate entries.", nameof(diagnostics));
        Relation = relation;
        QueryResults = [.. normalizedResults.OrderBy(static result => result.Result.Value, StringComparer.Ordinal)];
        Diagnostics =
        [
            .. normalizedDiagnostics
                .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Evaluation.Value, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Occurrence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Gap?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Output?.Id.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.EvidenceReference ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];
    }

    /// <summary>Whether execution succeeded, remained incomplete, or failed.</summary>
    public RelationQueryExecutionStatus Status { get; }

    /// <summary>
    /// Whether all demanded outputs were evaluated conclusively under policy. Reported error diagnostics can
    /// still be present when a policy both resolves and reports a requirement gap.
    /// </summary>
    public bool IsSuccessful => Status == RelationQueryExecutionStatus.Succeeded;

    /// <summary>Stable identity of the runtime evaluation.</summary>
    public RelationQueryEvaluationId Evaluation => Evidence.Evaluation;

    /// <summary>Exact compiled input-contract attribution carried by <see cref="Evidence"/>.</summary>
    public RelationQueryRuntimePlanReference PlanReference => Evidence.PlanReference;

    /// <summary>Exact plan-attributed materialized evidence supplied to this execution.</summary>
    public RelationQueryRuntimeEvidence Evidence { get; }

    /// <summary>Causal requirement gaps and per-impact policy decisions for <see cref="Evidence"/>.</summary>
    public RelationRequirementGapAnalysisResult RequirementGapAnalysis { get; }

    /// <summary>
    /// Relation output, or <see langword="null"/> when the definition is a query or relation execution failed
    /// before a terminal could be materialized.
    /// </summary>
    public RelationQueryRelationResult? Relation { get; }

    /// <summary>
    /// Demanded named query results, or an empty array for a relation or a query that failed before terminals
    /// could be materialized.
    /// </summary>
    public ImmutableArray<RelationQueryNamedResult> QueryResults { get; }

    /// <summary>
    /// Combined evidence, requirement-gap, policy, and execution diagnostics in deterministic order.
    /// </summary>
    public ImmutableArray<RelationRuntimeDiagnostic> Diagnostics { get; }

    /// <summary>Whether any combined diagnostic has error severity.</summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}

/// <summary>Interprets a successful canonical relation/query plan over materialized runtime evidence.</summary>
public interface IRelationQueryInterpreter
{
    /// <summary>Executes the demand-scoped compiled plan without acquiring external data.</summary>
    /// <param name="request">Compiled plan, materialized evidence, and requirement-gap policy.</param>
    /// <param name="cancellationToken">Token observed between nodes and during potentially large row operations.</param>
    /// <returns>Attributable relation or query outputs together with gaps and diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The configured requirement-gap policy exposes a default or empty identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured requirement-gap policy exposes an unsupported policy source.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured requirement-gap policy returns no choice for an impact, or a plan shape snapshot cannot
    /// be represented by the runtime-attribution canonicalization profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A plan shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A plan shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    /// <remarks>Exceptions thrown by a caller-supplied requirement-gap policy propagate unchanged.</remarks>
    RelationQueryExecutionResult Execute(RelationQueryExecutionRequest request, CancellationToken cancellationToken = default);
}
