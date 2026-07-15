using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Diagnostics;

/// <summary>
/// Interprets a compiled relation/query input contract against explicit runtime evidence.
/// </summary>
public static class RelationRequirementGapAnalyzer
{
    /// <summary>Analyzes runtime input availability without fetching data or executing the logical plan.</summary>
    /// <param name="plan">Successful target-independent static compilation to interpret.</param>
    /// <param name="evidence">Runtime evidence snapshot for one evaluation.</param>
    /// <param name="policy">
    /// Policy used to disposition and report affected outputs, or <see langword="null"/> to use
    /// <see cref="RelationRequirementGapPolicy.Conventional"/>.
    /// </param>
    /// <returns>
    /// Immutable evidence diagnostics, causal requirement gaps, and per-impact policy decisions in deterministic order.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="policy"/> exposes a default or empty policy identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> exposes an unsupported policy source.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="policy"/> returns no choice for an impact, or a candidate plan's shape snapshot
    /// cannot be represented by the compiled-plan canonicalization profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A candidate plan's shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A candidate plan's shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    /// <remarks>
    /// Exceptions thrown by a caller-supplied policy delegate propagate to the caller. Evidence inconsistencies are
    /// returned as structured diagnostics and do not throw.
    /// </remarks>
    public static RelationRequirementGapAnalysisResult Analyze(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence,
        IRelationRequirementGapPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evidence);
        var selectedPolicy = policy ?? RelationRequirementGapPolicy.Conventional;
        if (string.IsNullOrWhiteSpace(selectedPolicy.Id.Value))
        {
            throw new ArgumentException("A relation requirement gap policy requires a stable identity.", nameof(policy));
        }

        if (!Enum.IsDefined(selectedPolicy.Source))
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                selectedPolicy.Source,
                "Unsupported relation requirement gap policy source.");
        }

        return new Analysis(plan, evidence, selectedPolicy).Run();
    }

    sealed class Analysis
    {
        readonly CompiledRelationQueryPlan plan;
        readonly RelationQueryRuntimeEvidence evidence;
        readonly IRelationRequirementGapPolicy policy;
        readonly Dictionary<RelationQueryInputId, RelationQueryRequirementInput> inputs;
        readonly Dictionary<RelationQueryInputId, RelationQueryDependencyEntry> dependencies;
        readonly Dictionary<RelationQueryInputId, RelationQuerySourceInputContract> sourceContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryTraversalInputContract> traversalContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryFieldInputContract> fieldContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryIdentityInputContract> identityContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryParameterInputContract> parameterContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryCapabilityInputContract> capabilityContracts;
        readonly Dictionary<RelationQueryInputId, RelationQueryFieldInputContract> forwardReferenceFields = [];
        readonly Dictionary<RelationQueryInputId, RelationQueryIdentityInputContract> inverseAnchorIdentities = [];
        readonly Dictionary<RelationQueryInputId, RelationQuerySourceEvidence> sources = [];
        readonly Dictionary<(RelationQueryInputId Input, RelationQueryOccurrenceId Owner), RelationQueryFieldEvidence> fields = [];
        readonly Dictionary<(RelationQueryInputId Input, RelationQueryOccurrenceId From), RelationQueryTraversalEvidence> traversals = [];
        readonly Dictionary<RelationQueryInputId, RelationQueryParameterEvidence> parameters = [];
        readonly Dictionary<RelationQueryInputId, RelationQueryCapabilityEvidence> capabilities = [];
        readonly Dictionary<(RelationQueryInputId Input, string Occurrence), RelationQueryConversionFailureEvidence> conversions = [];
        readonly Dictionary<RelationQueryOccurrenceId, RelationQueryObservationOccurrence> occurrences = [];
        readonly List<RelationRuntimeDiagnostic> diagnostics = [];
        readonly Dictionary<(string Occurrence, RelationQueryInputId Input, RelationRequirementGapCause Cause), RelationRequirementGap> gaps = [];
        readonly HashSet<(RelationQueryInputId Input, RelationQueryOccurrenceId Owner)> processedFields = [];
        readonly HashSet<(RelationQueryInputId Input, RelationQueryOccurrenceId Owner)> processedIdentities = [];
        readonly HashSet<(RelationQueryInputId Input, string Occurrence)> processedConversions = [];
        readonly HashSet<RelationQueryOccurrenceId> blockedOccurrences = [];
        readonly Dictionary<RelationQueryInputId, ImmutableArray<RelationQueryInputId>> traversalDescendantCache = [];

        public Analysis(
            CompiledRelationQueryPlan plan,
            RelationQueryRuntimeEvidence evidence,
            IRelationRequirementGapPolicy policy)
        {
            this.plan = plan;
            this.evidence = evidence;
            this.policy = policy;
            inputs = plan.RequirementGraph.Inputs.ToDictionary(static input => input.Id);
            dependencies = plan.DependencyManifest.Entries.ToDictionary(static entry => entry.Input.Id);
            sourceContracts = plan.InputContract.Sources.ToDictionary(static source => source.Input.Id);
            traversalContracts = plan.InputContract.Traversals.ToDictionary(static traversal => traversal.Input.Id);
            fieldContracts = plan.InputContract.Sources.SelectMany(static source => source.Fields)
                .Concat(plan.InputContract.Traversals.SelectMany(static traversal => traversal.Fields))
                .ToDictionary(static field => field.Input.Id);
            identityContracts = plan.InputContract.Identities.ToDictionary(static identity => identity.Input.Id);
            parameterContracts = plan.InputContract.Parameters.ToDictionary(static parameter => parameter.Input.Id);
            capabilityContracts = plan.InputContract.Capabilities.ToDictionary(static capability => capability.Input.Id);
        }

        public RelationRequirementGapAnalysisResult Run()
        {
            ValidateEvidence();
            ValidateTopology();
            ValidateForwardTraversalCorrelations();
            if (HasEvidenceErrors())
            {
                return new(
                    isEvidenceValid: false,
                    isConclusive: false,
                    [],
                    [],
                    NormalizeDiagnostics(diagnostics));
            }

            AnalyzeConversionFailures();
            AnalyzeSources();
            AnalyzeTraversals();
            AnalyzeFields();
            AnalyzeIdentities();
            AnalyzeParameters();
            AnalyzeCapabilities();

            var normalizedGaps = gaps.Values
                .OrderBy(static gap => gap.Occurrence?.Id.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static gap => gap.Input.Id.Value, StringComparer.Ordinal)
                .ThenBy(static gap => (int)gap.Cause)
                .ThenBy(static gap => gap.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var decisions = ProjectPolicy(normalizedGaps);
            var isConclusive = evidence.Completeness == RelationQueryEvidenceCompleteness.Complete
                && sources.Values.All(static source =>
                    source.State != RelationQuerySourceEvidenceState.Inconclusive
                    && (source.State != RelationQuerySourceEvidenceState.Provided
                        || source.Completeness == RelationQueryEvidenceCompleteness.Complete))
                && fields.Values.All(static field =>
                    field.State != RelationQueryFieldEvidenceState.Inconclusive)
                && traversals.Values.All(static traversal =>
                    traversal.State != RelationQueryTraversalEvidenceState.Inconclusive
                    && (traversal.State != RelationQueryTraversalEvidenceState.Completed
                        || traversal.Completeness == RelationQueryEvidenceCompleteness.Complete));
            return new(
                isEvidenceValid: true,
                isConclusive,
                normalizedGaps,
                decisions,
                NormalizeDiagnostics(diagnostics));
        }

        void ValidateEvidence()
        {
            var planMismatches = evidence.PlanReference.GetMismatchedComponents(plan);
            if (!planMismatches.IsDefaultOrEmpty)
            {
                AddDiagnostic(
                    RelationRuntimeDiagnosticCodes.PlanMismatch,
                    $"Runtime evidence belongs to a different compiled relation/query input contract. Mismatched components: {string.Join(", ", planMismatches)}.");
            }

            foreach (var source in QuarantineDuplicateEvidence(
                         evidence.Sources,
                         static source => source.Input,
                         static source => source.Input,
                         static _ => null,
                         "source"))
            {
                if (!ValidateInputKind<RelationQuerySourceSetInput>(source.Input, "source", null))
                {
                    continue;
                }

                sources.Add(source.Input, source);

                var contract = sourceContracts[source.Input];
                foreach (var occurrence in source.Occurrences)
                {
                    RegisterOccurrence(occurrence, source.Input, source.EvidenceReference);
                    if (occurrence.Binding != contract.Binding || occurrence.Shape != contract.Shape)
                    {
                        AddDiagnostic(
                            RelationRuntimeDiagnosticCodes.EvidenceConflict,
                            $"Source occurrence '{occurrence.Id.Value}' does not match compiled binding '{contract.Binding.Value}' and shape '{contract.Shape}'.",
                            source.Input,
                            occurrence.Id,
                            source.EvidenceReference);
                    }
                }
            }

            foreach (var traversal in QuarantineDuplicateEvidence(
                         evidence.Traversals,
                         static traversal => (traversal.Input, traversal.From),
                         static traversal => traversal.Input,
                         static traversal => traversal.From,
                         "traversal"))
            {
                if (!ValidateInputKind<RelationQueryRelationshipInput>(traversal.Input, "traversal", traversal.From))
                {
                    continue;
                }

                traversals.Add((traversal.Input, traversal.From), traversal);

                var contract = traversalContracts[traversal.Input];
                foreach (var result in traversal.Results)
                {
                    RegisterOccurrence(result, traversal.Input, traversal.EvidenceReference);
                    if (result.Binding != contract.Result || result.Shape != contract.ResultShape)
                    {
                        AddDiagnostic(
                            RelationRuntimeDiagnosticCodes.EvidenceConflict,
                            $"Traversal result occurrence '{result.Id.Value}' does not match compiled result binding '{contract.Result.Value}' and shape '{contract.ResultShape}'.",
                            traversal.Input,
                            result.Id,
                            traversal.EvidenceReference);
                    }
                }
            }

            foreach (var traversal in traversals.Values)
            {
                var contract = traversalContracts[traversal.Input];
                if (!occurrences.TryGetValue(traversal.From, out var from))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Traversal evidence references unknown source occurrence '{traversal.From.Value}'.",
                        traversal.Input,
                        traversal.From,
                        traversal.EvidenceReference);
                    continue;
                }
                if (from.Binding != contract.From || from.Shape != contract.FromShape)
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Traversal source occurrence '{from.Id.Value}' does not match compiled binding '{contract.From.Value}' and shape '{contract.FromShape}'.",
                        traversal.Input,
                        from.Id,
                        traversal.EvidenceReference);
                }
            }

            foreach (var field in QuarantineDuplicateEvidence(
                         evidence.Fields,
                         static field => (field.Input, field.Owner),
                         static field => field.Input,
                         static field => field.Owner,
                         "field"))
            {
                if (!ValidateInputKind<RelationQueryFieldInput>(field.Input, "field", field.Owner))
                {
                    continue;
                }

                fields.Add((field.Input, field.Owner), field);

                var contract = fieldContracts[field.Input].Input;
                if (!occurrences.TryGetValue(field.Owner, out var owner))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Field evidence references unknown owner occurrence '{field.Owner.Value}'.",
                        field.Input,
                        field.Owner,
                        field.EvidenceReference);
                    continue;
                }
                if (owner.Binding != contract.Binding || owner.Shape != contract.Field.Shape)
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Field owner occurrence '{owner.Id.Value}' does not match compiled binding '{contract.Binding.Value}' and shape '{contract.Field.Shape}'.",
                        field.Input,
                        owner.Id,
                        field.EvidenceReference);
                }
                if (field.State == RelationQueryFieldEvidenceState.Value
                    && contract.ValueContract is { } valueContract
                    && field.Value is { } observed
                    && !valueContract.IsSatisfiedByConstant(observed))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.ValueContractMismatch,
                        $"Field evidence for input '{field.Input.Value}' does not satisfy its compiled value contract.",
                        field.Input,
                        field.Owner,
                        field.EvidenceReference);
                }
            }

            foreach (var parameter in QuarantineDuplicateEvidence(
                         evidence.Parameters,
                         static parameter => parameter.Input,
                         static parameter => parameter.Input,
                         static _ => null,
                         "parameter"))
            {
                if (!ValidateInputKind<RelationQueryParameterInput>(parameter.Input, "parameter", null))
                {
                    continue;
                }

                parameters.Add(parameter.Input, parameter);

                var contract = parameterContracts[parameter.Input];
                if (parameter.State == RelationQueryParameterEvidenceState.Provided
                    && parameter.Value is { } value
                    && !contract.ValueContract.IsSatisfiedByConstant(value))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.ValueContractMismatch,
                        $"Parameter evidence for input '{parameter.Input.Value}' does not satisfy its compiled value contract.",
                        parameter.Input,
                        occurrence: null,
                        parameter.EvidenceReference);
                }
            }

            foreach (var capability in QuarantineDuplicateEvidence(
                         evidence.Capabilities,
                         static capability => capability.Input,
                         static capability => capability.Input,
                         static _ => null,
                         "capability"))
            {
                if (!ValidateInputKind<RelationQueryCapabilityInput>(capability.Input, "capability", null))
                {
                    continue;
                }

                capabilities.Add(capability.Input, capability);
            }

            foreach (var conversion in QuarantineDuplicateEvidence(
                         evidence.ConversionFailures,
                         static conversion => (conversion.Input, conversion.Occurrence?.Value ?? string.Empty),
                         static conversion => conversion.Input,
                         static conversion => conversion.Occurrence,
                         "conversion"))
            {
                if (!inputs.TryGetValue(conversion.Input, out var input))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.InputUnknown,
                        $"Conversion evidence references unknown compiled input '{conversion.Input.Value}'.",
                        conversion.Input,
                        conversion.Occurrence,
                        conversion.EvidenceReference);
                    continue;
                }

                var occurrenceKey = conversion.Occurrence?.Value ?? string.Empty;
                conversions.Add((conversion.Input, occurrenceKey), conversion);

                var occurrenceRequired = input is RelationQueryFieldInput
                    or RelationQueryObservationIdentityInput
                    or RelationQueryRelationshipInput;
                if (occurrenceRequired != (conversion.Occurrence is not null))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        occurrenceRequired
                            ? $"Conversion evidence for input '{conversion.Input.Value}' requires a binding occurrence."
                            : $"Conversion evidence for evaluation-wide input '{conversion.Input.Value}' cannot declare a binding occurrence.",
                        conversion.Input,
                        conversion.Occurrence,
                        conversion.EvidenceReference);
                    continue;
                }
                if (conversion.Occurrence is not { } occurrence)
                {
                    continue;
                }

                if (!occurrences.TryGetValue(occurrence, out var owner))
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Conversion evidence references unknown occurrence '{occurrence.Value}'.",
                        conversion.Input,
                        occurrence,
                        conversion.EvidenceReference);
                    continue;
                }

                var ownsInput = input switch
                {
                    RelationQueryFieldInput field =>
                        owner.Binding == field.Binding && owner.Shape == field.Field.Shape,
                    RelationQueryObservationIdentityInput identity =>
                        owner.Binding == identity.Binding && owner.Shape == identity.Shape,
                    RelationQueryRelationshipInput relationship =>
                        owner.Binding == relationship.From && owner.Shape == relationship.FromShape,
                    _ => true
                };
                if (!ownsInput)
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Occurrence '{occurrence.Value}' does not own compiled input '{conversion.Input.Value}'.",
                        conversion.Input,
                        occurrence,
                        conversion.EvidenceReference);
                }
            }
        }

        void ValidateTopology()
        {
            var allFields = fieldContracts.Values.ToArray();
            foreach (var traversal in traversalContracts.Values)
            {
                if (traversal.Input.Direction == RelationshipTraversalDirection.Forward)
                {
                    var matches = allFields.Where(field =>
                            field.Input.Binding == traversal.From
                            && field.Input.Field.Shape == traversal.Definition.SourceShape
                            && field.Input.Field.Path == traversal.Definition.SourceReference)
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        AddDiagnostic(
                            RelationRuntimeDiagnosticCodes.EvidenceConflict,
                            $"Compiled traversal input '{traversal.Input.Id.Value}' does not have one unambiguous source-reference field input.",
                            traversal.Input.Id);
                        continue;
                    }
                    forwardReferenceFields.Add(traversal.Input.Id, matches[0]);
                    continue;
                }

                var anchors = identityContracts.Values.Where(identity =>
                        identity.Input.Binding == traversal.From
                        && identity.Input.Shape == traversal.FromShape)
                    .ToArray();
                if (anchors.Length != 1)
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Compiled inverse traversal input '{traversal.Input.Id.Value}' does not have one unambiguous source-identity input.",
                        traversal.Input.Id);
                    continue;
                }
                inverseAnchorIdentities.Add(traversal.Input.Id, anchors[0]);
            }
        }

        void ValidateForwardTraversalCorrelations()
        {
            foreach (var contract in traversalContracts.Values.Where(static item =>
                         item.Input.Direction == RelationshipTraversalDirection.Forward))
            {
                if (!forwardReferenceFields.TryGetValue(contract.Input.Id, out var reference))
                {
                    continue;
                }

                foreach (var observedTraversal in traversals.Values.Where(item =>
                             item.Input == contract.Input.Id
                             && item.State == RelationQueryTraversalEvidenceState.Completed))
                {
                    if (contract.Cardinality == RelationshipTraversalCardinality.AtMostOne
                        && observedTraversal.Results.Length > 1)
                    {
                        continue;
                    }

                    if (!fields.TryGetValue((reference.Input.Id, observedTraversal.From), out var observedReference)
                        || observedReference.State != RelationQueryFieldEvidenceState.Value
                        || observedReference.Value is not { } referenceValue
                        || !TryGetReferenceIdentities(contract.Cardinality, referenceValue, out var expected))
                    {
                        continue;
                    }

                    var remainingReferences = expected.ToHashSet(StringComparer.Ordinal);
                    var unmatchedKnownIdentities = 0;
                    foreach (var result in observedTraversal.Results)
                    {
                        if (result.ObservationIdentity is { } identity
                            && !remainingReferences.Remove(identity))
                        {
                            unmatchedKnownIdentities++;
                        }
                    }

                    var resultCountOverflow = Math.Max(0, observedTraversal.Results.Length - expected.Count);
                    var unaddressedCount = Math.Max(unmatchedKnownIdentities, resultCountOverflow);
                    if (unaddressedCount == 0)
                    {
                        continue;
                    }

                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.EvidenceConflict,
                        $"Completed forward traversal evidence contains {unaddressedCount.ToString(CultureInfo.InvariantCulture)} "
                        + (unaddressedCount == 1 ? "result occurrence" : "result occurrences")
                        + " that cannot be correlated to the loaded relationship reference.",
                        contract.Input.Id,
                        observedTraversal.From,
                        observedTraversal.EvidenceReference);
                }
            }
        }

        void AnalyzeConversionFailures()
        {
            PreblockCausalConversionDescendants();

            foreach (var conversion in conversions.Values
                         .OrderBy(static item => item.Occurrence?.Value ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(static item => item.Input.Value, StringComparer.Ordinal))
            {
                var occurrence = conversion.Occurrence is { } occurrenceId
                    ? occurrences[occurrenceId]
                    : null;
                if (occurrence is not null && blockedOccurrences.Contains(occurrence.Id))
                {
                    processedConversions.Add((conversion.Input, occurrence.Id.Value));
                    continue;
                }
                if (IsPrunedCorrelationConversion(conversion))
                {
                    processedConversions.Add((conversion.Input, conversion.Occurrence?.Value ?? string.Empty));
                    continue;
                }

                var blocked = GetBlockedInputs(conversion.Input);
                RelationRequirementGapValueContext? valueContext = null;
                RelationRequirementGapRelationshipContext? relationshipContext = null;
                if (fieldContracts.TryGetValue(conversion.Input, out var field))
                {
                    fields.TryGetValue((conversion.Input, occurrence!.Id), out var observed);
                    valueContext = new(
                        field.Input.Field,
                        field.Input.ValueContract,
                        observed?.State ?? RelationQueryFieldEvidenceState.Failed,
                        observed?.Value,
                        conversion.EvidenceReference);
                }
                if (traversalContracts.TryGetValue(conversion.Input, out var traversal))
                {
                    traversals.TryGetValue((conversion.Input, occurrence!.Id), out var observed);
                    relationshipContext = CreateRelationshipContext(traversal, observed, referenceValue: null);
                    BlockResults(observed);
                }
                if (sourceContracts.ContainsKey(conversion.Input)
                    && sources.TryGetValue(conversion.Input, out var observedSource)
                    && observedSource.State == RelationQuerySourceEvidenceState.Provided)
                {
                    BlockOccurrences(observedSource.Occurrences.Select(static item => item.Id));
                }

                AddGap(
                    inputs[conversion.Input],
                    occurrence,
                    RelationRequirementGapCause.ConversionFailure,
                    blocked,
                    valueContext,
                    relationshipContext,
                    conversion.EvidenceReference);
                processedConversions.Add((conversion.Input, conversion.Occurrence?.Value ?? string.Empty));
            }
        }

        void PreblockCausalConversionDescendants()
        {
            foreach (var conversion in conversions.Values)
            {
                if (IsPrunedCorrelationConversion(conversion))
                {
                    continue;
                }

                if (conversion.Occurrence is not { } occurrence)
                {
                    if (sourceContracts.ContainsKey(conversion.Input)
                        && sources.TryGetValue(conversion.Input, out var observedSource)
                        && observedSource.State == RelationQuerySourceEvidenceState.Provided)
                    {
                        BlockOccurrences(observedSource.Occurrences.Select(static item => item.Id));
                    }
                    continue;
                }

                if (traversalContracts.ContainsKey(conversion.Input))
                {
                    if (traversals.TryGetValue((conversion.Input, occurrence), out var observedTraversal))
                    {
                        BlockResults(observedTraversal);
                    }
                    continue;
                }

                foreach (var traversal in forwardReferenceFields
                             .Where(pair => pair.Value.Input.Id == conversion.Input)
                             .Select(static pair => pair.Key)
                             .Concat(inverseAnchorIdentities
                                 .Where(pair => pair.Value.Input.Id == conversion.Input)
                                 .Select(static pair => pair.Key)))
                {
                    if (traversals.TryGetValue((traversal, occurrence), out var observedTraversal))
                    {
                        BlockResults(observedTraversal);
                    }
                }
            }
        }

        void AnalyzeSources()
        {
            foreach (var contract in sourceContracts.Values.OrderBy(static item => item.Input.Id.Value, StringComparer.Ordinal))
            {
                if (IsConversionHandled(contract.Input.Id, null))
                {
                    continue;
                }

                sources.TryGetValue(contract.Input.Id, out var observed);
                if (observed is null && evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                {
                    continue;
                }

                if (observed is
                    {
                        State: RelationQuerySourceEvidenceState.Provided,
                        Completeness: RelationQueryEvidenceCompleteness.Partial
                    })
                {
                    AddDiagnostic(
                        RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive,
                        $"Source input '{contract.Input.Id.Value}' has partial results and cannot establish authoritative source-set completeness.",
                        contract.Input.Id,
                        evidenceReference: observed.EvidenceReference,
                        severity: DiagnosticSeverity.Warning);
                    continue;
                }

                var cause = observed?.State switch
                {
                    null => RelationRequirementGapCause.InputNotProvided,
                    RelationQuerySourceEvidenceState.NotProvided => RelationRequirementGapCause.InputNotProvided,
                    RelationQuerySourceEvidenceState.Failed => RelationRequirementGapCause.InputAcquisitionFailed,
                    RelationQuerySourceEvidenceState.Inconclusive =>
                        RelationRequirementGapCause.InputAcquisitionInconclusive,
                    RelationQuerySourceEvidenceState.Provided => (RelationRequirementGapCause?)null,
                    _ => throw new ArgumentOutOfRangeException()
                };
                if (cause is null)
                {
                    continue;
                }

                AddGap(
                    contract.Input,
                    occurrence: null,
                    cause.Value,
                    GetSourceDescendants(contract),
                    valueContext: null,
                    relationshipContext: null,
                    observed?.EvidenceReference);
            }
        }

        void AnalyzeTraversals()
        {
            foreach (var contract in GetTraversalOrder())
            {
                var owners = occurrences.Values
                    .Where(occurrence => occurrence.Binding == contract.From && occurrence.Shape == contract.FromShape)
                    .OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal)
                    .ToArray();
                foreach (var owner in owners)
                {
                    if (blockedOccurrences.Contains(owner.Id))
                    {
                        continue;
                    }

                    traversals.TryGetValue((contract.Input.Id, owner.Id), out var observedTraversal);
                    if (observedTraversal?.State == RelationQueryTraversalEvidenceState.NotApplicable)
                    {
                        if (contract.Input.Direction == RelationshipTraversalDirection.Forward
                            && forwardReferenceFields.TryGetValue(contract.Input.Id, out var inapplicableReference)
                            && !HasIndependentImpact(inapplicableReference.Input.Id))
                        {
                            processedFields.Add((inapplicableReference.Input.Id, owner.Id));
                        }
                        else if (contract.Input.Direction == RelationshipTraversalDirection.Inverse
                                 && inverseAnchorIdentities.TryGetValue(contract.Input.Id, out var inapplicableIdentity)
                                 && !HasIndependentImpact(inapplicableIdentity.Input.Id))
                        {
                            processedIdentities.Add((inapplicableIdentity.Input.Id, owner.Id));
                        }
                        continue;
                    }
                    if (IsConversionHandled(contract.Input.Id, owner.Id))
                    {
                        BlockResults(observedTraversal);
                        continue;
                    }

                    ObservationValue? referenceValue = null;
                    if (contract.Input.Direction == RelationshipTraversalDirection.Forward)
                    {
                        var reference = forwardReferenceFields[contract.Input.Id];
                        processedFields.Add((reference.Input.Id, owner.Id));
                        if (IsConversionHandled(reference.Input.Id, owner.Id))
                        {
                            BlockResults(observedTraversal);
                            continue;
                        }

                        fields.TryGetValue((reference.Input.Id, owner.Id), out var observedReference);
                        if (observedReference is null
                            && evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                        {
                            if (observedTraversal is null)
                            {
                                continue;
                            }
                        }
                        else if (TryGetReferenceGapCause(observedReference, out var referenceCause))
                        {
                            var blocked = Prepend(
                                contract.Input.Id,
                                GetTraversalDescendants(contract));
                            AddGap(
                                reference.Input,
                                owner,
                                referenceCause,
                                blocked,
                                new(
                                    reference.Input.Field,
                                    reference.Input.ValueContract,
                                    observedReference?.State ?? RelationQueryFieldEvidenceState.NotLoaded,
                                    observedReference?.Value,
                                    observedReference?.EvidenceReference),
                                CreateRelationshipContext(contract, observedTraversal, referenceValue: null),
                                observedReference?.EvidenceReference);
                            BlockResults(observedTraversal);
                            continue;
                        }
                        else if (observedReference?.Value is { } loadedReference)
                        {
                            referenceValue = loadedReference;
                        }
                    }
                    else
                    {
                        var identity = inverseAnchorIdentities[contract.Input.Id];
                        processedIdentities.Add((identity.Input.Id, owner.Id));
                        if (IsConversionHandled(identity.Input.Id, owner.Id))
                        {
                            BlockResults(observedTraversal);
                            continue;
                        }
                        if (owner.ObservationIdentity is null)
                        {
                            AddGap(
                                identity.Input,
                                owner,
                                RelationRequirementGapCause.ObservationIdentityMissing,
                                Prepend(contract.Input.Id, GetTraversalDescendants(contract)),
                                valueContext: null,
                                CreateRelationshipContext(contract, observedTraversal, referenceValue: null),
                                evidenceReference: null);
                            BlockResults(observedTraversal);
                            continue;
                        }
                    }

                    if (observedTraversal is null)
                    {
                        if (evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                        {
                            continue;
                        }

                        AddTraversalGap(
                            contract,
                            owner,
                            RelationRequirementGapCause.ResolutionNotAttempted,
                            observedTraversal,
                            referenceValue);
                        continue;
                    }

                    switch (observedTraversal.State)
                    {
                        case RelationQueryTraversalEvidenceState.NotApplicable:
                            break;
                        case RelationQueryTraversalEvidenceState.NotAttempted:
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.ResolutionNotAttempted,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Failed:
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.ResolutionFailed,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Rejected:
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.RelatedObservationRejected,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Inconclusive:
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.InputAcquisitionInconclusive,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Completed
                            when contract.Cardinality == RelationshipTraversalCardinality.AtMostOne
                                 && observedTraversal.Results.Length > 1:
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.CardinalityViolation,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Completed
                            when contract.Input.Direction == RelationshipTraversalDirection.Forward
                                 && observedTraversal.Completeness == RelationQueryEvidenceCompleteness.Complete
                                 && HasMissingForwardReference(
                                     contract,
                                     observedTraversal,
                                     referenceValue):
                            AddTraversalGap(
                                contract,
                                owner,
                                RelationRequirementGapCause.RelatedObservationNotFound,
                                observedTraversal,
                                referenceValue);
                            break;
                        case RelationQueryTraversalEvidenceState.Completed:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        void AnalyzeFields()
        {
            foreach (var contract in fieldContracts.Values.OrderBy(static item => item.Input.Id.Value, StringComparer.Ordinal))
            {
                var owners = occurrences.Values
                    .Where(occurrence => occurrence.Binding == contract.Input.Binding
                                         && occurrence.Shape == contract.Input.Field.Shape)
                    .OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal);
                foreach (var owner in owners)
                {
                    if (blockedOccurrences.Contains(owner.Id)
                        || processedFields.Contains((contract.Input.Id, owner.Id))
                        || IsConversionHandled(contract.Input.Id, owner.Id))
                    {
                        continue;
                    }

                    fields.TryGetValue((contract.Input.Id, owner.Id), out var observed);
                    if (observed is null && evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                    {
                        continue;
                    }

                    var state = observed?.State ?? RelationQueryFieldEvidenceState.NotLoaded;
                    var cause = state switch
                    {
                        RelationQueryFieldEvidenceState.Value => (RelationRequirementGapCause?)null,
                        RelationQueryFieldEvidenceState.Null
                            when contract.Input.ValueContract?.Nullability == FieldNullability.NonNullable =>
                            RelationRequirementGapCause.RequiredValueNull,
                        RelationQueryFieldEvidenceState.Null => null,
                        RelationQueryFieldEvidenceState.Missing
                            when contract.Input.ValueContract?.Presence == FieldPresence.Required =>
                            RelationRequirementGapCause.RequiredValueMissing,
                        RelationQueryFieldEvidenceState.Missing => null,
                        RelationQueryFieldEvidenceState.NotLoaded => RelationRequirementGapCause.RequiredFieldNotLoaded,
                        RelationQueryFieldEvidenceState.Failed => RelationRequirementGapCause.InputAcquisitionFailed,
                        RelationQueryFieldEvidenceState.Inconclusive =>
                            RelationRequirementGapCause.InputAcquisitionInconclusive,
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    if (cause is null)
                    {
                        continue;
                    }

                    AddGap(
                        contract.Input,
                        owner,
                        cause.Value,
                        [],
                        new(
                            contract.Input.Field,
                            contract.Input.ValueContract,
                            state,
                            observed?.Value,
                            observed?.EvidenceReference),
                        relationshipContext: null,
                        observed?.EvidenceReference);
                }
            }
        }

        void AnalyzeIdentities()
        {
            foreach (var contract in identityContracts.Values.OrderBy(static item => item.Input.Id.Value, StringComparer.Ordinal))
            {
                var owners = occurrences.Values
                    .Where(occurrence => occurrence.Binding == contract.Input.Binding
                                         && occurrence.Shape == contract.Input.Shape)
                    .OrderBy(static occurrence => occurrence.Id.Value, StringComparer.Ordinal);
                foreach (var owner in owners)
                {
                    if (blockedOccurrences.Contains(owner.Id)
                        || processedIdentities.Contains((contract.Input.Id, owner.Id))
                        || IsConversionHandled(contract.Input.Id, owner.Id)
                        || owner.ObservationIdentity is not null)
                    {
                        continue;
                    }

                    AddGap(
                        contract.Input,
                        owner,
                        RelationRequirementGapCause.ObservationIdentityMissing,
                        [],
                        valueContext: null,
                        relationshipContext: null,
                        evidenceReference: null);
                }
            }
        }

        void AnalyzeParameters()
        {
            foreach (var contract in parameterContracts.Values.OrderBy(static item => item.Input.Id.Value, StringComparer.Ordinal))
            {
                if (IsConversionHandled(contract.Input.Id, null))
                {
                    continue;
                }

                parameters.TryGetValue(contract.Input.Id, out var observed);
                if (observed is null && evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                {
                    continue;
                }

                if ((observed is null
                     || observed.State is RelationQueryParameterEvidenceState.NotProvided
                         or RelationQueryParameterEvidenceState.Missing)
                    && contract.Definition.Presence == FieldPresence.Optional)
                {
                    continue;
                }

                var cause = observed?.State switch
                {
                    null => RelationRequirementGapCause.InputNotProvided,
                    RelationQueryParameterEvidenceState.NotProvided => RelationRequirementGapCause.InputNotProvided,
                    RelationQueryParameterEvidenceState.Failed => RelationRequirementGapCause.InputAcquisitionFailed,
                    RelationQueryParameterEvidenceState.Missing => RelationRequirementGapCause.RequiredValueMissing,
                    RelationQueryParameterEvidenceState.Null
                        when !contract.ValueContract.IsSatisfiedByConstant(ObservationValue.Null) =>
                        RelationRequirementGapCause.RequiredValueNull,
                    RelationQueryParameterEvidenceState.Null => (RelationRequirementGapCause?)null,
                    RelationQueryParameterEvidenceState.Provided => (RelationRequirementGapCause?)null,
                    _ => throw new ArgumentOutOfRangeException()
                };
                if (cause is null)
                {
                    continue;
                }

                AddGap(
                    contract.Input,
                    occurrence: null,
                    cause.Value,
                    [],
                    valueContext: null,
                    relationshipContext: null,
                    observed?.EvidenceReference);
            }
        }

        void AnalyzeCapabilities()
        {
            foreach (var contract in capabilityContracts.Values.OrderBy(static item => item.Input.Id.Value, StringComparer.Ordinal))
            {
                if (IsConversionHandled(contract.Input.Id, null))
                {
                    continue;
                }

                capabilities.TryGetValue(contract.Input.Id, out var observed);
                if (observed is null && evidence.Completeness == RelationQueryEvidenceCompleteness.Partial)
                {
                    continue;
                }

                if (observed?.State == RelationQueryCapabilityEvidenceState.Available)
                {
                    continue;
                }

                AddGap(
                    contract.Input,
                    occurrence: null,
                    RelationRequirementGapCause.CapabilityUnavailable,
                    [],
                    valueContext: null,
                    relationshipContext: null,
                    observed?.EvidenceReference);
            }
        }

        void AddTraversalGap(
            RelationQueryTraversalInputContract contract,
            RelationQueryObservationOccurrence owner,
            RelationRequirementGapCause cause,
            RelationQueryTraversalEvidence? observed,
            ObservationValue? referenceValue)
        {
            AddGap(
                contract.Input,
                owner,
                cause,
                GetTraversalDescendants(contract),
                valueContext: null,
                CreateRelationshipContext(contract, observed, referenceValue),
                observed?.EvidenceReference);
            BlockResults(observed);
        }

        void AddGap(
            RelationQueryRequirementInput input,
            RelationQueryObservationOccurrence? occurrence,
            RelationRequirementGapCause cause,
            ImmutableArray<RelationQueryInputId> blockedInputs,
            RelationRequirementGapValueContext? valueContext,
            RelationRequirementGapRelationshipContext? relationshipContext,
            string? evidenceReference)
        {
            var occurrenceKey = occurrence?.Id.Value ?? string.Empty;
            var key = (occurrenceKey, input.Id, cause);
            var combinedBlocked = blockedInputs
                .Where(id => id != input.Id)
                .Distinct()
                .OrderBy(static id => id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (gaps.TryGetValue(key, out var existing))
            {
                combinedBlocked = existing.BlockedInputs.Concat(combinedBlocked)
                    .Distinct()
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                valueContext ??= existing.ValueContext;
                relationshipContext ??= existing.RelationshipContext;
                evidenceReference ??= existing.EvidenceReference;
            }

            var inputIds = Prepend(input.Id, combinedBlocked);
            var impacts = MergeImpacts(input.Id, inputIds);
            var requiredFields = inputIds
                .Select(id => fieldContracts.TryGetValue(id, out var field) ? field.Input.Field : (RelationQueryFieldReference?)null)
                .Where(static field => field is not null)
                .Select(static field => field!.Value)
                .Distinct()
                .OrderBy(static field => field.ToString(), StringComparer.Ordinal)
                .ToImmutableArray();
            var id = CreateGapId(input.Id, occurrence?.Id, cause);
            gaps[key] = new(
                id,
                evidence.Evaluation,
                occurrence,
                cause,
                input,
                impacts,
                combinedBlocked,
                requiredFields,
                valueContext,
                relationshipContext,
                evidenceReference,
                SuggestedResolutions(cause),
                plan.Provenance,
                plan.Demand);
        }

        ImmutableArray<RelationRequirementGapDecision> ProjectPolicy(ImmutableArray<RelationRequirementGap> normalizedGaps)
        {
            List<RelationRequirementGapDecision> decisions = [];
            var resolver = new RelationQueryShapeResolver(
                [.. plan.Provenance.ShapeDocuments.Select(static document => document.Graph)]);
            foreach (var gap in normalizedGaps)
            {
                foreach (var impact in gap.Impacts)
                {
                    var choice = policy.Decide(gap, impact)
                        ?? throw new InvalidOperationException(
                            $"Relation requirement gap policy '{policy.Id.Value}' returned no choice.");
                    var disposition = choice.Disposition;
                    var reporting = choice.Reporting;
                    var severity = choice.Severity;
                    if (disposition.Kind == RelationRequirementGapDispositionKind.SubstituteNull
                        && !AcceptsSubstitution(resolver, impact.Output, ObservationValue.Null))
                    {
                        AddDiagnostic(
                            RelationRuntimeDiagnosticCodes.NullSubstitutionInvalid,
                            $"Output '{impact.Output.Id.Value}' does not permit null substitution.",
                            gap.Input.Id,
                            gap.Occurrence?.Id,
                            gap.EvidenceReference,
                            gap.Id,
                            impact.Output);
                        disposition = RelationRequirementGapDisposition.Unresolved;
                        reporting = RelationRequirementGapReportingKind.Report;
                        severity = DiagnosticSeverity.Error;
                    }
                    else if (disposition.Kind == RelationRequirementGapDispositionKind.SubstituteDefault
                             && (disposition.Substitution is not { } substitution
                                 || !AcceptsSubstitution(resolver, impact.Output, substitution)))
                    {
                        AddDiagnostic(
                            RelationRuntimeDiagnosticCodes.DefaultSubstitutionInvalid,
                            $"Explicit default does not satisfy output '{impact.Output.Id.Value}'.",
                            gap.Input.Id,
                            gap.Occurrence?.Id,
                            gap.EvidenceReference,
                            gap.Id,
                            impact.Output);
                        disposition = RelationRequirementGapDisposition.Unresolved;
                        reporting = RelationRequirementGapReportingKind.Report;
                        severity = DiagnosticSeverity.Error;
                    }

                    decisions.Add(new(
                        gap.Id,
                        impact,
                        disposition,
                        reporting,
                        severity,
                        policy.Id,
                        policy.Source));
                    if (reporting == RelationRequirementGapReportingKind.Report)
                    {
                        AddDiagnostic(
                            DiagnosticCodeFor(gap.Cause),
                            $"Relation requirement gap '{gap.Cause}' for input '{gap.Input.Id.Value}' affects output '{impact.Output.Id.Value}' through '{impact.Effect}'.",
                            gap.Input.Id,
                            gap.Occurrence?.Id,
                            gap.EvidenceReference,
                            gap.Id,
                            impact.Output,
                            severity);
                    }
                }
            }

            return
            [
                .. decisions
                    .OrderBy(static decision => decision.Gap.Value, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Impact.Output.Id.Value, StringComparer.Ordinal)
                    .ThenBy(static decision => (int)decision.Impact.Effect)
            ];
        }

        static bool AcceptsSubstitution(
            RelationQueryShapeResolver resolver,
            RelationQueryOutputReference output,
            ObservationValue value) =>
            output.Field is { } field
            && resolver.TryGetTargetExpectation(output.Shape, field.Path, out var expectation)
            && expectation.Value is { } contract
            && contract.IsSatisfiedByConstant(value);

        bool TryGetReferenceGapCause(
            RelationQueryFieldEvidence? observed,
            out RelationRequirementGapCause cause)
        {
            if (observed is null)
            {
                cause = RelationRequirementGapCause.ReferenceFieldNotLoaded;
                return evidence.Completeness == RelationQueryEvidenceCompleteness.Complete;
            }

            cause = observed.State switch
            {
                RelationQueryFieldEvidenceState.NotLoaded => RelationRequirementGapCause.ReferenceFieldNotLoaded,
                RelationQueryFieldEvidenceState.Missing => RelationRequirementGapCause.ReferenceValueMissing,
                RelationQueryFieldEvidenceState.Null => RelationRequirementGapCause.ReferenceValueNull,
                RelationQueryFieldEvidenceState.Failed => RelationRequirementGapCause.InputAcquisitionFailed,
                RelationQueryFieldEvidenceState.Inconclusive =>
                    RelationRequirementGapCause.InputAcquisitionInconclusive,
                RelationQueryFieldEvidenceState.Value => default,
                _ => throw new ArgumentOutOfRangeException()
            };
            return observed.State != RelationQueryFieldEvidenceState.Value;
        }

        RelationRequirementGapRelationshipContext CreateRelationshipContext(
            RelationQueryTraversalInputContract contract,
            RelationQueryTraversalEvidence? observed,
            ObservationValue? referenceValue) =>
            new(
                contract.Definition,
                contract.Input.Direction,
                contract.From,
                contract.Result,
                contract.JoinKind,
                contract.Cardinality,
                observed?.State ?? RelationQueryTraversalEvidenceState.NotAttempted,
                observed?.Completeness ?? RelationQueryEvidenceCompleteness.Partial,
                observed?.Results.Length ?? 0,
                referenceValue,
                observed?.EvidenceReference);

        ImmutableArray<RelationQueryTraversalInputContract> GetTraversalOrder()
        {
            Dictionary<RelationQueryInputId, int> depths = [];
            int Depth(RelationQueryTraversalInputContract traversal, HashSet<RelationQueryInputId> active)
            {
                if (depths.TryGetValue(traversal.Input.Id, out var cached))
                {
                    return cached;
                }

                if (!active.Add(traversal.Input.Id))
                {
                    return 0;
                }

                var parents = traversalContracts.Values.Where(candidate =>
                        candidate.Result == traversal.From
                        && candidate.ResultShape == traversal.FromShape)
                    .ToArray();
                var depth = parents.Length == 0
                    ? 0
                    : 1 + parents.Max(parent => Depth(parent, active));
                active.Remove(traversal.Input.Id);
                depths[traversal.Input.Id] = depth;
                return depth;
            }

            return
            [
                .. traversalContracts.Values
                    .OrderBy(item => Depth(item, []))
                    .ThenBy(static item => item.Input.Id.Value, StringComparer.Ordinal)
            ];
        }

        ImmutableArray<RelationQueryInputId> GetBlockedInputs(RelationQueryInputId input)
        {
            if (sourceContracts.TryGetValue(input, out var source))
            {
                return GetSourceDescendants(source);
            }

            if (traversalContracts.TryGetValue(input, out var traversal))
            {
                return GetTraversalDescendants(traversal);
            }

            var dependentTraversals = forwardReferenceFields
                .Where(pair => pair.Value.Input.Id == input)
                .Select(pair => traversalContracts[pair.Key])
                .Concat(inverseAnchorIdentities
                    .Where(pair => pair.Value.Input.Id == input)
                    .Select(pair => traversalContracts[pair.Key]))
                .DistinctBy(static item => item.Input.Id)
                .ToArray();
            return
            [
                .. dependentTraversals
                    .SelectMany(traversal => Prepend(traversal.Input.Id, GetTraversalDescendants(traversal)))
                    .Distinct()
                    .OrderBy(static id => id.Value, StringComparer.Ordinal)
            ];
        }

        ImmutableArray<RelationQueryInputId> GetSourceDescendants(RelationQuerySourceInputContract source)
        {
            HashSet<RelationQueryInputId> result = [.. source.Fields.Select(static field => field.Input.Id)];
            foreach (var identity in identityContracts.Values.Where(identity =>
                         identity.Input.Producer == source.Node
                         && identity.Input.Binding == source.Binding
                         && identity.Input.Shape == source.Shape))
            {
                result.Add(identity.Input.Id);
            }
            foreach (var traversal in traversalContracts.Values.Where(traversal =>
                         traversal.From == source.Binding && traversal.FromShape == source.Shape))
            {
                result.Add(traversal.Input.Id);
                result.UnionWith(GetTraversalDescendants(traversal));
            }
            return [.. result.OrderBy(static id => id.Value, StringComparer.Ordinal)];
        }

        ImmutableArray<RelationQueryInputId> GetTraversalDescendants(RelationQueryTraversalInputContract traversal)
        {
            if (traversalDescendantCache.TryGetValue(traversal.Input.Id, out var cached))
            {
                return cached;
            }

            HashSet<RelationQueryInputId> result = [.. traversal.Fields.Select(static field => field.Input.Id)];
            foreach (var identity in identityContracts.Values.Where(identity =>
                         identity.Input.Producer == traversal.Input.Traversal
                         && identity.Input.Binding == traversal.Result
                         && identity.Input.Shape == traversal.ResultShape))
            {
                result.Add(identity.Input.Id);
            }
            foreach (var child in traversalContracts.Values.Where(child =>
                         child.From == traversal.Result && child.FromShape == traversal.ResultShape))
            {
                result.Add(child.Input.Id);
                result.UnionWith(GetTraversalDescendants(child));
            }
            var normalized = result.OrderBy(static id => id.Value, StringComparer.Ordinal).ToImmutableArray();
            traversalDescendantCache[traversal.Input.Id] = normalized;
            return normalized;
        }

        ImmutableArray<RelationQueryDependencyImpact> MergeImpacts(
            RelationQueryInputId primaryInput,
            ImmutableArray<RelationQueryInputId> inputIds)
        {
            var primaryImpacts = dependencies[primaryInput].Impacts;
            var gates = primaryImpacts
                .GroupBy(static impact => impact.Output.Id)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Any(static impact => impact.Requirement == QueryInputRequirement.Required)
                        ? QueryInputRequirement.Required
                        : QueryInputRequirement.Optional);
            var overallGate = primaryImpacts.Any(static impact => impact.Requirement == QueryInputRequirement.Required)
                ? QueryInputRequirement.Required
                : QueryInputRequirement.Optional;
            return
            [
                .. inputIds
                .Where(dependencies.ContainsKey)
                .SelectMany(input => dependencies[input].Impacts)
                .GroupBy(static impact => (impact.Output.Id, impact.Effect))
                .Select(group =>
                {
                    var gate = gates.GetValueOrDefault(group.Key.Id, overallGate);
                    var descendantRequirement = group.Any(static impact =>
                        impact.Requirement == QueryInputRequirement.Required)
                        ? QueryInputRequirement.Required
                        : QueryInputRequirement.Optional;
                    return new RelationQueryDependencyImpact(
                        group.First().Output,
                        group.Key.Effect,
                        gate == QueryInputRequirement.Optional
                            ? QueryInputRequirement.Optional
                            : descendantRequirement,
                        [.. group.SelectMany(static impact => impact.Traces)]);
                })
                .OrderBy(static impact => impact.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static impact => (int)impact.Effect)
            ];
        }

        static ImmutableArray<RelationRequirementGapResolutionKind> SuggestedResolutions(RelationRequirementGapCause cause)
        {
            ImmutableArray<RelationRequirementGapResolutionKind> values = cause switch
            {
                RelationRequirementGapCause.InputNotProvided => [RelationRequirementGapResolutionKind.ProvideInput],
                RelationRequirementGapCause.InputAcquisitionFailed => [RelationRequirementGapResolutionKind.RetryAcquisition],
                RelationRequirementGapCause.InputAcquisitionInconclusive =>
                    [RelationRequirementGapResolutionKind.RetryAcquisition],
                RelationRequirementGapCause.ObservationIdentityMissing => [RelationRequirementGapResolutionKind.ProvideValue],
                RelationRequirementGapCause.ReferenceFieldNotLoaded => [RelationRequirementGapResolutionKind.LoadField],
                RelationRequirementGapCause.ReferenceValueMissing or RelationRequirementGapCause.ReferenceValueNull =>
                    [RelationRequirementGapResolutionKind.ProvideReferenceValue],
                RelationRequirementGapCause.ResolutionNotAttempted => [RelationRequirementGapResolutionKind.ResolveRelationship],
                RelationRequirementGapCause.ResolutionFailed => [RelationRequirementGapResolutionKind.RetryAcquisition],
                RelationRequirementGapCause.RelatedObservationNotFound => [RelationRequirementGapResolutionKind.ProvideRelatedObservation],
                RelationRequirementGapCause.RelatedObservationRejected =>
                    [RelationRequirementGapResolutionKind.ProvideRelatedObservation, RelationRequirementGapResolutionKind.ResolveRelationship],
                RelationRequirementGapCause.RequiredFieldNotLoaded => [RelationRequirementGapResolutionKind.LoadField],
                RelationRequirementGapCause.RequiredValueMissing or RelationRequirementGapCause.RequiredValueNull =>
                    [RelationRequirementGapResolutionKind.ProvideValue],
                RelationRequirementGapCause.CapabilityUnavailable => [RelationRequirementGapResolutionKind.ProvideCapability],
                RelationRequirementGapCause.CardinalityViolation => [RelationRequirementGapResolutionKind.CorrectCardinality],
                RelationRequirementGapCause.ConversionFailure => [RelationRequirementGapResolutionKind.CorrectConversion],
                _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported relation requirement gap cause.")
            };
            return [.. values.Distinct().Order()];
        }

        static string DiagnosticCodeFor(RelationRequirementGapCause cause) => cause switch
        {
            RelationRequirementGapCause.InputNotProvided => RelationRuntimeDiagnosticCodes.RequirementGapInputNotProvided,
            RelationRequirementGapCause.InputAcquisitionFailed => RelationRuntimeDiagnosticCodes.RequirementGapInputAcquisitionFailed,
            RelationRequirementGapCause.InputAcquisitionInconclusive =>
                RelationRuntimeDiagnosticCodes.RequirementGapInputAcquisitionInconclusive,
            RelationRequirementGapCause.ObservationIdentityMissing => RelationRuntimeDiagnosticCodes.RequirementGapObservationIdentityMissing,
            RelationRequirementGapCause.ReferenceFieldNotLoaded => RelationRuntimeDiagnosticCodes.RequirementGapReferenceFieldNotLoaded,
            RelationRequirementGapCause.ReferenceValueMissing => RelationRuntimeDiagnosticCodes.RequirementGapReferenceValueMissing,
            RelationRequirementGapCause.ReferenceValueNull => RelationRuntimeDiagnosticCodes.RequirementGapReferenceValueNull,
            RelationRequirementGapCause.ResolutionNotAttempted => RelationRuntimeDiagnosticCodes.RequirementGapResolutionNotAttempted,
            RelationRequirementGapCause.ResolutionFailed => RelationRuntimeDiagnosticCodes.RequirementGapResolutionFailed,
            RelationRequirementGapCause.RelatedObservationNotFound => RelationRuntimeDiagnosticCodes.RequirementGapRelatedObservationNotFound,
            RelationRequirementGapCause.RelatedObservationRejected => RelationRuntimeDiagnosticCodes.RequirementGapRelatedObservationRejected,
            RelationRequirementGapCause.RequiredFieldNotLoaded => RelationRuntimeDiagnosticCodes.RequirementGapRequiredFieldNotLoaded,
            RelationRequirementGapCause.RequiredValueMissing => RelationRuntimeDiagnosticCodes.RequirementGapRequiredValueMissing,
            RelationRequirementGapCause.RequiredValueNull => RelationRuntimeDiagnosticCodes.RequirementGapRequiredValueNull,
            RelationRequirementGapCause.CapabilityUnavailable => RelationRuntimeDiagnosticCodes.RequirementGapCapabilityUnavailable,
            RelationRequirementGapCause.CardinalityViolation => RelationRuntimeDiagnosticCodes.RequirementGapCardinalityViolation,
            RelationRequirementGapCause.ConversionFailure => RelationRuntimeDiagnosticCodes.RequirementGapConversionFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported relation requirement gap cause.")
        };

        RelationRequirementGapId CreateGapId(
            RelationQueryInputId input,
            RelationQueryOccurrenceId? occurrence,
            RelationRequirementGapCause cause)
        {
            var definition = evidence.PlanReference.DefinitionFingerprint;
            var shapes = evidence.PlanReference.ShapeSnapshotsFingerprint;
            var catalog = evidence.PlanReference.RelationshipCatalogFingerprint;
            var demand = evidence.PlanReference.DemandFingerprint;
            return new(string.Join(
                ":",
                "relation-requirement-gap/v1",
                Encode(evidence.PlanReference.CompilerProfile),
                Encode(definition.Algorithm),
                Encode(definition.Canonicalization),
                Encode(definition.Value),
                Encode(shapes.Algorithm),
                Encode(shapes.Canonicalization),
                Encode(shapes.Value),
                Encode(catalog?.Algorithm ?? "no-catalog"),
                Encode(catalog?.Canonicalization ?? "no-catalog"),
                Encode(catalog?.Value ?? "no-catalog"),
                Encode(demand.Algorithm),
                Encode(demand.Canonicalization),
                Encode(demand.Value),
                Encode(evidence.Evaluation.Value),
                Encode(occurrence?.Value ?? "evaluation"),
                Encode(input.Value),
                Encode(DiagnosticCodeFor(cause))));
        }

        bool IsConversionHandled(RelationQueryInputId input, RelationQueryOccurrenceId? occurrence) =>
            processedConversions.Contains((input, occurrence?.Value ?? string.Empty));

        bool HasIndependentImpact(RelationQueryInputId input) =>
            dependencies[input].Impacts.Any(static impact => impact.Effect is not (
                RelationQueryRequirementEffect.Correlation
                or RelationQueryRequirementEffect.Acquisition));

        bool IsPrunedCorrelationConversion(RelationQueryConversionFailureEvidence conversion)
        {
            if (conversion.Occurrence is not { } occurrence || HasIndependentImpact(conversion.Input))
            {
                return false;
            }

            var dependentTraversals = forwardReferenceFields
                .Where(pair => pair.Value.Input.Id == conversion.Input)
                .Select(static pair => pair.Key)
                .Concat(inverseAnchorIdentities
                    .Where(pair => pair.Value.Input.Id == conversion.Input)
                    .Select(static pair => pair.Key))
                .Distinct()
                .ToArray();
            return dependentTraversals.Length > 0
                && dependentTraversals.All(input =>
                    traversals.TryGetValue((input, occurrence), out var observed)
                    && observed.State == RelationQueryTraversalEvidenceState.NotApplicable);
        }

        static bool HasMissingForwardReference(
            RelationQueryTraversalInputContract contract,
            RelationQueryTraversalEvidence observed,
            ObservationValue? referenceValue)
        {
            if (referenceValue is not { } value
                || !TryGetReferenceIdentities(contract.Cardinality, value, out var expected))
            {
                return false;
            }

            if (observed.Results.Length < expected.Count)
            {
                return true;
            }

            if (observed.Results.Any(static result => result.ObservationIdentity is null))
            {
                return false;
            }

            var resolved = observed.Results
                .Select(static result => result.ObservationIdentity!)
                .ToHashSet(StringComparer.Ordinal);
            return expected.Any(identity => !resolved.Contains(identity));
        }

        static bool TryGetReferenceIdentities(
            RelationshipTraversalCardinality cardinality,
            ObservationValue value,
            out HashSet<string> identities)
        {
            identities = new(StringComparer.Ordinal);
            switch (cardinality)
            {
                case RelationshipTraversalCardinality.AtMostOne
                    when value.Kind == ObservationValueKind.String
                         && !string.IsNullOrWhiteSpace(value.String):
                    identities.Add(value.String);
                    return true;
                case RelationshipTraversalCardinality.Many when value.Kind == ObservationValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.Kind != ObservationValueKind.String
                            || string.IsNullOrWhiteSpace(item.String))
                        {
                            identities.Clear();
                            return false;
                        }

                        identities.Add(item.String);
                    }
                    return true;
                case RelationshipTraversalCardinality.AtMostOne:
                case RelationshipTraversalCardinality.Many:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(cardinality),
                        cardinality,
                        "Unsupported relationship traversal cardinality.");
            }
        }

        void BlockResults(RelationQueryTraversalEvidence? traversal)
        {
            if (traversal is null)
            {
                return;
            }

            BlockOccurrences(traversal.Results.Select(static result => result.Id));
        }

        void BlockOccurrences(IEnumerable<RelationQueryOccurrenceId> roots)
        {
            Queue<RelationQueryOccurrenceId> pending = new(roots);
            while (pending.TryDequeue(out var occurrence))
            {
                if (!blockedOccurrences.Add(occurrence))
                {
                    continue;
                }

                foreach (var result in traversals.Values
                             .Where(item => item.From == occurrence)
                             .SelectMany(static item => item.Results))
                {
                    pending.Enqueue(result.Id);
                }
            }
        }

        void RegisterOccurrence(
            RelationQueryObservationOccurrence occurrence,
            RelationQueryInputId input,
            string? evidenceReference)
        {
            if (!occurrences.TryAdd(occurrence.Id, occurrence))
            {
                var existing = occurrences[occurrence.Id];
                AddDiagnostic(
                    Equals(existing, occurrence)
                        ? RelationRuntimeDiagnosticCodes.EvidenceDuplicate
                        : RelationRuntimeDiagnosticCodes.EvidenceConflict,
                    Equals(existing, occurrence)
                        ? $"Occurrence '{occurrence.Id.Value}' is declared more than once."
                        : $"Occurrence '{occurrence.Id.Value}' has conflicting binding, shape, or identity evidence.",
                    input,
                    occurrence.Id,
                    evidenceReference);
            }
        }

        bool ValidateInputKind<TInput>(
            RelationQueryInputId input,
            string evidenceKind,
            RelationQueryOccurrenceId? occurrence)
            where TInput : RelationQueryRequirementInput
        {
            if (!inputs.TryGetValue(input, out var expected))
            {
                AddDiagnostic(
                    RelationRuntimeDiagnosticCodes.InputUnknown,
                    $"{evidenceKind} evidence references unknown compiled input '{input.Value}'.",
                    input,
                    occurrence);
                return false;
            }
            if (expected is TInput)
            {
                return true;
            }

            AddDiagnostic(
                RelationRuntimeDiagnosticCodes.InputKindMismatch,
                $"{evidenceKind} evidence for input '{input.Value}' conflicts with compiled input kind '{expected.GetType().Name}'.",
                input,
                occurrence);
            return false;
        }

        IEnumerable<TEvidence> QuarantineDuplicateEvidence<TEvidence, TKey>(
            IEnumerable<TEvidence> values,
            Func<TEvidence, TKey> keySelector,
            Func<TEvidence, RelationQueryInputId> inputSelector,
            Func<TEvidence, RelationQueryOccurrenceId?> occurrenceSelector,
            string evidenceKind)
            where TKey : notnull
            where TEvidence : class
        {
            foreach (var group in values.GroupBy(keySelector))
            {
                var candidates = group.Take(2).ToArray();
                var candidate = candidates[0];
                if (candidates.Length == 1)
                {
                    yield return candidate;
                    continue;
                }

                var input = inputSelector(candidate);
                var occurrence = occurrenceSelector(candidate);
                AddDiagnostic(
                    RelationRuntimeDiagnosticCodes.EvidenceDuplicate,
                    $"{evidenceKind} evidence repeats input '{input.Value}'{(occurrence is null ? string.Empty : $" for occurrence '{occurrence.Value.Value}'")}.",
                    input,
                    occurrence);
            }
        }

        void AddDiagnostic(
            string code,
            string message,
            RelationQueryInputId? input = null,
            RelationQueryOccurrenceId? occurrence = null,
            string? evidenceReference = null,
            RelationRequirementGapId? gap = null,
            RelationQueryOutputReference? output = null,
            DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
            diagnostics.Add(new(
                code,
                severity,
                message,
                evidence.Evaluation,
                input,
                occurrence,
                gap,
                output,
                evidenceReference));

        bool HasEvidenceErrors() => diagnostics.Any(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Gap is null);

        static ImmutableArray<RelationRuntimeDiagnostic> NormalizeDiagnostics(
            IEnumerable<RelationRuntimeDiagnostic> values) =>
        [
            .. values
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Evaluation.Value, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Occurrence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Gap?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Output?.Id.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.EvidenceReference ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];

        static ImmutableArray<RelationQueryInputId> Prepend(
            RelationQueryInputId input,
            ImmutableArray<RelationQueryInputId> values) =>
        [
            .. values.Prepend(input).Distinct().OrderBy(static id => id.Value, StringComparer.Ordinal)
        ];

        static string Encode(string value) => Uri.EscapeDataString(value);
    }
}
