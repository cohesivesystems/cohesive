using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Reference interpreter for a compiled canonical relation or query over already materialized runtime evidence.
/// </summary>
/// <remarks>
/// The interpreter performs no external acquisition and chooses no physical backend strategy. It executes the
/// compiler-produced demand slice, preserving exact roots and occurrence provenance so requirement gaps and
/// policy effects remain attributable.
/// </remarks>
public sealed class RelationQueryInMemoryInterpreter : IRelationQueryInterpreter
{
    readonly ConditionalWeakTable<CompiledRelationQueryPlan, Lazy<RelationQueryRealizationReport>> realizations = new();

    /// <summary>
    /// Shared stateless interpreter configured with <see cref="DefaultTemporalCapabilities"/>.
    /// </summary>
    public static RelationQueryInMemoryInterpreter Default { get; } = new();

    /// <summary>
    /// Portable expression capabilities implemented by the canonical in-memory interpreter.
    /// </summary>
    /// <remarks>
    /// This target profile is intentionally narrower than the canonical relation-language profile. Execution
    /// preflights the demanded slice against this profile before evaluating any logical node. Collection-element
    /// paths are supported through canonical expansion over already materialized observation values.
    /// </remarks>
    public static ExprCapabilityProfile ExpressionCapabilities =>
        RelationQueryExpressionEvaluator.SupportedCapabilities;

    /// <summary>
    /// Temporal-join semantics supported by the conventional canonical in-memory interpreter.
    /// </summary>
    public static RelationQueryTemporalExecutionCapabilityProfile DefaultTemporalCapabilities =>
        RelationQueryTemporalExecutionCapabilityProfile.All;

    /// <summary>
    /// Complete shared target profile for the conventional canonical in-memory interpreter.
    /// </summary>
    public static RelationQueryTargetCapabilityProfile DefaultTargetProfile =>
        RelationQueryInMemoryTargetProfile.Default;

    /// <summary>Conventional realization compiler policy used by the canonical in-memory interpreter.</summary>
    public static RelationQueryRealizationPolicy DefaultRealizationPolicy =>
        RelationQueryInMemoryTargetProfile.Policy;

    /// <summary>Creates a stateless canonical in-memory interpreter.</summary>
    /// <param name="temporalCapabilities">
    /// Temporal-join semantics available to this interpreter instance, or <see langword="null"/> to use
    /// <see cref="DefaultTemporalCapabilities"/>.
    /// </param>
    public RelationQueryInMemoryInterpreter(
        RelationQueryTemporalExecutionCapabilityProfile? temporalCapabilities = null)
    {
        TemporalCapabilities = temporalCapabilities ?? DefaultTemporalCapabilities;
        TargetProfile = RelationQueryInMemoryTargetProfile.Create(TemporalCapabilities);
    }

    /// <summary>Temporal-join semantics available to this interpreter instance.</summary>
    public RelationQueryTemporalExecutionCapabilityProfile TemporalCapabilities { get; }

    /// <summary>
    /// Shared target capability profile used to realize plans for this interpreter instance.
    /// </summary>
    public RelationQueryTargetCapabilityProfile TargetProfile { get; }

    /// <summary>
    /// Produces an evaluation-independent report for this interpreter's target profile and policy.
    /// </summary>
    /// <remarks>
    /// Reports are computed once per plan and interpreter instance, then weakly cached so execution reuse does not
    /// repeat requirement projection, matching, or fingerprinting and does not extend the plan's lifetime.
    /// </remarks>
    /// <param name="plan">Successful demand-scoped compiled relation/query plan.</param>
    /// <returns>Deterministic realization decisions and diagnostics for <paramref name="plan"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The compiled plan contains inconsistent demand-scoped realization provenance, or a shape snapshot cannot be
    /// represented by compiled-plan canonicalization.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public RelationQueryRealizationReport Realize(CompiledRelationQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return realizations.GetValue(
            plan,
            candidate => new(
                () => CompileRealization(candidate),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    RelationQueryRealizationReport CompileRealization(CompiledRelationQueryPlan plan) =>
        RelationQueryRealizationCompiler.Compile(plan, TargetProfile, DefaultRealizationPolicy);

    /// <inheritdoc />
    public RelationQueryExecutionResult Execute(RelationQueryExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return RelationQueryTelemetryRuntime.IsInterpretationEnabled
            ? ExecuteObserved(request, cancellationToken)
            : ExecuteCore(request, cancellationToken);
    }

    RelationQueryExecutionResult ExecuteObserved(
        RelationQueryExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var activity = RelationQueryTelemetryRuntime.StartActivity(RelationQueryTelemetry.InterpretationActivityName);
        var started = RelationQueryTelemetryRuntime.StartTimer();
        Exception? failure = null;
        RelationQueryExecutionResult? result = null;
        try
        {
            result = ExecuteCore(request, cancellationToken);
            RelationQueryTelemetryRuntime.RecordRequirementGaps(result.RequirementGapAnalysis);
            if (activity?.IsAllDataRequested == true)
            {
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.DefinitionFingerprintTagName,
                    request.Plan.Provenance.DefinitionFingerprint.Value);
                activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, result.Diagnostics.Length);
                activity.SetTag(
                    RelationQueryTelemetry.GapCountTagName,
                    result.RequirementGapAnalysis.Gaps.Length);
                foreach (var diagnostic in result.Diagnostics)
                {
                    RelationQueryTelemetry.AddDiagnosticEvent(
                        activity,
                        diagnostic.Code,
                        diagnostic.Severity);
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException
                                          and not AccessViolationException)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var status = failure is OperationCanceledException
                ? RelationQueryTelemetry.CanceledStatus
                : failure is not null || result is null
                    ? RelationQueryTelemetry.ExceptionStatus
                    : RelationQueryTelemetry.GetStatusTagValue(result.Status);
            RelationQueryTelemetryRuntime.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.InterpretationActivityName,
                status,
                exception: failure);
        }
    }

    RelationQueryExecutionResult ExecuteCore(
        RelationQueryExecutionRequest request,
        CancellationToken cancellationToken)
    {

        var analysis = RelationRequirementGapAnalyzer.Analyze(
            request.Plan,
            request.Evidence,
            request.RequirementGapPolicy);
        if (!analysis.IsEvidenceValid)
        {
            return new(
                RelationQueryExecutionStatus.Failed,
                request.Evidence,
                analysis,
                relation: null,
                queryResults: [],
                analysis.Diagnostics);
        }

        var realization = Realize(request.Plan);
        var unsupportedDiagnostics = RelationQueryInMemorySupportAnalyzer.Analyze(
            realization,
            request.Evidence.Evaluation);
        if (!unsupportedDiagnostics.IsDefaultOrEmpty)
        {
            return new(
                RelationQueryExecutionStatus.Failed,
                request.Evidence,
                analysis,
                relation: null,
                queryResults: [],
                Engine.NormalizeDiagnostics(analysis.Diagnostics.Concat(unsupportedDiagnostics)));
        }

        return new Engine(request, analysis, cancellationToken).Run();
    }

    sealed class Engine
    {
        readonly RelationQueryExecutionRequest request;
        readonly RelationRequirementGapAnalysisResult gapAnalysis;
        readonly CancellationToken cancellationToken;
        readonly RelationQueryEvidenceIndex evidence;
        readonly RelationQueryExpressionEvaluator evaluator = new();
        readonly RelationQueryShapeResolver shapeResolver;
        readonly IReadOnlyDictionary<string, ObservationValue> parameters;
        readonly IReadOnlyDictionary<(ValueBindingId Binding, QualifiedShapeId Shape, FieldPath Path), RelationQueryFieldInput> fieldInputs;
        readonly IReadOnlyDictionary<string, RelationQueryParameterInput> parameterInputs;
        readonly IReadOnlyDictionary<ExprCapabilityId, RelationQueryCapabilityInput> capabilityInputs;
        readonly IReadOnlyDictionary<RelationQueryInputId, RelationQueryCapabilityEvidence> capabilityEvidence;
        readonly IReadOnlyDictionary<RelationRequirementGapId, RelationRequirementGap> gapsById;
        readonly IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationRequirementGap>> directGapsByInput;
        readonly IReadOnlyDictionary<RelationQueryInputId, ImmutableArray<RelationRequirementGap>> blockersByInput;
        readonly IReadOnlyDictionary<RelationQueryOccurrenceId, ImmutableArray<RelationQueryOccurrenceId>> occurrenceParents;
        readonly Dictionary<QueryNodeId, RelationQueryExecutionNode> nodes;
        readonly Dictionary<QueryNodeId, ImmutableArray<RelationQueryRuntimeRow>> results = [];
        readonly HashSet<QueryNodeId> incompleteNodes = [];
        readonly HashSet<QueryNodeId> globallyIncompleteNodes = [];
        readonly Dictionary<QueryNodeId, HashSet<RelationQueryOccurrenceId>> incompleteRootsByNode = [];
        readonly List<RelationRuntimeDiagnostic> executionDiagnostics = [];
        readonly HashSet<string> inconclusiveExpressionSites = new(StringComparer.Ordinal);
        readonly HashSet<RelationQueryOutputId> inconclusiveOutputs = [];
        readonly HashSet<RelationRequirementGapId> activeGaps = [];
        readonly HashSet<(RelationRequirementGapId Gap, RelationQueryOutputId Output)> unrealizablePolicyDecisions = [];
        readonly bool rootPartitioned;

        public Engine(
            RelationQueryExecutionRequest request,
            RelationRequirementGapAnalysisResult gapAnalysis,
            CancellationToken cancellationToken)
        {
            this.request = request;
            this.gapAnalysis = gapAnalysis;
            this.cancellationToken = cancellationToken;
            evidence = new(request.Plan, request.Evidence);
            shapeResolver = new(
                [.. request.Plan.Provenance.ShapeDocuments.Select(static document => document.Graph)]);
            parameters = evidence.CreateEffectiveParameterValues();
            fieldInputs = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryFieldInput>()
                .GroupBy(static input => (input.Binding, input.Field.Shape, input.Field.Path))
                .ToDictionary(static group => group.Key, static group => group.First());
            parameterInputs = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryParameterInput>()
                .ToDictionary(static input => input.Parameter.Value, StringComparer.Ordinal);
            capabilityInputs = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryCapabilityInput>()
                .ToDictionary(static input => input.Capability.Capability);
            capabilityEvidence = request.Evidence.Capabilities
                .ToDictionary(static item => item.Input);
            gapsById = gapAnalysis.Gaps.ToDictionary(static gap => gap.Id);
            directGapsByInput = gapAnalysis.Gaps
                .GroupBy(static gap => gap.Input.Id)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .OrderBy(static gap => gap.Id.Value, StringComparer.Ordinal)
                        .ToImmutableArray());
            blockersByInput = gapAnalysis.Gaps
                .SelectMany(static gap => gap.BlockedInputs.Select(blocked => (Blocked: blocked, Gap: gap)))
                .GroupBy(static item => item.Blocked)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .Select(static item => item.Gap)
                        .DistinctBy(static gap => gap.Id)
                        .OrderBy(static gap => gap.Id.Value, StringComparer.Ordinal)
                        .ToImmutableArray());
            occurrenceParents = request.Evidence.Traversals
                .SelectMany(static traversal => traversal.Results.Select(result => (
                    Child: result.Id,
                    Parent: traversal.From)))
                .GroupBy(static relation => relation.Child)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .Select(static relation => relation.Parent)
                        .Distinct()
                        .OrderBy(static parent => parent.Value, StringComparer.Ordinal)
                        .ToImmutableArray());
            nodes = request.Plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
            rootPartitioned = request.Plan.Definition is Cohesive.Relations.IR.RelationDefinition
            {
                Output.Mode: not RelationOutputMode.Set
            };
        }

        public RelationQueryExecutionResult Run()
        {
            try
            {
                foreach (var node in request.Plan.ExecutionSlice.Nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(node.Id, ExecuteNode(node));
                    PropagateIncompleteInputs(node.Id, node.LogicalPlan.EffectiveInputs);
                }

                RelationQueryRelationResult? relation = null;
                ImmutableArray<RelationQueryNamedResult> queryResults = [];
                if (request.Plan.ExecutionSlice.RelationOutput is { } relationOutput)
                    relation = MaterializeRelation(relationOutput);
                else
                    queryResults = MaterializeQueryResults(request.Plan.ExecutionSlice.QueryResults);

                RecordInconclusiveDiagnostics();
                var effectiveGapAnalysis = CreateEffectiveGapAnalysis();
                var diagnostics = NormalizeDiagnostics(effectiveGapAnalysis.Diagnostics.Concat(executionDiagnostics));
                var failed = executionDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                var incompleteOutputs = relation?.State == RelationQueryExecutionOutputState.Incomplete
                    || queryResults.Any(static result => result.State == RelationQueryExecutionOutputState.Incomplete);
                var hasUnresolved = effectiveGapAnalysis.Decisions.Any(static decision => decision.Disposition.Kind == RelationRequirementGapDispositionKind.Unresolved);
                var status = failed
                    ? RelationQueryExecutionStatus.Failed
                    : !gapAnalysis.IsConclusive
                        || hasUnresolved
                        || incompleteOutputs
                        || inconclusiveExpressionSites.Count != 0
                        || inconclusiveOutputs.Count != 0
                            ? RelationQueryExecutionStatus.Incomplete
                            : RelationQueryExecutionStatus.Succeeded;
                return new(
                    status,
                    request.Evidence,
                    effectiveGapAnalysis,
                    relation,
                    queryResults,
                    diagnostics
                    );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RelationQueryExecutionException exception)
            {
                executionDiagnostics.Add(new(
                    exception.Code,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    request.Evidence.Evaluation,
                    occurrence: exception.Occurrence,
                    node: exception.Node,
                    semanticSite: exception.SemanticSite));
                return new(
                    RelationQueryExecutionStatus.Failed,
                    request.Evidence,
                    gapAnalysis,
                    relation: null,
                    queryResults: [],
                    NormalizeDiagnostics(gapAnalysis.Diagnostics.Concat(executionDiagnostics)));
            }
            catch (RelationQueryExpressionEvaluationException exception)
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    request.Evidence.Evaluation));
                return new(
                    RelationQueryExecutionStatus.Failed,
                    request.Evidence,
                    gapAnalysis,
                    relation: null,
                    queryResults: [],
                    NormalizeDiagnostics(gapAnalysis.Diagnostics.Concat(executionDiagnostics)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    DiagnosticSeverity.Error,
                    $"Canonical in-memory execution failed: {exception.Message}",
                    request.Evidence.Evaluation));
                return new(
                    RelationQueryExecutionStatus.Failed,
                    request.Evidence,
                    gapAnalysis,
                    relation: null,
                    queryResults: [],
                    NormalizeDiagnostics(gapAnalysis.Diagnostics.Concat(executionDiagnostics)));
            }
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteNode(RelationQueryExecutionNode execution) => execution.CanonicalNode switch
        {
            SourceQueryNode node => ExecuteSource(node),
            FilterQueryNode node => ExecuteFilter(execution, node),
            TraverseRelationshipQueryNode node => ExecuteTraversal(execution, node),
            JoinQueryNode node => ExecuteJoin(execution, node),
            TemporalJoinQueryNode node => ExecuteTemporalJoin(execution, node),
            ExpandCollectionQueryNode node => ExecuteExpand(execution, node),
            ProjectQueryNode node => ExecuteProject(execution, node),
            DistinctQueryNode node => ExecuteDistinct(execution, node),
            AggregateQueryNode node => ExecuteAggregate(execution, node),
            OrderQueryNode node => ExecuteOrder(execution, node),
            PageQueryNode node => ExecutePage(execution, node),
            _ => throw Failure(
                RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                $"Logical node '{execution.CanonicalNode.GetType().Name}' is not supported.",
                execution.Id)
        };

        ImmutableArray<RelationQueryRuntimeRow> ExecuteSource(SourceQueryNode node)
        {
            var input = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQuerySourceSetInput>()
                .SingleOrDefault(candidate => candidate.Source == node.Id)
                ?? throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    $"Retained source node '{node.Id.Value}' has no compiled source-set input.",
                    node.Id);
            var directGaps = DirectGaps(input.Id, row: null);
            if (!directGaps.IsDefaultOrEmpty)
            {
                MarkNodeIncomplete(node.Id);
                RecordUnrealizableStructuralSubstitutions(input.Id, directGaps);
                return [];
            }
            if (!evidence.TryCreateSourceRows(input, out var rows))
            {
                MarkNodeIncomplete(node.Id);
                return [];
            }

            if (evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                MarkNodeIncomplete(node.Id);

            return rootPartitioned || input.Role != RelationQuerySourceInputRole.RelationRoot
                ? rows
                : [.. rows.Select(static row => row.WithoutRoot())];
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteFilter(RelationQueryExecutionNode execution, FilterQueryNode node)
        {
            var site = SingleSite(execution, RelationQueryExpressionSiteKind.FilterPredicate);
            var filtered = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryEvaluate(site, row, out var value))
                    continue;
                if (RequireBoolean(value, site, row))
                    filtered.Add(row);
            }
            return [.. filtered];
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteTraversal(
            RelationQueryExecutionNode execution,
            TraverseRelationshipQueryNode node)
        {
            var input = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryRelationshipInput>()
                .SingleOrDefault(candidate => candidate.Traversal == node.Id)
                ?? throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    $"Retained traversal node '{node.Id.Value}' has no compiled relationship input.",
                    node.Id);
            var traversed = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!row.TryGetBinding(node.From, out var from))
                {
                    throw Failure(
                        RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                        $"Traversal '{node.Id.Value}' cannot resolve source binding '{node.From.Value}'.",
                        node.Id);
                }

                if (from.Kind == RelationQueryRuntimeBindingKind.Absent)
                {
                    if (node.JoinKind == JoinKind.Left)
                        traversed.Add(row.WithBinding(node.Result, RelationQueryRuntimeBinding.CreateAbsent(input.ResultShape)));
                    continue;
                }

                if (from.Occurrence is not { } occurrence)
                {
                    throw Failure(
                        RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                        $"Traversal '{node.Id.Value}' requires an observed source occurrence.",
                        node.Id);
                }

                var causalBlockers = BlockingGaps(input.Id, row);
                if (!causalBlockers.IsDefaultOrEmpty)
                {
                    MarkNodeIncomplete(node.Id, row);
                    RecordUnrealizableStructuralSubstitutions(input.Id, causalBlockers);
                    continue;
                }

                var directGaps = DirectGaps(input.Id, row);
                ActivateGaps(directGaps);
                var blockingDirectGaps = directGaps.Where(static gap => gap.Cause is
                        RelationRequirementGapCause.ConversionFailure
                        or RelationRequirementGapCause.CardinalityViolation)
                    .ToImmutableArray();
                if (!blockingDirectGaps.IsDefaultOrEmpty)
                {
                    MarkNodeIncomplete(node.Id, row);
                    RecordUnrealizableStructuralSubstitutions(input.Id, blockingDirectGaps);
                    continue;
                }

                if (!evidence.TryGetTraversal(input, occurrence, out var traversal))
                {
                    MarkNodeIncomplete(node.Id, row);
                    RecordUnrealizableStructuralSubstitutions(input.Id, directGaps);
                    continue;
                }

                if (traversal.State == RelationQueryTraversalEvidenceState.NotApplicable)
                {
                    if (node.JoinKind == JoinKind.Left)
                    {
                        traversed.Add(row.WithBinding(
                            node.Result,
                            RelationQueryRuntimeBinding.CreateAbsent(input.ResultShape)));
                    }
                }
                else if (traversal.State == RelationQueryTraversalEvidenceState.Completed)
                {
                    if (traversal.Completeness == RelationQueryEvidenceCompleteness.Partial)
                        MarkNodeIncomplete(node.Id, row);

                    foreach (var related in traversal.Results)
                    {
                        traversed.Add(row.WithBinding(
                            node.Result,
                            evidence.CreateObservedBinding(related)));
                    }

                    if (traversal.Results.IsDefaultOrEmpty
                        && traversal.Completeness == RelationQueryEvidenceCompleteness.Complete
                        && node.JoinKind == JoinKind.Left)
                    {
                        traversed.Add(row.WithBinding(
                            node.Result,
                            RelationQueryRuntimeBinding.CreateAbsent(input.ResultShape)));
                    }
                    else if (traversal.Results.IsDefaultOrEmpty && node.JoinKind == JoinKind.Inner)
                    {
                        RecordUnrealizableStructuralSubstitutions(input.Id, directGaps);
                    }
                }
                else
                {
                    MarkNodeIncomplete(node.Id, row);
                    RecordUnrealizableStructuralSubstitutions(input.Id, directGaps);
                }
            }

            return traversed.ToImmutable();
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteJoin(
            RelationQueryExecutionNode execution,
            JoinQueryNode node)
        {
            var left = InputRows(execution, inputIndex: 0);
            var right = InputRows(execution, inputIndex: 1);
            var site = SingleSite(execution, RelationQueryExpressionSiteKind.JoinPredicate);
            var matchedRight = new bool[right.Length];
            var unknownRight = new bool[right.Length];
            var joined = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();

            foreach (var leftRow in left)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matched = false;
                var unknown = false;
                for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanJoinRows(leftRow, right[rightIndex]))
                        continue;

                    var candidate = leftRow.Merge(right[rightIndex]);
                    if (!TryEvaluate(site, candidate, out var value))
                    {
                        unknown = true;
                        unknownRight[rightIndex] = true;
                        continue;
                    }
                    if (!RequireBoolean(value, site, candidate))
                        continue;

                    matched = true;
                    matchedRight[rightIndex] = true;
                    joined.Add(candidate);
                }

                if (!matched && !unknown && node.Kind is JoinKind.Left or JoinKind.Full)
                    joined.Add(leftRow.Merge(CreateAbsentSide(execution.LogicalPlan.EffectiveInputs[1])));
            }

            if (node.Kind is JoinKind.Right or JoinKind.Full)
            {
                var absentLeft = CreateAbsentSide(execution.LogicalPlan.EffectiveInputs[0]);
                for (var index = 0; index < right.Length; index++)
                {
                    if (!matchedRight[index] && !unknownRight[index])
                        joined.Add(absentLeft.Merge(right[index]));
                }
            }

            return joined.ToImmutable();
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteTemporalJoin(
            RelationQueryExecutionNode execution,
            TemporalJoinQueryNode node)
        {
            var temporal = execution.TemporalJoin
                ?? throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    $"Temporal join node '{node.Id.Value}' has no prepared temporal execution semantics.",
                    node.Id);
            var leftInput = execution.LogicalPlan.EffectiveInputs[0];
            var rightInput = execution.LogicalPlan.EffectiveInputs[1];
            var left = InputRows(execution, inputIndex: 0);
            var right = InputRows(execution, inputIndex: 1);
            var matchedRight = new bool[right.Length];
            var indeterminateRight = new bool[right.Length];
            var joined = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();

            foreach (var leftRow in left)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matched = false;
                var indeterminate = false;
                for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanJoinRows(leftRow, right[rightIndex]))
                        continue;

                    var candidate = leftRow.Merge(right[rightIndex]);
                    if (!TryEvaluate(temporal.CorrelationSite, candidate, out var correlation))
                    {
                        indeterminate = true;
                        indeterminateRight[rightIndex] = true;
                        continue;
                    }
                    if (!RequireBoolean(correlation, temporal.CorrelationSite, candidate))
                        continue;

                    if (!TryEvaluateTemporalMatch(temporal, candidate, out var temporalResult)
                        || temporalResult is RelationQueryTemporalEvaluationKind.InvalidOperand
                            or RelationQueryTemporalEvaluationKind.InvalidInterval)
                    {
                        indeterminate = true;
                        indeterminateRight[rightIndex] = true;
                        MarkNodeIncomplete(node.Id, candidate);
                        continue;
                    }
                    if (temporalResult == RelationQueryTemporalEvaluationKind.NoMatch)
                        continue;

                    matched = true;
                    matchedRight[rightIndex] = true;
                    joined.Add(candidate);
                }

                if (!matched
                    && !indeterminate
                    && IsNodeCompleteForPartition(rightInput, leftRow.Root?.Id)
                    && node.Kind is JoinKind.Left or JoinKind.Full)
                {
                    joined.Add(leftRow.Merge(CreateAbsentSide(rightInput)));
                }
            }

            if (node.Kind is JoinKind.Right or JoinKind.Full)
            {
                var absentLeft = CreateAbsentSide(leftInput);
                for (var index = 0; index < right.Length; index++)
                {
                    if (!matchedRight[index]
                        && !indeterminateRight[index]
                        && IsNodeCompleteForPartition(leftInput, right[index].Root?.Id))
                    {
                        joined.Add(absentLeft.Merge(right[index]));
                    }
                }
            }

            return joined.ToImmutable();
        }

        bool TryEvaluateTemporalMatch(
            RelationQueryTemporalJoinExecution temporal,
            RelationQueryRuntimeRow row,
            out RelationQueryTemporalEvaluationKind result)
        {
            // A fully structurally unbounded overlap has no finite operand from which to infer a domain.
            // Its result is domain-independent, so any supported temporal domain can drive the pure helper.
            var domain = temporal.Domain ?? ScalarTypeKind.Instant;
            var allAvailable = true;
            var hasInvalidOperand = false;
            var hasInvalidInterval = false;
            var point = ObservationValue.Undefined;
            if (temporal.PointSite is { } pointSite)
            {
                if (!TryEvaluate(pointSite, row, out point))
                {
                    allAvailable = false;
                }
                else if (!RelationQueryTemporalSemantics.TryCompare(domain, point, point, out _))
                {
                    hasInvalidOperand = true;
                    RecordInvalidTemporalOperand(pointSite, row, domain);
                }
            }

            var intervals = ImmutableArray.CreateBuilder<RelationQueryTemporalIntervalValue>(
                temporal.Intervals.Length);
            foreach (var interval in temporal.Intervals)
            {
                var lowerAvailable = TryEvaluateTemporalBound(
                    interval.Lower,
                    row,
                    domain,
                    out var lower,
                    ref hasInvalidOperand);
                var upperAvailable = TryEvaluateTemporalBound(
                    interval.Upper,
                    row,
                    domain,
                    out var upper,
                    ref hasInvalidOperand);
                allAvailable &= lowerAvailable && upperAvailable;

                var value = new RelationQueryTemporalIntervalValue(lower, upper);
                intervals.Add(value);
                if (!lowerAvailable || !upperAvailable)
                    continue;

                switch (RelationQueryTemporalSemantics.ClassifyInterval(domain, value))
                {
                    case RelationQueryTemporalIntervalKind.InvalidOperand:
                        hasInvalidOperand = true;
                        break;
                    case RelationQueryTemporalIntervalKind.InvalidInterval:
                        hasInvalidInterval = true;
                        RecordInvalidTemporalInterval(temporal, interval, row);
                        break;
                }
            }

            if (!allAvailable)
            {
                result = RelationQueryTemporalEvaluationKind.InvalidOperand;
                return false;
            }
            if (hasInvalidOperand)
            {
                result = RelationQueryTemporalEvaluationKind.InvalidOperand;
                return true;
            }
            if (hasInvalidInterval)
            {
                result = RelationQueryTemporalEvaluationKind.InvalidInterval;
                return true;
            }

            result = temporal.Definition.Match switch
            {
                TemporalPointInIntervalMatch => RelationQueryTemporalSemantics.PointInInterval(
                    domain,
                    point,
                    intervals[0]),
                TemporalIntervalOverlapMatch => RelationQueryTemporalSemantics.IntervalsOverlap(
                    domain,
                    intervals[0],
                    intervals[1]),
                _ => RelationQueryTemporalEvaluationKind.InvalidOperand
            };
            return true;
        }

        bool TryEvaluateTemporalBound(
            RelationQueryTemporalBoundExecution bound,
            RelationQueryRuntimeRow row,
            ScalarTypeKind domain,
            out RelationQueryTemporalBoundValue value,
            ref bool hasInvalidOperand)
        {
            ObservationValue? evaluated = null;
            if (bound.ValueSite is { } site)
            {
                if (!TryEvaluate(site, row, out var result))
                {
                    value = RelationQueryTemporalBoundValue.Invalid();
                    return false;
                }
                evaluated = result;
            }

            value = RelationQueryTemporalSemantics.ResolveBound(bound.Definition, evaluated);
            if (value.Kind == RelationQueryTemporalBoundValueKind.Invalid
                || value.Kind == RelationQueryTemporalBoundValueKind.Finite
                && !RelationQueryTemporalSemantics.TryCompare(domain, value.Value, value.Value, out _))
            {
                hasInvalidOperand = true;
                if (bound.ValueSite is { } invalidSite)
                    RecordInvalidTemporalOperand(invalidSite, row, domain);
            }
            return true;
        }

        void RecordInvalidTemporalOperand(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row,
            ScalarTypeKind domain)
        {
            if (site.Node is { } node)
                MarkNodeIncomplete(node, row);
            executionDiagnostics.Add(new(
                RelationRuntimeDiagnosticCodes.ExecutionTemporalOperandInvalid,
                DiagnosticSeverity.Warning,
                $"Temporal join operand at site '{site.Analysis.Site.Id.Value}' is null, missing, malformed, or outside the declared '{domain}' domain.",
                request.Evidence.Evaluation,
                occurrence: ResolveSiteOccurrence(site, row),
                node: site.Node,
                semanticSite: site.Analysis.Site.Id.Value));
        }

        void RecordInvalidTemporalInterval(
            RelationQueryTemporalJoinExecution temporal,
            RelationQueryTemporalIntervalExecution interval,
            RelationQueryRuntimeRow row)
        {
            MarkNodeIncomplete(temporal.Definition.Id, row);
            var site = interval.Lower.ValueSite ?? interval.Upper.ValueSite;
            var semanticSite = site is null
                ? $"{temporal.Definition.Id.Value}/temporalJoin/interval/{interval.Ordinal}"
                : site.Analysis.Site.Id.Value[..site.Analysis.Site.Id.Value.LastIndexOf('/')];
            executionDiagnostics.Add(new(
                RelationRuntimeDiagnosticCodes.ExecutionTemporalIntervalInvalid,
                DiagnosticSeverity.Warning,
                $"Temporal join interval at site '{semanticSite}' has a lower endpoint after its upper endpoint.",
                request.Evidence.Evaluation,
                occurrence: ResolveIntervalOccurrence(interval, row),
                node: temporal.Definition.Id,
                semanticSite: semanticSite));
        }

        static RelationQueryOccurrenceId? ResolveIntervalOccurrence(
            RelationQueryTemporalIntervalExecution interval,
            RelationQueryRuntimeRow row)
        {
            var occurrences = new[] { interval.Lower.ValueSite, interval.Upper.ValueSite }
                .Where(static site => site is not null)
                .Select(site => TryResolveSiteOccurrence(site!, row, out var occurrence)
                    ? occurrence
                    : (RelationQueryOccurrenceId?)null)
                .Where(static occurrence => occurrence is not null)
                .Select(static occurrence => occurrence!.Value)
                .Distinct()
                .ToArray();
            return occurrences.Length == 1 ? occurrences[0] : row.Root?.Id;
        }

        static RelationQueryOccurrenceId? ResolveSiteOccurrence(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row) =>
            TryResolveSiteOccurrence(site, row, out var occurrence)
                ? occurrence
                : row.Root?.Id;

        static bool TryResolveSiteOccurrence(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row,
            out RelationQueryOccurrenceId occurrence)
        {
            var bindings = site.Analysis.Requirements.Fields
                .Where(static field => field.Root == ExprFieldRootKind.Binding)
                .Select(static field => field.Binding)
                .Where(static binding => binding is not null)
                .Select(static binding => binding!.Value)
                .Distinct()
                .ToArray();
            if (bindings.Length == 1
                && row.TryGetBinding(bindings[0], out var binding)
                && binding.Occurrence is { } observed)
            {
                occurrence = observed.Id;
                return true;
            }

            occurrence = default;
            return false;
        }

        void MarkNodeIncomplete(QueryNodeId node)
        {
            incompleteNodes.Add(node);
            globallyIncompleteNodes.Add(node);
        }

        void MarkNodeIncomplete(QueryNodeId node, RelationQueryRuntimeRow row)
        {
            if (!rootPartitioned || row.Root is not { } root)
            {
                MarkNodeIncomplete(node);
                return;
            }

            MarkNodeIncompleteForRoot(node, root.Id);
        }

        void MarkNodeIncompleteForRoot(QueryNodeId node, RelationQueryOccurrenceId root)
        {
            incompleteNodes.Add(node);
            if (!incompleteRootsByNode.TryGetValue(node, out var roots))
            {
                roots = [];
                incompleteRootsByNode.Add(node, roots);
            }
            roots.Add(root);
        }

        void PropagateIncompleteInputs(
            QueryNodeId node,
            ImmutableArray<QueryNodeId> inputs)
        {
            foreach (var input in inputs)
            {
                if (globallyIncompleteNodes.Contains(input))
                    MarkNodeIncomplete(node);
                if (!incompleteRootsByNode.TryGetValue(input, out var roots))
                    continue;
                foreach (var root in roots)
                    MarkNodeIncompleteForRoot(node, root);
            }
        }

        bool IsNodeCompleteForPartition(
            QueryNodeId node,
            RelationQueryOccurrenceId? root)
        {
            if (globallyIncompleteNodes.Contains(node))
                return false;
            if (!incompleteRootsByNode.TryGetValue(node, out var roots))
                return true;
            return root is { } partition
                ? !roots.Contains(partition)
                : roots.Count == 0;
        }

        bool CanJoinRows(
            RelationQueryRuntimeRow left,
            RelationQueryRuntimeRow right) =>
            !rootPartitioned
            || left.Root is null
            || right.Root is null
            || left.Root.Id == right.Root.Id;

        ImmutableArray<RelationQueryRuntimeRow> ExecuteExpand(
            RelationQueryExecutionNode execution,
            ExpandCollectionQueryNode node)
        {
            var site = SingleSite(execution, RelationQueryExpressionSiteKind.ExpandCollection);
            var expanded = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryEvaluate(site, row, out var collection)
                    || collection.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                {
                    continue;
                }
                if (collection.Kind != ObservationValueKind.Array || collection.Array.IsDefault)
                {
                    throw ExpressionFailure(
                        site,
                        row,
                        $"Collection expansion requires an array, but received '{collection.Kind}'.");
                }

                for (var index = 0; index < collection.Array.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = collection.Array[index];
                    RelationQueryObservationOccurrence? itemOccurrence = null;
                    if (node.ItemShape is { } itemShape
                        && node.Collection is FieldExpr { Binding: { } ownerBinding }
                        && row.TryGetBinding(ownerBinding, out var owner)
                        && owner.Occurrence is { } ownerOccurrence)
                    {
                        itemOccurrence = new(
                            RelationQueryCollectionOccurrenceIdentity.Create(
                                node.Id,
                                ownerOccurrence.Id,
                                index),
                            node.ItemBinding,
                            itemShape);
                    }
                    expanded.Add(row.WithBinding(
                        node.ItemBinding,
                        RelationQueryRuntimeBinding.FromComputed(
                            ResolveBindingShape(execution, node.ItemBinding),
                            item,
                            occurrence: itemOccurrence)));
                }
            }
            return expanded.ToImmutable();
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteProject(
            RelationQueryExecutionNode execution,
            ProjectQueryNode node)
        {
            var projected = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            var topLevelOnly = execution.ProjectionAssignments.All(static assignment =>
                RelationQueryObjectValues.TryGetTopLevelFieldName(
                    assignment.Definition.Target,
                    out _));
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = RelationQueryObjectValues.Empty;
                var topLevelFields = topLevelOnly
                    ? ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal)
                    : null;
                List<FieldPath> unavailableFields = [];
                foreach (var assignment in execution.ProjectionAssignments)
                {
                    if (TryEvaluate(assignment.ValueSite, row, out var assigned))
                    {
                        if (topLevelFields is null)
                        {
                            value = RelationQueryObjectValues.Set(value, assignment.Definition.Target, assigned);
                        }
                        else
                        {
                            _ = RelationQueryObjectValues.TryGetTopLevelFieldName(
                                assignment.Definition.Target,
                                out var fieldName);
                            if (assigned.Kind == ObservationValueKind.Undefined)
                                topLevelFields.Remove(fieldName);
                            else
                                topLevelFields[fieldName] = assigned;
                        }
                    }
                    else
                        unavailableFields.Add(assignment.Definition.Target);
                }

                if (topLevelFields is not null)
                    value = ObservationValue.FromObject(topLevelFields.ToImmutable());

                projected.Add(row.WithOnlyBinding(
                    node.ResultBinding,
                    RelationQueryRuntimeBinding.FromComputed(
                        node.ResultShape,
                        value,
                        [.. unavailableFields])));
            }
            return projected.ToImmutable();
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteDistinct(RelationQueryExecutionNode execution, DistinctQueryNode node)
        {
            List<RelationQueryRuntimeRow> distinct = [];
            Dictionary<RelationQueryValueVector, int> retained = [];
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RelationQueryValueVector key;
                if (execution.DistinctKeys.IsDefaultOrEmpty)
                {
                    List<ObservationValue> parts = [];
                    AddRootPartition(parts, row);
                    foreach (var (binding, value) in row.Bindings.OrderBy(
                                 static pair => pair.Key.Value,
                                 StringComparer.Ordinal))
                    {
                        parts.Add(ObservationValue.FromString(binding.Value));
                        parts.Add(ObservationValue.FromInt64((int)value.Kind));
                        parts.Add(value.Value);
                    }
                    key = new(parts);
                }
                else
                {
                    List<ObservationValue> parts = [];
                    AddRootPartition(parts, row);
                    var complete = true;
                    foreach (var site in execution.DistinctKeys)
                    {
                        if (TryEvaluate(site, row, out var value))
                            parts.Add(value);
                        else
                            complete = false;
                    }
                    if (!complete)
                        continue;
                    key = new(parts);
                }

                if (retained.TryGetValue(key, out var retainedIndex))
                {
                    distinct[retainedIndex] = distinct[retainedIndex]
                        .WithAdditionalProvenance(row.Provenance);
                    continue;
                }

                retained.Add(key, distinct.Count);
                distinct.Add(row);
            }

            return [.. distinct];
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteAggregate(
            RelationQueryExecutionNode execution,
            AggregateQueryNode node)
        {
            List<AggregateGroup> groups = [];
            Dictionary<RelationQueryValueVector, AggregateGroup> byKey = [];
            foreach (var row in InputRows(execution, inputIndex: 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ObservationValue> keyParts = [];
                AddRootPartition(keyParts, row);
                var groupingValues = new ObservationValue[execution.AggregateGroupings.Length];
                var complete = true;
                for (var index = 0; index < execution.AggregateGroupings.Length; index++)
                {
                    if (!TryEvaluate(execution.AggregateGroupings[index].KeySite, row, out var key))
                    {
                        complete = false;
                        break;
                    }
                    groupingValues[index] = key;
                    keyParts.Add(key);
                }
                if (!complete)
                    continue;

                var vector = new RelationQueryValueVector(keyParts);
                if (!byKey.TryGetValue(vector, out var group))
                {
                    group = new(groupingValues, row.Root);
                    byKey.Add(vector, group);
                    groups.Add(group);
                }
                group.Rows.Add(row);
            }

            if (execution.AggregateGroupings.IsDefaultOrEmpty)
            {
                if (rootPartitioned)
                {
                    foreach (var root in RelationRoots())
                    {
                        var key = new RelationQueryValueVector(
                        [
                            ObservationValue.FromString("$relationRoot"),
                            ObservationValue.FromString(root.Id.Value)
                        ]);
                        if (!byKey.ContainsKey(key))
                        {
                            var group = new AggregateGroup([], root);
                            byKey.Add(key, group);
                            groups.Add(group);
                        }
                    }
                }
                else if (groups.Count == 0)
                {
                    groups.Add(new([], root: null));
                }
            }

            var aggregated = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = RelationQueryObjectValues.Empty;
                List<FieldPath> unavailableFields = [];
                for (var index = 0; index < execution.AggregateGroupings.Length; index++)
                {
                    output = RelationQueryObjectValues.Set(
                        output,
                        execution.AggregateGroupings[index].Definition.Target,
                        group.GroupingValues[index]);
                }

                foreach (var assignment in execution.AggregateAssignments)
                {
                    List<ObservationValue> values = [];
                    long rowCount = 0;
                    var assignmentUnavailable = !TryUseAggregateCapability(execution, assignment);
                    foreach (var row in group.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (assignment.FilterSite is { } filter)
                        {
                            if (!TryEvaluate(filter, row, out var predicate))
                            {
                                assignmentUnavailable = true;
                                continue;
                            }
                            if (!RequireBoolean(predicate, filter, row))
                            {
                                continue;
                            }
                        }

                        rowCount++;
                        if (assignment.ValueSite is not { } valueSite)
                            continue;
                        if (!TryEvaluate(valueSite, row, out var value))
                        {
                            assignmentUnavailable = true;
                            continue;
                        }
                        if (RelationQueryValueSemantics.IsNullish(value))
                        {
                            continue;
                        }
                        values.Add(value);
                    }

                    var aggregate = assignmentUnavailable
                        ? ObservationValue.Undefined
                        : assignment.Definition.Operation == AggregateOperator.Count
                            && assignment.ValueSite is null
                                ? ObservationValue.FromInt64(rowCount)
                                : evaluator.Aggregate(assignment.Definition.Operation, values);
                    output = RelationQueryObjectValues.Set(
                        output,
                        assignment.Definition.Target,
                        aggregate);
                    if (assignmentUnavailable)
                        unavailableFields.Add(assignment.Definition.Target);
                }

                var aggregateRow = group.Rows.Count == 0
                    ? group.Root is null
                        ? RelationQueryRuntimeRow.Empty
                        : RelationQueryRuntimeRow.Empty.WithRoot(group.Root)
                    : MergeAggregateProvenance(group.Rows);
                aggregated.Add(aggregateRow.WithOnlyBinding(
                    node.ResultBinding,
                    RelationQueryRuntimeBinding.FromComputed(
                        node.ResultShape,
                        output,
                        [.. unavailableFields])));
            }

            return aggregated.ToImmutable();
        }

        bool TryUseAggregateCapability(
            RelationQueryExecutionNode execution,
            RelationQueryAggregateAssignmentExecution assignment)
        {
            var capability = ExprCapabilities.ForAggregate(assignment.Definition.Operation);
            if (IsCapabilityAvailable(capability))
                return true;

            var site = $"{execution.Id.Value}/aggregate/{assignment.Definition.Id.Value}/operation";
            var input = capabilityInputs.GetValueOrDefault(capability);
            var gaps = input is null
                ? []
                : gapAnalysis.Gaps.Where(gap =>
                    gap.Input.Id == input.Id && activeGaps.Contains(gap.Id)).ToArray();
            var gapIds = gaps.Select(static gap => gap.Id).ToHashSet();
            var isDispositioned = gapIds.Count != 0
                && gapAnalysis.Decisions
                    .Where(decision => gapIds.Contains(decision.Gap))
                    .All(static decision =>
                        decision.Disposition.Kind != RelationRequirementGapDispositionKind.Unresolved);
            if (!isDispositioned)
            {
                inconclusiveExpressionSites.Add(site);
                if (input is not null)
                {
                    foreach (var edge in request.Plan.RequirementGraph.Edges.Where(edge =>
                                 edge.Input.Id == input.Id))
                    {
                        inconclusiveOutputs.Add(edge.Output.Id);
                    }
                }
            }

            return false;
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteOrder(
            RelationQueryExecutionNode execution,
            OrderQueryNode node)
        {
            var rows = InputRows(execution, inputIndex: 0);
            return ApplyPerRootPartition(rows, partition =>
            {
                List<OrderEntry> entries = [];
                for (var index = 0; index < partition.Length; index++)
                {
                    var row = partition[index];
                    var keys = new ObservationValue[execution.OrderKeys.Length];
                    var complete = true;
                    for (var keyIndex = 0; keyIndex < execution.OrderKeys.Length; keyIndex++)
                    {
                        if (!TryEvaluate(execution.OrderKeys[keyIndex], row, out keys[keyIndex]))
                        {
                            complete = false;
                            break;
                        }
                    }
                    if (complete)
                        entries.Add(new(row, keys, index));
                }

                try
                {
                    entries.Sort((left, right) => CompareOrderEntries(left, right, execution, node));
                }
                catch (InvalidOperationException exception)
                    when (exception.InnerException is
                        RelationQueryExecutionException or OperationCanceledException)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
                return [.. entries.Select(static entry => entry.Row)];
            });
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecutePage(
            RelationQueryExecutionNode execution,
            PageQueryNode node)
        {
            var rows = InputRows(execution, inputIndex: 0);
            return node.Page switch
            {
                OffsetPageDefinition offset => ApplyPerRootPartition(
                    rows,
                    partition => [.. partition.Skip(offset.Offset).Take(offset.Limit)]),
                KeysetPageDefinition keyset => ExecuteKeysetPage(execution, keyset, rows),
                _ => throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    $"Page definition '{node.Page.GetType().Name}' is not supported.",
                    node.Id)
            };
        }

        ImmutableArray<RelationQueryRuntimeRow> ExecuteKeysetPage(
            RelationQueryExecutionNode execution,
            KeysetPageDefinition page,
            ImmutableArray<RelationQueryRuntimeRow> rows)
        {
            if (page.After.IsDefaultOrEmpty)
            {
                return ApplyPerRootPartition(
                    rows,
                    partition => [.. partition.Take(page.Limit)]);
            }

            var inputNode = nodes[execution.LogicalPlan.EffectiveInputs[0]];
            if (inputNode.CanonicalNode is not OrderQueryNode order
                || inputNode.OrderKeys.Length != execution.KeysetBoundaries.Length)
            {
                throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    $"Keyset page '{execution.Id.Value}' requires a matching retained order node.",
                    execution.Id);
            }

            var boundaries = new ObservationValue[execution.KeysetBoundaries.Length];
            for (var index = 0; index < boundaries.Length; index++)
            {
                if (!TryEvaluate(
                        execution.KeysetBoundaries[index],
                        RelationQueryRuntimeRow.Empty,
                        out boundaries[index]))
                {
                    return [];
                }
                if (RelationQueryValueSemantics.IsNullish(boundaries[index])
                    && HasGapAtSite(RelationQueryRuntimeRow.Empty, execution.KeysetBoundaries[index]))
                {
                    inconclusiveExpressionSites.Add(
                        execution.KeysetBoundaries[index].Analysis.Site.Id.Value);
                    return [];
                }
            }

            return ApplyPerRootPartition(rows, partition =>
            {
                List<RelationQueryRuntimeRow> selected = [];
                foreach (var row in partition)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var comparison = CompareRowToBoundary(row, inputNode, order, boundaries);
                    if (comparison > 0)
                        selected.Add(row);
                    if (selected.Count == page.Limit)
                        break;
                }
                return [.. selected];
            });
        }

        RelationQueryRelationResult MaterializeRelation(
            RelationQueryRelationExecutionOutput terminal)
        {
            var sourceRows = results[terminal.Definition.Node];
            List<RelationQueryOutputRow> outputRows = [];
            var anyRowSuppressed = false;
            foreach (var row in sourceRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetOutputObject(row, terminal.Binding, terminal.Definition.Node, out var value))
                    continue;

                ActivateTerminalFields(row, terminal.Binding, terminal.Fields);
                var policy = ApplyPolicy(value, row, terminal.Outputs);
                if (policy.SuppressRow)
                {
                    anyRowSuppressed = true;
                    continue;
                }

                var effectiveRow = policy.BindingValueChanged
                    ? WithEffectiveBindingValue(row, terminal.Binding, policy)
                    : row;
                ObservationValue? identity = null;
                if (terminal.KeySite is { } keySite)
                {
                    if (TryEvaluate(keySite, effectiveRow, out var key))
                    {
                        if (IsConcreteScalar(key))
                        {
                            identity = key;
                        }
                        else
                        {
                            executionDiagnostics.Add(new(
                                RelationRuntimeDiagnosticCodes.ExecutionOutputIdentityInvalid,
                                DiagnosticSeverity.Error,
                                $"Relation '{terminal.Relation.Value}' produced a missing, null, or non-scalar output key.",
                                request.Evidence.Evaluation,
                                occurrence: row.Root?.Id,
                                node: terminal.Definition.Node,
                                semanticSite: keySite.Analysis.Site.Id.Value));
                        }
                    }
                }
                else if (row.TryGetBinding(terminal.Binding, out var outputBinding)
                         && outputBinding.ObservationIdentity is { } observationIdentity)
                {
                    identity = ObservationValue.FromString(observationIdentity);
                }

                EvaluateInvariants(terminal, effectiveRow);
                var demandedValue = SelectDemandedValue(policy.Value, terminal.Fields);
                ValidateOutputValue(
                    terminal.Definition.Shape,
                    demandedValue,
                    terminal.Fields,
                    terminal.Outputs,
                    row,
                    terminal.Definition.Node);

                outputRows.Add(RelationQueryOutputRow.FromPrevalidatedExecution(
                    terminal.Definition.Shape,
                    demandedValue,
                    identity,
                    terminal.Definition.Mode == RelationOutputMode.Set ? null : row.Root,
                    row.Provenance,
                    policy.UnresolvedGaps));
            }

            ValidateRelationIdentities(terminal, outputRows);
            ValidateRelationCardinality(terminal, outputRows);
            var state = GetTerminalState(terminal.Outputs, outputRows.Count, anyRowSuppressed);
            return new(
                terminal.Relation,
                terminal.Definition.Shape,
                terminal.Definition.Mode,
                state,
                [.. outputRows]);
        }

        ImmutableArray<RelationQueryNamedResult> MaterializeQueryResults(
            ImmutableArray<RelationQueryResultExecutionBranch> terminals)
        {
            var materialized = ImmutableArray.CreateBuilder<RelationQueryNamedResult>(terminals.Length);
            foreach (var terminal in terminals)
            {
                List<RelationQueryOutputRow> outputRows = [];
                var anyRowSuppressed = false;
                foreach (var row in results[terminal.Definition.Input])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetOutputObject(row, terminal.Binding, terminal.Definition.Input, out var value))
                        continue;

                    ActivateTerminalFields(row, terminal.Binding, terminal.Fields);
                    var policy = ApplyPolicy(value, row, terminal.Outputs);
                    if (policy.SuppressRow)
                    {
                        anyRowSuppressed = true;
                        continue;
                    }

                    var demandedValue = SelectDemandedValue(policy.Value, terminal.Fields);
                    ValidateOutputValue(
                        terminal.Shape,
                        demandedValue,
                        terminal.Fields,
                        terminal.Outputs,
                        row,
                        terminal.Definition.Input);

                    ObservationValue? identity = null;
                    if (row.TryGetBinding(terminal.Binding, out var binding)
                        && binding.ObservationIdentity is { } observationIdentity)
                    {
                        identity = ObservationValue.FromString(observationIdentity);
                    }
                    outputRows.Add(RelationQueryOutputRow.FromPrevalidatedExecution(
                        terminal.Shape,
                        demandedValue,
                        identity,
                        root: null,
                        row.Provenance,
                        policy.UnresolvedGaps));
                }

                materialized.Add(new(
                    terminal.Id,
                    terminal.Definition is AggregationQueryResultDefinition
                        ? RelationQueryExecutionResultKind.Aggregation
                        : RelationQueryExecutionResultKind.Rows,
                    terminal.Shape,
                    GetTerminalState(terminal.Outputs, outputRows.Count, anyRowSuppressed),
                    [.. outputRows]));
            }

            return materialized.ToImmutable();
        }

        void EvaluateInvariants(
            RelationQueryRelationExecutionOutput terminal,
            RelationQueryRuntimeRow row)
        {
            foreach (var invariant in terminal.Invariants)
            {
                if (!TryEvaluate(invariant.PredicateSite, row, out var result))
                    continue;
                if (result.Kind == ObservationValueKind.Bool && result.Bool)
                    continue;

                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionInvariantViolation,
                    DiagnosticSeverity.Error,
                    $"Relation invariant '{invariant.Definition.Name}' was not satisfied.",
                    request.Evidence.Evaluation,
                    occurrence: row.Root?.Id,
                    node: terminal.Definition.Node,
                    semanticSite: invariant.Definition.Name));
            }
        }

        PolicyApplication ApplyPolicy(
            ObservationValue value,
            RelationQueryRuntimeRow row,
            ImmutableArray<RelationQueryOutputReference> outputs)
        {
            if (activeGaps.Count == 0)
            {
                return new(
                    value,
                    SuppressRow: false,
                    UnresolvedGaps: [],
                    AuthoritativeFields: [],
                    IsAuthoritativeValue: false,
                    BindingValueChanged: false);
            }

            var outputIds = outputs.Select(static output => output.Id).ToHashSet();
            var applicable = gapAnalysis.Decisions
                .Where(decision => activeGaps.Contains(decision.Gap)
                    && outputIds.Contains(decision.Impact.Output.Id)
                    && gapsById.TryGetValue(decision.Gap, out var gap)
                    && GapAppliesToRow(gap, row))
                .GroupBy(static decision => decision.Impact.Output.Id)
                .OrderBy(static group => group.Key.Value, StringComparer.Ordinal);
            var result = value;
            var suppressRow = false;
            HashSet<RelationRequirementGapId> unresolved = [];
            HashSet<FieldPath> authoritativeFields = [];
            var isAuthoritativeValue = false;
            var bindingValueChanged = false;
            foreach (var group in applicable)
            {
                var decisions = group.ToArray();
                var output = decisions[0].Impact.Output;
                var unresolvedDecisions = decisions.Where(static decision =>
                    decision.Disposition.Kind == RelationRequirementGapDispositionKind.Unresolved).ToArray();
                if (unresolvedDecisions.Length != 0)
                {
                    foreach (var decision in unresolvedDecisions)
                        unresolved.Add(decision.Gap);
                    continue;
                }

                var hasSuppression = decisions.Any(static decision =>
                    decision.Disposition.Kind == RelationRequirementGapDispositionKind.SuppressOutput);
                var hasSubstitution = decisions.Any(static decision =>
                    decision.Disposition.Kind is RelationRequirementGapDispositionKind.SubstituteNull
                        or RelationRequirementGapDispositionKind.SubstituteDefault);
                if (hasSuppression && hasSubstitution)
                {
                    foreach (var decision in decisions)
                        unresolved.Add(decision.Gap);
                    executionDiagnostics.Add(new(
                        RelationRuntimeDiagnosticCodes.ExecutionPolicyConflict,
                        DiagnosticSeverity.Error,
                        $"Requirement-gap policy selected suppression and substitution for output '{output.Id.Value}'.",
                        request.Evidence.Evaluation,
                        output: output));
                    continue;
                }

                if (hasSuppression)
                {
                    if (output.Field is null)
                        suppressRow = true;
                    else
                    {
                        result = RelationQueryObjectValues.Remove(result, output.Field.Value.Path);
                        bindingValueChanged = true;
                    }
                    continue;
                }

                var substitutions = decisions
                    .Where(static decision => decision.Disposition.Kind is
                        RelationRequirementGapDispositionKind.SubstituteNull
                        or RelationRequirementGapDispositionKind.SubstituteDefault)
                    .Select(static decision => (
                        decision.Gap,
                        Value: decision.Disposition.Kind == RelationRequirementGapDispositionKind.SubstituteNull
                            ? ObservationValue.Null
                            : decision.Disposition.Substitution!.Value))
                    .ToArray();
                if (substitutions.Length == 0)
                    continue;

                var selected = substitutions[0].Value;
                if (substitutions.Skip(1).Any(candidate =>
                        !RelationQueryValueSemantics.Equals(selected, candidate.Value)))
                {
                    foreach (var substitution in substitutions)
                        unresolved.Add(substitution.Gap);
                    executionDiagnostics.Add(new(
                        RelationRuntimeDiagnosticCodes.ExecutionPolicyConflict,
                        DiagnosticSeverity.Error,
                        $"Requirement-gap policy selected conflicting substitutions for output '{output.Id.Value}'.",
                        request.Evidence.Evaluation,
                        output: output));
                    continue;
                }

                if (output.Field is { } field)
                {
                    result = RelationQueryObjectValues.Set(result, field.Path, selected);
                    authoritativeFields.Add(field.Path);
                    bindingValueChanged = true;
                }
                else if (selected.Kind == ObservationValueKind.Object)
                {
                    result = selected;
                    isAuthoritativeValue = true;
                    bindingValueChanged = true;
                }
                else
                {
                    foreach (var substitution in substitutions)
                        unresolved.Add(substitution.Gap);
                    executionDiagnostics.Add(new(
                        RelationRuntimeDiagnosticCodes.ExecutionPolicyConflict,
                        DiagnosticSeverity.Error,
                        $"A row-level output substitution for '{output.Id.Value}' must be object-shaped.",
                        request.Evidence.Evaluation,
                        output: output));
                }
            }

            return new(
                result,
                suppressRow,
                [.. unresolved.OrderBy(static gap => gap.Value, StringComparer.Ordinal)],
                [.. authoritativeFields.OrderBy(static path => path.ToString(), StringComparer.Ordinal)],
                isAuthoritativeValue,
                bindingValueChanged);
        }

        RelationQueryExecutionOutputState GetTerminalState(
            ImmutableArray<RelationQueryOutputReference> outputs,
            int outputCount,
            bool anyRowSuppressed)
        {
            var outputIds = outputs.Select(static output => output.Id).ToHashSet();
            var relevant = gapAnalysis.Decisions
                .Where(decision => activeGaps.Contains(decision.Gap)
                    && outputIds.Contains(decision.Impact.Output.Id))
                .ToArray();
            if (!IsTerminalEvidenceConclusive(outputIds)
                || outputs.Any(output => incompleteNodes.Contains(output.Node))
                || HasInconclusiveSiteForOutputs(outputIds)
                || outputIds.Overlaps(inconclusiveOutputs)
                || relevant.Any(static decision =>
                    decision.Disposition.Kind == RelationRequirementGapDispositionKind.Unresolved))
            {
                return RelationQueryExecutionOutputState.Incomplete;
            }

            var hasRowSuppression = relevant.Any(static decision =>
                decision.Impact.Output.Field is null
                && decision.Disposition.Kind == RelationRequirementGapDispositionKind.SuppressOutput);
            return outputCount == 0 && (anyRowSuppressed || hasRowSuppression)
                ? RelationQueryExecutionOutputState.Suppressed
                : RelationQueryExecutionOutputState.Complete;
        }

        bool IsTerminalEvidenceConclusive(IReadOnlySet<RelationQueryOutputId> outputs)
        {
            if (request.Evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                return false;

            var partialSourceInputs = request.Evidence.Sources
                .Where(static source =>
                    source.State == RelationQuerySourceEvidenceState.Provided
                    && source.Completeness == RelationQueryEvidenceCompleteness.Partial)
                .Select(static source => source.Input)
                .ToHashSet();
            var partialTraversalInputs = request.Evidence.Traversals
                .Where(static traversal =>
                    traversal.State == RelationQueryTraversalEvidenceState.Completed
                    && traversal.Completeness == RelationQueryEvidenceCompleteness.Partial)
                .Select(static traversal => traversal.Input)
                .ToHashSet();
            return !request.Plan.RequirementGraph.Edges.Any(edge =>
                (partialSourceInputs.Contains(edge.Input.Id)
                    || partialTraversalInputs.Contains(edge.Input.Id))
                && outputs.Contains(edge.Output.Id));
        }

        bool HasInconclusiveSiteForOutputs(IReadOnlySet<RelationQueryOutputId> outputs) =>
            request.Plan.RequirementGraph.Edges.Any(edge =>
                outputs.Contains(edge.Output.Id)
                && edge.Traces.SelectMany(static trace => trace.Steps)
                    .Any(step => step.ExpressionSite is { } site
                        && inconclusiveExpressionSites.Contains(site.Value)));

        bool TryGetOutputObject(
            RelationQueryRuntimeRow row,
            ValueBindingId binding,
            QueryNodeId node,
            out ObservationValue value)
        {
            if (!row.TryGetBinding(binding, out var result))
            {
                throw Failure(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid,
                    $"Output node '{node.Value}' did not provide binding '{binding.Value}'.",
                    node,
                    row.Root?.Id);
            }
            if (result.Kind == RelationQueryRuntimeBindingKind.Absent)
            {
                value = default;
                return false;
            }
            if (result.Value.Kind != ObservationValueKind.Object)
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid,
                    DiagnosticSeverity.Error,
                    $"Output binding '{binding.Value}' did not produce an object-shaped value.",
                    request.Evidence.Evaluation,
                    occurrence: row.Root?.Id,
                    node: node));
                value = default;
                return false;
            }

            value = result.Value;
            return true;
        }

        static RelationQueryRuntimeRow WithEffectiveBindingValue(
            RelationQueryRuntimeRow row,
            ValueBindingId binding,
            PolicyApplication policy)
        {
            if (!row.TryGetBinding(binding, out var current))
                throw new InvalidOperationException($"Runtime row does not contain binding '{binding.Value}'.");

            var effective = current.Occurrence is { } occurrence
                ? RelationQueryRuntimeBinding.FromObservation(
                    occurrence,
                    policy.Value,
                    [.. current.AuthoritativeFields, .. policy.AuthoritativeFields],
                    current.IsAuthoritativeValue || policy.IsAuthoritativeValue)
                : RelationQueryRuntimeBinding.FromComputed(
                    current.Shape,
                    policy.Value,
                    current.UnavailableFields,
                    [.. current.AuthoritativeFields, .. policy.AuthoritativeFields],
                    current.IsAuthoritativeValue || policy.IsAuthoritativeValue);
            return row.WithBinding(binding, effective);
        }

        void ValidateOutputValue(
            QualifiedShapeId shape,
            ObservationValue value,
            ImmutableArray<RelationQueryFieldReference> fields,
            ImmutableArray<RelationQueryOutputReference> outputs,
            RelationQueryRuntimeRow row,
            QueryNodeId node)
        {
            foreach (var field in fields)
            {
                if (!shapeResolver.TryGetFieldContract(shape, field.Path, out var contract))
                {
                    throw Failure(
                        RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid,
                        $"Output field '{field}' cannot be resolved against its compiled shape snapshot.",
                        node,
                        row.Root?.Id);
                }

                var present = RelationQueryObjectValues.TryGet(value, field.Path, out var observed);
                var valid = present
                    ? contract.IsSatisfiedByConstant(observed)
                    : contract.Presence == FieldPresence.Optional;
                if (valid || HasDispositionedFieldGap(field, outputs, row))
                    continue;

                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputShapeInvalid,
                    DiagnosticSeverity.Error,
                    present
                        ? $"Output field '{field}' does not satisfy its compiled value contract."
                        : $"Required output field '{field}' was not produced.",
                    request.Evidence.Evaluation,
                    occurrence: row.Root?.Id,
                    node: node,
                    semanticSite: field.Path.ToString()));
            }
        }

        bool HasDispositionedFieldGap(
            RelationQueryFieldReference field,
            ImmutableArray<RelationQueryOutputReference> outputs,
            RelationQueryRuntimeRow row)
        {
            var outputIds = outputs
                .Where(output => output.Field == field)
                .Select(static output => output.Id)
                .ToHashSet();
            if (outputIds.Count == 0)
                return false;

            return gapAnalysis.Decisions.Any(decision =>
                activeGaps.Contains(decision.Gap)
                && outputIds.Contains(decision.Impact.Output.Id)
                && gapsById.TryGetValue(decision.Gap, out var gap)
                && GapAppliesToRow(gap, row)
                && decision.Disposition.Kind is
                    RelationRequirementGapDispositionKind.Unresolved
                    or RelationRequirementGapDispositionKind.SuppressOutput);
        }

        static ObservationValue SelectDemandedValue(
            ObservationValue value,
            ImmutableArray<RelationQueryFieldReference> fields) =>
            fields.IsDefaultOrEmpty
                ? value
                : RelationQueryObjectValues.SelectCanonical(value, fields);

        void ActivateTerminalFields(
            RelationQueryRuntimeRow row,
            ValueBindingId binding,
            ImmutableArray<RelationQueryFieldReference> fields)
        {
            foreach (var field in fields)
                _ = IsFieldAvailable(row, binding, field.Path);
        }

        void ValidateRelationIdentities(
            RelationQueryRelationExecutionOutput terminal,
            IReadOnlyList<RelationQueryOutputRow> rows)
        {
            if (terminal.KeySite is null)
                return;

            Dictionary<ObservationValue, RelationQueryOutputRow> identities =
                new(RelationQueryValueSemantics.EqualityComparer);
            foreach (var row in rows)
            {
                if (row.Identity is not { } identity)
                    continue;
                if (identities.TryAdd(identity, row))
                    continue;

                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputIdentityInvalid,
                    DiagnosticSeverity.Error,
                    $"Relation '{terminal.Relation.Value}' produced a duplicate output key.",
                    request.Evidence.Evaluation,
                    occurrence: row.Root?.Id,
                    node: terminal.Definition.Node,
                    semanticSite: terminal.KeySite.Analysis.Site.Id.Value));
            }
        }

        void ValidateRelationCardinality(
            RelationQueryRelationExecutionOutput terminal,
            IReadOnlyList<RelationQueryOutputRow> rows)
        {
            if (terminal.Definition.Mode == RelationOutputMode.Set)
                return;

            foreach (var row in rows.Where(static row => row.Root is null))
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation,
                    DiagnosticSeverity.Error,
                    $"Relation '{terminal.Relation.Value}' produced an unrooted row for rooted output mode '{terminal.Definition.Mode}'.",
                    request.Evidence.Evaluation,
                    node: terminal.Definition.Node));
            }

            if (terminal.Definition.Mode == RelationOutputMode.ManyPerRoot)
                return;

            var rowsByRoot = rows
                .Where(static row => row.Root is not null)
                .GroupBy(static row => row.Root!.Id)
                .ToDictionary(static group => group.Key, static group => group.Count());
            foreach (var root in RelationRoots())
            {
                var count = rowsByRoot.GetValueOrDefault(root.Id);
                var exceedsUpperBound = count > 1;
                var missesRequiredOutput = terminal.Definition.Mode == RelationOutputMode.OnePerRoot
                    && count == 0;
                if (!exceedsUpperBound
                    && (!missesRequiredOutput
                        || !gapAnalysis.IsConclusive
                        || !IsNodeCompleteForPartition(terminal.Definition.Node, root.Id)
                        || HasGapForRoot(root, terminal.Outputs)
                        || HasRowSuppressionForRoot(root, terminal.Outputs)))
                {
                    continue;
                }

                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionOutputCardinalityViolation,
                    DiagnosticSeverity.Error,
                    $"Relation '{terminal.Relation.Value}' violated '{terminal.Definition.Mode}' for one root occurrence.",
                    request.Evidence.Evaluation,
                    occurrence: root.Id,
                    node: terminal.Definition.Node));
            }
        }

        ImmutableArray<RelationQueryRuntimeRow> InputRows(
            RelationQueryExecutionNode execution,
            int inputIndex) =>
            results[execution.LogicalPlan.EffectiveInputs[inputIndex]];

        static RelationQueryExpressionSiteAnalysis SingleSite(
            RelationQueryExecutionNode execution,
            RelationQueryExpressionSiteKind kind) =>
            execution.ExpressionSites.Single(site => site.Kind == kind);

        bool TryEvaluate(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row,
            out ObservationValue value)
        {
            try
            {
                value = evaluator.Evaluate(
                    site.Analysis.Site.Expression,
                    row.CreateExpressionContext(
                        site.Analysis.Site.Scope.ImplicitBinding,
                        parameters,
                        isFieldAvailable: (binding, path) => IsFieldAvailable(row, binding, path),
                        isParameterAvailable: IsParameterAvailable,
                        isCapabilityAvailable: IsCapabilityAvailable));
                return true;
            }
            catch (RelationQueryExpressionEvaluationException exception)
                when (exception.Error == RelationQueryExpressionEvaluationError.RuntimeInputUnavailable)
            {
                var unavailableGaps = GapsAtSite(row, site)
                    .Where(gap => activeGaps.Contains(gap.Id))
                    .ToImmutableArray();
                if (unavailableGaps.IsDefaultOrEmpty
                    || !CanDispositionWithoutEvaluation(site, unavailableGaps))
                {
                    inconclusiveExpressionSites.Add(site.Analysis.Site.Id.Value);
                    if (site.Node is { } node)
                        MarkNodeIncomplete(node, row);
                }
                value = default;
                return false;
            }
            catch (RelationQueryExpressionEvaluationException exception)
            {
                throw new RelationQueryExecutionException(
                    RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                    exception.Message,
                    site.Node,
                    site.Analysis.Site.Id.Value,
                    row.Root?.Id,
                    exception);
            }
        }

        bool IsFieldAvailable(
            RelationQueryRuntimeRow row,
            ValueBindingId binding,
            FieldPath path)
        {
            if (!row.TryGetBinding(binding, out var runtimeBinding)
                || runtimeBinding.Kind == RelationQueryRuntimeBindingKind.Absent)
            {
                return true;
            }
            if (runtimeBinding.IsAuthoritativeValue
                || runtimeBinding.AuthoritativeFields.Any(authoritative =>
                    authoritative.IsPrefixOf(path)))
            {
                return true;
            }

            if (runtimeBinding.Kind == RelationQueryRuntimeBindingKind.Computed)
            {
                return !runtimeBinding.UnavailableFields.Any(unavailable =>
                    unavailable.Overlaps(path));
            }

            if (runtimeBinding.Occurrence is not { } occurrence
                || runtimeBinding.Shape is not { } shape
                || !fieldInputs.TryGetValue((binding, shape, path), out var input))
            {
                return false;
            }

            if (MarkInputGaps(input.Id, occurrence.Id))
                return false;

            return evidence.ResolveValidatedField(input.Id, occurrence.Id).State is
                RelationQueryMaterializedValueState.Value
                or RelationQueryMaterializedValueState.Null
                or RelationQueryMaterializedValueState.Missing
                or RelationQueryMaterializedValueState.Defaulted;
        }

        bool IsParameterAvailable(string parameter)
        {
            if (!parameterInputs.TryGetValue(parameter, out var input))
                return false;
            if (MarkInputGaps(input.Id, occurrence: null))
                return false;
            try
            {
                var resolved = evidence.ResolveEffectiveParameter(parameter);
                return resolved.State is
                    RelationQueryMaterializedValueState.Value
                    or RelationQueryMaterializedValueState.Null
                    or RelationQueryMaterializedValueState.Missing
                    or RelationQueryMaterializedValueState.Defaulted
                    || input.Definition.Presence == FieldPresence.Optional
                        && (resolved.State == RelationQueryMaterializedValueState.NotProvided
                            || resolved.State == RelationQueryMaterializedValueState.Omitted
                                && evidence.Completeness == RelationQueryEvidenceCompleteness.Complete);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        bool IsCapabilityAvailable(ExprCapabilityId capability)
        {
            if (!capabilityInputs.TryGetValue(capability, out var input))
                return false;
            if (MarkInputGaps(input.Id, occurrence: null))
                return false;
            return capabilityEvidence.TryGetValue(input.Id, out var observed)
                && observed.State == RelationQueryCapabilityEvidenceState.Available;
        }

        bool RequireBoolean(
            ObservationValue value,
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row)
        {
            if (value.Kind == ObservationValueKind.Bool)
                return value.Bool;

            throw ExpressionFailure(
                site,
                row,
                $"Expression site '{site.Analysis.Site.Id.Value}' requires a Boolean result, but received '{value.Kind}'.");
        }

        RelationQueryRuntimeRow CreateAbsentSide(QueryNodeId input)
        {
            var row = RelationQueryRuntimeRow.Empty;
            foreach (var binding in nodes[input].OutputBindings)
            {
                row = row.WithBinding(
                    binding.Binding,
                    RelationQueryRuntimeBinding.CreateAbsent(binding.Shape));
            }
            return row;
        }

        static QualifiedShapeId? ResolveBindingShape(
            RelationQueryExecutionNode execution,
            ValueBindingId binding) =>
            execution.OutputBindings.Single(candidate => candidate.Binding == binding).Shape;

        void AddRootPartition(
            ICollection<ObservationValue> parts,
            RelationQueryRuntimeRow row)
        {
            if (!rootPartitioned)
                return;
            parts.Add(ObservationValue.FromString("$relationRoot"));
            parts.Add(row.Root is null
                ? ObservationValue.Undefined
                : ObservationValue.FromString(row.Root.Id.Value));
        }

        ImmutableArray<RelationQueryRuntimeRow> ApplyPerRootPartition(
            ImmutableArray<RelationQueryRuntimeRow> rows,
            Func<ImmutableArray<RelationQueryRuntimeRow>, ImmutableArray<RelationQueryRuntimeRow>> transform)
        {
            if (!rootPartitioned)
                return transform(rows);

            List<string> order = [];
            Dictionary<string, ImmutableArray<RelationQueryRuntimeRow>.Builder> partitions =
                new(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = row.Root?.Id.Value ?? string.Empty;
                if (!partitions.TryGetValue(key, out var partition))
                {
                    partition = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
                    partitions.Add(key, partition);
                    order.Add(key);
                }
                partition.Add(row);
            }

            var result = ImmutableArray.CreateBuilder<RelationQueryRuntimeRow>();
            foreach (var key in order)
                result.AddRange(transform(partitions[key].ToImmutable()));
            return result.ToImmutable();
        }

        int CompareOrderEntries(
            OrderEntry left,
            OrderEntry right,
            RelationQueryExecutionNode execution,
            OrderQueryNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < node.Orderings.Length; index++)
            {
                var ordering = node.Orderings[index];
                int compared;
                try
                {
                    compared = RelationQueryValueSemantics.CompareForOrdering(
                        left.Keys[index],
                        right.Keys[index],
                        ordering.Direction,
                        ordering.NullPlacement);
                }
                catch (RelationQueryExpressionEvaluationException exception)
                {
                    var site = execution.OrderKeys[index];
                    throw new RelationQueryExecutionException(
                        RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                        exception.Message,
                        node.Id,
                        site.Analysis.Site.Id.Value,
                        left.Row.Root?.Id,
                        exception);
                }
                if (compared != 0)
                    return compared;
            }

            return left.Ordinal.CompareTo(right.Ordinal);
        }

        int CompareRowToBoundary(
            RelationQueryRuntimeRow row,
            RelationQueryExecutionNode orderExecution,
            OrderQueryNode order,
            IReadOnlyList<ObservationValue> boundaries)
        {
            for (var index = 0; index < order.Orderings.Length; index++)
            {
                if (!TryEvaluate(orderExecution.OrderKeys[index], row, out var key))
                    return -1;
                int compared;
                try
                {
                    compared = RelationQueryValueSemantics.CompareForOrdering(
                        key,
                        boundaries[index],
                        order.Orderings[index].Direction,
                        order.Orderings[index].NullPlacement);
                }
                catch (RelationQueryExpressionEvaluationException exception)
                {
                    var site = orderExecution.OrderKeys[index];
                    throw new RelationQueryExecutionException(
                        RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                        exception.Message,
                        order.Id,
                        site.Analysis.Site.Id.Value,
                        row.Root?.Id,
                        exception);
                }
                if (compared != 0)
                    return compared;
            }
            return 0;
        }

        RelationQueryRuntimeRow MergeAggregateProvenance(
            IReadOnlyList<RelationQueryRuntimeRow> rows)
        {
            var merged = rows[0];
            for (var index = 1; index < rows.Count; index++)
                merged = merged.WithAdditionalProvenance(rows[index].Provenance);
            return merged;
        }

        ImmutableArray<RelationQueryObservationOccurrence> RelationRoots()
        {
            var roots = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQuerySourceSetInput>()
                .Where(static input => input.Role == RelationQuerySourceInputRole.RelationRoot)
                .SelectMany(input => evidence.TryGetSource(input, out var source)
                    && source.State == RelationQuerySourceEvidenceState.Provided
                        ? source.Occurrences
                        : [])
                .GroupBy(static occurrence => occurrence.Id)
                .Select(static group => group.First())
                .OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal);
            return [.. roots];
        }

        bool HasGapAtSite(
            RelationQueryRuntimeRow row,
            RelationQueryExpressionSiteAnalysis site) =>
            GapsAtSite(row, site).Any(gap => activeGaps.Contains(gap.Id));

        ImmutableArray<RelationRequirementGap> GapsAtSite(
            RelationQueryRuntimeRow row,
            RelationQueryExpressionSiteAnalysis site) =>
        [
            .. gapAnalysis.Gaps.Where(gap =>
                GapAppliesToRow(gap, row)
                && gap.Impacts.SelectMany(static impact => impact.Traces)
                    .SelectMany(static trace => trace.Steps)
                    .Any(step => step.ExpressionSite == site.Analysis.Site.Id))
        ];

        bool CanDispositionWithoutEvaluation(
            RelationQueryExpressionSiteAnalysis site,
            ImmutableArray<RelationRequirementGap> gaps)
        {
            if (site.Kind is not (
                    RelationQueryExpressionSiteKind.ProjectionAssignmentValue
                    or RelationQueryExpressionSiteKind.AggregateAssignmentValue
                    or RelationQueryExpressionSiteKind.AggregateAssignmentFilter))
            {
                return false;
            }

            var gapIds = gaps.Select(static gap => gap.Id).ToHashSet();
            var decisions = gapAnalysis.Decisions
                .Where(decision => gapIds.Contains(decision.Gap)
                    && decision.Impact.Traces.SelectMany(static trace => trace.Steps)
                        .Any(step => step.ExpressionSite == site.Analysis.Site.Id))
                .ToArray();
            return decisions.Length != 0
                && decisions.All(static decision =>
                    decision.Disposition.Kind != RelationRequirementGapDispositionKind.Unresolved);
        }

        static bool GapAppliesToRow(
            RelationRequirementGap gap,
            RelationQueryRuntimeRow row) =>
            gap.Occurrence is null
            || row.Provenance.Any(occurrence => occurrence.Id == gap.Occurrence.Id);

        bool HasGapForRoot(
            RelationQueryObservationOccurrence root,
            ImmutableArray<RelationQueryOutputReference> outputs)
        {
            var outputIds = outputs.Select(static output => output.Id).ToHashSet();
            return gapAnalysis.Gaps.Any(gap =>
                activeGaps.Contains(gap.Id)
                && (gap.Occurrence is null || IsWithinRoot(gap.Occurrence.Id, root.Id))
                && gap.Impacts.Any(impact =>
                    outputIds.Contains(impact.Output.Id)
                    && impact.Effect is RelationQueryRequirementEffect.Membership
                        or RelationQueryRequirementEffect.Correlation
                        or RelationQueryRequirementEffect.Acquisition
                        or RelationQueryRequirementEffect.Cardinality));
        }

        bool HasRowSuppressionForRoot(
            RelationQueryObservationOccurrence root,
            ImmutableArray<RelationQueryOutputReference> outputs)
        {
            var outputIds = outputs.Select(static output => output.Id).ToHashSet();
            return gapAnalysis.Decisions.Any(decision =>
                activeGaps.Contains(decision.Gap)
                && outputIds.Contains(decision.Impact.Output.Id)
                && decision.Impact.Output.Field is null
                && decision.Disposition.Kind == RelationRequirementGapDispositionKind.SuppressOutput
                && gapsById.TryGetValue(decision.Gap, out var gap)
                && (gap.Occurrence is null || IsWithinRoot(gap.Occurrence.Id, root.Id)));
        }

        ImmutableArray<RelationRequirementGap> DirectGaps(
            RelationQueryInputId input,
            RelationQueryRuntimeRow? row) =>
            ApplicableGaps(directGapsByInput.GetValueOrDefault(input), row);

        ImmutableArray<RelationRequirementGap> BlockingGaps(
            RelationQueryInputId input,
            RelationQueryRuntimeRow row) =>
            ApplicableGaps(blockersByInput.GetValueOrDefault(input), row);

        static ImmutableArray<RelationRequirementGap> ApplicableGaps(
            ImmutableArray<RelationRequirementGap> gaps,
            RelationQueryRuntimeRow? row)
        {
            if (gaps.IsDefaultOrEmpty)
                return [];

            return row is null
                ? [.. gaps.Where(static gap => gap.Occurrence is null)]
                : [.. gaps.Where(gap => GapAppliesToRow(gap, row))];
        }

        void ActivateGaps(IEnumerable<RelationRequirementGap> gaps)
        {
            foreach (var gap in gaps)
                activeGaps.Add(gap.Id);
        }

        void RecordUnrealizableStructuralSubstitutions(
            RelationQueryInputId blockedInput,
            ImmutableArray<RelationRequirementGap> gaps)
        {
            foreach (var gap in gaps)
            {
                activeGaps.Add(gap.Id);
                var decisions = gapAnalysis.Decisions
                    .Where(decision => decision.Gap == gap.Id)
                    .ToArray();
                if (decisions.Any(static decision =>
                        decision.Impact.Output.Field is null
                        && decision.Disposition.Kind
                            == RelationRequirementGapDispositionKind.SuppressOutput))
                {
                    continue;
                }

                foreach (var decision in decisions.Where(static decision =>
                             decision.Disposition.Kind is
                                 RelationRequirementGapDispositionKind.SubstituteNull
                                 or RelationRequirementGapDispositionKind.SubstituteDefault))
                {
                    if (!unrealizablePolicyDecisions.Add((gap.Id, decision.Impact.Output.Id)))
                        continue;

                    executionDiagnostics.Add(new(
                        RelationRuntimeDiagnosticCodes.ExecutionPolicyDispositionUnrealizable,
                        DiagnosticSeverity.Error,
                        $"Requirement-gap substitution for output '{decision.Impact.Output.Id.Value}' cannot create a row because causal input '{gap.Input.Id.Value}' blocks structural input '{blockedInput.Value}'.",
                        request.Evidence.Evaluation,
                        input: gap.Input.Id,
                        occurrence: gap.Occurrence?.Id,
                        gap: gap.Id,
                        output: decision.Impact.Output));
                }
            }
        }

        bool MarkInputGaps(
            RelationQueryInputId input,
            RelationQueryOccurrenceId? occurrence)
        {
            if (!directGapsByInput.TryGetValue(input, out var inputGaps))
                return false;

            var found = false;
            foreach (var gap in inputGaps)
            {
                if (gap.Occurrence?.Id != occurrence)
                    continue;
                activeGaps.Add(gap.Id);
                found = true;
            }
            return found;
        }

        void RecordInconclusiveDiagnostics()
        {
            if (request.Evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive,
                    DiagnosticSeverity.Warning,
                    "The runtime evidence snapshot is partial; omitted records cannot be interpreted as semantic absence.",
                    request.Evidence.Evaluation));
            }

            foreach (var traversal in request.Evidence.Traversals.Where(static traversal =>
                         traversal.State == RelationQueryTraversalEvidenceState.Completed
                         && traversal.Completeness == RelationQueryEvidenceCompleteness.Partial))
            {
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive,
                    DiagnosticSeverity.Warning,
                    "A completed relationship traversal has partial results and cannot establish authoritative absence.",
                    request.Evidence.Evaluation,
                    input: traversal.Input,
                    occurrence: traversal.From,
                    evidenceReference: traversal.EvidenceReference));
            }

            var sitesById = request.Plan.ExecutionSlice.ExpressionSites.ToDictionary(
                static site => site.Analysis.Site.Id.Value,
                StringComparer.Ordinal);
            foreach (var siteId in inconclusiveExpressionSites.Order(StringComparer.Ordinal))
            {
                sitesById.TryGetValue(siteId, out var site);
                executionDiagnostics.Add(new(
                    RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive,
                    DiagnosticSeverity.Warning,
                    $"Expression site '{siteId}' could not be evaluated conclusively from the supplied evidence.",
                    request.Evidence.Evaluation,
                    node: site?.Node,
                    semanticSite: siteId));
            }
        }

        RelationRequirementGapAnalysisResult CreateEffectiveGapAnalysis() =>
            new(
                gapAnalysis.IsEvidenceValid,
                gapAnalysis.IsConclusive,
                [.. gapAnalysis.Gaps.Where(gap => activeGaps.Contains(gap.Id))],
                [.. gapAnalysis.Decisions.Where(decision => activeGaps.Contains(decision.Gap))],
                [.. gapAnalysis.Diagnostics.Where(diagnostic =>
                    diagnostic.Gap is null || activeGaps.Contains(diagnostic.Gap.Value))]);

        bool IsWithinRoot(
            RelationQueryOccurrenceId occurrence,
            RelationQueryOccurrenceId root)
        {
            if (occurrence == root)
                return true;

            HashSet<RelationQueryOccurrenceId> visited = [occurrence];
            Queue<RelationQueryOccurrenceId> pending = new();
            pending.Enqueue(occurrence);
            while (pending.TryDequeue(out var current))
            {
                if (!occurrenceParents.TryGetValue(current, out var parents))
                    continue;
                foreach (var parent in parents)
                {
                    if (parent == root)
                        return true;
                    if (visited.Add(parent))
                        pending.Enqueue(parent);
                }
            }

            return false;
        }

        static bool IsConcreteScalar(ObservationValue value) =>
            value.Kind is not (
                ObservationValueKind.Undefined
                or ObservationValueKind.Null
                or ObservationValueKind.Object
                or ObservationValueKind.Array);

        RelationQueryExecutionException ExpressionFailure(
            RelationQueryExpressionSiteAnalysis site,
            RelationQueryRuntimeRow row,
            string message) =>
            new(
                RelationRuntimeDiagnosticCodes.ExecutionExpressionFailure,
                message,
                site.Node,
                site.Analysis.Site.Id.Value,
                row.Root?.Id);

        static RelationQueryExecutionException Failure(
            string code,
            string message,
            QueryNodeId? node = null,
            RelationQueryOccurrenceId? occurrence = null) =>
            new(code, message, node, semanticSite: null, occurrence);

        internal static ImmutableArray<RelationRuntimeDiagnostic> NormalizeDiagnostics(
            IEnumerable<RelationRuntimeDiagnostic> diagnostics) =>
        [
            .. diagnostics
                .Distinct()
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

        sealed class AggregateGroup(
            ObservationValue[] groupingValues,
            RelationQueryObservationOccurrence? root)
        {
            public ObservationValue[] GroupingValues { get; } = groupingValues;

            public RelationQueryObservationOccurrence? Root { get; } = root;

            public List<RelationQueryRuntimeRow> Rows { get; } = [];
        }

        readonly record struct OrderEntry(
            RelationQueryRuntimeRow Row,
            ObservationValue[] Keys,
            int Ordinal);

        readonly record struct PolicyApplication(
            ObservationValue Value,
            bool SuppressRow,
            ImmutableArray<RelationRequirementGapId> UnresolvedGaps,
            ImmutableArray<FieldPath> AuthoritativeFields,
            bool IsAuthoritativeValue,
            bool BindingValueChanged);
    }

    sealed class RelationQueryValueVector : IEquatable<RelationQueryValueVector>
    {
        readonly ImmutableArray<ObservationValue> values;

        public RelationQueryValueVector(IEnumerable<ObservationValue> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            this.values = [.. values];
        }

        public bool Equals(RelationQueryValueVector? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null || values.Length != other.values.Length)
                return false;
            for (var index = 0; index < values.Length; index++)
            {
                if (!RelationQueryValueSemantics.Equals(values[index], other.values[index]))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) =>
            obj is RelationQueryValueVector other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            foreach (var value in values)
                hash.Add(RelationQueryValueSemantics.GetHashCode(value));
            return hash.ToHashCode();
        }
    }

    sealed class RelationQueryExecutionException(
        string code,
        string message,
        QueryNodeId? node,
        string? semanticSite,
        RelationQueryOccurrenceId? occurrence,
        Exception? innerException = null
        ) : InvalidOperationException(message, innerException)
    {
        public string Code { get; } = code;

        public QueryNodeId? Node { get; } = node;

        public string? SemanticSite { get; } = semanticSite;

        public RelationQueryOccurrenceId? Occurrence { get; } = occurrence;
    }
}
