using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Realization;

/// <summary>
/// Qualifies profile-level relation/query feasibility using exact placement and adapter-projected binding evidence.
/// </summary>
public static class RelationQueryBoundRealizationCompiler
{
    /// <summary>Produces an exact, deterministic bound-realization prediction.</summary>
    /// <param name="request">Plan, profile-feasibility, placement, and selected branches to qualify.</param>
    /// <param name="evidence">Target-neutral projection of the exact adapter binding and physical assessments.</param>
    /// <returns>An exact bound-realization report suitable for native-compilation authorization.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Compiled plan snapshots cannot be represented by their canonicalization profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A compiled shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A compiled shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public static RelationQueryBoundRealizationReport Compile(
        RelationQueryBoundRealizationRequest request,
        RelationQueryContextualEvidenceProjection evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);

        ImmutableArray<RelationQueryRealizationDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryRealizationDiagnostic>();
        var invalid = false;
        var unavailable = false;

        foreach (var inputDiagnostic in request.ValidateInputs())
        {
            invalid = true;
            diagnostics.Add(new(
                RelationQueryRealizationDiagnosticCodes.ContextInvalid,
                DiagnosticSeverity.Error,
                inputDiagnostic.Message,
                node: inputDiagnostic.Node,
                contextEvidence: null,
                branch: inputDiagnostic.Branch,
                input: inputDiagnostic.Input,
                resolution: "Recompile the canonical plan, feasibility report, and placement from the same definition snapshot."));
        }

        ValidateBindingAffinity(request, evidence.Binding, diagnostics, ref invalid);

        var selectedBranches = request.Selection.Branches.ToDictionary(static selection => selection.Branch.Id);
        var requirements = request.ProfileFeasibility.Requirements.ToDictionary(static requirement => requirement.Id);
        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        var inputs = request.Plan.InputContract.Requirements.Inputs.ToDictionary(static input => input.Id);
        var temporalInputs = request.Plan.InputContract.TemporalCapabilities.ToDictionary(static input => input.Id);
        var placementBindings = request.Placement.Bindings.ToDictionary(static item => item.Id);
        var bindingConfiguration = evidence.Binding.ConfigurationDecisions
            .ToDictionary(static item => item.Setting, StringComparer.Ordinal);

        foreach (var assessment in evidence.Assessments)
        {
            var foreign = false;
            List<string> affinityIssues = [];
            void Reject(string issue)
            {
                foreign = true;
                affinityIssues.Add(issue);
            }
            selectedBranches.TryGetValue(assessment.Branch, out var selectedBranch);
            requirements.TryGetValue(assessment.Requirement, out var contextualRequirement);
            if (selectedBranch is null
                || contextualRequirement is null
                || !selectedBranch.ContainsRequirement(assessment.Requirement))
            {
                Reject("branch-requirement scope");
            }
            if (assessment.Node is { } node
                && (selectedBranch is null || !selectedBranch.ContainsNode(node)))
                Reject("logical-node branch affinity");
            RelationQueryRequirementInput? contextualInput = null;
            RelationQueryTemporalCapabilityInputContract? temporalInput = null;
            if (assessment.Input is { } input)
            {
                inputs.TryGetValue(input, out contextualInput);
                temporalInputs.TryGetValue(input, out temporalInput);
                if (contextualInput is null && temporalInput is null)
                {
                    Reject("compiled-input identity");
                }
                else if (selectedBranch is not null
                         && contextualRequirement is not null
                         && !selectedBranch.IsInputRelevant(input, assessment.Node, contextualRequirement))
                {
                    Reject("compiled-input branch affinity");
                }
            }
            if (assessment.Field is not null
                && assessment.Input is not null
                && (contextualInput is not RelationQueryFieldInput fieldInput
                    || fieldInput.Field.Path != assessment.Field))
            {
                Reject("field-input ownership");
            }
            RelationQuerySourcePlacementBinding? contextualPlacement = null;
            if (assessment.PlacementBinding is { } placement)
            {
                if (!placementBindings.TryGetValue(placement, out contextualPlacement))
                    Reject("source-placement binding identity");
            }
            if (contextualPlacement is not null
                && contextualInput is not null
                && RequiresPlacementOwner(contextualInput)
                && !PlacementOwnsInput(contextualPlacement, contextualInput))
            {
                Reject("compiled-input placement ownership");
            }
            if (decisions.TryGetValue(assessment.Requirement, out var contextualDecision))
            {
                var relevantEvidence = contextualDecision.GetCapabilityEvidence().ToHashSet();
                var relevantBoundaries = contextualDecision.GetBoundaryValidations()
                    .Select(static validation => validation.Boundary)
                    .ToHashSet();
                var relevantGuarantees = contextualDecision.GetPreservedGuarantees().ToHashSet();
                if (assessment.CapabilityEvidence.Any(item => !relevantEvidence.Contains(item))
                    || assessment.MissingCapabilityEvidence.Any(item => !relevantEvidence.Contains(item))
                    || assessment.OperatingBoundaries.Any(item => !relevantBoundaries.Contains(item))
                    || (assessment.FailedOperatingBoundary is { } failedBoundary
                        && !relevantBoundaries.Contains(failedBoundary))
                    || assessment.PreservedGuarantees.Any(item => !relevantGuarantees.Contains(item)))
                {
                    Reject("requirement-decision proof identity");
                }
            }
            else
            {
                Reject("requirement decision");
            }

            if (assessment.ConfigurationSetting is { } setting)
            {
                if (!bindingConfiguration.TryGetValue(setting, out var decision)
                    || decision.Origin != assessment.Origin
                    || !string.Equals(decision.Authority, assessment.Authority, StringComparison.Ordinal))
                {
                    Reject("configuration attribution");
                }
            }

            if (foreign)
            {
                invalid = true;
                diagnostics.Add(ContextDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.ContextInvalid,
                    assessment,
                    $"The contextual assessment is foreign to the exact request in: {string.Join(", ", affinityIssues)}.",
                    "Project evidence again from the exact immutable placement and adapter binding."));
                continue;
            }

            switch (assessment.Status)
            {
                case RelationQueryBoundAssessmentStatus.Available:
                    break;
                case RelationQueryBoundAssessmentStatus.Unavailable:
                    unavailable = true;
                    diagnostics.Add(ContextDiagnostic(
                        RelationQueryRealizationDiagnosticCodes.ContextUnavailable,
                        assessment,
                        assessment.Message,
                        assessment.Resolution!));
                    break;
                case RelationQueryBoundAssessmentStatus.Invalid:
                    invalid = true;
                    diagnostics.Add(ContextDiagnostic(
                        RelationQueryRealizationDiagnosticCodes.ContextInvalid,
                        assessment,
                        assessment.Message,
                        assessment.Resolution!));
                    break;
                case RelationQueryBoundAssessmentStatus.Blocked:
                    unavailable = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(evidence),
                        assessment.Status,
                        "Unsupported contextual assessment status.");
            }
        }

        if (request.ProfileFeasibility.Status == RelationQueryRealizationStatus.Invalid)
        {
            invalid = true;
            diagnostics.Add(new(
                RelationQueryRealizationDiagnosticCodes.ContextInvalid,
                DiagnosticSeverity.Error,
                "An invalid profile-feasibility report cannot be qualified by physical binding evidence.",
                resolution: "Correct the target capability profile or realization policy before contextual binding."));
        }
        else if (!request.ProfileFeasibility.IsRealizable)
        {
            unavailable = true;
        }
        else
        {
            ValidateCoverage(
                request,
                evidence.Assessments,
                diagnostics,
                ref invalid,
                ref unavailable);
        }

        var status = invalid
            ? RelationQueryRealizationStatus.Invalid
            : unavailable
                ? RelationQueryRealizationStatus.NotRealizable
                : RelationQueryRealizationStatus.Realizable;
        var normalizedDiagnostics = NormalizeDiagnostics(diagnostics.ToImmutable());
        var branchIds = request.Branches.Select(static branch => branch.Id).ToImmutableArray();
        var fingerprint = RelationQueryBoundRealizationFingerprinter.Compute(
            request.ProfileFeasibility,
            request.Placement.Fingerprint,
            branchIds,
            evidence,
            normalizedDiagnostics,
            status);
        return new(
            request.ProfileFeasibility,
            request.Placement.Fingerprint,
            branchIds,
            evidence,
            normalizedDiagnostics,
            status,
            fingerprint);
    }

    internal static ImmutableArray<RelationQueryRealizationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Bound-realization diagnostics cannot contain null entries.", nameof(diagnostics));
        return
        [
            .. normalized
                .Distinct()
                .OrderBy(static item => item.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.CompositionRule?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Override?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.SemanticSite ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.ContextEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.BindingSetting ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.ConfigurationOrigin is { } origin ? (int)origin : -1)
                .ThenBy(static item => item.ConfigurationAuthority ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.AdapterDecisionCode?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => (int)item.Severity)
                .ThenBy(static item => item.Message, StringComparer.Ordinal)
                .ThenBy(static item => item.Resolution ?? string.Empty, StringComparer.Ordinal)
        ];
    }

    static bool RequiresPlacementOwner(RelationQueryRequirementInput input) => input is
        RelationQueryFieldInput
        or RelationQueryObservationIdentityInput
        or RelationQuerySourceSetInput
        or RelationQueryRelationshipInput;

    static bool PlacementOwnsInput(
        RelationQuerySourcePlacementBinding placement,
        RelationQueryRequirementInput input) => input switch
    {
        RelationQueryFieldInput field =>
            placement.Node == field.Producer
            && placement.Fields.Any(candidate => candidate.Input == field.Id
                                                 && candidate.SemanticPath == field.Field.Path),
        RelationQueryObservationIdentityInput identity =>
            placement.Node == identity.Producer
            && placement.Shape == identity.Shape
            && placement.Identity is not null,
        RelationQuerySourceSetInput source =>
            placement.Input == source.Id
            && placement.Node == source.Source
            && placement.Binding == source.Binding
            && placement.Shape == source.Shape,
        RelationQueryRelationshipInput traversal =>
            placement.Input == traversal.Id
            && placement.Node == traversal.Traversal
            && placement.Binding == traversal.Result
            && placement.Shape == traversal.ResultShape,
        _ => true
    };

    static void ValidateBindingAffinity(
        RelationQueryBoundRealizationRequest request,
        RelationQueryAdapterBindingReference binding,
        ImmutableArray<RelationQueryRealizationDiagnostic>.Builder diagnostics,
        ref bool invalid)
    {
        List<string> mismatches = [];
        if (binding.Target != request.ProfileFeasibility.TargetProfile.Target)
            mismatches.Add("target");
        if (binding.TargetProfile != request.ProfileFeasibility.TargetProfile.Id)
            mismatches.Add("target profile");

        var expectedPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference);
        if (binding.CompiledPlanFingerprint is null)
            mismatches.Add("compiled-plan affinity (missing)");
        else if (!Equals(binding.CompiledPlanFingerprint, expectedPlan))
            mismatches.Add("compiled-plan affinity");
        if (binding.PlacementFingerprint is null)
            mismatches.Add("source-placement affinity (missing)");
        else if (!Equals(binding.PlacementFingerprint, request.Placement.Fingerprint))
            mismatches.Add("source-placement affinity");

        HashSet<RelationQuerySourcePlacementBindingId> selectedPlacements = [];
        HashSet<RelationQuerySourceInstanceId> selectedSources = [];
        HashSet<RelationQuerySourcePlacementBindingId> requiredPlacements = [];
        HashSet<RelationQuerySourceInstanceId> requiredSources = [];
        foreach (var placementBinding in request.Selection.PlacementBindings)
        {
            selectedPlacements.Add(placementBinding.Id);
            selectedSources.Add(placementBinding.Source);
            if (placementBinding.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
                continue;
            requiredPlacements.Add(placementBinding.Id);
            requiredSources.Add(placementBinding.Source);
        }
        var boundPlacements = binding.PlacementBindings.ToHashSet();
        if (!boundPlacements.IsSubsetOf(selectedPlacements))
            mismatches.Add("placement-binding identity");
        else if (!requiredPlacements.IsSubsetOf(boundPlacements))
            mismatches.Add("selected acquired placement-binding coverage");
        var boundSources = binding.Sources.ToHashSet();
        if (!boundSources.IsSubsetOf(selectedSources))
            mismatches.Add("source identity");
        else if (!requiredSources.IsSubsetOf(boundSources))
            mismatches.Add("selected acquired source coverage");

        if (mismatches.Count == 0)
            return;

        invalid = true;
        diagnostics.Add(new(
            RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch,
            DiagnosticSeverity.Error,
            $"The adapter binding does not match the exact contextual request in: {string.Join(", ", mismatches)}.",
            resolution: "Re-author the adapter binding from the selected plan-bound placement inputs."));
    }

    static void ValidateCoverage(
        RelationQueryBoundRealizationRequest request,
        ImmutableArray<RelationQueryBoundRequirementAssessment> assessments,
        ImmutableArray<RelationQueryRealizationDiagnostic>.Builder diagnostics,
        ref bool invalid,
        ref bool unavailable)
    {
        var assessmentsByPair = assessments
            .GroupBy(static item => (item.Branch, item.Requirement))
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static item => item.Requirement);

        foreach (var branch in request.Selection.Branches)
        {
            foreach (var requirement in branch.Requirements)
            {
                var pair = (Branch: branch.Branch.Id, Requirement: requirement.Id);
                if (!assessmentsByPair.TryGetValue(pair, out var pairAssessments)
                    || pairAssessments.IsDefaultOrEmpty)
                {
                    invalid = true;
                    diagnostics.Add(new(
                        RelationQueryRealizationDiagnosticCodes.ContextEvidenceIncomplete,
                        DiagnosticSeverity.Error,
                        $"No contextual assessment covers requirement '{pair.Requirement.Value}' for branch '{pair.Branch.Value}'.",
                        requirement: pair.Requirement,
                        branch: pair.Branch,
                        resolution: "Extend the adapter evidence projector to assess this branch-scoped requirement."));
                    continue;
                }

                var assessmentStatus = pairAssessments[0].Status;
                if (pairAssessments.Any(item => item.Status != assessmentStatus))
                {
                    invalid = true;
                    diagnostics.Add(new(
                        RelationQueryRealizationDiagnosticCodes.ContextInvalid,
                        DiagnosticSeverity.Error,
                        $"Contextual assessments conflict for requirement '{pair.Requirement.Value}' on branch '{pair.Branch.Value}'.",
                        requirement: pair.Requirement,
                        branch: pair.Branch,
                        resolution: "Make the adapter projector emit one consistent outcome for the exact physical facts."));
                    continue;
                }
                if (assessmentStatus == RelationQueryBoundAssessmentStatus.Unavailable)
                {
                    unavailable = true;
                    continue;
                }
                if (assessmentStatus == RelationQueryBoundAssessmentStatus.Invalid)
                {
                    invalid = true;
                    continue;
                }
                if (assessmentStatus == RelationQueryBoundAssessmentStatus.Blocked)
                {
                    unavailable = true;
                    continue;
                }

                var decision = decisions[pair.Requirement];
                var requiredEvidence = decision.GetCapabilityEvidence();
                var retainedEvidence = pairAssessments
                    .SelectMany(static assessment => assessment.CapabilityEvidence)
                    .ToHashSet();
                var missingEvidence = FindFirstMissing(requiredEvidence, retainedEvidence);
                var requiredBoundaries = decision.GetTargetEnforcedBoundaries();
                var validatedBoundaries = pairAssessments
                    .SelectMany(static assessment => assessment.OperatingBoundaries)
                    .ToHashSet();
                var missingBoundary = FindFirstMissing(requiredBoundaries, validatedBoundaries);
                var preservedGuarantees = pairAssessments
                    .SelectMany(static assessment => assessment.PreservedGuarantees)
                    .ToHashSet();
                var missingGuarantee = FindFirstMissing(
                    decision.GetPreservedGuarantees(),
                    preservedGuarantees);
                if (missingEvidence is null && missingBoundary is null && missingGuarantee is null)
                    continue;

                invalid = true;
                diagnostics.Add(new(
                    RelationQueryRealizationDiagnosticCodes.ContextEvidenceIncomplete,
                    DiagnosticSeverity.Error,
                    $"Contextual evidence for requirement '{pair.Requirement.Value}' on branch '{pair.Branch.Value}' "
                    + "does not retain every profile capability, target-enforced boundary, and required guarantee.",
                    requirement: pair.Requirement,
                    capabilityEvidence: missingEvidence,
                    operatingBoundary: missingBoundary,
                    branch: pair.Branch,
                    resolution: "Project attributable evidence for every retained boundary and semantic guarantee."));
            }
        }
    }

    static T? FindFirstMissing<T>(ImmutableArray<T> required, IReadOnlySet<T> retained)
        where T : struct
    {
        foreach (var item in required)
        {
            if (!retained.Contains(item))
                return item;
        }
        return null;
    }

    static RelationQueryRealizationDiagnostic ContextDiagnostic(
        string code,
        RelationQueryBoundRequirementAssessment assessment,
        string message,
        string resolution) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        requirement: assessment.Requirement,
        capabilityEvidence: assessment.MissingCapabilityEvidence.IsDefaultOrEmpty
            ? assessment.CapabilityEvidence.IsDefaultOrEmpty
                ? null
                : assessment.CapabilityEvidence[0]
            : assessment.MissingCapabilityEvidence[0],
        operatingBoundary: assessment.FailedOperatingBoundary
            ?? (assessment.OperatingBoundaries.IsDefaultOrEmpty
                ? null
                : assessment.OperatingBoundaries[0]),
        node: assessment.Node,
        contextEvidence: assessment.Id,
        branch: assessment.Branch,
        input: assessment.Input,
        field: assessment.Field,
        placementBinding: assessment.PlacementBinding,
        bindingSetting: assessment.FailedConfigurationSetting ?? assessment.ConfigurationSetting,
        resolution: resolution,
        configurationOrigin: assessment.Origin,
        configurationAuthority: assessment.Authority,
        adapterDecisionCode: assessment.AdapterDecisionCode);
}

/// <summary>Computes deterministic fingerprints for exact bound-realization reports.</summary>
public static class RelationQueryBoundRealizationFingerprinter
{
    /// <summary>Fingerprint algorithm identity.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identity.</summary>
    public const string Canonicalization = "relation-query-bound-realization/v1-c14n/v3";

    /// <summary>Computes a deterministic fingerprint from a normalized bound-realization report.</summary>
    /// <param name="report">Bound-realization report to fingerprint.</param>
    /// <returns>A versioned SHA-256 fingerprint of exact contextual realization inputs and outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static RelationQueryBoundRealizationFingerprint Compute(RelationQueryBoundRealizationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Compute(
            report.ProfileFeasibility,
            report.Placement,
            report.Branches,
            report.Evidence,
            report.Diagnostics,
            report.Status);
    }

    internal static RelationQueryBoundRealizationFingerprint Compute(
        RelationQueryRealizationReport profileFeasibility,
        RelationQuerySourcePlacementFingerprint placement,
        ImmutableArray<RelationQueryNativeResultBranchId> branches,
        RelationQueryContextualEvidenceProjection evidence,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics,
        RelationQueryRealizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(profileFeasibility);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(evidence);
        var normalizedDiagnostics = RelationQueryBoundRealizationCompiler.NormalizeDiagnostics(diagnostics);
        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Canonicalization);
        AppendFingerprint(canonical, profileFeasibility.Fingerprint.Algorithm, profileFeasibility.Fingerprint.Canonicalization, profileFeasibility.Fingerprint.Value);
        AppendFingerprint(canonical, placement.Algorithm, placement.Canonicalization, placement.Value);

        Append(canonical, branches.Length);
        foreach (var branch in branches)
            Append(canonical, branch.Value);
        AppendBinding(canonical, evidence.Binding);

        Append(canonical, evidence.Assessments.Length);
        foreach (var assessment in evidence.Assessments)
            AppendAssessment(canonical, assessment);
        Append(canonical, normalizedDiagnostics.Length);
        foreach (var diagnostic in normalizedDiagnostics)
            AppendDiagnostic(canonical, diagnostic);
        Append(canonical, (int)status);

        var hash = SHA256.HashData(canonical.WrittenSpan);
        return new(Algorithm, Canonicalization, Convert.ToHexString(hash).ToLowerInvariant());
    }

    static void AppendBinding(ArrayBufferWriter<byte> buffer, RelationQueryAdapterBindingReference binding)
    {
        Append(buffer, binding.SchemaVersion);
        Append(buffer, binding.BindingId);
        Append(buffer, binding.Target.Value);
        Append(buffer, binding.TargetProfile.Value);
        AppendFingerprint(buffer, binding.Fingerprint.Algorithm, binding.Fingerprint.Canonicalization, binding.Fingerprint.Value);
        Append(buffer, binding.CompiledPlanFingerprint is not null);
        if (binding.CompiledPlanFingerprint is { } plan)
            AppendFingerprint(buffer, plan.Algorithm, plan.Canonicalization, plan.Value);
        Append(buffer, binding.PlacementFingerprint is not null);
        if (binding.PlacementFingerprint is { } placement)
            AppendFingerprint(buffer, placement.Algorithm, placement.Canonicalization, placement.Value);
        AppendIds(buffer, binding.Sources, static item => item.Value);
        AppendIds(buffer, binding.PlacementBindings, static item => item.Value);
        Append(buffer, binding.ConfigurationDecisions.Length);
        foreach (var decision in binding.ConfigurationDecisions)
        {
            Append(buffer, decision.Setting);
            Append(buffer, (int)decision.Origin);
            Append(buffer, decision.Authority);
        }
    }

    static void AppendAssessment(ArrayBufferWriter<byte> buffer, RelationQueryBoundRequirementAssessment assessment)
    {
        Append(buffer, assessment.Id.Value);
        Append(buffer, assessment.Branch.Value);
        Append(buffer, assessment.Requirement.Value);
        Append(buffer, (int)assessment.Status);
        Append(buffer, (int)assessment.Origin);
        Append(buffer, assessment.Authority);
        AppendIds(buffer, assessment.CapabilityEvidence, static item => item.Value);
        AppendIds(buffer, assessment.OperatingBoundaries, static item => item.Value);
        AppendIds(
            buffer,
            assessment.PreservedGuarantees,
            static item => ((int)item).ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        AppendNullableInt32(buffer, assessment.UnavailableReason is { } reason ? (int)reason : null);
        AppendOptional(buffer, assessment.Node?.Value);
        AppendOptional(buffer, assessment.Input?.Value);
        Append(buffer, assessment.Field is not null);
        if (assessment.Field is { } field)
            AppendFieldPath(buffer, field);
        AppendOptional(buffer, assessment.PlacementBinding?.Value);
        AppendOptional(buffer, assessment.ConfigurationSetting);
        AppendOptional(buffer, assessment.AdapterDecisionCode?.Value);
        AppendIds(buffer, assessment.MissingCapabilityEvidence, static item => item.Value);
        AppendOptional(buffer, assessment.FailedOperatingBoundary?.Value);
        AppendOptional(buffer, assessment.FailedConfigurationSetting);
        AppendOptional(buffer, assessment.BlockedBy?.Value);
    }

    static void AppendDiagnostic(ArrayBufferWriter<byte> buffer, RelationQueryRealizationDiagnostic diagnostic)
    {
        Append(buffer, diagnostic.Code);
        Append(buffer, (int)diagnostic.Severity);
        AppendOptional(buffer, diagnostic.Requirement?.Value);
        AppendOptional(buffer, diagnostic.CapabilityEvidence?.Value);
        AppendOptional(buffer, diagnostic.CompositionRule?.Value);
        AppendOptional(buffer, diagnostic.OperatingBoundary?.Value);
        AppendOptional(buffer, diagnostic.Override?.Value);
        AppendOptional(buffer, diagnostic.Node?.Value);
        AppendOptional(buffer, diagnostic.SemanticSite);
        AppendOptional(buffer, diagnostic.ContextEvidence?.Value);
        AppendOptional(buffer, diagnostic.Branch?.Value);
        AppendOptional(buffer, diagnostic.Input?.Value);
        Append(buffer, diagnostic.Field is not null);
        if (diagnostic.Field is { } field)
            AppendFieldPath(buffer, field);
        AppendOptional(buffer, diagnostic.PlacementBinding?.Value);
        AppendOptional(buffer, diagnostic.BindingSetting);
        AppendNullableInt32(
            buffer,
            diagnostic.ConfigurationOrigin is { } origin ? (int)origin : null);
        AppendOptional(buffer, diagnostic.ConfigurationAuthority);
        AppendOptional(buffer, diagnostic.AdapterDecisionCode?.Value);
    }

    static void AppendFieldPath(ArrayBufferWriter<byte> buffer, FieldPath path)
    {
        Append(buffer, path.Segments.Length);
        foreach (var segment in path.Segments)
        {
            Append(buffer, (int)segment.Kind);
            AppendOptional(buffer, segment.Segment);
        }
    }

    static void AppendIds<T>(ArrayBufferWriter<byte> buffer, ImmutableArray<T> values, Func<T, string> select)
    {
        Append(buffer, values.Length);
        foreach (var value in values)
            Append(buffer, select(value));
    }

    static void AppendFingerprint(ArrayBufferWriter<byte> buffer, string algorithm, string canonicalization, string value)
    {
        Append(buffer, algorithm);
        Append(buffer, canonicalization);
        Append(buffer, value);
    }

    static void AppendOptional(ArrayBufferWriter<byte> buffer, string? value)
    {
        if (value is null)
        {
            Append(buffer, -1);
            return;
        }
        Append(buffer, value);
    }

    static void AppendNullableInt32(ArrayBufferWriter<byte> buffer, int? value)
    {
        Append(buffer, value.HasValue);
        if (value is { } concrete)
            Append(buffer, concrete);
    }

    static void Append(ArrayBufferWriter<byte> buffer, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Append(buffer, length);
        var destination = buffer.GetSpan(length);
        Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        buffer.Advance(length);
    }

    static void Append(ArrayBufferWriter<byte> buffer, bool value)
    {
        var destination = buffer.GetSpan(1);
        destination[0] = value ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    static void Append(ArrayBufferWriter<byte> buffer, int value)
    {
        var destination = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        buffer.Advance(sizeof(int));
    }
}
