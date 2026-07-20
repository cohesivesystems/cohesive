using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>Deterministically lowers an exact semantic plan, realization report, and placement into a bounded physical plan.</summary>
public static class RelationQueryPhysicalPlanner
{
    static readonly RelationQueryPhysicalLoweringRuleId SuppliedInputLowering =
        new("cohesive.relations.physical/supplied-input/v1");
    static readonly RelationQueryPhysicalLoweringRuleId BoundedEnumerationLowering =
        new("cohesive.relations.physical/bounded-enumeration/v1");
    static readonly RelationQueryPhysicalLoweringRuleId ForwardRelationshipLowering =
        new("cohesive.relations.physical/forward-identity-batch/v1");
    static readonly RelationQueryPhysicalLoweringRuleId InverseRelationshipLowering =
        new("cohesive.relations.physical/inverse-predicate-batch/v1");
    static readonly RelationQueryPhysicalLoweringRuleId LocalEquijoinLowering =
        new("cohesive.relations.physical/local-equijoin/v1");
    static readonly RelationQueryPhysicalLoweringRuleId EvidenceAssemblyLowering =
        new("cohesive.relations.physical/runtime-evidence/v1");
    static readonly RelationQueryPhysicalLoweringRuleId ReferenceInterpreterLowering =
        new("cohesive.relations.physical/reference-interpreter/v1");

    /// <summary>Compiles one exact bounded physical realization.</summary>
    /// <param name="plan">Successful target-independent semantic plan.</param>
    /// <param name="realization">
    /// Exact realizable report produced by <paramref name="interpreter"/> for <paramref name="plan"/>.
    /// </param>
    /// <param name="placement">Plan-scoped physical source placement.</param>
    /// <param name="policy">Explicit bounded physical-planning policy.</param>
    /// <param name="interpreter">
    /// Exact canonical interpreter that produced <paramref name="realization"/> and will execute the physical
    /// terminal, or <see langword="null"/> to use <see cref="RelationQueryInMemoryInterpreter.Default"/>.
    /// </param>
    /// <returns>A compiled physical plan or structured invalid/unavailable diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A semantic snapshot cannot be fingerprinted deterministically.</exception>
    /// <exception cref="System.Text.Json.JsonException">A semantic snapshot cannot be serialized for fingerprinting.</exception>
    /// <exception cref="NotSupportedException">A semantic snapshot contains a runtime type unsupported by serialization.</exception>
    public static RelationQueryPhysicalPlanningResult Compile(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        RelationQueryPhysicalPlanningPolicy policy,
        IRelationQueryInterpreter? interpreter = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(realization);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(policy);

        return RelationQueryTelemetryRuntime.IsOperationEnabled
            ? CompileObserved(plan, realization, placement, policy, interpreter)
            : CompileCore(plan, realization, placement, policy, interpreter);
    }

    static RelationQueryPhysicalPlanningResult CompileCore(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        RelationQueryPhysicalPlanningPolicy policy,
        IRelationQueryInterpreter? interpreter)
    {
        var context = new PlanningContext(
            plan,
            realization,
            placement,
            policy,
            interpreter ?? RelationQueryInMemoryInterpreter.Default);
        return context.Compile();
    }

    static RelationQueryPhysicalPlanningResult CompileObserved(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        RelationQueryPhysicalPlanningPolicy policy,
        IRelationQueryInterpreter? interpreter)
    {
        var activity = RelationQueryTelemetryRuntime.StartActivity(
            RelationQueryTelemetry.PhysicalPlanningActivityName);
        var started = RelationQueryTelemetryRuntime.StartTimer();
        Exception? failure = null;
        RelationQueryPhysicalPlanningResult? result = null;
        try
        {
            result = CompileCore(plan, realization, placement, policy, interpreter);
            if (activity?.IsAllDataRequested == true)
            {
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.PlanFingerprintTagName,
                    RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                        RelationQueryCompiledPlanReference.From(plan)).Value);
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.RealizationFingerprintTagName,
                    realization.Fingerprint.Value);
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.PlacementFingerprintTagName,
                    placement.Fingerprint.Value);
                activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, result.Diagnostics.Length);
                foreach (var diagnostic in result.Diagnostics)
                {
                    RelationQueryTelemetry.AddDiagnosticEvent(
                        activity,
                        diagnostic.Code,
                        diagnostic.Severity);
                }
                if (result.Plan is { } physicalPlan)
                {
                    RelationQueryTelemetry.TrySetFingerprintTag(
                        activity,
                        RelationQueryTelemetry.PhysicalPlanFingerprintTagName,
                        physicalPlan.Fingerprint.Value);
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
            RelationQueryTelemetryRuntime.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.PhysicalPlanningActivityName,
                failure is not null || result is null
                    ? RelationQueryTelemetry.ExceptionStatus
                    : RelationQueryTelemetry.GetStatusTagValue(result.Status),
                exception: failure);
        }
    }

    sealed class PlanningContext
    {
        readonly CompiledRelationQueryPlan plan;
        readonly RelationQueryRealizationReport realization;
        readonly RelationQuerySourcePlacement placement;
        readonly RelationQueryPhysicalPlanningPolicy policy;
        readonly IRelationQueryInterpreter interpreter;
        readonly Dictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements;
        readonly Dictionary<RelationQuerySourceInstanceId, RelationQuerySourceInstance> sources;
        readonly Dictionary<RelationQueryRealizationRequirementId, RelationQueryRealizationDecision> decisions;
        readonly List<RelationQueryPhysicalPlanningDiagnostic> diagnostics = [];
        readonly List<RelationQueryPhysicalStage> stages = [];
        readonly Dictionary<ValueBindingId, RelationQueryPhysicalStageId> bindingProducers = [];
        readonly Dictionary<QueryNodeId, RelationQueryPhysicalStageId> sourceNodeProducers = [];
        readonly Dictionary<QueryNodeId, RelationQueryPhysicalStageId> traversalNodeProducers = [];

        public PlanningContext(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQuerySourcePlacement placement,
            RelationQueryPhysicalPlanningPolicy policy,
            IRelationQueryInterpreter interpreter)
        {
            this.plan = plan;
            this.realization = realization;
            this.placement = placement;
            this.policy = policy;
            this.interpreter = interpreter;
            placements = placement.Bindings.ToDictionary(static binding => binding.Input);
            sources = placement.SourceInstances.ToDictionary(static source => source.Id);
            decisions = realization.Decisions.ToDictionary(static decision => decision.Requirement);
        }

        public RelationQueryPhysicalPlanningResult Compile()
        {
            if (!ValidateGlobalInputs())
                return Failure(RelationQueryPhysicalPlanningStatus.Invalid);

            ValidatePlacementCompleteness();
            ValidateUnsupportedNodes();
            if (HasErrors)
                return Failure(CurrentFailureStatus());

            foreach (var source in plan.InputContract.Sources)
                LowerSource(source);

            var logicalOrder = plan.LogicalPlan.EvaluationOrder
                .Select(static (node, ordinal) => (node, ordinal))
                .ToDictionary(static item => item.node, static item => item.ordinal);
            foreach (var traversal in plan.InputContract.Traversals
                         .OrderBy(traversal => logicalOrder[traversal.Input.Traversal])
                         .ThenBy(static traversal => traversal.Input.Id.Value, StringComparer.Ordinal))
                LowerTraversal(traversal);

            LowerExplicitJoins();
            if (HasErrors)
                return Failure(CurrentFailureStatus());

            var acquisitionTerminals = stages
                .Where(static stage => stage.Kind is RelationQueryPhysicalStageKind.ExactFieldProjection
                    or RelationQueryPhysicalStageKind.SuppliedInput
                    or RelationQueryPhysicalStageKind.SourceRead
                    or RelationQueryPhysicalStageKind.LocalCorrelation)
                .Where(stage => !stages.Any(candidate => candidate.Dependencies.Contains(stage.Id)))
                .Select(static stage => stage.Id)
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (acquisitionTerminals.IsDefaultOrEmpty)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid,
                    "Physical planning produced no acquisition terminal.");
                return Failure(RelationQueryPhysicalPlanningStatus.Invalid);
            }

            var allInputs = plan.RequirementGraph.Inputs
                .Select(static input => input.Id)
                .OrderBy(static input => input.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var evidenceId = StageId("runtime-evidence", "assembly");
            stages.Add(new(
                evidenceId,
                RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly,
                acquisitionTerminals,
                placementBinding: null,
                semanticInputs: allInputs,
                requestedFields: [],
                batchSize: null,
                Provenance(
                    nodes: plan.LogicalPlan.RetainedNodes,
                    inputs: allInputs,
                    placementBinding: null,
                    source: null,
                    primitives: [],
                    EvidenceAssemblyLowering,
                    includeRealizationRules: false,
                    includeAllRequirements: true)));
            var terminalId = StageId("reference-interpreter", "terminal");
            stages.Add(new(
                terminalId,
                RelationQueryPhysicalStageKind.ReferenceInterpreterTerminal,
                [evidenceId],
                placementBinding: null,
                semanticInputs: allInputs,
                requestedFields: [],
                batchSize: null,
                Provenance(
                    nodes: plan.LogicalPlan.RetainedNodes,
                    inputs: allInputs,
                    placementBinding: null,
                    source: null,
                    primitives: [],
                    ReferenceInterpreterLowering,
                    includeRealizationRules: false,
                    includeAllRequirements: true)));

            try
            {
                var physical = new CompiledRelationQueryPhysicalPlan(
                    CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
                    RelationQueryCompiledPlanReference.From(plan),
                    realization.Fingerprint,
                    placement,
                    policy,
                    [.. stages],
                    terminalId,
                    diagnostics: []);
                return new(RelationQueryPhysicalPlanningStatus.Planned, physical);
            }
            catch (ArgumentException exception)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid,
                    $"The derived physical stage graph is invalid: {exception.Message}");
                return Failure(RelationQueryPhysicalPlanningStatus.Invalid);
            }
        }

        bool ValidateGlobalInputs()
        {
            var reportFingerprint = RelationQueryRealizationFingerprinter.Compute(realization);
            var terminalRealization = interpreter.Realize(plan);
            if (!Equals(reportFingerprint, realization.Fingerprint)
                || realization.Status != RelationQueryRealizationStatus.Realizable
                || !realization.IsRealizable
                || !Equals(terminalRealization.Fingerprint, realization.Fingerprint)
                || !realization.Plan.GetMismatchedComponents(plan).IsDefaultOrEmpty)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.RealizationInvalid,
                    "Physical planning requires the configured reference interpreter's realizable report for the exact compiled plan.");
            }

            if (!placement.Plan.GetMismatchedComponents(plan).IsDefaultOrEmpty)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    "Source placement is stale or belongs to another compiled plan.");
            }

            return !HasErrors;
        }

        void ValidatePlacementCompleteness()
        {
            foreach (var source in placement.SourceInstances)
            {
                var analysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(source.TargetProfile);
                if (!analysis.Issues.IsDefaultOrEmpty)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                        $"Source '{source.Id.Value}' has an invalid target capability profile.");
                }
                if (!source.TargetProfile.SupportedDefinitionSchemaVersions.Contains(
                        plan.Provenance.DefinitionDocument.SchemaVersion,
                        StringComparer.Ordinal)
                    || !source.TargetProfile.SupportedCompilerProfiles.Contains(
                        plan.Provenance.CompilerProfile,
                        StringComparer.Ordinal))
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                        $"Source '{source.Id.Value}' does not support the exact definition schema and compiler profile.");
                }
            }

            HashSet<RelationQueryInputId> expected =
            [
                .. plan.InputContract.Sources.Select(static source => source.Input.Id),
                .. plan.InputContract.Traversals.Select(static traversal => traversal.Input.Id)
            ];
            foreach (var input in expected.OrderBy(static input => input.Value, StringComparer.Ordinal))
            {
                if (!placements.ContainsKey(input))
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMissing,
                        $"Compiled acquisition input '{input.Value}' has no source placement.",
                        input);
                }
            }

            foreach (var extra in placements.Keys.Except(expected).OrderBy(static input => input.Value, StringComparer.Ordinal))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    $"Placement input '{extra.Value}' is not a source or traversal in the exact input contract.",
                    extra);
            }

            foreach (var source in plan.InputContract.Sources)
                if (placements.TryGetValue(source.Input.Id, out var binding))
                    ValidateSourcePlacement(source, binding);
            foreach (var traversal in plan.InputContract.Traversals)
                if (placements.TryGetValue(traversal.Input.Id, out var binding))
                    ValidateTraversalPlacement(traversal, binding);

            foreach (var binding in placement.Bindings.Where(static binding => binding.Partition is not null))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.OperatingBoundaryInvalid,
                    $"Placement '{binding.Id.Value}' uses a partition selector, which the v1 physical reader contract cannot preserve.",
                    binding.Input,
                    binding.Id);
            }
        }

        void ValidateSourcePlacement(
            RelationQuerySourceInputContract contract,
            RelationQuerySourcePlacementBinding binding)
        {
            var expectedAcquisition = contract.Role == RelationQuerySourceInputRole.RelationRoot
                ? RelationQuerySourceAcquisitionKind.Supplied
                : RelationQuerySourceAcquisitionKind.BoundedEnumeration;
            if (binding.Kind != RelationQuerySourcePlacementBindingKind.SourceSet
                || binding.Input != contract.Input.Id
                || binding.Node != contract.Node
                || binding.Binding != contract.Binding
                || binding.Shape != contract.Shape
                || binding.Acquisition != expectedAcquisition)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    $"Placement '{binding.Id.Value}' does not preserve source input '{contract.Input.Id.Value}'.",
                    contract.Input.Id,
                    binding.Id);
            }

            ValidateFields(contract.Fields, binding);
            if (expectedAcquisition != RelationQuerySourceAcquisitionKind.Supplied)
            {
                RequireIdentity(binding, contract.Input.Id);
                RequireCapabilities(
                    binding,
                    contract.Input.Id,
                    SourceReadCapabilities(contract));
            }
        }

        void ValidateTraversalPlacement(
            RelationQueryTraversalInputContract contract,
            RelationQuerySourcePlacementBinding binding)
        {
            if (binding.Kind != RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                || binding.Input != contract.Input.Id
                || binding.Node != contract.Input.Traversal
                || binding.Binding != contract.Result
                || binding.Shape != contract.ResultShape
                || binding.Acquisition != RelationQuerySourceAcquisitionKind.BoundedLookup)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    $"Placement '{binding.Id.Value}' does not preserve traversal input '{contract.Input.Id.Value}'.",
                    contract.Input.Id,
                    binding.Id);
            }

            ValidateFields(contract.Fields, binding);
            RequireIdentity(binding, contract.Input.Id);
            if (!RelationQueryPhysicalReachability.TryGetPreservingInterveningTraversals(
                    plan,
                    contract,
                    out _))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.LoweringUnavailable,
                    "The v1 traversal lowerer cannot preserve logical reachability across an upstream row-selective or cardinality-changing node.",
                    contract.Input.Id,
                    binding.Id);
            }
            if (contract.Input.Direction == RelationshipTraversalDirection.Forward)
            {
                if (contract.Cardinality != RelationshipTraversalCardinality.AtMostOne)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.LoweringUnavailable,
                        "The v1 forward lookup lowerer supports only at-most-one traversals; forward-many acquisition requires a lossless mixed-batch evidence model.",
                        contract.Input.Id,
                        binding.Id);
                }
                if (contract.Definition.TargetKey is not ObservationIdentityRelationshipTargetKey)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.LoweringUnavailable,
                        "The v1 forward lookup lowerer supports only observation-identity target keys.",
                        contract.Input.Id,
                        binding.Id);
                }
                RequireCapabilities(
                    binding,
                    contract.Input.Id,
                    TraversalReadCapabilities(contract));
            }
            else
            {
                var key = binding.RelationshipKeys.SingleOrDefault(candidate => candidate.Input == contract.Input.Id);
                if (key is null || key.SemanticPath != contract.Definition.SourceReference)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                        "Inverse traversal placement requires the exact canonical source-reference selector.",
                        contract.Input.Id,
                        binding.Id);
                }
                RequireCapabilities(
                    binding,
                    contract.Input.Id,
                    TraversalReadCapabilities(contract));
            }
        }

        void ValidateFields(
            ImmutableArray<RelationQueryFieldInputContract> expected,
            RelationQuerySourcePlacementBinding binding)
        {
            var expectedById = expected.ToDictionary(static field => field.Input.Id);
            var actualById = binding.Fields.ToDictionary(static field => field.Input);
            if (!expectedById.Keys.ToHashSet().SetEquals(actualById.Keys))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    $"Placement '{binding.Id.Value}' must bind exactly the fields selected by the input contract.",
                    binding.Input,
                    binding.Id);
                return;
            }

            foreach (var (input, field) in expectedById)
            {
                if (actualById[input].SemanticPath != field.Input.Field.Path)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                        $"Placement field '{input.Value}' does not preserve its semantic path.",
                        input,
                        binding.Id);
                }
            }
        }

        void RequireIdentity(RelationQuerySourcePlacementBinding binding, RelationQueryInputId input)
        {
            if (binding.Identity is null || binding.Identity.Shape != binding.Shape)
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch,
                    $"Externally acquired placement '{binding.Id.Value}' requires identity metadata for '{binding.Shape}'.",
                    input,
                    binding.Id);
            }
        }

        void RequireCapabilities(
            RelationQuerySourcePlacementBinding binding,
            RelationQueryInputId input,
            params RelationQueryPrimitiveCapabilityKind[] capabilities)
        {
            var source = sources[binding.Source];
            foreach (var capability in capabilities.Distinct().Order())
            {
                var declared = source.TargetProfile.Capabilities.Any(evidence =>
                    evidence.Capability is PrimitiveRelationQueryCapability primitive
                    && primitive.Kind == capability);
                if (!declared)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.CapabilityEvidenceMissing,
                        $"Source '{source.Id.Value}' does not advertise primitive capability '{capability}'.",
                        input,
                        binding.Id);
                    continue;
                }

                if (!RelationQueryPhysicalBoundaryEvaluator.SelectCompatibleEvidence(
                        source,
                        binding,
                        policy,
                        [capability]).IsDefaultOrEmpty)
                {
                    continue;
                }

                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.OperatingBoundaryInvalid,
                    $"Source '{source.Id.Value}' advertises primitive capability '{capability}' only outside the selected physical operating boundaries.",
                    input,
                binding.Id);
            }
        }

        RelationQueryPrimitiveCapabilityKind[] SourceReadCapabilities(
            RelationQuerySourceInputContract contract)
        {
            List<RelationQueryPrimitiveCapabilityKind> capabilities =
            [
                RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
                RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead
            ];
            if (!contract.Fields.IsDefaultOrEmpty)
                capabilities.Add(RelationQueryPrimitiveCapabilityKind.FieldProjection);
            if (RequiresForwardRelationshipReference(contract.Binding))
                capabilities.Add(RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead);
            return [.. capabilities];
        }

        RelationQueryPrimitiveCapabilityKind[] TraversalReadCapabilities(
            RelationQueryTraversalInputContract contract)
        {
            List<RelationQueryPrimitiveCapabilityKind> capabilities =
            [
                contract.Input.Direction == RelationshipTraversalDirection.Forward
                    ? RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup
                    : RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup,
                RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead
            ];
            if (!contract.Fields.IsDefaultOrEmpty)
                capabilities.Add(RelationQueryPrimitiveCapabilityKind.FieldProjection);
            if (contract.Input.Direction == RelationshipTraversalDirection.Inverse
                || RequiresForwardRelationshipReference(contract.Result))
            {
                capabilities.Add(RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead);
            }
            return [.. capabilities];
        }

        bool RequiresForwardRelationshipReference(ValueBindingId binding) =>
            plan.InputContract.Traversals.Any(traversal =>
                traversal.From == binding
                && traversal.Input.Direction == RelationshipTraversalDirection.Forward);

        void ValidateUnsupportedNodes()
        {
            foreach (var node in plan.ExecutionSlice.Nodes.Select(static execution => execution.CanonicalNode))
            {
                if (node is TemporalJoinQueryNode)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.CrossSourceJoinUnsupported,
                        $"Temporal join '{node.Id.Value}' has no v1 federated acquisition lowering.");
                }
                else if (node is ExpandCollectionQueryNode)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.LocalWorkUnbounded,
                        $"Collection expansion '{node.Id.Value}' has no statically proven output-row bound for v1 local execution.");
                }
                else if (node is JoinQueryNode join && !TryGetEquijoin(join, out _, out _))
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.CrossSourceJoinUnsupported,
                        $"Join '{join.Id.Value}' is not a statically proven field-equality join.");
                }
            }
        }

        void LowerSource(RelationQuerySourceInputContract contract)
        {
            var binding = placements[contract.Input.Id];
            var baseKind = binding.Acquisition == RelationQuerySourceAcquisitionKind.Supplied
                ? RelationQueryPhysicalStageKind.SuppliedInput
                : RelationQueryPhysicalStageKind.SourceRead;
            var baseId = StageId(baseKind == RelationQueryPhysicalStageKind.SuppliedInput ? "supplied" : "source-read", contract.Input.Id.Value);
            var fields = contract.Fields.Select(static field => field.Input.Id).ToImmutableArray();
            var primitives = baseKind == RelationQueryPhysicalStageKind.SourceRead
                ? SourceReadCapabilities(contract)
                : [];
            stages.Add(new(
                baseId,
                baseKind,
                dependencies: [],
                binding.Id,
                semanticInputs: [contract.Input.Id, .. fields],
                requestedFields: baseKind == RelationQueryPhysicalStageKind.SourceRead ? fields : [],
                batchSize: null,
                Provenance(
                    [contract.Node],
                    [contract.Input.Id, .. fields],
                    binding.Id,
                    sources[binding.Source],
                    primitives,
                    baseKind == RelationQueryPhysicalStageKind.SourceRead
                        ? BoundedEnumerationLowering
                        : SuppliedInputLowering)));

            var terminal = baseId;
            if (!fields.IsDefaultOrEmpty)
            {
                terminal = StageId("exact-fields", contract.Input.Id.Value);
                stages.Add(new(
                    terminal,
                    RelationQueryPhysicalStageKind.ExactFieldProjection,
                    [baseId],
                    placementBinding: null,
                    semanticInputs: [contract.Input.Id, .. fields],
                    requestedFields: fields,
                    batchSize: null,
                    Provenance(
                        [contract.Node],
                        [contract.Input.Id, .. fields],
                        binding.Id,
                        sources[binding.Source],
                        primitives: [],
                        baseKind == RelationQueryPhysicalStageKind.SourceRead
                            ? BoundedEnumerationLowering
                            : SuppliedInputLowering)));
            }
            bindingProducers[contract.Binding] = terminal;
            sourceNodeProducers[contract.Node] = terminal;
        }

        void LowerTraversal(RelationQueryTraversalInputContract contract)
        {
            if (!bindingProducers.TryGetValue(contract.From, out var owner))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid,
                    $"Traversal '{contract.Input.Traversal.Value}' has no acquired producer for binding '{contract.From.Value}'.",
                    contract.Input.Id);
                return;
            }

            if (!RelationQueryPhysicalReachability.TryGetPreservingInterveningTraversals(
                    plan,
                    contract,
                    out var interveningTraversals))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid,
                    $"Traversal '{contract.Input.Traversal.Value}' has no proven v1 source-occurrence reachability chain.",
                    contract.Input.Id);
                return;
            }
            if (!interveningTraversals.IsDefaultOrEmpty
                && !traversalNodeProducers.TryGetValue(
                    interveningTraversals[^1].Input.Traversal,
                    out owner))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid,
                    $"Traversal '{contract.Input.Traversal.Value}' has no physical producer for its prior semantic traversal.",
                    contract.Input.Id);
                return;
            }

            var binding = placements[contract.Input.Id];
            var fields = contract.Fields.Select(static field => field.Input.Id).ToImmutableArray();
            var extraction = StageId("keys", contract.Input.Id.Value);
            stages.Add(new(
                extraction,
                RelationQueryPhysicalStageKind.RelationshipKeyExtraction,
                [owner],
                placementBinding: null,
                semanticInputs: [contract.Input.Id],
                requestedFields: [],
                batchSize: null,
                Provenance(
                    [contract.Input.Traversal],
                    [contract.Input.Id],
                    binding.Id,
                    sources[binding.Source],
                    primitives: [],
                    contract.Input.Direction == RelationshipTraversalDirection.Forward
                        ? ForwardRelationshipLowering
                        : InverseRelationshipLowering)));
            var dedupe = StageId("dedupe", contract.Input.Id.Value);
            stages.Add(new(
                dedupe,
                RelationQueryPhysicalStageKind.KeyDeduplication,
                [extraction],
                placementBinding: null,
                semanticInputs: [contract.Input.Id],
                requestedFields: [],
                batchSize: null,
                Provenance(
                    [contract.Input.Traversal],
                    [contract.Input.Id],
                    binding.Id,
                    sources[binding.Source],
                    primitives: [],
                    contract.Input.Direction == RelationshipTraversalDirection.Forward
                        ? ForwardRelationshipLowering
                        : InverseRelationshipLowering)));
            var source = sources[binding.Source];
            var batchSize = Math.Min(policy.MaximumBatchSize, source.Limits.MaximumBatchSize);
            var lookupKind = contract.Input.Direction == RelationshipTraversalDirection.Forward
                ? RelationQueryPhysicalStageKind.BatchedIdentityLookup
                : RelationQueryPhysicalStageKind.BatchedPredicateLookup;
            var lookup = StageId(lookupKind == RelationQueryPhysicalStageKind.BatchedIdentityLookup
                ? "identity-batch"
                : "predicate-batch", contract.Input.Id.Value);
            var primitives = TraversalReadCapabilities(contract);
            stages.Add(new(
                lookup,
                lookupKind,
                [dedupe],
                binding.Id,
                semanticInputs: [contract.Input.Id, .. fields],
                requestedFields: fields,
                batchSize,
                Provenance(
                    [contract.Input.Traversal],
                    [contract.Input.Id, .. fields],
                    binding.Id,
                    source,
                    primitives,
                    contract.Input.Direction == RelationshipTraversalDirection.Forward
                        ? ForwardRelationshipLowering
                        : InverseRelationshipLowering)));

            var lookupTerminal = lookup;
            if (!fields.IsDefaultOrEmpty)
            {
                lookupTerminal = StageId("exact-fields", $"traversal/{contract.Input.Id.Value}");
                stages.Add(new(
                    lookupTerminal,
                    RelationQueryPhysicalStageKind.ExactFieldProjection,
                    [lookup],
                    placementBinding: null,
                    semanticInputs: [contract.Input.Id, .. fields],
                    requestedFields: fields,
                    batchSize: null,
                    Provenance(
                        [contract.Input.Traversal],
                        [contract.Input.Id, .. fields],
                        binding.Id,
                        source,
                        primitives: [],
                        contract.Input.Direction == RelationshipTraversalDirection.Forward
                            ? ForwardRelationshipLowering
                            : InverseRelationshipLowering)));
            }

            var correlation = StageId("correlate", contract.Input.Id.Value);
            stages.Add(new(
                correlation,
                RelationQueryPhysicalStageKind.LocalCorrelation,
                [extraction, lookupTerminal],
                placementBinding: null,
                semanticInputs: [contract.Input.Id, .. fields],
                requestedFields: [],
                batchSize: null,
                Provenance(
                    [contract.Input.Traversal],
                    [contract.Input.Id, .. fields],
                    binding.Id,
                    source: null,
                    primitives: [],
                    contract.Input.Direction == RelationshipTraversalDirection.Forward
                        ? ForwardRelationshipLowering
                        : InverseRelationshipLowering)));
            bindingProducers[contract.Result] = correlation;
            traversalNodeProducers[contract.Input.Traversal] = correlation;
        }

        void LowerExplicitJoins()
        {
            foreach (var join in plan.ExecutionSlice.Nodes
                         .Select(static execution => execution.CanonicalNode)
                         .OfType<JoinQueryNode>()
                         .OrderBy(static join => join.Id.Value, StringComparer.Ordinal))
            {
                if (!TryGetEquijoin(join, out var leftField, out var rightField))
                    continue;
                if (!sourceNodeProducers.TryGetValue(join.Left, out var left)
                    || !sourceNodeProducers.TryGetValue(join.Right, out var right))
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.CrossSourceJoinUnsupported,
                        $"Join '{join.Id.Value}' v1 lowering requires two directly placed bounded source nodes.");
                    continue;
                }

                var fieldInputs = plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>().ToArray();
                var leftInput = fieldInputs.SingleOrDefault(input =>
                    input.Binding == leftField.Binding && input.Field.Path == leftField.Path);
                var rightInput = fieldInputs.SingleOrDefault(input =>
                    input.Binding == rightField.Binding && input.Field.Path == rightField.Path);
                if (leftInput is null || rightInput is null)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.CrossSourceJoinUnsupported,
                        $"Join '{join.Id.Value}' does not retain exact compiled field inputs for both equality keys.");
                    continue;
                }
                if (!IsIdentityField(leftInput) && !IsIdentityField(rightInput))
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.LocalWorkUnbounded,
                        $"Join '{join.Id.Value}' can multiply two non-unique rowsets; v1 local equijoin lowering requires an identity field on at least one side.");
                    continue;
                }

                var correlation = StageId("equijoin", join.Id.Value);
                stages.Add(new(
                    correlation,
                    RelationQueryPhysicalStageKind.LocalCorrelation,
                    [left, right],
                    placementBinding: null,
                    semanticInputs: [leftInput.Id, rightInput.Id],
                    requestedFields: [],
                    batchSize: null,
                    Provenance(
                        [join.Id],
                        [leftInput.Id, rightInput.Id],
                        placementBinding: null,
                        source: null,
                        primitives: [],
                        LocalEquijoinLowering)));
            }
        }

        RelationQueryPhysicalStageProvenance Provenance(
            IEnumerable<QueryNodeId> nodes,
            IEnumerable<RelationQueryInputId> inputs,
            RelationQuerySourcePlacementBindingId? placementBinding,
            RelationQuerySourceInstance? source,
            IEnumerable<RelationQueryPrimitiveCapabilityKind> primitives,
            RelationQueryPhysicalLoweringRuleId lowering,
            bool includeRealizationRules = true,
            bool includeAllRequirements = false)
        {
            var normalizedInputs = inputs.Distinct().OrderBy(static input => input.Value, StringComparer.Ordinal).ToImmutableArray();
            var normalizedNodes = nodes.Distinct().OrderBy(static node => node.Value, StringComparer.Ordinal).ToImmutableArray();
            var requirements = realization.Requirements
                .Where(requirement => includeAllRequirements
                    || requirement.Origin?.Input is { } input && normalizedInputs.Contains(input)
                    || requirement.Origin?.Node is { } node && normalizedNodes.Contains(node))
                .Select(static requirement => requirement.Id)
                .Distinct()
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var relevantDecisions = requirements
                .Where(decisions.ContainsKey)
                .Select(requirement => decisions[requirement])
                .ToArray();
            var compositionRules = includeRealizationRules
                ? relevantDecisions.SelectMany(static decision => decision.GetCompositionRules()).Distinct()
                    .OrderBy(static rule => rule.Value, StringComparer.Ordinal).ToImmutableArray()
                : [];
            foreach (var rule in compositionRules)
            {
                var selected = policy.LoweringSelections.SingleOrDefault(candidate => candidate.CompositionRule == rule);
                if (selected is null || selected.PhysicalLowering != lowering)
                {
                    Error(
                        RelationQueryPhysicalPlanningDiagnosticCodes.LoweringUnavailable,
                        $"Selected realization rule '{rule.Value}' has no matching '{lowering.Value}' physical lowering.");
                }
            }
            var realizationBoundaries = includeRealizationRules
                ? relevantDecisions.SelectMany(static decision => decision.GetBoundaryValidations())
                    .Select(static validation => validation.Boundary)
                    .Distinct()
                    .OrderBy(static boundary => boundary.Value, StringComparer.Ordinal).ToImmutableArray()
                : [];
            ImmutableArray<RelationQueryTargetCapabilityEvidence> selectedEvidence = source is null
                ? []
                : RelationQueryPhysicalBoundaryEvaluator.SelectCompatibleEvidence(
                    source,
                    placementBinding is { } bindingId
                        ? placement.Bindings.Single(binding => binding.Id == bindingId)
                        : throw new InvalidOperationException("Source capability provenance requires a placement binding."),
                    policy,
                    primitives);
            var evidence = selectedEvidence
                .Select(item => new RelationQueryPhysicalCapabilityEvidenceReference(
                    source!.Id,
                    source.TargetProfile.Target,
                    source.TargetProfile.Id,
                    item.Id))
                .OrderBy(static item => item.Evidence.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var evidenceBoundaries = selectedEvidence
                .SelectMany(static item => item.OperatingBoundaries)
                .Distinct()
                .OrderBy(static boundary => boundary.Value, StringComparer.Ordinal);
            var boundaries = realizationBoundaries.Concat(evidenceBoundaries)
                .Distinct()
                .OrderBy(static boundary => boundary.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (source is not null
                && primitives.Distinct().Any(primitive => !selectedEvidence.Any(item =>
                    item.Capability is PrimitiveRelationQueryCapability selected
                    && selected.Kind == primitive)))
            {
                Error(
                    RelationQueryPhysicalPlanningDiagnosticCodes.OperatingBoundaryInvalid,
                    $"Source '{source.Id.Value}' cannot prove every primitive used by physical lowering '{lowering.Value}' within the selected operating boundaries.");
            }
            return new(
                normalizedNodes,
                normalizedInputs,
                requirements,
                evidence,
                compositionRules,
                boundaries,
                placementBinding is { } binding ? [binding] : [],
                lowering,
                [new RelationQueryPhysicalPlanningDecisionId($"policy/{policy.Id.Value}/{lowering.Value}")]);
        }

        bool IsIdentityField(RelationQueryFieldInput input)
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

        static bool TryGetEquijoin(JoinQueryNode join, out FieldExpr left, out FieldExpr right)
        {
            if (join.Predicate is BinaryExpr
                {
                    Operator: BinaryOperator.Eq,
                    Left: FieldExpr { Binding: not null } candidateLeft,
                    Right: FieldExpr { Binding: not null } candidateRight
                }
                && candidateLeft.Binding != candidateRight.Binding)
            {
                left = candidateLeft;
                right = candidateRight;
                return true;
            }
            left = null!;
            right = null!;
            return false;
        }

        static RelationQueryPhysicalStageId StageId(string kind, string semanticIdentity) =>
            new($"physical/{kind}/{Uri.EscapeDataString(semanticIdentity)}");

        bool HasErrors => diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        RelationQueryPhysicalPlanningStatus CurrentFailureStatus() => diagnostics.Any(static diagnostic =>
            diagnostic.Code is RelationQueryPhysicalPlanningDiagnosticCodes.PlacementAmbiguous
                or RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch
                or RelationQueryPhysicalPlanningDiagnosticCodes.RealizationInvalid
                or RelationQueryPhysicalPlanningDiagnosticCodes.PolicyInvalid
                or RelationQueryPhysicalPlanningDiagnosticCodes.StageProvenanceInvalid)
            ? RelationQueryPhysicalPlanningStatus.Invalid
            : RelationQueryPhysicalPlanningStatus.Unavailable;

        void Error(
            string code,
            string message,
            RelationQueryInputId? input = null,
            RelationQuerySourcePlacementBindingId? placementBinding = null) =>
            diagnostics.Add(new(
                code,
                DiagnosticSeverity.Error,
                message,
                input,
                placementBinding: placementBinding));

        RelationQueryPhysicalPlanningResult Failure(RelationQueryPhysicalPlanningStatus status) =>
            new(status, plan: null, [.. diagnostics]);
    }
}
