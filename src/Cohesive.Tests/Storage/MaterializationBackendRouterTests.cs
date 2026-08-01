using System.Collections.Immutable;
using System.Text.Json;
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
    public void RoutingWire_UsesStrictClosedEnumAndOperationContracts()
    {
        var options = StrictDocumentJson.CreateOptions();
        MaterializationBackendRoutingReceipt receipt = new(
            commandId: new("command/wire"),
            operation: MaterializationBackendRoutingOperation.Swap,
            revision: new("1"),
            fence: FenceOne,
            committedAtUtc: Epoch);

        var json = JsonSerializer.Serialize(receipt, options);
        var restored = JsonSerializer.Deserialize<MaterializationBackendRoutingReceipt>(json, options);

        Assert.Equal(receipt, restored);
        Assert.Contains("\"operation\":\"Swap\"", json);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRole>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRole>("\"activeRead\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingDisposition>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingDisposition>("\"applied\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingOperation>("0", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MaterializationBackendRoutingOperation>("\"swap\"", options));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterializationBackendRoutingReceipt(
            commandId: new("command/invalid-operation"),
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
                FabricateReadableReference(rig.Second.Generation, At(3)),
                rig.Second.Generation,
                Configuration(rig, rig.Second.Target, rig.Second.Target)));

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
        Assert.Equal(MaterializationBackendRoutingDisposition.EvidenceConflict, rejected.Disposition);
        Assert.Equal(new MaterializationBackendRoutingRevision("2"), rejected.Snapshot.Revision);
        Assert.Equal(rig.First.Read, rejected.Snapshot.ActiveRead);
        Assert.Equal(rig.First.Generation, rejected.Snapshot.ActiveWrite);
        Assert.Equal(rig.Second.Generation, rejected.Snapshot.Candidate);
        Assert.Equal(rig.First.Generation, (await rig.Router.ResolveReadAsync(Context())).Generation);
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

        var read = await rig.Router.ResolveReadAsync(Context());
        var write = await rig.Router.ResolveWriteAsync(Context());
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
        Assert.Equal(rig.First.Generation, (await rig.Router.ResolveReadAsync(Context())).Generation);
        Assert.Equal(rig.Second.Generation, (await rig.Router.ResolveWriteAsync(Context())).Generation);
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
            rig.Definition.Id,
            rig.Document.DefinitionFingerprint,
            new("1"),
            FenceOne,
            rig.First.Read,
            foreignWrite,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: [],
            Configuration(rig, rig.First.Target, rig.First.Target)));
    }

    [Fact]
    public async Task Snapshot_RejectsImpossiblePrecommitAndFutureLifecycleEvidence()
    {
        using var rig = await RoutingRig.CreateAsync(secondBackendActive: false);
        var acceptedFenceOnly = new MaterializationBackendRoutingSnapshot(
            poolId: rig.Definition.Id,
            poolDefinitionFingerprint: rig.Document.DefinitionFingerprint,
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
            poolId: rig.Definition.Id,
            poolDefinitionFingerprint: rig.Document.DefinitionFingerprint,
            revision: MaterializationBackendRoutingRevision.Initial,
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: rig.Second.Generation,
            draining: [],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            poolId: rig.Definition.Id,
            poolDefinitionFingerprint: rig.Document.DefinitionFingerprint,
            revision: new("1"),
            latestFence: default(MaterializationBackendRoutingFence),
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            poolId: rig.Definition.Id,
            poolDefinitionFingerprint: rig.Document.DefinitionFingerprint,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [new(rig.Second.Generation, new("2"))],
            retired: [],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingSnapshot(
            poolId: rig.Definition.Id,
            poolDefinitionFingerprint: rig.Document.DefinitionFingerprint,
            revision: new("1"),
            latestFence: FenceOne,
            activeRead: null,
            activeWrite: null,
            candidate: null,
            draining: [],
            retired: [new(rig.Second.Generation, new("2"))],
            cleaned: []));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRouteBinding(
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
        Assert.Equal(MaterializationBackendRoutingRevision.Initial, (await router.InspectAsync(Context())).Revision);
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
        var snapshot = await rig.Router.InspectAsync(Context());
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
            rig.First.Generation,
            retiredAtRevision: completedDrain.Snapshot.Revision,
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
        MaterializationBackendCleanupProof cleanupProof = new(
            rig.First.Generation,
            retiredAtRevision: retired.Snapshot.Revision,
            cleanupFingerprint: "cleanup/physical-receipt",
            observedAtUtc: At(10));
        var cleanupRequest = new MaterializationCleanupBackendGenerationRequest(
            Header(rig, "command/cleanup", retired.Snapshot.Revision, FenceOne, At(11)),
            cleanupProof);
        var cleaned = await rig.Router.CleanupAsync(Context(), cleanupRequest);
        var replayedCleanup = await rig.Router.CleanupAsync(Context(), cleanupRequest);
        var readmission = await rig.Router.AdmitCandidateAsync(
            Context(),
            new(
                Header(rig, "command/readmit-cleaned", cleaned.Snapshot.Revision, FenceOne, At(12)),
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
        Assert.Equal(rig.Second.Generation, (await rig.Router.ResolveReadAsync(Context())).Generation);
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
        new(
            new(commandId),
            rig.Definition.Id,
            rig.Document.DefinitionFingerprint,
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
        MaterializationBackendGenerationReference generation,
        DateTimeOffset activatedAtUtc) =>
        new(
            generation,
            new(
                MaterializationActiveGenerationReference.CurrentSchemaVersion,
                PlanFingerprint,
                MaterializationId,
                generation.TargetId,
                generation.GenerationId,
                targetRevision: new("1"),
                promotion: new("promotion/incomplete"),
                promotionFence: new("1"),
                validation: new("validation/incomplete"),
                activatedAtUtc));

    static DateTimeOffset At(int minute) => Epoch.AddHours(2).AddMinutes(minute);

    static OperationContext Context() => OperationContext.Create();

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
            Read: null);
    }

    static async Task<BackendFixture> ActivateAsync(
        InMemoryMaterializationTarget target,
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
            PlanFingerprint,
            MaterializationId,
            fixture.Generation.TargetId,
            fixture.Generation.GenerationId,
            receipt.TargetRevision,
            receipt.PromotionId,
            receipt.PromotionFence,
            receipt.ValidationFingerprint,
            receipt.PromotedAtUtc);
        return fixture with { Read = new(fixture.Generation, activation) };
    }

    sealed record BackendFixture(
        InMemoryMaterializationTarget Target,
        MaterializationBackendGenerationReference Generation,
        MaterializationReadableBackendReference? Read);

    sealed class RoutingRig : IDisposable
    {
        RoutingRig(
            MaterializationBackendPoolDefinition definition,
            MaterializationBackendPoolDocument document,
            InMemoryMaterializationBackendRouter router,
            BackendFixture first,
            BackendFixture second)
        {
            Definition = definition;
            Document = document;
            Router = router;
            First = first;
            Second = second;
        }

        internal MaterializationBackendPoolDefinition Definition { get; }

        internal MaterializationBackendPoolDocument Document { get; }

        internal InMemoryMaterializationBackendRouter Router { get; }

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
            var first = await ActivateAsync(firstTarget, "backend-a", Epoch);
            var second = secondBackendActive
                ? await ActivateAsync(secondTarget, "backend-b", Epoch.AddMinutes(10))
                : await BeginLoadingAsync(secondTarget, "backend-b", Epoch.AddMinutes(10));
            return new(definition, document, new(document, pool), first, second);
        }

        public void Dispose() => Router.Dispose();
    }
}
