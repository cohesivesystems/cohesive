using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;

namespace Cohesive.Tests.Storage.Conformance;

// The same receipt protocol assertions run in memory, SQLite, PostgreSQL and the gated Cosmos fixture.
public static class EntityTransitionReceiptConformance
{
    public static async Task ConcurrentReplay(IEntityRepository repository)
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.CommitTransitionOperation(context, commit)));
        Assert.Single(results, result => result.Disposition == EntityTransitionOperationDisposition.Committed);
        Assert.Equal(7, results.Count(result => result.Disposition == EntityTransitionOperationDisposition.Replayed));
        Assert.All(results, result => Assert.Equal(commit.Fingerprint, result.Receipt!.Commit.Fingerprint));
        var stored = await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a"));
        Assert.Equal(1, stored!.Entity.Version);
    }
    public static async Task StaleFence(IEntityRepository repository)
    {
        var context = OperationContext.Create();
        var before = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        var newer = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "newer" }, 1));
        var result = await repository.CommitTransitionOperation(context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.ConcurrencyConflict, result.Disposition);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, Assert.Single(result.Diagnostics).Code);
        Assert.Equal("/write/expectedConcurrencyToken", Assert.Single(result.Diagnostics).Location);
        Assert.Equal(newer, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
        Assert.Equal(EntityTransitionOperationDisposition.NotFound,
            (await repository.TryGetTransitionOperation(context, evidence.Request)).Disposition);
    }
    public static async Task Creation(IEntityRepository repository, bool existingSubject = false)
    {
        var context = OperationContext.Create();
        var creation = TransitionAuthoring.Create<RunControl, RunControl, string>(RunControlFixture.Entity.Shape,
            id: new("adoption/create-control"), revision: new("1"),
            transition => transition.CreatesFrom(new("initial"), input => new RunControl(input.Id, input.Tenant,
                input.Status, input.Attempt, input.Enabled, input.Limit, input.ScheduledAt, input.InputDigest)).Return("created"));
        var plan = creation.Compile().Plan!;
        var operation = new ProcessOperationOccurrence(new(new("creation"), new("attempt/1")), new("activation/1"),
            new("token/1"), new("create"), 0);
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(RunControlFixture.Initial()));
        var request = new EntityTransitionOperationRequest(operation, new("adoption", "tenant/a"), plan.DefinitionReference,
            new(new(repository.EntityType), new("run/1")), input);
        var decision = TransitionReferenceInterpreter.DecideCreation(plan, operation.Activation, input);
        var candidate = TransitionStateProjector.ApplyToEntity(RunControlFixture.Entity, "run/1", decision);
        var commit = new EntityTransitionOperationCommit(request, new(candidate.Snapshot), decision.Kind,
            ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence,
            EntityTransitionSubjectCondition.MustBeAbsent);
        if (existingSubject)
        {
            var original = await repository.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
            var failure = await repository.CommitTransitionOperation(context, commit);
            Assert.Equal(EntityTransitionOperationDisposition.SubjectStateConflict, failure.Disposition);
            Assert.Equal(EntityTransitionOperationDiagnosticCodes.SubjectStateConflict, Assert.Single(failure.Diagnostics).Code);
            Assert.Equal("/write/subjectCondition", Assert.Single(failure.Diagnostics).Location);
            Assert.Equal(original, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));
            Assert.Equal(EntityTransitionOperationDisposition.NotFound, (await repository.TryGetTransitionOperation(context, request)).Disposition);
            return;
        }
        var committed = await repository.CommitTransitionOperation(context, commit);
        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        var replacementOperation = new ProcessOperationOccurrence(new(new("creation"), new("attempt/2")), new("activation/2"),
            new("token/1"), new("create"), 0);
        var replacement = new EntityTransitionOperationRequest(replacementOperation, request.AuthorityScope,
            request.Transition, request.Subject, input);
        var replacementDecision = TransitionReferenceInterpreter.DecideCreation(plan, replacementOperation.Activation, input);
        var replacementCommit = new EntityTransitionOperationCommit(replacement, commit.Write, commit.DecisionKind,
            commit.Result, replacementDecision.GuaranteeDemands, replacementDecision.Evidence, EntityTransitionSubjectCondition.MustBeAbsent);
        var replay = await repository.CommitTransitionOperation(context, replacementCommit);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        Assert.Equal(operation, replay.Receipt!.Request.Operation);
        Assert.Equal(committed.Receipt!.Entity, replay.Receipt.Entity);
        var different = new EntityTransitionOperationCommit(replacement,
            RunControlFixture.Write(RunControlFixture.Initial() with { Status = "different" }), commit.DecisionKind,
            commit.Result, replacementDecision.GuaranteeDemands, replacementDecision.Evidence, EntityTransitionSubjectCondition.MustBeAbsent);
        var conflict = await repository.CommitTransitionOperation(context, different);
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal("/commit", Assert.Single(conflict.Diagnostics).Location);
        Assert.Equal(committed.Receipt.Entity, await repository.TryGet(context, "run/1", new(partitionKey: "tenant/a")));

    }
}
