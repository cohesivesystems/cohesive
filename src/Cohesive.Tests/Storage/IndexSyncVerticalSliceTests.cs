using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Cohesive.Tests.Elastic;
using Cohesive.Tests.ExecutionKernel;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.Tests.Storage;

/// <summary>
/// Shared industrial index-sync acceptance slice. Provider policy changes, while canonical Relations hydration,
/// impact interpretation, durable progress, Control, and the Elasticsearch generation target remain identical.
/// </summary>
public sealed class IndexSyncVerticalSliceTests
{
    const long ReadBytes = 1_000_000;
    const long WriteBytes = 1_000_000;
    const int MaximumItems = 10;
    const string ReadAlias = "index-sync-read";
    const string MaterializationIdValue = "tests/index-sync/materialization";
    const string TargetIdValue = "tests/index-sync/elastic-target";
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly InteractionAuthorityScope ProcessAuthority =
        new("authority/index-sync-vertical-slice", "tenant/cohesive");
    static readonly byte[] AuthenticationKey = Convert.FromHexString(
        "6A68A7530D77D4EC92FC40B9DA97BEA07E19BB7269C8A2B2E8B8FD640F1689F7");

    [Theory]
    [InlineData(SourceProvider.Cosmos)]
    [InlineData(SourceProvider.Postgres)]
    public async Task SharedRelation_RebuildsResumesConvergesAndPromotesThroughRealAdapters(
        SourceProvider provider)
    {
        var semantic = CreateSemanticFixture();
        await using var source = await CreateSourceAsync(provider, semantic);
        var target = CreateTarget(semantic.Definition, semantic.Readback.StorageBinding);
        var fixture = CreateExecutionFixture(semantic, source, target);
        var context = OperationContext.Create(timeProvider: fixture.Clock);
        var attempt = Attempt("attempt-1", StartedAtUtc);
        var generation = MaterializationRebuildIdentities.Generation(fixture.Plan, attempt);
        target.Observer.Bind(fixture.ControlProvider, fixture.Clock);
        target.Transport.EnqueueRetryableBulkItemFailure(itemOrdinal: 0);
        var interrupted = new ThrowAfterFirstCheckpoint();
        var firstExecutor = new MaterializationRebuildExecutor(fixture.Resolved, interrupted);

        var begun = await firstExecutor.BeginAttemptAsync(context, attempt);
        Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, begun.Disposition);
        Assert.Equal(generation, begun.Generation);
        await AssertPauseAndContinuePreserveGenerationAsync(
            fixture,
            target,
            attempt,
            generation);

        await Assert.ThrowsAsync<InjectedRebuildInterruption>(() => firstExecutor.RunShardAsync(
            context,
            attempt,
            fixture.Shard.Id));
        var interruptedGeneration = await target.Target.InspectGenerationAsync(context, generation);
        Assert.Equal(MaterializationGenerationState.Loading, interruptedGeneration?.State);
        Assert.Equal(generation, begun.Generation);

        var resumedExecutor = new MaterializationRebuildExecutor(fixture.Resolved);
        var resumed = await resumedExecutor.RunShardAsync(context, attempt, fixture.Shard.Id);
        Assert.Equal(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            resumed.Disposition);
        Assert.Equal(generation, resumed.Generation);

        var generationIndex = target.Binding.GetGenerationIndexName(generation);
        var generationBulks = target.Transport.BulkRequests
            .Select(batch => batch.Where(operation => operation.Index == generationIndex).ToImmutableArray())
            .Where(static batch => !batch.IsEmpty)
            .ToImmutableArray();
        Assert.True(generationBulks.Length >= 3);
        Assert.Equal(2, generationBulks[0].Length);
        Assert.Equal(generationBulks[0][0].Id, Assert.Single(generationBulks[1]).Id);
        var controlled = Assert.Single(await fixture.ControlProvider
            .ForGeneration(generation)
            .GetSnapshotsAsync(context));
        Assert.Equal(1, controlled.State.OperatingPoint.Get(ControlActuatorKind.BatchItems).Quantity.Value);

        source.PublishUpdateAndDelete();
        var activation = new MaterializationGenerationActivationExecutor(
            fixture.Resolved,
            new InMemoryMaterializationSynchronizationWorkStore());
        MaterializationGenerationActivationResult result;
        var activationOrdinal = 0;
        do
        {
            result = await activation.ActivateAsync(
                context,
                attempt,
                new($"invocation/{provider}/{activationOrdinal}"),
                new($"worker/{provider}/{activationOrdinal}"));
            activationOrdinal++;
        }
        while (result.Disposition == MaterializationGenerationActivationDisposition.WorkRemaining
               && activationOrdinal < 8);

        Assert.Equal(MaterializationGenerationActivationDisposition.Active, result.Disposition);
        var active = await target.Target.InspectAsync(context);
        var activated = await target.Target.InspectGenerationAsync(context, generation);
        Assert.Equal(generation, active.ActiveGenerationId);
        Assert.Equal(MaterializationGenerationState.Active, activated?.State);
        Assert.Equal(2, activated?.VisibleItemCount);
        Assert.Equal(0, activated?.PendingRetryableMutationCount);
        await AssertPromotedReadbackAsync(semantic.Readback, target, generationIndex);
        await AssertExplicitBackendPoolSwapAsync(fixture.Plan, target, result, context);
        await AssertRestartAttemptAbandonsCandidateAndPreservesActiveGenerationAsync(
            fixture,
            target,
            generation);
    }

    static async Task AssertPauseAndContinuePreserveGenerationAsync(
        ExecutionFixture fixture,
        TargetFixture target,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation)
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var execution = new MaterializationRebuildExecution(
            fixture.Resolved,
            attempt,
            new InMemoryMaterializationSynchronizationWorkStore());
        Assert.Equal(generation, execution.Generation);
        var lifecycle = RebuildProcessLifecycle(
            fixture.Plan.Fingerprint,
            artifacts,
            new ExactRebuildExecutionResolver(execution));
        var initialized = await lifecycle.InitializeAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(attempt.StartedAtUtc)),
            RebuildProcessStart(artifacts, fixture.Plan.Fingerprint, attempt));
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var initialAffinity = Assert.Single(initialSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings);
        var candidateBeforeControl = await target.Target.InspectGenerationAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(attempt.StartedAtUtc)),
            generation);
        Assert.NotNull(candidateBeforeControl);
        var bulkCountBeforeControl = target.Transport.BulkRequests.Count();
        var controls = ProcessControlTestFixture.Create();

        var pausedAtUtc = attempt.StartedAtUtc.AddSeconds(1);
        var paused = await lifecycle.ApplyControlAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(pausedAtUtc)),
            controls.Pause(
                initialSnapshot.Checkpoint.Control,
                id: "pause/index-sync-vertical-slice",
                issuedAtUtc: pausedAtUtc));
        var pausedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot);
        var continuedAtUtc = attempt.StartedAtUtc.AddSeconds(2);
        var continued = await lifecycle.ApplyControlAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(continuedAtUtc)),
            controls.Continue(
                pausedSnapshot.Checkpoint.Control,
                id: "continue/index-sync-vertical-slice",
                issuedAtUtc: continuedAtUtc));
        var continuedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(continued.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.ProcessDisposition);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, paused.ProcessDisposition);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, continued.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, initialized.Realization);
        Assert.Equal(MaterializationRebuildProcessRealization.Preserved, paused.Realization);
        Assert.Equal(MaterializationRebuildProcessRealization.Preserved, continued.Realization);
        Assert.Equal(generation, initialized.Generation);
        Assert.Equal(generation, paused.Generation);
        Assert.Equal(generation, continued.Generation);
        Assert.Equal(ProcessControlMode.Paused, pausedSnapshot.Checkpoint.Control.Mode);
        Assert.Equal(ProcessControlMode.Running, continuedSnapshot.Checkpoint.Control.Mode);
        Assert.Equal(
            initialAffinity,
            Assert.Single(pausedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings));
        Assert.Equal(
            initialAffinity,
            Assert.Single(continuedSnapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings));
        Assert.Equal(
            candidateBeforeControl,
            await target.Target.InspectGenerationAsync(
                OperationContext.Create(timeProvider: new FixedTimeProvider(continuedAtUtc)),
                generation));
        Assert.Equal(bulkCountBeforeControl, target.Transport.BulkRequests.Count());
    }

    static async Task AssertRestartAttemptAbandonsCandidateAndPreservesActiveGenerationAsync(
        ExecutionFixture fixture,
        TargetFixture target,
        MaterializationGenerationId activeGeneration)
    {
        var candidateAttempt = Attempt("attempt-2", StartedAtUtc.AddMinutes(1));
        var candidateExecution = new MaterializationRebuildExecution(
            fixture.Resolved,
            candidateAttempt,
            new InMemoryMaterializationSynchronizationWorkStore());
        var replacementAttempt = Attempt("attempt-3", StartedAtUtc.AddMinutes(2));
        var replacementExecution = new MaterializationRebuildExecution(
            fixture.Resolved,
            replacementAttempt,
            new InMemoryMaterializationSynchronizationWorkStore());
        Assert.NotEqual(activeGeneration, candidateExecution.Generation);
        Assert.NotEqual(candidateExecution.Generation, replacementExecution.Generation);
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var resolver = new ExactRebuildExecutionResolver(candidateExecution);
        var lifecycle = RebuildProcessLifecycle(fixture.Plan.Fingerprint, artifacts, resolver);
        var candidateInitialized = await lifecycle.InitializeAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(candidateAttempt.StartedAtUtc)),
            RebuildProcessStart(artifacts, fixture.Plan.Fingerprint, candidateAttempt));
        var candidateProcess = Assert.IsType<ProcessDurableStoreSnapshot>(candidateInitialized.Snapshot);
        var candidate = await target.Target.InspectGenerationAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(candidateAttempt.StartedAtUtc)),
            candidateExecution.Generation);
        var candidateControl = Assert.Single(await fixture.ControlProvider
            .ForGeneration(candidateExecution.Generation)
            .GetSnapshotsAsync(OperationContext.Create(
                timeProvider: new FixedTimeProvider(candidateAttempt.StartedAtUtc))));
        var servingBeforeRestart = await target.Target.InspectAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(candidateAttempt.StartedAtUtc)));
        var aliasBeforeRestart = Assert.Single((await target.Transport.InspectAliasesAsync(
            [ReadAlias],
            maximumResponseBytes: checked((int)WriteBytes),
            CancellationToken.None)).Bindings);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, candidateInitialized.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, candidateInitialized.Realization);
        Assert.Equal(candidateExecution.Generation, candidateInitialized.Generation);
        Assert.Equal(MaterializationGenerationState.Loading, candidate?.State);
        Assert.Equal(activeGeneration, servingBeforeRestart.ActiveGenerationId);
        Assert.Equal(target.Binding.GetGenerationIndexName(activeGeneration), aliasBeforeRestart.Index);
        Assert.Equal(candidateControl.Key.Epoch, candidateControl.State.Epoch);
        Assert.Equal(
            2,
            candidateControl.State.OperatingPoint.Get(ControlActuatorKind.BatchItems).Quantity.Value);
        resolver.Add(replacementExecution);
        var controls = ProcessControlTestFixture.Create();
        var restarted = await lifecycle.ApplyControlAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc)),
            controls.Restart(
                candidateProcess.Checkpoint.Control,
                newAttemptId: replacementAttempt.Continuation.ProcessAttemptId.Value,
                id: "restart/index-sync-vertical-slice",
                issuedAtUtc: replacementAttempt.StartedAtUtc));
        var restartedProcess = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot);

        var abandoned = await target.Target.InspectGenerationAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc)),
            candidateExecution.Generation);
        var abandonmentEvidence = await target.Target.AbandonGenerationAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc)),
            new(
                abandonmentId: MaterializationRebuildIdentities.Abandonment(
                    fixture.Plan,
                    candidateAttempt),
                generationId: candidateExecution.Generation,
                abandonedAtUtc: replacementAttempt.StartedAtUtc));
        var replacement = await target.Target.InspectGenerationAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc)),
            replacementExecution.Generation);
        var replacementControl = Assert.Single(await fixture.ControlProvider
            .ForGeneration(replacementExecution.Generation)
            .GetSnapshotsAsync(OperationContext.Create(
                timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc))));
        var servingAfterRestart = await target.Target.InspectAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(replacementAttempt.StartedAtUtc)));
        var aliasAfterRestart = Assert.Single((await target.Transport.InspectAliasesAsync(
            [ReadAlias],
            maximumResponseBytes: checked((int)WriteBytes),
            CancellationToken.None)).Bindings);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.ProcessDisposition);
        Assert.Equal(MaterializationRebuildProcessRealization.Ready, restarted.Realization);
        Assert.Equal(replacementExecution.Generation, restarted.Generation);
        Assert.Equal(replacementAttempt.Continuation, restartedProcess.Checkpoint.ContinuationIdentity);
        Assert.Equal(2, restartedProcess.Checkpoint.Control.Attempts.Length);
        Assert.Equal(
            MaterializationRebuildIdentities.GenerationAffinity(
                MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
                candidateExecution.Generation),
            Assert.Single(restartedProcess.Checkpoint.Control.Attempts[0].AffinityBindings).Affinity);
        Assert.Equal(
            MaterializationRebuildIdentities.GenerationAffinity(
                MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId,
                replacementExecution.Generation),
            Assert.Single(restartedProcess.Checkpoint.Control.CurrentAttempt.AffinityBindings).Affinity);
        Assert.Equal(MaterializationGenerationState.Retired, abandoned?.State);
        Assert.Equal(MaterializationTargetOperationDisposition.Replayed, abandonmentEvidence.Disposition);
        Assert.Equal(MaterializationGenerationState.Retired, abandonmentEvidence.Generation?.State);
        Assert.Equal(
            MaterializationRebuildIdentities.Abandonment(fixture.Plan, candidateAttempt),
            abandonmentEvidence.Receipt?.AbandonmentId);
        Assert.Equal(candidateExecution.Generation, abandonmentEvidence.Receipt?.GenerationId);
        Assert.Equal(replacementAttempt.StartedAtUtc, abandonmentEvidence.Receipt?.AbandonedAtUtc);
        Assert.Equal(MaterializationGenerationState.Loading, replacement?.State);
        Assert.Equal(activeGeneration, servingAfterRestart.ActiveGenerationId);
        Assert.Equal(servingBeforeRestart.Revision, servingAfterRestart.Revision);
        Assert.Equal(servingBeforeRestart.LatestPromotionFence, servingAfterRestart.LatestPromotionFence);
        Assert.Equal(aliasBeforeRestart.Alias, aliasAfterRestart.Alias);
        Assert.Equal(aliasBeforeRestart.Index, aliasAfterRestart.Index);
        Assert.Equal(aliasBeforeRestart.Filter, aliasAfterRestart.Filter);
        Assert.Equal(candidateExecution.Generation, candidateControl.Key.GenerationId);
        Assert.Equal(replacementExecution.Generation, replacementControl.Key.GenerationId);
        Assert.NotEqual(candidateControl.Key.Epoch, replacementControl.Key.Epoch);
        Assert.Equal(replacementControl.Key.Epoch, replacementControl.State.Epoch);
        Assert.Equal(
            2,
            replacementControl.State.OperatingPoint.Get(ControlActuatorKind.BatchItems).Quantity.Value);
    }

    static MaterializationRebuildProcessLifecycle RebuildProcessLifecycle(
        MaterializationRebuildPlanFingerprint plan,
        MaterializationRebuildProcessArtifacts artifacts,
        IMaterializationRebuildExecutionResolver resolver) =>
        new(
            new ProcessDurableRuntime(
                new InMemoryProcessDurableStore(),
                RejectingProcessReferenceHost.Instance,
                new(
                    workerId: "worker/index-sync-vertical-slice",
                    workerLease: TimeSpan.FromMinutes(5),
                    maxAmbiguousStoreMutationAttempts: 3)),
            artifacts,
            plan,
            resolver);

    static ProcessStartReceipt RebuildProcessStart(
        MaterializationRebuildProcessArtifacts artifacts,
        MaterializationRebuildPlanFingerprint plan,
        MaterializationRebuildAttempt attempt)
    {
        var planReference = MaterializationRebuildWorkReferenceJsonSerializer.SerializePlan(new(plan));
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.CoordinatorPlan.DefinitionReference,
            context: new(
                commandId: new("command/index-sync-vertical-slice/start"),
                idempotencyKey: new("idempotency/index-sync-vertical-slice/start"),
                processInstanceId: attempt.Continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/index-sync-vertical-slice",
                    authorityScope: ProcessAuthority,
                    evidenceReference: "policy/index-sync-vertical-slice/allow"),
                issuedAtUtc: attempt.StartedAtUtc,
                provenance: Provenance("process-start")),
            initialContinuation: attempt.Continuation,
            input: PortableValue.Concrete(
                artifacts.CoordinatorPlan.Definition.Input,
                ObservationValue.FromString(planReference)));
        return new(request, acceptedAtUtc: attempt.StartedAtUtc);
    }

    static ExecutionFixture CreateExecutionFixture(
        SemanticFixture semantic,
        SourceFixture source,
        TargetFixture target)
    {
        var materialization = MaterializationDocument.FromDefinition(semantic.Definition);
        var sourceRequirement = Assert.Single(semantic.Definition.Sources);
        var sourceProfile = source.Source.Descriptor.CapabilityProfile;
        MaterializationRebuildSourcePlan sourcePlan = new(
            input: sourceRequirement.Input,
            source: source.Source.Descriptor.Source,
            profile: sourceProfile,
            capabilityMatch: MaterializationCapabilityMatcher.MatchForMode(
                sourceRequirement.Capabilities,
                sourceProfile,
                MaterializationSynchronizationMode.Rebuild));
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            semantic.Definition.TargetCapabilities,
            target.Target.Descriptor.Capabilities,
            MaterializationSynchronizationMode.Rebuild);
        MaterializationRebuildShardPlan shard = new(
            id: new("shard/root"),
            scope: source.Scope,
            read: source.Read,
            hydrationPhysicalPlan: semantic.HydrationPhysicalPlan.Fingerprint);
        var impactPlan = MaterializationRebuildTestPlan.CompileImpactPlan(
            materialization,
            policyId: "tests/index-sync/impact/v1",
            maximumAffectedRoots: MaximumItems,
            maximumReadBytes: ReadBytes);
        var feedCatalog = MaterializationRebuildTestPlan.CreateChangeFeedCatalog(
            semantic.Plan,
            source.Scope.PhysicalPlan,
            impactPlan,
            sourcePlans: [sourcePlan],
            shards: [shard],
            contributorPlacement: _ => throw new InvalidOperationException(
                "The shared one-root relation has no contributor feed."),
            channelCanonicalization: "tests/index-sync/channel/v1");
        MaterializationRebuildPlan plan = new(
            materialization,
            impactPlan,
            sources: [sourcePlan],
            target: target.Target.Descriptor,
            targetCapabilityMatch: targetMatch,
            shards: [shard],
            changeFeedCatalogs: feedCatalog.Evidence,
            changeFeeds: feedCatalog.Feeds,
            limits: new(
                maximumPageItems: 2,
                maximumPageBytes: ReadBytes,
                maximumBulkItems: 2,
                maximumBulkBytes: WriteBytes,
                maximumPagesPerShard: 10,
                maximumStartsPerActivation: 2,
                maximumParallelism: 2,
                maximumChangeFeedsPerConvergenceActivation: 4),
            provenance: Provenance("rebuild-plan"));
        var controlProvider = new MaterializationIndexSyncControlRuntimeProvider(
            plan,
            new InMemoryMaterializationIndexSyncControlStateStore(),
            new MaterializationIndexSyncAdmissionGate());
        var hydrator = new RelationQueryMaterializationRebuildHydrator(
            semantic.Plan,
            semantic.HydrationPhysicalPlan,
            semantic.Realization,
            semantic.Root.Input.Id,
            semantic.Output,
            sourceReaders: []);
        var impactRuntime = new RelationQueryMaterializationImpactRuntime(
            impactPlan,
            semantic.Definition,
            semantic.HydrationPhysicalPlan,
            semantic.Realization,
            sourceReaders: []);
        var interpreter = new MaterializationImpactPlanInterpreter(
            impactPlan,
            semantic.Definition,
            impactRuntime);
        var feed = Assert.Single(feedCatalog.Feeds);
        var resolved = new ResolvedMaterializationRebuildPlan(
            plan,
            target.Target,
            new InMemoryMaterializationProgressStore(),
            shardBindings: [new(shard, source.Source, hydrator)],
            changeFeedBindings: [new(feed, feed.Channel, source.Source, interpreter)],
            controlRuntimeProvider: controlProvider);
        return new(
            plan,
            shard,
            resolved,
            controlProvider,
            new MutableTimeProvider(StartedAtUtc));
    }

    static SemanticFixture CreateSemanticFixture()
    {
        var author = RelationQuery.Expression();
        var inputShape = author.Clr.Shape<CanonicalInput>();
        var outputShape = author.Clr.Shape<CanonicalOutput>();
        var items = author.Source(inputShape, "tests/index-sync/items");
        var projected = author.Project(
            items,
            (CanonicalInput item) => new CanonicalOutput { Id = item.Id, Name = item.Name },
            sourceReference: "tests/index-sync/project");
        var authored = projected.BuildRelation(
            (CanonicalOutput row) => row.Id,
            id: new("tests-index-sync-relation"),
            name: new("TestsIndexSyncRelation"),
            sourceReference: "tests/index-sync/relation");
        var compilationRequest = new RelationQueryCompilationRequest(
            authored.CreateDocument(),
            author.ShapeDocuments);
        var compilation = RelationQueryStaticCompiler.Compile(compilationRequest);
        var plan = compilation.Plan ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var root = Assert.Single(plan.InputContract.Sources);
        var output = Assert.Single(
            plan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(compilationRequest, output.Id);
        var definition = Definition(plan, relation);
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var hydrationPhysicalPlan = CreateHydrationPhysicalPlan(plan, realization, root);
        return new(
            plan,
            realization,
            hydrationPhysicalPlan,
            root,
            output,
            definition,
            CreateReadbackFixture());
    }

    static ReadbackFixture CreateReadbackFixture()
    {
        var author = RelationQuery.Expression();
        var sourceShape = author.Clr.Shape<CanonicalOutput>();
        var source = author.Source(sourceShape, "tests/index-sync/readback/source");
        var ordered = author.Order(
            source.Node,
            (CanonicalOutput row) => row.Id,
            source.Binding,
            sourceReference: "tests/index-sync/readback/order");
        var page = author.Page(
            ordered,
            new OffsetPageDefinition(limit: MaximumItems),
            sourceReference: "tests/index-sync/readback/page");
        var aggregate = author.Aggregate<SourceQueryNode, CanonicalCount>(
            source.Node,
            builder => builder.Count(result => result.Count),
            sourceReference: "tests/index-sync/readback/count");
        var rows = author.Rows(page, source.Binding, id: "rows");
        var count = author.Aggregation(aggregate, id: "count");
        var authored = author.BuildQuery(
            new("tests-index-sync-readback"),
            new("TestsIndexSyncReadback"),
            rows,
            count);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            authored.CreateDocument(),
            author.ShapeDocuments));
        var plan = compilation.Plan ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            ElasticRelationQueryTargetProfile.Default,
            ElasticRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        if (!realization.IsRealizable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                realization.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }

        var contract = Assert.Single(plan.InputContract.Sources);
        RelationQuerySourceInstance sourceInstance = new(
            new("tests/index-sync/readback/elastic-source"),
            new("tests/index-sync/readback/elastic-domain"),
            ElasticRelationQueryTargetProfile.Default,
            new(
                maximumBatchSize: MaximumItems,
                maximumBufferedRows: MaximumItems,
                maximumFanOut: MaximumItems,
                maximumConcurrency: 1));
        RelationQuerySourcePlacementBinding placementBinding = new(
            id: new("placement/index-sync/readback"),
            input: contract.Input.Id,
            node: contract.Node,
            binding: contract.Binding,
            shape: contract.Shape,
            source: sourceInstance.Id,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(contract.Shape, "value.id", FieldPath.FromField("id")),
            fields:
            [
                .. contract.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                    field.Input.Id,
                    field.Input.Field.Path,
                    $"value.{field.Input.Field.Path}"))
            ]);
        RelationQuerySourcePlacement placement = new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
            sourceInstances: [sourceInstance],
            bindings: [placementBinding]);
        var fieldBindings = contract.Fields.Select(static field =>
        {
            var semanticPath = field.Input.Field.Path;
            var isIdentity = semanticPath.Matches("id");
            var physical = $"value.{semanticPath}";
            return new ElasticRelationQueryFieldBinding(
                input: field.Input.Id,
                sourceField: FieldPath.Parse(physical),
                queryField: FieldPath.Parse($"{physical}.keyword"),
                mappingKind: ElasticRelationQueryFieldMappingKind.Keyword,
                retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
                retrievalEncoding: ElasticRelationQueryFieldValueEncoding.JsonString,
                documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
                semanticCapabilities: isIdentity
                    ? ElasticRelationQueryFieldSemanticCapabilities.ExactTerm
                        | ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering
                        | ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering
                    : ElasticRelationQueryFieldSemanticCapabilities.None,
                semanticProfile: isIdentity ? "tests/index-sync/ordinal-keyword/v1" : null,
                missingValueBehavior: ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion,
                nullValueBehavior: ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion);
        }).ToImmutableArray();
        ElasticRelationQueryStorageBinding storage = new(
            id: new("tests/index-sync/readback-binding/v1"),
            source: sourceInstance.Id,
            placementBinding: placementBinding.Id,
            target: ElasticRelationQueryTargetProfile.Target,
            targetProfile: ElasticRelationQueryTargetProfile.ProfileId,
            indexName: ReadAlias,
            fields: fieldBindings,
            paginationConsistency: ElasticRelationQueryPaginationConsistency.Unproven,
            conventionSetVersion: ElasticRelationQueryStorageBinding.SemanticPathConventionSet,
            compiledPlanFingerprint: RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(plan)),
            placementFingerprint: placement.Fingerprint);
        var native = new ElasticRelationQueryCompiler().Compile(
            new RelationQueryBoundRealizationRequest(plan, realization, placement),
            storage);
        if (!native.IsSuccessful)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                native.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        Assert.Equal(2, native.Artifacts.Length);
        return new(plan, realization, placement, storage, native.Artifacts);
    }

    static CompiledRelationQueryPhysicalPlan CreateHydrationPhysicalPlan(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourceInputContract root)
    {
        RelationQuerySourceInstanceId sourceId = new("tests/index-sync/hydration-root");
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/index-sync/hydration-root"),
            input: root.Input.Id,
            node: root.Node,
            binding: root.Binding,
            shape: root.Shape,
            source: sourceId,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.Supplied,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            fields:
            [
                .. root.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                    field.Input.Id,
                    field.Input.Field.Path,
                    CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(
                        field.Input.Field.Path)))
            ]);
        RelationQuerySourcePlacement sourcePlacement = new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            conventionSetVersion: "tests/index-sync/hydration-placement/v1",
            sourceInstances:
            [
                new(
                    sourceId,
                    new("tests/index-sync/hydration-domain"),
                    PrimitiveTargetProfile(),
                    new(
                        maximumBatchSize: MaximumItems,
                        maximumBufferedRows: MaximumItems,
                        maximumFanOut: MaximumItems,
                        maximumConcurrency: 1))
            ],
            bindings: [placement]);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            sourcePlacement,
            new(
                new("tests/index-sync/hydration-policy/v1"),
                sourcePlacement.ConventionSetVersion,
                maximumBatchSize: MaximumItems,
                maximumBufferedRows: MaximumItems,
                maximumLocalRows: MaximumItems,
                maximumFanOut: MaximumItems,
                maximumReferenceKeysPerObservation: MaximumItems,
                maximumConcurrency: 1));
        return physical.Plan ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            physical.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    static RelationQueryTargetCapabilityProfile PrimitiveTargetProfile()
    {
        RelationQueryPrimitiveCapabilityKind[] capabilities =
        [
            RelationQueryPrimitiveCapabilityKind.KeyExtraction,
            RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            RelationQueryPrimitiveCapabilityKind.PredicateRead,
            RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.LocalCorrelation,
            RelationQueryPrimitiveCapabilityKind.HashJoin,
            RelationQueryPrimitiveCapabilityKind.FieldProjection,
            RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead,
            RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead,
            RelationQueryPrimitiveCapabilityKind.ProvenanceTracking,
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup
        ];
        return new(
            new("tests/index-sync/hydration-target"),
            new("tests/index-sync/hydration-target-profile/v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                .. capabilities.Select(capability => new RelationQueryTargetCapabilityEvidence(
                    new($"evidence/{(int)capability}"),
                    new PrimitiveRelationQueryCapability(capability)))
            ]);
    }

    static MaterializationDefinition Definition(
        CompiledRelationQueryPlan plan,
        MaterializationRelationReference relation)
    {
        var root = Assert.Single(plan.InputContract.Sources);
        ImmutableArray<MaterializationCapabilityRequirement> sourceCapabilities =
        [
            Requirement("source/read", MaterializationCapabilityKind.SourceBoundedEnumeration, MaterializationSynchronizationMode.Rebuild),
            Requirement("source/continuation", MaterializationCapabilityKind.SourceContinuation, MaterializationSynchronizationMode.Rebuild),
            Requirement("source/changes", MaterializationCapabilityKind.SourceChangeDelivery, MaterializationSynchronizationMode.All)
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targetCapabilities =
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
        var control = TargetBatchControl();
        return new(
            id: new(MaterializationIdValue),
            relation,
            sources: [new(root.Input.Id, sourceCapabilities)],
            targetCapabilities,
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
            controlLoops: [control],
            provenance: Provenance("definition"),
            controlWorkloads: [new(control.Id, MaterializationIndexSyncWorkloadKind.Rebuild)]);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) => new(
        id: new(id),
        capability,
        guarantees: Guarantees(capability),
        operatingLimits: OperatingLimits(capability),
        modes);

    static ImmutableArray<MaterializationGuaranteeKind> Guarantees(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBoundedEnumeration =>
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

    static ImmutableArray<MaterializationOperatingLimit> OperatingLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, MaximumItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, MaximumItems),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, 2),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

    static ControlLoopDefinition TargetBatchControl() => new(
        ControlLoopDefinition.CurrentSchemaVersion,
        new("index-sync/elastic-target-batch"),
        target: MaterializationIdValue,
        applicationAuthority: MaterializationIndexSyncControlCompiler.ApplicationAuthority,
        stage: ControlStageKind.Target,
        hardLimits: new([
            new(
                new(
                    ControlActuatorKind.BatchItems,
                    new(1, ControlUnit.Count),
                    new(2, ControlUnit.Count)),
                ControlHardLimitOrigin.Semantic,
                "tests/index-sync/materialization/v1")
        ]),
        initialOperatingPoint: new([
            new(ControlActuatorKind.BatchItems, new(2, ControlUnit.Count))
        ]),
        objectives:
        [
            new(
                ControlMetricKind.RejectionRatio,
                ControlStatisticKind.Last,
                ControlObjectiveDirection.HigherIsCongested,
                new(0, ControlUnit.BasisPoints),
                new(2_500, ControlUnit.BasisPoints))
        ],
        policy: AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.BatchItems,
            new AimdControlPolicyLayer(
                EffectiveConfigurationOrigin.Explicit,
                "tests/index-sync/control-policy/v1",
                new AimdControlPolicySettings(
                    additiveIncrease: 1,
                    multiplicativeDecreaseBasisPoints: 5_000,
                    healthyObservationCount: 2,
                    recoveryCooldownMilliseconds: 1_000,
                    minimumDwellMilliseconds: 1_000,
                    maximumObservationAgeMilliseconds: 60_000,
                    minimumSampleCount: 1))),
        budgets: [],
        provenance: Provenance("control-loop"));

    static TargetFixture CreateTarget(
        MaterializationDefinition definition,
        ElasticRelationQueryStorageBinding searchBinding)
    {
        ElasticMaterializationTargetBinding binding = new(
            new("tests/index-sync/elastic-binding/v1"),
            new("cluster-index-sync"),
            new(TargetIdValue),
            definition.Id,
            ReadAlias,
            "index-sync-generation-",
            ".cohesive-index-sync-control",
            new(
                "index-sync-template",
                new("sha256", "elastic-index-template/v1", new string('a', 64)),
                "tests/index-sync/template/v1"),
            new("tests/index-sync/process-runtime/v1", "search-index/index-sync"),
            searchBinding);
        var runtime = new ElasticElasticsearchRuntimeBinding(
            binding.Cluster,
            new ElasticsearchClient(new ElasticsearchClientSettings(new InMemoryRequestInvoker())),
            "tests/index-sync/elastic-runtime/v1");
        var transport = new FakeElasticMaterializationTransport();
        ControlFeedbackObserver observer = new();
        return new(
            binding,
            transport,
            observer,
            new ElasticMaterializationTarget(
                binding: binding,
                policy: ElasticMaterializationTargetPolicy.Default,
                runtimeBinding: runtime,
                transport: transport,
                observer: observer));
    }

    static async Task AssertPromotedReadbackAsync(
        ReadbackFixture readback,
        TargetFixture target,
        string generationIndex)
    {
        var retained = await target.Transport.ScanAsync(
            new(
                generationIndex,
                ElasticMaterializationWireJson.MatchAllQuery,
                $"{ElasticMaterializationTargetBinding.MetadataField}.itemId",
                afterSortValue: null,
                maximumItems: MaximumItems,
                maximumResponseBytes: checked((int)WriteBytes)),
            CancellationToken.None);
        Assert.Equal(3, retained.Hits.Length);
        var retainedDocuments = retained.Hits.Select(static hit => Json(hit.Source)).ToImmutableArray();
        var tombstone = Assert.Single(retainedDocuments, static document => document
            .GetProperty(ElasticMaterializationTargetBinding.MetadataField)
            .GetProperty("deleted")
            .GetBoolean());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("value").ValueKind);

        var visible = await target.Transport.ScanAsync(
            new(
                ReadAlias,
                ElasticMaterializationWireJson.MatchAllQuery,
                $"{ElasticMaterializationTargetBinding.MetadataField}.itemId",
                afterSortValue: null,
                maximumItems: MaximumItems,
                maximumResponseBytes: checked((int)WriteBytes)),
            CancellationToken.None);
        var visibleCount = await target.Transport.CountAsync(
            ReadAlias,
            ElasticMaterializationWireJson.MatchAllQuery,
            maximumResponseBytes: checked((int)WriteBytes),
            CancellationToken.None);
        Assert.Equal(2, visibleCount.Count);
        Assert.Equal(2, visible.Hits.Length);
        var values = visible.Hits.Select(static hit => Json(hit.Source).GetProperty("value"))
            .ToImmutableArray();
        Assert.Contains(values, static value =>
            value.GetProperty("id").GetString() == "a"
            && value.GetProperty("name").GetString() == "Alpha Updated");
        Assert.DoesNotContain(values, static value => value.GetProperty("id").GetString() == "b");

        Assert.Equal(readback.StorageBinding.Fingerprint, target.Binding.SearchBinding.Fingerprint);
        foreach (var artifact in readback.Artifacts)
        {
            Assert.Equal(ReadAlias, artifact.RequestTemplate.Index);
            var response = artifact.Branch.Kind switch
            {
                RelationQueryNativeResultKind.QueryRows => SearchResponse(
                    generationIndex,
                    visible.Hits,
                    total: visibleCount.Count),
                RelationQueryNativeResultKind.QueryAggregation => SearchResponse(
                    generationIndex,
                    hits: [],
                    total: visibleCount.Count),
                _ => throw new InvalidOperationException(
                    $"Unexpected Elasticsearch readback branch '{artifact.Branch.Kind}'.")
            };
            var runtime = ReadbackRuntime(target.Binding.Cluster, response);
            var result = await new ElasticRelationQueryArtifactExecutor(runtime).ExecuteAsync(
                new(
                    RelationQueryCompiledPlanReference.From(readback.Plan),
                    readback.Realization.Fingerprint,
                    readback.Placement.Fingerprint,
                    readback.StorageBinding.Fingerprint,
                    runtime.Fingerprint,
                    artifact,
                    maximumRows: MaximumItems,
                    parameters: new Dictionary<QueryParameterId, ObservationValue>()));
            Assert.True(
                result.IsSuccessful,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            if (artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows)
            {
                Assert.Equal(2, result.Rows.Length);
                Assert.Contains(result.Rows, static row =>
                    Field(row, "id").String == "a"
                    && Field(row, "name").String == "Alpha Updated");
                Assert.DoesNotContain(result.Rows, static row => Field(row, "id").String == "b");
            }
            else
            {
                var count = Assert.Single(result.Rows);
                Assert.Equal(2, Field(count, "count").Int64);
            }
        }
    }

    static async Task AssertExplicitBackendPoolSwapAsync(
        MaterializationRebuildPlan plan,
        TargetFixture promotedTarget,
        MaterializationGenerationActivationResult activation,
        OperationContext context)
    {
        MaterializationTargetId priorTargetId = new("tests/index-sync/prior-target");
        var promotedDescriptor = promotedTarget.Target.Descriptor;
        MaterializationTargetDescriptor priorDescriptor = new(
            priorTargetId,
            promotedDescriptor.MaterializationId,
            new(
                new("tests/index-sync/prior-target-profile/v1"),
                MaterializationEndpointRole.Target,
                priorTargetId.Value,
                promotedDescriptor.Capabilities.Evidence));
        var priorTarget = new InMemoryMaterializationTarget(priorDescriptor);
        var priorGenerationId = new MaterializationGenerationId("generation/index-sync/prior");
        var priorCreatedAtUtc = StartedAtUtc.AddMinutes(-10);
        var priorBegun = await priorTarget.BeginGenerationAsync(
            context,
            new(
                promotedDescriptor.MaterializationId,
                priorGenerationId,
                plan.Materialization.DefinitionFingerprint,
                MaterializationWorkerFence.Initial,
                priorCreatedAtUtc));
        var priorSealed = await priorTarget.SealGenerationAsync(
            context,
            new(
                new("seal/index-sync/prior"),
                priorGenerationId,
                priorBegun.Generation!.Revision,
                MaterializationWorkerFence.Initial,
                priorCreatedAtUtc.AddMinutes(1)));
        var priorValidated = await priorTarget.ValidateGenerationAsync(
            context,
            new(
                new("validation/index-sync/prior"),
                priorGenerationId,
                priorSealed.Generation!.Revision,
                priorSealed.Receipt!.Fingerprint,
                expectedVisibleItemCount: 0,
                validator: "tests/index-sync/backend-pool-validator/v1",
                MaterializationWorkerFence.Initial,
                priorCreatedAtUtc.AddMinutes(2)));
        var priorPromoted = await priorTarget.PromoteGenerationAsync(
            context,
            new(
                new("promotion/index-sync/prior"),
                priorGenerationId,
                priorValidated.Generation!.Revision,
                priorValidated.Receipt!.Fingerprint,
                expectedActiveGenerationId: null,
                MaterializationTargetRevision.Initial,
                MaterializationWorkerFence.Initial,
                MaterializationPromotionFence.Initial,
                priorCreatedAtUtc.AddMinutes(3)));
        var priorReceipt = priorPromoted.Receipt!;
        MaterializationBackendGenerationReference priorGeneration = new(
            priorTargetId,
            priorGenerationId,
            plan.Materialization.DefinitionFingerprint);
        MaterializationReadableBackendReference priorRead = new(
            priorGeneration,
            new(
                MaterializationActiveGenerationReference.CurrentSchemaVersion,
                plan.Fingerprint,
                promotedDescriptor.MaterializationId,
                priorTargetId,
                priorGenerationId,
                priorReceipt.TargetRevision,
                priorReceipt.PromotionId,
                priorReceipt.PromotionFence,
                priorReceipt.ValidationFingerprint,
                priorReceipt.PromotedAtUtc));

        MaterializationBackendPoolDefinition poolDefinition = new(
            new("pool/index-sync/read-write"),
            promotedDescriptor.MaterializationId,
            plan.Materialization.DefinitionFingerprint,
            [priorDescriptor, promotedDescriptor],
            defaultTarget: priorTargetId,
            provenance: Provenance("backend-pool"));
        var poolDocument = MaterializationBackendPoolDocument.FromDefinition(poolDefinition);
        var pool = new InMemoryMaterializationTargetPool(
            poolDefinition,
            [priorTarget, promotedTarget.Target]);
        using InMemoryMaterializationBackendRouter router = new(poolDocument, pool);
        MaterializationBackendRoutingFence fence = new("1");
        var initializeConfiguration = RouteConfiguration(
            poolDefinition,
            priorTargetId,
            priorTargetId,
            authority: "tests/index-sync/feature-flags/initial/v1");
        var initialized = await router.SwapAsync(
            context,
            new(
                new(
                    new("route/index-sync/initialize"),
                    poolDefinition.Id,
                    poolDocument.DefinitionFingerprint,
                    MaterializationBackendRoutingRevision.Initial,
                    fence,
                    context.UtcNow),
                priorRead,
                priorGeneration,
                initializeConfiguration));
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, initialized.Disposition);

        var promotion = activation.Activation!.PromotionReceipt!;
        MaterializationBackendGenerationReference promotedGeneration = new(
            promotedDescriptor.Id,
            activation.Generation,
            plan.Materialization.DefinitionFingerprint);
        MaterializationReadableBackendReference promotedRead = new(
            promotedGeneration,
            new(
                MaterializationActiveGenerationReference.CurrentSchemaVersion,
                plan.Fingerprint,
                promotedDescriptor.MaterializationId,
                promotedDescriptor.Id,
                activation.Generation,
                promotion.TargetRevision,
                promotion.PromotionId,
                promotion.PromotionFence,
                promotion.ValidationFingerprint,
                promotion.PromotedAtUtc));

        var routedBeforeAdmission = await router.ResolveReadAsync(context);
        Assert.Same(priorTarget, routedBeforeAdmission.Target);
        Assert.Equal(activation.Generation, (await promotedTarget.Target.InspectAsync(context)).ActiveGenerationId);
        var admitted = await router.AdmitCandidateAsync(
            context,
            new(
                new(
                    new("route/index-sync/admit-elastic"),
                    poolDefinition.Id,
                    poolDocument.DefinitionFingerprint,
                    initialized.Snapshot.Revision,
                    fence,
                    context.UtcNow.AddMilliseconds(1)),
                promotedGeneration));
        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
        var featureFlagConfiguration = RouteConfiguration(
            poolDefinition,
            promotedDescriptor.Id,
            promotedDescriptor.Id,
            authority: "tests/index-sync/feature-flags/elastic/v1");
        var swapped = await router.SwapAsync(
            context,
            new(
                new(
                    new("route/index-sync/swap-elastic"),
                    poolDefinition.Id,
                    poolDocument.DefinitionFingerprint,
                    admitted.Snapshot.Revision,
                    fence,
                    context.UtcNow.AddMilliseconds(2)),
                promotedRead,
                promotedGeneration,
                featureFlagConfiguration));
        var routed = await router.ResolveReadAsync(context);

        Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
        Assert.Same(promotedTarget.Target, routed.Target);
        Assert.Equal(promotedGeneration, routed.Generation);
        Assert.Equal(promotedDescriptor.Id, swapped.Snapshot.Configuration!.ReadTarget);
        Assert.Equal(promotedDescriptor.Id, swapped.Snapshot.Configuration.WriteTarget);
    }

    static MaterializationBackendRoutingConfiguration RouteConfiguration(
        MaterializationBackendPoolDefinition definition,
        MaterializationTargetId read,
        MaterializationTargetId write,
        string authority) => MaterializationBackendRoutingConfigurationResolver.Resolve(
        definition,
        new MaterializationBackendRoutingConfigurationLayer(
            EffectiveConfigurationOrigin.Explicit,
            authority,
            new(read, write)));

    static ElasticElasticsearchRuntimeBinding ReadbackRuntime(
        ElasticClusterId cluster,
        byte[] response)
    {
        InMemoryRequestInvoker invoker = new(
            response,
            statusCode: 200,
            exception: null,
            contentType: "application/json",
            headers: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Elastic-Product"] = ["Elasticsearch"]
            });
        return new(
            cluster,
            new ElasticsearchClient(new ElasticsearchClientSettings(invoker)),
            "tests/index-sync/readback-runtime/v1");
    }

    static byte[] SearchResponse(
        string generationIndex,
        ImmutableArray<ElasticScanHit> hits,
        long total)
    {
        var responseHits = hits.Select(hit =>
        {
            var source = Json(hit.Source);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_index"] = generationIndex,
                ["_id"] = hit.Id,
                ["_source"] = source,
                ["sort"] = new[] { source.GetProperty("value").GetProperty("id").GetString() }
            };
        }).ToArray();
        Dictionary<string, object?> response = new(StringComparer.Ordinal)
        {
            ["took"] = 1,
            ["timed_out"] = false,
            ["_shards"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total"] = 1,
                ["successful"] = 1,
                ["skipped"] = 0,
                ["failed"] = 0
            },
            ["hits"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = total,
                    ["relation"] = "eq"
                },
                ["max_score"] = null,
                ["hits"] = responseHits
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    static JsonElement Json(byte[] source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    static ObservationValue Field(RelationQueryOutputRow row, string name)
    {
        Assert.True(row.Value.TryGetField(FieldPath.FromField(name), out var value));
        return value;
    }

    static ValueTask<SourceFixture> CreateSourceAsync(SourceProvider provider, SemanticFixture semantic) =>
        provider switch
        {
            SourceProvider.Cosmos => ValueTask.FromResult<SourceFixture>(CreateCosmosSource(semantic)),
            SourceProvider.Postgres => CreatePostgresSourceAsync(semantic),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported source provider.")
        };

    static CosmosSourceFixture CreateCosmosSource(SemanticFixture semantic)
    {
        var baseline = new CosmosBaselineFeed([
            new("a", "Alpha"),
            new("b", "Beta"),
            new("c", "Gamma")
        ]);
        var queryFeed = new CosmosJsonQueryFeedReader(
            new Uri("https://tests.invalid"),
            "index-sync",
            "items",
            baseline.Create);
        CosmosRelationQuerySourcePolicy queryPolicy = new(
            CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector,
            crossPartitionPolicy: CosmosRelationQueryCrossPartitionPolicy.Prohibit,
            fixedPartitionKey: new("tenant-a"),
            maximumEnumerationRows: MaximumItems,
            maximumSdkPageSize: 2,
            readConsistencyLevel: ConsistencyLevel.Strong);
        RelationQuerySourceInstance sourceInstance = new(
            new("tests/index-sync/cosmos-source"),
            new("tests/index-sync/cosmos-domain"),
            CosmosRelationQuerySourceReader.TargetProfile,
            queryPolicy.GetEffectivePlacementLimits(CosmosRelationQuerySourceReader.DefaultLimits));
        var reader = new CosmosRelationQuerySourceReader(
            semantic.Root.Shape,
            sourceInstance,
            queryFeed,
            "https://tests.invalid",
            "index-sync",
            "items",
            queryPolicy);
        var fields = semantic.Root.Fields.Select(field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(field.Input.Field.Path)))
            .ToImmutableArray();
        RelationQueryPhysicalPlanFingerprint physicalPlan = new(
            "sha256",
            "tests/index-sync/cosmos-scan/v1",
            new string('b', 64));
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/index-sync/cosmos-root"),
            input: semantic.Root.Input.Id,
            node: semantic.Root.Node,
            binding: semantic.Root.Binding,
            shape: semantic.Root.Shape,
            source: sourceInstance.Id,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(semantic.Root.Shape, reader.IdentitySourceSelector),
            fields);
        var changes = new CosmosChangeFeed
        {
            Current = CosmosChangeFeed.Page([], "cut/start", HttpStatusCode.NotModified)
        };
        CosmosMaterializationSourcePolicy sourcePolicy = new(
            TimeSpan.FromHours(12),
            "tests/index-sync/continuous-backup",
            "tests/index-sync/previous-images",
            "tests/index-sync/strong-consistency",
            maximumScanPageItems: MaximumItems,
            maximumScanPageBytes: ReadBytes,
            maximumChangePageItems: MaximumItems,
            maximumChangePageBytes: ReadBytes,
            maximumProviderPageItems: 2,
            maximumContainerParallelism: 1);
        var source = new CosmosMaterializationSource(
            reader: reader,
            physicalPlan: physicalPlan,
            placement: placement,
            policy: sourcePolicy,
            admissionIndex: new CosmosMaterializationAdmissionIndex(),
            changeFeedReader: changes,
            authenticationKey: AuthenticationKey,
            observer: null);
        RelationQuerySourceReadRequest read = new(
            physicalPlan: physicalPlan,
            stage: new("stage/index-sync/cosmos-root"),
            placementBinding: placement.Id,
            source: sourceInstance.Id,
            shape: semantic.Root.Shape,
            identitySelector: reader.IdentitySourceSelector,
            fields:
            [
                .. fields.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: MaximumItems),
            maximumBufferedRows: MaximumItems);
        return new(source, read, changes, semantic.Root.Shape);
    }

    static async ValueTask<SourceFixture> CreatePostgresSourceAsync(SemanticFixture semantic)
    {
        RelationQuerySourceInstance sourceInstance = new(
            new("tests/index-sync/postgres-source"),
            new("tests/index-sync/postgres-domain"),
            PostgresRelationQuerySourceTargetProfile.Default,
            new(
                maximumBatchSize: MaximumItems,
                maximumBufferedRows: MaximumItems,
                maximumFanOut: MaximumItems,
                maximumConcurrency: 1));
        var sourceFields = semantic.Root.Fields.Select(field => new RelationQuerySourceFieldBinding(
                input: field.Input.Id,
                semanticPath: field.Input.Field.Path,
                sourceSelector: CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(
                    field.Input.Field.Path)))
            .ToImmutableArray();
        var identityPath = FieldPath.FromField("id");
        var placement = new RelationQuerySourcePlacementBinding(
            id: semantic.HydrationPhysicalPlan.Placement.Bindings.Single().Id,
            input: semantic.Root.Input.Id,
            node: semantic.Root.Node,
            binding: semantic.Root.Binding,
            shape: semantic.Root.Shape,
            source: sourceInstance.Id,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(semantic.Root.Shape, "item_id", identityPath),
            fields: sourceFields);
        RelationQuerySourcePlacement sourcePlacement = new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(semantic.Plan),
            conventionSetVersion: "tests/index-sync/postgres-scan-placement/v1",
            sourceInstances: [sourceInstance],
            bindings: [placement]);
        var physicalPlan = CreatePostgresScanPhysicalPlan(semantic, sourcePlacement, placement);
        var ordering = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            new("ck_index_sync_id_ascii", "tests/index-sync/postgres-ordering/v1"));
        var tableFields = semantic.Root.Fields.Select(field => new PostgresRelationQueryFieldBinding(
                input: field.Input.Id,
                semanticPath: field.Input.Field.Path,
                columnName: field.Input.Field.Path.Matches("id") ? "item_id" : "item_name",
                scalarType: PostgresRelationQueryScalarType.Text,
                missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                textSemantics: field.Input.Field.Path.Matches("id") ? ordering : null,
                ordering: field.Input.Field.Path.Matches("id")
                    ? PostgresRelationQueryOrderingCapability.Exact
                        | PostgresRelationQueryOrderingCapability.StableUnique
                    : PostgresRelationQueryOrderingCapability.None))
            .ToImmutableArray();
        PostgresRelationQueryTableBinding tableBinding = new(
            source: sourceInstance.Id,
            placementBinding: placement.Id,
            input: placement.Input,
            shape: placement.Shape,
            schemaName: "public",
            tableName: "index_sync_items",
            identity: new(
                semanticPath: identityPath,
                columnName: "item_id",
                scalarType: PostgresRelationQueryScalarType.Text,
                textSemantics: ordering),
            fields: tableFields);
        PostgresRelationQueryStorageBinding storage = new(
            id: new("tests/index-sync/postgres-binding/v1"),
            database: new("tests-index-sync"),
            target: PostgresRelationQueryTargetProfile.Target,
            targetProfile: PostgresRelationQueryTargetProfile.ProfileId,
            tables: [tableBinding],
            compiledPlanFingerprint: RelationQueryCompiledPlanReferenceFingerprinter.Compute(sourcePlacement.Plan),
            placementFingerprint: sourcePlacement.Fingerprint);
        var tableExecutor = new PostgresTableExecutor([
            new("a", "Alpha"),
            new("b", "Beta"),
            new("c", "Gamma")
        ]);
        var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=tests-index-sync;Username=postgres;Password=not-used;Timeout=1");
        var runtime = new PostgresNpgsqlRuntimeBinding(
            storage.Database,
            dataSource,
            "tests/index-sync/postgres-runtime/v1");
        var providerReader = new PostgresRelationQuerySourceReader(
            semantic.Plan,
            physicalPlan,
            sourceInstance.Id,
            storage,
            dataSource,
            runtime,
            new(
                maximumBatchKeys: MaximumItems,
                maximumRowsPerRead: MaximumItems,
                maximumPageItems: MaximumItems,
                maximumPageBytes: ReadBytes))
            .WithCommandExecutor(tableExecutor.ExecuteAsync);
        var replicaIdentity = new PostgresLogicalReplicationReplicaIdentityBinding(
            PostgresLogicalReplicationReplicaIdentityKind.Full);
        var replicationBinding = new PostgresLogicalReplicationBinding(
            publicationName: "cohesive_index_sync",
            slotName: "cohesive_index_sync_slot",
            slotGeneration: "generation-1",
            expectedReplicaIdentity: replicaIdentity,
            beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required);
        var protocol = new PostgresLogicalProtocol(Deployment(tableBinding, replicationBinding));
        var source = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
            reader: providerReader,
            placement: placement,
            runtimeBinding: runtime,
            binding: replicationBinding,
            protocol: protocol,
            positionAuthenticationKey: AuthenticationKey,
            policy: new(
                maximumTransactionChanges: MaximumItems,
                maximumTransactionBytes: ReadBytes,
                maximumTransactionsPerRead: 4,
                maximumReconnectAttempts: 0),
            observer: null,
            cancellationToken: default);
        var stage = physicalPlan.Stages.Single(candidate =>
            candidate.PlacementBinding == placement.Id
            && candidate.Kind == RelationQueryPhysicalStageKind.SourceRead);
        RelationQuerySourceReadRequest read = new(
            physicalPlan: physicalPlan.Fingerprint,
            stage: stage.Id,
            placementBinding: placement.Id,
            source: sourceInstance.Id,
            shape: semantic.Root.Shape,
            identitySelector: placement.Identity!.SourceSelector,
            fields:
            [
                .. placement.Fields
                    .Where(field => stage.RequestedFields.Contains(field.Input))
                    .Select(static field => new RelationQuerySourceReadField(
                        field.Input,
                        field.SemanticPath,
                        field.SourceSelector,
                        RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: MaximumItems),
            maximumBufferedRows: MaximumItems);
        return new PostgresSourceFixture(source, read, protocol, tableBinding, dataSource);
    }

    static CompiledRelationQueryPhysicalPlan CreatePostgresScanPhysicalPlan(
        SemanticFixture semantic,
        RelationQuerySourcePlacement placement,
        RelationQuerySourcePlacementBinding rootPlacement)
    {
        var suppliedRoot = semantic.HydrationPhysicalPlan.Stages.Single(stage =>
            stage.Kind == RelationQueryPhysicalStageKind.SuppliedInput
            && stage.PlacementBinding == rootPlacement.Id);
        var stages = semantic.HydrationPhysicalPlan.Stages.Select(stage =>
        {
            var provenance = new RelationQueryPhysicalStageProvenance(
                nodes: stage.Provenance.Nodes,
                inputs: stage.Provenance.Inputs,
                requirements: stage.Provenance.Requirements,
                capabilityEvidence: [],
                compositionRules: stage.Provenance.CompositionRules,
                operatingBoundaries: stage.Provenance.OperatingBoundaries,
                placementBindings: stage.Provenance.PlacementBindings,
                loweringRule: stage.Provenance.LoweringRule,
                policyDecisions: stage.Provenance.PolicyDecisions);
            return new RelationQueryPhysicalStage(
                id: stage.Id,
                kind: stage.Id == suppliedRoot.Id
                    ? RelationQueryPhysicalStageKind.SourceRead
                    : stage.Kind,
                dependencies: stage.Dependencies,
                placementBinding: stage.PlacementBinding,
                semanticInputs: stage.SemanticInputs,
                requestedFields: stage.Id == suppliedRoot.Id
                    ? [.. semantic.Root.Fields.Select(static field => field.Input.Id)]
                    : stage.RequestedFields,
                batchSize: stage.BatchSize,
                provenance: provenance);
        }).ToImmutableArray();
        return new(
            CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(semantic.Plan),
            semantic.Realization.Fingerprint,
            placement,
            semantic.HydrationPhysicalPlan.Policy,
            stages,
            semantic.HydrationPhysicalPlan.Terminal);
    }

    static PostgresLogicalReplicationDeployment Deployment(
        PostgresRelationQueryTableBinding table,
        PostgresLogicalReplicationBinding binding)
    {
        var columns = table.Fields
            .Select(static field => (field.ColumnName, field.ScalarType))
            .Append((table.Identity!.ColumnName, table.Identity.ScalarType))
            .GroupBy(static column => column.ColumnName, StringComparer.Ordinal)
            .Select(static group => new PostgresLogicalReplicationColumn(
                Name: group.Key,
                DataTypeId: PostgresTypeId(group.Select(static column => column.ScalarType).Distinct().Single()),
                TypeModifier: -1,
                IsReplicaIdentity: true))
            .ToImmutableArray();
        return new(
            SystemIdentifier: "postgres-index-sync-system",
            Timeline: 1,
            DatabaseName: "tests-index-sync",
            PublicationName: binding.PublicationName,
            PublishesInserts: true,
            PublishesUpdates: true,
            PublishesDeletes: true,
            PublishesTruncates: false,
            PublishesViaPartitionRoot: false,
            IncludesTable: true,
            HasRowFilter: false,
            IncludesAllTableColumns: true,
            SchemaName: table.SchemaName,
            TableName: table.TableName,
            ReplicaIdentity: binding.ExpectedReplicaIdentity,
            Columns: columns,
            SlotName: binding.SlotName,
            OutputPlugin: "pgoutput",
            IsLogicalSlot: true,
            IsTemporarySlot: false,
            IsTwoPhaseSlot: false,
            IsActive: false,
            RestartPosition: new(50),
            ConfirmedFlushPosition: new(100),
            CurrentWalPosition: new(200),
            WalState: PostgresLogicalReplicationWalState.Reserved,
            SafeWalBytes: ReadBytes,
            InactiveSinceUtc: null,
            InvalidationReason: null);
    }

    static uint PostgresTypeId(PostgresRelationQueryScalarType scalarType) => scalarType switch
    {
        PostgresRelationQueryScalarType.Text => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported test scalar.")
    };

    static MaterializationRebuildAttempt Attempt(string suffix, DateTimeOffset startedAtUtc) => new(
        continuation: new(
            processInstanceId: new("process/index-sync/vertical-slice"),
            processAttemptId: new($"process/index-sync/{suffix}")),
        startedAtUtc);

    static ExecutionProvenance Provenance(string source) => new(
        producer: new("cohesive-tests", "1"),
        source: new($"tests/index-sync/{source}"),
        origin: DocumentOrigin.Generated);

    public enum SourceProvider
    {
        Cosmos = 0,
        Postgres = 1
    }

    sealed class CanonicalInput
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class CanonicalOutput
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class CanonicalCount
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed record SemanticFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        CompiledRelationQueryPhysicalPlan HydrationPhysicalPlan,
        RelationQuerySourceInputContract Root,
        RelationQueryOutputReference Output,
        MaterializationDefinition Definition,
        ReadbackFixture Readback);

    sealed record ReadbackFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        RelationQuerySourcePlacement Placement,
        ElasticRelationQueryStorageBinding StorageBinding,
        ImmutableArray<ElasticRelationQueryCompiledArtifact> Artifacts);

    sealed record TargetFixture(
        ElasticMaterializationTargetBinding Binding,
        FakeElasticMaterializationTransport Transport,
        ControlFeedbackObserver Observer,
        ElasticMaterializationTarget Target);

    sealed record ExecutionFixture(
        MaterializationRebuildPlan Plan,
        MaterializationRebuildShardPlan Shard,
        ResolvedMaterializationRebuildPlan Resolved,
        MaterializationIndexSyncControlRuntimeProvider ControlProvider,
        MutableTimeProvider Clock);

    abstract class SourceFixture(
        IMaterializationPullChangeSource source,
        RelationQuerySourceReadRequest read,
        MaterializationSourceScope scope) : IAsyncDisposable
    {
        internal IMaterializationPullChangeSource Source { get; } = source;

        internal RelationQuerySourceReadRequest Read { get; } = read;

        internal MaterializationSourceScope Scope { get; } = scope;

        internal abstract void PublishUpdateAndDelete();

        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class CosmosSourceFixture(
        CosmosMaterializationSource source,
        RelationQuerySourceReadRequest read,
        CosmosChangeFeed changes,
        QualifiedShapeId shape) : SourceFixture(source, read, source.Scope)
    {
        internal override void PublishUpdateAndDelete()
        {
            var beforeA = CosmosDocument("a", "Alpha", version: 1, shape);
            var afterA = CosmosDocument("a", "Alpha Updated", version: 2, shape);
            var beforeB = CosmosDocument("b", "Beta", version: 1, shape);
            changes.Pages["cut/start"] = CosmosChangeFeed.Page(
                [
                    CosmosChange(
                        CosmosMaterializationProviderChangeKind.Replace,
                        current: afterA,
                        previous: beforeA,
                        lsn: 201),
                    CosmosChange(
                        CosmosMaterializationProviderChangeKind.Delete,
                        current: null,
                        previous: beforeB,
                        lsn: 202)
                ],
                "cut/end",
                HttpStatusCode.OK);
            changes.Pages["cut/end"] = CosmosChangeFeed.Page([], "cut/end", HttpStatusCode.NotModified);
        }

        static CosmosObservationContainerDocument CosmosDocument(
            string identity,
            string name,
            long version,
            QualifiedShapeId shape) => new(
            Id: $"entity/{identity}",
            PartitionKey: "tenant-a",
            DocumentKind: CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
            ObservationType: shape.ShapeId.Value,
            ObservationId: identity,
            ObservationVersion: version,
            Observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["id"] = ObservationValue.FromString(identity),
                ["name"] = ObservationValue.FromString(name)
            });

        static CosmosMaterializationProviderChange CosmosChange(
            CosmosMaterializationProviderChangeKind operation,
            CosmosObservationContainerDocument? current,
            CosmosObservationContainerDocument? previous,
            long lsn) => new(
            Current: current,
            Previous: previous,
            Lsn: lsn,
            PreviousLsn: lsn - 1,
            OperationType: operation,
            ConflictResolutionTimestamp: StartedAtUtc.UtcDateTime,
            IsTimeToLiveExpired: false,
            DeletedItemId: previous?.Id);
    }

    sealed class PostgresSourceFixture(
        PostgresLogicalReplicationMaterializationChangeSource source,
        RelationQuerySourceReadRequest read,
        PostgresLogicalProtocol protocol,
        PostgresRelationQueryTableBinding table,
        NpgsqlDataSource dataSource) : SourceFixture(source, read, source.Scope)
    {
        internal override void PublishUpdateAndDelete()
        {
            protocol.Deployment = protocol.Deployment with { CurrentWalPosition = new(300) };
            protocol.Batch = new(
                Transactions:
                [
                    Transaction(
                        transactionId: 41,
                        endPosition: 250,
                        new PostgresLogicalReplicationMutation(
                            Ordinal: 0,
                            Kind: PostgresLogicalReplicationMutationKind.Update,
                            ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                            OldRow: Row("a", "Alpha"),
                            NewRow: Row("a", "Alpha Updated"))),
                    Transaction(
                        transactionId: 42,
                        endPosition: 300,
                        new PostgresLogicalReplicationMutation(
                            Ordinal: 0,
                            Kind: PostgresLogicalReplicationMutationKind.Delete,
                            ReplicaIdentity: PostgresLogicalReplicationReplicaIdentityKind.Full,
                            OldRow: Row("b", "Beta"),
                            NewRow: null))
                ],
                ScannedThrough: new(300),
                ReachedUpperBoundary: true);
        }

        PostgresLogicalReplicationRow Row(string id, string name) => new([
            new(
                table.Identity!.ColumnName,
                PostgresLogicalReplicationCellKind.Value,
                id,
                EncodedBytes: id.Length),
            new(
                table.Fields.Single(field => field.SemanticPath == FieldPath.FromField("name")).ColumnName,
                PostgresLogicalReplicationCellKind.Value,
                name,
                EncodedBytes: name.Length)
        ]);

        static PostgresLogicalReplicationTransaction Transaction(
            uint transactionId,
            ulong endPosition,
            params PostgresLogicalReplicationMutation[] mutations) => new(
            transactionId,
            FinalPosition: new(endPosition - 1),
            CommitPosition: new(endPosition - 1),
            EndPosition: new(endPosition),
            CommittedAtUtc: StartedAtUtc.AddSeconds(1),
            Mutations: [.. mutations],
            RetainedBytes: mutations.Sum(static mutation =>
                (mutation.OldRow?.Cells.Sum(static cell => cell.EncodedBytes) ?? 0)
                + (mutation.NewRow?.Cells.Sum(static cell => cell.EncodedBytes) ?? 0)));

        public override ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    sealed record TestRow(string Id, string Name)
    {
        internal object Get(string columnName) => columnName switch
        {
            "item_id" => Id,
            "item_name" => Name,
            _ => throw new InvalidOperationException($"Unknown PostgreSQL test column '{columnName}'.")
        };
    }

    sealed class PostgresTableExecutor
    {
        static readonly Regex Projection = new(
            "\\\"source\\\"\\.\\\"(?<column>[^\\\"]+)\\\" AS \\\"(?<alias>_[^\\\"]+)\\\"",
            RegexOptions.CultureInvariant);
        static readonly Regex Limit = new(" LIMIT (?<limit>[0-9]+)$", RegexOptions.CultureInvariant);
        readonly ImmutableArray<TestRow> rows;

        internal PostgresTableExecutor(ImmutableArray<TestRow> rows) =>
            this.rows = [.. rows.OrderBy(static row => row.Id, StringComparer.Ordinal)];

        internal ValueTask<PostgresNpgsqlCommandResult> ExecuteAsync(
            PostgresNpgsqlCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<TestRow> selected = rows;
            var after = command.Parameters.FirstOrDefault(static parameter => !parameter.IsArray).Value as string;
            if (after is not null)
                selected = selected.Where(row => StringComparer.Ordinal.Compare(row.Id, after) > 0);
            var maximum = int.Parse(
                Limit.Match(command.Text).Groups["limit"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            var columns = Projection.Matches(command.Text)
                .Select(match => match.Groups["column"].Value)
                .ToArray();
            var result = ImmutableArray.CreateBuilder<ImmutableArray<object?>>();
            foreach (var row in selected.Take(maximum))
            {
                var values = ImmutableArray.CreateBuilder<object?>(columns.Length);
                foreach (var column in columns)
                    values.Add(row.Get(column));
                result.Add(values.MoveToImmutable());
            }
            return ValueTask.FromResult(new PostgresNpgsqlCommandResult(result.ToImmutable()));
        }
    }

    sealed class PostgresLogicalProtocol(
        PostgresLogicalReplicationDeployment deployment) : IPostgresLogicalReplicationProtocol
    {
        internal PostgresLogicalReplicationDeployment Deployment { get; set; } = deployment;

        internal PostgresLogicalReplicationReadBatch Batch { get; set; } = new(
            [],
            deployment.CurrentWalPosition,
            ReachedUpperBoundary: true);

        public ValueTask<PostgresLogicalReplicationDeployment> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Deployment);
        }

        public ValueTask<PostgresLogicalReplicationReadBatch> ReadAsync(
            PostgresLogicalReplicationWalPosition afterPosition,
            PostgresLogicalReplicationWalPosition upperBoundary,
            int maximumTransactions,
            int preferredMaximumMutations,
            long preferredMaximumBytes,
            int maximumTransactionMutations,
            long maximumTransactionBytes,
            TimeSpan inactivityTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Batch);
        }

        public ValueTask<PostgresLogicalReplicationFeedback> SettleAsync(
            PostgresLogicalReplicationWalPosition position,
            TimeSpan confirmationTimeout,
            TimeSpan confirmationPollInterval,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prior = Deployment.ConfirmedFlushPosition;
            Deployment = Deployment with { ConfirmedFlushPosition = position };
            return ValueTask.FromResult(new PostgresLogicalReplicationFeedback(
                prior >= position
                    ? PostgresLogicalReplicationFeedbackDisposition.AlreadyConfirmed
                    : PostgresLogicalReplicationFeedbackDisposition.Confirmed,
                prior,
                position));
        }

        public ValueTask<IPostgresLogicalReplicationSnapshotExport> CreateSnapshotExportAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The generic vertical slice uses bounded keyset enumeration.");
    }

    sealed record CosmosProviderPage(
        ImmutableArray<JsonElement> Rows,
        string? ContinuationToken,
        HttpStatusCode StatusCode = HttpStatusCode.OK);

    sealed class CosmosBaselineFeed(ImmutableArray<TestRow> rows)
    {
        internal FeedIterator<JsonElement> Create(
            FeedRange? feedRange,
            Microsoft.Azure.Cosmos.QueryDefinition query,
            string? continuationToken,
            QueryRequestOptions options)
        {
            Assert.Null(feedRange);
            Assert.NotNull(query);
            Assert.NotNull(options.PartitionKey);
            var offset = continuationToken is null
                ? 0
                : int.Parse(continuationToken, System.Globalization.CultureInfo.InvariantCulture);
            var selected = rows.Skip(offset).Take(2).Select((row, _) =>
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["_identity"] = row.Id,
                    ["_field0"] = row.Id,
                    ["_field1"] = row.Name
                }));
                return document.RootElement.Clone();
            }).ToImmutableArray();
            var nextOffset = offset + selected.Length;
            var page = new CosmosProviderPage(
                selected,
                nextOffset < rows.Length
                    ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null);
            return new CosmosFeedIterator(page);
        }
    }

    sealed class CosmosFeedIterator(CosmosProviderPage page) : FeedIterator<JsonElement>
    {
        bool read;

        public override bool HasMoreResults => !read || page.ContinuationToken is not null;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
                throw new InvalidOperationException("The deterministic Cosmos page was already read.");
            read = true;
            return Task.FromResult<FeedResponse<JsonElement>>(new CosmosFeedResponse(page));
        }
    }

    sealed class CosmosFeedResponse(CosmosProviderPage page) : FeedResponse<JsonElement>
    {
        public override string ContinuationToken => page.ContinuationToken!;
        public override int Count => page.Rows.Length;
        public override string IndexMetrics => string.Empty;
        public override string QueryAdvice => string.Empty;
        public override Headers Headers { get; } = new();
        public override IEnumerable<JsonElement> Resource => page.Rows;
        public override HttpStatusCode StatusCode => page.StatusCode;
        public override CosmosDiagnostics Diagnostics => null!;
        public override double RequestCharge => 1;
        public override string ActivityId => "tests/index-sync/cosmos-baseline";
        public override string ETag => string.Empty;
        public override IEnumerator<JsonElement> GetEnumerator() =>
            ((IEnumerable<JsonElement>)page.Rows).GetEnumerator();
    }

    sealed class CosmosChangeFeed : ICosmosMaterializationChangeFeedReader
    {
        internal CosmosMaterializationProviderChangePage Current { get; set; } = null!;

        internal Dictionary<string, CosmosMaterializationProviderChangePage> Pages { get; } =
            new(StringComparer.Ordinal);

        internal static CosmosMaterializationProviderChangePage Page(
            ImmutableArray<CosmosMaterializationProviderChange> changes,
            string continuation,
            HttpStatusCode statusCode) => new(
            changes,
            continuation,
            statusCode,
            requestCharge: 1,
            providerEvidenceReference: "tests/index-sync/cosmos-change-feed");

        public ValueTask<CosmosMaterializationProviderChangePage> ReadPageAsync(
            CosmosMaterializationChangeFeedStart start,
            FeedRange? feedRange,
            int pageSizeHint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(start.Kind == CosmosMaterializationChangeFeedStartKind.Now
                ? Current
                : Pages[start.ContinuationToken!]);
        }
    }

    sealed class ControlFeedbackObserver : IElasticMaterializationTargetObserver
    {
        MaterializationIndexSyncControlRuntimeProvider? provider;
        MutableTimeProvider? clock;
        long observationOrdinal;

        internal void Bind(
            MaterializationIndexSyncControlRuntimeProvider controlProvider,
            MutableTimeProvider timeProvider)
        {
            provider = controlProvider ?? throw new ArgumentNullException(nameof(controlProvider));
            clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public void Observe(ElasticMaterializationTargetObservation observation)
        {
            if (provider is null || clock is null
                || observation.ControlEvidenceKind != ElasticMaterializationTargetControlEvidenceKind.PressureSample)
            {
                return;
            }
            var rejection = observation.Measurements.Single(measurement =>
                measurement.Metric == ControlMetricKind.RejectionRatio);
            if (rejection.Availability != ControlMeasurementAvailability.Available)
                return;
            var runtime = provider.ForGeneration(observation.GenerationId);
            var observedAtUtc = observation.CompletedAtUtc.AddMilliseconds(1);
            clock.AdvanceTo(observedAtUtc);
            var context = OperationContext.Create(timeProvider: clock);
            var snapshot = Assert.Single(runtime.GetSnapshotsAsync(context).AsTask().GetAwaiter().GetResult());
            ControlObservation controlObservation = new(
                ControlLoopDefinition.CurrentSchemaVersion,
                new($"observation/elastic-rejection/{Interlocked.Increment(ref observationOrdinal)}"),
                snapshot.State.LoopId,
                snapshot.State.DefinitionFingerprint,
                snapshot.State.Target,
                snapshot.State.Epoch,
                snapshot.State.Revision,
                observation.StartedAtUtc,
                observation.CompletedAtUtc,
                observedAtUtc,
                "tests/index-sync/elastic-observer-bridge/v1",
                [rejection]);
            _ = runtime.ObserveAsync(
                    context,
                    MaterializationIndexSyncWorkloadKind.Rebuild,
                    controlObservation)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            clock.AdvanceTo(observedAtUtc.AddMilliseconds(1));
        }
    }

    sealed class ThrowAfterFirstCheckpoint : IMaterializationRebuildCrashInjector
    {
        bool thrown;

        public ValueTask ObserveAsync(
            OperationContext context,
            MaterializationRebuildCrashObservation observation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(observation);
            context.ThrowIfCancellationRequested();
            if (!thrown && observation.Point == MaterializationRebuildCrashPoint.AfterCheckpoint)
            {
                thrown = true;
                throw new InjectedRebuildInterruption();
            }
            return ValueTask.CompletedTask;
        }
    }

    sealed class InjectedRebuildInterruption : Exception;

    sealed class ExactRebuildExecutionResolver : IMaterializationRebuildExecutionResolver
    {
        readonly Dictionary<ProcessContinuationIdentity, MaterializationRebuildExecution> executions = [];

        internal ExactRebuildExecutionResolver(params MaterializationRebuildExecution[] executions)
        {
            foreach (var execution in executions)
                Add(execution);
        }

        internal void Add(MaterializationRebuildExecution execution) =>
            executions.Add(execution.Attempt.Continuation, execution);

        public bool TryResolve(
            MaterializationRebuildPlanFingerprint plan,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? resolved)
        {
            if (executions.TryGetValue(continuation, out var execution)
                && plan == execution.PlanFingerprint)
            {
                resolved = execution;
                return true;
            }

            resolved = null;
            return false;
        }
    }

    sealed class RejectingProcessReferenceHost : IProcessReferenceHost
    {
        internal static RejectingProcessReferenceHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        readonly object gate = new();
        DateTimeOffset utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
                return utcNow;
        }

        internal void AdvanceTo(DateTimeOffset value)
        {
            lock (gate)
            {
                if (value > utcNow)
                    utcNow = value;
            }
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
