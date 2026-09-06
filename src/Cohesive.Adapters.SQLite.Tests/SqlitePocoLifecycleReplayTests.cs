using System.Text;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqlitePocoLifecycleReplayTests
{
    static readonly OperationContext Context = OperationContext.Create();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedPocoDecisionReplaysAfterRestartAndLostAcknowledgement(bool direct)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var store = new SqliteProofStore(file.Database);
        store.Initialize();
        var original = RunControlFixture.Initial();
        var before = await repository.Upsert(Context, RunControlFixture.Write(original));
        var evidence = RunControlFixture.Prepare(before);
        Save(store, evidence);
        var envelopes = RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct);
        var write = RunControlFixture.Commit(evidence, evidence.Decision,
            RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts())).Write;
        EntitySnapshot? committed = null;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            committed = direct
                ? (await repository.UpsertWithOutbox(Context, new(write, envelopes))).Entity
                : (await repository.CommitTransitionOperation(Context, RunControlFixture.Commit(evidence, evidence.Decision, envelopes))).Receipt!.Entity;
            throw new IOException("Commit succeeded but the acknowledgement was lost.");
        });
        var advanced = await repository.Upsert(Context, RunControlFixture.Write(original with { Status = "completed", Attempt = 1 }, version: 2));

        // A new authored revision is present, but exact replay must continue selecting the saved revision.
        var later = TransitionDefinitionDocuments.Create(RunControlFixture.Start.Reference.DefinitionId, new("revision/2"),
            RunControlFixture.Start.Definition, RunControlFixture.Provenance);
        store.PutBytes(SqliteProofStore.DocumentKey(later), ExecutionDefinitionJsonSerializer.GetCanonicalBytes(later));
        var reopened = new SqliteEntityOutboxRepository(new(new(file.Path)), repository.Mapping);
        var reconstructed = Replay(new SqliteProofStore(new(new(file.Path))), direct);
        EntitySnapshot retried;
        if (direct)
        {
            retried = (await reopened.UpsertWithOutbox(Context, new(reconstructed.Write, reconstructed.Envelopes))).Entity;
            Assert.Single(await reopened.ReadOutbox(Context));
        }
        else
        {
            var replay = await reopened.CommitTransitionOperation(Context, reconstructed.Operation!);
            Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
            Assert.Equal(reconstructed.Operation!.Fingerprint, replay.Receipt!.Commit.Fingerprint);
            retried = replay.Receipt.Entity;
            Assert.Empty(await reopened.ReadOutbox(Context));
        }
        Assert.Equal(committed, retried);
        Assert.Equal(advanced, await reopened.TryGet(Context, original.Id));
        var control = ObservationMaterializer.For<RunControl>(RunControlFixture.Entity.StateShape).Compile().Materialize(retried.Entity.Observation);
        Assert.Equal("running", control.Status);
        Assert.Equal(1, control.Attempt);
        Assert.Equal(original.InputDigest, control.InputDigest);
        Assert.Equal("scheduled", original.Status);
        Assert.Equal(0, original.Attempt);
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("event")]
    [InlineData("process")]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("document-content")]
    public async Task ReplayRejectsMissingOrMismatchedPinnedDefinitions(string change)
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var store = new SqliteProofStore(file.Database);
        store.Initialize();
        var before = await repository.Upsert(Context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(before);
        Save(store, evidence);
        if (change is "transition" or "event" or "process")
        {
            var document = change switch { "transition" => RunControlFixture.Start.Document, "event" => RunControlFixture.EventDocument, _ => RunControlFixture.ProcessDocument };
            store.Delete(SqliteProofStore.DocumentKey(document));
        }
        else if (change == "document-content")
        {
            var key = SqliteProofStore.DocumentKey(RunControlFixture.Start.Document);
            var node = JsonNode.Parse(store.GetBytes(key))!;
            node["definition"]!["body"]!["steps"]![0]!["id"] = "tampered";
            store.PutBytes(key, Encoding.UTF8.GetBytes(node.ToJsonString()));
        }
        else
        {
            var old = evidence.Request.Transition;
            var changed = new ExecutionDefinitionReference(old.DefinitionId, change == "revision" ? new("missing") : old.RevisionId,
                change == "fingerprint" ? new(old.Fingerprint.Algorithm, old.Fingerprint.Canonicalization, new string('f', 64)) : old.Fingerprint);
            store.Put("request", new EntityTransitionOperationRequest(evidence.Request.Operation, evidence.Request.AuthorityScope,
                changed, evidence.Request.Subject, evidence.Request.Input));
        }
        if (change == "document-content")
        {
            var error = Assert.Throws<System.Text.Json.JsonException>(() => Replay(new SqliteProofStore(new(new(file.Path))), direct: false));
            Assert.Contains("execution.definition.fingerprint.mismatch", error.Message);
        }
        else
            Assert.Throws<InvalidOperationException>(() => Replay(new SqliteProofStore(new(new(file.Path))), direct: false));
        Assert.Equal(before, await repository.TryGet(Context, "run/1"));
        Assert.Empty(await repository.ReadOutbox(Context));
    }

    [Fact]
    public async Task ChangedPriorStateCannotMasqueradeAsDecisionReplay()
    {
        using var file = new DatabaseFixture();
        var repository = Repository(file);
        var store = new SqliteProofStore(file.Database);
        store.Initialize();
        var before = await repository.Upsert(Context, RunControlFixture.Write(RunControlFixture.Initial()));
        Save(store, RunControlFixture.Prepare(before));
        store.Put("before", before with { Entity = RunControlFixture.Write(RunControlFixture.Initial() with { Enabled = false }).Entity });
        var error = Assert.Throws<InvalidOperationException>(() => Replay(store, direct: false));
        Assert.Contains("decision differs", error.Message);
        Assert.Equal(before, await repository.TryGet(Context, "run/1"));
    }

    static SqliteEntityOutboxRepository Repository(DatabaseFixture file)
    {
        var mapping = new SqliteEntityRepositoryMapping(RunControlFixture.Entity, nameof(RunControl.Id), partitionField: nameof(RunControl.Tenant));
        var repository = new SqliteEntityOutboxRepository(file.Database, mapping);
        new SqliteSchema("run/state", [mapping.InitialMigration]).Apply(file.Database);
        new SqliteSchema("run/outbox", repository.Migrations).Apply(file.Database);
        return repository;
    }

    static void Save(SqliteProofStore store, RunReplayEvidence evidence)
    {
        foreach (var document in RunControlFixture.Documents) store.PutBytes(SqliteProofStore.DocumentKey(document), ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document));
        store.Put("before", evidence.Before);
        store.Put("request", evidence.Request);
        store.Put("process", evidence.Process);
        store.Put("decision", evidence.Decision);
    }

    static (EntityWriteRequest Write, System.Collections.Immutable.ImmutableArray<InteractionEnvelope> Envelopes, EntityTransitionOperationCommit? Operation) Replay(SqliteProofStore store, bool direct)
    {
        var before = store.Get<EntitySnapshot>("before");
        var request = store.Get<EntityTransitionOperationRequest>("request");
        var process = store.Get<ExecutionDefinitionReference>("process");
        var catalog = store.LoadCatalog();
        RunControlFixture.Require(catalog.ValidateReference(request.Transition, "/transition", out var transitionDocument));
        RunControlFixture.Require(catalog.ValidateReference(process, "/process", out var processDocument));
        var compilation = TransitionStaticCompiler.Compile(transitionDocument!);
        RunControlFixture.Require(compilation.Validation);
        var plan = compilation.Plan!;
        // This fixture is intentionally a flat Transition, not a speculative general dependency traversal.
        var eventReference = plan.Definition.Body.Steps.OfType<EmitTransitionNode>().Single().Contract;
        RunControlFixture.Require(catalog.ValidateReference(eventReference, "/emission/contract", out var eventDocument));
        RunControlFixture.Require(InteractionContractCatalog.TryCreate([eventDocument!], out var contracts));
        var processCompilation = ProcessStaticCompiler.Compile(processDocument!, new(definitions:
            [new(request.Transition, ProcessDefinitionLinkKind.Transition, plan.Definition.Input, plan.Definition.Outcome)]));
        RunControlFixture.Require(processCompilation.Validation);
        var decision = RunControlFixture.Decide(plan, before, request);
        if (!store.GetBytes("decision").AsSpan().SequenceEqual(StrictDocumentJson.GetCanonicalBytes(decision, SqliteProofStore.Json)))
            throw new InvalidOperationException("Replayed decision differs from its retained patches, outcome, or emission intents.");
        var evidence = new RunReplayEvidence(before, request, process, decision);
        var processEnvelopes = RunControlFixture.Lower(evidence, decision, contracts!);
        var commit = RunControlFixture.Commit(evidence, decision, processEnvelopes);
        return (commit.Write, direct ? RunControlFixture.Lower(evidence, decision, contracts!, direct: true) : processEnvelopes, direct ? null : commit);
    }
}
