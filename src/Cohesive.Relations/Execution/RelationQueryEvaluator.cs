using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Execution;

/// <summary>Structured failure detected at the canonical host-evaluation boundary.</summary>
public sealed record RelationQueryEvaluationDiagnostic
{
    /// <summary>Creates an attributable host-evaluation diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Effective diagnostic severity.</param>
    /// <param name="message">Human-readable explanation without runtime payload values.</param>
    /// <param name="planComponents">Compiled-plan components whose affinity check failed.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A required string is empty, or <paramref name="planComponents"/> contains an empty component name.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    public RelationQueryEvaluationDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        ImmutableArray<string> planComponents = default
        )
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported evaluation diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        var normalizedComponents = planComponents.IsDefault ? [] : planComponents;
        if (normalizedComponents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Plan-component names cannot be empty.", nameof(planComponents));
        Severity = severity;
        PlanComponents =
        [
            .. normalizedComponents
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Effective diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation without runtime payload values.</summary>
    public string Message { get; }

    /// <summary>Mismatched compiled-plan component names in deterministic order.</summary>
    public ImmutableArray<string> PlanComponents { get; }
}

/// <summary>Stable machine-readable canonical evaluation diagnostic codes.</summary>
public static class RelationQueryEvaluationDiagnosticCodes
{
    /// <summary>An attached compiled-plan reference is stale or belongs to another semantic snapshot.</summary>
    public const string PlanReferenceMismatch = "REL2301";
}

/// <summary>Host boundary for compiling, realizing, planning, acquiring, and interpreting a relation or query.</summary>
public interface IRelationQueryEvaluator
{
    /// <summary>Evaluates one exact canonical relation or query request.</summary>
    /// <param name="evaluation">Definition snapshots, runtime inputs, demand, and attribution to evaluate.</param>
    /// <param name="cancellationToken">Token that cancels every evaluation phase and delegated source read.</param>
    /// <returns>
    /// The terminal outcome retaining each phase artifact that was produced. Compilation, realization, or planning
    /// failures are represented by their structured phase artifacts; contract violations and cancellation propagate.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A configured requirement-gap policy, source reader, or canonical interpreter rejects invalid input.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A configured policy or canonical interpretation exposes an unsupported enum value.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="InvalidOperationException">
    /// Placement is incompatible with supplied roots, or a compiler, reader, planner, or interpreter encounters an
    /// inconsistent semantic snapshot. Stale or foreign plan attribution is returned as a structured failed outcome.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// An evaluation or plan snapshot cannot be serialized for deterministic fingerprint verification.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An evaluation or plan snapshot contains an unsupported serialization type, or a delegated source reader or
    /// interpreter rejects an unsupported operation.
    /// </exception>
    ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(RelationQueryEvaluation evaluation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Terminal in-process outcome retaining the exact artifacts produced by canonical relation/query evaluation.
/// </summary>
/// <remarks>
/// This type deliberately does not project rows, gaps, diagnostics, evidence, or source traces into another model.
/// Consumers inspect the retained phase artifacts and <see cref="Result"/>. Like
/// <see cref="RelationQueryPhysicalExecutionResult"/>, it is not a persisted wire contract.
/// </remarks>
public sealed class RelationQueryEvaluationOutcome
{
    /// <summary>Creates and validates one terminal phase chain.</summary>
    /// <param name="evaluation">Exact evaluation request represented by the outcome.</param>
    /// <param name="compilation">Static compilation result, which is always present.</param>
    /// <param name="realization">Realization report when compilation succeeded.</param>
    /// <param name="placement">
    /// Attempted source placement when realization was successful, including a stale or foreign attempt rejected
    /// by attributable physical-planning diagnostics.
    /// </param>
    /// <param name="physicalPlanning">Physical-planning result when realization was successful.</param>
    /// <param name="physicalExecution">Physical execution result when planning produced an executable plan.</param>
    /// <param name="diagnostics">Structured failures detected before realization or physical planning.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evaluation"/> or <paramref name="compilation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The compilation does not belong to <paramref name="evaluation"/>; the phase chain is incomplete; a phase
    /// could not follow its predecessor; a phase cites a different compiled plan without an attributable rejection;
    /// physical execution does not correspond to the planned artifact; or diagnostics conflict with retained later
    /// phases.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A retained phase artifact cannot be fingerprinted because its semantic snapshot is inconsistent.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A retained semantic snapshot cannot be serialized for fingerprint verification.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A retained semantic snapshot contains a serialization type unsupported by fingerprint verification.
    /// </exception>
    public RelationQueryEvaluationOutcome(
        RelationQueryEvaluation evaluation,
        RelationQueryCompilationResult compilation,
        RelationQueryRealizationReport? realization = null,
        RelationQuerySourcePlacement? placement = null,
        RelationQueryPhysicalPlanningResult? physicalPlanning = null,
        RelationQueryPhysicalExecutionResult? physicalExecution = null,
        ImmutableArray<RelationQueryEvaluationDiagnostic> diagnostics = default)
    {
        Evaluation = Guard.RequireNotNull(evaluation);
        Compilation = Guard.RequireNotNull(compilation);
        if (!ReferenceEquals(Compilation.Request, Evaluation.Compilation))
        {
            throw new ArgumentException(
                "Compilation must be the exact result produced for the evaluation's immutable compilation request.",
                nameof(compilation));
        }
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Evaluation diagnostics cannot contain null entries.", nameof(diagnostics));
        Diagnostics =
        [
            .. normalizedDiagnostics
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];
        var hasEvaluationErrors = Diagnostics.Any(
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        if (!Compilation.IsSuccessful)
        {
            if (realization is not null || placement is not null || physicalPlanning is not null || physicalExecution is not null)
                throw new ArgumentException("Failed compilation cannot retain later evaluation phases.", nameof(realization));
        }
        else if (hasEvaluationErrors)
        {
            if (realization is not null || placement is not null || physicalPlanning is not null || physicalExecution is not null)
            {
                throw new ArgumentException(
                    "Failed evaluation preflight cannot retain realization or physical phases.",
                    nameof(diagnostics));
            }
        }
        else
        {
            var plan = Compilation.Plan!;
            if (realization is null)
                throw new ArgumentException("Successful compilation requires a realization report.", nameof(realization));
            var mismatches = realization.Plan.GetMismatchedComponents(plan);
            if (!mismatches.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    $"Realization report cites a different compiled plan ({string.Join(", ", mismatches)}).",
                    nameof(realization));
            }

            if (!realization.IsRealizable)
            {
                if (placement is not null || physicalPlanning is not null || physicalExecution is not null)
                    throw new ArgumentException("An unrealizable report cannot retain physical phases.", nameof(physicalPlanning));
            }
            else
            {
                if (placement is null)
                    throw new ArgumentException("A realizable report requires an attempted source placement.", nameof(placement));
                if (physicalPlanning is null)
                    throw new ArgumentException("A realizable report requires a physical-planning result.", nameof(physicalPlanning));
                var placementMismatches = placement.Plan.GetMismatchedComponents(plan);
                if (!placementMismatches.IsDefaultOrEmpty
                    && (physicalPlanning.IsSuccessful
                        || !physicalPlanning.Diagnostics.Any(static diagnostic =>
                            diagnostic.Severity == DiagnosticSeverity.Error
                            && diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch)))
                {
                    throw new ArgumentException(
                        $"Source placement cites a different compiled plan ({string.Join(", ", placementMismatches)}) "
                        + "without an attributable placement-mismatch planning diagnostic.",
                        nameof(placement));
                }
                if (physicalPlanning.IsSuccessful != (physicalExecution is not null))
                {
                    throw new ArgumentException(
                        "Physical execution must be present exactly when physical planning succeeds.",
                        nameof(physicalExecution));
                }
                if (physicalPlanning.Plan is { } physicalPlan)
                {
                    var physicalPlanMismatches = physicalPlan.Plan.GetMismatchedComponents(plan);
                    if (!physicalPlanMismatches.IsDefaultOrEmpty
                        || !Equals(physicalPlan.Realization, realization.Fingerprint))
                    {
                        throw new ArgumentException(
                            "Physical plan cites a different semantic plan or realization report.",
                            nameof(physicalPlanning));
                    }
                    if (!ReferenceEquals(physicalPlan.Placement, placement))
                    {
                        throw new ArgumentException(
                            "Physical planning must retain the exact attempted source-placement artifact.",
                            nameof(physicalPlanning));
                    }
                }
                if (physicalExecution is { } execution)
                {
                    if (!ReferenceEquals(execution.Request.PhysicalPlan, physicalPlanning.Plan)
                        || !ReferenceEquals(execution.Request.Plan, plan)
                        || execution.Request.Evaluation != evaluation.Evaluation)
                    {
                        throw new ArgumentException(
                            "Physical execution does not belong to the exact planned evaluation.",
                            nameof(physicalExecution));
                    }
                }
            }
        }

        Realization = realization;
        Placement = placement;
        PhysicalPlanning = physicalPlanning;
        PhysicalExecution = physicalExecution;
    }

    /// <summary>Exact evaluation request represented by this outcome.</summary>
    public RelationQueryEvaluation Evaluation { get; }

    /// <summary>Static compilation result, including structured validation diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }

    /// <summary>Structured diagnostics emitted by host-evaluation preflight.</summary>
    public ImmutableArray<RelationQueryEvaluationDiagnostic> Diagnostics { get; }

    /// <summary>Realization report, or <see langword="null"/> when compilation or evaluation preflight failed.</summary>
    public RelationQueryRealizationReport? Realization { get; }

    /// <summary>
    /// Attempted source placement, or <see langword="null"/> when realization did not succeed. A failed planning
    /// result may retain a stale or foreign attempt when its structured diagnostics explicitly identify the
    /// placement mismatch.
    /// </summary>
    public RelationQuerySourcePlacement? Placement { get; }

    /// <summary>
    /// Physical-planning result, or <see langword="null"/> when compilation or realization did not succeed.
    /// </summary>
    public RelationQueryPhysicalPlanningResult? PhysicalPlanning { get; }

    /// <summary>Physical execution result, or <see langword="null"/> when no executable plan was produced.</summary>
    public RelationQueryPhysicalExecutionResult? PhysicalExecution { get; }

    /// <summary>Canonical interpreted result, or <see langword="null"/> when interpretation did not run.</summary>
    public RelationQueryExecutionResult? Result => PhysicalExecution?.Interpretation;

    /// <summary>Terminal canonical status; failures before interpretation are reported as failed.</summary>
    public RelationQueryExecutionStatus Status =>
        PhysicalExecution?.Status ?? RelationQueryExecutionStatus.Failed;

    /// <summary>Whether every phase and canonical interpretation succeeded conclusively.</summary>
    public bool IsSuccessful => Status == RelationQueryExecutionStatus.Succeeded;
}

/// <summary>
/// Canonical reference evaluator over explicit physical placement, bounded source readers, and one configured
/// realization-and-interpretation contract.
/// </summary>
public sealed class RelationQueryEvaluator : IRelationQueryEvaluator
{
    const string CapabilityEvidenceReferencePrefix = "relation-query-realization-target";
    const string SuppliedOnlyConventionSetVersion = "cohesive.relations/supplied-only-conventions/v1";
    const string SuppliedOnlySourceKey = "cohesive.relations/supplied-only";

    readonly Func<CompiledRelationQueryPlan, RelationQuerySourcePlacement> createPlacement;
    readonly RelationQueryPhysicalPlanningPolicy physicalPlanningPolicy;
    readonly IRelationQueryInterpreter interpreter;
    readonly RelationQueryPhysicalExecutor physicalExecutor;
    readonly IRelationRequirementGapPolicy requirementGapPolicy;

    /// <summary>
    /// Creates a convention-configured evaluator for relations whose only input is a supplied root set.
    /// </summary>
    /// <remarks>
    /// This convenience path performs the full canonical compile, realize, plan, execute, and interpret pipeline,
    /// but it performs no external I/O. Compiled plans retaining traversals or acquired source sets require the
    /// explicit evaluator constructor with placement and source readers.
    /// </remarks>
    /// <param name="maximumRootRows">Maximum supplied root observations admitted to one evaluation.</param>
    /// <param name="requirementGapPolicy">
    /// Runtime requirement-gap policy, or <see langword="null"/> for the conventional policy.
    /// </param>
    /// <returns>An evaluator restricted to supplied-only relation plans.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumRootRows"/> is not positive or exceeds the portable integer range.
    /// </exception>
    public static RelationQueryEvaluator CreateSuppliedOnly(
        long maximumRootRows = 10_000,
        IRelationRequirementGapPolicy? requirementGapPolicy = null)
    {
        RelationQuerySourcePlacementLimits limits = new(
            maximumBatchSize: 1,
            maximumBufferedRows: maximumRootRows,
            maximumFanOut: 1,
            maximumConcurrency: 1);
        RelationQueryPhysicalPlanningPolicy policy = new(
            new($"cohesive.relations/supplied-only/max-{maximumRootRows.ToString(CultureInfo.InvariantCulture)}/v1"),
            SuppliedOnlyConventionSetVersion,
            maximumBatchSize: 1,
            maximumBufferedRows: maximumRootRows,
            maximumLocalRows: maximumRootRows,
            maximumFanOut: 1,
            maximumReferenceKeysPerObservation: 1,
            maximumConcurrency: 1);
        return new(
            plan => CreateSuppliedOnlyPlacement(plan, limits),
            policy,
            sourceReaders: [],
            requirementGapPolicy: requirementGapPolicy);
    }

    /// <summary>Creates a canonical evaluator over explicit placement policy and source readers.</summary>
    /// <param name="createPlacement">
    /// Resolves one exact plan-scoped source placement. The returned artifact must cite the supplied plan and use
    /// <see cref="RelationQuerySourceAcquisitionKind.Supplied"/> for a relation-root input.
    /// </param>
    /// <param name="physicalPlanningPolicy">Explicit bounded physical-planning policy.</param>
    /// <param name="sourceReaders">Physical source readers addressable by placements returned by <paramref name="createPlacement"/>.</param>
    /// <param name="interpreter">Canonical interpreter instance, or <see langword="null"/> for the shared default.</param>
    /// <param name="requirementGapPolicy">
    /// Runtime requirement-gap policy, or <see langword="null"/> for the conventional policy.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="createPlacement"/>, <paramref name="physicalPlanningPolicy"/>, or
    /// <paramref name="sourceReaders"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReaders"/> contains a null reader or repeats a source identity.
    /// </exception>
    public RelationQueryEvaluator(
        Func<CompiledRelationQueryPlan, RelationQuerySourcePlacement> createPlacement,
        RelationQueryPhysicalPlanningPolicy physicalPlanningPolicy,
        IEnumerable<IRelationQuerySourceReader> sourceReaders,
        IRelationQueryInterpreter? interpreter = null,
        IRelationRequirementGapPolicy? requirementGapPolicy = null
        )
    {
        this.createPlacement = Guard.RequireNotNull(createPlacement);
        this.physicalPlanningPolicy = Guard.RequireNotNull(physicalPlanningPolicy);
        this.interpreter = interpreter ?? RelationQueryInMemoryInterpreter.Default;
        physicalExecutor = new(sourceReaders, this.interpreter);
        this.requirementGapPolicy = requirementGapPolicy ?? RelationRequirementGapPolicy.Conventional;
    }

    static RelationQuerySourcePlacement CreateSuppliedOnlyPlacement(
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacementLimits limits)
    {
        var sourceContract = plan.InputContract.Sources.Length == 1
            ? plan.InputContract.Sources[0]
            : null;
        if (sourceContract is null
            || sourceContract.Role != RelationQuerySourceInputRole.RelationRoot
            || !plan.InputContract.Traversals.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "The supplied-only evaluator requires one relation-root source and no relationship traversals.");
        }

        var placement = RelationQueryPlacement.For(plan);
        var source = placement.Source(
            SuppliedOnlySourceKey,
            RelationQueryInMemoryInterpreter.DefaultTargetProfile,
            limits: limits);
        placement.PlaceSource(source).FieldsBySemanticPath();
        return placement.Build().RequireValue().Placement;
    }

    /// <inheritdoc />
    public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        return RelationQueryTelemetryRuntime.IsOperationEnabled
            ? EvaluateObservedAsync(evaluation, cancellationToken)
            : EvaluateCoreAsync(evaluation, cancellationToken);
    }

    async ValueTask<RelationQueryEvaluationOutcome> EvaluateObservedAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var activity = RelationQueryTelemetryRuntime.StartActivity(RelationQueryTelemetry.EvaluationActivityName);
        var started = RelationQueryTelemetryRuntime.StartTimer();
        Exception? failure = null;
        RelationQueryEvaluationOutcome? outcome = null;
        try
        {
            outcome = await EvaluateCoreAsync(evaluation, cancellationToken).ConfigureAwait(false);
            if (activity?.IsAllDataRequested == true)
            {
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.DefinitionFingerprintTagName,
                    evaluation.Compilation.DefinitionDocument.DefinitionFingerprint.Value);
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.EvaluationFingerprintTagName,
                    evaluation.Fingerprint.Value);
                activity.SetTag(
                    RelationQueryTelemetry.SchemaVersionTagName,
                    evaluation.Compilation.DefinitionDocument.SchemaVersion);
                activity.SetTag(
                    RelationQueryTelemetry.DiagnosticCountTagName,
                    outcome.Diagnostics.Length
                    + outcome.Compilation.Diagnostics.Length
                    + (outcome.Realization?.Diagnostics.Length ?? 0)
                    + (outcome.PhysicalPlanning?.Diagnostics.Length ?? 0)
                    + (outcome.PhysicalExecution?.Diagnostics.Length ?? 0)
                    + (outcome.Result?.Diagnostics.Length ?? 0));
                foreach (var diagnostic in outcome.Diagnostics)
                {
                    RelationQueryTelemetry.AddDiagnosticEvent(
                        activity,
                        diagnostic.Code,
                        diagnostic.Severity);
                }
                if (outcome.Realization is { } realization)
                {
                    RelationQueryTelemetry.TrySetFingerprintTag(
                        activity,
                        RelationQueryTelemetry.RealizationFingerprintTagName,
                        realization.Fingerprint.Value);
                }
                if (outcome.PhysicalExecution?.Request.PhysicalPlan is { } physicalPlan)
                {
                    RelationQueryTelemetry.TrySetFingerprintTag(
                        activity,
                        RelationQueryTelemetry.PhysicalPlanFingerprintTagName,
                        physicalPlan.Fingerprint.Value);
                }
            }
            return outcome;
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
                : failure is not null
                    ? RelationQueryTelemetry.ExceptionStatus
                    : outcome is null
                        ? RelationQueryTelemetry.ExceptionStatus
                        : RelationQueryTelemetry.GetStatusTagValue(outcome.Status);
            RelationQueryTelemetryRuntime.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.EvaluationActivityName,
                status,
                TerminalPhase(outcome, failure),
                failure);
        }
    }

    async ValueTask<RelationQueryEvaluationOutcome> EvaluateCoreAsync(
        RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken)
    {

        var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        if (!compilation.IsSuccessful)
            return new(evaluation, compilation);

        var plan = compilation.Plan!;
        if (evaluation.PlanReference is { } expectedPlan)
        {
            var mismatches = expectedPlan.GetMismatchedComponents(plan);
            if (!mismatches.IsDefaultOrEmpty)
            {
                return new(
                    evaluation,
                    compilation,
                    diagnostics:
                    [
                        new(RelationQueryEvaluationDiagnosticCodes.PlanReferenceMismatch,
                            DiagnosticSeverity.Error,
                            $"The evaluation's compiled-plan reference is stale or foreign ({string.Join(", ", mismatches)}).",
                            mismatches)
                    ]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var realization = interpreter.Realize(plan);
        if (!realization.IsRealizable)
            return new(evaluation, compilation, realization);

        cancellationToken.ThrowIfCancellationRequested();
        var placement = createPlacement(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var physicalPlanning = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            placement,
            physicalPlanningPolicy,
            interpreter
            );
        if (!physicalPlanning.IsSuccessful)
            return new(
                evaluation,
                compilation,
                realization,
                placement,
                physicalPlanning
                );

        var suppliedSources = CreateSuppliedSources(evaluation, plan, placement);
        var parameters = ProjectParameters(evaluation, plan);
        var capabilities = CreateCapabilityEvidence(plan, realization);
        RelationQueryPhysicalExecutionRequest request = new(
            plan,
            physicalPlanning.Plan!,
            realization,
            evaluation.Evaluation,
            suppliedSources,
            parameters,
            capabilities,
            requirementGapPolicy: requirementGapPolicy
            );
        var execution = await physicalExecutor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new(
            evaluation,
            compilation,
            realization,
            placement,
            physicalPlanning,
            execution
            );
    }

    static string TerminalPhase(RelationQueryEvaluationOutcome? outcome, Exception? failure)
    {
        if (failure is not null)
            return failure is OperationCanceledException
                ? RelationQueryTelemetry.CancellationTerminalPhase
                : RelationQueryTelemetry.ExceptionTerminalPhase;
        if (outcome is null || !outcome.Compilation.IsSuccessful)
            return RelationQueryTelemetry.StaticCompilationTerminalPhase;
        if (outcome.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return RelationQueryTelemetry.PlanAffinityTerminalPhase;
        if (outcome.Realization is not { IsRealizable: true })
            return RelationQueryTelemetry.RealizationTerminalPhase;
        if (outcome.PhysicalPlanning is not { IsSuccessful: true })
            return RelationQueryTelemetry.PhysicalPlanningTerminalPhase;
        return RelationQueryTelemetry.PhysicalExecutionTerminalPhase;
    }

    static ImmutableArray<RelationQueryParameterEvidence> ProjectParameters(
        RelationQueryEvaluation evaluation,
        CompiledRelationQueryPlan plan)
    {
        var byInput = evaluation.Parameters.ToDictionary(static parameter => parameter.Input);
        return
        [
            .. plan.InputContract.Parameters
                .Select(parameter => byInput[parameter.Input.Id])
                .OrderBy(static parameter => parameter.Input.Value, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<RelationQueryCapabilityEvidence> CreateCapabilityEvidence(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization)
    {
        var evidenceReference = string.Concat(
            CapabilityEvidenceReferencePrefix,
            "/",
            Uri.EscapeDataString(realization.TargetProfile.Target.Value),
            "/profile/",
            Uri.EscapeDataString(realization.TargetProfile.Id.Value));
        return
        [
            .. plan.InputContract.Capabilities.Select(capability => new RelationQueryCapabilityEvidence(
                capability.Input.Id,
                RelationQueryCapabilityEvidenceState.Available,
                evidenceReference))
        ];
    }

    static ImmutableArray<RelationQuerySuppliedSourceInput> CreateSuppliedSources(
        RelationQueryEvaluation evaluation,
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement)
    {
        if (evaluation.SuppliedRoots is null)
            return [];

        var root = plan.InputContract.Sources.SingleOrDefault(
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            ?? throw new InvalidOperationException("Supplied roots require one compiled relation-root input.");
        var binding = placement.Bindings.Single(candidate => candidate.Input == root.Input.Id);
        if (binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
        {
            throw new InvalidOperationException(
                "The relation-root placement must use directly supplied acquisition when roots are supplied.");
        }

        var fields = RelationQuerySourceReadFields.CreateSemantic(root.Fields, binding);
        ImmutableArray<RelationQuerySourceReadObservation> observations =
        [
            .. evaluation.SuppliedRoots.Observations.Select(observation =>
                CreateSourceObservation(observation, root.Shape, fields))
        ];
        return
        [
            new(
                root.Input.Id,
                evaluation.SuppliedRoots.Completeness,
                observations,
                evaluation.SuppliedRoots.EvidenceReference)
        ];
    }

    static RelationQuerySourceReadObservation CreateSourceObservation(
        Observation observation,
        QualifiedShapeId shape,
        ImmutableArray<RelationQuerySourceReadField> fields)
    {
        if (observation.ShapeId != shape.ShapeId)
        {
            throw new InvalidOperationException(
                $"Supplied root '{observation.Id}' has shape '{observation.ShapeId.Value}', expected '{shape.ShapeId.Value}'.");
        }

        var value = ObservationValue.FromObject(observation.Fields);
        return new(
            observation.Id,
            shape,
            [.. fields.Select(field => CreateFieldResult(value, field))]);
    }

    static RelationQuerySourceReadFieldResult CreateFieldResult(
        ObservationValue observation,
        RelationQuerySourceReadField field)
    {
        if (!RelationQueryObjectValues.TryGet(observation, field.SemanticPath, out var value)
            || value.Kind == ObservationValueKind.Undefined)
        {
            return new(field, RelationQuerySourceReadFieldState.Missing);
        }

        return value.Kind == ObservationValueKind.Null
            ? new(field, RelationQuerySourceReadFieldState.Null)
            : new(field, RelationQuerySourceReadFieldState.Value, value);
    }
}
