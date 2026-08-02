using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationBackendRouterTests
{
    static readonly DateTimeOffset Epoch = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationId MaterializationId = new("materialization/backend-routing");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "cohesive-materialization-definition/v1-c14n/v1",
        new string('a', 64));
    static readonly MaterializationRebuildPlanFingerprint PlanFingerprint = new(
        "sha256",
        "tests/materialization-backend-routing-plan/v1",
        new string('b', 64));
    static readonly MaterializationBackendRoutingFence FenceOne = new("1");
    static readonly MaterializationBackendRoutingFence FenceTwo = new("2");

    [Fact]
    public async Task RoutingWire_UsesStrictClosedEnumAndOperationContracts()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var (initialRequest, initialized) = await InitializeAsync(rig);
        var options = StrictDocumentJson.CreateOptions();
        MaterializationBackendRoutingReceipt receipt = new(
            commandId: new("command/wire"),
            placementSlice: rig.Scope,
            operation: MaterializationBackendRoutingOperation.Swap,
            revision: new("1"),
            fence: FenceOne,
            committedAtUtc: Epoch);

        var json = JsonSerializer.Serialize(receipt, options);
        var restored = JsonSerializer.Deserialize<MaterializationBackendRoutingReceipt>(json, options);

        Assert.Equal(receipt, restored);
        Assert.Contains("\"operation\":\"Swap\"", json);
        MaterializationBackendRoutingReceipt reservationReceipt = new(
            commandId: new("command/wire-cleanup-reservation"),
            placementSlice: rig.Scope,
            operation: MaterializationBackendRoutingOperation.ReserveCleanup,
            revision: new("2"),
            fence: FenceOne,
            committedAtUtc: Epoch);
        MaterializationBackendCleanupReservation reservation = new(
            generation: rig.First.Generation,
            retirements: [new(rig.Scope, new("1"))],
            receipt: reservationReceipt,
            token: "cleanup-reservation/wire");
        var reservationJson = JsonSerializer.Serialize(reservation, options);
        var restoredReservation = JsonSerializer.Deserialize<MaterializationBackendCleanupReservation>(
            reservationJson,
            options);
        Assert.Equal(reservation, restoredReservation);
        var openReservation = JsonNode.Parse(reservationJson)!.AsObject();
        openReservation["unexpected"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendCleanupReservation>(
            openReservation.ToJsonString(),
            options));
        var incompleteReservation = JsonNode.Parse(reservationJson)!.AsObject();
        incompleteReservation.Remove("token");
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize<MaterializationBackendCleanupReservation>(
            incompleteReservation.ToJsonString(),
            options));
        MaterializationBackendRoutingReceipt noncausalReservationReceipt = new(
            commandId: new("command/wire-noncausal-cleanup-reservation"),
            placementSlice: rig.Scope,
            operation: MaterializationBackendRoutingOperation.ReserveCleanup,
            revision: new("1"),
            fence: FenceOne,
            committedAtUtc: Epoch);
        Assert.Throws<ArgumentException>(() => new MaterializationBackendCleanupReservation(
            generation: rig.First.Generation,
            retirements: [new(rig.Scope, new("1"))],
            receipt: noncausalReservationReceipt,
            token: "cleanup-reservation/noncausal"));
        MaterializationBackendPoolReference foreignPool = new(
            schemaVersion: MaterializationBackendPoolReference.CurrentSchemaVersion,
            pool: new("pool/backend-routing-foreign"),
            materialization: rig.Scope.Materialization,
            definitionFingerprint: new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-backend-routing-pool/v1",
                value: new string('9', 64)));
        var foreignPoolSlice = MaterializationPlacementSliceReference.Create(
            materialization: rig.Scope.Materialization,
            membership: rig.Scope.Membership,
            pool: foreignPool,
            target: rig.Scope.Target,
            subjects: rig.Scope.Subjects);
        ImmutableArray<MaterializationBackendCleanupRetirementClaim> crossPoolClaims =
        [
            .. new[]
            {
                new MaterializationBackendCleanupRetirementClaim(rig.Scope, new("1")),
                new MaterializationBackendCleanupRetirementClaim(foreignPoolSlice, new("1"))
            }.OrderBy(static claim => claim.PlacementSlice.Fingerprint.Value, StringComparer.Ordinal)
        ];
        Assert.Throws<ArgumentException>(() => new MaterializationBackendCleanupReservation(
            generation: rig.First.Generation,
            retirements: crossPoolClaims,
            receipt: reservationReceipt,
            token: "cleanup-reservation/cross-pool"));
        var headerJson = JsonSerializer.Serialize(initialRequest.Header, options);
        var restoredHeader = JsonSerializer.Deserialize<MaterializationBackendRoutingCommandHeader>(headerJson, options);
        Assert.Equal(initialRequest.Header, restoredHeader);
        Assert.DoesNotContain("poolId", headerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("poolDefinitionFingerprint", headerJson, StringComparison.Ordinal);

        var snapshotJson = JsonSerializer.Serialize(initialized.Snapshot, options);
        var restoredSnapshot = JsonSerializer.Deserialize<MaterializationBackendRoutingSnapshot>(snapshotJson, options);
        Assert.NotNull(restoredSnapshot);
        Assert.Equal(initialized.Snapshot.PlacementSlice, restoredSnapshot.PlacementSlice);
        Assert.Equal(initialized.Snapshot.Revision, restoredSnapshot.Revision);
        Assert.Equal(initialized.Snapshot.ActiveRead, restoredSnapshot.ActiveRead);
        Assert.Equal(initialized.Snapshot.ActiveWrite, restoredSnapshot.ActiveWrite);
        Assert.Equal(initialized.Snapshot.Configuration, restoredSnapshot.Configuration);

        MaterializationSwapBackendRoutingRequest reservedFollowUp = new(
            Header(
                rig,
                "command/wire-reserved-follow-up",
                new("1"),
                FenceOne,
                At(2)),
            FabricateReadableReference(rig.Scope, rig.Second.Generation, At(2)),
            rig.Second.Generation,
            Configuration(rig, rig.Second.Target, rig.Second.Target));
        MaterializationBackendRoutingSnapshot pendingSnapshot = new(
            placementSlice: rig.Scope,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: rig.Second.Generation,
            draining: [],
            retired: [],
            cleaned: [],
            pendingFollowUp: new(reservedFollowUp));
        var pendingJson = JsonSerializer.Serialize(pendingSnapshot, options);
        var stalePending = JsonNode.Parse(pendingJson)!.AsObject();
        stalePending["pendingFollowUp"]!["request"]!["header"]!["expectedRevision"] = "2";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingSnapshot>(
            stalePending.ToJsonString(),
            options));
        var foreignFencePending = JsonNode.Parse(pendingJson)!.AsObject();
        foreignFencePending["pendingFollowUp"]!["request"]!["header"]!["fence"] = "2";
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingSnapshot>(
            foreignFencePending.ToJsonString(),
            options));
        var siblingPending = JsonNode.Parse(pendingJson)!.AsObject();
        var siblingSlice = JsonSerializer.SerializeToNode(rig.AlternateScope, options)!;
        siblingPending["pendingFollowUp"]!["request"]!["header"]!["placementSlice"] = siblingSlice.DeepClone();
        siblingPending["pendingFollowUp"]!["request"]!["read"]!["placementSlice"] = siblingSlice.DeepClone();
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingSnapshot>(
            siblingPending.ToJsonString(),
            options));

        MaterializationBackendDrainProof drainProof = new(
            placementSlice: rig.Scope,
            generation: rig.First.Generation,
            admissionsClosedAtRevision: initialized.Snapshot.Revision,
            inFlightOperationCount: 0,
            quiescenceToken: "quiescence/wire",
            observedAtUtc: At(2));
        var proofJson = JsonSerializer.Serialize(drainProof, options);
        Assert.Equal(
            drainProof,
            JsonSerializer.Deserialize<MaterializationBackendDrainProof>(proofJson, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRole>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRole>("\"activeRead\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingDisposition>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingDisposition>("\"applied\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingOperation>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingOperation>("\"swap\"", options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterializationBackendRoutingReceipt(
            commandId: new("command/invalid-operation"),
            placementSlice: rig.Scope,
            operation: (MaterializationBackendRoutingOperation)int.MaxValue,
            revision: new("1"),
            fence: FenceOne,
            committedAtUtc: Epoch));
    }

    [Fact]
    public async Task IncompleteCandidate_NeverBecomesReadable()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        var admitted = await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));

        var rejected = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(rig, "command/read-incomplete", new("2"), FenceOne, At(3)),
                FabricateReadableReference(rig.Scope, rig.Second.Generation, At(3)),
                rig.Second.Generation,
                Configuration(rig, rig.Second.Target, rig.Second.Target)));

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, rejected.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("2"), rejected.Snapshot.Revision);
        Assert.Equal(rig.First.Read, rejected.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, rejected.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, rejected.Snapshot.Candidate);
        Assert.Equal(rig.First.Generation, (await rig.Router.ResolveReadAsync(Context(), rig.Scope)).Generation);
        Assert.Equal(
            MaterializationGenerationState.Loading,
            (await rig.Second.Target.InspectGenerationAsync(Context(), rig.Second.Generation.GenerationId))!.State);
    }

    [Fact]
    public async Task ExactActivationSwap_AtomicallyMovesBothRoutesAndStartsPriorDrain()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));

        var swapped = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/swap-second",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("3"), swapped.Snapshot.Revision);
        Assert.Equal(rig.Second.Read, swapped.Snapshot.ActiveRead);
        Assert.Equal(rig.Second.Generation, swapped.Snapshot.ActiveWrite);
        Assert.Null(swapped.Snapshot.Candidate);
        var drain = Assert.Single(swapped.Snapshot.Draining);
        Assert.Equal(rig.First.Generation, drain.Generation);
        Assert.Equal(swapped.Snapshot.Revision, drain.AdmissionsClosedAtRevision);
        Assert.True(
            swapped.Snapshot.GetRoles(rig.Second.Generation).SequenceEqual(
                [MaterializationBackendRole.ActiveRead, MaterializationBackendRole.ActiveWrite]));

        var read = await rig.Router.ResolveReadAsync(Context(), rig.Scope);
        var write = await rig.Router.ResolveWriteAsync(Context(), rig.Scope);
        Assert.Equal(swapped.Snapshot.Revision, read.Revision);
        Assert.Equal(swapped.Snapshot.Revision, write.Revision);
        Assert.Same(rig.Second.Target, read.Target);
        Assert.Same(rig.Second.Target, write.Target);
    }

    [Fact]
    public async Task Candidate_CanStageWriteIndependentlyWhilePriorReadRemainsPinned()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));

        var staged = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/stage-write",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.First.Read!,
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, staged.Disposition);
        Assert.Equal(rig.First.Read, staged.Snapshot.ActiveRead);
        Assert.Equal(rig.Second.Generation, staged.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, staged.Snapshot.Candidate);
        Assert.True(
            staged.Snapshot.GetRoles(rig.First.Generation).SequenceEqual(
                [MaterializationBackendRole.ActiveRead, MaterializationBackendRole.Draining]));
        Assert.True(
            staged.Snapshot.GetRoles(rig.Second.Generation).SequenceEqual(
                [MaterializationBackendRole.ActiveWrite, MaterializationBackendRole.Candidate]));
        Assert.Equal(rig.First.Generation, (await rig.Router.ResolveReadAsync(Context(), rig.Scope)).Generation);
        Assert.Equal(rig.Second.Generation, (await rig.Router.ResolveWriteAsync(Context(), rig.Scope)).Generation);
    }

    [Fact]
    public async Task ActiveWriteGeneration_CannotBeNewlyAdmittedAsARebuildCandidate()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var (_, initialized) = await InitializeAsync(rig);

        var rejected = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/admit-current-write", initialized.Snapshot.Revision, FenceOne, At(2)),
                rig.First.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, rejected.Disposition);
        Assert.Equal(initialized.Snapshot.Revision, rejected.Snapshot.Revision);
        Assert.Equal(rig.First.Generation, rejected.Snapshot.ActiveWrite);
        Assert.Null(rejected.Snapshot.Candidate);
    }

    [Fact]
    public async Task Candidate_CanMoveReadFirstThenConvergeWriteThroughTheExistingReadRoute()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));

        var readFirst = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/move-read-first",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                rig.First.Generation));
        var converged = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/converge-write",
                expectedRevision: readFirst.Snapshot.Revision,
                issuedAtUtc: At(4),
                rig.Second.Read!,
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, readFirst.Disposition);
        Assert.Null(readFirst.Snapshot.Candidate);
        Assert.Equal(rig.Second.Read, readFirst.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, readFirst.Snapshot.ActiveWrite);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, converged.Disposition);
        Assert.Equal(rig.Second.Read, converged.Snapshot.ActiveRead);
        Assert.Equal(rig.Second.Generation, converged.Snapshot.ActiveWrite);
        var drain = Assert.Single(converged.Snapshot.Draining);
        Assert.Equal(rig.First.Generation, drain.Generation);
        Assert.Equal(converged.Snapshot.Revision, drain.AdmissionsClosedAtRevision);
    }

    [Fact]
    public async Task Candidate_CannotAuthorizeAnUnadmittedGenerationInTheOtherChangedRoute()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var unadmitted = await BeginLoadingAsync(rig.First.Target, "unadmitted", At(2));

        var rejected = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/smuggle-unadmitted-write",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                unadmitted.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, rejected.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("2"), rejected.Snapshot.Revision);
        Assert.Equal(rig.First.Read, rejected.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, rejected.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, rejected.Snapshot.Candidate);
    }

    [Fact]
    public async Task AbandonedCandidate_ClearsOnlyWithTargetReceiptAndAllowsFreshRestartGeneration()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var abandonment = await rig.Second.Target.AbandonGenerationAsync(
            Context(),
            new(
                new("abandonment/backend-b"),
                rig.Second.Generation.GenerationId,
                abandonedAtUtc: At(3)));
        var abandonRequest = new MaterializationAbandonBackendCandidateRequest(
            Header(rig, "command/abandon-candidate", new("2"), FenceOne, At(4)),
            rig.Second.Generation,
            abandonment.Receipt!);

        var cleared = await rig.Router.AbandonCandidateAsync(Context(), abandonRequest);
        var fresh = await BeginLoadingAsync(rig.Second.Target, "backend-b-restart", At(5));
        var readmitted = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/admit-restart", cleared.Snapshot.Revision, FenceOne, At(6)),
                fresh.Generation));
        var replayedClear = await rig.Router.AbandonCandidateAsync(Context(), abandonRequest);

        Assert.Equal(MaterializationTargetOperationDisposition.Applied, abandonment.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, cleared.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.AbandonCandidate, cleared.Receipt!.Operation);
        Assert.Null(cleared.Snapshot.Candidate);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, readmitted.Disposition);
        Assert.Equal(fresh.Generation, readmitted.Snapshot.Candidate);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedClear.Disposition);
        Assert.Equal(cleared.Receipt, replayedClear.Receipt);
        Assert.Equal(fresh.Generation, replayedClear.Snapshot.Candidate);
    }

    [Fact]
    public async Task StagedWriteCandidate_MustLeaveRoutingBeforeAbandonmentClearsItsPoolRole()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var staged = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/stage-before-abandon",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.First.Read!,
                rig.Second.Generation));
        var abandonment = await rig.Second.Target.AbandonGenerationAsync(
            Context(),
            new(
                new("abandonment/staged-backend-b"),
                rig.Second.Generation.GenerationId,
                abandonedAtUtc: At(4)));

        var rejected = await rig.Router.AbandonCandidateAsync(
            Context(),
            new(
                Header(rig, "command/abandon-routed-candidate", staged.Snapshot.Revision, FenceOne, At(5)),
                rig.Second.Generation,
                abandonment.Receipt!));
        MaterializationBackendRollbackProof rollbackProof = new(
            rig.Scope,
            rig.First.Generation,
            staged.Snapshot.ActiveRead!,
            staged.Snapshot.ActiveWrite!,
            staged.Snapshot.Revision,
            equivalenceFingerprint: "equivalence/remove-failed-write",
            observedAtUtc: At(5));
        var rolledBack = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/remove-failed-write",
                expectedRevision: staged.Snapshot.Revision,
                issuedAtUtc: At(6),
                rig.First.Read!,
                rig.First.Generation,
                rollbackProof));
        var cleared = await rig.Router.AbandonCandidateAsync(
            Context(),
            new(
                Header(rig, "command/clear-failed-candidate", rolledBack.Snapshot.Revision, FenceOne, At(7)),
                rig.Second.Generation,
                abandonment.Receipt!));

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, rejected.Disposition);
        Assert.Equal(rig.Second.Generation, rejected.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, rejected.Snapshot.Candidate);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, rolledBack.Disposition);
        Assert.Equal(rig.First.Generation, rolledBack.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, rolledBack.Snapshot.Candidate);
        Assert.Contains(rolledBack.Snapshot.Draining, drain => drain.Generation == rig.Second.Generation);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, cleared.Disposition);
        Assert.Null(cleared.Snapshot.Candidate);
        Assert.Contains(cleared.Snapshot.Draining, drain => drain.Generation == rig.Second.Generation);
    }

    [Fact]
    public async Task Snapshot_RejectsMixedMaterializationDefinitionEvidence()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        ExecutionDefinitionFingerprint foreignFingerprint = new(
            "sha256",
            "tests/foreign-materialization-definition/v1",
            new string('c', 64));
        MaterializationBackendGenerationReference foreignWrite = new(
            rig.First.Generation.TargetId,
            rig.First.Generation.GenerationId,
            foreignFingerprint);

        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            rig.Scope,
            new("1"),
            FenceOne,
            rig.First.Read,
            foreignWrite,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: [],
            Configuration(rig, rig.First.Target, rig.First.Target)));

        var candidateOnly = new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: rig.Second.Generation,
            draining: [],
            retired: [],
            cleaned: []);
        var options = StrictDocumentJson.CreateOptions();
        var forgedDocument = JsonNode.Parse(JsonSerializer.Serialize(candidateOnly, options))!.AsObject();
        forgedDocument["candidate"]!["definitionFingerprint"]!["value"] = foreignFingerprint.Value;

        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingSnapshot>(
            forgedDocument.ToJsonString(),
            options));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRouteBinding(
            rig.Scope,
            new("1"),
            foreignWrite,
            rig.First.Target));
    }

    [Fact]
    public async Task Snapshot_RejectsImpossiblePrecommitAndFutureLifecycleEvidence()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var acceptedFenceOnly = new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: MaterializationBackendRoutingRevision.Initial,
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: []);

        Assert.Equal(FenceOne, acceptedFenceOnly.LatestFence);
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: MaterializationBackendRoutingRevision.Initial,
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: rig.Second.Generation,
            draining: [],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: new("1"),
            latestFence: default(MaterializationBackendRoutingFence),
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [new(rig.Second.Generation, new("2"))],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            placementSlice: rig.Scope,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [new(rig.Second.Generation, new("2"))],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRouteBinding(
            rig.Scope,
            MaterializationBackendRoutingRevision.Initial,
            rig.First.Generation,
            rig.First.Target));
    }

    [Fact]
    public async Task Router_ComposesRoundTrippedPoolIrWithSemanticallyExactDependencies()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var restored = MaterializationBackendPoolJsonSerializer.Deserialize(
            MaterializationBackendPoolJsonSerializer.Serialize(
                rig.Document,
                PortableDocumentJsonFormatting.Compact));
        var pool = new InMemoryMaterializationTargetPool(
            rig.Definition,
            [rig.First.Target, rig.Second.Target]);

        using var router = new InMemoryMaterializationBackendRouter(restored, pool);

        Assert.Equal(rig.Definition, restored.Definition);
        Assert.Equal(rig.Definition.GetHashCode(), restored.Definition.GetHashCode());
        Assert.Equal(restored.DefinitionFingerprint, router.Document.DefinitionFingerprint);
        Assert.Equal(
            MaterializationBackendRoutingRevision.Initial,
            (await router.InspectAsync(Context(), rig.Scope)).Revision);
    }

    [Fact]
    public async Task HigherFenceTakeover_MakesPriorOwnerStaleEvenWhenTakeoverRevisionConflicts()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);

        var takeover = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/takeover",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceTwo,
                    At(2)),
                rig.Second.Generation));
        var stale = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/stale-owner", new("1"), FenceOne, At(3)),
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, takeover.Disposition);
        Assert.Equal(FenceTwo, takeover.Snapshot.LatestFence);
        Assert.Equal(MaterializationBackendRoutingDisposition.StaleFence, stale.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("1"), stale.Snapshot.Revision);
        Assert.Null(stale.Snapshot.Candidate);
    }

    [Fact]
    public async Task HigherFenceTakeover_IsRetainedBeforeTheFirstRoutingCommit()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);

        var takeover = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/precommit-takeover", new("1"), FenceTwo, At(1)),
                rig.Second.Generation));
        var stale = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/precommit-stale",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceOne,
                    At(2)),
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, takeover.Disposition);
        Assert.Equal(MaterializationBackendRoutingRevision.Initial, takeover.Snapshot.Revision);
        Assert.Equal(FenceTwo, takeover.Snapshot.LatestFence);
        Assert.Equal(MaterializationBackendRoutingDisposition.StaleFence, stale.Disposition);
        Assert.Equal(FenceTwo, stale.Snapshot.LatestFence);
        Assert.Null(stale.Snapshot.Candidate);
    }

    [Fact]
    public async Task ExactCommandReplay_AfterLaterTransitionReturnsOriginalReceiptAndCurrentSnapshot()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        var (initialRequest, initialized) = await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var swapped = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/later-swap",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                rig.Second.Generation));

        var replayed = await rig.Router.SwapAsync(Context(), initialRequest);

        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayed.Disposition);
        Assert.Equal(initialized.Receipt, replayed.Receipt);
        Assert.Equal(new MaterializationBackendRoutingRevision("1"), replayed.Receipt!.Revision);
        Assert.Equal(swapped.Snapshot.Revision, replayed.Snapshot.Revision);
        Assert.Equal(rig.Second.Read, replayed.Snapshot.ActiveRead);
        Assert.Equal(rig.Second.Generation, replayed.Snapshot.ActiveWrite);
    }

    [Fact]
    public async Task ReusedCommandIdentity_WithDifferentContentConflictsWithoutChangingRoutes()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var (_, initialized) = await InitializeAsync(rig, commandId: "command/shared");

        var conflict = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/shared", new("1"), FenceTwo, At(2)),
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal(initialized.Snapshot.Revision, conflict.Snapshot.Revision);
        Assert.Equal(rig.First.Read, conflict.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, conflict.Snapshot.ActiveWrite);
        Assert.Null(conflict.Snapshot.Candidate);
        Assert.Equal(FenceTwo, conflict.Snapshot.LatestFence);
    }

    [Fact]
    public async Task RejectedCommandIntent_StillPreventsChangedContentFromReusingItsIdentity()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        var rejectedRequest = new MaterializationAdmitBackendCandidateRequest(
            Header(
                rig,
                "command/rejected-intent",
                MaterializationBackendRoutingRevision.Initial,
                FenceTwo,
                At(2)),
            rig.Second.Generation);

        var rejected = await rig.Router.AdmitCandidateAsync(Context(), rejectedRequest);
        var changed = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/rejected-intent", new("1"), FenceTwo, At(3)),
                rig.Second.Generation));
        var exactRetry = await rig.Router.AdmitCandidateAsync(Context(), rejectedRequest);
        var refreshedAttempt = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/rejected-intent-refreshed", new("1"), FenceTwo, At(3)),
                rig.Second.Generation));

        Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, rejected.Disposition);
        Assert.Equal(FenceTwo, rejected.Snapshot.LatestFence);
        Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, changed.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, exactRetry.Disposition);
        Assert.Null(changed.Snapshot.Candidate);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, refreshedAttempt.Disposition);
        Assert.Equal(rig.Second.Generation, refreshedAttempt.Snapshot.Candidate);
        Assert.Equal(new MaterializationBackendRoutingRevision("1"), changed.Snapshot.Revision);
    }

    [Fact]
    public async Task CandidateAdmission_ReservesExactSwapAndBlocksInterleavingWithoutAdvancingFence()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        MaterializationBackendRoutingCommandId swapCommandId = new("command/reserved-follow-up-swap");
        var reservedSwap = SwapRequest(
            rig,
            swapCommandId.Value,
            expectedRevision: new("2"),
            issuedAtUtc: At(4),
            rig.Second.Read!,
            rig.Second.Generation);
        var admissionRequest = new MaterializationAdmitBackendCandidateRequest(
            Header(rig, "command/admit-with-follow-up", new("1"), FenceOne, At(2)),
            rig.Second.Generation,
            expectedFollowUp: reservedSwap);

        var admitted = await rig.Router.AdmitCandidateAsync(Context(), admissionRequest);
        var blocked = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/interleaving-mutation", new("2"), FenceTwo, At(3)),
                rig.Second.Generation));
        var malformedSameIdentity = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                swapCommandId.Value,
                expectedRevision: new("2"),
                issuedAtUtc: At(4),
                rig.First.Read!,
                rig.First.Generation));
        var swapped = await rig.Router.SwapAsync(Context(), reservedSwap);
        var replayedAdmission = await rig.Router.AdmitCandidateAsync(Context(), admissionRequest);

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
        Assert.Equal(
            new MaterializationBackendFollowUpReservation(reservedSwap),
            admitted.Snapshot.PendingFollowUp);
        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, blocked.Disposition);
        Assert.Equal(FenceOne, blocked.Snapshot.LatestFence);
        Assert.Equal(admitted.Snapshot.Revision, blocked.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, malformedSameIdentity.Disposition);
        Assert.Equal(admitted.Snapshot.Revision, malformedSameIdentity.Snapshot.Revision);
        Assert.Equal(admitted.Snapshot.PendingFollowUp, malformedSameIdentity.Snapshot.PendingFollowUp);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
        Assert.Null(swapped.Snapshot.PendingFollowUp);
        Assert.Null(swapped.Snapshot.Candidate);
        Assert.Equal(rig.Second.Generation, swapped.Snapshot.ActiveRead!.Generation);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedAdmission.Disposition);
        Assert.Equal(admitted.Receipt, replayedAdmission.Receipt);
        Assert.Null(replayedAdmission.Snapshot.PendingFollowUp);
    }

    [Fact]
    public async Task ExactCandidateAbandonment_ClearsFollowUpReservationButKeepsPromisedIdentityConsumed()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        await InitializeAsync(rig);
        MaterializationBackendRoutingCommandId swapCommandId = new("command/abandoned-follow-up-swap");
        var reservedSwap = SwapRequest(
            rig,
            swapCommandId.Value,
            expectedRevision: new("2"),
            issuedAtUtc: At(5),
            FabricateReadableReference(rig.Scope, rig.Second.Generation, At(4)),
            rig.Second.Generation);
        var admitted = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/admit-before-abandon", new("1"), FenceOne, At(2)),
                rig.Second.Generation,
                expectedFollowUp: reservedSwap));
        var abandonment = await rig.Second.Target.AbandonGenerationAsync(
            Context(),
            new(
                new("abandonment/follow-up-candidate"),
                rig.Second.Generation.GenerationId,
                abandonedAtUtc: At(3)));
        var cleared = await rig.Router.AbandonCandidateAsync(
            Context(),
            new(
                Header(rig, "command/clear-follow-up-candidate", admitted.Snapshot.Revision, FenceOne, At(4)),
                rig.Second.Generation,
                abandonment.Receipt!));
        var promisedIdentityReuse = await rig.Router.SwapAsync(Context(), reservedSwap);

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, cleared.Disposition);
        Assert.Null(cleared.Snapshot.Candidate);
        Assert.Null(cleared.Snapshot.PendingFollowUp);
        Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, promisedIdentityReuse.Disposition);
        Assert.Equal(cleared.Snapshot.Revision, promisedIdentityReuse.Snapshot.Revision);
    }

    [Fact]
    public async Task PlacementSlices_IsolateRevisionsFencesCommandIdentityRoutesAndReceipts()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        const string sharedCommandId = "command/shared-across-placement-slices";
        var primaryRequest = SwapRequest(
            rig,
            sharedCommandId,
            MaterializationBackendRoutingRevision.Initial,
            At(1),
            rig.First.Read!,
            rig.First.Generation);
        MaterializationReadableBackendReference alternateRead = new(
            rig.AlternateScope,
            rig.First.Generation,
            rig.First.Activation!);
        MaterializationSwapBackendRoutingRequest alternateRequest = new(
            Header(
                rig,
                rig.AlternateScope,
                sharedCommandId,
                MaterializationBackendRoutingRevision.Initial,
                FenceTwo,
                At(2)),
            alternateRead,
            rig.First.Generation,
            Configuration(rig, rig.First.Target, rig.First.Target));

        var primaryInitialized = await rig.Router.SwapAsync(Context(), primaryRequest);
        var alternateInitialized = await rig.Router.SwapAsync(Context(), alternateRequest);
        var primaryAdvanced = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/advance-primary-only", new("1"), FenceOne, At(3)),
                rig.Second.Generation));
        var primarySnapshot = await rig.Router.InspectAsync(Context(), rig.Scope);
        var alternateSnapshot = await rig.Router.InspectAsync(Context(), rig.AlternateScope);
        var primaryRead = await rig.Router.ResolveReadAsync(Context(), rig.Scope);
        var alternateReadBinding = await rig.Router.ResolveReadAsync(Context(), rig.AlternateScope);
        var primaryReplay = await rig.Router.SwapAsync(Context(), primaryRequest);
        var alternateReplay = await rig.Router.SwapAsync(Context(), alternateRequest);

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, primaryInitialized.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, alternateInitialized.Disposition);
        Assert.Equal(primaryInitialized.Receipt!.CommandId, alternateInitialized.Receipt!.CommandId);
        Assert.Equal(rig.Scope, primaryInitialized.Receipt.PlacementSlice);
        Assert.Equal(rig.AlternateScope, alternateInitialized.Receipt.PlacementSlice);
        Assert.NotEqual(primaryInitialized.Receipt, alternateInitialized.Receipt);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, primaryAdvanced.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("2"), primarySnapshot.Revision);
        Assert.Equal(FenceOne, primarySnapshot.LatestFence);
        Assert.Equal(rig.Second.Generation, primarySnapshot.Candidate);
        Assert.Equal(new MaterializationBackendRoutingRevision("1"), alternateSnapshot.Revision);
        Assert.Equal(FenceTwo, alternateSnapshot.LatestFence);
        Assert.Null(alternateSnapshot.Candidate);
        Assert.Equal(rig.Scope, primaryRead.PlacementSlice);
        Assert.Equal(rig.AlternateScope, alternateReadBinding.PlacementSlice);
        Assert.Equal(rig.First.Generation, primaryRead.Generation);
        Assert.Equal(rig.First.Generation, alternateReadBinding.Generation);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, primaryReplay.Disposition);
        Assert.Equal(primaryInitialized.Receipt, primaryReplay.Receipt);
        Assert.Equal(primarySnapshot.Revision, primaryReplay.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, alternateReplay.Disposition);
        Assert.Equal(alternateInitialized.Receipt, alternateReplay.Receipt);
        Assert.Equal(alternateSnapshot.Revision, alternateReplay.Snapshot.Revision);
    }

    [Fact]
    public async Task ReadableRoute_RejectsActivationForAnotherPlacementSubjectSet()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var foreignSubjects = PlacementSlice(
            rig.Document,
            rig.First.Target.Descriptor.Id,
            subject: "routing-foreign-subject",
            membershipDigestCharacter: 'f');

        Assert.Throws<ArgumentException>(() => new MaterializationReadableBackendReference(
            placementSlice: foreignSubjects,
            generation: rig.First.Generation,
            activation: rig.First.Activation!));
    }

    [Fact]
    public async Task InitialSwap_RejectsUnadmittedWriteGeneration()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);

        var rejected = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/initial-unadmitted-write",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceOne,
                    At(1)),
                rig.First.Read!,
                rig.Second.Generation,
                Configuration(rig, rig.First.Target, rig.Second.Target)));

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, rejected.Disposition);
        Assert.Equal(MaterializationBackendRoutingRevision.Initial, rejected.Snapshot.Revision);
        Assert.Null(rejected.Snapshot.ActiveRead);
        Assert.Null(rejected.Snapshot.ActiveWrite);
    }

    [Fact]
    public async Task InitialSwap_RejectsActivationWithAnotherPromotionFence()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var activation = rig.First.Activation!;
        MaterializationActiveGenerationReference foreignFenceActivation = new(
            schemaVersion: activation.SchemaVersion,
            authority: activation.Authority,
            generation: activation.Generation,
            targetRevision: activation.TargetRevision,
            promotion: activation.Promotion,
            promotionFence: new("2"),
            validation: activation.Validation,
            activatedAtUtc: activation.ActivatedAtUtc);
        MaterializationReadableBackendReference read = new(
            placementSlice: rig.Scope,
            generation: rig.First.Generation,
            activation: foreignFenceActivation);

        var rejected = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/initial-foreign-promotion-fence",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceOne,
                    At(1)),
                read,
                rig.First.Generation,
                Configuration(rig, rig.First.Target, rig.First.Target)));

        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, rejected.Disposition);
        Assert.Equal(MaterializationBackendRoutingRevision.Initial, rejected.Snapshot.Revision);
    }

    [Fact]
    public async Task ConcurrentSameRevisionSwap_AdmitsExactlyOneCommand()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var first = SwapRequest(
            rig,
            "command/concurrent-first",
            expectedRevision: new("2"),
            issuedAtUtc: At(3),
            rig.Second.Read!,
            rig.Second.Generation);
        var second = SwapRequest(
            rig,
            "command/concurrent-second",
            expectedRevision: new("2"),
            issuedAtUtc: At(3),
            rig.Second.Read!,
            rig.Second.Generation);

        var results = await Task.WhenAll(
            rig.Router.SwapAsync(Context(), first).AsTask(),
            rig.Router.SwapAsync(Context(), second).AsTask());

        Assert.Single(results, static result => result.Disposition == MaterializationBackendRoutingDisposition.Applied);
        Assert.Single(results, static result => result.Disposition == MaterializationBackendRoutingDisposition.RevisionConflict);
        var snapshot = await rig.Router.InspectAsync(Context(), rig.Scope);
        Assert.Equal(new MaterializationBackendRoutingRevision("3"), snapshot.Revision);
        Assert.Equal(rig.Second.Read, snapshot.ActiveRead);
        Assert.Equal(rig.Second.Generation, snapshot.ActiveWrite);
        Assert.Null(snapshot.Candidate);
    }

    [Fact]
    public async Task Rollback_RequiresExactCurrentRevisionRouteAndEquivalenceProof()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        var current = await SwapToSecondAsync(rig);
        MaterializationBackendRollbackProof staleProof = new(
            rig.Scope,
            rig.First.Generation,
            current.ActiveRead!,
            current.ActiveWrite!,
            expectedRoutingRevision: new("2"),
            equivalenceFingerprint: "equivalence/stale",
            observedAtUtc: At(4));

        var rejected = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/rollback-stale-proof",
                expectedRevision: current.Revision,
                issuedAtUtc: At(5),
                rig.First.Read!,
                rig.First.Generation,
                staleProof));
        MaterializationBackendRollbackProof exactProof = new(
            rig.Scope,
            rig.First.Generation,
            current.ActiveRead!,
            current.ActiveWrite!,
            current.Revision,
            equivalenceFingerprint: "equivalence/exact",
            observedAtUtc: At(6));
        var rolledBack = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/rollback-exact",
                expectedRevision: current.Revision,
                issuedAtUtc: At(7),
                rig.First.Read!,
                rig.First.Generation,
                exactProof));

        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, rejected.Disposition);
        Assert.Equal(current.Revision, rejected.Snapshot.Revision);
        Assert.Equal(rig.Second.Read, rejected.Snapshot.ActiveRead);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, rolledBack.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("4"), rolledBack.Snapshot.Revision);
        Assert.Equal(rig.First.Read, rolledBack.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, rolledBack.Snapshot.ActiveWrite);
        Assert.DoesNotContain(rolledBack.Snapshot.Draining, drain => drain.Generation == rig.First.Generation);
        Assert.Contains(rolledBack.Snapshot.Draining, drain => drain.Generation == rig.Second.Generation);
    }

    [Fact]
    public async Task DrainCompletion_RequiresProofBoundToTheExactAdmissionRevision()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        var current = await SwapToSecondAsync(rig);
        var drain = Assert.Single(current.Draining);
        MaterializationBackendDrainProof staleProof = new(
            rig.Scope,
            rig.First.Generation,
            admissionsClosedAtRevision: new("2"),
            inFlightOperationCount: 0,
            quiescenceToken: "quiescence/stale",
            observedAtUtc: At(4));

        var rejected = await rig.Router.CompleteDrainAsync(
            Context(),
            new(
                Header(rig, "command/drain-stale-proof", current.Revision, FenceOne, At(5)),
                staleProof));
        MaterializationBackendDrainProof exactProof = new(
            rig.Scope,
            rig.First.Generation,
            drain.AdmissionsClosedAtRevision,
            inFlightOperationCount: 0,
            quiescenceToken: "quiescence/exact",
            observedAtUtc: At(6));
        var completed = await rig.Router.CompleteDrainAsync(
            Context(),
            new(
                Header(rig, "command/drain-exact", current.Revision, FenceOne, At(7)),
                exactProof));

        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, rejected.Disposition);
        Assert.Equal(current.Revision, rejected.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, completed.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.CompleteDrain, completed.Receipt!.Operation);
        Assert.Equal(new MaterializationBackendRoutingRevision("4"), completed.Snapshot.Revision);
        Assert.Equal(exactProof, Assert.Single(completed.Snapshot.Draining).Proof);
    }

    [Fact]
    public async Task RetireAndCleanup_RequireOrderedEvidenceRetainTombstoneAndDoNotMutateTargetLifecycle()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        var swapped = await SwapToSecondAsync(rig);

        var prematureRetirement = await rig.Router.RetireAsync(
            Context(),
            new(
                Header(rig, "command/retire-before-drain", swapped.Revision, FenceOne, At(4)),
                rig.First.Generation));
        var drain = Assert.Single(swapped.Draining);
        MaterializationBackendDrainProof drainProof = new(
            rig.Scope,
            rig.First.Generation,
            drain.AdmissionsClosedAtRevision,
            inFlightOperationCount: 0,
            quiescenceToken: "quiescence/retire",
            observedAtUtc: At(5));
        var completedDrain = await rig.Router.CompleteDrainAsync(
            Context(),
            new(
                Header(rig, "command/complete-drain", swapped.Revision, FenceOne, At(6)),
                drainProof));
        MaterializationBackendCleanupProof prematureCleanupProof = new(
            rig.Scope,
            rig.First.Generation,
            retiredAtRevision: completedDrain.Snapshot.Revision,
            reservationToken: "cleanup-reservation/not-yet-reserved",
            cleanupFingerprint: "cleanup/physical-receipt",
            observedAtUtc: At(7));
        var prematureCleanup = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(rig, "command/cleanup-before-retire", completedDrain.Snapshot.Revision, FenceOne, At(8)),
                prematureCleanupProof));
        var retired = await rig.Router.RetireAsync(
            Context(),
            new(
                Header(rig, "command/retire", completedDrain.Snapshot.Revision, FenceOne, At(9)),
                rig.First.Generation));
        var targetStateAfterRetirement = await rig.First.Target.InspectGenerationAsync(
            Context(),
            rig.First.Generation.GenerationId);
        var reservationRequest = new MaterializationReserveBackendCleanupRequest(
            Header(rig, "command/reserve-cleanup", retired.Snapshot.Revision, FenceOne, At(10)),
            rig.First.Generation);
        var reserved = await rig.Router.ReserveCleanupAsync(Context(), reservationRequest);
        var replayedReservation = await rig.Router.ReserveCleanupAsync(Context(), reservationRequest);
        MaterializationBackendCleanupProof preReservationCleanupProof = new(
            rig.Scope,
            rig.First.Generation,
            retiredAtRevision: retired.Snapshot.Revision,
            reservationToken: reserved.Reservation!.Token,
            cleanupFingerprint: "cleanup/pre-reservation-physical-receipt",
            observedAtUtc: At(99));
        var preReservationCleanup = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/cleanup-with-pre-reservation-proof",
                    reserved.Routing.Snapshot.Revision,
                    FenceOne,
                    At(101)),
                preReservationCleanupProof));
        MaterializationBackendCleanupProof wrongReservationProof = new(
            rig.Scope,
            rig.First.Generation,
            retiredAtRevision: retired.Snapshot.Revision,
            reservationToken: "cleanup-reservation/wrong",
            cleanupFingerprint: "cleanup/wrong-reservation-token",
            observedAtUtc: At(101));
        var wrongReservationCleanup = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/cleanup-with-wrong-reservation",
                    reserved.Routing.Snapshot.Revision,
                    FenceOne,
                    At(102)),
                wrongReservationProof));
        MaterializationBackendCleanupProof cleanupProof = new(
            rig.Scope,
            rig.First.Generation,
            retiredAtRevision: retired.Snapshot.Revision,
            reservationToken: reserved.Reservation!.Token,
            cleanupFingerprint: "cleanup/physical-receipt",
            observedAtUtc: At(101));
        var cleanupRequest = new MaterializationCleanupBackendGenerationRequest(
            Header(rig, "command/cleanup", reserved.Routing.Snapshot.Revision, FenceOne, At(102)),
            cleanupProof);
        var cleaned = await rig.Router.CleanupAsync(Context(), cleanupRequest);
        var replayedCleanup = await rig.Router.CleanupAsync(Context(), cleanupRequest);
        var readmission = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/readmit-cleaned", cleaned.Snapshot.Revision, FenceOne, At(103)),
                rig.First.Generation));
        var targetStateAfterCleanup = await rig.First.Target.InspectGenerationAsync(
            Context(),
            rig.First.Generation.GenerationId);

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, prematureRetirement.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, prematureCleanup.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, retired.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.Retire, retired.Receipt!.Operation);
        var retirement = Assert.Single(retired.Snapshot.Retired);
        Assert.Equal(rig.First.Generation, retirement.Generation);
        Assert.Equal(retired.Snapshot.Revision, retirement.RetiredAtRevision);
        Assert.Equal(MaterializationGenerationState.Active, targetStateAfterRetirement!.State);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, reserved.Routing.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.ReserveCleanup, reserved.Routing.Receipt!.Operation);
        Assert.Equal(rig.First.Generation, reserved.Reservation!.Generation);
        Assert.Equal(rig.Scope, Assert.Single(reserved.Reservation.Retirements).PlacementSlice);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedReservation.Routing.Disposition);
        Assert.Equal(reserved.Reservation, replayedReservation.Reservation);
        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, preReservationCleanup.Disposition);
        Assert.Equal(reserved.Routing.Snapshot.Revision, preReservationCleanup.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, wrongReservationCleanup.Disposition);
        Assert.Equal(reserved.Routing.Snapshot.Revision, wrongReservationCleanup.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, cleaned.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.Cleanup, cleaned.Receipt!.Operation);
        Assert.Empty(cleaned.Snapshot.Retired);
        Assert.Contains(rig.First.Generation, cleaned.Snapshot.Cleaned);
        Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedCleanup.Disposition);
        Assert.Equal(cleaned.Receipt, replayedCleanup.Receipt);
        Assert.Equal(cleaned.Snapshot.Revision, replayedCleanup.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, readmission.Disposition);
        Assert.Equal(cleaned.Snapshot.Revision, readmission.Snapshot.Revision);
        Assert.Equal(MaterializationGenerationState.Active, targetStateAfterCleanup!.State);
        Assert.Equal(rig.Second.Generation, (await rig.Router.ResolveReadAsync(Context(), rig.Scope)).Generation);
    }

    [Fact]
    public async Task SharedRetiredGeneration_UsesOneCleanupReservationAndIndependentPlacementAcknowledgements()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: true);
        await InitializeAsync(rig);
        MaterializationReadableBackendReference alternateFirstRead = new(
            rig.AlternateScope,
            rig.First.Generation,
            rig.First.Activation!);
        MaterializationReadableBackendReference alternateSecondRead = new(
            rig.AlternateScope,
            rig.Second.Generation,
            rig.Second.Activation!);
        var alternateInitialized = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/alternate-initialize-cleanup",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceOne,
                    At(1)),
                alternateFirstRead,
                rig.First.Generation,
                Configuration(rig, rig.First.Target, rig.First.Target)));
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var alternateAdmitted = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/alternate-admit-cleanup",
                    alternateInitialized.Snapshot.Revision,
                    FenceOne,
                    At(2)),
                rig.Second.Generation));
        var primarySwapped = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/primary-swap-cleanup",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                rig.Second.Generation));
        var alternateSwapped = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/alternate-swap-cleanup",
                    alternateAdmitted.Snapshot.Revision,
                    FenceOne,
                    At(3)),
                alternateSecondRead,
                rig.Second.Generation,
                Configuration(rig, rig.Second.Target, rig.Second.Target)));
        var primaryDrain = Assert.Single(primarySwapped.Snapshot.Draining);
        var alternateDrain = Assert.Single(alternateSwapped.Snapshot.Draining);
        var primaryCompleted = await rig.Router.CompleteDrainAsync(
            Context(),
            new(
                Header(rig, "command/primary-complete-shared-drain", primarySwapped.Snapshot.Revision, FenceOne, At(5)),
                new MaterializationBackendDrainProof(
                    placementSlice: rig.Scope,
                    generation: rig.First.Generation,
                    admissionsClosedAtRevision: primaryDrain.AdmissionsClosedAtRevision,
                    inFlightOperationCount: 0,
                    quiescenceToken: "quiescence/shared-primary",
                    observedAtUtc: At(4))));
        var alternateCompleted = await rig.Router.CompleteDrainAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/alternate-complete-shared-drain",
                    alternateSwapped.Snapshot.Revision,
                    FenceOne,
                    At(5)),
                new MaterializationBackendDrainProof(
                    placementSlice: rig.AlternateScope,
                    generation: rig.First.Generation,
                    admissionsClosedAtRevision: alternateDrain.AdmissionsClosedAtRevision,
                    inFlightOperationCount: 0,
                    quiescenceToken: "quiescence/shared-alternate",
                    observedAtUtc: At(4))));
        var primaryRetired = await rig.Router.RetireAsync(
            Context(),
            new(
                Header(rig, "command/primary-retire-shared", primaryCompleted.Snapshot.Revision, FenceOne, At(6)),
                rig.First.Generation));
        var prematureReservation = await rig.Router.ReserveCleanupAsync(
            Context(),
            new(
                Header(
                    rig,
                    "command/reserve-shared-cleanup-before-all-retired",
                    primaryRetired.Snapshot.Revision,
                    FenceOne,
                    At(7)),
                rig.First.Generation));
        var alternateRetired = await rig.Router.RetireAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/alternate-retire-shared",
                    alternateCompleted.Snapshot.Revision,
                    FenceOne,
                    At(6)),
                rig.First.Generation));
        var reserved = await rig.Router.ReserveCleanupAsync(
            Context(),
            new(
                Header(rig, "command/reserve-shared-cleanup", primaryRetired.Snapshot.Revision, FenceOne, At(8)),
                rig.First.Generation));
        var reservation = reserved.Reservation!;
        var primaryCleaned = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(rig, "command/acknowledge-primary-cleanup", reserved.Routing.Snapshot.Revision, FenceOne, At(102)),
                new MaterializationBackendCleanupProof(
                    placementSlice: rig.Scope,
                    generation: rig.First.Generation,
                    retiredAtRevision: primaryRetired.Snapshot.Revision,
                    reservationToken: reservation.Token,
                    cleanupFingerprint: "cleanup/shared-physical-receipt",
                    observedAtUtc: At(101))));
        var conflictingAlternateCleanup = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/acknowledge-conflicting-alternate-cleanup",
                    alternateRetired.Snapshot.Revision,
                    FenceOne,
                    At(102)),
                new MaterializationBackendCleanupProof(
                    placementSlice: rig.AlternateScope,
                    generation: rig.First.Generation,
                    retiredAtRevision: alternateRetired.Snapshot.Revision,
                    reservationToken: reservation.Token,
                    cleanupFingerprint: "cleanup/conflicting-physical-receipt",
                    observedAtUtc: At(101))));
        var alternateCleaned = await rig.Router.CleanupAsync(
            Context(),
            new(
                Header(
                    rig,
                    rig.AlternateScope,
                    "command/acknowledge-alternate-cleanup",
                    alternateRetired.Snapshot.Revision,
                    FenceOne,
                    At(102)),
                new MaterializationBackendCleanupProof(
                    placementSlice: rig.AlternateScope,
                    generation: rig.First.Generation,
                    retiredAtRevision: alternateRetired.Snapshot.Revision,
                    reservationToken: reservation.Token,
                    cleanupFingerprint: "cleanup/shared-physical-receipt",
                    observedAtUtc: At(101))));
        var futureScope = PlacementSlice(
            rig.Document,
            rig.First.Target.Descriptor.Id,
            subject: "routing-shared-subject",
            membershipDigestCharacter: '1');
        var futureAdmission = await rig.Router.SwapAsync(
            Context(),
            new(
                Header(
                    rig,
                    futureScope,
                    "command/route-cleaned-generation-from-new-scope",
                    MaterializationBackendRoutingRevision.Initial,
                    FenceOne,
                    At(103)),
                new MaterializationReadableBackendReference(
                    futureScope,
                    rig.First.Generation,
                    rig.First.Activation!),
                rig.First.Generation,
                Configuration(rig, rig.First.Target, rig.First.Target)));

        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, prematureReservation.Routing.Disposition);
        Assert.Equal(primaryRetired.Snapshot.Revision, prematureReservation.Routing.Snapshot.Revision);
        Assert.Null(prematureReservation.Reservation);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, reserved.Routing.Disposition);
        Assert.Equal(2, reservation.Retirements.Length);
        Assert.Contains(reservation.Retirements, claim => claim.PlacementSlice == rig.Scope);
        Assert.Contains(reservation.Retirements, claim => claim.PlacementSlice == rig.AlternateScope);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, primaryCleaned.Disposition);
        Assert.Equal(
            MaterializationBackendRoutingDisposition.EvidenceConflict,
            conflictingAlternateCleanup.Disposition);
        Assert.Equal(alternateRetired.Snapshot.Revision, conflictingAlternateCleanup.Snapshot.Revision);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, alternateCleaned.Disposition);
        Assert.Contains(rig.First.Generation, primaryCleaned.Snapshot.Cleaned);
        Assert.Contains(rig.First.Generation, alternateCleaned.Snapshot.Cleaned);
        Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, futureAdmission.Disposition);
        Assert.Equal(MaterializationBackendRoutingRevision.Initial, futureAdmission.Snapshot.Revision);
    }

    static async Task<(MaterializationSwapBackendRoutingRequest Request, MaterializationBackendRoutingResult Result)>
        InitializeAsync(
            RoutingRig rig,
            string commandId = "command/initialize")
    {
        var request = SwapRequest(
            rig,
            commandId,
            MaterializationBackendRoutingRevision.Initial,
            At(1),
            rig.First.Read!,
            rig.First.Generation);
        var result = await rig.Router.SwapAsync(Context(), request);
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, result.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.Swap, result.Receipt!.Operation);
        return (request, result);
    }

    static async Task<MaterializationBackendRoutingResult> AdmitSecondAsync(
        RoutingRig rig,
        MaterializationBackendRoutingRevision expectedRevision,
        DateTimeOffset issuedAtUtc)
    {
        var result = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/admit-second", expectedRevision, FenceOne, issuedAtUtc),
                rig.Second.Generation));
        if (result.Disposition == MaterializationBackendRoutingDisposition.Applied)
            Assert.Equal(MaterializationBackendRoutingOperation.AdmitCandidate, result.Receipt!.Operation);
        return result;
    }

    static async Task<MaterializationBackendRoutingSnapshot> SwapToSecondAsync(RoutingRig rig)
    {
        await InitializeAsync(rig);
        await AdmitSecondAsync(rig, expectedRevision: new("1"), issuedAtUtc: At(2));
        var swapped = await rig.Router.SwapAsync(
            Context(),
            SwapRequest(
                rig,
                "command/swap-second",
                expectedRevision: new("2"),
                issuedAtUtc: At(3),
                rig.Second.Read!,
                rig.Second.Generation));
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
        Assert.Equal(MaterializationBackendRoutingOperation.Swap, swapped.Receipt!.Operation);
        return swapped.Snapshot;
    }

    static MaterializationSwapBackendRoutingRequest SwapRequest(
        RoutingRig rig,
        string commandId,
        MaterializationBackendRoutingRevision expectedRevision,
        DateTimeOffset issuedAtUtc,
        MaterializationReadableBackendReference read,
        MaterializationBackendGenerationReference write,
        MaterializationBackendRollbackProof? rollback = null) =>
        new(
            Header(rig, commandId, expectedRevision, FenceOne, issuedAtUtc),
            read,
            write,
            Configuration(rig, read.Generation.TargetId, write.TargetId),
            rollback);

    static MaterializationBackendRoutingCommandHeader Header(
        RoutingRig rig,
        string commandId,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc) =>
        Header(rig, rig.Scope, commandId, expectedRevision, fence, issuedAtUtc);

    static MaterializationBackendRoutingCommandHeader Header(
        RoutingRig rig,
        MaterializationPlacementSliceReference placementSlice,
        string commandId,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc) =>
        new(
            new(commandId),
            placementSlice,
            expectedRevision,
            fence,
            issuedAtUtc);

    static MaterializationBackendRoutingConfiguration Configuration(
        RoutingRig rig,
        InMemoryMaterializationTarget read,
        InMemoryMaterializationTarget write) =>
        Configuration(rig, read.Descriptor.Id, write.Descriptor.Id);

    static MaterializationBackendRoutingConfiguration Configuration(
        RoutingRig rig,
        MaterializationTargetId read,
        MaterializationTargetId write) =>
        MaterializationBackendRoutingConfigurationResolver.Resolve(
            rig.Definition,
            new MaterializationBackendRoutingConfigurationLayer(
                EffectiveConfigurationOrigin.Explicit,
                "tests/materialization-backend-routing/v1",
                new(read, write)));

    static MaterializationReadableBackendReference FabricateReadableReference(
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference generation,
        DateTimeOffset activatedAtUtc) =>
        new(
            placementSlice,
            generation,
            new(
                MaterializationActiveGenerationReference.CurrentSchemaVersion,
                Authority(placementSlice),
                generation.GenerationId,
                targetRevision: new("1"),
                promotion: new("promotion/incomplete"),
                promotionFence: new("1"),
                validation: new("validation/incomplete"),
                activatedAtUtc));

    static DateTimeOffset At(int minute) => Epoch.AddHours(2).AddMinutes(minute);

    static OperationContext Context() => OperationContext.Create();

    static MaterializationPlacementSliceReference PlacementSlice(
        MaterializationBackendPoolDocument document,
        MaterializationTargetId target,
        string subject,
        char membershipDigestCharacter) =>
        MaterializationPlacementSliceReference.Create(
            materialization: MaterializationBackendPoolReference.FromDocument(document).Materialization,
            membership: new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-backend-routing-membership/v1",
                value: new string(membershipDigestCharacter, 64)),
            pool: MaterializationBackendPoolReference.FromDocument(document),
            target,
            subjects: [new($"placement-subject/{subject}")]);

    static MaterializationRebuildLeafExecutionAuthority Authority(
        MaterializationPlacementSliceReference placementSlice)
    {
        MaterializationRebuildRequestReference request = new(
            MaterializationRebuildRequestReference.CurrentSchemaVersion,
            placementSlice.Materialization,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-backend-routing-request/v1",
                value: new string('f', 64)));
        MaterializationRebuildPlanSetReference planSet = new(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-backend-routing-plan-set/v1",
                value: new string('0', 64)));
        return new(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet,
            new(
                placementSlice,
                new(PlanFingerprint, placementSlice.Fingerprint)));
    }

    static MaterializationTargetDescriptor Descriptor(MaterializationTargetId targetId)
    {
        MaterializationCapabilityEvidence Evidence(
            string id,
            MaterializationCapabilityKind capability,
            ImmutableArray<MaterializationGuaranteeKind> guarantees,
            ImmutableArray<MaterializationOperatingLimit> limits = default) =>
            new(
                new($"{targetId.Value}/{id}"),
                capability,
                CapabilityRealizationKind.Native,
                guarantees,
                limits.IsDefault ? [] : limits,
                ["cohesive.storage.in-memory/backend-routing-tests/v1"]);

        ImmutableArray<MaterializationOperatingLimit> writeLimits =
        [
            new(MaterializationLimitKind.WriteItems, 16),
            new(MaterializationLimitKind.WriteBytes, 1_000_000)
        ];
        MaterializationCapabilityProfile profile = new(
            new($"profile/{targetId.Value}"),
            MaterializationEndpointRole.Target,
            targetId.Value,
            [
                Evidence(
                    "isolation",
                    MaterializationCapabilityKind.TargetGenerationIsolation,
                    [MaterializationGuaranteeKind.FencedMutation, MaterializationGuaranteeKind.GenerationIsolation]),
                Evidence(
                    "outcomes",
                    MaterializationCapabilityKind.TargetPerItemOutcomes,
                    [MaterializationGuaranteeKind.ExactPerItemOutcome],
                    writeLimits),
                Evidence(
                    "promotion",
                    MaterializationCapabilityKind.TargetFencedPromotion,
                    [MaterializationGuaranteeKind.AtomicPromotion, MaterializationGuaranteeKind.FencedPromotion]),
                Evidence(
                    "seal",
                    MaterializationCapabilityKind.TargetSeal,
                    [MaterializationGuaranteeKind.FencedMutation]),
                Evidence(
                    "upsert",
                    MaterializationCapabilityKind.TargetBulkUpsert,
                    [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                    writeLimits),
                Evidence(
                    "validation",
                    MaterializationCapabilityKind.TargetValidation,
                    [MaterializationGuaranteeKind.FencedMutation])
            ]);
        return new(targetId, MaterializationId, profile);
    }

    static async Task<BackendFixture> BeginLoadingAsync(
        InMemoryMaterializationTarget target,
        string suffix,
        DateTimeOffset createdAtUtc)
    {
        MaterializationGenerationId generationId = new($"generation/{suffix}");
        var begun = await target.BeginGenerationAsync(
            Context(),
            new(
                MaterializationId,
                generationId,
                DefinitionFingerprint,
                MaterializationWorkerFence.Initial,
                createdAtUtc));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, begun.Disposition);
        return new(
            target,
            new(target.Descriptor.Id, generationId, DefinitionFingerprint),
            Activation: null,
            Read: null);
    }

    static async Task<BackendFixture> ActivateAsync(
        InMemoryMaterializationTarget target,
        MaterializationPlacementSliceReference placementSlice,
        string suffix,
        DateTimeOffset createdAtUtc)
    {
        var fixture = await BeginLoadingAsync(target, suffix, createdAtUtc);
        var written = await target.ApplyBatchAsync(
            Context(),
            new(
                new($"batch/{suffix}"),
                fixture.Generation.GenerationId,
                MaterializationWorkerFence.Initial,
                [
                    new MaterializationUpsert(
                        new($"item/{suffix}"),
                        new($"mutation/{suffix}"),
                        new("1"),
                        ObservationValue.FromString(suffix))
                ]));
        Assert.Equal(MaterializationBatchDisposition.Applied, written.Disposition);
        var sealedResult = await target.SealGenerationAsync(
            Context(),
            new(
                new($"seal/{suffix}"),
                fixture.Generation.GenerationId,
                written.GenerationRevision!.Value,
                MaterializationWorkerFence.Initial,
                createdAtUtc.AddMinutes(1)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        var validated = await target.ValidateGenerationAsync(
            Context(),
            new(
                new($"validation/{suffix}"),
                fixture.Generation.GenerationId,
                sealedResult.Generation!.Revision,
                sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                validator: "tests/materialization-backend-routing-validator/v1",
                MaterializationWorkerFence.Initial,
                createdAtUtc.AddMinutes(2)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        var targetBeforePromotion = await target.InspectAsync(Context());
        var promoted = await target.PromoteGenerationAsync(
            Context(),
            new(
                new($"promotion/{suffix}"),
                fixture.Generation.GenerationId,
                validated.Generation!.Revision,
                validated.Receipt!.Fingerprint,
                targetBeforePromotion.ActiveGenerationId,
                targetBeforePromotion.Revision,
                MaterializationWorkerFence.Initial,
                MaterializationPromotionFence.Initial,
                createdAtUtc.AddMinutes(3)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);
        var receipt = promoted.Receipt!;
        MaterializationActiveGenerationReference activation = new(
            MaterializationActiveGenerationReference.CurrentSchemaVersion,
            Authority(placementSlice),
            fixture.Generation.GenerationId,
            receipt.TargetRevision,
            receipt.PromotionId,
            receipt.PromotionFence,
            receipt.ValidationFingerprint,
            receipt.PromotedAtUtc);
        return fixture with { Activation = activation };
    }

    sealed record BackendFixture(
        InMemoryMaterializationTarget Target,
        MaterializationBackendGenerationReference Generation,
        MaterializationActiveGenerationReference? Activation,
        MaterializationReadableBackendReference? Read);

    sealed class RoutingRig : IDisposable
    {
        RoutingRig(
            MaterializationBackendPoolDefinition definition,
            MaterializationBackendPoolDocument document,
            InMemoryMaterializationBackendRouter router,
            MaterializationPlacementSliceReference scope,
            MaterializationPlacementSliceReference alternateScope,
            BackendFixture first,
            BackendFixture second)
        {
            Definition = definition;
            Document = document;
            Router = router;
            Scope = scope;
            AlternateScope = alternateScope;
            First = first;
            Second = second;
        }

        internal MaterializationBackendPoolDefinition Definition { get; }

        internal MaterializationBackendPoolDocument Document { get; }

        internal InMemoryMaterializationBackendRouter Router { get; }

        internal MaterializationPlacementSliceReference Scope { get; }

        internal MaterializationPlacementSliceReference AlternateScope { get; }

        internal BackendFixture First { get; }

        internal BackendFixture Second { get; }

        internal static async Task<RoutingRig> CreateAsync(bool secondBackendActive)
        {
            var firstTarget = new InMemoryMaterializationTarget(Descriptor(new("target/backend-a")));
            var secondTarget = new InMemoryMaterializationTarget(Descriptor(new("target/backend-b")));
            MaterializationBackendPoolDefinition definition = new(
                new("pool/backend-routing"),
                MaterializationId,
                DefinitionFingerprint,
                [firstTarget.Descriptor, secondTarget.Descriptor],
                defaultTarget: firstTarget.Descriptor.Id,
                provenance: new(
                    new("tests", "1"),
                    new("tests/materialization-backend-routing"),
                    DocumentOrigin.Generated));
            var document = MaterializationBackendPoolDocument.FromDefinition(definition);
            var pool = new InMemoryMaterializationTargetPool(definition, [firstTarget, secondTarget]);
            var scope = PlacementSlice(
                document,
                secondTarget.Descriptor.Id,
                subject: "routing-shared-subject",
                membershipDigestCharacter: 'c');
            var alternateScope = PlacementSlice(
                document,
                secondTarget.Descriptor.Id,
                subject: "routing-shared-subject",
                membershipDigestCharacter: 'd');
            var firstActivationScope = PlacementSlice(
                document,
                firstTarget.Descriptor.Id,
                subject: "routing-shared-subject",
                membershipDigestCharacter: 'e');
            var first = await ActivateAsync(firstTarget, firstActivationScope, "backend-a", Epoch);
            var second = secondBackendActive
                ? await ActivateAsync(secondTarget, scope, "backend-b", Epoch.AddMinutes(10))
                : await BeginLoadingAsync(secondTarget, "backend-b", Epoch.AddMinutes(10));
            first = first with
            {
                Read = new(scope, first.Generation, first.Activation!)
            };
            if (second.Activation is not null)
            {
                second = second with
                {
                    Read = new(scope, second.Generation, second.Activation)
                };
            }

            return new(
                definition,
                document,
                new(document, pool, timeProvider: new FixedTimeProvider(At(100))),
                scope,
                alternateScope,
                first,
                second);
        }

        public void Dispose() => Router.Dispose();
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
