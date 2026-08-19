using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Storage;

public sealed partial class MaterializationRebuildProcessConformanceTests
{
    const long ReadBytes = 1_000_000;
    const long WriteBytes = 1_000_000;
    const int ReadItems = 100;
    const int WriteItems = 100;

    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    static readonly InteractionAuthorityScope Authority =
        new("authority/materialization-rebuild-conformance", "tenant/cohesive");

    [Fact]
    public void ExactRuntimeCatalogs_RejectAmbiguousAdapterAndChildPlanRegistrations()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/materialization-rebuild/catalog"),
            processAttemptId: new("process-attempt/materialization-rebuild/catalog"));
        var materialization = CreateMaterializationFixture();
        var execution = new MaterializationRebuildExecution(
            resolved: materialization.Resolved,
            attempt: new(
                continuation: continuation,
                startedAtUtc: StartedAtUtc),
            synchronizationWorkStore: new InMemoryMaterializationSynchronizationWorkStore());
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            request: artifacts.ShardRebuildRequest,
            resolver: new ExactExecutionResolver(execution));

        var adapterException = Assert.Throws<ArgumentException>(() =>
            new DurableOperationAdapterCatalog([adapter, adapter]));
        var childPlanException = Assert.Throws<ArgumentException>(() =>
            new ProcessChildPlanCatalog([artifacts.WorkerPlan, artifacts.WorkerPlan]));

        Assert.Contains("handled more than once", adapterException.Message, StringComparison.Ordinal);
        Assert.Contains("registered more than once", childPlanException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalLeafCoordinator_DrivesBaselineCatchUpToReadyThenActivationConsumesExactEvidence()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ProcessContinuationIdentity coordinatorContinuation = new(
            processInstanceId: new("process-instance/materialization-rebuild/conformance"),
            processAttemptId: new("process-attempt/materialization-rebuild/1"));
        var attempt = new MaterializationRebuildAttempt(
            continuation: coordinatorContinuation,
            startedAtUtc: StartedAtUtc);
        var materialization = CreateMaterializationFixture();
        var execution = new MaterializationRebuildExecution(
            materialization.Resolved,
            attempt,
            new InMemoryMaterializationSynchronizationWorkStore());
        var executionResolver = new ExactExecutionResolver(execution);
        var initializationAdapter = new MaterializationRebuildInitializationDurableOperationAdapter(
            request: artifacts.InitializationRequest,
            resolver: executionResolver);
        var shardAdapter = new MaterializationRebuildShardDurableOperationAdapter(
            request: artifacts.ShardRebuildRequest,
            resolver: executionResolver);
        var preparationAdapter = new MaterializationSynchronizationPreparationDurableOperationAdapter(
            request: artifacts.SynchronizationPreparationRequest,
            resolver: executionResolver);
        var workerRuntime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/materialization-rebuild-shards",
                workerLease: TimeSpan.FromMinutes(5)),
            bindingResolver: new ExactBindingResolver([artifacts.ShardRebuildBinding]),
            operationAdapterResolver: new DurableOperationAdapterCatalog([shardAdapter]));
        var childAdapter = new ProcessChildDurableOperationAdapter(
            runtime: workerRuntime,
            planResolver: new ProcessChildPlanCatalog([artifacts.WorkerPlan]),
            supportedRequests: [artifacts.WorkerInvocationRequest]);
        var coordinatorStore = new InMemoryProcessDurableStore();
        var coordinatorRuntime = new ProcessDurableRuntime(
            store: coordinatorStore,
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/materialization-rebuild-coordinator",
                workerLease: TimeSpan.FromMinutes(5)),
            bindingResolver: new ExactBindingResolver(
                [
                    artifacts.InitializationBinding,
                    artifacts.WorkerInvocationBinding,
                    artifacts.SynchronizationPreparationBinding
                ]),
            operationAdapterResolver: new DurableOperationAdapterCatalog(
                [initializationAdapter, childAdapter, preparationAdapter]));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));
        var start = Start(artifacts, coordinatorContinuation, materialization.Resolved.Authority);

        var initialized = await coordinatorRuntime.InitializeAsync(
            context,
            artifacts.CoordinatorPlan,
            start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);

        var emittedInitialization = await ActivateAndCompareAsync(
            context,
            coordinatorStore,
            coordinatorRuntime,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            Activation(
                artifacts,
                id: "activation/coordinator/start",
                cause: ProcessActivationCause.Start,
                inputs: []));
        var initializationOperation = Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(emittedInitialization.Snapshot)
                .Checkpoint.DurableOperations);
        Assert.Equal(artifacts.InitializationRequest, initializationOperation.Request.Contract);

        var initializationAdvanced = await coordinatorRuntime.AdvanceOperationAsync(
            context,
            artifacts.CoordinatorPlan,
            coordinatorContinuation.ProcessInstanceId,
            initializationOperation.OperationId);
        Assert.Equal(DurableOperationStatus.Dispositioned, initializationAdvanced.Operation?.Status);

        var admitted = await ActivateAndCompareAsync(
            context,
            coordinatorStore,
            coordinatorRuntime,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            NextActivation(
                artifacts,
                Assert.IsType<ProcessDurableStoreSnapshot>(initializationAdvanced.Snapshot).Checkpoint,
                id: "activation/coordinator/admit-first-two"));
        var admittedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot).Checkpoint;
        var firstWave = PendingChildOperations(admittedCheckpoint, artifacts.WorkerInvocationRequest);

        Assert.Equal(MaterializationRebuildProcessFactory.MaximumParallelism, firstWave.Length);
        Assert.Equal(2, admittedCheckpoint.Continuation.Children.Count(static child =>
            child.Disposition == ProcessChildDisposition.Active));
        Assert.Single(admittedCheckpoint.Continuation.Children, static child =>
            child.Disposition == ProcessChildDisposition.Pending);

        var truthfulOrigins = ImmutableArray.CreateBuilder<ProcessInteractionOrigin>(3);
        ProcessDurableStoreSnapshot afterFirstWave = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);
        foreach (var operation in firstWave)
        {
            var advanced = await coordinatorRuntime.AdvanceOperationAsync(
                context,
                artifacts.CoordinatorPlan,
                coordinatorContinuation.ProcessInstanceId,
                operation.OperationId);
            afterFirstWave = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot);
            truthfulOrigins.Add(await AssertTruthfulChildOriginAsync(
                context,
                artifacts,
                workerRuntime,
                Assert.IsType<DurableOperationState>(advanced.Operation)));
        }

        var admittedFinal = await ActivateAndCompareAsync(
            context,
            coordinatorStore,
            coordinatorRuntime,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            NextActivation(
                artifacts,
                afterFirstWave.Checkpoint,
                id: "activation/coordinator/admit-final"));
        var finalWaveCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admittedFinal.Snapshot).Checkpoint;
        var finalOperation = Assert.Single(
            PendingChildOperations(finalWaveCheckpoint, artifacts.WorkerInvocationRequest));
        Assert.Equal(2, finalWaveCheckpoint.Continuation.Children.Count(static child =>
            child.Disposition == ProcessChildDisposition.Completed));
        Assert.Single(finalWaveCheckpoint.Continuation.Children, static child =>
            child.Disposition == ProcessChildDisposition.Active);

        var finalAdvanced = await coordinatorRuntime.AdvanceOperationAsync(
            context,
            artifacts.CoordinatorPlan,
            coordinatorContinuation.ProcessInstanceId,
            finalOperation.OperationId);
        truthfulOrigins.Add(await AssertTruthfulChildOriginAsync(
            context,
            artifacts,
            workerRuntime,
            Assert.IsType<DurableOperationState>(finalAdvanced.Operation)));

        var readiness = await materialization.Executor.InspectReadinessAsync(context, attempt);
        Assert.NotNull(readiness);
        Assert.Equal(execution.Generation, readiness.Generation);
        Assert.Equal(3, readiness.Shards.Length);
        Assert.All(readiness.Shards, static progress =>
        {
            Assert.Equal(MaterializationCheckpointKind.BatchCompleted, progress.LatestBatchCheckpoint?.Kind);
            Assert.Equal(MaterializationCheckpointKind.ChangeProgress, progress.LatestChangeCheckpoint?.Kind);
        });

        var preparationRequested = await ActivateAndCompareAsync(
            context,
            coordinatorStore,
            coordinatorRuntime,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            NextActivation(
                artifacts,
                Assert.IsType<ProcessDurableStoreSnapshot>(finalAdvanced.Snapshot).Checkpoint,
                id: "activation/coordinator/request-synchronization"));
        var preparationRequestedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(
            preparationRequested.Snapshot).Checkpoint;
        var preparationOperation = Assert.Single(
            preparationRequestedCheckpoint.DurableOperations,
            operation => operation.Request.Contract == artifacts.SynchronizationPreparationRequest
                && operation.Status != DurableOperationStatus.Dispositioned);

        var preparationAdvanced = await coordinatorRuntime.AdvanceOperationAsync(
            context,
            artifacts.CoordinatorPlan,
            coordinatorContinuation.ProcessInstanceId,
            preparationOperation.OperationId);
        Assert.Equal(DurableOperationStatus.Dispositioned, preparationAdvanced.Operation?.Status);
        Assert.Equal(
            MaterializationRebuildProcessFactory.ReadyOutcome,
            preparationAdvanced.Operation?.Acknowledgement?.Outcome.Id);

        var completionActivation = NextActivation(
            artifacts,
            Assert.IsType<ProcessDurableStoreSnapshot>(preparationAdvanced.Snapshot).Checkpoint,
            id: "activation/coordinator/complete");
        var completed = await ActivateAndCompareAsync(
            context,
            coordinatorStore,
            coordinatorRuntime,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            completionActivation);
        var completedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(completed.Snapshot).Checkpoint;

        var replayedCompletion = await coordinatorRuntime.ActivateAsync(
            context,
            artifacts.CoordinatorPlan,
            coordinatorContinuation,
            completionActivation);
        var replayedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(replayedCompletion.Snapshot).Checkpoint;

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, completedCheckpoint.Continuation.Terminal.Kind);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayedCompletion.Disposition);
        Assert.Null(replayedCompletion.Decision);
        Assert.Equivalent(completedCheckpoint.Continuation, replayedCheckpoint.Continuation, strict: true);
        Assert.Equal(
            completedCheckpoint.DurableOperations.Select(static operation => operation.OperationId),
            replayedCheckpoint.DurableOperations.Select(static operation => operation.OperationId));
        var readyGenerationReference = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
            Assert.IsType<ObservationValue>(
                    Assert.IsType<PortableValue>(completedCheckpoint.Continuation.Terminal.Detail?.Value).Value)
                .GetRequiredString());
        Assert.Equal(materialization.Plan.Fingerprint, readyGenerationReference.Plan);
        Assert.Equal(execution.Authority, readyGenerationReference.Authority);
        Assert.Equal(attempt, readyGenerationReference.Attempt);
        Assert.Equal(execution.Generation, readyGenerationReference.Generation);
        Assert.True(readyGenerationReference.Preparation.IsReady);
        Assert.Equal(3, truthfulOrigins.Count);
        Assert.Equal(3, truthfulOrigins.Select(static origin => origin.Continuation).Distinct().Count());

        var readyGeneration = await materialization.Target.InspectGenerationAsync(context, execution.Generation);
        var readyTarget = await materialization.Target.InspectAsync(context);
        Assert.Equal(MaterializationGenerationState.Validated, readyGeneration?.State);
        Assert.Null(readyTarget.ActiveGenerationId);

        var activation = await execution.ActivateReadyAsync(context, readyGenerationReference);
        Assert.Equal(MaterializationGenerationActivationDisposition.Active, activation.Disposition);
        var generation = await materialization.Target.InspectGenerationAsync(context, execution.Generation);
        var target = await materialization.Target.InspectAsync(context);
        var items = await materialization.Target.InspectItemsAsync(
            context,
            execution.Generation,
            afterItemId: null,
            maximumItems: 100);
        Assert.Equal(MaterializationGenerationState.Active, generation?.State);
        Assert.Equal(execution.Generation, target.ActiveGenerationId);
        Assert.Equal(9, items?.Items.Length);
    }

    static async Task<ProcessInteractionOrigin> AssertTruthfulChildOriginAsync(
        OperationContext context,
        MaterializationRebuildProcessArtifacts artifacts,
        ProcessDurableRuntime workerRuntime,
        DurableOperationState operation)
    {
        Assert.Equal(DurableOperationStatus.Dispositioned, operation.Status);
        var target = Assert.IsType<ProcessChildRequestTarget>(operation.Request.ChildTarget);
        var origin = Assert.IsType<ProcessInteractionOrigin>(operation.Acknowledgement?.ReplyOrigin);
        Assert.Equal(artifacts.WorkerPlan.DefinitionReference, origin.Definition);
        Assert.Equal(target.Continuation, origin.Continuation);

        var inspected = await workerRuntime.InspectAsync(
            context,
            artifacts.WorkerPlan,
            target.Continuation);
        var child = Assert.IsType<ProcessDurableStoreSnapshot>(inspected.Snapshot).Checkpoint;
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, child.Continuation.Terminal.Kind);
        var terminalReceipt = child.Activations[^1];
        var terminalTrace = terminalReceipt.Evidence.Trace[^1];
        Assert.Equal(terminalReceipt.Activation.Id, origin.Activation);
        Assert.Equal(terminalTrace.Node, origin.Node);
        Assert.Equal(terminalTrace.Token, origin.Token);
        return origin;
    }

    static async Task<ProcessDurableActivationResult> ActivateAndCompareAsync(
        OperationContext context,
        InMemoryProcessDurableStore store,
        ProcessDurableRuntime runtime,
        Cohesive.Processes.Compilation.CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        ProcessActivation activation)
    {
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            context,
            continuation.ProcessInstanceId));
        var expected = ProcessReferenceInterpreter.Activate(
            plan,
            before.Checkpoint.Continuation,
            activation,
            RejectingHost.Instance);

        var result = await runtime.ActivateAsync(
            context,
            plan,
            continuation,
            activation);
        var actual = Assert.IsType<ProcessActivationDecision>(result.Decision);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equivalent(expected, actual, strict: true);
        Assert.Equivalent(expected.State, checkpoint.Continuation, strict: true);
        return result;
    }

    static ImmutableArray<DurableOperationState> PendingChildOperations(
        ProcessDurableCheckpoint checkpoint,
        RequestContractReference request) =>
        [.. checkpoint.DurableOperations
            .Where(operation => operation.Request.Contract == request
                && operation.Status != DurableOperationStatus.Dispositioned)
            .OrderBy(static operation => operation.OperationId.Value, StringComparer.Ordinal)];

    static ProcessStartReceipt Start(
        MaterializationRebuildProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation,
        MaterializationRebuildLeafExecutionAuthority authority)
    {
        var planReference = MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(authority);
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.CoordinatorPlan.DefinitionReference,
            context: new(
                commandId: new("command/materialization-rebuild/start"),
                idempotencyKey: new("idempotency/materialization-rebuild/start"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/materialization-rebuild-conformance",
                    authorityScope: Authority,
                    evidenceReference: "policy/materialization-rebuild/allow"),
                issuedAtUtc: StartedAtUtc,
                provenance: Provenance("process-start")),
            initialContinuation: continuation,
            input: PortableValue.Concrete(
                artifacts.CoordinatorPlan.Definition.Input,
                ObservationValue.FromString(planReference)));
        return new(request, acceptedAtUtc: StartedAtUtc);
    }

    static ProcessActivation NextActivation(
        MaterializationRebuildProcessArtifacts artifacts,
        ProcessDurableCheckpoint checkpoint,
        string id)
    {
        var inputs = checkpoint.Inbox
            .Where(entry => entry.Receipt is null
                && entry.Input.Target.Continuation == checkpoint.Continuation.Continuation)
            .OrderBy(static entry => entry.EmissionId.Value, StringComparer.Ordinal)
            .Select(static entry => entry.Input)
            .ToImmutableArray();
        Assert.NotEmpty(inputs);
        return Activation(artifacts, id, ProcessActivationCause.Interaction, inputs);
    }

    static ProcessActivation Activation(
        MaterializationRebuildProcessArtifacts artifacts,
        string id,
        ProcessActivationCause cause,
        ImmutableArray<ProcessActivationInput> inputs) => new(
        id: new(id),
        cause,
        observedAtUtc: StartedAtUtc,
        context: new(
            authorityScope: Authority,
            correlationId: new("correlation/materialization-rebuild-conformance"),
            delivery: new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance: artifacts.CoordinatorProcessDocument.Metadata.Provenance),
        inputs);

    static MaterializationFixture CreateMaterializationFixture()
    {
        RelationQueryCompilationRequest compilationRequest = new(
            FederatedLoadRelationFixture.RelationDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var semantic = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument);
        var root = Assert.Single(semantic.Plan.InputContract.Sources);
        var output = Assert.Single(
            semantic.Plan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(compilationRequest, output.Id);
        var definition = Definition(semantic.Plan, relation);
        var materialization = MaterializationDocument.FromDefinition(definition);
        var sourceIds = SourceIdentities(semantic.Plan, root);
        var sourcePlans = definition.Sources.Select(requirement =>
        {
            var source = sourceIds[requirement.Input];
            var profile = CapabilityProfile(
                id: $"tests/rebuild-process-source/{Uri.EscapeDataString(requirement.Input.Value)}/v1",
                role: MaterializationEndpointRole.Source,
                subject: source.Value,
                requirements: requirement.Capabilities);
            return new MaterializationRebuildSourcePlan(
                input: requirement.Input,
                source,
                profile,
                capabilityMatch: MaterializationCapabilityMatcher.MatchForMode(
                    requirement.Capabilities,
                    profile,
                    MaterializationSynchronizationMode.Rebuild));
        }).ToImmutableArray();
        var rootSourcePlan = sourcePlans.Single(source => source.Input == root.Input.Id);
        var targetId = new MaterializationTargetId("tests/rebuild-process-target");
        var targetProfile = CapabilityProfile(
            id: "tests/rebuild-process-target/v1",
            role: MaterializationEndpointRole.Target,
            subject: targetId.Value,
            requirements: definition.TargetCapabilities);
        var targetDescriptor = new MaterializationTargetDescriptor(
            id: targetId,
            materializationId: definition.Id,
            capabilities: targetProfile);
        var scanPlacement = new RelationQuerySourcePlacementBinding(
            id: new("tests/rebuild-process-scan-placement"),
            input: root.Input.Id,
            node: root.Node,
            binding: root.Binding,
            shape: root.Shape,
            source: rootSourcePlan.Source,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new RelationQuerySourceIdentityBinding(root.Shape, "id"));
        var scanFingerprint = new RelationQueryPhysicalPlanFingerprint(
            algorithm: "sha256",
            canonicalization: "tests/rebuild-process-scan/v1",
            value: "0123456789abcdef");
        string[] shardIds = ["shard-a", "shard-b", "shard-c"];
        var shards = shardIds.Select(shardId =>
        {
            var suffix = shardId[^1];
            var scope = new MaterializationSourceScope(
                physicalPlan: scanFingerprint,
                placement: scanPlacement,
                partition: new($"partition-{suffix}"),
                orderingScope: new($"ordering-{suffix}"));
            var read = new RelationQuerySourceReadRequest(
                physicalPlan: scanFingerprint,
                stage: new("tests/rebuild-process-scan"),
                placementBinding: scanPlacement.Id,
                source: rootSourcePlan.Source,
                shape: root.Shape,
                identitySelector: "id",
                fields: [],
                constraint: new RelationQueryBoundedEnumeration(maximumRows: ReadItems),
                maximumBufferedRows: ReadItems);
            return new MaterializationRebuildShardPlan(
                id: new(shardId),
                scope,
                read,
                hydrationPhysicalPlan: semantic.PhysicalPlan.Fingerprint);
        }).ToImmutableArray();
        var impactPlan = MaterializationRebuildTestPlan.CompileImpactPlan(
            materialization,
            policyId: "tests/materialization-rebuild-process-impact/v1",
            maximumAffectedRoots: ReadItems,
            maximumReadBytes: ReadBytes);
        var changeFeedCatalog = MaterializationRebuildTestPlan.CreateChangeFeedCatalog(
            semantic.Plan,
            semantic.PhysicalPlan.Fingerprint,
            impactPlan,
            sourcePlans,
            shards,
            contributorPlacement: route => semantic.Placement.Bindings.Single(candidate =>
                candidate.Input == route.ChangeInput),
            channelCanonicalization: "tests/materialization-rebuild-process-channel/v1");
        var plan = new MaterializationRebuildPlan(
            materialization,
            placementSlice: MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlacementSlice(
                materialization,
                targetDescriptor),
            impactPlan,
            sources: sourcePlans,
            target: targetDescriptor,
            targetCapabilityMatch: MaterializationCapabilityMatcher.MatchForMode(
                definition.TargetCapabilities,
                targetProfile,
                MaterializationSynchronizationMode.Rebuild),
            shards: shards,
            changeFeedCatalogs: changeFeedCatalog.Evidence,
            changeFeeds: changeFeedCatalog.Feeds,
            limits: new(
                maximumPageItems: 2,
                maximumPageBytes: ReadBytes,
                maximumBulkItems: 2,
                maximumBulkBytes: WriteBytes,
                maximumPagesPerShard: 10,
                maximumStartsPerActivation: 2,
                maximumParallelism: 2,
                maximumChangeFeedsPerConvergenceActivation: 16),
            provenance: Provenance("rebuild-plan"));
        var physicalScenario = FederatedLoadConformanceData.CreatePhysicalScenario(
            semantic,
            rootCount: 9,
            distinctCustomerCount: 3,
            distinctEquipmentCount: 3);
        var hydrator = new TestHydrator(
            plan: RelationQueryCompiledPlanReference.From(semantic.Plan),
            physicalPlan: semantic.PhysicalPlan.Fingerprint,
            outputShape: output.Shape);
        var rootPhysicalSource = FederatedLoadPhysicalExecutionFixture.Source(
            semantic,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        Dictionary<MaterializationRebuildShardId, InMemoryMaterializationSource> sourceByShard = [];
        for (var index = 0; index < shardIds.Length; index++)
        {
            var observations = physicalScenario.SuppliedLoads.Observations.Slice(index * 3, 3);
            var reader = new StaticReader(
                descriptor: new(
                    source: rootPhysicalSource.Id,
                    executionDomain: rootPhysicalSource.ExecutionDomain,
                    targetProfile: rootPhysicalSource.TargetProfile,
                    logicalPartition: plan.Shards[index].Scope.LogicalPartition),
                result: new(
                    state: RelationQuerySourceReadState.Complete,
                    observations,
                    evidenceReference: $"tests/rebuild-process-source/{shardIds[index]}"));
            sourceByShard.Add(
                new(shardIds[index]),
                new InMemoryMaterializationSource(
                    new MaterializationQuerySourceDescriptor(reader, rootSourcePlan.Profile)));
        }

        var target = new InMemoryMaterializationTarget(targetDescriptor);
        var progress = new InMemoryMaterializationProgressStore();
        var impactInterpreter = new MaterializationImpactPlanInterpreter(
            plan.ImpactPlan,
            definition,
            new MaterializationTestImpactRuntime(plan.ImpactPlan.Fingerprint));
        var sourceByFeed = plan.ChangeFeeds.ToDictionary(
            static feed => feed.Id,
            feed =>
            {
                var matchingShard = plan.Shards.SingleOrDefault(shard => shard.Scope == feed.Scope);
                if (matchingShard is not null)
                {
                    return sourceByShard[matchingShard.Id];
                }

                var sourcePlan = plan.Sources.Single(source => source.Input == feed.Scope.Input);
                var reader = physicalScenario.Readers.Single(candidate =>
                    candidate.Descriptor.Source == sourcePlan.Source);
                return new InMemoryMaterializationSource(
                    new MaterializationQuerySourceDescriptor(reader, sourcePlan.Profile));
            });
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var resolved = new ResolvedMaterializationRebuildPlan(
            planSet,
            plan,
            target,
            progress,
            shardBindings: plan.Shards.Select(shard => new MaterializationRebuildShardBinding(
                shard,
                source: sourceByShard[shard.Id],
                hydrator)),
            changeFeedBindings: plan.ChangeFeeds.Select(feed => new MaterializationChangeFeedBinding(
                feed: feed,
                channel: feed.Channel,
                source: sourceByFeed[feed.Id],
                interpreter: impactInterpreter)));
        return new(plan, resolved, new MaterializationRebuildExecutor(resolved), target);
    }

    static MaterializationDefinition Definition(
        CompiledRelationQueryPlan plan,
        MaterializationRelationReference relation)
    {
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                isRoot: source.Role == RelationQuerySourceInputRole.RelationRoot)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                isRoot: false))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> target =
        [
            Requirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert, MaterializationSynchronizationMode.All),
            Requirement("target/delete", MaterializationCapabilityKind.TargetBulkDelete, MaterializationSynchronizationMode.All),
            Requirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationSynchronizationMode.All),
            Requirement("target/seal", MaterializationCapabilityKind.TargetSeal, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/validation", MaterializationCapabilityKind.TargetValidation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/abandonment", MaterializationCapabilityKind.TargetGenerationAbandonment, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/retirement", MaterializationCapabilityKind.TargetRetirement, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup, MaterializationSynchronizationMode.Rebuild)
        ];
        return new(
            id: new("tests/load-search-process-conformance"),
            relation,
            sources,
            targetCapabilities: target,
            updatePolicy: new(
                supportedModes: MaterializationSynchronizationMode.All,
                consistency: MaterializationConsistencyKind.BaselinePlusCatchUp,
                idempotency: MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(
                maximumAttempts: 3,
                exhaustedDisposition: MaterializationFailureDisposition.Stop),
            freshnessPolicy: new(
                maximumLagMilliseconds: 30_000,
                maximumUnsettledMilliseconds: 10_000),
            controlLoops: [],
            provenance: Provenance("materialization-definition"));
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind readCapability,
        bool isRoot)
    {
        ImmutableArray<MaterializationCapabilityRequirement> capabilities =
        [
            Requirement($"{input.Value}/read", readCapability, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/continuation", MaterializationCapabilityKind.SourceContinuation, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/changes", MaterializationCapabilityKind.SourceChangeDelivery, MaterializationSynchronizationMode.All),
            Requirement($"{input.Value}/settlement", MaterializationCapabilityKind.SourceSettlement, MaterializationSynchronizationMode.All)
        ];
        if (isRoot)
        {
            capabilities = capabilities.Add(Requirement(
                $"{input.Value}/inverse",
                MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                MaterializationSynchronizationMode.Incremental));
        }

        return new(input, capabilities);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) => new(
        id: new(id),
        capability,
        guarantees: Guarantees(capability),
        operatingLimits: Limits(capability),
        modes: modes);

    static MaterializationCapabilityProfile CapabilityProfile(
        string id,
        MaterializationEndpointRole role,
        string subject,
        ImmutableArray<MaterializationCapabilityRequirement> requirements) => new(
        id: new(id),
        role,
        subject,
        evidence:
        [
            .. requirements.Select(requirement => new MaterializationCapabilityEvidence(
                id: new($"evidence/{Uri.EscapeDataString(requirement.Id.Value)}"),
                capability: requirement.Capability,
                realization: CapabilityRealizationKind.Native,
                guarantees: requirement.Guarantees,
                operatingLimits: requirement.OperatingLimits,
                sourceReferences: ["tests/ari-176-process-conformance/v1"]))
        ]);

    static ImmutableArray<MaterializationGuaranteeKind> Guarantees(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.AtLeastOnceDelivery,
                    MaterializationGuaranteeKind.BaselinePlusCatchUp,
                    MaterializationGuaranteeKind.CompleteMutationDelivery
                ],
            MaterializationCapabilityKind.SourceSettlement =>
                [MaterializationGuaranteeKind.ExplicitSettlement],
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [
                    MaterializationGuaranteeKind.GenerationIsolation,
                    MaterializationGuaranteeKind.FencedMutation
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete =>
                [
                    MaterializationGuaranteeKind.IdempotentWrite,
                    MaterializationGuaranteeKind.FencedMutation,
                    MaterializationGuaranteeKind.VersionConditionalWrite
                ],
            MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationGuaranteeKind.ExactPerItemOutcome],
            MaterializationCapabilityKind.TargetFencedPromotion =>
                [
                    MaterializationGuaranteeKind.AtomicPromotion,
                    MaterializationGuaranteeKind.FencedPromotion
                ],
            MaterializationCapabilityKind.TargetGenerationAbandonment =>
                [MaterializationGuaranteeKind.AtomicDurableGenerationExclusion],
            MaterializationCapabilityKind.TargetSeal
                or MaterializationCapabilityKind.TargetValidation
                or MaterializationCapabilityKind.TargetRetirement
                or MaterializationCapabilityKind.TargetCleanup =>
                [MaterializationGuaranteeKind.FencedMutation],
            _ => []
        };

    static ImmutableArray<MaterializationOperatingLimit> Limits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, ReadItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, ReadItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

    static ImmutableDictionary<RelationQueryInputId, RelationQuerySourceInstanceId> SourceIdentities(
        CompiledRelationQueryPlan plan,
        RelationQuerySourceInputContract root)
    {
        var builder = ImmutableDictionary.CreateBuilder<RelationQueryInputId, RelationQuerySourceInstanceId>();
        builder.Add(root.Input.Id, FederatedLoadPhysicalExecutionFixture.LoadsSource);
        foreach (var traversal in plan.InputContract.Traversals)
        {
            var source = traversal.ResultShape == FederatedLoadRelationFixture.CustomerShapeId
                ? FederatedLoadPhysicalExecutionFixture.CustomersSource
                : FederatedLoadPhysicalExecutionFixture.EquipmentSource;
            builder.Add(traversal.Input.Id, source);
        }
        return builder.ToImmutable();
    }

    static ExecutionProvenance Provenance(string source) => new(
        producer: new("tests/materialization-rebuild-process-conformance", "1"),
        source: new($"tests/ari-176/{source}"),
        origin: DocumentOrigin.Generated);

    sealed record MaterializationFixture(
        MaterializationRebuildPlan Plan,
        ResolvedMaterializationRebuildPlan Resolved,
        MaterializationRebuildExecutor Executor,
        InMemoryMaterializationTarget Target);

    sealed class StaticReader(
        RelationQuerySourceReaderDescriptor descriptor,
        RelationQuerySourceReadResult result) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor { get; } = descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    sealed class TestHydrator(
        RelationQueryCompiledPlanReference plan,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        QualifiedShapeId outputShape) : IMaterializationRebuildHydrator
    {
        public RelationQueryCompiledPlanReference Plan { get; } = plan;

        public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; } = physicalPlan;

        public ValueTask<MaterializationRebuildHydrationResult> HydrateAsync(
            OperationContext context,
            MaterializationRebuildHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            ImmutableArray<RelationQueryOutputRow> rows =
            [
                .. request.Page.Read.Observations.Select(observation => new RelationQueryOutputRow(
                    shape: outputShape,
                    value: ObservationValue.FromObject(
                        new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                        {
                            ["id"] = ObservationValue.FromString(observation.Identity)
                        }),
                    identity: ObservationValue.FromString(observation.Identity),
                    root: null,
                    inputOccurrences: [],
                    unresolvedGaps: []))
            ];
            return ValueTask.FromResult(new MaterializationRebuildHydrationResult(
                rows,
                evidenceReference: request.Evaluation.Value));
        }
    }

    sealed class ExactExecutionResolver(MaterializationRebuildExecution execution)
        : IMaterializationRebuildExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? resolved)
        {
            resolved = execution.Authority == authority && execution.Attempt.Continuation == continuation
                ? execution
                : null;
            return resolved is not null;
        }
    }

    sealed class ExactBindingResolver(ImmutableArray<DurableRequestBinding> bindings)
        : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
        {
            binding = bindings.FirstOrDefault(candidate => candidate.Request == request.Contract);
            return binding is not null;
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
