using Cohesive.Model.Serialization;
using Cohesive.Model.Authoring;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Machines;

public sealed class GuardedDiscriminatorTests
{
    [JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
    public enum OrderStatus { Draft, Submitted }

    public sealed record Order(OrderStatus Status, int ItemCount, string SubmittedBy);
    public sealed record SubmitOrder(string Actor, bool AcceptTerms);

    [Theory]
    [InlineData(OrderStatus.Draft, 1, true, false, true)]
    [InlineData(OrderStatus.Draft, 0, true, false, false)]
    [InlineData(OrderStatus.Draft, 1, false, false, false)]
    [InlineData(OrderStatus.Submitted, 1, true, false, false)]
    [InlineData(OrderStatus.Submitted, 2, true, true, false)]
    [InlineData(OrderStatus.Draft, 1, true, true, true)]
    [InlineData(OrderStatus.Draft, 2, true, true, false)]
    public void GuardedMovementOwnsPreconditionsAndDiscriminatorAssignment(
        OrderStatus status, int itemCount, bool acceptTerms, bool competingEdge, bool accepted)
    {
        var source = Machine.Define("order.lifecycle", revision: "1");
        var draft = source.State("draft").Initial();
        var submitted = source.State("submitted").Final();
        var submit = source.Edge("submit").From(draft).To(submitted)
            .When((Order order, SubmitOrder command) => order.ItemCount > 0 && command.AcceptTerms);
        if (competingEdge)
            source.Edge("alternate-submit").On("submit").From(draft).To(submitted)
                .When((Order order, SubmitOrder command) => order.ItemCount >= 2 && command.AcceptTerms);
        var lifecycle = source.Build();
        var links = new MachineDiscriminatorBinding<Order, OrderStatus>(lifecycle, order => order.Status)
            .Map(draft, OrderStatus.Draft).Map(submitted, OrderStatus.Submitted).Build();
        Assert.Equal(competingEdge, links.Diagnostics.Any(d => d.Code == "machines.linking.disjointnessUnknown"));
        var entity = ObjectEntityDefinition.For<Order>(new("order"));
        var transition = TransitionAuthoring.Create<Order, SubmitOrder, string>(
            entity.Shape, new("order.submit"), new("1"),
            t => t.Move(submit, rejection: (order, command) => "Cannot submit")
                .Set(new("record-submitter"), order => order.SubmittedBy, (order, command) => command.Actor)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"));
        var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
        Assert.True(compiled.IsSuccessful, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
        var order = new Order(status, itemCount, "");
        var command = new SubmitOrder("alice", acceptTerms);
        var facts = ObservationValue.FromObject(order);
        var input = ObservationValue.FromObject(command);
        var standalone = lifecycle.Evaluate(status == OrderStatus.Draft ? "draft" : "submitted", "submit", facts, input);
        Assert.Equal(accepted, standalone.Kind == MachineDecisionKind.Accepted);
        if (status == OrderStatus.Draft && competingEdge && itemCount >= 2)
            Assert.Equal(MachineDecisionKind.Ambiguous, standalone.Kind);
        var decision = TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("operation"),
            PortableValue.Concrete(transition.Definition.Input, input),
            PortableValue.Concrete(transition.Definition.Observation, facts));
        var ambiguous = standalone.Kind == MachineDecisionKind.Ambiguous;
        Assert.Equal(accepted ? TransitionDecisionKind.Applied
            : ambiguous ? TransitionDecisionKind.InvalidDefinition : TransitionDecisionKind.AdmissionRejected, decision.Kind);
        Assert.Equal(ambiguous, decision.Diagnostics.Any(d => d.Code == TransitionExecutionDiagnosticCodes.MachineAdmissionAmbiguous));
        if (accepted)
        {
            var original = entity.CreateState("order-1", order, version: 1);
            var candidate = TransitionStateProjector.ApplyToEntity(entity, "order-1", decision, original.Snapshot);
            Assert.Equal("Submitted", candidate.Fields["Status"].GetString());
            Assert.Equal("alice", candidate.Fields["SubmittedBy"].GetString());
            Assert.Single(decision.MachineMovements);
            Assert.Equal("Draft", original.Fields["Status"].GetString());
        }
        else
        {
            Assert.Empty(decision.Patch);
            Assert.Empty(decision.MachineMovements);
            Assert.Empty(decision.Emissions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledRequestedEdgeStillReportsAmbiguousSiblings(bool predicateBinding)
    {
        var source = Machine.Define("ambiguous-order", "1");
        var draft = source.State("draft").Initial();
        var done = source.State("done").Final();
        source.Edge("first").On("submit").From(draft).To(done)
            .When((Order order, SubmitOrder command) => command.AcceptTerms);
        source.Edge("second").On("submit").From(draft).To(done)
            .When((Order order, SubmitOrder command) => order.ItemCount > 0);
        var disabled = source.Edge("disabled").On("submit").From(draft).To(done)
            .When((Order order, SubmitOrder command) => order.ItemCount > 100);
        var machine = source.Build();
        var links = predicateBinding
            ? new MachinePredicateBinding<Order, SubmitOrder>(machine)
                .State(draft, order => order.Status == OrderStatus.Draft)
                .State(done, order => order.Status == OrderStatus.Submitted).Build()
            : new MachineDiscriminatorBinding<Order, OrderStatus>(machine, order => order.Status)
                .Map(draft, OrderStatus.Draft).Map(done, OrderStatus.Submitted).Build();
        // Persisted linker evidence retains the explicit ambiguity obligation.
        var restored = System.Text.Json.JsonSerializer.Deserialize<TransitionMachineEdgeLink>(
            System.Text.Json.JsonSerializer.Serialize(links.Edges[0]))!;
        Assert.Equal(links.Edges[0].CreateAmbiguousConfiguration(), restored.CreateAmbiguousConfiguration());
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(links.Edges[0]))!.AsObject();
        legacy.Remove("AdmissionGuards");
        legacy["AmbiguousConfiguration"] = true;
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<TransitionMachineEdgeLink>(legacy.ToJsonString()));
        var entity = ObjectEntityDefinition.For<Order>(new("order"));
        var transition = TransitionAuthoring.Create<Order, SubmitOrder, string>(entity.Shape,
            new("disabled-request"), new("1"), t => t
                .Move(disabled, rejection: (order, command) => "ordinary rejection")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "submitted"));
        var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
        Assert.True(compiled.IsSuccessful, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
        var decision = TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("test"),
            PortableValue.Concrete(transition.Definition.Input, ObservationValue.FromObject(new SubmitOrder("alice", true))),
            PortableValue.Concrete(transition.Definition.Observation, ObservationValue.FromObject(new Order(OrderStatus.Draft, 1, ""))));
        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Contains(decision.Diagnostics, d => d.Code == TransitionExecutionDiagnosticCodes.MachineAdmissionAmbiguous);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.MachineMovements);
    }

    [Fact]
    public void ConfiguredContractsAreSharedWithEntityAndTransitionAuthoring()
    {
        var mapper = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef>
        {
            [typeof(OrderStatus)] = new EnumTypeRef("custom-order-status", ["Draft", "Submitted"])
        });
        var source = Machine.Define("configured", "1");
        var draft = source.State("draft").Initial();
        var done = source.State("done").Final();
        var submit = source.Edge("submit").From(draft).To(done)
            .When((Order order, SubmitOrder input) => order.ItemCount > 0, typeRefMapper: mapper);
        var machine = source.Build();
        var links = new MachineDiscriminatorBinding<Order, OrderStatus>(machine, order => order.Status, typeRefMapper: mapper)
            .Map(draft, OrderStatus.Draft).Map(done, OrderStatus.Submitted).Build();
        var entity = ObjectEntityDefinition.For<Order>(new("order"), typeRefMapper: mapper);
        var metadata = new TransitionAuthoringMetadata(new("configured.submit"), new("1"), new("body"),
            new(new(TransitionAuthoring.Producer), new("tests/configured"), DocumentOrigin.User));
        var transition = TransitionAuthoring.Create<Order, SubmitOrder, string>(entity.Shape, metadata,
            t => t.Move(submit, rejection: (order, input) => "rejected")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "submitted"), typeRefMapper: mapper);
        var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
        Assert.True(compiled.IsSuccessful, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
        Assert.Equal(ValueContract.FromField(entity.Shape.Fields.Single(f => f.Name.Value == "Status")),
            links.Edges[0].Assignments[0].Value.Contract);
        Assert.Equal(TransitionDecisionKind.Applied, TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("test"),
            PortableValue.Concrete(transition.Definition.Input, ObservationValue.FromObject(new SubmitOrder("alice", true))),
            PortableValue.Concrete(transition.Definition.Observation, ObservationValue.FromObject(new Order(OrderStatus.Draft, 1, "")))).Kind);
    }
}
