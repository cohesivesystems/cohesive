using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Model;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.ExecutionKernel.TestFixtures.Storage;

/// <summary>Ordinary immutable control record; numeric/time/byte fields also exercise portable storage encodings.</summary>
/// <param name="Id">Stable control identity.</param>
/// <param name="Tenant">Logical partition.</param>
/// <param name="Status">Scheduled, running, or completed lifecycle state.</param>
/// <param name="Attempt">Logical start attempt count.</param>
/// <param name="Enabled">Admission switch.</param>
/// <param name="Limit">Exact decimal control value with no financial interpretation.</param>
/// <param name="ScheduledAt">Pinned UTC schedule time.</param>
/// <param name="InputDigest">Pinned bytes copied into canonical observations at the authoring boundary.</param>
public sealed record RunControl(string Id, string Tenant, string Status, long Attempt, bool Enabled,
    decimal Limit, DateTimeOffset ScheduledAt, byte[] InputDigest);

/// <summary>Typed start intent.</summary>
/// <param name="ExpectedAttempt">Attempt observed by the coordinator.</param>
public sealed record StartRun(long ExpectedAttempt);

/// <summary>Persistable inputs and reference decision for replay; it is not evidence that a commit succeeded.</summary>
/// <param name="Before">Exact prior state and CAS fence.</param>
/// <param name="Request">Original Process occurrence and materialized input.</param>
/// <param name="Process">Exact publication-coordinator definition.</param>
/// <param name="Decision">Original pure decision, including ordered patches and emission intents.</param>
public sealed record RunReplayEvidence(EntitySnapshot Before, EntityTransitionOperationRequest Request,
    ExecutionDefinitionReference Process, TransitionDecision Decision);

/// <summary>Reusable authoring and replay inputs shared by storage conformance and the SQLite adoption proof.</summary>
public static class RunControlFixture
{
    /// <summary>Canonical entity shape projected from the POCO once.</summary>
    public static EntityDefinition Entity { get; } = ObjectEntityDefinition.For<RunControl>(new("adoption/run-control"));
    /// <summary>Fixture revision, explicitly independent of source-control or assembly versions.</summary>
    public static ExecutionRevisionId Revision { get; } = new("revision/1");
    /// <summary>Canonical string outcome/event contract.</summary>
    public static ValueContract StringContract { get; } = new(new ScalarTypeRef(ScalarTypeKind.String));
    /// <summary>Exact event definition consumed by Transition emission lowering.</summary>
    public static ExecutionDefinitionDocument EventDocument { get; } = InteractionContractDocuments.Create(
        new("adoption/event/run-started"), Revision,
        new DomainEventContractDefinition(new(StringContract, new("run-started/v1"))), Provenance);
    /// <summary>Materialized POCO-authored Transition document.</summary>
    public static Transition<RunControl, StartRun, string> Start { get; } = TransitionAuthoring.Create<RunControl, StartRun, string>(
        Entity.Shape, new(new("adoption/transition/start-run"), Revision, new("body"), Provenance), transition => transition
            .Requires(new("enabled"), (state, input) => state.Enabled, (state, input) => "disabled")
            .Requires(new("scheduled"), (state, input) => state.Status == "scheduled", (state, input) => "already-started")
            .Requires(new("attempt"), (state, input) => state.Attempt == input.ExpectedAttempt, (state, input) => "stale-attempt")
            .Set(new("running"), state => state.Status, "running")
            .Set(new("increment"), state => state.Attempt, (state, input) => state.Attempt + 1)
            .Emit(new("emit-started"), Reference(EventDocument), (state, input) => state.Id)
            .Return(new("started"), TransitionOutcomeDisposition.Applied, (state, input) => "started"));
    /// <summary>Exact Process caller definition; the proof persists it but does not introduce a Process engine.</summary>
    public static ExecutionDefinitionDocument ProcessDocument { get; } = ProcessDefinitionDocuments.Create(
        new("adoption/process/run"), Revision,
        new ProcessDefinition(Start.Definition.Input, StringContract, new("invoke"),
        [
            new InvokeTransitionProcessNode(new("invoke"), Start.Reference, Expr.Const("run/1"),
                Expr.BoundValue(ProcessBindingIds.Input), new(new ProcessEdge(new("invoked"), new("return")))),
            new ReturnProcessNode(new("return"), Expr.Const("completed"))
        ], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
    /// <summary>Complete execution-definition dependency closure for this fixture's inline value contracts.</summary>
    public static ImmutableArray<ExecutionDefinitionDocument> Documents => [EventDocument, Start.Document, ProcessDocument];
    /// <summary>Stable fixture attribution used by documents and envelope policies.</summary>
    public static ExecutionProvenance Provenance => new(new("cohesive-adoption-fixture", "1"), new("fixtures/storage/run-control"), DocumentOrigin.Generated);

    /// <summary>Creates a representative scalar control record.</summary>
    /// <param name="id">Entity identity.</param>
    /// <param name="tenant">Logical partition.</param>
    /// <returns>A new immutable record with independently owned digest bytes.</returns>
    public static RunControl Initial(string id = "run/1", string tenant = "tenant/a") =>
        new(id, tenant, "scheduled", 0, true, 123.4500m, new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), [0, 1, 127, 255]);

    /// <summary>Projects a POCO into the canonical repository write contract.</summary>
    /// <param name="value">Complete record.</param>
    /// <param name="version">Semantic observation version.</param>
    /// <param name="token">Optional expected storage fence.</param>
    /// <returns>A validated observation write.</returns>
    public static EntityWriteRequest Write(RunControl value, long version = 0, EntityConcurrencyToken? token = null) =>
        new(Entity.CreateState(value.Id, ObservationValue.FromObject(value).Fields!, version).Snapshot, token);

    /// <summary>Prepares replay inputs by executing the authored canonical document once.</summary>
    /// <param name="before">Persisted prior state.</param>
    /// <param name="occurrence">Stable Process occurrence index.</param>
    /// <returns>Explicit data sufficient to re-evaluate the decision without re-authoring it.</returns>
    public static RunReplayEvidence Prepare(EntitySnapshot before, int occurrence = 0)
    {
        var compilation = Start.Compile();
        Require(compilation.Validation);
        var plan = compilation.Plan!;
        var operation = new ProcessOperationOccurrence(new(new("adoption/run"), new("attempt/1")), new($"activation/{occurrence}"),
            new("token/start"), new("invoke"), occurrence);
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new StartRun(0)));
        var request = new EntityTransitionOperationRequest(operation, new("adoption", before.PartitionKey), plan.DefinitionReference,
            new(new(Entity.Shape.Id.Value), before.Entity.EntityId), input);
        return new(before, request, Reference(ProcessDocument), Decide(plan, before, request));
    }

    /// <summary>Purely re-evaluates explicit prior state/input against a compiled pinned document.</summary>
    /// <param name="plan">Plan compiled from the resolved persisted document.</param>
    /// <param name="before">Exact prior observation.</param>
    /// <param name="request">Original activation and input.</param>
    /// <returns>A deterministic decision with no repository access or external effects.</returns>
    public static TransitionDecision Decide(CompiledTransitionPlan plan, EntitySnapshot before, EntityTransitionOperationRequest request) =>
        TransitionReferenceInterpreter.DecideFullState(plan, request.Operation.Activation, request.Input,
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(before.Entity.Observation.Fields)));

    /// <summary>Lowers a decision with stable canonical identities and the requested publication origin.</summary>
    /// <param name="evidence">Persisted evaluation inputs and coordinator reference.</param>
    /// <param name="decision">Original or re-evaluated decision.</param>
    /// <param name="contracts">Interaction contracts resolved from pinned documents.</param>
    /// <param name="direct">Whether publication belongs to the direct entity outbox instead of the invoking Process.</param>
    /// <returns>Exact ordered canonical envelopes.</returns>
    public static ImmutableArray<InteractionEnvelope> Lower(RunReplayEvidence evidence, TransitionDecision decision,
        InteractionContractCatalog contracts, bool direct = false)
    {
        var request = evidence.Request;
        var outcome = decision.Evidence.Trace.Last(trace => trace.Kind == TransitionTraceEventKind.OutcomeReturned).Node;
        var policy = new TransitionEmissionLoweringPolicy((intent, _) => new(
            new($"emission:{request.Fingerprint.Value}:{intent.Node.Value}:{(direct ? "direct" : "process")}"),
            direct ? new TransitionInteractionOrigin(request.Transition, intent.Node, request.Subject, outcome)
                : new ProcessInteractionOrigin(evidence.Process, request.Operation.Node, request.Operation.Continuation,
                    request.Operation.Activation, request.Operation.Token, request.Subject, request.Transition, outcome, intent.Node),
            new("correlation/run-1"), causationId: null, request.AuthorityScope,
            new($"idempotency/{request.Fingerprint.Value}/{intent.Node.Value}"), ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Provenance));
        Require(TransitionEmissionEnvelopeLowerer.TryLower(decision, contracts, policy, out var envelopes));
        return envelopes;
    }

    /// <summary>Projects pure patches and lowered envelopes into an atomic Process handoff commit.</summary>
    /// <param name="evidence">Original evaluation inputs.</param>
    /// <param name="decision">Validated decision.</param>
    /// <param name="envelopes">Lowered Process-origin envelopes.</param>
    /// <returns>A canonical atomic commit request guarded by the original storage token.</returns>
    public static EntityTransitionOperationCommit Commit(RunReplayEvidence evidence, TransitionDecision decision, ImmutableArray<InteractionEnvelope> envelopes)
    {
        var candidate = TransitionStateProjector.Apply(ObservationValue.FromObject(evidence.Before.Entity.Observation.Fields), decision);
        var state = Entity.CreateState(evidence.Before.Entity.EntityId.Value, candidate.Fields!, evidence.Before.Entity.Version + 1).Snapshot;
        return new(evidence.Request, new(state, evidence.Before.ConcurrencyToken), decision.Kind,
            ProcessOperationResult.Completed(decision.Outcome!, envelopes), decision.GuaranteeDemands, decision.Evidence);
    }

    /// <summary>Builds the validated event catalog used by shared conformance probes.</summary>
    /// <returns>The exact event contract catalog.</returns>
    public static InteractionContractCatalog Contracts()
    {
        Require(InteractionContractCatalog.TryCreate([EventDocument], out var contracts));
        return contracts!;
    }

    /// <summary>Projects one complete exact document reference without inventing identity components.</summary>
    /// <param name="document">Canonical fingerprinted document.</param>
    /// <returns>Its exact identity, revision, and fingerprint.</returns>
    public static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);

    /// <summary>Fails a fixture setup when canonical validation reports errors.</summary>
    /// <param name="validation">Structured canonical diagnostics.</param>
    /// <exception cref="InvalidOperationException">Validation failed; the message retains diagnostic codes and locations.</exception>
    public static void Require(DocumentValidationResult validation)
    {
        if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Diagnostics.Select(d => $"{d.Code} {d.Location}: {d.Message}")));
    }
}
