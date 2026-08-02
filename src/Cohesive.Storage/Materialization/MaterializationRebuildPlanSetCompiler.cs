using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted by rebuild request, membership, placement, and plan-set compilation.</summary>
public static class MaterializationRebuildPlanningDiagnosticCodes
{
    /// <summary>Frozen membership was produced for another materialization or selector.</summary>
    public const string MembershipAffinityMismatch = "materialization.rebuildPlanning.membership.affinityMismatch";

    /// <summary>Membership evidence does not prove a complete authoritative selection.</summary>
    public const string MembershipIncomplete = "materialization.rebuildPlanning.membership.incomplete";

    /// <summary>The observed membership contains a duplicate or default subject identity.</summary>
    public const string MembershipDuplicate = "materialization.rebuildPlanning.membership.duplicate";

    /// <summary>An explicitly selected subject is absent from observed membership.</summary>
    public const string MembershipMissing = "materialization.rebuildPlanning.membership.missing";

    /// <summary>Observed membership contains a subject outside an explicit selection.</summary>
    public const string MembershipExtra = "materialization.rebuildPlanning.membership.extra";

    /// <summary>The supplied backend-pool evidence differs from the request's pinned pool.</summary>
    public const string PoolMismatch = "materialization.rebuildPlanning.placement.poolMismatch";

    /// <summary>A placement assignment repeats a selected subject.</summary>
    public const string AssignmentDuplicate = "materialization.rebuildPlanning.placement.assignmentDuplicate";

    /// <summary>A raw membership, assignment, capacity-domain, or capacity-mapping observation is null.</summary>
    public const string EvidenceInvalid = "materialization.rebuildPlanning.evidence.invalid";

    /// <summary>A selected subject has no placement assignment.</summary>
    public const string AssignmentMissing = "materialization.rebuildPlanning.placement.assignmentMissing";

    /// <summary>A placement assignment addresses a subject outside frozen membership.</summary>
    public const string AssignmentExtra = "materialization.rebuildPlanning.placement.assignmentExtra";

    /// <summary>A placement assignment addresses a target outside the pinned backend pool.</summary>
    public const string TargetOutsidePool = "materialization.rebuildPlanning.placement.targetOutsidePool";

    /// <summary>Physical capacity-domain evidence repeats an identity.</summary>
    public const string CapacityDomainDuplicate = "materialization.rebuildPlanning.placement.capacityDomainDuplicate";

    /// <summary>Physical capacity mappings repeat one selected target.</summary>
    public const string CapacityMappingDuplicate = "materialization.rebuildPlanning.placement.capacityMappingDuplicate";

    /// <summary>A selected target has no unique physical capacity-domain mapping.</summary>
    public const string CapacityMappingMissing = "materialization.rebuildPlanning.placement.capacityMappingMissing";

    /// <summary>A physical capacity mapping or domain is not used by the exact selected target set.</summary>
    public const string CapacityMappingExtra = "materialization.rebuildPlanning.placement.capacityMappingExtra";

    /// <summary>Request, membership, or placement artifacts do not retain exact semantic affinity.</summary>
    public const string ArtifactAffinityMismatch = "materialization.rebuildPlanning.planSet.affinityMismatch";

    /// <summary>No leaf rebuild plan covers one exact placement slice.</summary>
    public const string LeafPlanMissing = "materialization.rebuildPlanning.planSet.leafMissing";

    /// <summary>A leaf rebuild plan does not belong to any exact placement slice.</summary>
    public const string LeafPlanExtra = "materialization.rebuildPlanning.planSet.leafExtra";

    /// <summary>Multiple or conflicting leaf plans address one independently promoted target.</summary>
    public const string LeafPlanConflict = "materialization.rebuildPlanning.planSet.leafConflict";

    /// <summary>A leaf rebuild plan implements another exact materialization definition.</summary>
    public const string LeafPlanMaterializationMismatch = "materialization.rebuildPlanning.planSet.leafMaterializationMismatch";

    /// <summary>A leaf target descriptor differs from the exact descriptor pinned by the backend pool.</summary>
    public const string LeafPlanTargetMismatch = "materialization.rebuildPlanning.planSet.leafTargetMismatch";

    /// <summary>Replay produced content different from the exact persisted plan-set authority.</summary>
    public const string ReplayConflict = "materialization.rebuildPlanning.planSet.replayConflict";
}

/// <summary>Deterministic structured result of one pure rebuild-planning phase.</summary>
/// <typeparam name="TArtifact">Canonical artifact produced on success.</typeparam>
public sealed record MaterializationRebuildPlanningResult<TArtifact>
    where TArtifact : class
{
    /// <summary>Creates a planning result with success/error coherence.</summary>
    /// <param name="artifact">Canonical artifact on success; otherwise <see langword="null"/>.</param>
    /// <param name="diagnostics">Complete attributable diagnostics.</param>
    /// <exception cref="ArgumentException">
    /// Diagnostics are invalid, success retains an error, or failure has no error diagnostic.
    /// </exception>
    public MaterializationRebuildPlanningResult(
        TArtifact? artifact,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        var normalized = MaterializationContract.NormalizeDiagnostics(
            diagnostics.IsDefault ? [] : diagnostics,
            nameof(diagnostics));
        if (artifact is not null && normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("A successful planning result cannot retain error diagnostics.", nameof(diagnostics));
        if (artifact is null && normalized.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error))
            throw new ArgumentException("A failed planning result requires an error diagnostic.", nameof(diagnostics));
        Artifact = artifact;
        Diagnostics = normalized;
    }

    /// <summary>Canonical artifact on success; otherwise <see langword="null"/>.</summary>
    public TArtifact? Artifact { get; }

    /// <summary>Complete attributable diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether a canonical artifact was produced without error diagnostics.</summary>
    public bool IsSuccessful => Artifact is not null
        && Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>Pure deterministic compilation of frozen membership and explicit target placement evidence.</summary>
public static class MaterializationRebuildPlanSetCompiler
{
    const string MembershipStage = "materialization-rebuild-membership-freeze";
    const string PlacementStage = "materialization-target-placement-compilation";

    /// <summary>Freezes exact selector output into finite authoritative membership evidence.</summary>
    /// <param name="request">Canonical rebuild request whose selector was evaluated.</param>
    /// <param name="observedMembers">Raw selected subjects; input order is immaterial.</param>
    /// <param name="authority">Revision, cut, and completeness evidence for the observation.</param>
    /// <param name="provenance">Producer attribution for selector evaluation and membership freezing.</param>
    /// <returns>Canonical frozen membership, or structured exact-coverage diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical selector or membership content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content has no portable representation.</exception>
    public static MaterializationRebuildPlanningResult<MaterializationRebuildMembershipEvidence> FreezeMembership(
        MaterializationRebuildRequestDocument request,
        ImmutableArray<MaterializationPlacementSubjectId> observedMembers,
        MaterializationRebuildMembershipAuthority authority,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(provenance);
        var diagnostics = new List<DocumentValidationDiagnostic>();
        var requestReference = RequestReference(request);

        var observed = observedMembers.IsDefault ? [] : observedMembers;
        var defaultCount = observed.Count(static subject => string.IsNullOrWhiteSpace(subject.Value));
        var duplicateSubjects = observed
            .Where(static subject => !string.IsNullOrWhiteSpace(subject.Value))
            .GroupBy(static subject => subject)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (defaultCount > 0 || duplicateSubjects.Length > 0)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.MembershipDuplicate,
                "Observed membership must contain each defined placement subject at most once.",
                "/observedMembers",
                MembershipStage,
                request,
                [requestReference],
                "unique defined placement subjects",
                defaultCount > 0
                    ? $"{defaultCount} default and {duplicateSubjects.Length} duplicated identities"
                    : string.Join(",", duplicateSubjects)));
        }

        if (authority.Completeness != MaterializationRebuildMembershipCompleteness.Complete)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.MembershipIncomplete,
                "A rebuild plan set requires proof that omission from frozen membership is authoritative.",
                "/authority/completeness",
                MembershipStage,
                request,
                authority.EvidenceReferences,
                MaterializationRebuildMembershipCompleteness.Complete.ToString(),
                authority.Completeness.ToString()));
        }

        if (request.Selection is MaterializationExplicitPlacementSubjectSelection explicitSelection)
        {
            var observedSet = observed
                .Where(static subject => !string.IsNullOrWhiteSpace(subject.Value))
                .ToHashSet();
            var missing = explicitSelection.Subjects.Where(subject => !observedSet.Contains(subject)).ToArray();
            var expectedSet = explicitSelection.Subjects.ToHashSet();
            var extra = observed.Where(subject => !string.IsNullOrWhiteSpace(subject.Value) && !expectedSet.Contains(subject))
                .Distinct()
                .OrderBy(static subject => subject.Value, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanningDiagnosticCodes.MembershipMissing,
                    "Observed membership omits one or more explicitly selected subjects.",
                    "/observedMembers",
                    MembershipStage,
                    request,
                    [requestReference],
                    string.Join(",", explicitSelection.Subjects.Select(static subject => subject.Value)),
                    string.Join(",", missing.Select(static subject => subject.Value))));
            }
            if (extra.Length > 0)
            {
                diagnostics.Add(Error(
                    MaterializationRebuildPlanningDiagnosticCodes.MembershipExtra,
                    "Observed membership includes one or more subjects outside the explicit selection.",
                    "/observedMembers",
                    MembershipStage,
                    request,
                    [requestReference],
                    "only explicitly selected subjects",
                    string.Join(",", extra.Select(static subject => subject.Value))));
            }
        }

        if (diagnostics.Count > 0)
            return Failure<MaterializationRebuildMembershipEvidence>(diagnostics);

        var membership = new MaterializationRebuildMembershipEvidence(
            schemaVersion: MaterializationRebuildMembershipEvidence.CurrentSchemaVersion,
            materialization: request.MaterializationReference,
            selector: MaterializationRebuildPlanningFingerprinters.ComputeSelection(request.Selection),
            members: observed,
            authority,
            provenance);
        return Success(membership);
    }

    /// <summary>Compiles exact subject assignments and separate physical-capacity evidence into target slices.</summary>
    /// <param name="request">Canonical rebuild request.</param>
    /// <param name="membership">Complete frozen membership for the request selector.</param>
    /// <param name="backendPool">Exact pinned backend-pool document.</param>
    /// <param name="assignments">Raw subject-to-target assignments; input order is immaterial.</param>
    /// <param name="capacityDomains">Raw physical capacity-domain evidence.</param>
    /// <param name="capacityAssignments">Raw selected-target to capacity-domain mappings.</param>
    /// <param name="provenance">Producer attribution for explicit or convention-derived placement decisions.</param>
    /// <returns>A canonical placement plan, or structured affinity and exact-coverage diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The supplied backend-pool document or successful compiled artifact violates its canonical contract.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical selector, pool, slice, or placement content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content has no portable representation.</exception>
    public static MaterializationRebuildPlanningResult<MaterializationTargetPlacementPlan> CompilePlacement(
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationBackendPoolDocument backendPool,
        ImmutableArray<MaterializationTargetPlacementAssignment> assignments,
        ImmutableArray<MaterializationPhysicalCapacityDomain> capacityDomains,
        ImmutableArray<MaterializationTargetCapacityAssignment> capacityAssignments,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(backendPool);
        ArgumentNullException.ThrowIfNull(provenance);
        var diagnostics = new List<DocumentValidationDiagnostic>();
        var requestReference = RequestReference(request);
        var membershipReference = $"membership:{membership.Fingerprint.Value}";

        var expectedSelector = MaterializationRebuildPlanningFingerprinters.ComputeSelection(request.Selection);
        if (membership.Materialization != request.MaterializationReference || membership.Selector != expectedSelector)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.MembershipAffinityMismatch,
                "Frozen membership does not belong to the exact request materialization and selector.",
                "/membership",
                PlacementStage,
                request,
                [requestReference, membershipReference],
                $"{request.MaterializationReference.DefinitionFingerprint.Value}/{expectedSelector.Value}",
                $"{membership.Materialization.DefinitionFingerprint.Value}/{membership.Selector.Value}"));
        }
        if (membership.Authority.Completeness != MaterializationRebuildMembershipCompleteness.Complete)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.MembershipIncomplete,
                "Placement cannot compile from incomplete frozen membership.",
                "/membership/authority/completeness",
                PlacementStage,
                request,
                membership.Authority.EvidenceReferences,
                MaterializationRebuildMembershipCompleteness.Complete.ToString(),
                membership.Authority.Completeness.ToString()));
        }

        var pool = MaterializationBackendPoolReference.FromDocument(backendPool);
        if (pool != request.Placement.Pool)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.PoolMismatch,
                "Placement evidence does not identify the request's exact pinned backend pool.",
                "/backendPool",
                PlacementStage,
                request,
                [requestReference, $"pool:{pool.DefinitionFingerprint.Value}"],
                request.Placement.Pool.DefinitionFingerprint.Value,
                pool.DefinitionFingerprint.Value));
        }

        var rawAssignments = assignments.IsDefault ? [] : assignments;
        if (rawAssignments.Any(static assignment => assignment is null))
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.EvidenceInvalid,
                "Placement assignments cannot contain null observations.",
                "/assignments",
                PlacementStage,
                request,
                [membershipReference],
                "non-null unique assignments",
                "null assignment"));
        }
        var validAssignments = rawAssignments.Where(static assignment => assignment is not null).ToArray();
        var duplicatedAssignments = validAssignments.GroupBy(static assignment => assignment.Subject)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicatedAssignments.Length > 0)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.AssignmentDuplicate,
                "A placement subject must be assigned to exactly one target.",
                "/assignments",
                PlacementStage,
                request,
                [membershipReference],
                "one assignment per frozen member",
                string.Join(",", duplicatedAssignments)));
        }

        var membershipSet = membership.Members.ToHashSet();
        var assignedSubjects = validAssignments.Select(static assignment => assignment.Subject).ToHashSet();
        AddSetDifference(
            diagnostics,
            membershipSet.Except(assignedSubjects).Select(static subject => subject.Value),
            MaterializationRebuildPlanningDiagnosticCodes.AssignmentMissing,
            "Frozen membership contains subjects without a placement assignment.",
            "/assignments",
            request,
            [membershipReference],
            "one assignment for every frozen member");
        AddSetDifference(
            diagnostics,
            assignedSubjects.Except(membershipSet).Select(static subject => subject.Value),
            MaterializationRebuildPlanningDiagnosticCodes.AssignmentExtra,
            "Placement contains assignments outside frozen membership.",
            "/assignments",
            request,
            [membershipReference],
            "only frozen members");

        var poolTargets = backendPool.Definition.Members.Select(static member => member.Id).ToHashSet();
        AddSetDifference(
            diagnostics,
            validAssignments.Where(assignment => !poolTargets.Contains(assignment.Target)).Select(static assignment => assignment.Target.Value),
            MaterializationRebuildPlanningDiagnosticCodes.TargetOutsidePool,
            "Placement assigns one or more subjects to targets outside the pinned backend pool.",
            "/assignments",
            request,
            [$"pool:{pool.DefinitionFingerprint.Value}"],
            "targets declared by the pinned backend pool");

        var rawDomains = capacityDomains.IsDefault ? [] : capacityDomains;
        var validDomains = rawDomains.Where(static domain => domain is not null).ToArray();
        var duplicateDomains = validDomains.GroupBy(static domain => domain.Id)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value);
        if (rawDomains.Any(static domain => domain is null))
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.EvidenceInvalid,
                "Physical capacity-domain evidence cannot contain null observations.",
                "/capacityDomains",
                PlacementStage,
                request,
                [$"pool:{pool.DefinitionFingerprint.Value}"],
                "non-null physical capacity domains",
                "null domain"));
        }
        if (duplicateDomains.Any())
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.CapacityDomainDuplicate,
                "Physical capacity-domain evidence cannot repeat an identity.",
                "/capacityDomains",
                PlacementStage,
                request,
                [$"pool:{pool.DefinitionFingerprint.Value}"],
                "unique physical capacity domains",
                string.Join(",", duplicateDomains.Order(StringComparer.Ordinal))));
        }

        var rawCapacityAssignments = capacityAssignments.IsDefault ? [] : capacityAssignments;
        var validCapacityAssignments = rawCapacityAssignments.Where(static assignment => assignment is not null).ToArray();
        var duplicateCapacityTargets = validCapacityAssignments.GroupBy(static assignment => assignment.Target)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (rawCapacityAssignments.Any(static assignment => assignment is null))
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.EvidenceInvalid,
                "Physical capacity mappings cannot contain null observations.",
                "/capacityAssignments",
                PlacementStage,
                request,
                [$"pool:{pool.DefinitionFingerprint.Value}"],
                "non-null capacity-domain mappings",
                "null mapping"));
        }
        if (duplicateCapacityTargets.Length > 0)
        {
            diagnostics.Add(Error(
                MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingDuplicate,
                "A selected target cannot repeat its physical capacity-domain mapping.",
                "/capacityAssignments",
                PlacementStage,
                request,
                [$"pool:{pool.DefinitionFingerprint.Value}"],
                "one capacity-domain mapping per selected target",
                string.Join(",", duplicateCapacityTargets)));
        }

        var usedTargets = validAssignments.Select(static assignment => assignment.Target).ToHashSet();
        var mappedTargets = validCapacityAssignments.Select(static assignment => assignment.Target).ToHashSet();
        AddSetDifference(
            diagnostics,
            usedTargets.Except(mappedTargets).Select(static target => target.Value),
            MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingMissing,
            "A selected target has no physical capacity-domain mapping.",
            "/capacityAssignments",
            request,
            [$"pool:{pool.DefinitionFingerprint.Value}"],
            "one mapping per selected target");
        AddSetDifference(
            diagnostics,
            mappedTargets.Except(usedTargets).Select(static target => target.Value),
            MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingExtra,
            "Physical capacity mappings contain a target with no placement slice.",
            "/capacityAssignments",
            request,
            [$"pool:{pool.DefinitionFingerprint.Value}"],
            "only selected placement targets");

        var domainIds = validDomains.Select(static domain => domain.Id).ToHashSet();
        AddSetDifference(
            diagnostics,
            validCapacityAssignments.Where(assignment => !domainIds.Contains(assignment.CapacityDomain))
                .Select(static assignment => assignment.CapacityDomain.Value),
            MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingMissing,
            "A target capacity mapping names an undeclared physical domain.",
            "/capacityAssignments",
            request,
            [$"pool:{pool.DefinitionFingerprint.Value}"],
            "declared physical capacity domains");
        var mappedDomains = validCapacityAssignments.Select(static assignment => assignment.CapacityDomain).ToHashSet();
        AddSetDifference(
            diagnostics,
            domainIds.Except(mappedDomains).Select(static domain => domain.Value),
            MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingExtra,
            "Physical capacity evidence contains a domain unused by any placement slice.",
            "/capacityDomains",
            request,
            [$"pool:{pool.DefinitionFingerprint.Value}"],
            "only domains used by selected targets");

        if (diagnostics.Count > 0)
            return Failure<MaterializationTargetPlacementPlan>(diagnostics);

        var capacityByTarget = validCapacityAssignments.ToDictionary(static assignment => assignment.Target);
        var slices = validAssignments
            .GroupBy(static assignment => assignment.Target)
            .OrderBy(static group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => new MaterializationPlacementSliceReference(
                schemaVersion: MaterializationPlacementSliceReference.CurrentSchemaVersion,
                id: new($"placement-slice/{MaterializationStableIdentity.Digest(
                    request.MaterializationReference.DefinitionFingerprint.Value,
                    membership.Fingerprint.Value,
                    pool.DefinitionFingerprint.Value,
                    group.Key.Value)}"),
                materialization: request.MaterializationReference,
                membership: membership.Fingerprint,
                pool,
                target: group.Key,
                subjects: [.. group.Select(static assignment => assignment.Subject)]))
            .ToImmutableArray();
        var sliceCapacityBindings = slices.Select(slice => new MaterializationPlacementSliceCapacityBinding(
            slice: slice.Id,
            capacityDomain: capacityByTarget[slice.Target].CapacityDomain)).ToImmutableArray();
        var placement = new MaterializationTargetPlacementPlan(
            schemaVersion: MaterializationTargetPlacementPlan.CurrentSchemaVersion,
            materialization: request.MaterializationReference,
            membership: membership.Fingerprint,
            backendPool,
            slices,
            capacityDomains: [.. validDomains],
            capacityBindings: sliceCapacityBindings,
            provenance);
        return Success(placement);
    }

    internal static MaterializationRebuildPlanningResult<TArtifact> Success<TArtifact>(TArtifact artifact)
        where TArtifact : class => new(artifact);

    internal static MaterializationRebuildPlanningResult<TArtifact> Failure<TArtifact>(
        IEnumerable<DocumentValidationDiagnostic> diagnostics)
        where TArtifact : class => new(null, [.. diagnostics]);

    internal static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string stage,
        MaterializationRebuildRequestDocument request,
        ImmutableArray<string> sources,
        string expected,
        string observed) =>
        MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            stage,
            request.MaterializationReference.Materialization.Value,
            sources,
            expected,
            observed);

    static void AddSetDifference(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        IEnumerable<string> differences,
        string code,
        string message,
        string location,
        MaterializationRebuildRequestDocument request,
        ImmutableArray<string> sources,
        string expected)
    {
        var values = differences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length == 0)
            return;
        diagnostics.Add(Error(code, message, location, PlacementStage, request, sources, expected, string.Join(",", values)));
    }

    internal static string RequestReference(MaterializationRebuildRequestDocument request) =>
        $"request:{request.Fingerprint.Value}";
}

/// <summary>Pure deterministic linker from canonical planning evidence and actual one-target leaf plans.</summary>
public static class MaterializationRebuildPlanSetLinker
{
    const string LinkStage = "materialization-rebuild-plan-set-link";
    const string ReplayStage = "materialization-rebuild-plan-set-replay";
    const string SchedulingConvention = "cohesive.storage/materialization-rebuild-plan-set/scheduling/v1";

    /// <summary>Links exact request, membership, placement, and one-target leaf plans into a canonical plan set.</summary>
    /// <param name="request">Canonical rebuild request.</param>
    /// <param name="membership">Complete frozen membership evidence.</param>
    /// <param name="placement">Exact target placement and separate capacity evidence.</param>
    /// <param name="leafPlans">Actual verified leaf plans to reference, in any order.</param>
    /// <param name="provenance">Compiler and source attribution for the linked plan set.</param>
    /// <returns>A canonical linked plan set, or structured affinity and exact-coverage diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A successful linked artifact violates its canonical contract.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical comparison or plan-set content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content has no portable representation.</exception>
    public static MaterializationRebuildPlanningResult<MaterializationRebuildPlanSet> Link(
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationTargetPlacementPlan placement,
        ImmutableArray<MaterializationRebuildPlan> leafPlans,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(provenance);
        var diagnostics = new List<DocumentValidationDiagnostic>();
        var requestSource = MaterializationRebuildPlanSetCompiler.RequestReference(request);
        var membershipSource = $"membership:{membership.Fingerprint.Value}";
        var placementSource = $"placement:{placement.Fingerprint.Value}";

        var selector = MaterializationRebuildPlanningFingerprinters.ComputeSelection(request.Selection);
        if (membership.Materialization != request.MaterializationReference
            || membership.Selector != selector
            || placement.Materialization != request.MaterializationReference
            || placement.Membership != membership.Fingerprint
            || placement.Pool != request.Placement.Pool)
        {
            diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.ArtifactAffinityMismatch,
                "Request, membership, and placement do not form one exact semantic evidence chain.",
                "/",
                LinkStage,
                request,
                [requestSource, membershipSource, placementSource],
                "exact request → selector membership → pinned-pool placement affinity",
                "one or more fingerprints or references differ"));
        }
        if (membership.Authority.Completeness != MaterializationRebuildMembershipCompleteness.Complete)
        {
            diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.MembershipIncomplete,
                "A linked plan set cannot retain incomplete membership evidence.",
                "/membership/authority/completeness",
                LinkStage,
                request,
                membership.Authority.EvidenceReferences,
                MaterializationRebuildMembershipCompleteness.Complete.ToString(),
                membership.Authority.Completeness.ToString()));
        }

        var placedMembers = placement.Slices.SelectMany(static slice => slice.Subjects).ToHashSet();
        var membershipSet = membership.Members.ToHashSet();
        if (!placedMembers.SetEquals(membershipSet))
        {
            diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.ArtifactAffinityMismatch,
                "Placement slices must cover frozen membership exactly without gaps or extras.",
                "/placement/slices",
                LinkStage,
                request,
                [membershipSource, placementSource],
                string.Join(",", membership.Members.Select(static subject => subject.Value)),
                string.Join(",", placedMembers.Select(static subject => subject.Value).Order(StringComparer.Ordinal))));
        }

        var rawLeaves = leafPlans.IsDefault ? [] : leafPlans;
        if (rawLeaves.Any(static leaf => leaf is null))
        {
            diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.LeafPlanConflict,
                "Leaf-plan catalogs cannot contain null entries.",
                "/leafPlans",
                LinkStage,
                request,
                [placementSource],
                "non-null one-target leaf plans",
                "null leaf plan"));
        }
        var leaves = rawLeaves.Where(static leaf => leaf is not null).ToArray();
        var duplicatedTargets = leaves.GroupBy(static leaf => leaf.Target.Id)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var duplicatedReferences = leaves.GroupBy(static leaf => leaf.Fingerprint)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicatedTargets.Length > 0 || duplicatedReferences.Length > 0)
        {
            diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.LeafPlanConflict,
                "Each independently promoted target requires one distinct exact leaf-plan reference.",
                "/leafPlans",
                LinkStage,
                request,
                [placementSource],
                "one distinct leaf plan per placement target",
                string.Join(",", duplicatedTargets.Concat(duplicatedReferences).Order(StringComparer.Ordinal))));
        }

        foreach (var leaf in leaves)
        {
            MaterializationDefinitionReference leafMaterialization;
            try
            {
                leafMaterialization = MaterializationDefinitionReference.FromDocument(leaf.Materialization);
            }
            catch (ArgumentException)
            {
                leafMaterialization = new(
                    MaterializationDefinitionReference.CurrentSchemaVersion,
                    leaf.Materialization.Definition.Id,
                    leaf.Materialization.DefinitionFingerprint);
            }
            if (leafMaterialization != request.MaterializationReference)
            {
                diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                    MaterializationRebuildPlanningDiagnosticCodes.LeafPlanMaterializationMismatch,
                    "A leaf plan implements another exact materialization definition.",
                    "/leafPlans",
                    LinkStage,
                    request,
                    [requestSource, $"leaf:{leaf.Fingerprint.Value}"],
                    request.MaterializationReference.DefinitionFingerprint.Value,
                    leafMaterialization.DefinitionFingerprint.Value));
            }

            var pinnedTarget = placement.BackendPool.Definition.Members.SingleOrDefault(member => member.Id == leaf.Target.Id);
            if (pinnedTarget is not null && !MaterializationContract.CanonicalEquals(pinnedTarget, leaf.Target))
            {
                diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
                    MaterializationRebuildPlanningDiagnosticCodes.LeafPlanTargetMismatch,
                    "A leaf plan's target capability descriptor differs from the exact pinned backend-pool member.",
                    "/leafPlans",
                    LinkStage,
                    request,
                    [$"pool:{placement.Pool.DefinitionFingerprint.Value}", $"leaf:{leaf.Fingerprint.Value}"],
                    pinnedTarget.Capabilities.Id.Value,
                    leaf.Target.Capabilities.Id.Value));
            }
        }

        var sliceTargets = placement.Slices.Select(static slice => slice.Target).ToHashSet();
        var leafTargets = leaves.Select(static leaf => leaf.Target.Id).ToHashSet();
        AddLeafDifference(
            diagnostics,
            sliceTargets.Except(leafTargets),
            MaterializationRebuildPlanningDiagnosticCodes.LeafPlanMissing,
            "No leaf plan covers one or more exact placement slices.",
            request,
            placementSource);
        AddLeafDifference(
            diagnostics,
            leafTargets.Except(sliceTargets),
            MaterializationRebuildPlanningDiagnosticCodes.LeafPlanExtra,
            "One or more leaf plans address targets outside the exact placement slices.",
            request,
            placementSource);

        if (diagnostics.Count > 0)
            return MaterializationRebuildPlanSetCompiler.Failure<MaterializationRebuildPlanSet>(diagnostics);

        var leavesByTarget = leaves.ToDictionary(static leaf => leaf.Target.Id);
        var bindings = placement.Slices.Select(slice => new MaterializationRebuildLeafPlanBinding(
            slice,
            new(leavesByTarget[slice.Target].Fingerprint))).ToImmutableArray();
        var scheduling = RealizeScheduling(request, placement);
        var planSet = new MaterializationRebuildPlanSet(
            schemaVersion: MaterializationRebuildPlanSet.CurrentSchemaVersion,
            request: MaterializationRebuildRequestReference.FromDocument(request),
            membership,
            placement,
            scheduling,
            promotion: request.Promotion,
            leafPlans: bindings,
            provenance);
        return MaterializationRebuildPlanSetCompiler.Success(planSet);
    }

    /// <summary>Re-links supplied evidence and rejects any substitution for one persisted plan-set authority.</summary>
    /// <param name="expected">Exact persisted plan set being replayed.</param>
    /// <param name="request">Exact request supplied on replay.</param>
    /// <param name="membership">Exact membership evidence supplied on replay.</param>
    /// <param name="placement">Exact placement supplied on replay.</param>
    /// <param name="leafPlans">Actual exact leaf plans supplied on replay.</param>
    /// <returns>The exactly reproduced plan set, or structured linkage/replay-conflict diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A successful relinked artifact violates its canonical contract.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical comparison or plan-set content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content has no portable representation.</exception>
    public static MaterializationRebuildPlanningResult<MaterializationRebuildPlanSet> ValidateReplay(
        MaterializationRebuildPlanSet expected,
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationTargetPlacementPlan placement,
        ImmutableArray<MaterializationRebuildPlan> leafPlans)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var linked = Link(request, membership, placement, leafPlans, expected.Provenance);
        if (!linked.IsSuccessful || linked.Artifact is not { } candidate)
            return linked;
        if (candidate.Fingerprint == expected.Fingerprint)
            return linked;

        return MaterializationRebuildPlanSetCompiler.Failure<MaterializationRebuildPlanSet>([
            MaterializationRebuildPlanSetCompiler.Error(
                MaterializationRebuildPlanningDiagnosticCodes.ReplayConflict,
                "Replay cannot substitute changed request, membership, placement, schedule, promotion, or leaf bindings.",
                "/fingerprint",
                ReplayStage,
                request,
                [$"plan-set:{expected.Fingerprint.Value}", $"candidate:{candidate.Fingerprint.Value}"],
                expected.Fingerprint.Value,
                candidate.Fingerprint.Value)
        ]);
    }

    internal static MaterializationRebuildSchedulingRealization RealizeScheduling(
        MaterializationRebuildRequestDocument request,
        MaterializationTargetPlacementPlan placement)
    {
        if (placement.Slices.IsEmpty)
        {
            return new(
                maximumStartsPerActivation: 0,
                maximumParallelism: 0,
                configuration:
                [
                    new(MaterializationRebuildSchedulingSettingNames.MaximumStartsPerActivation, EffectiveConfigurationOrigin.FrameworkDefault, SchedulingConvention),
                    new(MaterializationRebuildSchedulingSettingNames.MaximumParallelism, EffectiveConfigurationOrigin.FrameworkDefault, SchedulingConvention)
                ]);
        }

        var starts = Math.Min(request.Scheduling.MaximumStartsPerActivation, placement.Slices.Length);
        var physicalParallelism = placement.CapacityDomains.Sum(static domain => (long)domain.MaximumParallelism);
        var parallelism = (int)Math.Min(
            request.Scheduling.MaximumParallelism,
            Math.Min(placement.Slices.Length, physicalParallelism));
        var requestAuthority = $"request:{request.Fingerprint.Value}";
        var startsDecision = starts == request.Scheduling.MaximumStartsPerActivation
            ? new EffectiveConfigurationDecision(
                MaterializationRebuildSchedulingSettingNames.MaximumStartsPerActivation,
                EffectiveConfigurationOrigin.Explicit,
                requestAuthority)
            : new(
                MaterializationRebuildSchedulingSettingNames.MaximumStartsPerActivation,
                EffectiveConfigurationOrigin.FrameworkDefault,
                SchedulingConvention);
        EffectiveConfigurationDecision parallelismDecision;
        if (parallelism == request.Scheduling.MaximumParallelism)
        {
            parallelismDecision = new(
                MaterializationRebuildSchedulingSettingNames.MaximumParallelism,
                EffectiveConfigurationOrigin.Explicit,
                requestAuthority);
        }
        else if (parallelism == physicalParallelism && physicalParallelism < placement.Slices.Length)
        {
            parallelismDecision = new(
                MaterializationRebuildSchedulingSettingNames.MaximumParallelism,
                EffectiveConfigurationOrigin.AdapterConvention,
                $"placement:{placement.Fingerprint.Value}");
        }
        else
        {
            parallelismDecision = new(
                MaterializationRebuildSchedulingSettingNames.MaximumParallelism,
                EffectiveConfigurationOrigin.FrameworkDefault,
                SchedulingConvention);
        }

        return new(starts, parallelism, [startsDecision, parallelismDecision]);
    }

    static void AddLeafDifference(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        IEnumerable<MaterializationTargetId> targets,
        string code,
        string message,
        MaterializationRebuildRequestDocument request,
        string placementSource)
    {
        var values = targets.Select(static target => target.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length == 0)
            return;
        diagnostics.Add(MaterializationRebuildPlanSetCompiler.Error(
            code,
            message,
            "/leafPlans",
            LinkStage,
            request,
            [placementSource],
            "exactly one leaf plan per placement target",
            string.Join(",", values)));
    }

}
