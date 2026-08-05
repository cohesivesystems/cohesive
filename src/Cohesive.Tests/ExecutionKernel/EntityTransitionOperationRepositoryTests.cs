using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Model;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EntityTransitionOperationRepositoryTests
{
    [Fact]
    public async Task ExactRetry_ReplaysTypedResultAndHandoffWithoutReevaluatingOrPublishing()
    {
        var fixture = await Fixture.CreateAsync();
        var evaluations = 0;

        var committed = await fixture.Repository.ExecuteTransitionOperation(
            fixture.Context,
            fixture.Request,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });
        var operation = fixture.Request.Operation;
        var reconstructedRequest = new EntityTransitionOperationRequest(
            new(
                operation.Continuation,
                operation.Activation,
                operation.Token,
                operation.Node,
                operation.Occurrence),
            fixture.Request.Transition,
            fixture.Request.Subject,
            fixture.Request.Input);
        var replayed = await fixture.Repository.ExecuteTransitionOperation(
            fixture.Context,
            reconstructedRequest,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });

        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(1, evaluations);
        var receipt = Assert.IsType<EntityTransitionOperationReceipt>(committed.Receipt);
        Assert.Same(receipt, replayed.Receipt);
        Assert.Equal(fixture.Commit.Result, receipt.Result);
        Assert.Equal(
            fixture.Commit.Result.Emissions.Select(static envelope => envelope.Context.EmissionId),
            receipt.Result.Emissions.Select(static envelope => envelope.Context.EmissionId));
        Assert.Equal(
            EntityTransitionEmissionPublicationAuthority.ProcessOutbox,
            receipt.PublicationAuthority);
        Assert.Empty(fixture.Repository.OutboxEnvelopes);
        Assert.Equal(
            "approved",
            receipt.Entity.Entity.GetField(nameof(CustomerEntity.Status)).GetString());
    }

    [Fact]
    public async Task CrashBeforeAtomicCommit_RetainsNeitherMutationNorReceipt_AndExactRetryCommitsOnce()
    {
        var crashPending = true;
        var fixture = await Fixture.CreateAsync(phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.BeforeAtomicCommit && crashPending)
            {
                crashPending = false;
                throw new InjectedCrashException(phase);
            }
        });
        var evaluations = 0;

        var crash = await Assert.ThrowsAsync<InjectedCrashException>(() =>
            fixture.Repository.ExecuteTransitionOperation(
                fixture.Context,
                fixture.Request,
                () =>
                {
                    evaluations++;
                    return fixture.Commit;
                }));

        Assert.Equal(EntityTransitionOperationCommitPhase.BeforeAtomicCommit, crash.Phase);
        var afterCrash = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.Subject.EntityId.Value,
            EntityReadOptions.Full);
        Assert.Equal(fixture.Initial, afterCrash);
        var lookup = await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request);
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, lookup.Disposition);

        var retry = await fixture.Repository.ExecuteTransitionOperation(
            fixture.Context,
            fixture.Request,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });

        Assert.Equal(EntityTransitionOperationDisposition.Committed, retry.Disposition);
        Assert.Equal(2, evaluations);
        Assert.NotEqual(fixture.Initial.ConcurrencyToken, retry.Receipt!.Entity.ConcurrencyToken);
    }

    [Fact]
    public async Task CrashAfterAtomicCommit_RetainsOneMutationAndReceipt_AndExactRetryOnlyReplays()
    {
        var crashPending = true;
        var fixture = await Fixture.CreateAsync(phase =>
        {
            if (phase == EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn && crashPending)
            {
                crashPending = false;
                throw new InjectedCrashException(phase);
            }
        });
        var evaluations = 0;

        var crash = await Assert.ThrowsAsync<InjectedCrashException>(() =>
            fixture.Repository.ExecuteTransitionOperation(
                fixture.Context,
                fixture.Request,
                () =>
                {
                    evaluations++;
                    return fixture.Commit;
                }));

        Assert.Equal(EntityTransitionOperationCommitPhase.AfterAtomicCommitBeforeReturn, crash.Phase);
        var retained = await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, retained.Disposition);
        var token = retained.Receipt!.Entity.ConcurrencyToken;

        var retry = await fixture.Repository.ExecuteTransitionOperation(
            fixture.Context,
            fixture.Request,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });

        Assert.Equal(EntityTransitionOperationDisposition.Replayed, retry.Disposition);
        Assert.Equal(1, evaluations);
        Assert.Equal(token, retry.Receipt!.Entity.ConcurrencyToken);
        var current = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.Subject.EntityId.Value,
            EntityReadOptions.Full);
        Assert.Equal(token, current!.ConcurrencyToken);
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("subject")]
    [InlineData("input")]
    public async Task ReusedOperationOccurrenceWithDifferentRequestIdentity_FailsBeforeEvaluation(string conflict)
    {
        var fixture = await Fixture.CreateAsync();
        _ = await fixture.Repository.CommitTransitionOperation(fixture.Context, fixture.Commit);
        EntityTransitionOperationRequest request = conflict switch
        {
            "transition" => new(
                fixture.Request.Operation,
                ProcessDurabilityTestFixture.DefinitionReference("transition/another", '7'),
                fixture.Subject,
                fixture.Request.Input),
            "subject" => new(
                fixture.Request.Operation,
                fixture.Transition,
                new(fixture.Subject.EntityType, new("customer/another")),
                fixture.Request.Input),
            "input" => new(
                fixture.Request.Operation,
                fixture.Transition,
                fixture.Subject,
                ProcessDurabilityTestFixture.StringValue("input/another")),
            _ => throw new ArgumentOutOfRangeException(nameof(conflict), conflict, null)
        };
        var evaluations = 0;

        var result = await fixture.Repository.ExecuteTransitionOperation(
            fixture.Context,
            request,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });

        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(0, evaluations);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.IdentityConflict
                                 && diagnostic.Location == "/request");
    }

    [Fact]
    public async Task ReusedOperationOccurrenceWithDifferentNormalizedResult_IsRejected()
    {
        var fixture = await Fixture.CreateAsync();
        var committed = await fixture.Repository.CommitTransitionOperation(fixture.Context, fixture.Commit);
        var conflicting = fixture.CreateCommit(resultValue: "another-outcome");

        var result = await fixture.Repository.CommitTransitionOperation(fixture.Context, conflicting);

        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.IdentityConflict
                                 && diagnostic.Location == "/commit");
        Assert.Equal(fixture.Commit.Result, committed.Receipt!.Result);
    }

    [Fact]
    public async Task StaleEntityConcurrencyFence_FailsWithoutMutationOrReceipt()
    {
        var fixture = await Fixture.CreateAsync();
        var stale = fixture.CreateCommit(expectedConcurrencyToken: new("mem:stale"));

        var result = await fixture.Repository.CommitTransitionOperation(fixture.Context, stale);

        Assert.Equal(EntityTransitionOperationDisposition.ConcurrencyConflict, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict
                                 && diagnostic.Location == "/write/expectedConcurrencyToken");
        var current = await fixture.Repository.TryGet(
            fixture.Context,
            fixture.Subject.EntityId.Value,
            EntityReadOptions.Full);
        Assert.Equal(fixture.Initial, current);
        var lookup = await fixture.Repository.TryGetTransitionOperation(fixture.Context, fixture.Request);
        Assert.Equal(EntityTransitionOperationDisposition.NotFound, lookup.Disposition);
    }

    [Fact]
    public async Task RepositoryWithoutAtomicCapability_ReturnsStructuredDiagnosticBeforeEvaluation()
    {
        var fixture = await Fixture.CreateAsync();
        IEntityRepository repository = new NonAtomicRepository(fixture.Repository);
        var evaluations = 0;

        var result = await repository.ExecuteTransitionOperation(
            fixture.Context,
            fixture.Request,
            () =>
            {
                evaluations++;
                return fixture.Commit;
            });

        Assert.Equal(EntityTransitionOperationDisposition.CapabilityInsufficient, result.Disposition);
        Assert.Equal(0, evaluations);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.CapabilityInsufficient
                                 && diagnostic.Location == "/repository/transitionOperationCapabilities");
    }

    sealed class Fixture
    {
        static readonly DateTimeOffset CommittedAtUtc =
            new(2026, 8, 5, 4, 0, 0, TimeSpan.Zero);
        static readonly ValueContract StateContract = new(new ObjectTypeRef(
        [
            new ObjectFieldTypeDef(nameof(CustomerEntity.Id), ProcessDurabilityTestFixture.StringContract.Type!),
            new ObjectFieldTypeDef(nameof(CustomerEntity.Tenant), ProcessDurabilityTestFixture.StringContract.Type!),
            new ObjectFieldTypeDef(nameof(CustomerEntity.Status), ProcessDurabilityTestFixture.StringContract.Type!)
        ]));
        static readonly ExecutionDefinitionReference Process =
            ProcessDurabilityTestFixture.DefinitionReference("process/customer-approval", '2');
        static readonly ExecutionDefinitionReference Event =
            ProcessDurabilityTestFixture.DefinitionReference("event/customer-approved", '3');

        Fixture(
            OperationContext context,
            InMemoryEntityOutboxRepository repository,
            EntitySnapshot initial,
            InteractionEntityReference subject,
            ExecutionDefinitionReference transition,
            EntityTransitionOperationRequest request,
            TransitionDecision decision,
            EntityTransitionOperationCommit commit)
        {
            Context = context;
            Repository = repository;
            Initial = initial;
            Subject = subject;
            Transition = transition;
            Request = request;
            Decision = decision;
            Commit = commit;
        }

        internal OperationContext Context { get; }

        internal InMemoryEntityOutboxRepository Repository { get; }

        internal EntitySnapshot Initial { get; }

        internal InteractionEntityReference Subject { get; }

        internal ExecutionDefinitionReference Transition { get; }

        internal EntityTransitionOperationRequest Request { get; }

        internal TransitionDecision Decision { get; }

        internal EntityTransitionOperationCommit Commit { get; }

        internal static async Task<Fixture> CreateAsync(
            Action<EntityTransitionOperationCommitPhase>? commitBoundary = null)
        {
            var context = OperationContext.Create(new FixedTimeProvider(CommittedAtUtc));
            var definition = CustomerEntity.Instance.Definition;
            var partitionKey = EntityPartitionKeyPolicy.FromField(nameof(CustomerEntity.Tenant));
            var repository = commitBoundary is null
                ? new InMemoryEntityOutboxRepository(definition, partitionKey)
                : new InMemoryEntityOutboxRepository(definition, partitionKey, commitBoundary);
            var initialState = CustomerEntity.Instance.CreateState(
                "customer/1",
                new CustomerState("customer/1", "tenant/acme", "pending"));
            var initial = await repository.Upsert(context, new(initialState.Observation));
            var subject = new InteractionEntityReference(
                new(repository.EntityType),
                new(initial.Entity.Id));
            var operation = new ProcessOperationOccurrence(
                new(new("process-instance/customer-approval"), new("process-attempt/1")),
                new("process-activation/1"),
                new("token/approval"),
                new("process/invoke-approval"),
                occurrence: 0);
            var decision = Decide(operation.Activation, initial.Entity);
            var transition = decision.Evidence.Definition;
            var request = new EntityTransitionOperationRequest(
                operation,
                transition,
                subject,
                ProcessDurabilityTestFixture.StringValue("input/approve"));
            var candidateValue = TransitionStateProjector.Apply(
                ObservationValue.FromObject(initial.Entity.Fields),
                decision);
            var candidate = new Observation(
                initial.Entity.ShapeId,
                initial.Entity.Id,
                candidateValue.Fields!,
                version: initial.Entity.Version + 1,
                initial.Entity.Lineage);
            var commit = CreateCommit(
                request,
                candidate,
                initial.ConcurrencyToken,
                decision,
                resultValue: "approval/accepted");
            return new(context, repository, initial, subject, transition, request, decision, commit);
        }

        internal EntityTransitionOperationCommit CreateCommit(
            string resultValue = "approval/accepted",
            EntityConcurrencyToken? expectedConcurrencyToken = null)
        {
            var candidateValue = TransitionStateProjector.Apply(
                ObservationValue.FromObject(Initial.Entity.Fields),
                Decision);
            var candidate = new Observation(
                Initial.Entity.ShapeId,
                Initial.Entity.Id,
                candidateValue.Fields!,
                version: Initial.Entity.Version + 1,
                Initial.Entity.Lineage);
            return CreateCommit(
                Request,
                candidate,
                expectedConcurrencyToken ?? Initial.ConcurrencyToken,
                Decision,
                resultValue);
        }

        static EntityTransitionOperationCommit CreateCommit(
            EntityTransitionOperationRequest request,
            Observation candidate,
            EntityConcurrencyToken expectedConcurrencyToken,
            TransitionDecision decision,
            string resultValue)
        {
            var emissionNode = Assert.Single(decision.Emissions).Node;
            var outcomeNode = decision.Evidence.Trace.Last(static trace =>
                trace.Kind == TransitionTraceEventKind.OutcomeReturned).Node;
            var provenance = new ExecutionProvenance(
                new("entity-transition-operation-tests", "1"),
                new("tests/execution-kernel/entity-transition-operation"),
                DocumentOrigin.User);
            var envelope = new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                new(
                    new("emission/customer-approved/1"),
                    new ProcessInteractionOrigin(
                        Process,
                        request.Operation.Node,
                        request.Operation.Continuation,
                        request.Operation.Activation,
                        request.Operation.Token,
                        request.Subject,
                        request.Transition,
                        outcomeNode,
                        emissionNode),
                    new("correlation/customer-approval"),
                    causationId: null,
                    new("authority/tests", "tenant/acme"),
                    new("idempotency/customer-approved/1"),
                    ordering: null,
                    new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance),
                new(Event),
                ProcessDurabilityTestFixture.StringValue("customer/1"));
            var result = ProcessOperationResult.Completed(
                ProcessDurabilityTestFixture.StringValue(resultValue),
                [envelope]);
            return new(
                request,
                new(candidate, expectedConcurrencyToken),
                decision.Kind,
                result,
                decision.GuaranteeDemands,
                decision.Evidence);
        }

        static TransitionDecision Decide(ActivationId activation, Observation current)
        {
            var definition = new CanonicalTransitionDefinition(
                ProcessDurabilityTestFixture.StringContract,
                StateContract,
                ProcessDurabilityTestFixture.StringContract,
                [],
                new SequenceTransitionNode(
                    new("transition/body"),
                    [
                        new UpdateTransitionNode(
                            new("transition/set-approved"),
                            FieldPath.FromField(nameof(CustomerEntity.Status)),
                            new SetTransitionPatch(Expr.Const("approved"))),
                        new EmitTransitionNode(
                            new("transition/emit-approved"),
                            Event,
                            Expr.Const("customer/1")),
                        new OutcomeTransitionNode(
                            new("transition/outcome-applied"),
                            TransitionOutcomeDisposition.Applied,
                            Expr.Const("approval/accepted"))
                    ]));
            var document = TransitionDefinitionDocuments.Create(
                new("transition/approve-customer"),
                new("revision/1"),
                definition,
                new(
                    new("entity-transition-operation-tests", "1"),
                    new("tests/execution-kernel/entity-transition-operation/transition"),
                    DocumentOrigin.User));
            var compilation = TransitionStaticCompiler.Compile(document);
            Assert.True(
                compilation.IsSuccessful,
                string.Join(Environment.NewLine, compilation.Validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
            var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
            return TransitionReferenceInterpreter.DecideFullState(
                plan,
                activation,
                ProcessDurabilityTestFixture.StringValue("input/approve"),
                PortableValue.Concrete(StateContract, ObservationValue.FromObject(current.Fields)));
        }
    }

    sealed class CustomerEntity : Entity<CustomerEntity>
    {
        public CustomerEntity()
            : base(nameof(CustomerEntity))
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Status = Field<string>(nameof(Status));
        }

        public Field<string> Id { get; }

        public Field<string> Tenant { get; }

        public Field<string> Status { get; }
    }

    sealed record CustomerState(string Id, string Tenant, string Status);

    sealed class NonAtomicRepository(IEntityRepository inner) : IEntityRepository
    {
        public EntityDefinition EntityDefinition => inner.EntityDefinition;

        public ShapeMappingContext MappingContext => inner.MappingContext;

        public Task<EntitySnapshot?> TryGet(
            OperationContext context,
            string id,
            EntityReadOptions? options = null) =>
            inner.TryGet(context, id, options);

        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
            inner.Upsert(context, write);
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class InjectedCrashException(EntityTransitionOperationCommitPhase phase) : Exception
    {
        internal EntityTransitionOperationCommitPhase Phase { get; } = phase;
    }
}
