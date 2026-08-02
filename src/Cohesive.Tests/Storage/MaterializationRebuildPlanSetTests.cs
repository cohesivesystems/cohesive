using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanSetTests
{
    [Fact]
    public void CanonicalArtifacts_AreInvariantAcrossSeededInputPermutations()
    {
        var leaves = CreateLeaves();
        var pool = CreatePool(leaves, reverseMembers: false);
        var subjects = Enumerable.Range(0, 6)
            .Select(static index => new MaterializationPlacementSubjectId($"subject/{index}"))
            .ToArray();
        byte[]? expectedRequest = null;
        byte[]? expectedMembership = null;
        byte[]? expectedPlacement = null;
        byte[]? expectedPlanSet = null;

        for (var seed = 0; seed < 8; seed++)
        {
            var random = new Random(seed);
            var request = CreateRequest(
                leaves[0].Materialization,
                pool,
                new MaterializationExplicitPlacementSubjectSelection(Shuffle(subjects, random)),
                source: "tests/ari-192/request/permutation");
            var membership = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
                request,
                Shuffle(subjects, random),
                Authority("revision/permutation", "cut/permutation"),
                Provenance("tests/ari-192/membership/permutation")));
            var orderedLeaves = leaves.OrderBy(static leaf => leaf.Target.Id.Value, StringComparer.Ordinal).ToArray();
            var assignments = subjects.Select((subject, index) => new MaterializationTargetPlacementAssignment(
                subject,
                orderedLeaves[index % orderedLeaves.Length].Target.Id)).ToArray();
            var domains = new[]
            {
                new MaterializationPhysicalCapacityDomain(new("capacity/permutation/a"), 1, ["tests/capacity/permutation/a"]),
                new MaterializationPhysicalCapacityDomain(new("capacity/permutation/b"), 1, ["tests/capacity/permutation/b"])
            };
            var capacityAssignments = new[]
            {
                new MaterializationTargetCapacityAssignment(orderedLeaves[0].Target.Id, domains[0].Id),
                new MaterializationTargetCapacityAssignment(orderedLeaves[1].Target.Id, domains[1].Id)
            };
            var placement = AssertSuccessful(MaterializationRebuildPlanSetCompiler.CompilePlacement(
                request,
                membership,
                pool,
                Shuffle(assignments, random),
                Shuffle(domains, random),
                Shuffle(capacityAssignments, random),
                Provenance("tests/ari-192/placement/permutation")));
            var boundLeaves = BindLeavesToPlacement(leaves, placement);
            var planSet = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
                request,
                membership,
                placement,
                Shuffle(boundLeaves, random),
                Provenance("tests/ari-192/link/permutation")));

            expectedRequest ??= MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(request);
            expectedMembership ??= MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(membership);
            expectedPlacement ??= MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(placement);
            expectedPlanSet ??= MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(planSet);
            Assert.Equal(expectedRequest, MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(request));
            Assert.Equal(expectedMembership, MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(membership));
            Assert.Equal(expectedPlacement, MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(placement));
            Assert.Equal(expectedPlanSet, MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(planSet));
        }
    }

    [Fact]
    public void PlanningPipeline_IsOrderIndependentAndRoundTripsEveryAuthority()
    {
        var forward = CreateScenario(reverseInputs: false);
        var reverse = CreateScenario(reverseInputs: true);

        Assert.Equal(forward.Request.Fingerprint, reverse.Request.Fingerprint);
        Assert.Equal(forward.Membership.Fingerprint, reverse.Membership.Fingerprint);
        Assert.Equal(forward.Placement.Fingerprint, reverse.Placement.Fingerprint);
        Assert.Equal(forward.PlanSet.Fingerprint, reverse.PlanSet.Fingerprint);
        Assert.NotEqual(forward.Request.Fingerprint.Value, forward.PlanSet.Fingerprint.Value);
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(forward.Request),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(reverse.Request));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(forward.Membership),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(reverse.Membership));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(forward.Placement),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(reverse.Placement));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(forward.PlanSet),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(reverse.PlanSet));

        var request = MaterializationRebuildPlanningJsonSerializer.DeserializeRequest(
            MaterializationRebuildPlanningJsonSerializer.SerializeRequest(forward.Request));
        var membership = MaterializationRebuildPlanningJsonSerializer.DeserializeMembership(
            MaterializationRebuildPlanningJsonSerializer.SerializeMembership(forward.Membership));
        var placement = MaterializationRebuildPlanningJsonSerializer.DeserializePlacement(
            MaterializationRebuildPlanningJsonSerializer.SerializePlacement(forward.Placement));
        var planSet = MaterializationRebuildPlanningJsonSerializer.DeserializePlanSet(
            MaterializationRebuildPlanningJsonSerializer.SerializePlanSet(forward.PlanSet));

        Assert.Equal(forward.Request.Fingerprint, request.Fingerprint);
        Assert.Equal(forward.Request.Promotion, request.Promotion);
        Assert.Equal(forward.Membership.Fingerprint, membership.Fingerprint);
        Assert.Equal(forward.Membership.Authority, membership.Authority);
        Assert.Equal(forward.Placement.Fingerprint, placement.Fingerprint);
        Assert.Equal(forward.Placement.Slices.Select(static slice => slice.Fingerprint),
            placement.Slices.Select(static slice => slice.Fingerprint));
        Assert.Equal(forward.PlanSet.Fingerprint, planSet.Fingerprint);
        Assert.Equal("cohesive-materialization-rebuild-plan-set/v2", planSet.SchemaVersion);
        Assert.Equal(
            "cohesive-materialization-rebuild-plan-set/v2-c14n/v1",
            planSet.Fingerprint.Canonicalization);
        Assert.Equal(forward.PlanSet.Request, planSet.Request);
        Assert.Equal(forward.PlanSet.LeafPlans.Select(static binding => binding.LeafPlan),
            planSet.LeafPlans.Select(static binding => binding.LeafPlan));
        Assert.True(forward.PlanSet.Scheduling.Configuration.SequenceEqual(planSet.Scheduling.Configuration));
        Assert.Equal(forward.Request.Provenance, request.Provenance);
        Assert.Equal(forward.Membership.Provenance, membership.Provenance);
        Assert.Equal(forward.Placement.Provenance, placement.Provenance);
        Assert.Equal(forward.PlanSet.Provenance, planSet.Provenance);
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(forward.Request),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(request));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(forward.Membership),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalMembershipBytes(membership));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(forward.Placement),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlacementBytes(placement));
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(forward.PlanSet),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(planSet));

        Assert.Equal(
            forward.Placement.Slices.Select(static slice => slice.Id),
            forward.Placement.CapacityBindings.Select(static binding => binding.Slice));
        Assert.Equal(
            forward.Placement.Slices.Select(static slice => slice.Id),
            forward.PlanSet.LeafPlans.Select(static binding => binding.Slice.Id));
    }

    [Fact]
    public void CapacityEvidence_ChangesPlacementButNotPromotionSliceIdentity()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var replacementDomains = ImmutableArray.Create(
            new MaterializationPhysicalCapacityDomain(
                id: new("capacity/replacement"),
                maximumParallelism: 1,
                evidenceReferences: ["tests/capacity/replacement"]));
        var replacementMappings = scenario.Leaves.Select(leaf => new MaterializationTargetCapacityAssignment(
            target: leaf.Target.Id,
            capacityDomain: replacementDomains[0].Id)).ToImmutableArray();

        var result = MaterializationRebuildPlanSetCompiler.CompilePlacement(
            scenario.Request,
            scenario.Membership,
            scenario.Pool,
            scenario.Assignments,
            replacementDomains,
            replacementMappings,
            Provenance("tests/ari-192/placement/replacement"));

        var replacement = AssertSuccessful(result);
        Assert.NotEqual(scenario.Placement.Fingerprint, replacement.Fingerprint);
        Assert.Equal(
            scenario.Placement.Slices.Select(static slice => slice.Fingerprint),
            replacement.Slices.Select(static slice => slice.Fingerprint));
        Assert.NotEqual(
            scenario.Placement.CapacityBindings.Select(static binding => binding.CapacityDomain),
            replacement.CapacityBindings.Select(static binding => binding.CapacityDomain));
    }

    [Fact]
    public void FreezeMembership_ReturnsStructuredDuplicateMissingExtraAndCompletenessDiagnostics()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var incomplete = new MaterializationRebuildMembershipAuthority(
            authority: "membership/authority",
            revision: "revision/2",
            cut: "cut/2",
            completeness: MaterializationRebuildMembershipCompleteness.Incomplete,
            evidenceReferences: ["tests/membership/incomplete"]);

        var result = MaterializationRebuildPlanSetCompiler.FreezeMembership(
            scenario.Request,
            [new("subject/a"), new("subject/a"), new("subject/extra")],
            incomplete,
            Provenance("tests/ari-192/membership/invalid"));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.MembershipDuplicate);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.MembershipMissing);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.MembershipExtra);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.MembershipIncomplete);
        Assert.All(result.Diagnostics, static diagnostic =>
        {
            Assert.NotNull(diagnostic.Evidence);
            Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
        });

        var diagnosticJson = JsonSerializer.Serialize(
            new DocumentValidationResult(result.Diagnostics),
            MaterializationRebuildPlanningJsonSerializer.CreateOptions());
        var restored = JsonSerializer.Deserialize<DocumentValidationResult>(
            diagnosticJson,
            MaterializationRebuildPlanningJsonSerializer.CreateOptions());
        Assert.NotNull(restored);
        Assert.Equal(result.Diagnostics.Select(static diagnostic => diagnostic.Code),
            restored!.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.Equal(result.Diagnostics.Select(static diagnostic => diagnostic.Evidence?.Expected),
            restored.Diagnostics.Select(static diagnostic => diagnostic.Evidence?.Expected));
    }

    [Fact]
    public void CompilePlacement_ReturnsStructuredCoveragePoolAndCapacityDiagnostics()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var domain = new MaterializationPhysicalCapacityDomain(
            id: new("capacity/duplicate"),
            maximumParallelism: 1,
            evidenceReferences: ["tests/capacity/duplicate"]);
        var result = MaterializationRebuildPlanSetCompiler.CompilePlacement(
            scenario.Request,
            scenario.Membership,
            scenario.Pool,
            assignments:
            [
                new(new("subject/a"), scenario.Leaves[0].Target.Id),
                new(new("subject/a"), scenario.Leaves[1].Target.Id),
                new(new("subject/extra"), new("target/outside-pool"))
            ],
            capacityDomains: [domain, domain],
            capacityAssignments:
            [
                new(scenario.Leaves[0].Target.Id, domain.Id),
                new(scenario.Leaves[0].Target.Id, domain.Id),
                new(new("target/unused"), new("capacity/undeclared"))
            ],
            provenance: Provenance("tests/ari-192/placement/invalid"));

        Assert.False(result.IsSuccessful);
        var codes = result.Diagnostics.Select(static diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.AssignmentDuplicate, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.AssignmentMissing, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.AssignmentExtra, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.TargetOutsidePool, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.CapacityDomainDuplicate, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingDuplicate, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingMissing, codes);
        Assert.Contains(MaterializationRebuildPlanningDiagnosticCodes.CapacityMappingExtra, codes);
    }

    [Fact]
    public void CompilePlacement_RejectsAnotherExactBackendPool()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var otherPool = CreatePool(
            scenario.Leaves,
            reverseMembers: false,
            poolId: "pool/ari-192/other");
        var mappings = scenario.Placement.Slices.Select((slice, index) =>
            new MaterializationTargetCapacityAssignment(
                target: slice.Target,
                capacityDomain: scenario.Placement.CapacityBindings[index].CapacityDomain)).ToImmutableArray();

        var result = MaterializationRebuildPlanSetCompiler.CompilePlacement(
            scenario.Request,
            scenario.Membership,
            otherPool,
            scenario.Assignments,
            scenario.Placement.CapacityDomains,
            mappings,
            Provenance("tests/ari-192/placement/pool-mismatch"));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.PoolMismatch);
    }

    [Fact]
    public void Linker_RequiresExactOneTargetLeafCoverage()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var missing = MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [scenario.Leaves[0]],
            Provenance("tests/ari-192/link/missing"));
        var conflictingSameTarget = CloneLeaf(
            scenario.Leaves[0],
            scenario.Leaves[0].Target.Id.Value,
            profileId: scenario.Leaves[0].Target.Capabilities.Id.Value,
            provenance: Provenance("tests/ari-193/link/distinct-same-target-leaf"));
        Assert.NotEqual(scenario.Leaves[0].Fingerprint, conflictingSameTarget.Fingerprint);
        Assert.Equal(scenario.Leaves[0].Target.Id, conflictingSameTarget.Target.Id);
        var conflicting = MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [scenario.Leaves[0], conflictingSameTarget, scenario.Leaves[1]],
            Provenance("tests/ari-192/link/conflict"));
        var extraLeaf = CloneLeaf(scenario.Leaves[0], "target/outside-placement");
        var extra = MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [.. scenario.Leaves, extraLeaf],
            Provenance("tests/ari-192/link/extra"));
        var descriptorDrift = CloneLeaf(
            scenario.Leaves[0],
            scenario.Leaves[0].Target.Id.Value,
            profileId: "profile/drifted");
        var drifted = MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [descriptorDrift, scenario.Leaves[1]],
            Provenance("tests/ari-192/link/target-drift"));

        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.LeafPlanMissing);
        Assert.Contains(conflicting.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.LeafPlanConflict);
        Assert.Contains(extra.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.LeafPlanExtra);
        Assert.Contains(drifted.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.LeafPlanTargetMismatch);
    }

    [Fact]
    public void Linker_RejectsDetachedOrSubstitutedSameTargetPlacementSlices()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var retained = scenario.Leaves[0];
        MaterializationRebuildMembershipFingerprint substitutedMembership = new(
            algorithm: "sha256",
            canonicalization: "tests/ari-193/substituted-membership/v1",
            value: new string('e', 64));
        var substitutedSlice = MaterializationPlacementSliceReference.Create(
            retained.PlacementSlice.Materialization,
            substitutedMembership,
            retained.PlacementSlice.Pool,
            retained.Target.Id,
            retained.PlacementSlice.Subjects);
        var substituted = CloneLeaf(
            retained,
            retained.Target.Id.Value,
            profileId: retained.Target.Capabilities.Id.Value,
            placementSlice: substitutedSlice);

        var result = MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            scenario.Placement,
            [substituted, scenario.Leaves[1]],
            Provenance("tests/ari-193/link/substituted-slice"));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.LeafPlanPlacementMismatch);
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildLeafPlanBinding(
            scenario.Placement.Slices[0],
            MaterializationRebuildPlanReference.FromPlan(scenario.Leaves[1])));
    }

    [Fact]
    public void LeafExecutionAuthority_RequiresExactLinkedPlanSetLeafAndFullSlice()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var leaf = scenario.Leaves[0];

        var authority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(scenario.PlanSet, leaf);

        Assert.Equal(MaterializationRebuildPlanSetReference.FromPlanSet(scenario.PlanSet), authority.PlanSet);
        Assert.Equal(MaterializationRebuildPlanReference.FromPlan(leaf), authority.LeafPlan);
        Assert.Equal(leaf.PlacementSlice, authority.PlacementSlice);
        Assert.Throws<ArgumentException>(() => MaterializationRebuildLeafExecutionAuthority.FromPlanSet(
            scenario.PlanSet,
            CreateLeaves()[0]));
        var substitutedTarget = CloneLeaf(
            leaf,
            leaf.Target.Id.Value,
            profileId: "profile/substituted-execution-target");
        var substitutedBindings = scenario.PlanSet.LeafPlans
            .Select(binding => binding.LeafPlan.Plan == leaf.Fingerprint
                ? new MaterializationRebuildLeafPlanBinding(
                    binding.Slice,
                    MaterializationRebuildPlanReference.FromPlan(substitutedTarget))
                : binding)
            .ToImmutableArray();
        MaterializationRebuildPlanSet substitutedPlanSet = new(
            schemaVersion: scenario.PlanSet.SchemaVersion,
            request: scenario.PlanSet.Request,
            membership: scenario.PlanSet.Membership,
            placement: scenario.PlanSet.Placement,
            scheduling: scenario.PlanSet.Scheduling,
            promotion: scenario.PlanSet.Promotion,
            leafPlans: substitutedBindings,
            provenance: scenario.PlanSet.Provenance);
        Assert.Throws<ArgumentException>(() => MaterializationRebuildLeafExecutionAuthority.FromPlanSet(
            substitutedPlanSet,
            substitutedTarget));
    }

    [Fact]
    public void RelationsReevaluation_CreatesNewEvidenceAndReplayRejectsSubstitution()
    {
        var leaves = CreateLeaves();
        var pool = CreatePool(leaves, reverseMembers: false);
        var request = CreateRequest(
            leaves[0].Materialization,
            pool,
            CreateRelationsSelection(leaves[0]),
            source: "tests/ari-192/request/relations");
        var firstMembership = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            [new("subject/a"), new("subject/b")],
            Authority("revision/1", "cut/1"),
            Provenance("tests/ari-192/membership/relations/1")));
        var secondMembership = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            [new("subject/a"), new("subject/c")],
            Authority("revision/2", "cut/2"),
            Provenance("tests/ari-192/membership/relations/2")));
        var staleSubstitution = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            [new("subject/a"), new("subject/b")],
            Authority("revision/3", "cut/3"),
            Provenance("tests/ari-192/membership/relations/3")));
        var firstPlacement = CompilePlacement(request, firstMembership, pool, leaves,
            [new("subject/a"), new("subject/b")]);
        var secondPlacement = CompilePlacement(request, secondMembership, pool, leaves,
            [new("subject/a"), new("subject/c")]);
        var stalePlacement = CompilePlacement(request, staleSubstitution, pool, leaves,
            [new("subject/a"), new("subject/b")]);
        var firstLeaves = BindLeavesToPlacement(leaves, firstPlacement);
        var secondLeaves = BindLeavesToPlacement(leaves, secondPlacement);
        var staleLeaves = BindLeavesToPlacement(leaves, stalePlacement);
        var expected = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            request,
            firstMembership,
            firstPlacement,
            firstLeaves,
            Provenance("tests/ari-192/link/relations")));
        var reevaluated = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            request,
            secondMembership,
            secondPlacement,
            secondLeaves,
            Provenance("tests/ari-192/link/relations")));

        Assert.Equal(request.Fingerprint, expected.Request.Request);
        Assert.NotEqual(firstMembership.Fingerprint, secondMembership.Fingerprint);
        Assert.NotEqual(expected.Fingerprint, reevaluated.Fingerprint);
        var replay = MaterializationRebuildPlanSetLinker.ValidateReplay(
            expected,
            request,
            secondMembership,
            secondPlacement,
            secondLeaves);
        Assert.False(replay.IsSuccessful);
        Assert.Contains(replay.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.ReplayConflict);
        var staleReplay = MaterializationRebuildPlanSetLinker.ValidateReplay(
            expected,
            request,
            staleSubstitution,
            stalePlacement,
            staleLeaves);
        Assert.Contains(staleReplay.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.ReplayConflict);
    }

    [Fact]
    public void ReplayRejectsChangedSchedulingAndPromotionPolicy()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var changedRequest = CreateRequest(
            scenario.Request.Materialization,
            scenario.Pool,
            scenario.Request.Selection,
            source: scenario.Request.Provenance.Source.Reference,
            scheduling: new(maximumStartsPerActivation: 1, maximumParallelism: 1),
            promotion: new(MaterializationRebuildPromotionMode.Independent));
        var changed = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            changedRequest,
            scenario.Membership,
            scenario.Placement,
            scenario.Leaves,
            scenario.PlanSet.Provenance));

        Assert.NotEqual(scenario.Request.Fingerprint, changedRequest.Fingerprint);
        Assert.NotEqual(scenario.PlanSet.Fingerprint, changed.Fingerprint);
        var replay = MaterializationRebuildPlanSetLinker.ValidateReplay(
            scenario.PlanSet,
            changedRequest,
            scenario.Membership,
            scenario.Placement,
            scenario.Leaves);
        Assert.Contains(replay.Diagnostics, diagnostic => diagnostic.Code == MaterializationRebuildPlanningDiagnosticCodes.ReplayConflict);
    }

    [Fact]
    public void SchedulingRealization_AttributesExplicitFrameworkAndAdapterBounds()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var frameworkDecisions = scenario.PlanSet.Scheduling.Configuration.ToDictionary(static decision => decision.Setting);
        Assert.Equal(
            EffectiveConfigurationOrigin.FrameworkDefault,
            frameworkDecisions[MaterializationRebuildSchedulingSettingNames.MaximumStartsPerActivation].Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.FrameworkDefault,
            frameworkDecisions[MaterializationRebuildSchedulingSettingNames.MaximumParallelism].Origin);

        var explicitRequest = CreateRequest(
            scenario.Request.Materialization,
            scenario.Pool,
            scenario.Request.Selection,
            source: "tests/ari-192/request/explicit-scheduling",
            scheduling: new(maximumStartsPerActivation: 1, maximumParallelism: 1));
        var explicitPlanSet = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            explicitRequest,
            scenario.Membership,
            scenario.Placement,
            scenario.Leaves,
            Provenance("tests/ari-192/link/explicit-scheduling")));
        Assert.All(explicitPlanSet.Scheduling.Configuration, decision =>
        {
            Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin);
            Assert.Equal($"request:{explicitRequest.Fingerprint.Value}", decision.Authority);
        });

        var sharedCapacity = new MaterializationPhysicalCapacityDomain(
            id: new("capacity/shared-limited"),
            maximumParallelism: 1,
            evidenceReferences: ["tests/capacity/shared-limited"]);
        var capacityMappings = scenario.Leaves.Select(leaf => new MaterializationTargetCapacityAssignment(
            target: leaf.Target.Id,
            capacityDomain: sharedCapacity.Id)).ToImmutableArray();
        var constrainedPlacement = AssertSuccessful(MaterializationRebuildPlanSetCompiler.CompilePlacement(
            scenario.Request,
            scenario.Membership,
            scenario.Pool,
            scenario.Assignments,
            capacityDomains: [sharedCapacity],
            capacityAssignments: capacityMappings,
            provenance: Provenance("tests/ari-192/placement/adapter-scheduling")));
        var constrainedPlanSet = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            scenario.Request,
            scenario.Membership,
            constrainedPlacement,
            scenario.Leaves,
            Provenance("tests/ari-192/link/adapter-scheduling")));
        var constrainedDecisions = constrainedPlanSet.Scheduling.Configuration.ToDictionary(static decision => decision.Setting);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            constrainedDecisions[MaterializationRebuildSchedulingSettingNames.MaximumParallelism].Origin);
        Assert.Equal(
            $"placement:{constrainedPlacement.Fingerprint.Value}",
            constrainedDecisions[MaterializationRebuildSchedulingSettingNames.MaximumParallelism].Authority);
    }

    [Fact]
    public void RelationsSelector_StrictRoundTripRetainsExactEvaluationAuthority()
    {
        var leaf = CreateLeaves()[0];
        var pool = CreatePool([leaf], reverseMembers: false);
        var selection = CreateRelationsSelection(leaf);
        var request = CreateRequest(
            leaf.Materialization,
            pool,
            selection,
            source: "tests/ari-192/request/relations-roundtrip");

        var json = MaterializationRebuildPlanningJsonSerializer.SerializeRequest(
            request,
            PortableDocumentJsonFormatting.Compact);
        var restored = MaterializationRebuildPlanningJsonSerializer.DeserializeRequest(json);

        var restoredSelection = Assert.IsType<MaterializationRelationsPlacementSubjectSelection>(restored.Selection);
        Assert.Equal(selection, restoredSelection);
        Assert.Equal(selection.Evaluation.Fingerprint, restoredSelection.Evaluation.Fingerprint);
        Assert.Equal(request.Fingerprint, restored.Fingerprint);
    }

    [Fact]
    public void QueryResultRelationsSelector_RetainsOneExactIdentityFieldDemand()
    {
        RelationQueryFieldReference identity = new(
            FederatedLoadRelationFixture.LoadSearchShapeId,
            FederatedLoadRelationFixture.SearchIdPath);
        var compilationRequest = new RelationQueryCompilationRequest(
            FederatedLoadRelationFixture.QueryDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument,
            RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    FederatedLoadRelationFixture.RowsResultId,
                    [identity])
            ]));
        var plan = RelationQueryStaticCompiler.Compile(compilationRequest).Plan
            ?? throw new InvalidOperationException("The query-result subject selector did not compile.");
        var evaluation = new RelationQueryEvaluationBuilder(
                FederatedLoadRelationFixture.QueryDocument,
                evaluation: new("evaluation/ari-192/query-result-subjects"),
                shapeDocuments: FederatedLoadRelationFixture.ShapeGraphDocuments,
                relationshipCatalogDocument: FederatedLoadRelationFixture.RelationshipCatalogDocument,
                planReference: RelationQueryCompiledPlanReference.From(plan))
            .Select(FederatedLoadRelationFixture.RowsResultId, [identity])
            .Build();
        var selection = new MaterializationRelationsPlacementSubjectSelection(evaluation);

        var leaf = CreateLeaves()[0];
        var request = CreateRequest(
            leaf.Materialization,
            CreatePool([leaf], reverseMembers: false),
            selection,
            source: "tests/ari-192/request/query-result-selector");
        var restored = MaterializationRebuildPlanningJsonSerializer.DeserializeRequest(
            MaterializationRebuildPlanningJsonSerializer.SerializeRequest(request));

        var restoredSelection = Assert.IsType<MaterializationRelationsPlacementSubjectSelection>(restored.Selection);
        Assert.Equal(evaluation.Fingerprint, restoredSelection.Evaluation.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(request),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalRequestBytes(restored));
    }

    [Fact]
    public void EmptyExplicitSelection_ProducesCanonicalZeroWorkPlanSet()
    {
        var leaf = CreateLeaves()[0];
        var pool = CreatePool([leaf], reverseMembers: false);
        var request = CreateRequest(
            leaf.Materialization,
            pool,
            new MaterializationExplicitPlacementSubjectSelection([]),
            source: "tests/ari-192/request/empty");
        var membership = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            [],
            Authority("revision/empty", "cut/empty"),
            Provenance("tests/ari-192/membership/empty")));
        var placement = AssertSuccessful(MaterializationRebuildPlanSetCompiler.CompilePlacement(
            request,
            membership,
            pool,
            assignments: [],
            capacityDomains: [],
            capacityAssignments: [],
            provenance: Provenance("tests/ari-192/placement/empty")));
        var planSet = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            request,
            membership,
            placement,
            leafPlans: [],
            provenance: Provenance("tests/ari-192/link/empty")));

        Assert.Empty(planSet.Placement.Slices);
        Assert.Empty(planSet.LeafPlans);
        Assert.Equal(0, planSet.Scheduling.MaximumStartsPerActivation);
        Assert.Equal(0, planSet.Scheduling.MaximumParallelism);
    }

    [Fact]
    public void PlanSetDeserializer_RejectsForgedFingerprintWithStructuredDiagnostic()
    {
        var planSet = CreateScenario(reverseInputs: false).PlanSet;
        var json = MaterializationRebuildPlanningJsonSerializer.SerializePlanSet(
            planSet,
            PortableDocumentJsonFormatting.Compact);
        var forged = json.Replace(planSet.Fingerprint.Value, new string('0', 64), StringComparison.Ordinal);

        var result = MaterializationRebuildPlanningJsonSerializer.TryDeserializePlanSet(forged, out var restored);

        Assert.False(result.IsValid);
        Assert.Null(restored);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "materialization.rebuildPlanning.json.invalid");
    }

    [Fact]
    public void IndependentPromotion_FullFingerprintCoordinatesCannotAliasAttributionOrCommands()
    {
        var scenario = CreateScenario(
            reverseInputs: false,
            promotion: new(MaterializationRebuildPromotionMode.Independent));
        var leaf = scenario.Leaves[0];
        var authority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(scenario.PlanSet, leaf);
        MaterializationActiveGenerationReference active = new(
            schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
            authority,
            generation: new("generation/independent-coordinate-alias"),
            targetRevision: new("1"),
            promotion: new("promotion/independent-coordinate-alias"),
            promotionFence: new("1"),
            validation: new("validation/independent-coordinate-alias"),
            activatedAtUtc: new(2026, 8, 2, 11, 0, 0, TimeSpan.Zero));
        MaterializationBackendRoutingRevision revision = new("1");
        MaterializationBackendRoutingFence fence = new("1");
        var admitIssuedAtUtc = active.ActivatedAtUtc.AddSeconds(1);
        var swapIssuedAtUtc = admitIssuedAtUtc.AddTicks(1);
        var baseline = MaterializationIndependentPromotionExecutor.CreateCommandIdentities(
            planSet: authority.PlanSet,
            leafPlan: authority.LeafPlan,
            placementSlice: authority.PlacementSlice.Fingerprint,
            activeGeneration: active,
            expectedRevision: revision,
            fence,
            admitIssuedAtUtc,
            swapIssuedAtUtc);

        MaterializationRebuildPlanSetReference planSetAlgorithmAlias = new(
            schemaVersion: authority.PlanSet.SchemaVersion,
            request: authority.PlanSet.Request,
            planSet: new(
                algorithm: "sha256-alias",
                canonicalization: authority.PlanSet.PlanSet.Canonicalization,
                value: authority.PlanSet.PlanSet.Value));
        MaterializationRebuildPlanSetReference planSetCanonicalizationAlias = new(
            schemaVersion: authority.PlanSet.SchemaVersion,
            request: authority.PlanSet.Request,
            planSet: new(
                algorithm: authority.PlanSet.PlanSet.Algorithm,
                canonicalization: "tests/independent-promotion/plan-set-alias/v1",
                value: authority.PlanSet.PlanSet.Value));
        MaterializationRebuildPlanReference leafAlgorithmAlias = new(
            plan: new(
                algorithm: "sha256-alias",
                canonicalization: authority.LeafPlan.Plan.Canonicalization,
                value: authority.LeafPlan.Plan.Value),
            placementSlice: authority.LeafPlan.PlacementSlice);
        MaterializationRebuildPlanReference leafCanonicalizationAlias = new(
            plan: new(
                algorithm: authority.LeafPlan.Plan.Algorithm,
                canonicalization: "tests/independent-promotion/leaf-alias/v1",
                value: authority.LeafPlan.Plan.Value),
            placementSlice: authority.LeafPlan.PlacementSlice);
        MaterializationPlacementSliceFingerprint sliceAlgorithmAlias = new(
            algorithm: "sha256-alias",
            canonicalization: authority.PlacementSlice.Fingerprint.Canonicalization,
            value: authority.PlacementSlice.Fingerprint.Value);
        MaterializationPlacementSliceFingerprint sliceCanonicalizationAlias = new(
            algorithm: authority.PlacementSlice.Fingerprint.Algorithm,
            canonicalization: "tests/independent-promotion/slice-alias/v1",
            value: authority.PlacementSlice.Fingerprint.Value);

        Assert.Equal(authority.PlanSet.PlanSet.Value, planSetAlgorithmAlias.PlanSet.Value);
        Assert.Equal(authority.PlanSet.PlanSet.Value, planSetCanonicalizationAlias.PlanSet.Value);
        Assert.NotEqual(
            MaterializationIndependentPromotionExecutor.ConfigurationAuthority(authority.PlanSet),
            MaterializationIndependentPromotionExecutor.ConfigurationAuthority(planSetAlgorithmAlias));
        Assert.NotEqual(
            MaterializationIndependentPromotionExecutor.ConfigurationAuthority(authority.PlanSet),
            MaterializationIndependentPromotionExecutor.ConfigurationAuthority(planSetCanonicalizationAlias));

        (MaterializationRebuildPlanSetReference PlanSet,
            MaterializationRebuildPlanReference Leaf,
            MaterializationPlacementSliceFingerprint Slice)[] aliases =
        [
            (planSetAlgorithmAlias, authority.LeafPlan, authority.PlacementSlice.Fingerprint),
            (planSetCanonicalizationAlias, authority.LeafPlan, authority.PlacementSlice.Fingerprint),
            (authority.PlanSet, leafAlgorithmAlias, authority.PlacementSlice.Fingerprint),
            (authority.PlanSet, leafCanonicalizationAlias, authority.PlacementSlice.Fingerprint),
            (authority.PlanSet,
                new(authority.LeafPlan.Plan, sliceAlgorithmAlias),
                sliceAlgorithmAlias),
            (authority.PlanSet,
                new(authority.LeafPlan.Plan, sliceCanonicalizationAlias),
                sliceCanonicalizationAlias)
        ];
        foreach (var alias in aliases)
        {
            var aliased = MaterializationIndependentPromotionExecutor.CreateCommandIdentities(
                planSet: alias.PlanSet,
                leafPlan: alias.Leaf,
                placementSlice: alias.Slice,
                activeGeneration: active,
                expectedRevision: revision,
                fence,
                admitIssuedAtUtc,
                swapIssuedAtUtc);
            Assert.NotEqual(baseline.Admit, aliased.Admit);
            Assert.NotEqual(baseline.Swap, aliased.Swap);
        }
    }

    [Fact]
    public void Generation_FullDefinitionAndLeafFingerprintCoordinatesCannotAlias()
    {
        var scenario = CreateScenario(reverseInputs: false);
        var authority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(
            scenario.PlanSet,
            scenario.Leaves[0]);
        MaterializationRebuildAttempt attempt = new(
            continuation: new(
                processInstanceId: new("process-instance/generation-coordinate-alias"),
                processAttemptId: new("process-attempt/generation-coordinate-alias/1")),
            startedAtUtc: new(2026, 8, 2, 11, 0, 0, TimeSpan.Zero));
        var baseline = MaterializationRebuildIdentities.Generation(authority, attempt);
        var definition = authority.PlacementSlice.Materialization.DefinitionFingerprint;
        var plan = authority.LeafPlan.Plan;
        MaterializationRebuildLeafExecutionAuthority[] aliases =
        [
            WithDefinitionFingerprint(authority, new(
                algorithm: "sha256-alias",
                canonicalization: definition.Canonicalization,
                value: definition.Value)),
            WithDefinitionFingerprint(authority, new(
                algorithm: definition.Algorithm,
                canonicalization: "tests/materialization-generation/definition-alias/v1",
                value: definition.Value)),
            WithLeafFingerprint(authority, new(
                algorithm: "sha256-alias",
                canonicalization: plan.Canonicalization,
                value: plan.Value)),
            WithLeafFingerprint(authority, new(
                algorithm: plan.Algorithm,
                canonicalization: "tests/materialization-generation/leaf-alias/v1",
                value: plan.Value))
        ];

        Assert.StartsWith("materialization-rebuild/v2/generation/", baseline.Value);
        Assert.Equal(
            baseline,
            MaterializationRebuildIdentities.Generation(scenario.Leaves[0], attempt));
        Assert.All(aliases, alias => Assert.NotEqual(
            baseline,
            MaterializationRebuildIdentities.Generation(alias, attempt)));
    }

    static MaterializationRebuildLeafExecutionAuthority WithLeafFingerprint(
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationRebuildPlanFingerprint fingerprint) =>
        new(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            authority.PlanSet,
            new(
                authority.PlacementSlice,
                new(fingerprint, authority.PlacementSlice.Fingerprint)));

    static MaterializationRebuildLeafExecutionAuthority WithDefinitionFingerprint(
        MaterializationRebuildLeafExecutionAuthority authority,
        ExecutionDefinitionFingerprint fingerprint)
    {
        var sourceSlice = authority.PlacementSlice;
        MaterializationDefinitionReference materialization = new(
            MaterializationDefinitionReference.CurrentSchemaVersion,
            sourceSlice.Materialization.Materialization,
            fingerprint);
        MaterializationBackendPoolReference pool = new(
            MaterializationBackendPoolReference.CurrentSchemaVersion,
            sourceSlice.Pool.Pool,
            materialization,
            sourceSlice.Pool.DefinitionFingerprint);
        MaterializationPlacementSliceReference slice = new(
            MaterializationPlacementSliceReference.CurrentSchemaVersion,
            sourceSlice.Id,
            materialization,
            sourceSlice.Membership,
            pool,
            sourceSlice.Target,
            sourceSlice.Subjects);
        MaterializationRebuildRequestReference request = new(
            MaterializationRebuildRequestReference.CurrentSchemaVersion,
            materialization,
            authority.PlanSet.Request.Request);
        MaterializationRebuildPlanSetReference planSet = new(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request,
            authority.PlanSet.PlanSet);
        return new(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet,
            new(
                slice,
                new(authority.LeafPlan.Plan, slice.Fingerprint)));
    }

    [Fact]
    public async Task IndependentPromotion_RejectsForeignAuthorityBeforeRouterIo()
    {
        var progressive = CreateScenario(reverseInputs: false);
        Assert.Throws<ArgumentException>(() => new MaterializationIndependentPromotionExecutor(
            progressive.PlanSet,
            progressive.Leaves[0]));

        var scenario = CreateScenario(
            reverseInputs: false,
            promotion: new(MaterializationRebuildPromotionMode.Independent));
        var leaf = scenario.Leaves[0];
        var binding = scenario.PlanSet.LeafPlans.Single(candidate => candidate.LeafPlan.Plan == leaf.Fingerprint);
        var leafAuthority = MaterializationRebuildLeafExecutionAuthority.FromPlanSet(scenario.PlanSet, leaf);
        MaterializationActiveGenerationReference active = new(
            schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
            authority: leafAuthority,
            generation: new("generation/independent-promotion"),
            targetRevision: new("1"),
            promotion: new("promotion/independent"),
            promotionFence: new("1"),
            validation: new("validation/independent"),
            activatedAtUtc: new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        MaterializationBackendRoutingSnapshot snapshot = new(
            placementSlice: binding.Slice,
            revision: MaterializationBackendRoutingRevision.Initial,
            latestFence: null,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: []);
        var executor = new MaterializationIndependentPromotionExecutor(scenario.PlanSet, leaf);
        var valid = executor.CreateRequest(active, snapshot, new("1"), active.ActivatedAtUtc.AddSeconds(1));
        MaterializationBackendRoutingSnapshot refreshedSnapshot = new(
            placementSlice: binding.Slice,
            revision: new("1"),
            latestFence: new("2"),
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: []);
        var refreshed = executor.CreateRequest(
            active,
            refreshedSnapshot,
            new("2"),
            valid.AdmitIssuedAtUtc.AddSeconds(1));
        Assert.NotEqual(valid.AdmitCommandId, refreshed.AdmitCommandId);
        Assert.NotEqual(valid.SwapCommandId, refreshed.SwapCommandId);
        MaterializationActiveGenerationReference distinctEvidence = new(
            schemaVersion: active.SchemaVersion,
            authority: active.Authority,
            generation: active.Generation,
            targetRevision: active.TargetRevision,
            promotion: new("promotion/independent-distinct-evidence"),
            promotionFence: active.PromotionFence,
            validation: active.Validation,
            activatedAtUtc: active.ActivatedAtUtc);
        var evidenceChanged = executor.CreateRequest(
            distinctEvidence,
            snapshot,
            new("1"),
            valid.AdmitIssuedAtUtc);
        Assert.NotEqual(valid.AdmitCommandId, evidenceChanged.AdmitCommandId);
        Assert.NotEqual(valid.SwapCommandId, evidenceChanged.SwapCommandId);
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.CreateRequest(
            active,
            snapshot,
            new("1"),
            DateTimeOffset.MaxValue));
        var json = MaterializationIndependentPromotionRequestJsonSerializer.Serialize(valid);
        var restored = MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(json);
        Assert.Equal(valid, restored);
        Assert.Equal("cohesive-materialization-independent-promotion-request/v2", restored.SchemaVersion);
        Assert.StartsWith("materialization-independent-promotion/v2/admit/", restored.AdmitCommandId.Value);
        Assert.StartsWith("materialization-independent-promotion/v2/swap/", restored.SwapCommandId.Value);
        Assert.All(
            restored.Configuration.Configuration,
            decision => Assert.StartsWith(
                "cohesive.storage/materialization-independent-promotion/configuration/v2/plan-set/",
                decision.Authority));
        Assert.Equal(
            json,
            MaterializationIndependentPromotionRequestJsonSerializer.Serialize(restored));
        Assert.Throws<ArgumentException>(() => new MaterializationIndependentPromotionRequest(
            schemaVersion: valid.SchemaVersion,
            activeGeneration: valid.ActiveGeneration,
            configuration: valid.Configuration,
            expectedRoutingRevision: valid.ExpectedRoutingRevision,
            fence: valid.Fence,
            admitCommandId: valid.AdmitCommandId,
            swapCommandId: valid.SwapCommandId,
            admitIssuedAtUtc: valid.ActiveGeneration.ActivatedAtUtc.AddTicks(-1),
            swapIssuedAtUtc: valid.ActiveGeneration.ActivatedAtUtc));

        var unknownMember = JsonNode.Parse(json)!.AsObject();
        unknownMember["unexpected"] = true;
        Assert.Throws<JsonException>(() => MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(
            unknownMember.ToJsonString()));
        var missingFence = JsonNode.Parse(json)!.AsObject();
        missingFence.Remove("fence");
        Assert.Throws<JsonException>(() => MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(
            missingFence.ToJsonString()));
        var foreignSchema = JsonNode.Parse(json)!.AsObject();
        foreignSchema["schemaVersion"] = "cohesive-materialization-independent-promotion-request/v1";
        Assert.Throws<JsonException>(() => MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(
            foreignSchema.ToJsonString()));
        var substitutedSlice = JsonNode.Parse(json)!.AsObject();
        substitutedSlice["activeGeneration"]!["authority"]!["binding"]!["slice"]!["subjects"]![0] =
            "subject/substituted";
        Assert.Throws<JsonException>(() => MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(
            substitutedSlice.ToJsonString()));
        var substitutedConfiguration = JsonNode.Parse(json)!.AsObject();
        substitutedConfiguration["configuration"]!["readTarget"] = "target/substituted";
        Assert.Throws<JsonException>(() => MaterializationIndependentPromotionRequestJsonSerializer.Deserialize(
            substitutedConfiguration.ToJsonString()));

        MaterializationRebuildPlanSetReference foreignPlanSet = new(
            schemaVersion: MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request: valid.Authority.PlanSet.Request,
            planSet: new(
                valid.Authority.PlanSet.PlanSet.Algorithm,
                valid.Authority.PlanSet.PlanSet.Canonicalization,
                new string('0', 64)));
        MaterializationRebuildLeafExecutionAuthority foreignAuthority = new(
            schemaVersion: MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet: foreignPlanSet,
            binding: valid.Authority.Binding);
        MaterializationActiveGenerationReference foreignActive = new(
            schemaVersion: valid.ActiveGeneration.SchemaVersion,
            authority: foreignAuthority,
            generation: valid.ActiveGeneration.Generation,
            targetRevision: valid.ActiveGeneration.TargetRevision,
            promotion: valid.ActiveGeneration.Promotion,
            promotionFence: valid.ActiveGeneration.PromotionFence,
            validation: valid.ActiveGeneration.Validation,
            activatedAtUtc: valid.ActiveGeneration.ActivatedAtUtc);
        var foreignConfiguration = MaterializationBackendRoutingConfigurationResolver.Resolve(
            scenario.Pool.Definition,
            new MaterializationBackendRoutingConfigurationLayer(
                origin: EffectiveConfigurationOrigin.Explicit,
                authority: MaterializationIndependentPromotionExecutor.ConfigurationAuthority(foreignPlanSet),
                settings: new(readTarget: binding.Slice.Target, writeTarget: binding.Slice.Target)));
        MaterializationIndependentPromotionRequest foreign = new(
            schemaVersion: valid.SchemaVersion,
            activeGeneration: foreignActive,
            configuration: foreignConfiguration,
            expectedRoutingRevision: valid.ExpectedRoutingRevision,
            fence: valid.Fence,
            admitCommandId: valid.AdmitCommandId,
            swapCommandId: valid.SwapCommandId,
            admitIssuedAtUtc: valid.AdmitIssuedAtUtc,
            swapIssuedAtUtc: valid.SwapIssuedAtUtc);
        var router = new RecordingBackendRouter();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await executor.ExecuteAsync(OperationContext.Create(), foreign, router));
        Assert.Equal(0, router.CallCount);

        MaterializationIndependentPromotionRequest noncanonicalCommand = new(
            schemaVersion: valid.SchemaVersion,
            activeGeneration: valid.ActiveGeneration,
            configuration: valid.Configuration,
            expectedRoutingRevision: valid.ExpectedRoutingRevision,
            fence: valid.Fence,
            admitCommandId: new("promotion/noncanonical-admit-command"),
            swapCommandId: valid.SwapCommandId,
            admitIssuedAtUtc: valid.AdmitIssuedAtUtc,
            swapIssuedAtUtc: valid.SwapIssuedAtUtc);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await executor.ExecuteAsync(OperationContext.Create(), noncanonicalCommand, router));
        Assert.Equal(0, router.CallCount);
    }

    [Fact]
    public async Task IndependentPromotion_ReportsPerSlicePartialSuccessWithoutCoordinatedProjection()
    {
        var scenario = CreateScenario(
            reverseInputs: false,
            promotion: new(MaterializationRebuildPromotionMode.Independent));
        var failedSlice = scenario.PlanSet.LeafPlans[1].Slice;
        IndependentOutcomeRouter router = new(failedSlice.Fingerprint);
        var outcomes = ImmutableArray.CreateBuilder<MaterializationIndependentPromotionResult>(2);
        for (var index = 0; index < scenario.Leaves.Length; index++)
        {
            var leaf = scenario.Leaves[index];
            var binding = scenario.PlanSet.LeafPlans.Single(candidate => candidate.LeafPlan.Plan == leaf.Fingerprint);
            MaterializationActiveGenerationReference active = new(
                schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
                authority: MaterializationRebuildLeafExecutionAuthority.FromPlanSet(scenario.PlanSet, leaf),
                generation: new($"generation/independent-partial/{index}"),
                targetRevision: new("1"),
                promotion: new($"promotion/independent-partial/{index}"),
                promotionFence: new("1"),
                validation: new($"validation/independent-partial/{index}"),
                activatedAtUtc: new(2026, 8, 2, 14, index, 0, TimeSpan.Zero));
            MaterializationBackendRoutingSnapshot initial = new(
                placementSlice: binding.Slice,
                revision: MaterializationBackendRoutingRevision.Initial,
                latestFence: null,
                activeRead: null,
                activeWrite: null,
                candidate: null,
                draining: [],
                retired: [],
                cleaned: []);
            var executor = new MaterializationIndependentPromotionExecutor(scenario.PlanSet, leaf);
            var request = executor.CreateRequest(
                active,
                initial,
                new("1"),
                active.ActivatedAtUtc.AddSeconds(1));
            outcomes.Add(await executor.ExecuteAsync(OperationContext.Create(), request, router));
        }

        var selected = outcomes[0];
        var failed = outcomes[1];
        Assert.True(selected.IsCurrentlySelected);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, selected.Admission.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, selected.Routing!.Disposition);
        Assert.False(failed.IsCurrentlySelected);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, failed.Admission.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, failed.Routing!.Disposition);
        Assert.Null(failed.Routing.Receipt);
        Assert.NotEqual(selected.PlacementSlice.Fingerprint, failed.PlacementSlice.Fingerprint);
        Assert.Equal(selected.Generation, selected.Routing.Snapshot.ActiveRead!.Generation);
        Assert.Null(failed.Routing.Snapshot.ActiveRead);
    }

    static Scenario CreateScenario(
        bool reverseInputs,
        MaterializationRebuildPromotionPolicy? promotion = null)
        => CreateScenario(CreateLeaves(), reverseInputs, promotion);

    internal static (
        MaterializationRebuildPlanSet PlanSet,
        ImmutableArray<MaterializationRebuildPlan> Leaves) CreateIndependentTwoLeafScenario(
        MaterializationRebuildPlan firstLeaf)
    {
        ArgumentNullException.ThrowIfNull(firstLeaf);
        var leaves = ImmutableArray.Create(
            firstLeaf,
            CloneLeaf(firstLeaf, "tests/rebuild-process-target/secondary"));
        var scenario = CreateScenario(
            leaves,
            reverseInputs: false,
            promotion: new(MaterializationRebuildPromotionMode.Independent));
        return (scenario.PlanSet, scenario.Leaves);
    }

    static Scenario CreateScenario(
        ImmutableArray<MaterializationRebuildPlan> leaves,
        bool reverseInputs,
        MaterializationRebuildPromotionPolicy? promotion)
    {
        if (reverseInputs)
            leaves = [.. leaves.Reverse()];
        var pool = CreatePool(leaves, reverseMembers: reverseInputs);
        MaterializationPlacementSubjectId first = new("subject/a");
        MaterializationPlacementSubjectId second = new("subject/b");
        var members = reverseInputs
            ? ImmutableArray.Create(second, first)
            : ImmutableArray.Create(first, second);
        var request = CreateRequest(
            leaves[0].Materialization,
            pool,
            new MaterializationExplicitPlacementSubjectSelection(members),
            source: "tests/ari-192/request",
            promotion: promotion);
        var membership = AssertSuccessful(MaterializationRebuildPlanSetCompiler.FreezeMembership(
            request,
            members,
            Authority("revision/1", "cut/1"),
            Provenance("tests/ari-192/membership")));
        var leavesByTarget = leaves.OrderBy(static leaf => leaf.Target.Id.Value, StringComparer.Ordinal).ToArray();
        var assignments = ImmutableArray.Create(
            new MaterializationTargetPlacementAssignment(first, leavesByTarget[0].Target.Id),
            new MaterializationTargetPlacementAssignment(second, leavesByTarget[1].Target.Id));
        var domains = ImmutableArray.Create(
            new MaterializationPhysicalCapacityDomain(new("capacity/a"), 1, ["tests/capacity/a"]),
            new MaterializationPhysicalCapacityDomain(new("capacity/b"), 2, ["tests/capacity/b"]));
        var capacityAssignments = ImmutableArray.Create(
            new MaterializationTargetCapacityAssignment(leavesByTarget[0].Target.Id, domains[0].Id),
            new MaterializationTargetCapacityAssignment(leavesByTarget[1].Target.Id, domains[1].Id));
        if (reverseInputs)
        {
            assignments = [.. assignments.Reverse()];
            domains = [.. domains.Reverse()];
            capacityAssignments = [.. capacityAssignments.Reverse()];
        }

        var placement = AssertSuccessful(MaterializationRebuildPlanSetCompiler.CompilePlacement(
            request,
            membership,
            pool,
            assignments,
            domains,
            capacityAssignments,
            Provenance("tests/ari-192/placement")));
        leaves = BindLeavesToPlacement(leaves, placement);
        var planSet = AssertSuccessful(MaterializationRebuildPlanSetLinker.Link(
            request,
            membership,
            placement,
            leaves,
            Provenance("tests/ari-192/link")));
        return new(request, membership, pool, assignments, leaves, placement, planSet);
    }

    static MaterializationTargetPlacementPlan CompilePlacement(
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationBackendPoolDocument pool,
        ImmutableArray<MaterializationRebuildPlan> leaves,
        ImmutableArray<MaterializationPlacementSubjectId> subjects)
    {
        var targets = leaves.OrderBy(static leaf => leaf.Target.Id.Value, StringComparer.Ordinal).ToArray();
        var assignments = subjects.Select((subject, index) => new MaterializationTargetPlacementAssignment(
            subject,
            targets[index % targets.Length].Target.Id)).ToImmutableArray();
        var domain = new MaterializationPhysicalCapacityDomain(
            new("capacity/shared"),
            maximumParallelism: targets.Length,
            evidenceReferences: ["tests/capacity/shared"]);
        var mappings = targets.Select(leaf => new MaterializationTargetCapacityAssignment(
            leaf.Target.Id,
            domain.Id)).ToImmutableArray();
        return AssertSuccessful(MaterializationRebuildPlanSetCompiler.CompilePlacement(
            request,
            membership,
            pool,
            assignments,
            [domain],
            mappings,
            Provenance("tests/ari-192/placement/relations")));
    }

    static MaterializationRebuildRequestDocument CreateRequest(
        MaterializationDocument materialization,
        MaterializationBackendPoolDocument pool,
        MaterializationPlacementSubjectSelection selection,
        string source,
        MaterializationRebuildSchedulingPolicy? scheduling = null,
        MaterializationRebuildPromotionPolicy? promotion = null) =>
        new(
            schemaVersion: MaterializationRebuildRequestDocument.CurrentSchemaVersion,
            materialization,
            selection,
            placement: new(MaterializationBackendPoolReference.FromDocument(pool)),
            scheduling: scheduling ?? new(maximumStartsPerActivation: 8, maximumParallelism: 8),
            promotion: promotion ?? new(
                MaterializationRebuildPromotionMode.AllReadyProgressive,
                MaterializationProgressivePromotionFailurePolicy.RetainPromotedAndStop),
            provenance: Provenance(source));

    static MaterializationBackendPoolDocument CreatePool(
        ImmutableArray<MaterializationRebuildPlan> leaves,
        bool reverseMembers,
        string poolId = "pool/ari-192")
    {
        var members = leaves.Select(static leaf => leaf.Target).ToImmutableArray();
        if (reverseMembers)
            members = [.. members.Reverse()];
        var materialization = leaves[0].Materialization;
        return MaterializationBackendPoolDocument.FromDefinition(new(
            id: new(poolId),
            materializationId: materialization.Definition.Id,
            definitionFingerprint: materialization.DefinitionFingerprint,
            members,
            defaultTarget: members[0].Id,
            provenance: Provenance("tests/ari-192/pool")));
    }

    static ImmutableArray<MaterializationRebuildPlan> CreateLeaves()
    {
        var first = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        return [first, CloneLeaf(first, "target/loads-search-b")];
    }

    static MaterializationRebuildPlan CloneLeaf(
        MaterializationRebuildPlan source,
        string targetId,
        string? profileId = null,
        MaterializationPlacementSliceReference? placementSlice = null,
        ExecutionProvenance? provenance = null)
    {
        MaterializationTargetId id = new(targetId);
        var profile = new MaterializationCapabilityProfile(
            id: new(profileId ?? $"profile/{targetId}"),
            role: MaterializationEndpointRole.Target,
            subject: id.Value,
            evidence: source.Target.Capabilities.Evidence);
        var target = new MaterializationTargetDescriptor(id, source.Materialization.Definition.Id, profile);
        var match = MaterializationCapabilityMatcher.MatchForMode(
            source.Materialization.Definition.TargetCapabilities,
            profile,
            MaterializationSynchronizationMode.Rebuild);
        return new(
            source.Materialization,
            placementSlice ?? (id == source.Target.Id
                ? source.PlacementSlice
                : MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlacementSlice(
                    source.Materialization,
                    target)),
            source.ImpactPlan,
            source.Sources,
            target,
            match,
            source.Shards,
            source.ChangeFeedCatalogs,
            source.ChangeFeeds,
            source.Limits,
            provenance ?? source.Provenance);
    }

    static ImmutableArray<MaterializationRebuildPlan> BindLeavesToPlacement(
        ImmutableArray<MaterializationRebuildPlan> leaves,
        MaterializationTargetPlacementPlan placement) =>
        [
            .. placement.Slices.Select(slice =>
            {
                var leaf = leaves.Single(candidate => candidate.Target.Id == slice.Target);
                return CloneLeaf(
                    leaf,
                    leaf.Target.Id.Value,
                    profileId: leaf.Target.Capabilities.Id.Value,
                    placementSlice: slice);
            })
        ];

    static MaterializationRelationsPlacementSubjectSelection CreateRelationsSelection(MaterializationRebuildPlan leaf)
    {
        var source = leaf.Materialization.Definition.Relation.CompilationRequest;
        var shape = leaf.Materialization.Definition.Relation.Output.Shape;
        var graph = source.ShapeDocuments.Single(document => document.Graph.Id == shape.GraphId).Graph;
        var definition = graph.TryGetShape(shape) ?? throw new InvalidOperationException("The output shape is absent.");
        var identity = definition.Fields.Single(field => field.Role == FieldRole.Identity
            && field.Cardinality == FieldCardinality.Single
            && field.Type is ScalarTypeRef { Kind: ScalarTypeKind.String });
        RelationQueryFieldReference field = new(shape, FieldPath.FromField(identity.Name.Value));
        RelationQueryCompilationRequest compilation = new(
            source.DefinitionDocument,
            source.ShapeDocuments,
            source.RelationshipCatalogDocument,
            RelationQueryCompilationDemand.ForRelationFields([field]));
        var plan = RelationQueryStaticCompiler.Compile(compilation).Plan
            ?? throw new InvalidOperationException("The subject selector did not compile.");
        var evaluation = new RelationQueryEvaluationBuilder(
                source.DefinitionDocument,
                evaluation: new("evaluation/ari-192/subjects"),
                shapeDocuments: source.ShapeDocuments,
                relationshipCatalogDocument: source.RelationshipCatalogDocument,
                planReference: RelationQueryCompiledPlanReference.From(plan))
            .Select([field])
            .Build();
        return new(evaluation);
    }

    static MaterializationRebuildMembershipAuthority Authority(string revision, string cut) =>
        new(
            authority: "membership/authority",
            revision,
            cut,
            completeness: MaterializationRebuildMembershipCompleteness.Complete,
            evidenceReferences: [$"tests/membership/{revision}/{cut}"]);

    static ExecutionProvenance Provenance(string source) =>
        new(new("cohesive-tests", "1"), new(source), DocumentOrigin.Generated);

    static T AssertSuccessful<T>(MaterializationRebuildPlanningResult<T> result)
        where T : class
    {
        Assert.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<T>(result.Artifact);
    }

    static ImmutableArray<T> Shuffle<T>(IEnumerable<T> values, Random random) =>
        [.. values.OrderBy(_ => random.Next())];

    sealed class RecordingBackendRouter : IMaterializationBackendRouter
    {
        internal int CallCount { get; private set; }

        public ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Fail<MaterializationBackendRoutingSnapshot>();

        public ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Fail<MaterializationBackendRouteBinding>();

        public ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Fail<MaterializationBackendRouteBinding>();

        public ValueTask<MaterializationBackendRoutingResult> AdmitCandidateAsync(
            OperationContext context,
            MaterializationAdmitBackendCandidateRequest request) => Fail<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> AbandonCandidateAsync(
            OperationContext context,
            MaterializationAbandonBackendCandidateRequest request) => Fail<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> SwapAsync(
            OperationContext context,
            MaterializationSwapBackendRoutingRequest request) => Fail<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> CompleteDrainAsync(
            OperationContext context,
            MaterializationCompleteBackendDrainRequest request) => Fail<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> RetireAsync(
            OperationContext context,
            MaterializationRetireBackendGenerationRequest request) => Fail<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendCleanupReservationResult> ReserveCleanupAsync(
            OperationContext context,
            MaterializationReserveBackendCleanupRequest request) => Fail<MaterializationBackendCleanupReservationResult>();

        public ValueTask<MaterializationBackendRoutingResult> CleanupAsync(
            OperationContext context,
            MaterializationCleanupBackendGenerationRequest request) => Fail<MaterializationBackendRoutingResult>();

        ValueTask<TResult> Fail<TResult>()
        {
            CallCount++;
            return ValueTask.FromException<TResult>(new InvalidOperationException("Router I/O must not be reached."));
        }
    }

    sealed class IndependentOutcomeRouter(MaterializationPlacementSliceFingerprint failedSlice)
        : IMaterializationBackendRouter
    {
        public ValueTask<MaterializationBackendRoutingResult> AdmitCandidateAsync(
            OperationContext context,
            MaterializationAdmitBackendCandidateRequest request)
        {
            var revision = Next(request.Header.ExpectedRevision);
            MaterializationBackendRoutingSnapshot snapshot = new(
                placementSlice: request.Header.PlacementSlice,
                revision,
                latestFence: request.Header.Fence,
                activeRead: null,
                activeWrite: null,
                candidate: request.Candidate,
                draining: [],
                retired: [],
                cleaned: [],
                pendingFollowUp: request.ExpectedFollowUp is null
                    ? null
                    : new(request.ExpectedFollowUp));
            MaterializationBackendRoutingReceipt receipt = new(
                request.Header.CommandId,
                request.Header.PlacementSlice,
                MaterializationBackendRoutingOperation.AdmitCandidate,
                revision,
                request.Header.Fence,
                request.Header.IssuedAtUtc);
            return ValueTask.FromResult(new MaterializationBackendRoutingResult(
                MaterializationBackendRoutingDisposition.Applied,
                snapshot,
                receipt));
        }

        public ValueTask<MaterializationBackendRoutingResult> SwapAsync(
            OperationContext context,
            MaterializationSwapBackendRoutingRequest request)
        {
            if (request.Header.PlacementSlice.Fingerprint == failedSlice)
            {
                MaterializationBackendRoutingSnapshot rejected = new(
                    placementSlice: request.Header.PlacementSlice,
                    revision: request.Header.ExpectedRevision,
                    latestFence: request.Header.Fence,
                    activeRead: null,
                    activeWrite: null,
                    candidate: request.Write,
                    draining: [],
                    retired: [],
                    cleaned: [],
                    pendingFollowUp: new(request));
                return ValueTask.FromResult(new MaterializationBackendRoutingResult(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    rejected,
                    detail: "Injected independent-slice failure."));
            }

            var revision = Next(request.Header.ExpectedRevision);
            MaterializationBackendRoutingSnapshot applied = new(
                placementSlice: request.Header.PlacementSlice,
                revision,
                latestFence: request.Header.Fence,
                activeRead: request.Read,
                activeWrite: request.Write,
                candidate: null,
                draining: [],
                retired: [],
                cleaned: [],
                configuration: request.Configuration);
            MaterializationBackendRoutingReceipt receipt = new(
                request.Header.CommandId,
                request.Header.PlacementSlice,
                MaterializationBackendRoutingOperation.Swap,
                revision,
                request.Header.Fence,
                request.Header.IssuedAtUtc);
            return ValueTask.FromResult(new MaterializationBackendRoutingResult(
                MaterializationBackendRoutingDisposition.Applied,
                applied,
                receipt));
        }

        public ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Unsupported<MaterializationBackendRoutingSnapshot>();

        public ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Unsupported<MaterializationBackendRouteBinding>();

        public ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(
            OperationContext context,
            MaterializationPlacementSliceReference placementSlice) => Unsupported<MaterializationBackendRouteBinding>();

        public ValueTask<MaterializationBackendRoutingResult> AbandonCandidateAsync(
            OperationContext context,
            MaterializationAbandonBackendCandidateRequest request) => Unsupported<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> CompleteDrainAsync(
            OperationContext context,
            MaterializationCompleteBackendDrainRequest request) => Unsupported<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendRoutingResult> RetireAsync(
            OperationContext context,
            MaterializationRetireBackendGenerationRequest request) => Unsupported<MaterializationBackendRoutingResult>();

        public ValueTask<MaterializationBackendCleanupReservationResult> ReserveCleanupAsync(
            OperationContext context,
            MaterializationReserveBackendCleanupRequest request) => Unsupported<MaterializationBackendCleanupReservationResult>();

        public ValueTask<MaterializationBackendRoutingResult> CleanupAsync(
            OperationContext context,
            MaterializationCleanupBackendGenerationRequest request) => Unsupported<MaterializationBackendRoutingResult>();

        static MaterializationBackendRoutingRevision Next(MaterializationBackendRoutingRevision revision) =>
            new((revision.Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        static ValueTask<TResult> Unsupported<TResult>() =>
            ValueTask.FromException<TResult>(new NotSupportedException());
    }

    sealed record Scenario(
        MaterializationRebuildRequestDocument Request,
        MaterializationRebuildMembershipEvidence Membership,
        MaterializationBackendPoolDocument Pool,
        ImmutableArray<MaterializationTargetPlacementAssignment> Assignments,
        ImmutableArray<MaterializationRebuildPlan> Leaves,
        MaterializationTargetPlacementPlan Placement,
        MaterializationRebuildPlanSet PlanSet);
}
