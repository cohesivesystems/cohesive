using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildProcessLifecycleTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    static readonly InteractionAuthorityScope Authority =
        new("authority/materialization-rebuild-lifecycle", "tenant/cohesive");

    static readonly MaterializationRebuildPlanFingerprint PlanFingerprint = new(
        algorithm: "sha256",
        canonicalization: "tests/materialization-rebuild-lifecycle/v1",
        value: new string('a', 64));

    static readonly MaterializationId Materialization = new("materialization/lifecycle-tests");

    static readonly MaterializationTargetId Target = new("target/lifecycle-tests");

    static readonly MaterializationPlacementSliceReference PlacementSlice = CreatePlacementSlice();

    static readonly MaterializationRebuildPlanReference PlanReference =
        new(PlanFingerprint, PlacementSlice.Fingerprint);

    static readonly MaterializationRebuildLeafExecutionAuthority LeafAuthority =
        CreateLeafAuthority(planSetDigest: 'e');

    static readonly ImmutableArray<MaterializationRebuildShardId> Shards =
        [new("shard-a"), new("shard-b")];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_MalformedOrMismatchedPlanReferenceFailsBeforeProcessStart(bool malformed)
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new($"process/materialization-rebuild/lifecycle-invalid-start/{malformed}"),
            processAttemptId: new("attempt/1"));
        var beginCalls = 0;
        var execution = Execution(
            new(continuation, StartedAtUtc),
            new("generation/initial"),
            begin: _ =>
            {
                beginCalls++;
                return Task.FromResult(Initialization(new("generation/initial")));
            });
        var store = new InMemoryProcessDurableStore();
        var lifecycle = Lifecycle(store, new ExactExecutionResolver(execution), artifacts);
        var encodedPlan = malformed
            ? "not-json"
            : MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(
                CreateLeafAuthority(planSetDigest: 'f'));

        var result = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, continuation, encodedPlan));

        Assert.Null(result.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Rejected, result.Realization);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == MaterializationRebuildProcessLifecycleDiagnosticCodes.StartPlanInexact);
        Assert.Equal(0, beginCalls);
        Assert.Null(await store.LoadAsync(Context(StartedAtUtc), continuation.ProcessInstanceId));
    }

    [Fact]
    public async Task Initialize_BindsExactGenerationBeforeCandidateBeginAndReplaysOneGeneration()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/materialization-rebuild/lifecycle-initialize"),
            processAttemptId: new("attempt/1"));
        var attempt = new MaterializationRebuildAttempt(continuation, StartedAtUtc);
        MaterializationGenerationId generation = new("generation/initial");
        var store = new InMemoryProcessDurableStore();
        var beginCalls = 0;
        var execution = Execution(
            attempt,
            generation,
            begin: async context =>
            {
                beginCalls++;
                var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(
                    await store.LoadAsync(context, continuation.ProcessInstanceId));
                Assert.Equal(
                    MaterializationRebuildIdentities.GenerationAffinity(
                        MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
                        generation),
                    snapshot.Checkpoint.Control.CurrentAttempt.FindAffinity(
                        MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId));
                return Initialization(generation);
            });
        var lifecycle = Lifecycle(store, new ExactExecutionResolver(execution), artifacts);
        var start = Start(artifacts, continuation);

        var initialized = await lifecycle.InitializeAsync(Context(StartedAtUtc), start);
        var replayed = await lifecycle.InitializeAsync(Context(StartedAtUtc.AddSeconds(1)), start);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.ProcessDisposition);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayed.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, initialized.Realization);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, replayed.Realization);
        Assert.Equal(generation, initialized.Generation);
        Assert.Equal(generation, replayed.Generation);
        Assert.Equal(2, beginCalls);
        Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(replayed.Snapshot)
                .Checkpoint.Control.CurrentAttempt.AffinityBindings);
    }

    [Fact]
    public async Task Initialize_TransientExecutionUnavailabilityRemainsUnresolvedAndReplayCompletes()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/materialization-rebuild/lifecycle-resolver-unavailable"),
            processAttemptId: new("attempt/1"));
        MaterializationGenerationId generation = new("generation/resolver-recovered");
        var resolver = new ExactExecutionResolver();
        var lifecycle = Lifecycle(new InMemoryProcessDurableStore(), resolver, artifacts);
        var start = Start(artifacts, continuation);

        var unavailable = await lifecycle.InitializeAsync(Context(StartedAtUtc), start);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, unavailable.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Unresolved, unavailable.Realization);
        Assert.Null(unavailable.Generation);
        Assert.Contains(
            unavailable.Diagnostics,
            static diagnostic => diagnostic.Code
                == MaterializationRebuildProcessLifecycleDiagnosticCodes.ExecutionUnavailable);

        resolver.Add(Execution(new(continuation, StartedAtUtc), generation));
        var recovered = await lifecycle.InitializeAsync(Context(StartedAtUtc.AddSeconds(1)), start);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, recovered.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, recovered.Realization);
        Assert.Equal(generation, recovered.Generation);
    }

    [Fact]
    public async Task PauseAndContinue_PreserveGenerationWithoutCandidateLifecycleIo()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/materialization-rebuild/lifecycle-pause"),
            processAttemptId: new("attempt/1"));
        MaterializationGenerationId generation = new("generation/preserved");
        var beginCalls = 0;
        var abandonCalls = 0;
        var execution = Execution(
            new(continuation, StartedAtUtc),
            generation,
            begin: _ =>
            {
                beginCalls++;
                return Task.FromResult(Initialization(generation));
            },
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(true);
            });
        var lifecycle = Lifecycle(
            new InMemoryProcessDurableStore(),
            new ExactExecutionResolver(execution),
            artifacts);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, continuation));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var initialAffinity = Assert.Single(initialSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
        var controls = ProcessControlTestFixture.Create();

        var paused = await lifecycle.ApplyControlAsync(
            Context(StartedAtUtc.AddSeconds(1)),
            controls.Pause(
                initialSnapshot.Checkpoint.Control,
                id: "pause/materialization-rebuild",
                issuedAtUtc: StartedAtUtc.AddSeconds(1)));
        var pausedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot);
        var continued = await lifecycle.ApplyControlAsync(
            Context(StartedAtUtc.AddSeconds(2)),
            controls.Continue(
                pausedSnapshot.Checkpoint.Control,
                id: "continue/materialization-rebuild",
                issuedAtUtc: StartedAtUtc.AddSeconds(2)));
        var continuedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(continued.Snapshot);

        Assert.Equal(MaterializationRebuildProcessRealization.Preserved, paused.Realization);
        Assert.Equal(MaterializationRebuildProcessRealization.Preserved, continued.Realization);
        Assert.Equal(generation, paused.Generation);
        Assert.Equal(generation, continued.Generation);
        Assert.Equal(initialAffinity, Assert.Single(pausedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings));
        Assert.Equal(initialAffinity, Assert.Single(continuedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings));
        Assert.Equal(1, beginCalls);
        Assert.Equal(0, abandonCalls);
    }

    [Fact]
    public async Task Restart_AbandonsOldCandidateThenBindsAndBeginsExactlyOneReplacement()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-restart");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        MaterializationGenerationId initialGeneration = new("generation/initial");
        MaterializationGenerationId replacementGeneration = new("generation/replacement");
        var store = new InMemoryProcessDurableStore();
        var sequence = new List<string>();
        var initialExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            initialGeneration,
            abandon: (_, _) =>
            {
                sequence.Add("abandon-initial");
                return Task.FromResult(true);
            });
        var resolver = new ExactExecutionResolver(initialExecution);
        var lifecycle = Lifecycle(store, resolver, artifacts);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, initialContinuation));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var restartAtUtc = StartedAtUtc.AddMinutes(1);
        ProcessContinuationIdentity replacementContinuation = new(instanceId, new("attempt/2"));
        var replacementExecution = Execution(
            new(replacementContinuation, restartAtUtc),
            replacementGeneration,
            begin: async context =>
            {
                sequence.Add("begin-replacement");
                Assert.Equal("abandon-initial", sequence[^2]);
                var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(
                    await store.LoadAsync(context, instanceId));
                Assert.Equal(
                    MaterializationRebuildIdentities.GenerationAffinity(
                        MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
                        replacementGeneration),
                    snapshot.Checkpoint.Control.CurrentAttempt.FindAffinity(
                        MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId));
                return Initialization(replacementGeneration);
            });
        resolver.Add(replacementExecution);
        var controls = ProcessControlTestFixture.Create();
        var command = controls.Restart(
            initialSnapshot.Checkpoint.Control,
            newAttemptId: replacementContinuation.ProcessAttemptId.Value,
            id: "restart/materialization-rebuild",
            issuedAtUtc: restartAtUtc);

        sequence.Clear();
        var restarted = await lifecycle.ApplyControlAsync(Context(restartAtUtc), command);
        var replayed = await lifecycle.ApplyControlAsync(Context(restartAtUtc.AddSeconds(1)), command);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(replayed.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.ProcessDisposition);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayed.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, restarted.Realization);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, replayed.Realization);
        Assert.Equal(replacementContinuation, snapshot.Checkpoint.ContinuationIdentity);
        Assert.Equal(replacementGeneration, restarted.Generation);
        Assert.Equal(replacementGeneration, replayed.Generation);
        Assert.Equal(
            ["abandon-initial", "begin-replacement", "abandon-initial", "begin-replacement"],
            sequence);
        Assert.Equal(2, snapshot.Checkpoint.Control.Attempts.Length);
        Assert.Single(snapshot.Checkpoint.Control.Attempts[0].AffinityBindings);
        Assert.Single(snapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
    }

    [Fact]
    public async Task RestartCommitOutcomeUnknown_PerformsNoGenerationIoUntilExactReplay()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-unknown-restart");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        var armed = false;
        var crashed = false;
        var store = new InMemoryProcessDurableStore(crash =>
        {
            if (!armed
                || crashed
                || crash.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || crash.Phase != ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn)
            {
                return false;
            }
            crashed = true;
            return true;
        });
        var abandonCalls = 0;
        var replacementBeginCalls = 0;
        var initialExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            new("generation/initial"),
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(true);
            });
        var resolver = new ExactExecutionResolver(initialExecution);
        var lifecycle = Lifecycle(store, resolver, artifacts, maximumStoreAttempts: 1);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, initialContinuation));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var restartAtUtc = StartedAtUtc.AddMinutes(1);
        var replacementExecution = Execution(
            new(new(instanceId, new("attempt/2")), restartAtUtc),
            new("generation/replacement"),
            begin: _ =>
            {
                replacementBeginCalls++;
                return Task.FromResult(Initialization(new("generation/replacement")));
            });
        resolver.Add(replacementExecution);
        var controls = ProcessControlTestFixture.Create();
        var command = controls.Restart(
            initialSnapshot.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/unknown",
            issuedAtUtc: restartAtUtc);

        armed = true;
        var unknown = await lifecycle.ApplyControlAsync(Context(restartAtUtc), command);

        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, unknown.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.NotAttempted, unknown.Realization);
        Assert.Equal(0, abandonCalls);
        Assert.Equal(0, replacementBeginCalls);
        var committed = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(restartAtUtc),
            instanceId));
        Assert.Equal(new ProcessAttemptId("attempt/2"), committed.Checkpoint.Control.CurrentAttempt.AttemptId);

        var recovered = await lifecycle.ApplyControlAsync(Context(restartAtUtc.AddSeconds(1)), command);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, recovered.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, recovered.Realization);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(1, replacementBeginCalls);
    }

    [Fact]
    public async Task Restart_UnresolvedAbandonmentBlocksReplacementAffinityAndResumesOnReplay()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-abandonment-unresolved");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        var abandonmentConclusive = false;
        var abandonCalls = 0;
        var replacementBeginCalls = 0;
        var initialExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            new("generation/initial"),
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(abandonmentConclusive);
            });
        var resolver = new ExactExecutionResolver(initialExecution);
        var store = new InMemoryProcessDurableStore();
        var lifecycle = Lifecycle(store, resolver, artifacts);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, initialContinuation));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var restartAtUtc = StartedAtUtc.AddMinutes(1);
        var replacementExecution = Execution(
            new(new(instanceId, new("attempt/2")), restartAtUtc),
            new("generation/replacement"),
            begin: _ =>
            {
                replacementBeginCalls++;
                return Task.FromResult(Initialization(new("generation/replacement")));
            });
        resolver.Add(replacementExecution);
        var command = ProcessControlTestFixture.Create().Restart(
            initialSnapshot.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/abandonment-unresolved",
            issuedAtUtc: restartAtUtc);

        var unresolved = await lifecycle.ApplyControlAsync(Context(restartAtUtc), command);
        var unresolvedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(unresolved.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, unresolved.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Unresolved, unresolved.Realization);
        Assert.Empty(unresolvedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(0, replacementBeginCalls);

        abandonmentConclusive = true;
        var recovered = await lifecycle.ApplyControlAsync(Context(restartAtUtc.AddSeconds(1)), command);
        var recoveredSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(recovered.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, recovered.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, recovered.Realization);
        Assert.Single(recoveredSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
        Assert.Equal(2, abandonCalls);
        Assert.Equal(1, replacementBeginCalls);
    }

    [Fact]
    public async Task RestartBeforeInitialAffinity_TombstonesDeterministicOldGenerationThenBeginsReplacement()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-restart-before-affinity");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store);
        var processInitialized = await runtime.InitializeAsync(
            Context(StartedAtUtc),
            artifacts.CoordinatorPlan,
            Start(artifacts, initialContinuation));
        var processSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(processInitialized.Snapshot);
        Assert.Empty(processSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
        var oldBeginCalls = 0;
        var abandonCalls = 0;
        var replacementBeginCalls = 0;
        var initialExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            new("generation/initial"),
            begin: _ =>
            {
                oldBeginCalls++;
                return Task.FromResult(Initialization(new("generation/initial")));
            },
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(true);
            });
        var resolver = new ExactExecutionResolver(initialExecution);
        var lifecycle = new MaterializationRebuildProcessLifecycle(
            runtime,
            artifacts,
            LeafAuthority,
            resolver);
        var restartAtUtc = StartedAtUtc.AddMinutes(1);
        resolver.Add(Execution(
            new(new(instanceId, new("attempt/2")), restartAtUtc),
            new("generation/replacement"),
            begin: _ =>
            {
                replacementBeginCalls++;
                return Task.FromResult(Initialization(new("generation/replacement")));
            }));
        var command = ProcessControlTestFixture.Create().Restart(
            processSnapshot.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/before-affinity",
            issuedAtUtc: restartAtUtc);

        var restarted = await lifecycle.ApplyControlAsync(Context(restartAtUtc), command);
        var restartedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, restarted.Realization);
        Assert.Equal(0, oldBeginCalls);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(1, replacementBeginCalls);
        Assert.Empty(restartedSnapshot.Checkpoint.Control.Attempts[0].AffinityBindings);
        Assert.Single(restartedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
    }

    [Fact]
    public async Task Activation_MismatchedAffinityIsRejectedBeforeCandidateOrProcessExecution()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/materialization-rebuild/lifecycle-affinity-conflict"),
            processAttemptId: new("attempt/1"));
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store);
        var start = Start(artifacts, continuation);
        var initialized = await runtime.InitializeAsync(Context(StartedAtUtc), artifacts.CoordinatorPlan, start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var bound = await runtime.BindAttemptAffinityAsync(
            Context(StartedAtUtc.AddSeconds(1)),
            artifacts.CoordinatorPlan,
            new(
                new(
                    initializedSnapshot.Checkpoint.ContinuationIdentity,
                    initializedSnapshot.Checkpoint.Control.Revision),
                MaterializationRebuildIdentities.GenerationAffinity(
                    MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
                    new("generation/wrong")),
                StartedAtUtc.AddSeconds(1)));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, bound.Disposition);
        var beginCalls = 0;
        var execution = Execution(
            new(continuation, StartedAtUtc),
            new("generation/expected"),
            begin: _ =>
            {
                beginCalls++;
                return Task.FromResult(Initialization(new("generation/expected")));
            });
        var lifecycle = new MaterializationRebuildProcessLifecycle(
            runtime,
            artifacts,
            LeafAuthority,
            new ExactExecutionResolver(execution));

        var activated = await lifecycle.ActivateAsync(
            Context(StartedAtUtc.AddSeconds(2)),
            continuation,
            Activation(artifacts, StartedAtUtc.AddSeconds(2)));
        var after = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot);

        Assert.Null(activated.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Rejected, activated.Realization);
        Assert.Contains(
            activated.Diagnostics,
            static diagnostic => diagnostic.Code
                == MaterializationRebuildProcessLifecycleDiagnosticCodes.GenerationAffinityInexact);
        Assert.Equal(0, beginCalls);
        Assert.Empty(after.Checkpoint.Activations);
    }

    [Fact]
    public async Task ActivationCommitUnknown_StalePredecessorReplayCompletesRetainedRestartLifecycle()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-stale-activation-replay");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        var armed = false;
        var crashed = false;
        var store = new InMemoryProcessDurableStore(crash =>
        {
            if (!armed
                || crashed
                || crash.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || crash.Phase != ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn)
            {
                return false;
            }
            crashed = true;
            return true;
        });
        var runtime = Runtime(
            store,
            maximumStoreAttempts: 1,
            bindingResolver: new ExactBindingResolver(artifacts.InitializationBinding));
        var oldBeginCalls = 0;
        var abandonCalls = 0;
        var initialExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            new("generation/initial"),
            begin: _ =>
            {
                oldBeginCalls++;
                return Task.FromResult(Initialization(new("generation/initial")));
            },
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(true);
            });
        var resolver = new ExactExecutionResolver(initialExecution);
        var lifecycle = new MaterializationRebuildProcessLifecycle(
            runtime,
            artifacts,
            LeafAuthority,
            resolver);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, initialContinuation));
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, initialized.Realization);
        var activationAtUtc = StartedAtUtc.AddMinutes(1);
        var activation = Activation(artifacts, activationAtUtc);

        armed = true;
        var unknown = await lifecycle.ActivateAsync(
            Context(activationAtUtc),
            initialContinuation,
            activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, unknown.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Unresolved, unknown.Realization);
        var afterUnknown = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(activationAtUtc),
            instanceId));
        Assert.Single(afterUnknown.Checkpoint.Activations);

        var restartAtUtc = activationAtUtc.AddMinutes(1);
        var command = ProcessControlTestFixture.Create().Restart(
            afterUnknown.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/after-unknown-activation",
            issuedAtUtc: restartAtUtc);
        var processRestarted = await runtime.ApplyControlAsync(
            Context(restartAtUtc),
            artifacts.CoordinatorPlan,
            command);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, processRestarted.Disposition);
        var replacementBeginCalls = 0;
        resolver.Add(Execution(
            new(new(instanceId, new("attempt/2")), restartAtUtc),
            new("generation/replacement"),
            begin: _ =>
            {
                replacementBeginCalls++;
                return Task.FromResult(Initialization(new("generation/replacement")));
            }));

        var recovered = await lifecycle.ActivateAsync(
            Context(restartAtUtc.AddSeconds(1)),
            initialContinuation,
            activation);
        var recoveredSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(recovered.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, recovered.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, recovered.Realization);
        Assert.Equal(new ProcessAttemptId("attempt/2"), recoveredSnapshot.Checkpoint.Control.CurrentAttempt.AttemptId);
        Assert.Equal(2, oldBeginCalls);
        Assert.Equal(1, abandonCalls);
        Assert.Equal(1, replacementBeginCalls);
        Assert.Single(recoveredSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
    }

    [Fact]
    public async Task ConcurrentFacadeRestart_AfterActivationReconciliationReturnsReplacementNotOldGeneration()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessInstanceId instanceId = new("process/materialization-rebuild/lifecycle-cross-facade-race");
        ProcessContinuationIdentity initialContinuation = new(instanceId, new("attempt/1"));
        MaterializationGenerationId initialGeneration = new("generation/initial");
        MaterializationGenerationId replacementGeneration = new("generation/replacement");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(
            store,
            bindingResolver: new ExactBindingResolver(artifacts.InitializationBinding));
        var activationReachedBegin = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restartCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldBeginCalls = 0;
        var oldExecution = Execution(
            new(initialContinuation, StartedAtUtc),
            initialGeneration,
            begin: async _ =>
            {
                oldBeginCalls++;
                if (oldBeginCalls == 2)
                {
                    activationReachedBegin.SetResult(true);
                    await restartCompleted.Task;
                }
                return Initialization(initialGeneration);
            },
            abandon: (_, _) => Task.FromResult(true));
        var resolver = new ExactExecutionResolver(oldExecution);
        var activationFacade = new MaterializationRebuildProcessLifecycle(
            runtime,
            artifacts,
            LeafAuthority,
            resolver);
        var restartFacade = new MaterializationRebuildProcessLifecycle(
            runtime,
            artifacts,
            LeafAuthority,
            resolver);
        var initialized = await activationFacade.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, initialContinuation));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var restartAtUtc = StartedAtUtc.AddMinutes(2);
        resolver.Add(Execution(
            new(new(instanceId, new("attempt/2")), restartAtUtc),
            replacementGeneration));
        var activationAtUtc = StartedAtUtc.AddMinutes(1);
        var activationTask = activationFacade.ActivateAsync(
            Context(activationAtUtc),
            initialContinuation,
            Activation(artifacts, activationAtUtc));

        await activationReachedBegin.Task;
        var command = ProcessControlTestFixture.Create().Restart(
            initialSnapshot.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/cross-facade-race",
            issuedAtUtc: restartAtUtc);
        var restarted = await restartFacade.ApplyControlAsync(Context(restartAtUtc), command);
        restartCompleted.SetResult(true);
        var fenced = await activationTask;
        var fencedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(fenced.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, restarted.Realization);
        Assert.Equal(ProcessDurableRuntimeDisposition.StaleFence, fenced.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, fenced.Realization);
        Assert.Equal(replacementGeneration, fenced.Generation);
        Assert.NotEqual(initialGeneration, fenced.Generation);
        Assert.Equal(new ProcessAttemptId("attempt/2"), fencedSnapshot.Checkpoint.Control.CurrentAttempt.AttemptId);
        Assert.Single(fencedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
    }

    [Fact]
    public async Task Restart_RejectsNonAbandoningCleanupBeforeProcessOrCandidateIo()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process/materialization-rebuild/lifecycle-cleanup"),
            processAttemptId: new("attempt/1"));
        var abandonCalls = 0;
        var execution = Execution(
            new(continuation, StartedAtUtc),
            new("generation/initial"),
            abandon: (_, _) =>
            {
                abandonCalls++;
                return Task.FromResult(true);
            });
        var lifecycle = Lifecycle(
            new InMemoryProcessDurableStore(),
            new ExactExecutionResolver(execution),
            artifacts);
        var initialized = await lifecycle.InitializeAsync(
            Context(StartedAtUtc),
            Start(artifacts, continuation));
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var command = ProcessControlTestFixture.Create().Restart(
            snapshot.Checkpoint.Control,
            newAttemptId: "attempt/2",
            id: "restart/materialization-rebuild/invalid-cleanup",
            issuedAtUtc: StartedAtUtc.AddSeconds(1),
            cleanup: ProcessAttemptCleanupRequirement.RetainEvidence);

        var rejected = await lifecycle.ApplyControlAsync(Context(StartedAtUtc.AddSeconds(1)), command);

        Assert.Null(rejected.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Rejected, rejected.Realization);
        Assert.Equal(0, abandonCalls);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code
                == MaterializationRebuildProcessLifecycleDiagnosticCodes.CleanupUnsupported);
        Assert.Equal(
            continuation,
            Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint.ContinuationIdentity);
    }

    static MaterializationRebuildProcessLifecycle Lifecycle(
        InMemoryProcessDurableStore store,
        IMaterializationRebuildExecutionResolver resolver,
        MaterializationRebuildProcessArtifacts artifacts,
        int maximumStoreAttempts = 3) =>
        new(
            Runtime(store, maximumStoreAttempts),
            artifacts,
            LeafAuthority,
            resolver);

    static ProcessDurableRuntime Runtime(
        InMemoryProcessDurableStore store,
        int maximumStoreAttempts = 3,
        IDurableRequestBindingResolver? bindingResolver = null) =>
        new(
            store,
            RejectingHost.Instance,
            new(
                workerId: "worker/materialization-rebuild-lifecycle",
                workerLease: TimeSpan.FromMinutes(5),
                maxAmbiguousStoreMutationAttempts: maximumStoreAttempts),
            bindingResolver);

    static MaterializationRebuildExecution Execution(
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        Func<OperationContext, Task<MaterializationRebuildInitializationResult>>? begin = null,
        Func<OperationContext, DateTimeOffset, Task<bool>>? abandon = null) =>
        new(
            LeafAuthority,
            Shards,
            attempt,
            generation,
            Materialization,
            Target,
            begin ?? (_ => Task.FromResult(Initialization(generation))),
            (_, _) => Task.FromException<MaterializationRebuildShardResult>(
                new InvalidOperationException("Lifecycle tests do not execute rebuild shards.")),
            (_, _, _) => Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("Lifecycle tests do not activate generations.")),
            abandon);

    static MaterializationRebuildInitializationResult Initialization(
        MaterializationGenerationId generation)
    {
        var progress = Shards.Select(shard => InitialProgress(shard, generation)).ToImmutableArray();
        return new(
            MaterializationRebuildInitializationDisposition.Ready,
            generation,
            new MaterializationGenerationSnapshot(
                materializationId: new("materialization/lifecycle-tests"),
                generationId: generation,
                definitionFingerprint: DefinitionFingerprint(),
                state: MaterializationGenerationState.Loading,
                revision: MaterializationGenerationRevision.Initial,
                latestWorkerFence: MaterializationWorkerFence.Initial,
                hasPermanentFailures: false,
                pendingRetryableMutationCount: 0,
                visibleItemCount: 0,
                tombstoneCount: 0,
                sealReceipt: null,
                validationReceipt: null,
                createdAtUtc: StartedAtUtc,
                inactivatedAtUtc: null,
                retiredAtUtc: null),
            progress);
    }

    static MaterializationProgressSnapshot InitialProgress(
        MaterializationRebuildShardId shard,
        MaterializationGenerationId generation)
    {
        var scope = Scope(shard);
        MaterializationSourcePosition position = new(
            formatVersion: 1,
            scope,
            value: $"position/{shard.Value}");
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new($"change-checkpoint/{shard.Value}"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position,
            appliedDeliveries: [],
            committedAtUtc: StartedAtUtc,
            evidenceReference: "tests/materialization-rebuild-lifecycle-change-cut",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));
        return new(
            key: new(
                materialization: new("materialization/lifecycle-tests"),
                definitionFingerprint: DefinitionFingerprint(),
                generation,
                scope),
            revision: new("2"),
            fence: MaterializationProgressFence.Initial,
            fenceOwner: $"owner/{shard.Value}",
            latestChangeCheckpoint: checkpoint);
    }

    static MaterializationSourceScope Scope(MaterializationRebuildShardId shard)
    {
        QualifiedShapeId shape = new(new("graph/lifecycle-tests"), new("shape/lifecycle-tests"));
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/lifecycle-tests"),
            input: new("input/lifecycle-tests"),
            node: new("node/lifecycle-tests"),
            binding: new("binding/lifecycle-tests"),
            shape,
            source: new("source/lifecycle-tests"),
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit);
        return new(
            physicalPlan: new(
                algorithm: "sha256",
                canonicalization: "tests/lifecycle-physical-plan/v1",
                value: new string('c', 64)),
            placement,
            partition: new($"partition/{shard.Value}"),
            orderingScope: new($"ordering/{shard.Value}"));
    }

    static ExecutionDefinitionFingerprint DefinitionFingerprint() =>
        new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-definition/v1",
            value: new string('b', 64));

    static MaterializationPlacementSliceReference CreatePlacementSlice()
    {
        MaterializationDefinitionReference materialization = new(
            MaterializationDefinitionReference.CurrentSchemaVersion,
            Materialization,
            DefinitionFingerprint());
        MaterializationBackendPoolReference pool = new(
            MaterializationBackendPoolReference.CurrentSchemaVersion,
            new("pool/lifecycle-tests"),
            materialization,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-pool/v1",
                value: new string('c', 64)));
        MaterializationRebuildMembershipFingerprint membership = new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-membership/v1",
            value: new string('d', 64));
        return MaterializationPlacementSliceReference.Create(
            materialization,
            membership,
            pool,
            Target,
            [new("subject/lifecycle-tests")]);
    }

    static MaterializationRebuildLeafExecutionAuthority CreateLeafAuthority(char planSetDigest)
    {
        MaterializationRebuildRequestReference request = new(
            MaterializationRebuildRequestReference.CurrentSchemaVersion,
            PlacementSlice.Materialization,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-request/v1",
                value: new string('d', 64)));
        MaterializationRebuildPlanSetReference planSet = new(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-plan-set/v1",
                value: new string(planSetDigest, 64)));
        return new(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet,
            new(PlacementSlice, PlanReference));
    }

    static ProcessStartReceipt Start(
        MaterializationRebuildProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        string? encodedPlan = null)
    {
        var planReference = encodedPlan
            ?? MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority);
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.CoordinatorPlan.DefinitionReference,
            context: new(
                commandId: new("command/materialization-rebuild-lifecycle/start"),
                idempotencyKey: new("idempotency/materialization-rebuild-lifecycle/start"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/materialization-rebuild-lifecycle",
                    authorityScope: Authority,
                    evidenceReference: "policy/materialization-rebuild-lifecycle/allow"),
                issuedAtUtc: StartedAtUtc,
                provenance: Provenance("start")),
            initialContinuation: continuation,
            input: PortableValue.Concrete(
                artifacts.CoordinatorPlan.Definition.Input,
                ObservationValue.FromString(planReference)));
        return new(request, acceptedAtUtc: StartedAtUtc);
    }

    static ProcessActivation Activation(
        MaterializationRebuildProcessArtifacts artifacts,
        DateTimeOffset observedAtUtc) =>
        new(
            id: new("activation/materialization-rebuild-lifecycle/start"),
            cause: ProcessActivationCause.Start,
            observedAtUtc,
            context: new(
                authorityScope: Authority,
                correlationId: new("correlation/materialization-rebuild-lifecycle"),
                delivery: new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                provenance: artifacts.CoordinatorProcessDocument.Metadata.Provenance));

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    static ExecutionProvenance Provenance(string source) =>
        new(
            new ExecutionProducerProvenance("tests", "1"),
            new ExecutionSourceProvenance($"tests/materialization-rebuild-lifecycle/{source}"),
            DocumentOrigin.Generated);

    sealed class ExactExecutionResolver : IMaterializationRebuildExecutionResolver
    {
        readonly Dictionary<ProcessContinuationIdentity, MaterializationRebuildExecution> executions = [];

        internal ExactExecutionResolver(params MaterializationRebuildExecution[] executions)
        {
            foreach (var execution in executions)
                Add(execution);
        }

        internal void Add(MaterializationRebuildExecution execution) =>
            executions.Add(execution.Attempt.Continuation, execution);

        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? execution)
        {
            if (authority == LeafAuthority && executions.TryGetValue(continuation, out var found))
            {
                execution = found;
                return true;
            }

            execution = null;
            return false;
        }
    }

    sealed class ExactBindingResolver(DurableRequestBinding binding)
        : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = request.Contract == binding.Request ? binding : null;
            return resolved is not null;
        }
    }

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
