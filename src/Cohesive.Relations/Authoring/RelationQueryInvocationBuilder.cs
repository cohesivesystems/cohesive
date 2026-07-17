using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Target-neutral descriptor for one invocation of an exact canonical query document.
/// </summary>
/// <remarks>
/// <see cref="DeclaredParameters"/> retains evidence for the complete semantic invocation, including parameters
/// omitted from a demand-scoped plan. <see cref="Parameters"/> is the runtime-ready projection restricted to the
/// attached plan's exact inputs, or the same complete evidence when no plan is attached. Evidence deliberately
/// remains <see cref="RelationQueryParameterEvidenceState.NotProvided"/> even when the parameter declares a
/// default; canonical evaluators apply that persisted default when resolving the
/// effective value. This keeps omission, explicit null, semantic missing, and concrete values distinguishable.
/// </remarks>
public sealed class RelationQueryInvocation
{
    internal RelationQueryInvocation(
        RelationQueryDocument document,
        QueryDefinition query,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<RelationQueryParameterEvidence> parameters,
        ImmutableArray<RelationQueryParameterEvidence> declaredParameters,
        RelationQueryCompilationDemand demand,
        RelationQueryCompilationDemandOrigin demandOrigin,
        RelationQueryCompiledPlanReference? planReference)
    {
        Document = document;
        Query = query;
        Evaluation = evaluation;
        Parameters = parameters;
        DeclaredParameters = declaredParameters;
        Demand = demand;
        DemandOrigin = demandOrigin;
        PlanReference = planReference;
    }

    /// <summary>Exact persisted canonical query document being invoked.</summary>
    public RelationQueryDocument Document { get; }

    /// <summary>Canonical query definition retained by <see cref="Document"/>.</summary>
    public QueryDefinition Query { get; }

    /// <summary>Caller-assigned identity of this evaluation.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>
    /// Runtime-ready parameter evidence in deterministic canonical input-identity order. When
    /// <see cref="PlanReference"/> is present, this array contains only inputs belonging to that exact
    /// demand-scoped plan; otherwise it is identical to <see cref="DeclaredParameters"/>.
    /// </summary>
    public ImmutableArray<RelationQueryParameterEvidence> Parameters { get; }

    /// <summary>
    /// Evidence for every parameter declared by <see cref="Query"/>, including authored values that are not
    /// inputs to an attached demand-scoped plan and explicit
    /// <see cref="RelationQueryParameterEvidenceState.NotProvided"/> evidence for unassigned declarations.
    /// </summary>
    public ImmutableArray<RelationQueryParameterEvidence> DeclaredParameters { get; }

    /// <summary>Effective output demand selected for compilation and evaluation.</summary>
    public RelationQueryCompilationDemand Demand { get; }

    /// <summary>Whether <see cref="Demand"/> was selected explicitly or supplied by convention.</summary>
    public RelationQueryCompilationDemandOrigin DemandOrigin { get; }

    /// <summary>
    /// Optional exact compiled-plan attribution verified against <see cref="Document"/> and
    /// <see cref="Demand"/>, or <see langword="null"/> when the invocation is not yet plan-bound.
    /// </summary>
    public RelationQueryCompiledPlanReference? PlanReference { get; }
}

/// <summary>
/// Authors parameter evidence and output demand for a target-neutral invocation of a canonical query.
/// </summary>
/// <remarks>
/// The builder is mutable and is not thread-safe. It never selects a source placement, adapter, lowering
/// strategy, or execution engine. Each parameter and result may be configured at most once so accidental
/// authoring ambiguity is rejected rather than resolved by call order.
/// </remarks>
public sealed class RelationQueryInvocationBuilder
{
    readonly RelationQueryDocument document;
    readonly QueryDefinition query;
    readonly RelationQueryEvaluationId evaluation;
    readonly RelationQueryCompiledPlanReference? planReference;
    readonly IReadOnlyDictionary<QueryParameterId, QueryParameterDefinition> parameters;
    readonly IReadOnlySet<QueryResultId> results;
    readonly Dictionary<QueryParameterId, ParameterAssignment> assignments = [];
    readonly Dictionary<QueryResultId, QueryResultDemand> resultDemands = [];

    internal RelationQueryDefinitionFingerprint DefinitionFingerprint => document.DefinitionFingerprint;

    /// <summary>Creates an invocation builder for an exact persisted query document.</summary>
    /// <param name="document">Exact canonical query document to invoke.</param>
    /// <param name="evaluation">Caller-assigned identity for the evaluation.</param>
    /// <param name="planReference">
    /// Optional compiled-plan attribution. Its schema version and definition fingerprint are verified
    /// immediately; its demand fingerprint is verified by <see cref="Build"/> after demand authoring completes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="document"/> does not contain a valid canonical
    /// <see cref="QueryDefinition"/>, or
    /// <paramref name="planReference"/> identifies another schema version or definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryInvocationBuilder(
        RelationQueryDocument document,
        RelationQueryEvaluationId evaluation,
        RelationQueryCompiledPlanReference? planReference = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(evaluation.Value))
            throw new ArgumentException("A query invocation requires a non-empty evaluation identity.", nameof(evaluation));
        if (document.Definition is not QueryDefinition queryDefinition)
        {
            throw new ArgumentException(
                "A query invocation requires a document containing a canonical query definition.",
                nameof(document));
        }

        var validation = RelationQueryDocumentSemanticValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(
                    Environment.NewLine,
                    validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        if (planReference is not null)
            ValidatePlanDefinition(document, planReference);

        this.document = document;
        query = queryDefinition;
        this.evaluation = evaluation;
        this.planReference = planReference;
        parameters = query.Body.Parameters.ToDictionary(static parameter => parameter.Id);
        results = query.Results.Select(static result => result.Id).ToHashSet();
    }

    /// <summary>
    /// Creates an invocation builder by canonicalizing a query definition into a current-version document.
    /// </summary>
    /// <param name="query">Canonical query definition to persist and invoke.</param>
    /// <param name="evaluation">Caller-assigned identity for the evaluation.</param>
    /// <param name="planReference">
    /// Optional compiled-plan attribution verified against the generated document and effective demand.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="query"/> fails semantic validation, or
    /// <paramref name="planReference"/> identifies another schema version or definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The query contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryInvocationBuilder(
        QueryDefinition query,
        RelationQueryEvaluationId evaluation,
        RelationQueryCompiledPlanReference? planReference = null)
        : this(RelationQueryDocument.FromDefinition(query), evaluation, planReference)
    {
    }

    /// <summary>Supplies concrete, null, or missing evidence for a declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="value">
    /// Authored value. <see cref="ObservationValue.Null"/> becomes explicit null evidence and
    /// <see cref="ObservationValue.Undefined"/> becomes semantic missing evidence.
    /// </param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared, or <paramref name="value"/> is incompatible with its
    /// effective canonical parameter contract.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryInvocationBuilder Set(QueryParameterId parameter, ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Null => SetNull(parameter),
        ObservationValueKind.Undefined => SetMissing(parameter),
        _ => AddAssignment(parameter, RelationQueryParameterEvidenceState.Provided, value)
    };

    /// <summary>Supplies explicit null evidence for a nullable declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared or its effective canonical contract is non-nullable.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryInvocationBuilder SetNull(QueryParameterId parameter) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.Null);

    /// <summary>Supplies explicit semantic missing evidence for a declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentException"><paramref name="parameter"/> is undeclared.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryInvocationBuilder SetMissing(QueryParameterId parameter) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.Missing);

    /// <summary>
    /// Explicitly omits a declared query parameter without applying its persisted default to evidence.
    /// </summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentException"><paramref name="parameter"/> is undeclared.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryInvocationBuilder Omit(QueryParameterId parameter) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.NotProvided);

    /// <summary>Selects every field emitted by one declared query result.</summary>
    /// <param name="result">Declared query-result identity.</param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentException"><paramref name="result"/> is undeclared.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> was already selected.</exception>
    public RelationQueryInvocationBuilder Select(QueryResultId result) =>
        AddResultDemand(QueryResultDemand.AllFields(RequireResult(result)));

    /// <summary>Selects an explicit field subset emitted by one declared query result.</summary>
    /// <param name="result">Declared query-result identity.</param>
    /// <param name="fields">Non-empty graph-qualified result fields to demand.</param>
    /// <returns>This builder for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> is undeclared, or <paramref name="fields"/> is empty or invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> was already selected.</exception>
    public RelationQueryInvocationBuilder Select(
        QueryResultId result,
        IEnumerable<RelationQueryFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return AddResultDemand(QueryResultDemand.SelectedFields(RequireResult(result), fields));
    }

    /// <summary>Builds an immutable target-neutral invocation descriptor.</summary>
    /// <returns>
    /// An invocation retaining the exact document, evaluation identity, complete declared-parameter states,
    /// plan-compatible parameter evidence, effective demand, and optional verified compiled-plan attribution.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The explicitly selected demand does not match the supplied compiled-plan reference.
    /// </exception>
    public RelationQueryInvocation Build()
    {
        var demandOrigin = resultDemands.Count == 0
            ? RelationQueryCompilationDemandOrigin.Convention
            : RelationQueryCompilationDemandOrigin.Explicit;
        var demand = resultDemands.Count == 0
            ? RelationQueryCompilationDemand.AllDeclaredOutputs
            : RelationQueryCompilationDemand.ForQueryResults(resultDemands.Values);

        if (planReference is not null
            && !Equals(
                planReference.DemandFingerprint,
                RelationQueryCompiledPlanFingerprinter.ComputeDemand(demand)))
        {
            throw new InvalidOperationException(
                "The authored invocation demand does not match the supplied compiled-plan reference.");
        }

        ImmutableArray<RelationQueryParameterEvidence> declaredEvidence =
        [
            .. parameters.Values
                .Select(CreateEvidence)
                .OrderBy(static item => item.Input.Value, StringComparer.Ordinal)
        ];
        var evidence = planReference is null
            ? declaredEvidence
            : declaredEvidence
                .Where(parameter => planReference.Inputs.Contains(parameter.Input))
                .ToImmutableArray();

        return new(
            document,
            query,
            evaluation,
            evidence,
            declaredEvidence,
            demand,
            demandOrigin,
            planReference);
    }

    RelationQueryInvocationBuilder AddAssignment(
        QueryParameterId parameter,
        RelationQueryParameterEvidenceState state,
        ObservationValue? value = null)
    {
        var definition = RequireParameter(parameter);
        if (assignments.ContainsKey(parameter))
        {
            throw new InvalidOperationException(
                $"Query parameter '{parameter.Value}' was configured more than once.");
        }

        var semanticValue = state switch
        {
            RelationQueryParameterEvidenceState.Provided => value,
            RelationQueryParameterEvidenceState.Null => ObservationValue.Null,
            _ => null
        };
        if (semanticValue is { } candidate
            && !definition.EffectiveValueContract.IsSatisfiedByConstant(candidate))
        {
            throw new ArgumentException(
                $"Value for query parameter '{parameter.Value}' does not satisfy its effective canonical value contract.",
                state == RelationQueryParameterEvidenceState.Provided
                    ? nameof(value)
                    : nameof(parameter));
        }

        assignments.Add(parameter, new(state, value));
        return this;
    }

    QueryParameterDefinition RequireParameter(QueryParameterId parameter)
    {
        if (!parameters.TryGetValue(parameter, out var definition))
        {
            throw new ArgumentException(
                $"Query '{query.Id.Value}' does not declare parameter '{parameter.Value}'.",
                nameof(parameter));
        }

        return definition;
    }

    QueryResultId RequireResult(QueryResultId result)
    {
        if (!results.Contains(result))
        {
            throw new ArgumentException(
                $"Query '{query.Id.Value}' does not declare result '{result.Value}'.",
                nameof(result));
        }

        return result;
    }

    RelationQueryInvocationBuilder AddResultDemand(QueryResultDemand demand)
    {
        if (!resultDemands.TryAdd(demand.Result, demand))
        {
            throw new InvalidOperationException(
                $"Query result '{demand.Result.Value}' was selected more than once.");
        }

        return this;
    }

    RelationQueryParameterEvidence CreateEvidence(QueryParameterDefinition definition)
    {
        var assignment = assignments.GetValueOrDefault(
            definition.Id,
            new(RelationQueryParameterEvidenceState.NotProvided));
        return new(
            RelationQueryInputIds.ForParameter(definition.Id),
            assignment.State,
            assignment.State == RelationQueryParameterEvidenceState.Provided
                ? assignment.Value
                : null);
    }

    static void ValidatePlanDefinition(
        RelationQueryDocument document,
        RelationQueryCompiledPlanReference planReference)
    {
        if (!string.Equals(
                document.SchemaVersion,
                planReference.DefinitionSchemaVersion,
                StringComparison.Ordinal)
            || !Equals(document.DefinitionFingerprint, planReference.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "The supplied compiled-plan reference does not identify the invocation document.",
                nameof(planReference));
        }
    }

    readonly record struct ParameterAssignment(
        RelationQueryParameterEvidenceState State,
        ObservationValue? Value = null);
}

/// <summary>Convenience entry points for target-neutral canonical query invocation authoring.</summary>
public static class RelationQueryInvocationAuthoringExtensions
{
    /// <summary>Begins an invocation of an exact persisted canonical query document.</summary>
    /// <param name="document">Exact query document to invoke.</param>
    /// <param name="evaluation">Caller-assigned evaluation identity.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <returns>A target-neutral invocation builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="document"/> is invalid or does not contain a query, or
    /// <paramref name="planReference"/> identifies another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationQueryInvocationBuilder Invoke(
        this RelationQueryDocument document,
        RelationQueryEvaluationId evaluation,
        RelationQueryCompiledPlanReference? planReference = null) =>
        new(document, evaluation, planReference);

    /// <summary>Canonicalizes a query definition and begins a target-neutral invocation.</summary>
    /// <param name="query">Canonical query definition to invoke.</param>
    /// <param name="evaluation">Caller-assigned evaluation identity.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <returns>A target-neutral invocation builder retaining the generated canonical document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="query"/> is invalid, or
    /// <paramref name="planReference"/> identifies another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The query contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationQueryInvocationBuilder Invoke(
        this QueryDefinition query,
        RelationQueryEvaluationId evaluation,
        RelationQueryCompiledPlanReference? planReference = null) =>
        new(query, evaluation, planReference);
}
