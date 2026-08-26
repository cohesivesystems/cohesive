using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Portable directly supplied root evidence for one relation evaluation.
/// </summary>
/// <remarks>
/// Stable source identity is acquisition evidence rather than part of the identity-free semantic observation.
/// The enclosing evaluation validates <see cref="Fields"/> against <see cref="Shape"/> using its exact persisted
/// shape-graph snapshot before the evidence can be executed.
/// </remarks>
public sealed record RelationQuerySuppliedRoot
{
    /// <summary>Creates portable directly supplied root evidence.</summary>
    /// <param name="identity">Stable source identity for the supplied root.</param>
    /// <param name="shape">Exact graph-qualified semantic shape.</param>
    /// <param name="fields">Complete field values keyed by canonical semantic identity.</param>
    /// <exception cref="ArgumentException">The identity or qualified shape is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationQuerySuppliedRoot(
        string identity,
        QualifiedShapeId shape,
        IReadOnlyDictionary<string, ObservationValue> fields)
    {
        Identity = Guard.RequireNotNullOrWhiteSpace(identity);
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("Supplied root evidence requires a graph-qualified shape.", nameof(shape));
        ArgumentNullException.ThrowIfNull(fields);

        Shape = shape;
        Fields = fields.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    /// <summary>Stable source identity for this supplied root.</summary>
    [JsonPropertyName("id")]
    public string Identity { get; }

    /// <summary>Exact graph-qualified semantic shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Complete field values keyed by canonical semantic identity.</summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields { get; }

    /// <summary>Creates supplied-root evidence from a validated identity-free observation.</summary>
    /// <param name="identity">Stable source identity for the supplied root.</param>
    /// <param name="observation">Validated identity-free semantic observation.</param>
    /// <returns>Portable root evidence retaining the observation's exact qualified shape and values.</returns>
    public static RelationQuerySuppliedRoot FromObservation(string identity, CoreObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new(identity, observation.ShapeId, observation.Fields);
    }

    internal CoreObservation ToObservation(GraphShapeId shape)
    {
        if (Shape != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Supplied root '{Identity}' has shape '{Shape}', expected '{shape.QualifiedId}'.");
        }

        return CoreObservation.Create(shape, Fields);
    }
}

/// <summary>
/// Directly supplied root evidence for one relation evaluation.
/// </summary>
public sealed record RelationQuerySuppliedRootSet
{
    /// <summary>Creates normalized directly supplied root evidence.</summary>
    /// <param name="observations">Portable identity-bearing root evidence.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition containing every supplied root.</param>
    /// <param name="completeness">Whether omission from the supplied set is authoritative.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logicalPartition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="observations"/> contains a null entry or duplicate identity, or
    /// <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySuppliedRootSet(
        ImmutableArray<RelationQuerySuppliedRoot> observations,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        RelationQueryEvidenceCompleteness completeness,
        string? evidenceReference)
    {
        var normalized = observations.IsDefault ? [] : observations;
        if (normalized.Any(static observation => observation is null))
            throw new ArgumentException("Supplied roots cannot contain null observations.", nameof(observations));
        if (normalized.GroupBy(static observation => observation.Identity, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Supplied roots cannot repeat an observation identity.", nameof(observations));
        }
        if (!Enum.IsDefined(completeness))
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);

        Observations = [.. normalized.OrderBy(static observation => observation.Identity, StringComparer.Ordinal)];
        LogicalPartition = Guard.RequireNotNull(logicalPartition);
        Completeness = completeness;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Portable root evidence in deterministic identity order.</summary>
    public ImmutableArray<RelationQuerySuppliedRoot> Observations { get; }

    /// <summary>Provider-neutral logical partition containing every supplied root.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Whether omission from the supplied root set is authoritative.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Opaque provenance reference for the supplied roots, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>
/// Target-neutral descriptor for evaluating one exact canonical relation or query document.
/// </summary>
/// <remarks>
/// <see cref="Compilation"/> is the exact semantic snapshot supplied to static compilation.
/// <see cref="Parameters"/> retains evidence for every declaration. Omission remains distinguishable from explicit
/// null, semantic missing, and concrete values so evaluators can project the evidence required by the effective
/// demand-scoped plan and apply persisted defaults without destroying caller evidence.
/// </remarks>
public sealed class RelationQueryEvaluation
{
    /// <summary>Current portable canonical evaluation schema version.</summary>
    public const string CurrentSchemaVersion = "relation-query-evaluation/v3";

    internal RelationQueryEvaluation(
        RelationQueryCompilationRequest compilation,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<RelationQueryParameterEvidence> parameters,
        RelationQueryCompiledPlanReference? planReference,
        RelationQuerySuppliedRootSet? suppliedRoots)
        : this(
            CurrentSchemaVersion,
            compilation,
            evaluation,
            parameters,
            planReference,
            suppliedRoots)
    {
    }

    /// <summary>Creates and verifies a portable canonical relation/query evaluation.</summary>
    /// <param name="schemaVersion">Portable evaluation schema version.</param>
    /// <param name="compilation">Exact persisted semantic compilation request.</param>
    /// <param name="evaluation">Caller-assigned evaluation identity.</param>
    /// <param name="parameters">Evidence for every declared invocation parameter.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <param name="suppliedRoots">Optional directly supplied relation-root evidence.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/> or <paramref name="compilation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema version or evaluation identity is invalid; the definition document is invalid; parameter evidence
    /// is incomplete, duplicated, foreign, or incompatible; supplied roots conflict with the definition; the plan
    /// reference conflicts with the definition or demand; or <paramref name="fingerprint"/> is stale.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Canonical evaluation content cannot be represented by the canonical JSON profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical evaluation content cannot be written using the strict portable JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Canonical evaluation content contains an unsupported serialization type.
    /// </exception>
    [JsonConstructor]
    public RelationQueryEvaluation(
        string schemaVersion,
        RelationQueryCompilationRequest compilation,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<RelationQueryParameterEvidence> parameters,
        RelationQueryCompiledPlanReference? planReference = null,
        RelationQuerySuppliedRootSet? suppliedRoots = null,
        RelationQueryEvaluationFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported relation/query evaluation schema version '{SchemaVersion}'.", nameof(schemaVersion));
        Compilation = Guard.RequireNotNull(compilation);
        var documentValidation = RelationQueryDocumentSemanticValidator.Validate(Compilation.DefinitionDocument);
        if (!documentValidation.IsValid)
        {
            throw new ArgumentException(
                string.Join(
                    Environment.NewLine,
                    documentValidation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(compilation));
        }
        if (string.IsNullOrWhiteSpace(evaluation.Value))
            throw new ArgumentException("A relation/query evaluation requires a non-empty evaluation identity.", nameof(evaluation));
        Evaluation = evaluation;

        var normalizedParameters = parameters.IsDefault ? [] : parameters;
        if (normalizedParameters.Any(static parameter => parameter is null))
            throw new ArgumentException("Parameter evidence cannot contain null entries.", nameof(parameters));
        if (normalizedParameters.GroupBy(static parameter => parameter.Input).Any(static group => group.Count() > 1))
            throw new ArgumentException("Parameter evidence cannot repeat a compiled input identity.", nameof(parameters));
        var declarations = Definition.Body.Parameters.ToDictionary(
            static parameter => RelationQueryInputIds.ForParameter(parameter.Id));
        if (normalizedParameters.Length != declarations.Count
            || normalizedParameters.Any(parameter => !declarations.ContainsKey(parameter.Input)))
        {
            throw new ArgumentException(
                "Parameter evidence must identify every and only parameter declared by the evaluated definition.",
                nameof(parameters));
        }
        foreach (var parameter in normalizedParameters)
        {
            var declaration = declarations[parameter.Input];
            var semanticValue = parameter.State switch
            {
                RelationQueryParameterEvidenceState.Provided => parameter.Value,
                RelationQueryParameterEvidenceState.Null => ObservationValue.Null,
                _ => null
            };
            if (semanticValue is { } value && !declaration.EffectiveValueContract.IsSatisfiedByConstant(value))
            {
                throw new ArgumentException(
                    $"Evidence for query parameter '{declaration.Id.Value}' does not satisfy its canonical value contract.",
                    nameof(parameters));
            }
        }
        Parameters = [.. normalizedParameters.OrderBy(static parameter => parameter.Input.Value, StringComparer.Ordinal)];

        PlanReference = planReference;
        SuppliedRoots = suppliedRoots;
        if (PlanReference is not null)
        {
            ValidatePlanDefinition(Document, PlanReference);
            if (!Equals(
                    PlanReference.DemandFingerprint,
                    RelationQueryCompiledPlanFingerprinter.ComputeDemand(Demand)))
            {
                throw new ArgumentException(
                    "The compiled-plan reference does not identify the evaluation's effective demand.",
                    nameof(planReference));
            }
        }
        ValidateSuppliedRoots();

        var computed = RelationQueryEvaluationFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
            throw new ArgumentException("The evaluation fingerprint does not match normalized content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Portable canonical evaluation schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact definition, shape, relationship, and output-demand snapshots to compile.</summary>
    public RelationQueryCompilationRequest Compilation { get; }

    /// <summary>Exact persisted canonical relation/query document being evaluated.</summary>
    [JsonIgnore]
    public RelationQueryDocument Document => Compilation.DefinitionDocument;

    /// <summary>Canonical relation or query definition retained by <see cref="Document"/>.</summary>
    [JsonIgnore]
    public RelationQueryDefinition Definition => Document.Definition;

    /// <summary>Caller-assigned identity of this evaluation.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>
    /// Evidence for every parameter declared by <see cref="Definition"/> in deterministic canonical input-identity
    /// order, including explicit <see cref="RelationQueryParameterEvidenceState.NotProvided"/> evidence for
    /// unassigned declarations. The evaluator projects this single authoritative set onto the effective compiled
    /// plan.
    /// </summary>
    public ImmutableArray<RelationQueryParameterEvidence> Parameters { get; }

    /// <summary>Effective output demand selected for compilation and evaluation.</summary>
    [JsonIgnore]
    public RelationQueryCompilationDemand Demand => Compilation.Demand;

    /// <summary>Whether <see cref="Demand"/> was selected explicitly or supplied by convention.</summary>
    [JsonIgnore]
    public RelationQueryCompilationDemandOrigin DemandOrigin => Compilation.DemandOrigin;

    /// <summary>
    /// Optional exact compiled-plan attribution verified against <see cref="Document"/> and
    /// <see cref="Demand"/>, or <see langword="null"/> when the evaluation is not yet plan-bound.
    /// </summary>
    public RelationQueryCompiledPlanReference? PlanReference { get; }

    /// <summary>
    /// Directly supplied relation roots, or <see langword="null"/> when no root evidence was supplied. Query
    /// evaluations never carry supplied roots.
    /// </summary>
    public RelationQuerySuppliedRootSet? SuppliedRoots { get; }

    /// <summary>Deterministic fingerprint of the complete normalized evaluation content.</summary>
    public RelationQueryEvaluationFingerprint Fingerprint { get; }

    /// <summary>Determines whether another evaluation has the same complete portable semantic content.</summary>
    /// <param name="other">Evaluation to compare, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when schema version, compilation snapshots, demand, identity, runtime evidence,
    /// attribution, and provenance are canonically equivalent; otherwise <see langword="false"/>.
    /// </returns>
    public bool HasSameSemantics(RelationQueryEvaluation? other) =>
        other is not null && Equals(Fingerprint, other.Fingerprint);

    void ValidateSuppliedRoots()
    {
        if (SuppliedRoots is null)
            return;
        if (Definition is not RelationDefinition relation)
            throw new ArgumentException("Query evaluations cannot carry supplied relation roots.", nameof(SuppliedRoots));
        var sources = relation.Body.Nodes
            .OfType<SourceQueryNode>()
            .Where(source => source.Binding == relation.RootBinding)
            .Take(2)
            .ToArray();
        if (sources.Length != 1)
            throw new ArgumentException("A supplied-root relation must declare exactly one root source.", nameof(SuppliedRoots));
        if (SuppliedRoots.Observations.Any(observation => observation.Shape != sources[0].Shape))
        {
            throw new ArgumentException(
                $"Every supplied root must have shape '{sources[0].Shape}'.",
                nameof(SuppliedRoots));
        }

        var shape = ResolveGraphShape(sources[0].Shape);
        foreach (var observation in SuppliedRoots.Observations)
            _ = observation.ToObservation(shape);
    }

    GraphShapeId ResolveGraphShape(QualifiedShapeId shape)
    {
        var graph = Compilation.ShapeDocuments
            .Select(static document => document.Graph)
            .SingleOrDefault(candidate => candidate.Id == shape.GraphId)
            ?? throw new ArgumentException(
                $"Supplied root shape '{shape}' requires its exact graph snapshot.",
                nameof(SuppliedRoots));
        return new(graph, shape.ShapeId);
    }

    internal static void ValidatePlanDefinition(
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
                "The supplied compiled-plan reference does not identify the evaluation document.",
                nameof(planReference));
        }
    }
}

/// <summary>
/// Authors runtime inputs and output demand for a target-neutral evaluation of a canonical relation or query.
/// </summary>
/// <remarks>
/// The builder is mutable and is not thread-safe. It never selects a source placement, adapter, lowering
/// strategy, or execution engine. Each parameter, result, relation demand, and root set may be configured at most
/// once so accidental
/// authoring ambiguity is rejected rather than resolved by call order.
/// </remarks>
public sealed class RelationQueryEvaluationBuilder
{
    readonly RelationQueryDocument document;
    readonly RelationQueryDefinition definition;
    readonly ImmutableArray<ShapeGraphDocument> shapeDocuments;
    readonly RelationshipCatalogDocument? relationshipCatalogDocument;
    readonly RelationQueryEvaluationId evaluation;
    readonly RelationQueryCompiledPlanReference? planReference;
    readonly IReadOnlyDictionary<QueryParameterId, QueryParameterDefinition> parameters;
    readonly IReadOnlySet<QueryResultId> results;
    readonly Dictionary<QueryParameterId, ParameterAssignment> assignments = [];
    readonly Dictionary<QueryResultId, QueryResultDemand> resultDemands = [];
    RelationQueryCompilationDemand? relationDemand;
    RelationQuerySuppliedRootSet? suppliedRoots;

    internal RelationQueryDefinitionFingerprint DefinitionFingerprint => document.DefinitionFingerprint;

    /// <summary>Creates an evaluation builder for an exact persisted relation/query document and semantic context.</summary>
    /// <param name="document">Exact canonical relation/query document to evaluate.</param>
    /// <param name="evaluation">Caller-assigned identity for the evaluation.</param>
    /// <param name="shapeDocuments">Exact persisted shape snapshots available to compilation.</param>
    /// <param name="relationshipCatalogDocument">Exact persisted relationship catalog, or <see langword="null"/>.</param>
    /// <param name="planReference">
    /// Optional compiled-plan attribution. Its schema version and definition fingerprint are verified
    /// immediately; its demand fingerprint is verified by <see cref="Build"/> after demand authoring completes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="document"/> is not valid, a shape collection
    /// contains <see langword="null"/>, or
    /// <paramref name="planReference"/> identifies another schema version or definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryEvaluationBuilder(
        RelationQueryDocument document,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null,
        RelationQueryCompiledPlanReference? planReference = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(evaluation.Value))
            throw new ArgumentException("A relation/query evaluation requires a non-empty evaluation identity.", nameof(evaluation));

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
            RelationQueryEvaluation.ValidatePlanDefinition(document, planReference);

        this.document = document;
        definition = document.Definition;
        var normalizedShapes = shapeDocuments.IsDefault ? [] : shapeDocuments;
        if (normalizedShapes.Any(static shape => shape is null))
            throw new ArgumentException("Shape documents cannot contain null entries.", nameof(shapeDocuments));
        this.shapeDocuments = normalizedShapes;
        this.relationshipCatalogDocument = relationshipCatalogDocument;
        this.evaluation = evaluation;
        this.planReference = planReference;
        parameters = definition.Body.Parameters.ToDictionary(static parameter => parameter.Id);
        results = definition is QueryDefinition query
            ? query.Results.Select(static result => result.Id).ToHashSet()
            : new HashSet<QueryResultId>();
    }

    /// <summary>
    /// Creates an evaluation builder by canonicalizing a relation or query definition into a current-version document.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to persist and evaluate.</param>
    /// <param name="evaluation">Caller-assigned identity for the evaluation.</param>
    /// <param name="shapeDocuments">Exact persisted shape snapshots available to compilation.</param>
    /// <param name="relationshipCatalogDocument">Exact persisted relationship catalog, or <see langword="null"/>.</param>
    /// <param name="planReference">
    /// Optional compiled-plan attribution verified against the generated document and effective demand.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="definition"/> fails semantic validation, a shape
    /// collection contains <see langword="null"/>, or
    /// <paramref name="planReference"/> identifies another schema version or definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryEvaluationBuilder(
        RelationQueryDefinition definition,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null,
        RelationQueryCompiledPlanReference? planReference = null)
        : this(
            RelationQueryDocument.FromDefinition(definition),
            evaluation,
            shapeDocuments,
            relationshipCatalogDocument,
            planReference)
    {
    }

    /// <summary>Supplies concrete, null, or missing evidence for a declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="value">
    /// Authored value. <see cref="ObservationValue.Null"/> becomes explicit null evidence and
    /// <see cref="ObservationValue.Undefined"/> becomes semantic missing evidence.
    /// </param>
    /// <param name="evidenceReference">Optional opaque provenance or decoding reference.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared, or <paramref name="value"/> is incompatible with its
    /// effective canonical parameter contract, or <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryEvaluationBuilder Set(
        QueryParameterId parameter,
        ObservationValue value,
        string? evidenceReference = null) => value.Kind switch
    {
        ObservationValueKind.Null => SetNull(parameter, evidenceReference),
        ObservationValueKind.Undefined => SetMissing(parameter, evidenceReference),
        _ => AddAssignment(parameter, RelationQueryParameterEvidenceState.Provided, value, evidenceReference)
    };

    /// <summary>Supplies explicit null evidence for a nullable declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="evidenceReference">Optional opaque provenance or decoding reference.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared, its effective canonical contract is non-nullable, or
    /// <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryEvaluationBuilder SetNull(
        QueryParameterId parameter,
        string? evidenceReference = null) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.Null, evidenceReference: evidenceReference);

    /// <summary>Supplies explicit semantic missing evidence for a declared query parameter.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="evidenceReference">Optional opaque provenance or decoding reference.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared or <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryEvaluationBuilder SetMissing(
        QueryParameterId parameter,
        string? evidenceReference = null) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.Missing, evidenceReference: evidenceReference);

    /// <summary>Records that acquiring or decoding a declared query parameter failed.</summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="evidenceReference">Opaque failure reference suitable for diagnostics and correlation.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evidenceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared, or <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryEvaluationBuilder SetFailed(
        QueryParameterId parameter,
        string evidenceReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        return AddAssignment(
            parameter,
            RelationQueryParameterEvidenceState.Failed,
            evidenceReference: evidenceReference);
    }

    /// <summary>
    /// Explicitly omits a declared query parameter without applying its persisted default to evidence.
    /// </summary>
    /// <param name="parameter">Declared parameter identity.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference for the explicit omission.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameter"/> is undeclared or <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="parameter"/> was already configured.</exception>
    public RelationQueryEvaluationBuilder Omit(
        QueryParameterId parameter,
        string? evidenceReference = null) =>
        AddAssignment(parameter, RelationQueryParameterEvidenceState.NotProvided, evidenceReference: evidenceReference);

    /// <summary>Selects every field emitted by one declared query result.</summary>
    /// <param name="result">Declared query-result identity.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentException"><paramref name="result"/> is undeclared.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> was already selected.</exception>
    public RelationQueryEvaluationBuilder Select(QueryResultId result) =>
        AddResultDemand(QueryResultDemand.AllFields(RequireResult(result)));

    /// <summary>Selects an explicit field subset emitted by one declared query result.</summary>
    /// <param name="result">Declared query-result identity.</param>
    /// <param name="fields">Non-empty graph-qualified result fields to demand.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> is undeclared, or <paramref name="fields"/> is empty or invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> was already selected.</exception>
    public RelationQueryEvaluationBuilder Select(
        QueryResultId result,
        IEnumerable<RelationQueryFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return AddResultDemand(QueryResultDemand.SelectedFields(RequireResult(result), fields));
    }

    /// <summary>Selects an explicit field subset emitted by a relation output.</summary>
    /// <param name="fields">Non-empty graph-qualified relation-output fields to demand.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fields"/> is empty, invalid, or references a shape other than the relation output.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The evaluated definition is a query or relation fields were already selected.
    /// </exception>
    public RelationQueryEvaluationBuilder Select(IEnumerable<RelationQueryFieldReference> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var relation = RequireRelation();
        if (relationDemand is not null)
            throw new InvalidOperationException("Relation output fields were selected more than once.");
        var demand = RelationQueryCompilationDemand.ForRelationFields(fields);
        if (demand.RelationFields.Any(field => field.Shape != relation.Output.Shape))
        {
            throw new ArgumentException(
                $"Selected relation fields must belong to output shape '{relation.Output.Shape}'.",
                nameof(fields));
        }

        relationDemand = demand;
        return this;
    }

    /// <summary>Supplies already observed roots for a canonical relation evaluation.</summary>
    /// <param name="observations">
    /// Identity-bearing observations of the relation root shape. An empty collection explicitly supplies an empty
    /// root set and is distinct from omitting root evidence.
    /// </param>
    /// <param name="completeness">Whether omission from the supplied set is authoritative.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <param name="logicalPartition">
    /// Provider-neutral logical partition containing every supplied root, or <see langword="null"/> for the
    /// explicitly unpartitioned whole-source identity.
    /// </param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An observation is <see langword="null"/>, has the wrong root shape, repeats an identity, or
    /// <paramref name="evidenceReference"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// The evaluated definition is a query or roots were already supplied.
    /// </exception>
    public RelationQueryEvaluationBuilder Supply(
        IEnumerable<RelationQuerySuppliedRoot> observations,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        string? evidenceReference = null,
        RelationQueryLogicalPartitionIdentity? logicalPartition = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (!Enum.IsDefined(completeness))
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.");
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        if (suppliedRoots is not null)
            throw new InvalidOperationException("Relation roots were supplied more than once.");

        var rootShape = RequireRelationRootShape();
        var normalized = observations.ToImmutableArray();
        if (normalized.Any(static observation => observation is null))
            throw new ArgumentException("Supplied roots cannot contain null observations.", nameof(observations));
        if (normalized.Any(observation => observation.Shape != rootShape))
        {
            throw new ArgumentException(
                $"Every supplied root must have shape '{rootShape}'.",
                nameof(observations));
        }
        if (normalized.GroupBy(static observation => observation.Identity, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Supplied roots cannot repeat an observation identity.", nameof(observations));
        }

        suppliedRoots = new(
            [.. normalized.OrderBy(static observation => observation.Identity, StringComparer.Ordinal)],
            logicalPartition ?? RelationQueryLogicalPartitionIdentity.WholeSource,
            completeness,
            evidenceReference);
        return this;
    }

    /// <summary>Supplies validated identity-free observations as roots.</summary>
    /// <param name="observations">Validated semantic root observations.</param>
    /// <param name="selectIdentity">Selects each root's stable source identity.</param>
    /// <param name="completeness">Whether omission from the supplied set is authoritative.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition containing every supplied root.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    public RelationQueryEvaluationBuilder Supply(
        IEnumerable<CoreObservation> observations,
        Func<CoreObservation, string> selectIdentity,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        string? evidenceReference = null,
        RelationQueryLogicalPartitionIdentity? logicalPartition = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(selectIdentity);
        return Supply(
            observations.Select(observation =>
            {
                ArgumentNullException.ThrowIfNull(observation);
                return RelationQuerySuppliedRoot.FromObservation(selectIdentity(observation), observation);
            }),
            completeness,
            evidenceReference,
            logicalPartition);
    }

    /// <summary>Supplies versioned entity observations as relation roots.</summary>
    /// <param name="snapshots">Entity snapshots whose identity and semantic observation become supplied evidence.</param>
    /// <param name="completeness">Whether omission from the supplied set is authoritative.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition containing every supplied root.</param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <remarks>Entity versions remain entity-state semantics and are intentionally not copied into relation roots.</remarks>
    public RelationQueryEvaluationBuilder Supply(
        IEnumerable<EntityObservationSnapshot> snapshots,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        string? evidenceReference = null,
        RelationQueryLogicalPartitionIdentity? logicalPartition = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return Supply(
            snapshots.Select(snapshot =>
            {
                ArgumentNullException.ThrowIfNull(snapshot);
                return RelationQuerySuppliedRoot.FromObservation(snapshot.EntityId.Value, snapshot.Observation);
            }),
            completeness,
            evidenceReference,
            logicalPartition);
    }

    /// <summary>Projects CLR values through the core observation model and supplies them as roots.</summary>
    /// <typeparam name="T">CLR root type.</typeparam>
    /// <param name="values">CLR roots to map.</param>
    /// <param name="selectIdentity">Selects each root's stable semantic identity.</param>
    /// <param name="completeness">Whether omission from the supplied set is authoritative.</param>
    /// <param name="evidenceReference">Optional opaque provenance reference.</param>
    /// <param name="logicalPartition">
    /// Provider-neutral logical partition containing every supplied root, or <see langword="null"/> for the
    /// explicitly unpartitioned whole-source identity.
    /// </param>
    /// <returns>This builder for continued evaluation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="values"/> or <paramref name="selectIdentity"/> is <see langword="null"/>, a value is null, or
    /// <paramref name="selectIdentity"/> returns <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is empty or duplicated, <paramref name="evidenceReference"/> is empty, or mapped observations do
    /// not match the relation root shape.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// The evaluated definition is a query, roots were already supplied, or the CLR shape cannot be mapped.
    /// </exception>
    /// <exception cref="NotSupportedException">A CLR value cannot be represented as observation data.</exception>
    public RelationQueryEvaluationBuilder Supply<T>(
        IEnumerable<T> values,
        Func<T, string> selectIdentity,
        RelationQueryEvidenceCompleteness completeness = RelationQueryEvidenceCompleteness.Complete,
        string? evidenceReference = null,
        RelationQueryLogicalPartitionIdentity? logicalPartition = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(selectIdentity);
        var rootShape = RequireRelationRootShape();
        var shape = ResolveGraphShape(rootShape);
        List<RelationQuerySuppliedRoot> observations = [];
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var identity = selectIdentity(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            var observation = CoreObservation.Create(
                shape,
                ObservationValue.FromJsonNode(JsonSerializer.SerializeToNode(value, value.GetType())));
            observations.Add(RelationQuerySuppliedRoot.FromObservation(identity, observation));
        }

        return Supply(observations, completeness, evidenceReference, logicalPartition);
    }

    /// <summary>Builds an immutable target-neutral evaluation descriptor.</summary>
    /// <returns>
    /// An evaluation retaining the exact compilation context, identity, complete declared-parameter states,
    /// effective demand, provenance, and optional verified compiled-plan attribution.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The explicitly selected demand does not match the supplied compiled-plan reference.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// The normalized evaluation cannot be written using the strict portable JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The normalized evaluation contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryEvaluation Build()
    {
        var explicitDemand = definition switch
        {
            QueryDefinition when resultDemands.Count != 0 =>
                RelationQueryCompilationDemand.ForQueryResults(resultDemands.Values),
            RelationDefinition when relationDemand is not null => relationDemand,
            _ => null
        };
        var demand = explicitDemand ?? RelationQueryCompilationDemand.AllDeclaredOutputs;

        if (planReference is not null
            && !Equals(
                planReference.DemandFingerprint,
                RelationQueryCompiledPlanFingerprinter.ComputeDemand(demand)))
        {
            throw new InvalidOperationException(
                "The authored evaluation demand does not match the supplied compiled-plan reference.");
        }

        ImmutableArray<RelationQueryParameterEvidence> evidence =
        [
            .. parameters.Values
                .Select(CreateEvidence)
                .OrderBy(static item => item.Input.Value, StringComparer.Ordinal)
        ];
        RelationQueryCompilationRequest compilation = new(
            document,
            shapeDocuments,
            relationshipCatalogDocument,
            explicitDemand);
        return new(
            compilation,
            evaluation,
            evidence,
            planReference,
            suppliedRoots);
    }

    RelationQueryEvaluationBuilder AddAssignment(
        QueryParameterId parameter,
        RelationQueryParameterEvidenceState state,
        ObservationValue? value = null,
        string? evidenceReference = null)
    {
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
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

        assignments.Add(parameter, new(state, value, evidenceReference));
        return this;
    }

    QueryParameterDefinition RequireParameter(QueryParameterId parameter)
    {
        if (!parameters.TryGetValue(parameter, out var definition))
        {
            throw new ArgumentException(
                $"The evaluated definition does not declare parameter '{parameter.Value}'.",
                nameof(parameter));
        }

        return definition;
    }

    QueryResultId RequireResult(QueryResultId result)
    {
        var query = RequireQuery();
        if (!results.Contains(result))
        {
            throw new ArgumentException(
                $"Query '{query.Id.Value}' does not declare result '{result.Value}'.",
                nameof(result));
        }

        return result;
    }

    QueryDefinition RequireQuery() => definition as QueryDefinition
        ?? throw new InvalidOperationException("Named query results can only be selected for a query definition.");

    RelationDefinition RequireRelation() => definition as RelationDefinition
        ?? throw new InvalidOperationException("Relation output fields and roots require a relation definition.");

    QualifiedShapeId RequireRelationRootShape()
    {
        var relation = RequireRelation();
        var roots = relation.Body.Nodes
            .OfType<SourceQueryNode>()
            .Where(source => source.Binding == relation.RootBinding)
            .Take(2)
            .ToArray();
        return roots.Length == 1
            ? roots[0].Shape
            : throw new InvalidOperationException(
                $"Relation '{relation.Id.Value}' does not have exactly one source for root binding '{relation.RootBinding.Value}'.");
    }

    GraphShapeId ResolveGraphShape(QualifiedShapeId shape)
    {
        var graph = shapeDocuments
            .Select(static document => document.Graph)
            .SingleOrDefault(candidate => candidate.Id == shape.GraphId)
            ?? throw new InvalidOperationException(
                $"Relation root shape '{shape}' requires its exact graph snapshot.");
        return new(graph, shape.ShapeId);
    }

    RelationQueryEvaluationBuilder AddResultDemand(QueryResultDemand demand)
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
                : null,
            assignment.EvidenceReference);
    }

    readonly record struct ParameterAssignment(
        RelationQueryParameterEvidenceState State,
        ObservationValue? Value = null,
        string? EvidenceReference = null);
}

/// <summary>Convenience entry points for target-neutral canonical relation/query evaluation authoring.</summary>
public static class RelationQueryEvaluationAuthoringExtensions
{
    /// <summary>Begins an evaluation of an exact persisted canonical relation/query document.</summary>
    /// <param name="document">Exact relation/query document to evaluate.</param>
    /// <param name="evaluation">Caller-assigned evaluation identity.</param>
    /// <param name="shapeDocuments">Exact persisted shape snapshots available to compilation.</param>
    /// <param name="relationshipCatalogDocument">Exact persisted relationship catalog, or <see langword="null"/>.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <returns>A target-neutral evaluation builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="document"/> is invalid, a shape collection contains
    /// <see langword="null"/>, or
    /// <paramref name="planReference"/> identifies another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationQueryEvaluationBuilder Evaluate(
        this RelationQueryDocument document,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null,
        RelationQueryCompiledPlanReference? planReference = null
        ) =>
        new(document, evaluation, shapeDocuments, relationshipCatalogDocument, planReference);

    /// <summary>Canonicalizes a relation or query definition and begins a target-neutral evaluation.</summary>
    /// <param name="definition">Canonical relation or query definition to evaluate.</param>
    /// <param name="evaluation">Caller-assigned evaluation identity.</param>
    /// <param name="shapeDocuments">Exact persisted shape snapshots available to compilation.</param>
    /// <param name="relationshipCatalogDocument">Exact persisted relationship catalog, or <see langword="null"/>.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <returns>A target-neutral evaluation builder retaining the generated canonical document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evaluation"/> is default, <paramref name="definition"/> is invalid, a shape collection contains
    /// <see langword="null"/>, or
    /// <paramref name="planReference"/> identifies another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The definition contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationQueryEvaluationBuilder Evaluate(
        this RelationQueryDefinition definition,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<ShapeGraphDocument> shapeDocuments = default,
        RelationshipCatalogDocument? relationshipCatalogDocument = null,
        RelationQueryCompiledPlanReference? planReference = null) =>
        new(definition, evaluation, shapeDocuments, relationshipCatalogDocument, planReference);
}
