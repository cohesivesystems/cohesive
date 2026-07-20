using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Acquisition;

/// <summary>
/// Executes a deterministic bounded physical plan, assembles runtime evidence, and delegates all relation/query
/// semantics to a canonical interpreter.
/// </summary>
public sealed class RelationQueryPhysicalExecutor
{
    readonly ImmutableDictionary<RelationQuerySourceInstanceId, IRelationQuerySourceReader> readers;
    readonly IRelationQueryInterpreter interpreter;

    /// <summary>Creates a physical executor over an exact set of source readers.</summary>
    /// <param name="sourceReaders">Readers keyed by the physical source identities in a placement.</param>
    /// <param name="interpreter">
    /// Canonical interpreter that executes the terminal semantic stage, or <see langword="null"/> for
    /// <see cref="RelationQueryInMemoryInterpreter.Default"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceReaders"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReaders"/> contains a <see langword="null"/> reader or repeats a source identity.
    /// </exception>
    public RelationQueryPhysicalExecutor(
        IEnumerable<IRelationQuerySourceReader> sourceReaders,
        IRelationQueryInterpreter? interpreter = null)
    {
        ArgumentNullException.ThrowIfNull(sourceReaders);
        var normalized = sourceReaders.ToArray();
        if (normalized.Any(static reader => reader is null))
            throw new ArgumentException("Physical source readers cannot contain null entries.", nameof(sourceReaders));
        if (normalized.GroupBy(static reader => reader.Descriptor.Source).Any(static group => group.Count() > 1))
            throw new ArgumentException("Physical source readers cannot repeat a source identity.", nameof(sourceReaders));

        readers = normalized.ToImmutableDictionary(static reader => reader.Descriptor.Source);
        this.interpreter = interpreter ?? RelationQueryInMemoryInterpreter.Default;
    }

    /// <summary>Performs bounded acquisition and then executes the exact canonical semantic plan.</summary>
    /// <param name="request">Semantic, realization, placement, physical-plan, and evaluation inputs.</param>
    /// <param name="cancellationToken">Token that cancels acquisition and canonical interpretation.</param>
    /// <returns>Physical read traces, exact runtime evidence, and the canonical interpretation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The configured requirement-gap policy exposes an invalid identity, or a source reader or the canonical
    /// interpreter rejects an invalid argument.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured policy or canonical interpretation exposes an unsupported enum value.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled, or a source reader reports cancellation.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Plan fingerprinting, physical replanning, a delegated reader, or canonical interpretation encounters an
    /// internally inconsistent semantic snapshot.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A plan snapshot cannot be serialized for deterministic fingerprint verification.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A plan snapshot contains an unsupported serialization type, or a source reader or canonical interpreter rejects an
    /// unsupported operation.
    /// </exception>
    /// <remarks>
    /// Expected provider failures must be returned as source-read states. Exceptions thrown by a source reader or
    /// canonical interpretation propagate unchanged, except cancellation is always observed directly.
    /// </remarks>
    public ValueTask<RelationQueryPhysicalExecutionResult> ExecuteAsync(
        RelationQueryPhysicalExecutionRequest request,
        CancellationToken cancellationToken = default
        )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return RelationQueryTelemetryRuntime.IsOperationEnabled
            ? ExecuteObservedAsync(request, cancellationToken)
            : ExecuteCoreAsync(request, cancellationToken);
    }

    async ValueTask<RelationQueryPhysicalExecutionResult> ExecuteObservedAsync(
        RelationQueryPhysicalExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var activity = RelationQueryTelemetryRuntime.StartActivity(RelationQueryTelemetry.PhysicalExecutionActivityName);
        var started = RelationQueryTelemetryRuntime.StartTimer();
        Exception? failure = null;
        RelationQueryPhysicalExecutionResult? result = null;
        try
        {
            result = await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (activity?.IsAllDataRequested == true)
            {
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.PhysicalPlanFingerprintTagName,
                    request.PhysicalPlan.Fingerprint.Value);
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.RealizationFingerprintTagName,
                    request.Realization.Fingerprint.Value);
                activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, result.Diagnostics.Length);
                activity.SetTag(RelationQueryTelemetry.RowCountTagName, result.SourceReads.Sum(static read => read.ReturnedRows));
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
                RelationQueryTelemetry.PhysicalExecutionActivityName,
                status,
                exception: failure);
        }
    }

    async ValueTask<RelationQueryPhysicalExecutionResult> ExecuteCoreAsync(
        RelationQueryPhysicalExecutionRequest request,
        CancellationToken cancellationToken)
    {

        if (Preflight(request) is { } preflightFailure)
            return Failed(request, preflightFailure);

        ExecutionContext context = new(request, readers, interpreter);
        return await context.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    RelationQueryPhysicalExecutionDiagnostic? Preflight(RelationQueryPhysicalExecutionRequest request)
    {
        var physical = request.PhysicalPlan;
        var realizationFingerprint = RelationQueryRealizationFingerprinter.Compute(request.Realization);
        var placementFingerprint = RelationQuerySourcePlacementFingerprinter.Compute(physical.Placement);
        var physicalFingerprint = RelationQueryPhysicalPlanFingerprinter.Compute(physical);
        var replanned = RelationQueryPhysicalPlanner.Compile(
            request.Plan,
            request.Realization,
            physical.Placement,
            physical.Policy,
            interpreter);
        if (!Equals(realizationFingerprint, request.Realization.Fingerprint)
            || !Equals(physical.Realization, request.Realization.Fingerprint)
            || !Equals(placementFingerprint, physical.Placement.Fingerprint)
            || !Equals(physicalFingerprint, physical.Fingerprint)
            || !replanned.IsSuccessful
            || replanned.Plan is null
            || !Equals(replanned.Plan.Fingerprint, physical.Fingerprint))
        {
            return Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.PlanMismatch,
                "Physical execution requires the exact current semantic plan, realization, placement, policy, and physical-plan fingerprint.");
        }

        if (ValidateTraversalReachabilityConversions(request) is { } reachabilityFailure)
            return reachabilityFailure;

        var sourceContracts = request.Plan.InputContract.Sources.ToDictionary(static source => source.Input.Id);
        foreach (var supplied in request.SuppliedSources)
        {
            if (!sourceContracts.TryGetValue(supplied.Input, out var contract)
                || contract.Role != RelationQuerySourceInputRole.RelationRoot
                || physical.Placement.Bindings.Single(binding => binding.Input == supplied.Input).Acquisition
                    != RelationQuerySourceAcquisitionKind.Supplied)
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.SuppliedInputInvalid,
                    "A directly supplied input must identify an exact supplied relation-root source.",
                    input: supplied.Input);
            }
        }

        foreach (var binding in physical.Placement.Bindings)
        {
            if (binding.Partition is not null)
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    "The v1 source-read request cannot preserve a placed partition selector.",
                    input: binding.Input,
                    source: binding.Source);
            }

            if (binding.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
                continue;
            var source = physical.Placement.SourceInstances.Single(candidate => candidate.Id == binding.Source);
            if (!readers.TryGetValue(source.Id, out var reader))
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMissing,
                    "No source reader implements a required placed source instance.",
                    input: binding.Input,
                    source: source.Id);
            }

            var descriptor = reader.Descriptor;
            if (descriptor.Source != source.Id
                || descriptor.ExecutionDomain != source.ExecutionDomain
                || !ProfilesMatch(descriptor.TargetProfile, source.TargetProfile))
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.SourceReaderMismatch,
                    "A source reader does not match the placed source, execution domain, and capability profile.",
                    input: binding.Input,
                    source: source.Id);
            }
        }

        return null;
    }

    static RelationQueryPhysicalExecutionDiagnostic? ValidateTraversalReachabilityConversions(RelationQueryPhysicalExecutionRequest request)
    {
        if (request.ConversionFailures.IsDefaultOrEmpty)
            return null;

        var logicalOrder = request.Plan.LogicalPlan.EvaluationOrder
            .Select(static (node, ordinal) => (node, ordinal))
            .ToDictionary(static item => item.node, static item => item.ordinal);
        foreach (var downstream in request.Plan.InputContract.Traversals
                     .OrderBy(traversal => logicalOrder[traversal.Input.Traversal])
                     .ThenBy(static traversal => traversal.Input.Id.Value, StringComparer.Ordinal))
        {
            var downstreamPlacement = request.PhysicalPlan.Placement.Bindings.Single(binding => binding.Input == downstream.Input.Id);
            HashSet<RelationQueryInputId> reachabilityInputs = [];
            if (!TryAddBindingProducerReachabilityInputs(
                    request.Plan,
                    downstream.From,
                    downstream.FromShape,
                    reachabilityInputs,
                    new HashSet<RelationQueryInputId>()))
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    $"Traversal '{downstream.Input.Traversal.Value}' has no exact causal input chain for source binding '{downstream.From.Value}'.",
                    input: downstream.Input.Id,
                    source: downstreamPlacement.Source);
            }

            if (!RelationQueryPhysicalReachability.TryGetPreservingInterveningTraversals(
                    request.Plan,
                    downstream,
                    out var intervening))
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    $"Traversal '{downstream.Input.Traversal.Value}' has no proven v1 source-occurrence reachability chain.",
                    input: downstream.Input.Id,
                    source: downstreamPlacement.Source);
            }

            foreach (var prior in intervening)
            {
                if (!TryAddTraversalReachabilityInputs(request.Plan, prior, reachabilityInputs))
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                        $"Prior traversal '{prior.Input.Traversal.Value}' has no exact compiled correlation input for separated traversal '{downstream.Input.Traversal.Value}'.",
                        input: prior.Input.Id,
                        source: downstreamPlacement.Source);
                }
            }

            var failure = request.ConversionFailures
                .Where(conversion => reachabilityInputs.Contains(conversion.Input))
                .OrderBy(static conversion => conversion.Input.Value, StringComparer.Ordinal)
                .ThenBy(static conversion => conversion.Occurrence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static conversion => conversion.EvidenceReference, StringComparer.Ordinal)
                .FirstOrDefault();
            if (failure is null)
                continue;

            return Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                $"Predeclared conversion failure '{failure.Input.Value}' can change semantic reachability before traversal '{downstream.Input.Traversal.Value}', which the v1 physical executor cannot prove before acquisition.",
                input: failure.Input,
                source: downstreamPlacement.Source,
                evidenceReference: failure.EvidenceReference);
        }

        return null;
    }

    static bool TryAddBindingProducerReachabilityInputs(
        CompiledRelationQueryPlan plan,
        ValueBindingId binding,
        QualifiedShapeId shape,
        ISet<RelationQueryInputId> reachabilityInputs,
        ISet<RelationQueryInputId> visitedTraversals)
    {
        var sources = plan.InputContract.Sources
            .Where(source => source.Binding == binding && source.Shape == shape)
            .Take(2)
            .ToArray();
        var traversals = plan.InputContract.Traversals
            .Where(traversal => traversal.Result == binding && traversal.ResultShape == shape)
            .Take(2)
            .ToArray();
        if (sources.Length + traversals.Length != 1)
            return false;
        if (sources.Length == 1)
        {
            reachabilityInputs.Add(sources[0].Input.Id);
            return true;
        }

        var traversal = traversals[0];
        if (!visitedTraversals.Add(traversal.Input.Id)
            || !TryAddTraversalReachabilityInputs(plan, traversal, reachabilityInputs))
        {
            return false;
        }
        return TryAddBindingProducerReachabilityInputs(
            plan,
            traversal.From,
            traversal.FromShape,
            reachabilityInputs,
            visitedTraversals);
    }

    static bool TryAddTraversalReachabilityInputs(
        CompiledRelationQueryPlan plan,
        RelationQueryTraversalInputContract traversal,
        ISet<RelationQueryInputId> reachabilityInputs
        )
    {
        if (!TryGetBindingProducerNode(plan, traversal.From, traversal.FromShape, out var producer))
            return false;

        RelationQueryInputId[] correlationInputs = traversal.Input.Direction switch
        {
            RelationshipTraversalDirection.Forward =>
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryFieldInput>()
                    .Where(field => field.Producer == producer
                        && field.Binding == traversal.From
                        && field.Field.Shape == traversal.Definition.SourceShape
                        && field.Field.Path == traversal.Definition.SourceReference)
                    .Select(static field => field.Id)
                    .Take(2)
            ],
            RelationshipTraversalDirection.Inverse =>
            [
                .. plan.InputContract.Identities
                    .Where(identity => identity.Input.Producer == producer
                        && identity.Input.Binding == traversal.From
                        && identity.Input.Shape == traversal.FromShape)
                    .Select(static identity => identity.Input.Id)
                    .Take(2)
            ],
            _ => []
        };
        if (correlationInputs.Length != 1)
            return false;

        reachabilityInputs.Add(traversal.Input.Id);
        reachabilityInputs.Add(correlationInputs[0]);
        return true;
    }

    static bool TryGetBindingProducerNode(
        CompiledRelationQueryPlan plan,
        ValueBindingId binding,
        QualifiedShapeId shape,
        out QueryNodeId producer
        )
    {
        QueryNodeId[] producers =
        [
            .. plan.InputContract.Sources
                .Where(source => source.Binding == binding && source.Shape == shape)
                .Select(static source => source.Node)
                .Concat(plan.InputContract.Traversals
                    .Where(traversal => traversal.Result == binding && traversal.ResultShape == shape)
                    .Select(static traversal => traversal.Input.Traversal))
                .Take(2)
        ];
        if (producers.Length != 1)
        {
            producer = default;
            return false;
        }

        producer = producers[0];
        return true;
    }

    static bool ProfilesMatch(RelationQueryTargetCapabilityProfile left, RelationQueryTargetCapabilityProfile right)
        => left.HasSameSemantics(right);

    static RelationQueryPhysicalExecutionResult Failed(
        RelationQueryPhysicalExecutionRequest request,
        RelationQueryPhysicalExecutionDiagnostic diagnostic,
        ImmutableArray<RelationQuerySourceReadTrace> traces = default
        ) =>
        new(request, RelationQueryExecutionStatus.Failed, null, null, traces, [diagnostic]);

    static RelationQueryPhysicalExecutionDiagnostic Diagnostic(
        string code,
        string message,
        RelationQueryPhysicalStageId? stage = null,
        RelationQueryInputId? input = null,
        RelationQuerySourceInstanceId? source = null,
        string? evidenceReference = null
        ) =>
        new(code, DiagnosticSeverity.Error, message, stage, input, source, evidenceReference);

    sealed class ExecutionContext
    {
        readonly RelationQueryPhysicalExecutionRequest request;
        readonly IReadOnlyDictionary<RelationQuerySourceInstanceId, IRelationQuerySourceReader> readers;
        readonly IRelationQueryInterpreter interpreter;
        readonly IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements;
        readonly IReadOnlyDictionary<RelationQuerySourceInstanceId, RelationQuerySourceInstance> sources;
        readonly IReadOnlyDictionary<RelationQueryInputId, RelationQueryFieldInput> fieldInputs;
        readonly IReadOnlySet<RelationQueryInputId> identityFieldInputs;
        readonly List<RelationQuerySourceEvidence> sourceEvidence = [];
        readonly List<RelationQueryFieldEvidence> fieldEvidence = [];
        readonly List<RelationQueryTraversalEvidence> traversalEvidence = [];
        readonly List<RelationQuerySourceReadTrace> traces = [];
        readonly Dictionary<ValueBindingId, List<AcquiredOccurrence>> occurrencesByBinding = [];
        readonly Dictionary<RelationQueryPhysicalStageId, long> bufferedRowsByStage = [];
        readonly Dictionary<RelationQuerySourceInstanceId, long> bufferedRowsBySource = [];
        long localRows;

        public ExecutionContext(
            RelationQueryPhysicalExecutionRequest request,
            IReadOnlyDictionary<RelationQuerySourceInstanceId, IRelationQuerySourceReader> readers,
            IRelationQueryInterpreter interpreter
            )
        {
            this.request = request;
            this.readers = readers;
            this.interpreter = interpreter;
            placements = request.PhysicalPlan.Placement.Bindings.ToDictionary(static binding => binding.Input);
            sources = request.PhysicalPlan.Placement.SourceInstances.ToDictionary(static source => source.Id);
            fieldInputs = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryFieldInput>()
                .ToDictionary(static input => input.Id);
            identityFieldInputs = fieldInputs.Values
                .Where(input => IsStringIdentityField(request.Plan, input))
                .Select(static input => input.Id)
                .ToHashSet();
        }

        public async ValueTask<RelationQueryPhysicalExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (var source in request.Plan.InputContract.Sources.Where(static source => source.Role == RelationQuerySourceInputRole.RelationRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = AcquireSupplied(source, cancellationToken);
                if (diagnostic is not null)
                    return Failed(request, diagnostic, [.. traces]);
            }

            foreach (var source in request.Plan.InputContract.Sources.Where(static source => source.Role != RelationQuerySourceInputRole.RelationRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = await AcquireSourceAsync(source, cancellationToken).ConfigureAwait(false);
                if (diagnostic is not null)
                    return Failed(request, diagnostic, [.. traces]);
            }

            HashSet<ValueBindingId> readyBindings =
            [
                .. request.Plan.InputContract.Sources.Select(static source => source.Binding)
            ];
            var logicalOrder = request.Plan.LogicalPlan.EvaluationOrder
                .Select(static (node, ordinal) => (node, ordinal))
                .ToDictionary(static item => item.node, static item => item.ordinal);
            List<RelationQueryTraversalInputContract> remaining = [.. request.Plan.InputContract.Traversals];
            while (remaining.Count != 0)
            {
                var ready = remaining
                    .Where(traversal => readyBindings.Contains(traversal.From))
                    .OrderBy(traversal => logicalOrder[traversal.Input.Traversal])
                    .ThenBy(static traversal => traversal.Input.Id.Value, StringComparer.Ordinal)
                    .ToArray();
                if (ready.Length == 0)
                {
                    return Failed(
                        request,
                        Diagnostic(
                            RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                            "The physical traversal graph contains an unavailable owner binding."),
                        [.. traces]);
                }

                foreach (var traversal in ready)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var diagnostic = await AcquireTraversalAsync(traversal, cancellationToken).ConfigureAwait(false);
                    if (diagnostic is not null)
                        return Failed(request, diagnostic, [.. traces]);
                    readyBindings.Add(traversal.Result);
                    remaining.Remove(traversal);
                }
            }

            RelationQueryRuntimeEvidence evidence = new(
                request.Evaluation,
                request.Plan,
                RelationQueryEvidenceCompleteness.Complete,
                [.. sourceEvidence],
                [.. fieldEvidence],
                [.. traversalEvidence],
                request.Parameters,
                request.Capabilities,
                request.ConversionFailures
                );
            var interpretation = interpreter.Execute(
                new(request.Plan, evidence, request.RequirementGapPolicy),
                cancellationToken);
            return new(request, interpretation.Status, evidence, interpretation, [.. traces]);
        }

        RelationQueryPhysicalExecutionDiagnostic? AcquireSupplied(
            RelationQuerySourceInputContract contract,
            CancellationToken cancellationToken
            )
        {
            var binding = placements[contract.Input.Id];
            var source = sources[binding.Source];
            var stage = RequireStage(binding.Id, RelationQueryPhysicalStageKind.SuppliedInput);
            var expectedFields = RelationQuerySourceReadFields.CreateSemantic(contract.Fields, binding);
            var supplied = request.SuppliedSources.SingleOrDefault(candidate => candidate.Input == contract.Input.Id);
            if (supplied is null)
            {
                sourceEvidence.Add(new(
                    contract.Input.Id,
                    RelationQuerySourceEvidenceState.NotProvided,
                    RelationQueryEvidenceCompleteness.Partial));
                GetOccurrences(contract.Binding);
                return null;
            }

            var maximumRows = MaximumEnumerationRows(stage.Id, source);
            if ((long)supplied.Observations.Length > maximumRows
                || !TryReserveReadRows(stage.Id, source, supplied.Observations.Length)
                || !TryReserveLocalRows(supplied.Observations.Length))
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                    "A supplied source exceeds an explicit local or buffered-row boundary.",
                    input: contract.Input.Id,
                    source: binding.Source,
                    evidenceReference: supplied.EvidenceReference);
            }

            List<RelationQueryObservationOccurrence> occurrences = [];
            foreach (var row in supplied.Observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ValidateObservation(row, binding.Shape, expectedFields) is { } validation)
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.SuppliedInputInvalid,
                        validation,
                        input: contract.Input.Id,
                        source: binding.Source,
                        evidenceReference: supplied.EvidenceReference);
                }

                var acquired = Materialize(
                    row,
                    binding.Binding,
                    SourceOccurrenceId(contract.Input.Id, row.Identity),
                    cancellationToken);
                GetOccurrences(contract.Binding).Add(acquired);
                occurrences.Add(acquired.Occurrence);
            }

            sourceEvidence.Add(new(
                contract.Input.Id,
                RelationQuerySourceEvidenceState.Provided,
                supplied.Completeness,
                [.. occurrences],
                supplied.EvidenceReference));
            return null;
        }

        async ValueTask<RelationQueryPhysicalExecutionDiagnostic?> AcquireSourceAsync(
            RelationQuerySourceInputContract contract,
            CancellationToken cancellationToken
            )
        {
            var binding = placements[contract.Input.Id];
            var source = sources[binding.Source];
            var stage = RequireStage(binding.Id, RelationQueryPhysicalStageKind.SourceRead);
            var fields = RelationQuerySourceReadFields.CreateSemantic(contract.Fields, binding);
            var maximumBufferedRows = RemainingReadCapacity(stage.Id, source);
            var maximumRows = MaximumEnumerationRows(stage.Id, source);
            if (maximumBufferedRows <= 0 || maximumRows <= 0)
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                    "A bounded source read has no remaining local or buffered-row capacity.",
                    stage.Id,
                    contract.Input.Id,
                    source.Id);
            }
            RelationQuerySourceReadRequest read = new(
                request.PhysicalPlan.Fingerprint,
                stage.Id,
                binding.Id,
                source.Id,
                binding.Shape,
                binding.Identity!.SourceSelector,
                fields,
                new RelationQueryBoundedEnumeration(maximumRows),
                maximumBufferedRows
                );
            var result = await ReadSourceAsync(
                readers[source.Id],
                read,
                batchOrdinal: 0,
                cancellationToken).ConfigureAwait(false);
            if (result is null)
                return Invalid(read, evidenceReference: null, "A source reader returned no result object.");
            AddTrace(read, result, batchOrdinal: 0);
            if (ValidateResult(read, result, cancellationToken) is { } validation)
                return validation;
            if (!TryReserveReadRows(stage.Id, source, result.Observations.Length))
                return Boundary(read, result, "Physical acquisition exceeds an explicit stage or source buffered-row boundary.");

            var materializedRowCount = result.State is RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.Partial
                ? result.Observations.Length
                : 0;
            if (!TryReserveLocalRows(materializedRowCount))
                return Boundary(read, result, "Physical acquisition exceeds the plan-wide local-row boundary.");
            var acquiredRows = materializedRowCount == 0
                ? []
                : MaterializeSourceRows(contract, result.Observations, cancellationToken);
            var sourceState = result.State switch
            {
                RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.Partial
                    or RelationQuerySourceReadState.NotFound => RelationQuerySourceEvidenceState.Provided,
                RelationQuerySourceReadState.Failed => RelationQuerySourceEvidenceState.Failed,
                RelationQuerySourceReadState.Inconclusive => RelationQuerySourceEvidenceState.Inconclusive,
                _ => throw new ArgumentOutOfRangeException(nameof(result), result.State, "Unsupported source-read state.")
            };
            sourceEvidence.Add(new(
                contract.Input.Id,
                sourceState,
                result.Completeness,
                [.. acquiredRows.Select(static row => row.Occurrence)],
                result.EvidenceReference));
            GetOccurrences(contract.Binding).AddRange(acquiredRows);
            return null;
        }

        async ValueTask<RelationQueryPhysicalExecutionDiagnostic?> AcquireTraversalAsync(
            RelationQueryTraversalInputContract contract,
            CancellationToken cancellationToken
            ) =>
            contract.Input.Direction switch
            {
                RelationshipTraversalDirection.Forward =>
                    await AcquireForwardTraversalAsync(contract, cancellationToken).ConfigureAwait(false),
                RelationshipTraversalDirection.Inverse =>
                    await AcquireInverseTraversalAsync(contract, cancellationToken).ConfigureAwait(false),
                _ => Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    "The v1 executor encountered an unsupported traversal direction.",
                    input: contract.Input.Id)
            };

        async ValueTask<RelationQueryPhysicalExecutionDiagnostic?> AcquireForwardTraversalAsync(
            RelationQueryTraversalInputContract contract,
            CancellationToken cancellationToken
            )
        {
            var binding = placements[contract.Input.Id];
            var source = sources[binding.Source];
            var stage = RequireStage(binding.Id, RelationQueryPhysicalStageKind.BatchedIdentityLookup);
            var fields = RelationQuerySourceReadFields.CreateSemantic(contract.Fields, binding);
            if (SelectReachableOwners(contract, stage, out var owners) is { } reachabilityFailure)
                return reachabilityFailure;
            var referenceInput = request.Plan.RequirementGraph.Inputs
                .OfType<RelationQueryFieldInput>()
                .SingleOrDefault(input => input.Binding == contract.From
                    && input.Field.Shape == contract.Definition.SourceShape
                    && input.Field.Path == contract.Definition.SourceReference);
            if (referenceInput is null)
            {
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    "A forward traversal has no exact compiled source-reference field input.",
                    stage.Id,
                    contract.Input.Id,
                    source.Id);
            }

            List<OwnerKeys> active = [];
            foreach (var owner in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observed = owner.Fields.SingleOrDefault(field => field.Field.Input == referenceInput.Id);
                if (observed is null || observed.State != RelationQuerySourceReadFieldState.Value)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted,
                        evidenceReference: observed?.EvidenceReference));
                    continue;
                }

                var extraction = RelationQueryReferenceKeyExtractor.Extract(
                    observed.Value!.Value,
                    maximumKeys: 1,
                    cancellationToken,
                    out var extractedKeys);
                if (extraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                        "A relationship reference exceeds the explicit per-occurrence fan-out boundary.",
                        stage.Id,
                        contract.Input.Id,
                        source.Id,
                        observed.EvidenceReference);
                }
                if (extraction == RelationQueryReferenceKeyExtractionState.Invalid)
                {
                    return Invalid(
                        stage,
                        binding,
                        "A concrete relationship reference is not a non-empty string or an array of non-empty strings.",
                        observed.EvidenceReference);
                }
                if (extractedKeys.IsDefaultOrEmpty)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.Completed,
                        completeness: RelationQueryEvidenceCompleteness.Complete));
                    continue;
                }
                active.Add(new(owner, extractedKeys));
            }

            if (active.Count == 0)
            {
                GetOccurrences(contract.Result);
                return null;
            }

            HashSet<string> uniqueKeys = new(StringComparer.Ordinal);
            foreach (var owner in active)
                foreach (var key in owner.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uniqueKeys.Add(key);
                }
            var keys = uniqueKeys.Order(StringComparer.Ordinal).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, KeyBatchOutcome> outcomes = new(StringComparer.Ordinal);
            Dictionary<string, RelationQuerySourceReadObservation> rows = new(StringComparer.Ordinal);
            var batchSize = checked((int)Math.Min(stage.BatchSize!.Value, int.MaxValue));
            var batchOrdinal = 0;
            foreach (var batch in keys.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maximumBufferedRows = RemainingReadCapacity(stage.Id, source);
                if (maximumBufferedRows <= 0)
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                        "A batched identity lookup has no remaining stage or source buffer capacity.",
                        stage.Id,
                        contract.Input.Id,
                        source.Id);
                }
                var read = CreateLookupRequest(
                    stage,
                    binding,
                    source,
                    fields,
                    new RelationQueryIdentityBatchLookup([.. batch]),
                    maximumBufferedRows
                    );
                var result = await ReadSourceAsync(
                    readers[source.Id],
                    read,
                    batchOrdinal,
                    cancellationToken).ConfigureAwait(false);
                if (result is null)
                    return Invalid(read, evidenceReference: null, "A source reader returned no result object.");
                AddTrace(read, result, batchOrdinal++);
                if (ValidateResult(read, result, cancellationToken) is { } validation)
                    return validation;
                if (!TryReserveReadRows(stage.Id, source, result.Observations.Length))
                    return Boundary(read, result, "Physical acquisition exceeds an explicit stage or source buffered-row boundary.");
                if (result.State is RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.Partial)
                    foreach (var row in result.Observations)
                        rows.Add(row.Identity, row);
                var returnedIdentities = result.Observations
                    .Select(static row => row.Identity)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var key in batch)
                {
                    outcomes.Add(
                        key,
                        result.State == RelationQuerySourceReadState.Partial
                            && returnedIdentities.Contains(key)
                            ? new(
                                RelationQuerySourceReadState.Complete,
                                RelationQueryEvidenceCompleteness.Complete,
                                result.EvidenceReference)
                            : new(result.State, result.Completeness, result.EvidenceReference));
                }
            }

            foreach (var owner in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ownerOutcomes = owner.Keys.Select(key => outcomes[key]).ToArray();
                var failed = ownerOutcomes.FirstOrDefault(static outcome => outcome.State == RelationQuerySourceReadState.Failed);
                if (failed is not null)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.Failed,
                        evidenceReference: failed.EvidenceReference));
                    continue;
                }
                var inconclusive = ownerOutcomes.FirstOrDefault(static outcome => outcome.State == RelationQuerySourceReadState.Inconclusive);
                if (inconclusive is not null)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.Inconclusive,
                        evidenceReference: inconclusive.EvidenceReference));
                    continue;
                }

                var matched = owner.Keys.Where(rows.ContainsKey).Select(key => rows[key]).ToArray();
                var completeness = ownerOutcomes.Any(static outcome => outcome.Completeness == RelationQueryEvidenceCompleteness.Partial)
                    ? RelationQueryEvidenceCompleteness.Partial
                    : RelationQueryEvidenceCompleteness.Complete;
                if (ValidateFanOut(contract, source, matched.Length, stage) is { } fanOut)
                    return fanOut;
                if (!TryReserveLocalRows(matched.Length))
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                        "A correlated traversal result exceeds the plan-wide local-row boundary.",
                        stage.Id,
                        contract.Input.Id,
                        source.Id);
                }
                var occurrences = CloneTraversalRows(contract, owner.Owner, matched, cancellationToken);
                traversalEvidence.Add(new(
                    contract.Input.Id,
                    owner.Owner.Occurrence.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    occurrences,
                    completeness,
                    EvidenceReference(ownerOutcomes)));
            }
            return null;
        }

        async ValueTask<RelationQueryPhysicalExecutionDiagnostic?> AcquireInverseTraversalAsync(
            RelationQueryTraversalInputContract contract,
            CancellationToken cancellationToken)
        {
            var binding = placements[contract.Input.Id];
            var source = sources[binding.Source];
            var stage = RequireStage(binding.Id, RelationQueryPhysicalStageKind.BatchedPredicateLookup);
            var relationshipKey = binding.RelationshipKeys.Single(key => key.Input == contract.Input.Id);
            var fields = CreateInverseFields(contract.Fields, binding, relationshipKey);
            if (SelectReachableOwners(contract, stage, out var owners) is { } reachabilityFailure)
                return reachabilityFailure;
            List<OwnerKeys> active = [];
            foreach (var owner in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Occurrence.ObservationIdentity is not { } identity)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.NotAttempted));
                    continue;
                }
                active.Add(new(owner, [identity]));
            }
            if (active.Count == 0)
            {
                GetOccurrences(contract.Result);
                return null;
            }

            var keys = active.Select(static owner => owner.Keys[0])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, KeyBatchOutcome> outcomes = new(StringComparer.Ordinal);
            Dictionary<string, List<RelationQuerySourceReadObservation>> rows = new(StringComparer.Ordinal);
            var batchSize = checked((int)Math.Min(stage.BatchSize!.Value, int.MaxValue));
            var batchOrdinal = 0;
            foreach (var batch in keys.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maximumBufferedRows = RemainingReadCapacity(stage.Id, source);
                if (maximumBufferedRows <= 0)
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                        "A batched predicate lookup has no remaining stage or source buffer capacity.",
                        stage.Id,
                        contract.Input.Id,
                        source.Id);
                }
                var read = CreateLookupRequest(
                    stage,
                    binding,
                    source,
                    fields,
                    new RelationQueryRelationshipKeyBatchLookup(
                        relationshipKey.SemanticPath,
                        relationshipKey.SourceSelector,
                        [.. batch]),
                    maximumBufferedRows);
                var result = await ReadSourceAsync(
                    readers[source.Id],
                    read,
                    batchOrdinal,
                    cancellationToken).ConfigureAwait(false);
                if (result is null)
                    return Invalid(read, evidenceReference: null, "A source reader returned no result object.");
                AddTrace(read, result, batchOrdinal++);
                if (ValidateResult(read, result, cancellationToken) is { } validation)
                    return validation;
                if (!TryReserveReadRows(stage.Id, source, result.Observations.Length))
                    return Boundary(read, result, "Physical acquisition exceeds an explicit stage or source buffered-row boundary.");
                foreach (var key in batch)
                    outcomes.Add(key, new(result.State, result.Completeness, result.EvidenceReference));
                if (result.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.Partial))
                    continue;
                foreach (var row in result.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extraction = ExtractCorrelationKeys(
                        row,
                        relationshipKey,
                        request.PhysicalPlan.Policy.MaximumReferenceKeysPerObservation,
                        cancellationToken,
                        out var correlationKeys);
                    if (extraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                    {
                        return Boundary(
                            read,
                            result,
                            "A relationship correlation value exceeds the explicit fan-out boundary.");
                    }
                    if (extraction == RelationQueryReferenceKeyExtractionState.Invalid)
                    {
                        return Invalid(
                            read,
                            result.EvidenceReference,
                            "A relationship correlation value is not a string or bounded string array.");
                    }
                    foreach (var key in correlationKeys.Where(batch.Contains))
                    {
                        if (!rows.TryGetValue(key, out var found))
                            rows.Add(key, found = []);
                        found.Add(row);
                    }
                }
            }

            foreach (var owner in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = owner.Keys[0];
                var outcome = outcomes[key];
                if (outcome.State == RelationQuerySourceReadState.Failed)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.Failed,
                        evidenceReference: outcome.EvidenceReference));
                    continue;
                }
                if (outcome.State == RelationQuerySourceReadState.Inconclusive)
                {
                    traversalEvidence.Add(new(
                        contract.Input.Id,
                        owner.Owner.Occurrence.Id,
                        RelationQueryTraversalEvidenceState.Inconclusive,
                        evidenceReference: outcome.EvidenceReference));
                    continue;
                }

                var matched = rows.TryGetValue(key, out var found)
                    ? found.OrderBy(static row => row.Identity, StringComparer.Ordinal).ToArray()
                    : [];
                if (ValidateFanOut(contract, source, matched.Length, stage) is { } fanOut)
                    return fanOut;
                if (!TryReserveLocalRows(matched.Length))
                {
                    return Diagnostic(
                        RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                        "A correlated traversal result exceeds the plan-wide local-row boundary.",
                        stage.Id,
                        contract.Input.Id,
                        source.Id);
                }
                var occurrences = CloneTraversalRows(contract, owner.Owner, matched, cancellationToken);
                traversalEvidence.Add(new(
                    contract.Input.Id,
                    owner.Owner.Occurrence.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    occurrences,
                    outcome.Completeness,
                    outcome.EvidenceReference));
            }
            return null;
        }

        RelationQueryPhysicalExecutionDiagnostic? SelectReachableOwners(
            RelationQueryTraversalInputContract contract,
            RelationQueryPhysicalStage stage,
            out AcquiredOccurrence[] owners)
        {
            var candidates = GetOccurrences(contract.From)
                .OrderBy(static owner => owner.Occurrence.Id.Value, StringComparer.Ordinal)
                .ToArray();
            if (!RelationQueryPhysicalReachability.TryGetPreservingInterveningTraversals(
                    request.Plan,
                    contract,
                    out var intervening))
            {
                owners = [];
                return Diagnostic(
                    RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                    "A traversal has no proven v1 source-occurrence reachability chain.",
                    stage.Id,
                    contract.Input.Id,
                    placements[contract.Input.Id].Source);
            }
            if (intervening.IsDefaultOrEmpty)
            {
                owners = candidates;
                return null;
            }

            List<AcquiredOccurrence> reachable = new(candidates.Length);
            foreach (var candidate in candidates)
            {
                RelationQueryTraversalEvidence? blocker = null;
                foreach (var prior in intervening)
                {
                    var matches = traversalEvidence
                        .Where(evidence => evidence.Input == prior.Input.Id
                            && evidence.From == candidate.Occurrence.Id)
                        .Take(2)
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        owners = [];
                        return Diagnostic(
                            RelationQueryPhysicalExecutionDiagnosticCodes.StageInvalid,
                            $"Separated traversal '{contract.Input.Traversal.Value}' requires exactly one prior traversal outcome for source occurrence '{candidate.Occurrence.Id.Value}'.",
                            stage.Id,
                            contract.Input.Id,
                            placements[contract.Input.Id].Source);
                    }
                    if (!PreservesLeftRow(matches[0]))
                    {
                        blocker = matches[0];
                        break;
                    }
                }

                if (blocker is null)
                {
                    reachable.Add(candidate);
                    continue;
                }

                traversalEvidence.Add(new(
                    contract.Input.Id,
                    candidate.Occurrence.Id,
                    RelationQueryTraversalEvidenceState.NotApplicable,
                    evidenceReference: blocker.EvidenceReference));
            }

            owners = [.. reachable];
            return null;
        }

        static bool PreservesLeftRow(RelationQueryTraversalEvidence evidence) =>
            evidence.State == RelationQueryTraversalEvidenceState.NotApplicable
            || evidence.State == RelationQueryTraversalEvidenceState.Completed
            && (evidence.Results.Length == 1
                || evidence.Results.IsDefaultOrEmpty
                && evidence.Completeness == RelationQueryEvidenceCompleteness.Complete);

        ImmutableArray<AcquiredOccurrence> MaterializeSourceRows(
            RelationQuerySourceInputContract contract,
            ImmutableArray<RelationQuerySourceReadObservation> rows,
            CancellationToken cancellationToken
            )
        {
            var acquired = ImmutableArray.CreateBuilder<AcquiredOccurrence>(rows.Length);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                acquired.Add(Materialize(
                    row,
                    contract.Binding,
                    SourceOccurrenceId(contract.Input.Id, row.Identity),
                    cancellationToken));
            }
            return acquired.MoveToImmutable();
        }

        ImmutableArray<RelationQueryObservationOccurrence> CloneTraversalRows(
            RelationQueryTraversalInputContract contract,
            AcquiredOccurrence owner,
            IReadOnlyList<RelationQuerySourceReadObservation> rows,
            CancellationToken cancellationToken)
        {
            var occurrences = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>(rows.Count);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var acquired = Materialize(
                    row,
                    contract.Result,
                    TraversalOccurrenceId(contract.Input.Id, owner.Occurrence.Id, row.Identity),
                    cancellationToken);
                GetOccurrences(contract.Result).Add(acquired);
                occurrences.Add(acquired.Occurrence);
            }
            return occurrences.MoveToImmutable();
        }

        AcquiredOccurrence Materialize(
            RelationQuerySourceReadObservation row,
            ValueBindingId binding,
            RelationQueryOccurrenceId occurrenceId,
            CancellationToken cancellationToken
            )
        {
            RelationQueryObservationOccurrence occurrence = new(occurrenceId, binding, row.Shape, row.Identity);
            foreach (var field in row.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (field.Field.Input is not { } input
                    || field.Field.Purpose == RelationQuerySourceReadFieldPurpose.Correlation)
                    continue;
                var state = field.State switch
                {
                    RelationQuerySourceReadFieldState.Value => RelationQueryFieldEvidenceState.Value,
                    RelationQuerySourceReadFieldState.Null => RelationQueryFieldEvidenceState.Null,
                    RelationQuerySourceReadFieldState.Missing => RelationQueryFieldEvidenceState.Missing,
                    RelationQuerySourceReadFieldState.Failed => RelationQueryFieldEvidenceState.Failed,
                    RelationQuerySourceReadFieldState.Inconclusive => RelationQueryFieldEvidenceState.Inconclusive,
                    _ => throw new ArgumentOutOfRangeException(nameof(row), field.State, "Unsupported source field state.")
                };
                fieldEvidence.Add(new(input, occurrence.Id, state, field.Value, field.EvidenceReference));
            }
            return new(occurrence, row.Fields);
        }

        RelationQuerySourceReadRequest CreateLookupRequest(
            RelationQueryPhysicalStage stage,
            RelationQuerySourcePlacementBinding binding,
            RelationQuerySourceInstance source,
            ImmutableArray<RelationQuerySourceReadField> fields,
            RelationQuerySourceReadConstraint constraint,
            long maximumBufferedRows) =>
            new(
                request.PhysicalPlan.Fingerprint,
                stage.Id,
                binding.Id,
                source.Id,
                binding.Shape,
                binding.Identity!.SourceSelector,
                fields,
                constraint,
                maximumBufferedRows);

        RelationQueryPhysicalStage RequireStage(
            RelationQuerySourcePlacementBindingId binding,
            RelationQueryPhysicalStageKind kind) =>
            request.PhysicalPlan.Stages.Single(stage => stage.PlacementBinding == binding && stage.Kind == kind);

        RelationQueryPhysicalExecutionDiagnostic? ValidateResult(
            RelationQuerySourceReadRequest read,
            RelationQuerySourceReadResult result,
            CancellationToken cancellationToken)
        {
            if ((long)result.Observations.Length > read.MaximumBufferedRows
                || read.Constraint is RelationQueryBoundedEnumeration enumeration
                    && (long)result.Observations.Length > enumeration.MaximumRows)
            {
                return Boundary(read, result, "A source reader returned more rows than the request permits.");
            }

            foreach (var row in result.Observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ValidateObservation(row, read.Shape, read.Fields) is { } validation)
                    return Invalid(read, result.EvidenceReference, validation);
            }

            if (read.Constraint is RelationQueryIdentityBatchLookup identity
                && result.Observations.Any(row => !identity.Identities.Contains(row.Identity, StringComparer.Ordinal)))
            {
                return Invalid(read, result.EvidenceReference, "An identity lookup returned an observation outside the requested key batch.");
            }
            if (read.Constraint is RelationQueryRelationshipKeyBatchLookup relationship)
            {
                var source = sources[read.Source];
                foreach (var row in result.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extraction = ExtractCorrelationKeys(
                        row,
                        relationship,
                        request.PhysicalPlan.Policy.MaximumReferenceKeysPerObservation,
                        cancellationToken,
                        out var correlationKeys);
                    if (extraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                    {
                        return Boundary(
                            read,
                            result,
                            "A predicate lookup returned a correlation value beyond the explicit fan-out boundary.");
                    }
                    if (extraction == RelationQueryReferenceKeyExtractionState.Invalid
                        || !correlationKeys.Any(key => relationship.Keys.Contains(key, StringComparer.Ordinal)))
                    {
                        return Invalid(
                            read,
                            result.EvidenceReference,
                            "A predicate lookup returned an observation without a usable requested correlation key.");
                    }

                    var semanticReferences = row.Fields
                        .Where(field => field.Field.SemanticPath == relationship.RelationshipReference
                            && field.Field.Input is not null
                            && field.Field.Purpose is RelationQuerySourceReadFieldPurpose.SemanticInput
                                or RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation)
                        .Take(2)
                        .ToArray();
                    if (semanticReferences.Length != 1
                        || semanticReferences[0].State != RelationQuerySourceReadFieldState.Value)
                    {
                        return Invalid(
                            read,
                            result.EvidenceReference,
                            "A predicate lookup returned no unambiguous semantic relationship-reference value for correlation validation.");
                    }

                    var semanticExtraction = RelationQueryReferenceKeyExtractor.Extract(
                        semanticReferences[0].Value!.Value,
                        request.PhysicalPlan.Policy.MaximumReferenceKeysPerObservation,
                        cancellationToken,
                        out var semanticKeys);
                    if (semanticExtraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                    {
                        return Boundary(
                            read,
                            result,
                            "A predicate lookup returned a semantic relationship reference beyond the explicit reference-key boundary.");
                    }
                    if (semanticExtraction == RelationQueryReferenceKeyExtractionState.Invalid
                        || !semanticKeys.SequenceEqual(correlationKeys, StringComparer.Ordinal))
                    {
                        return Invalid(
                            read,
                            result.EvidenceReference,
                            "A predicate lookup returned conflicting semantic and correlation relationship-reference values.");
                    }
                }
            }

            return null;
        }

        string? ValidateObservation(
            RelationQuerySourceReadObservation row,
            QualifiedShapeId shape,
            ImmutableArray<RelationQuerySourceReadField> fields)
        {
            if (row.Shape != shape)
                return "A source reader returned an observation with a shape different from the exact request.";
            if (row.Fields.Length != fields.Length
                || !row.Fields.Select(static result => result.Field).SequenceEqual(fields))
            {
                return "A source reader returned missing, extra, or altered field selections.";
            }
            if (row.Fields.Any(result =>
                    result is
                    {
                        State: RelationQuerySourceReadFieldState.Value,
                        Field.Input: { } input,
                        Value: { } value
                    }
                    && fieldInputs[input].ValueContract is { } contract
                    && !contract.IsSatisfiedByConstant(value)))
            {
                return "A source reader returned a concrete field value that violates its compiled value contract.";
            }
            if (row.Fields.Any(result =>
                    result.Field.Input is { } input
                    && identityFieldInputs.Contains(input)
                    && (result.State != RelationQuerySourceReadFieldState.Value
                        || result.Value is not { Kind: ObservationValueKind.String } value
                        || !string.Equals(value.String, row.Identity, StringComparison.Ordinal))))
            {
                return "A source reader returned a semantic identity field that differs from its observation identity.";
            }
            return null;
        }

        static bool IsStringIdentityField(
            CompiledRelationQueryPlan plan,
            RelationQueryFieldInput input)
        {
            if (input.Field.Path.Segments.Length != 1
                || !input.Field.Path.Segments[0].TryGetFieldIdentity(out var fieldName))
            {
                return false;
            }

            var graph = plan.Provenance.ShapeDocuments
                .SingleOrDefault(document => document.Graph.Id == input.Field.Shape.GraphId)
                ?.Graph;
            var shape = graph?.TryGetShape(input.Field.Shape);
            return shape is not null
                && shape.TryGetField(fieldName, out var field)
                && field.Role == FieldRole.Identity
                && field.Cardinality == FieldCardinality.Single
                && field.Type is ScalarTypeRef { Kind: ScalarTypeKind.String };
        }

        RelationQueryPhysicalExecutionDiagnostic Invalid(
            RelationQuerySourceReadRequest read,
            string? evidenceReference,
            string message) =>
            Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid,
                message,
                read.Stage,
                placements.Values.Single(binding => binding.Id == read.PlacementBinding).Input,
                read.Source,
                evidenceReference);

        RelationQueryPhysicalExecutionDiagnostic Invalid(
            RelationQueryPhysicalStage stage,
            RelationQuerySourcePlacementBinding binding,
            string message,
            string? evidenceReference = null) =>
            Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.SourceResultInvalid,
                message,
                stage.Id,
                binding.Input,
                binding.Source,
                evidenceReference);

        RelationQueryPhysicalExecutionDiagnostic Boundary(
            RelationQuerySourceReadRequest read,
            RelationQuerySourceReadResult result,
            string message) =>
            Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                message,
                read.Stage,
                placements.Values.Single(binding => binding.Id == read.PlacementBinding).Input,
                read.Source,
                result.EvidenceReference);

        RelationQueryPhysicalExecutionDiagnostic? ValidateFanOut(
            RelationQueryTraversalInputContract contract,
            RelationQuerySourceInstance source,
            int count,
            RelationQueryPhysicalStage stage)
        {
            var maximum = MaximumFanOut(source);
            if (count <= maximum)
                return null;
            return Diagnostic(
                RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded,
                "A correlated traversal result exceeds an explicit fan-out boundary.",
                stage.Id,
                contract.Input.Id,
                source.Id);
        }

        static ValueTask<RelationQuerySourceReadResult> ReadSourceAsync(
            IRelationQuerySourceReader reader,
            RelationQuerySourceReadRequest request,
            int batchOrdinal,
            CancellationToken cancellationToken)
        {
            return RelationQueryTelemetryRuntime.IsSourceReadEnabled
                ? ReadSourceObservedAsync(reader, request, batchOrdinal, cancellationToken)
                : reader.ReadAsync(request, cancellationToken);
        }

        static async ValueTask<RelationQuerySourceReadResult> ReadSourceObservedAsync(
            IRelationQuerySourceReader reader,
            RelationQuerySourceReadRequest request,
            int batchOrdinal,
            CancellationToken cancellationToken)
        {
            var activity = RelationQueryTelemetryRuntime.StartActivity(
                RelationQueryTelemetry.SourceReadActivityName,
                ActivityKind.Client);
            var started = RelationQueryTelemetryRuntime.StartTimer();
            Exception? failure = null;
            RelationQuerySourceReadResult? result = null;
            var (kind, keyCount) = ReadFacts(request.Constraint);
            try
            {
                result = await reader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                if (activity?.IsAllDataRequested == true)
                {
                    activity.SetTag(RelationQueryTelemetry.ReadKindTagName, RelationQueryTelemetry.GetTagValue(kind));
                    activity.SetTag(RelationQueryTelemetry.KeyCountTagName, keyCount);
                    activity.SetTag(RelationQueryTelemetry.BatchOrdinalTagName, batchOrdinal);
                    if (result is not null)
                    {
                        activity.SetTag(
                            RelationQueryTelemetry.CompletenessTagName,
                            RelationQueryTelemetry.GetTagValue(result.Completeness));
                        activity.SetTag(RelationQueryTelemetry.RowCountTagName, result.Observations.Length);
                    }
                }
                if (result is not null)
                    RelationQueryTelemetryRuntime.RecordSourceRows(result.Observations.Length, kind, result.State);
                return result!;
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
                        : result is null
                            ? RelationQueryTelemetry.InvalidStatus
                            : RelationQueryTelemetry.GetStatusTagValue(result.State);
                RelationQueryTelemetryRuntime.CompleteOperation(
                    activity,
                    started,
                    RelationQueryTelemetry.SourceReadActivityName,
                    status,
                    exception: failure);
            }
        }

        void AddTrace(
            RelationQuerySourceReadRequest read,
            RelationQuerySourceReadResult result,
            int batchOrdinal)
        {
            var (kind, keyCount) = ReadFacts(read.Constraint);
            traces.Add(new(
                read.Stage,
                read.Source,
                kind,
                batchOrdinal,
                keyCount,
                result.State,
                result.Completeness,
                result.Observations.Length,
                result.EvidenceReference));
        }

        static (RelationQuerySourceReadKind Kind, int KeyCount) ReadFacts(
            RelationQuerySourceReadConstraint constraint) => constraint switch
            {
                RelationQueryBoundedEnumeration => (RelationQuerySourceReadKind.BoundedEnumeration, 0),
                RelationQueryIdentityBatchLookup identity =>
                    (RelationQuerySourceReadKind.IdentityBatch, identity.Identities.Length),
                RelationQueryRelationshipKeyBatchLookup relationship =>
                    (RelationQuerySourceReadKind.RelationshipKeyBatch, relationship.Keys.Length),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(constraint),
                    constraint,
                    "Unsupported source-read constraint.")
            };

        ImmutableArray<RelationQuerySourceReadField> CreateInverseFields(
            ImmutableArray<RelationQueryFieldInputContract> contracts,
            RelationQuerySourcePlacementBinding binding,
            RelationQueryRelationshipKeyBinding relationshipKey)
        {
            var fields = RelationQuerySourceReadFields.CreateSemantic(contracts, binding).ToBuilder();
            var combined = fields
                .Select((field, index) => (field, index))
                .FirstOrDefault(candidate => candidate.field.SemanticPath == relationshipKey.SemanticPath
                    && string.Equals(candidate.field.SourceSelector, relationshipKey.SourceSelector, StringComparison.Ordinal));
            if (combined.field is not null)
            {
                fields[combined.index] = new(
                    combined.field.Input,
                    combined.field.SemanticPath,
                    combined.field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation);
            }
            else
            {
                fields.Add(new(
                    input: null,
                    relationshipKey.SemanticPath,
                    relationshipKey.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.Correlation));
            }
            return [.. fields];
        }

        static RelationQueryReferenceKeyExtractionState ExtractCorrelationKeys(
            RelationQuerySourceReadObservation row,
            RelationQueryRelationshipKeyBinding binding,
            long maximumKeys,
            CancellationToken cancellationToken,
            out ImmutableArray<string> keys)
        {
            var field = row.Fields.Single(result =>
                result.Field.SemanticPath == binding.SemanticPath
                && string.Equals(result.Field.SourceSelector, binding.SourceSelector, StringComparison.Ordinal)
                && result.Field.Purpose is RelationQuerySourceReadFieldPurpose.Correlation
                    or RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation);
            if (field.State != RelationQuerySourceReadFieldState.Value)
            {
                keys = [];
                return RelationQueryReferenceKeyExtractionState.Invalid;
            }
            return RelationQueryReferenceKeyExtractor.Extract(
                field.Value!.Value,
                maximumKeys,
                cancellationToken,
                out keys);
        }

        static RelationQueryReferenceKeyExtractionState ExtractCorrelationKeys(
            RelationQuerySourceReadObservation row,
            RelationQueryRelationshipKeyBatchLookup lookup,
            long maximumKeys,
            CancellationToken cancellationToken,
            out ImmutableArray<string> keys)
        {
            var field = row.Fields.Single(result =>
                result.Field.SemanticPath == lookup.RelationshipReference
                && string.Equals(result.Field.SourceSelector, lookup.SourceSelector, StringComparison.Ordinal)
                && result.Field.Purpose is RelationQuerySourceReadFieldPurpose.Correlation
                    or RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation);
            if (field.State != RelationQuerySourceReadFieldState.Value)
            {
                keys = [];
                return RelationQueryReferenceKeyExtractionState.Invalid;
            }
            return RelationQueryReferenceKeyExtractor.Extract(
                field.Value!.Value,
                maximumKeys,
                cancellationToken,
                out keys);
        }

        long MaximumFanOut(RelationQuerySourceInstance source) =>
            Math.Min(request.PhysicalPlan.Policy.MaximumFanOut, source.Limits.MaximumFanOut);

        long MaximumEnumerationRows(
            RelationQueryPhysicalStageId stage,
            RelationQuerySourceInstance source) =>
            Math.Min(
                Math.Max(0, request.PhysicalPlan.Policy.MaximumLocalRows - localRows),
                RemainingReadCapacity(stage, source));

        long RemainingReadCapacity(
            RelationQueryPhysicalStageId stage,
            RelationQuerySourceInstance source) =>
            Math.Min(
                Math.Max(
                    0,
                    request.PhysicalPlan.Policy.MaximumBufferedRows
                    - bufferedRowsByStage.GetValueOrDefault(stage)),
                Math.Max(
                    0,
                    source.Limits.MaximumBufferedRows
                    - bufferedRowsBySource.GetValueOrDefault(source.Id)));

        bool TryReserveReadRows(
            RelationQueryPhysicalStageId stage,
            RelationQuerySourceInstance source,
            int count)
        {
            var stageRows = checked(bufferedRowsByStage.GetValueOrDefault(stage) + count);
            var sourceRows = checked(bufferedRowsBySource.GetValueOrDefault(source.Id) + count);
            bufferedRowsByStage[stage] = stageRows;
            bufferedRowsBySource[source.Id] = sourceRows;
            return stageRows <= request.PhysicalPlan.Policy.MaximumBufferedRows
                && sourceRows <= source.Limits.MaximumBufferedRows;
        }

        bool TryReserveLocalRows(int count)
        {
            localRows = checked(localRows + count);
            return localRows <= request.PhysicalPlan.Policy.MaximumLocalRows;
        }

        List<AcquiredOccurrence> GetOccurrences(ValueBindingId binding)
        {
            if (!occurrencesByBinding.TryGetValue(binding, out var occurrences))
                occurrencesByBinding.Add(binding, occurrences = []);
            return occurrences;
        }

        static RelationQueryOccurrenceId SourceOccurrenceId(RelationQueryInputId input, string identity) =>
            new($"physical/source/{Uri.EscapeDataString(input.Value)}/{Uri.EscapeDataString(identity)}");

        static RelationQueryOccurrenceId TraversalOccurrenceId(
            RelationQueryInputId input,
            RelationQueryOccurrenceId owner,
            string identity) =>
            new($"physical/traversal/{Uri.EscapeDataString(input.Value)}/{Uri.EscapeDataString(owner.Value)}/{Uri.EscapeDataString(identity)}");

        static string? EvidenceReference(IEnumerable<KeyBatchOutcome> outcomes) =>
            outcomes.Select(static outcome => outcome.EvidenceReference)
                .FirstOrDefault(static reference => reference is not null);

        sealed record AcquiredOccurrence(
            RelationQueryObservationOccurrence Occurrence,
            ImmutableArray<RelationQuerySourceReadFieldResult> Fields);

        sealed record OwnerKeys(AcquiredOccurrence Owner, ImmutableArray<string> Keys);

        sealed record KeyBatchOutcome(
            RelationQuerySourceReadState State,
            RelationQueryEvidenceCompleteness Completeness,
            string? EvidenceReference);
    }
}
