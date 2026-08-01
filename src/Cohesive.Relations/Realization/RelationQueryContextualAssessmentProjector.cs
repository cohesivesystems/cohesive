using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Realization;

/// <summary>
/// Target-neutral description of the first adapter decision that prevented one selected branch from being realized.
/// </summary>
public sealed record RelationQueryContextualBranchFailure
{
    /// <summary>Creates one validated branch-inspection failure.</summary>
    /// <param name="status">Unavailable or invalid outcome of the adapter decision.</param>
    /// <param name="reason">Typed reason the exact branch could not be realized.</param>
    /// <param name="adapterDecisionCode">Stable adapter-owned code identifying the failed decision.</param>
    /// <param name="message">Human-readable explanation of the failed adapter decision.</param>
    /// <param name="resolution">Actionable resolution for the failed adapter decision.</param>
    /// <param name="node">Failed logical node, or <see langword="null"/> when unavailable.</param>
    /// <param name="input">Failed compiled input, or <see langword="null"/> when unavailable.</param>
    /// <param name="requirement">
    /// Explicit branch-scoped requirement receiving the failure, or <see langword="null"/> to select by evidence
    /// and semantic site.
    /// </param>
    /// <param name="missingCapabilityEvidence">
    /// Capability-evidence identities whose absence contributed to the failure.
    /// </param>
    /// <param name="failedOperatingBoundary">
    /// Operating boundary that was missing or failed validation, or <see langword="null"/>.
    /// </param>
    /// <param name="failedConfigurationSetting">
    /// Expected configuration setting that was absent or invalid, or <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="message"/> or <paramref name="resolution"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is available or blocked; <paramref name="reason"/> is prerequisite-blocked; an
    /// identity or supplied string is empty; or <paramref name="missingCapabilityEvidence"/> repeats an identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> or <paramref name="reason"/> is unsupported.
    /// </exception>
    public RelationQueryContextualBranchFailure(
        RelationQueryBoundAssessmentStatus status,
        RelationQueryUnavailableReason reason,
        RelationQueryAdapterDecisionCode adapterDecisionCode,
        string message,
        string resolution,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        RelationQueryRealizationRequirementId? requirement = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> missingCapabilityEvidence = default,
        RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
        string? failedConfigurationSetting = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported branch-failure status.");
        if (status is not (RelationQueryBoundAssessmentStatus.Unavailable
            or RelationQueryBoundAssessmentStatus.Invalid))
        {
            throw new ArgumentException(
                "A branch failure must be unavailable or invalid.",
                nameof(status));
        }
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unsupported branch-failure reason.");
        if (reason == RelationQueryUnavailableReason.PrerequisiteBlocked)
        {
            throw new ArgumentException(
                "A directly examined branch failure cannot use the prerequisite-blocked reason.",
                nameof(reason));
        }
        if (string.IsNullOrWhiteSpace(adapterDecisionCode.Value))
            throw new ArgumentException("A branch failure requires an adapter decision code.", nameof(adapterDecisionCode));
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("A branch-failure node cannot be default.", nameof(node));
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("A branch-failure input cannot be default.", nameof(input));
        if (requirement is { } requirementId && string.IsNullOrWhiteSpace(requirementId.Value))
            throw new ArgumentException("A branch-failure requirement cannot be default.", nameof(requirement));
        if (failedOperatingBoundary is { } boundary && string.IsNullOrWhiteSpace(boundary.Value))
        {
            throw new ArgumentException(
                "A failed operating boundary cannot be default.",
                nameof(failedOperatingBoundary));
        }
        if (failedConfigurationSetting is not null && string.IsNullOrWhiteSpace(failedConfigurationSetting))
        {
            throw new ArgumentException(
                "A failed configuration setting cannot be empty.",
                nameof(failedConfigurationSetting));
        }

        Status = status;
        Reason = reason;
        AdapterDecisionCode = adapterDecisionCode;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        Resolution = Guard.RequireNotNullOrWhiteSpace(resolution);
        Node = node;
        Input = input;
        Requirement = requirement;
        MissingCapabilityEvidence = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            missingCapabilityEvidence,
            static value => value.Value,
            nameof(missingCapabilityEvidence));
        FailedOperatingBoundary = failedOperatingBoundary;
        FailedConfigurationSetting = failedConfigurationSetting;
    }

    /// <summary>Unavailable or invalid outcome of the adapter decision.</summary>
    public RelationQueryBoundAssessmentStatus Status { get; }

    /// <summary>Typed reason the exact branch could not be realized.</summary>
    public RelationQueryUnavailableReason Reason { get; }

    /// <summary>Stable adapter-owned code identifying the failed decision.</summary>
    public RelationQueryAdapterDecisionCode AdapterDecisionCode { get; }

    /// <summary>Human-readable explanation of the failed adapter decision.</summary>
    public string Message { get; }

    /// <summary>Actionable resolution for the failed adapter decision.</summary>
    public string Resolution { get; }

    /// <summary>Failed logical node, or <see langword="null"/> when unavailable.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Failed compiled input, or <see langword="null"/> when unavailable.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>
    /// Explicit branch-scoped requirement receiving the failure, or <see langword="null"/> to select by evidence
    /// and semantic site.
    /// </summary>
    public RelationQueryRealizationRequirementId? Requirement { get; }

    /// <summary>Missing capability-evidence identities in stable identity order.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> MissingCapabilityEvidence { get; }

    /// <summary>Failed operating boundary, or <see langword="null"/>.</summary>
    public RelationQueryOperatingBoundaryId? FailedOperatingBoundary { get; }

    /// <summary>Expected configuration setting that was absent or invalid, or <see langword="null"/>.</summary>
    public string? FailedConfigurationSetting { get; }
}

/// <summary>
/// Target-neutral configuration attribution and semantic site attached to one contextual assessment.
/// </summary>
public sealed record RelationQueryContextualAssessmentAttribution
{
    /// <summary>Creates one validated assessment attribution.</summary>
    /// <param name="origin">Configuration-precedence tier that supplied the examined fact.</param>
    /// <param name="authority">Stable declaration, profile, convention, or adapter authority.</param>
    /// <param name="node">Affected logical node, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="field">Affected semantic field path, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected source-placement binding, or <see langword="null"/>.</param>
    /// <param name="configurationSetting">Affected adapter-binding setting, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is empty; an identity is default; <paramref name="field"/> is empty; or
    /// <paramref name="configurationSetting"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    public RelationQueryContextualAssessmentAttribution(
        EffectiveConfigurationOrigin origin,
        string authority,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        FieldPath? field = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        string? configurationSetting = null)
    {
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported configuration-value origin.");
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("An assessment-attribution node cannot be default.", nameof(node));
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("An assessment-attribution input cannot be default.", nameof(input));
        if (field is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An assessment-attribution field path cannot be empty.", nameof(field));
        if (placementBinding is { } placement && string.IsNullOrWhiteSpace(placement.Value))
        {
            throw new ArgumentException(
                "An assessment-attribution placement binding cannot be default.",
                nameof(placementBinding));
        }
        if (configurationSetting is not null && string.IsNullOrWhiteSpace(configurationSetting))
        {
            throw new ArgumentException(
                "An assessment-attribution configuration setting cannot be empty.",
                nameof(configurationSetting));
        }

        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Node = node;
        Input = input;
        Field = field;
        PlacementBinding = placementBinding;
        ConfigurationSetting = configurationSetting;
    }

    /// <summary>Configuration-precedence tier that supplied the examined fact.</summary>
    public EffectiveConfigurationOrigin Origin { get; }

    /// <summary>Stable declaration, profile, convention, or adapter authority.</summary>
    public string Authority { get; }

    /// <summary>Affected logical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected semantic field path, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected source-placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected adapter-binding setting, or <see langword="null"/>.</summary>
    public string? ConfigurationSetting { get; }
}

/// <summary>
/// Coordinates deterministic contextual assessment projection after an adapter inspects each selected branch.
/// </summary>
public static class RelationQueryContextualAssessmentProjector
{
    /// <summary>
    /// Projects complete success proof, or one primary failure followed by prerequisite-blocked assessments, for
    /// every selected branch.
    /// </summary>
    /// <param name="request">Exact plan, profile feasibility, placement, and branch selection being assessed.</param>
    /// <param name="evidenceNamespace">
    /// Stable adapter-owned namespace used to derive collision-free contextual evidence identities.
    /// </param>
    /// <param name="selectBranchFailure">
    /// Callback invoked exactly once per selected branch. It returns the first failed adapter decision, or
    /// <see langword="null"/> only after complete branch inspection succeeds.
    /// </param>
    /// <param name="resolveAttribution">
    /// Callback that resolves configuration attribution and a semantic site for each assessment. Its failure
    /// argument is supplied only for the primary failed requirement and is <see langword="null"/> for successful
    /// and prerequisite-blocked requirements.
    /// </param>
    /// <returns>
    /// Assessments in canonical branch and requirement order. Successful branches retain every capability,
    /// target-enforced boundary, and guarantee from profile feasibility. Failed branches contain exactly one
    /// unavailable or invalid assessment and mark every other requirement as prerequisite-blocked without proof.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/>, <paramref name="evidenceNamespace"/>,
    /// <paramref name="selectBranchFailure"/>, or <paramref name="resolveAttribution"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="evidenceNamespace"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">
    /// A failed branch has no applicable requirement; a successful branch has an unavailable or missing profile
    /// decision; or <paramref name="resolveAttribution"/> returns <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exception">
    /// An exception thrown by <paramref name="selectBranchFailure"/> or <paramref name="resolveAttribution"/> is
    /// propagated unchanged.
    /// </exception>
    public static ImmutableArray<RelationQueryBoundRequirementAssessment> Project(
        RelationQueryBoundRealizationRequest request,
        string evidenceNamespace,
        Func<RelationQueryNativeResultBranch, RelationQueryContextualBranchFailure?> selectBranchFailure,
        Func<
            RelationQueryNativeResultBranch,
            RelationQueryRealizationRequirement,
            RelationQueryContextualBranchFailure?,
            RelationQueryContextualAssessmentAttribution> resolveAttribution)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guard.RequireNotNullOrWhiteSpace(evidenceNamespace);
        ArgumentNullException.ThrowIfNull(selectBranchFailure);
        ArgumentNullException.ThrowIfNull(resolveAttribution);

        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        var assessmentCount = 0;
        foreach (var selection in request.Selection.Branches)
            assessmentCount = checked(assessmentCount + selection.Requirements.Length);
        var assessments = ImmutableArray.CreateBuilder<RelationQueryBoundRequirementAssessment>(assessmentCount);

        foreach (var selection in request.Selection.Branches)
        {
            var branch = selection.Branch;
            var failure = selectBranchFailure(branch);
            if (failure is null)
            {
                foreach (var requirement in selection.Requirements)
                {
                    if (!decisions.TryGetValue(requirement.Id, out var decision)
                        || decision.Kind == CapabilityRealizationKind.Unavailable)
                    {
                        throw new InvalidOperationException(
                            $"Successful branch '{branch.Id.Value}' has no available profile decision for requirement "
                            + $"'{requirement.Id.Value}'.");
                    }

                    var attribution = ResolveAttribution(
                        resolveAttribution,
                        branch,
                        requirement,
                        failure: null);
                    assessments.Add(new(
                        CreateEvidenceId(evidenceNamespace, branch.Id, requirement.Id),
                        branch.Id,
                        requirement.Id,
                        RelationQueryBoundAssessmentStatus.Available,
                        attribution.Origin,
                        attribution.Authority,
                        decision.GetCapabilityEvidence(),
                        decision.GetTargetEnforcedBoundaries(),
                        decision.GetPreservedGuarantees(),
                        node: attribution.Node,
                        input: attribution.Input,
                        field: attribution.Field,
                        placementBinding: attribution.PlacementBinding,
                        configurationSetting: attribution.ConfigurationSetting,
                        message: "Complete branch inspection proved that the exact adapter binding preserves this requirement."));
                }
                continue;
            }

            var primaryRequirement = SelectPrimaryRequirement(selection, failure, decisions);
            var primaryId = CreateEvidenceId(evidenceNamespace, branch.Id, primaryRequirement.Id);
            foreach (var requirement in selection.Requirements)
            {
                var primary = requirement.Id == primaryRequirement.Id;
                var attribution = ResolveAttribution(
                    resolveAttribution,
                    branch,
                    requirement,
                    primary ? failure : null);
                assessments.Add(primary
                    ? new(
                        primaryId,
                        branch.Id,
                        requirement.Id,
                        failure.Status,
                        attribution.Origin,
                        attribution.Authority,
                        unavailableReason: failure.Reason,
                        node: attribution.Node,
                        input: attribution.Input,
                        field: attribution.Field,
                        placementBinding: attribution.PlacementBinding,
                        configurationSetting: attribution.ConfigurationSetting,
                        message: failure.Message,
                        resolution: failure.Resolution,
                        adapterDecisionCode: failure.AdapterDecisionCode,
                        missingCapabilityEvidence: failure.MissingCapabilityEvidence,
                        failedOperatingBoundary: failure.FailedOperatingBoundary,
                        failedConfigurationSetting: failure.FailedConfigurationSetting)
                    : new(
                        CreateEvidenceId(evidenceNamespace, branch.Id, requirement.Id),
                        branch.Id,
                        requirement.Id,
                        RelationQueryBoundAssessmentStatus.Blocked,
                        attribution.Origin,
                        attribution.Authority,
                        unavailableReason: RelationQueryUnavailableReason.PrerequisiteBlocked,
                        node: attribution.Node,
                        input: attribution.Input,
                        field: attribution.Field,
                        placementBinding: attribution.PlacementBinding,
                        configurationSetting: attribution.ConfigurationSetting,
                        message: $"Requirement '{requirement.Id.Value}' was not examined because adapter decision "
                            + $"'{failure.AdapterDecisionCode.Value}' failed first for requirement "
                            + $"'{primaryRequirement.Id.Value}'.",
                        resolution: failure.Resolution,
                        adapterDecisionCode: failure.AdapterDecisionCode,
                        blockedBy: primaryId));
            }
        }

        return assessments.MoveToImmutable();
    }

    static RelationQueryRealizationRequirement SelectPrimaryRequirement(
        RelationQueryBranchSelection selection,
        RelationQueryContextualBranchFailure failure,
        IReadOnlyDictionary<RelationQueryRealizationRequirementId, RelationQueryRealizationDecision> decisions)
    {
        if (failure.Requirement is { } explicitRequirement)
        {
            foreach (var requirement in selection.Requirements)
            {
                if (requirement.Id == explicitRequirement)
                    return requirement;
            }
            throw new InvalidOperationException(
                $"Failed branch '{selection.Branch.Id.Value}' explicitly attributes adapter decision "
                + $"'{failure.AdapterDecisionCode.Value}' to requirement '{explicitRequirement.Value}', which is "
                + "not applicable to the branch.");
        }

        RelationQueryRealizationRequirement? evidenceOrBoundaryMatch = null;
        foreach (var requirement in selection.Requirements)
        {
            if (!decisions.TryGetValue(requirement.Id, out var decision))
                continue;
            var matchesCapability = !failure.MissingCapabilityEvidence.IsDefaultOrEmpty
                && decision.GetCapabilityEvidence().Any(failure.MissingCapabilityEvidence.Contains);
            var matchesBoundary = failure.FailedOperatingBoundary is { } failedBoundary
                && decision.GetBoundaryValidations().Any(validation => validation.Boundary == failedBoundary);
            if (!matchesCapability && !matchesBoundary)
                continue;

            evidenceOrBoundaryMatch ??= requirement;
            if (failure.Input is { } input
                && (selection.IsInputRelevant(input, failure.Node, requirement)
                    || (failure.Node is not null
                        && selection.IsInputRelevant(input, node: null, requirement))))
            {
                return requirement;
            }
            if (failure.Input is null
                && failure.Node is { } node
                && requirement.Origin?.Node == node)
            {
                return requirement;
            }
        }

        if (evidenceOrBoundaryMatch is not null)
            return evidenceOrBoundaryMatch;

        return selection.SelectRequirementForFailure(failure.Input, failure.Node)
               ?? throw new InvalidOperationException(
                   $"Failed branch '{selection.Branch.Id.Value}' has no applicable realization requirement to "
                   + "receive the primary adapter decision.");
    }

    static RelationQueryContextualAssessmentAttribution ResolveAttribution(
        Func<
            RelationQueryNativeResultBranch,
            RelationQueryRealizationRequirement,
            RelationQueryContextualBranchFailure?,
            RelationQueryContextualAssessmentAttribution> resolveAttribution,
        RelationQueryNativeResultBranch branch,
        RelationQueryRealizationRequirement requirement,
        RelationQueryContextualBranchFailure? failure) =>
        resolveAttribution(branch, requirement, failure)
        ?? throw new InvalidOperationException(
            $"The attribution callback returned null for requirement '{requirement.Id.Value}' on branch "
            + $"'{branch.Id.Value}'.");

    static RelationQueryContextEvidenceId CreateEvidenceId(
        string evidenceNamespace,
        RelationQueryNativeResultBranchId branch,
        RelationQueryRealizationRequirementId requirement) =>
        new($"{evidenceNamespace}/{RelationQueryRealizationOrdering.SequenceKey([branch.Value, requirement.Value])}");
}
