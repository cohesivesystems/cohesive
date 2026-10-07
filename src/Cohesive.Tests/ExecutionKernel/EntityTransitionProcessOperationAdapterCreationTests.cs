using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EntityTransitionProcessOperationAdapterCreationTests
{
    [Fact]
    public async Task NativeBindingAttestsReceiptOnlyForMatchingAtomicRepository()
    {
        var fixture = await Fixture.CreateAsync();
        var binding = new ProcessTransitionOperationBinding(fixture.Plan, fixture.Repository, fixture.InteractionCatalog);
        var link = binding.CreateProcessDefinitionLink();
        Assert.Equal(fixture.Plan.DefinitionReference, link.Definition);
        Assert.Equal(fixture.Plan.Definition.Input, link.Input);
        Assert.Equal(fixture.Plan.Definition.Outcome, link.Result);
        Assert.Equal(EntityTransitionReceiptReferences.ValueContract, link.ReceiptContract);
        Assert.Throws<InvalidOperationException>(() => new ProcessTransitionOperationBinding(fixture.Plan,
            new NonAtomicRepository(fixture.Repository), fixture.InteractionCatalog).CreateProcessDefinitionLink());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessEnrichmentUsesAuthorizedOriginalReceiptAfterLaterWrite(bool allowDisclosure)
    {
        var fixture = await Fixture.CreateAsync();
        var query = HostedQuery<EntityTransitionOperationReference, string>.Create(
            new("query/committed-customer"), new("1"), new("tests.committed-customer", "1"), "v1",
            Fixture.Provenance(), evaluationSemantics: HostedQueryEvaluationSemantics.Observation);
        var receipt = new ProcessOutputBinding(new("receipt"), EntityTransitionReceiptReferences.ValueContract);
        var output = new ProcessOutputBinding(new("response"), query.ResultContract);
        var document = ProcessDefinitionDocuments.Create(new("process/enriched-customer"), new("1"),
            new(fixture.StateContract, query.ResultContract, new("commit"), [
                new InvokeTransitionProcessNode(new("commit"), fixture.Plan.DefinitionReference,
                    Expr.Field(ProcessBindingIds.Input, nameof(CustomerEntity.Id)), Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("commit/enrich"), new("enrich"))), receipt),
                new EvaluateRelationProcessNode(new("enrich"), query.Reference, Expr.BoundValue(receipt.Binding),
                    new(new(new("enrich/return"), new("return")), output)),
                new ReturnProcessNode(new("return"), Expr.BoundValue(output.Binding))
            ], ProcessRecoveryPolicy.ContinueAttempt), Fixture.Provenance());
        var binding = new ProcessTransitionOperationBinding(fixture.Plan, fixture.Repository, fixture.InteractionCatalog);
        var compilation = ProcessStaticCompiler.Compile(document, new(definitions:
            [binding.CreateProcessDefinitionLink(), query.CreateProcessDefinitionLink()], interactionContracts: fixture.InteractionCatalog));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var plan = compilation.Plan!;
        EntitySnapshot? committed = null;
        var authorizations = 0;
        var handlers = new ProcessRelationHandlerCatalog([
            ProcessRelationHandlerRegistration.CreateOutcome(query, async (context, evaluation, reference) =>
            {
                var resolved = await EntityTransitionReceiptReferences.ResolveSnapshotAsync(fixture.Repository,
                    context, reference, fixture.Plan.DefinitionReference, evaluation.Context.AuthorityScope,
                    evaluation.Continuation, "tenant/acme", (_, snapshot) =>
                    {
                        authorizations++;
                        Assert.Equal(committed, snapshot);
                        return ValueTask.FromResult(allowDisclosure);
                    });
                return resolved.Type == Cohesive.Prelude.ResultType.Failure
                    ? ProcessRelationHandlerOutcome<string>.Failed(resolved.Failure!)
                    : ProcessRelationHandlerOutcome<string>.Completed(
                        resolved.Success!.Entity.Observation.GetField(nameof(CustomerEntity.Status)).GetRequiredString()
                        + ":" + resolved.Success.ConcurrencyToken.Value);
            })]);
        var writes = 0;
        var host = new RegisteredAsyncProcessReferenceHost(handlers, async (context, invocation) =>
        {
            writes++;
            var result = await fixture.Adapter.ExecuteAsync(context, invocation);
            Assert.True(result.IsSuccessful);
            var retained = await fixture.Repository.ResolveTransitionOperation(context,
                EntityTransitionReceiptReferences.Read(result.ReceiptReference!));
            committed = retained.Receipt!.Entity;
            // Another writer wins before enrichment. A current-state read would return the wrong response.
            var later = CustomerEntity.Instance.CreateState(fixture.SubjectId,
                new CustomerState(fixture.SubjectId, "tenant/acme", "later"), committed.Entity.Version + 1);
            await fixture.Repository.Upsert(context, new(later.Snapshot, committed.ConcurrencyToken));
            return result;
        });
        var state = ProcessReferenceInterpreter.Create(plan, fixture.Invocation.Continuation, fixture.Invocation.Input);
        var activation = new ProcessActivation(new("activation/enriched-customer"), ProcessActivationCause.Start,
            fixture.Invocation.ObservedAtUtc, fixture.Invocation.Context);
        var decision = await ProcessReferenceInterpreter.ActivateAsync(fixture.Context, plan, state, activation, host);
        Assert.Equal(1, writes);
        Assert.Equal(1, authorizations);
        Assert.Single(decision.Emissions); // The committed interaction is retained even when disclosure is denied.
        var current = await fixture.Repository.TryGet(fixture.Context, fixture.SubjectId, EntityReadOptions.Full);
        Assert.Equal("later", current!.Entity.Observation.GetField(nameof(CustomerEntity.Status)).GetRequiredString());
        Assert.NotEqual(committed!.ConcurrencyToken, current.ConcurrencyToken);
        if (allowDisclosure)
        {
            Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
            Assert.Equal("pending:" + committed.ConcurrencyToken.Value,
                decision.State.Terminal.Detail!.Value!.Value!.Value.GetString());
        }
        else
        {
            Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
            Assert.Contains(decision.Diagnostics, diagnostic =>
                diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ReceiptResourceDenied);
        }
    }

    sealed class NonAtomicRepository(IEntityRepository inner) : IEntityRepository
    {
        public Cohesive.Transitions.Model.EntityDefinition EntityDefinition => inner.EntityDefinition;
        public string? IdentityField => inner.IdentityField;
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
            throw new InvalidOperationException("Attestation must not read.");
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
            throw new InvalidOperationException("Attestation must not write.");
    }

    [CosmosEntityTransitionCreationFact]
    public async Task CosmosRepository_CreationIndexReplaysOriginalReceiptForReplacementAttempt()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOS_ENTITY_TRANSITION_OPERATION_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The Cosmos connection string disappeared after discovery.");
        var fixture = await Fixture.CreateAsync();
        var databaseId = $"cohesive-entity-transition-creation-tests-{Guid.NewGuid():N}";
        using var client = new CosmosClient(connectionString, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            Serializer = new CosmosSystemTextJsonSerializer(),
            HttpClientFactory = static () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
        });
        var database = (await client.CreateDatabaseAsync(databaseId)).Database;
        try
        {
            var container = (await database.CreateContainerAsync(
                new ContainerProperties("entities", "/partitionKey"))).Container;
            var partitionKeyPolicy = new EntityPartitionKeyPolicy(
                "customer tenant",
                static (_, entity) => entity.Observation.GetField(nameof(CustomerEntity.Tenant)).GetRequiredString(),
                static (_, _) => "tenant/acme");
            var repository = new CosmosEntityOutboxRepository(
                fixture.Repository.EntityDefinition,
                container,
                partitionKeyPolicy: partitionKeyPolicy);
            var adapter = new EntityTransitionProcessOperationAdapter(invocation =>
                invocation.Definition == fixture.Plan.DefinitionReference
                    ? new(fixture.Plan, repository, fixture.InteractionCatalog)
                    : null);

            var committed = await adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
            var replacement = fixture.ReplacementInvocation();
            var replayed = await adapter.ExecuteAsync(fixture.Context, replacement);
            var creationReplay = await repository.TryGetCreationTransitionOperation(
                fixture.Context,
                fixture.RequestFor(replacement));
            var current = await repository.TryGet(
                fixture.Context,
                fixture.SubjectId,
                EntityReadOptions.Full.WithPartitionKey("tenant/acme"));

            Assert.True(committed.IsSuccessful);
            Assert.Equal(
                committed.Emissions.Select(static envelope => InteractionEnvelopeJsonSerializer.Serialize(envelope)),
                replayed.Emissions.Select(static envelope => InteractionEnvelopeJsonSerializer.Serialize(envelope)));
            Assert.Equal(EntityTransitionOperationDisposition.Replayed, creationReplay.Disposition);
            Assert.Equal(fixture.Request.Operation, creationReplay.Receipt?.Request.Operation);
            Assert.Equal(0, current?.Entity.Version);
            Assert.Equal(creationReplay.Receipt?.Entity.ConcurrencyToken, current?.ConcurrencyToken);
        }
        finally
        {
            await database.DeleteAsync();
        }
    }

    [Fact]
    public async Task CreateOnAbsent_CommitsInitialStateReceiptAndEmission_ThenExactRetryReplays()
    {
        var fixture = await Fixture.CreateAsync();

        var committed = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var firstSnapshot = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);
        var replayed = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var replayedSnapshot = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        Assert.True(committed.IsSuccessful);
        Assert.Equal("created", committed.Value?.Value?.GetString());
        Assert.Single(committed.Emissions);
        Assert.Equal(committed, replayed);
        Assert.NotNull(firstSnapshot);
        Assert.Equal(firstSnapshot, replayedSnapshot);
        Assert.Equal(0, firstSnapshot.Entity.Version);
        Assert.Equal("tenant/acme", firstSnapshot.Entity.Observation.GetField(nameof(CustomerEntity.Tenant)).GetString());
        Assert.Equal("pending", firstSnapshot.Entity.Observation.GetField(nameof(CustomerEntity.Status)).GetString());

        var retained = await fixture.Repository.TryGetTransitionOperation(
            fixture.Context,
            fixture.Request);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, retained.Disposition);
        Assert.Equal(EntityTransitionSubjectCondition.MustBeAbsent, retained.Receipt!.Commit.SubjectCondition);
        Assert.Equal(committed.Value, retained.Receipt.Result.Value);
        Assert.Equal(committed.Emissions, retained.Receipt.Result.Emissions);
        Assert.Null(retained.Receipt.Result.ReceiptReference);
        Assert.Equal(retained.Receipt.Request.Reference, EntityTransitionReceiptReferences.Read(committed.ReceiptReference!));
        Assert.Empty(fixture.Repository.OutboxEnvelopes);
    }

    [Fact]
    public async Task CreateOnPresent_FromDifferentOccurrenceWithExactIntent_ReplaysOriginalReceiptAndEmission()
    {
        var fixture = await Fixture.CreateAsync();
        var replacement = fixture.ReplacementInvocation();

        var committed = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var replayed = await fixture.Adapter.ExecuteAsync(fixture.Context, replacement);

        Assert.Equal(committed, replayed);
        Assert.Single(replayed.Emissions);
        var origin = Assert.IsType<ProcessInteractionOrigin>(Assert.Single(replayed.Emissions).Context.Origin);
        Assert.Equal(fixture.Invocation.Continuation, origin.Continuation);
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(
                fixture.Context,
                fixture.RequestFor(replacement))).Disposition);
        var semanticReplay = await fixture.Repository.TryGetCreationTransitionOperation(
            fixture.Context,
            fixture.RequestFor(replacement));
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, semanticReplay.Disposition);
        Assert.Equal(fixture.Request.Operation, semanticReplay.Receipt!.Request.Operation);
    }

    [Fact]
    public async Task CreateOnPresent_FromDifferentOccurrenceWithChangedIntent_IsIdentityConflict()
    {
        var fixture = await Fixture.CreateAsync();
        _ = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var replacement = fixture.ReplacementInvocation(input: fixture.Input(status: "changed"));

        var conflict = await fixture.Adapter.ExecuteAsync(fixture.Context, replacement);

        Assert.False(conflict.IsSuccessful);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.IdentityConflict, conflict.Failure?.Code);
    }

    [Fact]
    public async Task CreateOnPresent_FromDifferentAuthorityWithOtherwiseExactIntent_IsIdentityConflict()
    {
        var fixture = await Fixture.CreateAsync();
        _ = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var replacement = fixture.ReplacementInvocation(
            authorityScope: new("authority/other", "tenant/acme"));

        var conflict = await fixture.Adapter.ExecuteAsync(fixture.Context, replacement);

        Assert.False(conflict.IsSuccessful);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.IdentityConflict, conflict.Failure?.Code);
    }

    [Fact]
    public async Task ConcurrentExactCreationIntents_CommitOnceAndConvergeOnOriginalReceipt()
    {
        using var barrier = new Barrier(participantCount: 2);
        var fixture = await Fixture.CreateAsync(commitBoundary: phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.BeforeAtomicCommit
                && !barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Concurrent creation attempts did not reach the atomic boundary together.");
            }
        });
        var replacement = fixture.ReplacementInvocation();

        // The commit hook blocks at a barrier. Give each participant its own worker so a
        // saturated test-runner thread pool cannot prevent the second participant from starting.
        var results = await Task.WhenAll(
            Task.Factory.StartNew(() => fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation).GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() => fixture.Adapter.ExecuteAsync(fixture.Context, replacement).GetAwaiter().GetResult(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

        Assert.All(results, static result => Assert.True(result.IsSuccessful));
        Assert.Equal(results[0], results[1]);
        Assert.Single(results[0].Emissions);
        Assert.NotNull(await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full));
        var exactDispositions = await Task.WhenAll(
            fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request),
            fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.RequestFor(replacement)));
        Assert.Single(exactDispositions, static result =>
            result.Disposition == EntityTransitionOperationDisposition.Replayed);
        Assert.Single(exactDispositions, static result =>
            result.Disposition == EntityTransitionOperationDisposition.NotFound);
    }

    [Fact]
    public async Task CreateOnPresent_IsRejectedWithoutReplacingAuthoritativeState()
    {
        var fixture = await Fixture.CreateAsync(seedSubject: true);
        var before = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        var result = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var after = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectPresent, result.Failure?.Code);
        Assert.Equal(before, after);
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);
    }

    [Fact]
    public async Task SubjectCreatedAfterRead_IsRejectedAtomicallyWithoutReplacingWinner()
    {
        Fixture? fixture = null;
        var injectRace = true;
        fixture = await Fixture.CreateAsync(commitBoundary: phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.BeforeAtomicCommit && injectRace)
            {
                injectRace = false;
                fixture!.SeedSubjectAsync("racing-winner").GetAwaiter().GetResult();
            }
        });

        var result = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var retained = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        Assert.False(result.IsSuccessful);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.SubjectStateConflict, result.Failure?.Code);
        Assert.Equal("racing-winner", retained!.Entity.Observation.GetField(nameof(CustomerEntity.Status)).GetString());
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);
    }

    [Fact]
    public async Task UpdateOnlyTransitionOnAbsentSubject_RemainsRejected()
    {
        var fixture = await Fixture.CreateAsync(createsSubject: false);

        var result = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectMissing, result.Failure?.Code);
        Assert.Null(await fixture.Repository.TryGet(fixture.Context, fixture.SubjectId, EntityReadOptions.Full));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);
    }

    [Fact]
    public async Task InitialStateViolatingEntityInvariant_IsRejectedBeforeCommit()
    {
        var fixture = await Fixture.CreateAsync();
        var invalid = fixture.Invocation with { Input = fixture.Input(status: "invalid") };

        var result = await fixture.Adapter.ExecuteAsync(fixture.Context, invalid);

        Assert.False(result.IsSuccessful);
        Assert.Equal(
            "transition.state.observationInvalid",
            result.Failure?.Code);
        Assert.Equal("/decision/evidence/initialObservation", result.Failure?.Location);
        Assert.Null(await fixture.Repository.TryGet(fixture.Context, fixture.SubjectId, EntityReadOptions.Full));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);
    }

    [Fact]
    public async Task DurableProcess_CommitsCreationHandoffAndProcessEvidence_ThenActivationReplays()
    {
        var fixture = await Fixture.CreateAsync();
        var processDefinition = new Cohesive.Processes.IR.ProcessDefinition(
            fixture.StateContract,
            Fixture.StringContract,
            new("process/create-customer/invoke"),
            [
                new InvokeTransitionProcessNode(
                    new("process/create-customer/invoke"),
                    fixture.Plan.DefinitionReference,
                    Expr.Field(ProcessBindingIds.Input, nameof(CustomerEntity.Id)),
                    Expr.BoundValue(ProcessBindingIds.Input),
                    new(new ProcessEdge(
                        new("process/create-customer/invoke-return"),
                        new("process/create-customer/return")))),
                new ReturnProcessNode(
                    new("process/create-customer/return"),
                    Expr.Const("completed"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var processDocument = ProcessDefinitionDocuments.Create(
            new("process/create-customer"),
            new("revision/1"),
            processDefinition,
            Fixture.Provenance());
        var processCompilation = ProcessStaticCompiler.Compile(
            processDocument,
            new ProcessDefinitionValidationContext(
                definitions:
                [
                    new(
                        fixture.Plan.DefinitionReference,
                        ProcessDefinitionLinkKind.Transition,
                        fixture.StateContract,
                        Fixture.StringContract)
                ],
                interactionContracts: fixture.InteractionCatalog));
        Assert.True(processCompilation.IsSuccessful, string.Join(
            Environment.NewLine,
            processCompilation.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var processPlan = Assert.IsType<CompiledProcessPlan>(processCompilation.Plan);
        var continuation = new ProcessContinuationIdentity(
            new("process-instance/create-customer/durable"),
            new("process-attempt/1"));
        var start = new ProcessStartReceipt(
            new(
                ProcessStartRequest.CurrentSchemaVersion,
                processPlan.DefinitionReference,
                new(
                    new("start-command/create-customer/durable"),
                    new("start-idempotency/create-customer/durable"),
                    continuation.ProcessInstanceId,
                    new("operator/tests", fixture.Invocation.Context.AuthorityScope, "policy/tests/allow"),
                    fixture.Invocation.ObservedAtUtc,
                    Fixture.Provenance()),
                continuation,
                fixture.Invocation.Input),
            fixture.Invocation.ObservedAtUtc.AddSeconds(1));
        var activation = new ProcessActivation(
            new("activation/create-customer/durable"),
            ProcessActivationCause.Start,
            fixture.Invocation.ObservedAtUtc.AddSeconds(2),
            fixture.Invocation.Context);
        var store = new InMemoryProcessDurableStore();
        var runtime = new ProcessDurableRuntime(
            store,
            RejectingReferenceHost.Instance,
            new("worker/create-customer/durable", TimeSpan.FromMinutes(5)),
            transitionOperationAdapter: fixture.Adapter);
        var initialized = await runtime.InitializeAsync(
            OperationContext.Create(new FixedTimeProvider(fixture.Invocation.ObservedAtUtc.AddSeconds(1))),
            processPlan,
            start);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var activated = await runtime.ActivateAsync(
            OperationContext.Create(new FixedTimeProvider(fixture.Invocation.ObservedAtUtc.AddSeconds(3))),
            processPlan,
            before.Checkpoint.ContinuationIdentity,
            activation);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        var replayed = await runtime.ActivateAsync(
            OperationContext.Create(new FixedTimeProvider(fixture.Invocation.ObservedAtUtc.AddSeconds(4))),
            processPlan,
            before.Checkpoint.ContinuationIdentity,
            activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.Disposition);
        Assert.Equal(ProcessActivationDisposition.Completed, activated.Decision?.Disposition);
        Assert.Single(checkpoint.Activations);
        Assert.Single(checkpoint.Operations);
        Assert.Single(checkpoint.Emissions);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replayed.Disposition);
        Assert.NotNull(await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full));
        var operation = Assert.Single(checkpoint.Operations);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, (
            await fixture.Repository.TryGetTransitionOperation(
                fixture.Context,
                new(
                    operation.Key,
                    fixture.Invocation.Context.AuthorityScope,
                    fixture.Plan.DefinitionReference,
                    new(new(fixture.Repository.EntityType), new(fixture.SubjectId)),
                    fixture.Invocation.Input))).Disposition);
    }

    [Fact]
    public async Task ReusedOccurrenceWithChangedInput_IsIdentityConflictAndDoesNotReplaceCreatedState()
    {
        var fixture = await Fixture.CreateAsync();
        var committed = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var changed = fixture.Invocation with
        {
            Input = fixture.Input(status: "changed")
        };

        var conflict = await fixture.Adapter.ExecuteAsync(fixture.Context, changed);
        var retained = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        Assert.True(committed.IsSuccessful);
        Assert.False(conflict.IsSuccessful);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.IdentityConflict, conflict.Failure?.Code);
        Assert.Equal("pending", retained!.Entity.Observation.GetField(nameof(CustomerEntity.Status)).GetString());
    }

    [Fact]
    public async Task CrashBeforeAtomicCreate_RetainsNothing_AndRetryCreatesOnce()
    {
        var crashPending = true;
        var fixture = await Fixture.CreateAsync(commitBoundary: phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.BeforeAtomicCommit && crashPending)
            {
                crashPending = false;
                throw new InjectedCrashException(phase);
            }
        });

        var crash = await Assert.ThrowsAsync<InjectedCrashException>(() =>
            fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation).AsTask());
        Assert.Equal(EntityTransitionOperationCommitPhase.BeforeAtomicCommit, crash.Phase);
        Assert.Null(await fixture.Repository.TryGet(fixture.Context, fixture.SubjectId, EntityReadOptions.Full));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);

        var retry = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);

        Assert.True(retry.IsSuccessful);
        Assert.NotNull(await fixture.Repository.TryGet(fixture.Context, fixture.SubjectId, EntityReadOptions.Full));
    }

    [Fact]
    public async Task CrashAfterAtomicCreate_RetryReplaysWithoutAnotherMutation()
    {
        var crashPending = true;
        var fixture = await Fixture.CreateAsync(commitBoundary: phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn && crashPending)
            {
                crashPending = false;
                throw new InjectedCrashException(phase);
            }
        });

        var crash = await Assert.ThrowsAsync<InjectedCrashException>(() =>
            fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation).AsTask());
        Assert.Equal(EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn, crash.Phase);
        var afterCrash = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        var replayed = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        var afterReplay = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.SubjectId,
            EntityReadOptions.Full);

        Assert.True(replayed.IsSuccessful);
        Assert.Equal(afterCrash, afterReplay);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, (
            await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request)).Disposition);
    }

    sealed class Fixture
    {
        static readonly DateTimeOffset ObservedAtUtc = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);
        internal static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
        static readonly ExecutionDefinitionReference Process =
            ProcessDurabilityTestFixture.DefinitionReference("process/create-customer", '1');

        Fixture(
            OperationContext context,
            InMemoryEntityOutboxRepository repository,
            EntityTransitionProcessOperationAdapter adapter,
            CompiledTransitionPlan plan,
            InteractionContractCatalog interactionCatalog,
            ProcessTransitionInvocation invocation,
            EntityTransitionOperationRequest request,
            ValueContract stateContract)
        {
            Context = context;
            Repository = repository;
            Adapter = adapter;
            Plan = plan;
            InteractionCatalog = interactionCatalog;
            Invocation = invocation;
            Request = request;
            StateContract = stateContract;
        }

        internal OperationContext Context { get; }

        internal InMemoryEntityOutboxRepository Repository { get; }

        internal EntityTransitionProcessOperationAdapter Adapter { get; }

        internal CompiledTransitionPlan Plan { get; }

        internal InteractionContractCatalog InteractionCatalog { get; }

        internal ProcessTransitionInvocation Invocation { get; }

        internal EntityTransitionOperationRequest Request { get; }

        internal ValueContract StateContract { get; }

        internal string SubjectId => "customer/1";

        internal PortableValue Input(string status) => State(
            StateContract,
            SubjectId,
            "tenant/acme",
            status);

        internal EntityTransitionOperationRequest RequestFor(ProcessTransitionInvocation invocation) => new(
            new(
                invocation.Continuation,
                invocation.Activation,
                invocation.Token,
                invocation.Node,
                invocation.Occurrence),
            invocation.Context.AuthorityScope,
            invocation.Definition,
            new(new(Repository.EntityType), new(SubjectId)),
            invocation.Input);

        internal ProcessTransitionInvocation ReplacementInvocation(
            PortableValue? input = null,
            InteractionAuthorityScope? authorityScope = null)
        {
            var context = Invocation.Context;
            if (authorityScope is not null)
            {
                context = new(
                    authorityScope,
                    context.CorrelationId,
                    context.Delivery,
                    context.Provenance,
                    context.CausationId,
                    context.Ordering);
            }
            return Invocation with
            {
                Input = input ?? Invocation.Input,
                Continuation = new(
                    Invocation.Continuation.ProcessInstanceId,
                    new("process-attempt/2")),
                Activation = new("activation/create-customer/2"),
                Token = new("token/create-customer/2"),
                ObservedAtUtc = Invocation.ObservedAtUtc.AddSeconds(1),
                Context = context
            };
        }

        internal async Task SeedSubjectAsync(string status)
        {
            var seeded = CustomerEntity.Instance.CreateState(
                SubjectId,
                new CustomerState(SubjectId, "tenant/acme", status));
            _ = await Repository.Upsert(Context, new(seeded.Snapshot));
        }

        internal static async Task<Fixture> CreateAsync(
            bool seedSubject = false,
            bool createsSubject = true,
            Action<EntityTransitionOperationCommitPhase>? commitBoundary = null)
        {
            var context = OperationContext.Create(new FixedTimeProvider(ObservedAtUtc));
            var entity = CustomerEntity.Instance.Definition;
            var repository = commitBoundary is null
                ? new InMemoryEntityOutboxRepository(
                    entity,
                    EntityPartitionKeyPolicy.FromField(nameof(CustomerEntity.Tenant)))
                : new InMemoryEntityOutboxRepository(
                    entity,
                    EntityPartitionKeyPolicy.FromField(nameof(CustomerEntity.Tenant)),
                    commitBoundary);
            var stateContract = ValueContract.FromShape(entity.Shape);
            if (seedSubject)
            {
                var seeded = CustomerEntity.Instance.CreateState(
                    "customer/1",
                    new CustomerState("customer/1", "tenant/acme", "retained"));
                _ = await repository.Upsert(context, new(seeded.Snapshot));
            }

            var eventDocument = InteractionContractDocuments.Create(
                new("event/customer-created"),
                new("revision/1"),
                new DomainEventContractDefinition(new(StringContract, new("event/customer-created/v1"))),
                Provenance());
            var eventReference = new ExecutionDefinitionReference(
                eventDocument.Metadata.DefinitionId,
                eventDocument.Metadata.RevisionId,
                eventDocument.Metadata.Fingerprint);
            var definition = new TransitionDefinition(
                stateContract,
                stateContract,
                StringContract,
                [],
                new(
                    new("transition/create-customer/body"),
                    [
                        new EmitTransitionNode(
                            new("transition/create-customer/emit"),
                            eventReference,
                            Expr.Field(TransitionBindingIds.Input, nameof(CustomerEntity.Id))),
                        new OutcomeTransitionNode(
                            new("transition/create-customer/outcome"),
                            TransitionOutcomeDisposition.Applied,
                            Expr.Const("created"))
                    ]),
                subjectCreation: createsSubject
                    ? new(
                        new("transition/create-customer/initialize"),
                        Expr.BoundValue(TransitionBindingIds.Input))
                    : null);
            var transitionDocument = TransitionDefinitionDocuments.Create(
                new("transition/create-customer"),
                new("revision/1"),
                definition,
                Provenance());
            var compilation = TransitionStaticCompiler.Compile(transitionDocument);
            Assert.True(
                compilation.IsSuccessful,
                string.Join(Environment.NewLine, compilation.Validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
            var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
            var catalogValidation = InteractionContractCatalog.TryCreate([eventDocument], out var catalog);
            Assert.True(catalogValidation.IsValid);
            var adapter = new EntityTransitionProcessOperationAdapter(invocation =>
                invocation.Definition == plan.DefinitionReference
                    ? new(plan, repository, catalog!)
                    : null);
            var continuation = new ProcessContinuationIdentity(
                new("process-instance/create-customer"),
                new("process-attempt/1"));
            var activation = new ActivationId("activation/create-customer/1");
            var token = new TokenId("token/create-customer/1");
            var node = new ExecutionNodeId("process/create-customer/invoke");
            var invocation = new ProcessTransitionInvocation(
                Process,
                plan.DefinitionReference,
                ProcessDurabilityTestFixture.StringValue("customer/1"),
                State(stateContract, "customer/1", "tenant/acme", "pending"),
                continuation,
                activation,
                token,
                node,
                Occurrence: 0,
                ObservedAtUtc,
                ActivationContext());
            var request = new EntityTransitionOperationRequest(
                new(continuation, activation, token, node, occurrence: 0),
                invocation.Context.AuthorityScope,
                plan.DefinitionReference,
                new(new(repository.EntityType), new("customer/1")),
                invocation.Input);
            return new(context, repository, adapter, plan, catalog!, invocation, request, stateContract);
        }

        static PortableValue State(
            ValueContract contract,
            string id,
            string tenant,
            string status) => PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(CustomerEntity.Id)] = ObservationValue.FromString(id),
                [nameof(CustomerEntity.Tenant)] = ObservationValue.FromString(tenant),
                [nameof(CustomerEntity.Status)] = ObservationValue.FromString(status)
            }));

        static ProcessActivationContext ActivationContext() => new(
            new("authority/tests", "tenant/acme"),
            new("correlation/create-customer"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance());

        internal static ExecutionProvenance Provenance() => new(
            new("entity-transition-creation-tests", "1"),
            new("tests/execution-kernel/entity-transition-creation"),
            DocumentOrigin.User);
    }

    sealed class CustomerEntity : Entity<CustomerEntity>
    {
        public CustomerEntity()
            : base(nameof(CustomerEntity))
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Status = Field<string>(nameof(Status));
            Invariant("status-valid", entity => entity.Status != "invalid");
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Status { get; }
    }

    sealed record CustomerState(string Id, string Tenant, string Status);

    sealed class CosmosEntityTransitionCreationFactAttribute : FactAttribute
    {
        public CosmosEntityTransitionCreationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COSMOS_ENTITY_TRANSITION_OPERATION_CONNECTION_STRING")))
            {
                Skip = "Set COSMOS_ENTITY_TRANSITION_OPERATION_CONNECTION_STRING to run the Cosmos Transition creation integration test.";
            }
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class InjectedCrashException(EntityTransitionOperationCommitPhase phase) : Exception
    {
        internal EntityTransitionOperationCommitPhase Phase { get; } = phase;
    }

    sealed class RejectingReferenceHost : IProcessReferenceHost
    {
        internal static RejectingReferenceHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException(
                $"Transition '{invocation.Definition.DefinitionId.Value}' bypassed its durable entity adapter.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation operation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
