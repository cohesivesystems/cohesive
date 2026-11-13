using System.Text.Json;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Machines;

public sealed class EntityMachineBindingTests
{
    static readonly TenderState Unlinked = new("", false, false, false, "");
    static readonly TenderState Offered = Unlinked with { LoadId = "load-1" };
    static readonly TenderState Pending = Offered with { Accepted = true, PendingReceipt = true };

    [Fact]
    public void Tender_realizes_linkage_acceptance_confirmation_and_preservation()
    {
        var model = new TenderEntityModel();
        var linked = Run(model.Link, Unlinked, new LinkLoad("load-1"));
        Assert.Equal(TransitionDecisionKind.Applied, linked.Kind);
        Assert.Equal(model.Machine.AttachLoad.Id, Assert.Single(linked.MachineMovements).Edge.Value);
        var accepted = Run(model.AcceptPending, Offered, new SendResult(false));
        Assert.Equal(TransitionDecisionKind.Applied, accepted.Kind);
        Assert.Equal(model.Machine.SendPending.Id, Assert.Single(accepted.MachineMovements).Edge.Value);
        var confirmed = Run(model.ConfirmReceipt, Pending, new Receipt(true, true));
        Assert.Equal(TransitionDecisionKind.Applied, confirmed.Kind);
        Assert.Equal(model.Machine.ReceiptConfirmed.Id, Assert.Single(confirmed.MachineMovements).Edge.Value);
        Assert.Equal(TransitionDecisionKind.Applied, Run(model.AcceptConfirmed, Offered, new SendResult(true)).Kind);
        var edited = Run(model.EditNote, Pending, new UpdateNote("Checked documents"));
        Assert.Equal(TransitionDecisionKind.Applied, edited.Kind);
        Assert.Empty(edited.MachineMovements);
        Assert.Single(edited.Patch);
    }

    [Fact]
    public void Guard_and_wrong_source_rejections_have_no_commit_artifacts()
    {
        var model = new TenderEntityModel();
        foreach (var decision in new[] {
            Run(model.AcceptPending, Unlinked, new SendResult(false)),
            Run(model.AcceptPending, Offered, new SendResult(false, LinkedLoadExists: false)),
            Run(model.ConfirmReceipt, Pending, new Receipt(true, false)) })
        {
            Assert.Equal(TransitionDecisionKind.AdmissionRejected, decision.Kind);
            Assert.Empty(decision.Patch);
            Assert.Empty(decision.MachineMovements);
            Assert.Empty(decision.Emissions);
        }
    }

    [Fact]
    public void Unaccounted_successful_operation_is_rejected_by_compiler()
    {
        var model = new TenderEntityModel();
        var transition = Author(model.Entity, t => t.Set(x => x.Accepted, true)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        var compiled = transition.Compile();
        Assert.False(compiled.IsSuccessful);
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == TransitionCompilationDiagnosticCodes.EntityMachineCoverage);
    }

    [Fact]
    public void Preservation_cannot_change_classification()
    {
        var model = new TenderEntityModel();
        var transition = Author(model.Entity, t => t.PreserveMachineState().Set(x => x.Accepted, true)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        var decision = Run(transition, Offered, new UpdateNote(""));
        AssertInvalid(decision, TransitionExecutionDiagnosticCodes.MachinePreservationViolated);
    }

    [Fact]
    public void Movement_must_establish_its_final_target()
    {
        var model = new TenderEntityModel();
        var transition = TransitionAuthoring.Create<TenderState, Receipt, string>(model.Entity.Shape,
            new("broken-confirm"), new("1"), t => t
                .Move(model.Machine.ReceiptConfirmed, (entity, input) => "Rejected")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Forgot patch"));
        AssertInvalid(Run(transition, Pending, new Receipt(true, true)), TransitionExecutionDiagnosticCodes.MachineTargetViolated);
    }

    [Fact]
    public void Uncovered_initial_and_final_snapshots_are_invalid()
    {
        var model = new TenderEntityModel();
        AssertInvalid(Run(model.EditNote, Unlinked with { Accepted = true }, new UpdateNote("")),
            TransitionExecutionDiagnosticCodes.MachineClassificationInvalid);
        var invalidFinal = Author(model.Entity, t => t.PreserveMachineState().Set(x => x.LoadId, "")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        AssertInvalid(Run(invalidFinal, Pending, new UpdateNote("")), TransitionExecutionDiagnosticCodes.MachineClassificationInvalid);
    }

    [Fact]
    public void Overlapping_classification_is_invalid_even_for_preservation()
    {
        var model = new TenderEntityModel();
        var binding = new MachinePredicateBinding<TenderState, UpdateNote>(model.Machine.Definition)
            .State(model.Machine.Unlinked, x => !x.Accepted)
            .State(model.Machine.Offered, x => !x.Accepted)
            .State(model.Machine.Pending, x => x.Accepted && x.PendingReceipt)
            .State(model.Machine.Confirmed, x => x.Accepted && !x.PendingReceipt);
        var entity = binding.Bind(ObjectEntityDefinition.For<TenderState>(new("overlapping")));
        var transition = Author(entity, t => t.PreserveMachineState()
            .Return(new("result"), TransitionOutcomeDisposition.NoChange, "Unchanged"));
        AssertInvalid(Run(transition, Offered, new UpdateNote("")), TransitionExecutionDiagnosticCodes.MachineClassificationInvalid);
    }

    [Fact]
    public void Second_binding_and_foreign_movement_are_rejected()
    {
        var model = new TenderEntityModel();
        var second = new MachineDiscriminatorBinding<TenderState, string>(model.Machine.Definition, t => t.Note)
            .Map(model.Machine.Unlinked, "a").Map(model.Machine.Offered, "b")
            .Map(model.Machine.Pending, "c").Map(model.Machine.Confirmed, "d");
        Assert.Throws<InvalidOperationException>(() => second.Bind(model.Entity));
        var foreign = TenderAcceptanceMachines.LoadAcknowledgement();
        var transition = Author(model.Entity, t => t.Move(foreign.Confirm, (entity, input) => "Rejected")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        Assert.False(transition.Compile().IsSuccessful);
    }

    [Fact]
    public void Preservation_cannot_be_combined_with_named_movement()
    {
        var model = new TenderEntityModel();
        var transition = Author(model.Entity, t => t.PreserveMachineState()
            .Move(model.Machine.AttachLoad, (entity, input) => "Rejected")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        Assert.False(transition.Compile().IsSuccessful);
    }

    [Fact]
    public void Binding_and_interpretation_survive_serialization_without_external_catalog()
    {
        var model = new TenderEntityModel();
        var json = ExecutionDefinitionJsonSerializer.CreateOptions();
        var entity = JsonSerializer.Deserialize<EntityDefinition>(JsonSerializer.Serialize(model.Entity, json), json)!;
        var document = ExecutionDefinitionJsonSerializer.Deserialize(
            ExecutionDefinitionJsonSerializer.Serialize(model.EditNote.Document));
        var compiled = TransitionStaticCompiler.Compile(document);
        Assert.True(compiled.IsSuccessful, Diagnostics(compiled));
        EntityMachineBinding.RequireSame(entity, compiled.Definition!.MachineBinding);
        Assert.Equal(EntityMachineEffect.Preserve, compiled.Definition.MachineEffect);
        Assert.Equal(model.EditNote.Definition, compiled.Definition);
        Assert.Equal(model.EditNote.Definition.GetHashCode(), compiled.Definition.GetHashCode());
        var unbound = Author(ObjectEntityDefinition.For<TenderState>(), t => t
            .Return(new("result"), TransitionOutcomeDisposition.NoChange, "Unchanged"));
        Assert.Throws<InvalidOperationException>(() => EntityMachineBinding.RequireSame(entity, unbound.Definition.MachineBinding));
        var restored = RunPlan(compiled.Plan!, Pending, new UpdateNote("Restored"));
        Assert.Equal(TransitionDecisionKind.Applied, restored.Kind);
    }

    [Fact]
    public void An_uncovered_successful_branch_is_rejected()
    {
        var model = new TenderEntityModel();
        var transition = Author(model.Entity, t => t.Choose(new("choice"), choice => choice
            .Case(new("link"), (entity, input) => input.Note != "",
                branch => branch.Move(model.Machine.AttachLoad, (entity, input) => "Rejected")
                    .Set(entity => entity.LoadId, "load-1")
                    .Return(new("linked"), TransitionOutcomeDisposition.Applied, "Linked"))
            .Fallback(new("bypass"), branch => branch
                .Return(new("unchanged"), TransitionOutcomeDisposition.NoChange, "Unchanged"))));
        var compiled = transition.Compile();
        Assert.False(compiled.IsSuccessful);
        Assert.Contains(compiled.Validation.Diagnostics, diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.EntityMachineCoverage);
    }

    [Fact]
    public void Guarded_self_loop_is_a_movement_not_preservation()
    {
        var source = Machine.Define("guarded-loop", "1");
        var ready = source.State("ready").Initial();
        var record = source.Edge("record").From(ready).To(ready)
            .When((TenderState entity, UpdateNote input) => input.Note != "");
        var entity = new MachinePredicateBinding<TenderState, UpdateNote>(source.Build())
            .State(ready, entity => true).Bind(ObjectEntityDefinition.For<TenderState>());
        var transition = Author(entity, t => t.Move(record, (entity, input) => "Rejected")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Recorded"));
        var rejected = Run(transition, Offered, new UpdateNote(""));
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, rejected.Kind);
        Assert.Empty(rejected.MachineMovements);
        var accepted = Run(transition, Offered, new UpdateNote("Evidence"));
        Assert.Equal(TransitionDecisionKind.Applied, accepted.Kind);
        Assert.Single(accepted.MachineMovements);
        Assert.Empty(accepted.Patch);
    }

    [Fact]
    public void Discriminator_binding_assigns_state_without_an_external_catalog()
    {
        var source = Machine.Define("discriminator", "1");
        var ready = source.State("ready").Initial();
        var done = source.State("done").Final();
        var finish = source.Edge("finish").From(ready).To(done);
        var entity = new MachineDiscriminatorBinding<TenderState, string>(source.Build(), entity => entity.Note)
            .Map(ready, "ready").Map(done, "done").Bind(ObjectEntityDefinition.For<TenderState>());
        var transition = Author(entity, t => t.Move(finish, (entity, input) => "Rejected")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Finished"));
        var decision = Run(transition, Offered with { Note = "ready" }, new UpdateNote(""));
        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Single(decision.Patch);
        var original = entity.CreateState("tender-1", Offered with { Note = "ready" });
        var next = TransitionStateProjector.ApplyToEntity(entity, "tender-1", decision, original.Snapshot);
        Assert.Equal("done", next.Fields["Note"].GetString());
    }

    [Fact]
    public void Registration_and_state_preparation_reject_missing_binding_evidence()
    {
        var model = new TenderEntityModel();
        var unbound = Author(ObjectEntityDefinition.For<TenderState>(), t => t.Set(entity => entity.Accepted, true)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Bypass"));
        var plan = unbound.Compile().Plan!;
        Assert.Throws<InvalidOperationException>(() => new Cohesive.Api.Execution.Services.ServiceTransitionBinding(
            "bypass", plan, model.Entity, _ => throw new InvalidOperationException("Must not resolve")));
        var decision = RunPlan(plan, Offered, new UpdateNote(""));
        var state = model.Entity.CreateState("tender-1", Offered);
        var failure = Assert.Throws<TransitionStatePreparationException>(() =>
            TransitionStateProjector.ApplyToEntity(model.Entity, "tender-1", decision, state.Snapshot));
        Assert.Contains("machine", failure.Message, StringComparison.OrdinalIgnoreCase);
        var invalid = model.Entity.CreateState("tender-1", Unlinked with { Accepted = true });
        Assert.Throws<SemanticRuleViolationException>(() => model.Entity.ValidateState(invalid));
    }

    [Fact]
    public void Bound_creation_fails_explicitly_until_initialization_is_supported()
    {
        var model = new TenderEntityModel();
        var transition = TransitionAuthoring.Create<TenderState, TenderState, string>(model.Entity.Shape,
            new("create"), new("1"), t => t.CreatesFrom(new("initialize"), input => new TenderState(input.LoadId, input.Accepted, input.PendingReceipt, input.Cancelled, input.Note))
                .PreserveMachineState().Return(new("result"), TransitionOutcomeDisposition.Applied, "Created"));
        var compiled = transition.Compile();
        Assert.False(compiled.IsSuccessful);
        Assert.Contains(compiled.Validation.Diagnostics, diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.EntityMachineCoverage
            && diagnostic.Message.Contains("creation"));
    }

    public sealed record Calculation(long Raw, long Total);

    [Fact]
    public void Derived_changes_are_classified_and_graph_authority_cannot_be_omitted()
    {
        var source = Machine.Define("calculation", "1");
        var low = source.State("low").Initial();
        var high = source.State("high").Final();
        source.Edge("increase").From(low).To(high);
        var shape = new Shape(new("calculation"), [
            new(new FieldName(nameof(Calculation.Raw)), new ScalarTypeRef(ScalarTypeKind.Int64)),
            new(new FieldName(nameof(Calculation.Total)), new ScalarTypeRef(ScalarTypeKind.Int64),
                role: FieldRole.Computed, mutability: FieldMutability.Computed,
                compute: new(Expr.Add(Expr.Field(nameof(Calculation.Raw)), Expr.Const(1L))))], role: ShapeRoles.Entity);
        var entity = new MachinePredicateBinding<Calculation, UpdateNote>(source.Build())
            .State(low, value => value.Total < 10).State(high, value => value.Total >= 10)
            .Bind(new EntityDefinition(new("calculation"), shape));
        var graph = new ShapeGraph(new("calculation-graph"), [entity.Shape]);
        var transition = TransitionAuthoring.Create<Calculation, UpdateNote, string>(entity.Shape,
            new("change-raw"), new("1"), t => t.PreserveMachineState().Set(value => value.Raw, 20L)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Changed"));
        var authored = transition.Definition;
        var observation = ValueContract.FromShape(entity.Shape, graph.Qualify(entity.Shape.Id));
        ExecutionDefinitionDocument Document(EntityMachineBinding? binding) => TransitionDefinitionDocuments.Create(
            new("qualified"), new("1"), new(authored.Input, observation, authored.Outcome,
                authored.Preconditions, authored.Body, machineBinding: binding, machineEffect: authored.MachineEffect),
            transition.Document.Metadata.Provenance);
        Assert.False(TransitionStaticCompiler.Compile(Document(null), graph).IsSuccessful);
        var compiled = TransitionStaticCompiler.Compile(Document(authored.MachineBinding), graph);
        Assert.True(compiled.IsSuccessful, Diagnostics(compiled));
        var decision = TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("derived"),
            PortableValue.Concrete(authored.Input, ObservationValue.FromObject(new UpdateNote(""))),
            PortableValue.Concrete(observation, ObservationValue.FromObject(new Calculation(1, 2))));
        AssertInvalid(decision, TransitionExecutionDiagnosticCodes.MachinePreservationViolated);
    }

    [Fact]
    public void Missing_sparse_classification_evidence_is_not_a_definition_error()
    {
        var model = new TenderEntityModel();
        var plan = model.EditNote.Compile().Plan!;
        var decision = TransitionReferenceInterpreter.DecideSparse(plan, new("missing-observation"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new UpdateNote("Changed"))),
            observations: []);
        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, decision.Kind);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.ObservationUnavailable);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.MachineMovements);
    }

    static Transition<TenderState, UpdateNote, string> Author(EntityDefinition entity,
        Action<TransitionBuilder<TenderState, UpdateNote, string>> configure) =>
        TransitionAuthoring.Create<TenderState, UpdateNote, string>(entity.Shape, new("test"), new("1"), configure);

    static TransitionDecision Run<TInput>(Transition<TenderState, TInput, string> transition, TenderState state, TInput input)
    {
        var compiled = transition.Compile();
        Assert.True(compiled.IsSuccessful, Diagnostics(compiled));
        return RunPlan(compiled.Plan!, state, input);
    }

    static TransitionDecision RunPlan<TInput>(CompiledTransitionPlan plan, TenderState state, TInput input) =>
        TransitionReferenceInterpreter.DecideFullState(plan, new("test-activation"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(input)),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(state)));

    static string Diagnostics(TransitionCompilationResult result) => string.Join("; ", result.Validation.Diagnostics.Select(d => d.Message));

    static void AssertInvalid(TransitionDecision decision, string code)
    {
        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.MachineMovements);
    }
}
