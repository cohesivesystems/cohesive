using Cohesive.Machines;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.Machines;

public class ExpandedMachineTests
{
    public sealed record Facts(int Count);
    public sealed record Command(bool Allowed);
    public sealed record Order(string Status);
    static ObservationValue FactsValue(int count) => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Count"] = ObservationValue.FromInt64(count) });
    static ObservationValue CommandValue(bool allowed) => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Allowed"] = ObservationValue.FromBool(allowed) });

    [Fact]
    public void PublicAuthoringHasOneEntryAndGuardAllocationsDoNotScaleWithCatalogSize()
    {
        Assert.DoesNotContain(typeof(MachineDefinition).Assembly.GetExportedTypes(), type => type.Name == "MachineBuilder");
        Assert.DoesNotContain(typeof(MachineGuardAuthoring).GetMethods(), method => method.Name == "From");
        MachineDefinition Create(int count)
        {
            var source = Machine.Define("allocation", "1");
            var ready = source.State("ready").Initial();
            for (var index = 0; index < count; index++)
                source.Edge("action-" + index).From(ready).To(ready)
                    .When((Facts facts, Command input) => facts.Count > 0 && input.Allowed);
            Assert.Empty(source.Validate());
            return source.Build();
        }
        var facts = FactsValue(2);
        var input = CommandValue(true);
        const int measuredCalls = 1000;
        const int allocationBudgetPerCall = 1024;
        const int catalogGrowthBudgetPerCall = 8;
        long Measure(MachineDefinition machine, string action)
        {
            for (var i = 0; i < measuredCalls; i++) machine.Evaluate("ready", action, facts, input);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < measuredCalls; i++) machine.Evaluate("ready", action, facts, input);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long minimum = long.MaxValue;
        long maximum = 0;
        foreach (var count in new[] { 1, 16, 128, 1024 })
        {
            var allocated = Measure(Create(count), "action-" + (count - 1));
            Assert.InRange(allocated, 0, (long)allocationBudgetPerCall * measuredCalls);
            minimum = Math.Min(minimum, allocated);
            maximum = Math.Max(maximum, allocated);
        }
        // Compare within this runtime rather than pinning its object sizes. An eight-byte
        // allowance is smaller than an extra managed object per call. Compare batch totals
        // without integer averaging, and include intermediate sizes to catch nonlinear growth.
        Assert.InRange(maximum - minimum, 0, (long)catalogGrowthBudgetPerCall * measuredCalls);
    }

    [Fact]
    public void IncompleteDraftCanBeDiagnosedAndRepairedBeforeBuild()
    {
        var source = Machine.Define("draft", "1");
        var ready = source.State("ready").Initial();
        var done = source.State("done").Final();
        var movement = source.Edge("finish").From(ready);
        Assert.Contains(source.Validate(), diagnostic => diagnostic.Code == "machines.edge.incomplete");
        movement.To(done);
        Assert.Empty(source.Validate());
        Assert.Equal("done", source.Build().Decide("ready", "finish")!.To);
    }

    [Fact]
    public void CompilerSeamAndFluentGuardProduceTheSamePortableDefinition()
    {
        var guard = Cohesive.Relations.Authoring.MachineGuardCompiler.Compile<Facts, Command>((f, i) => f.Count > 0 && i.Allowed);
        MachineDefinition Build(bool useCompiler)
        {
            var source = Machine.Define("compiler-equivalence", "1");
            var ready = source.State("ready").Initial();
            var movement = source.Edge("retry").From(ready).To(ready);
            if (useCompiler) movement.When(guard);
            else movement.When((Facts f, Command i) => f.Count > 0 && i.Allowed);
            return source.Build();
        }
        Assert.Equal(Build(useCompiler: false).Fingerprint, Build(useCompiler: true).Fingerprint);
        Assert.Throws<ArgumentNullException>(() => Cohesive.Relations.Authoring.MachineGuardCompiler.Compile<Facts, Command>(null!));
    }

    [Fact]
    public void ConvenienceAdmissionAgreesWithEvaluateAndPreservesGuardRequirements()
    {
        var source = Machine.Define("indexed", "1");
        var ready = source.State("ready").Initial();
        var done = source.State("done").Final();
        source.Edge("z").From(ready).To(done);
        source.Edge("a").From(ready).To(ready);
        var machine = source.Build();
        Assert.Equal(new[] { "a", "z" }, machine.Enabled("ready"));
        Assert.Empty(machine.Enabled("done"));
        for (var index = 0; index < 1000; index++) { machine.Enabled("ready"); machine.Decide("ready", "z"); }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++) { machine.Enabled("ready"); machine.Decide("ready", "z"); }
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1000, 0, 64);
        foreach (var state in machine.States)
            foreach (var action in new[] { "a", "z" })
                Assert.Equal(machine.Evaluate(state, action, ObservationValue.EmptyObject, ObservationValue.EmptyObject).Edge,
                    machine.Decide(state, action));
        Assert.Throws<ArgumentException>(() => machine.Decide("ready", "unknown"));
        Assert.Throws<ArgumentException>(() => machine.Enabled("unknown"));
        var guarded = Machine.Define("guarded-index", "1");
        var first = guarded.State("first").Initial();
        var last = guarded.State("last").Final();
        guarded.Edge("finish").From(first).To(last).When((Facts f, Command i) => i.Allowed);
        var definition = guarded.Build();
        Assert.Throws<InvalidOperationException>(() => definition.Enabled("first"));
        Assert.Throws<InvalidOperationException>(() => definition.Decide("last", "finish"));
        Assert.Empty(definition.Enabled("last"));
    }

    [Fact]
    public void CoreMachinesRemainIndependentOfRelationsAndTransitions()
    {
        var assembly = typeof(MachineDefinition).Assembly;
        Assert.Equal(typeof(ObservationValue).Assembly, assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Cohesive.Relations" or "Cohesive.Transitions");
        Assert.Equal(typeof(TransitionStaticCompiler).Assembly, typeof(MachineTransitionLinker).Assembly);

        Assert.DoesNotContain(typeof(MachineGuardAuthoring).Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false)
            .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>(), attribute =>
                attribute.AssemblyName is "Cohesive.Transitions" or "Cohesive.Tests");
        var source = Machine.Define("typed-extension", "1");
        var draft = source.State("draft").Initial();
        var done = source.State("done").Final();
        source.Edge("submit").From(draft).To(done)
            .When((Facts facts, Command input) => facts.Count > 0 && input.Allowed);
        Assert.Equal(MachineDecisionKind.Accepted,
            source.Build().Evaluate("draft", "submit", FactsValue(2), CommandValue(true)).Kind);
    }

    [Fact]
    public void TypedAuthoringFreezesAndRejectsForeignStates()
    {
        var author = Machine.Define("example", "1");
        var draft = author.State("draft").Initial();
        var done = author.State("done").Final();
        var edge = author.Edge("submit").From(draft).To(done);
        var other = Machine.Define("other", "1").State("other");
        Assert.Throws<ArgumentException>(() => edge.From(other));
        var definition = author.Build();
        Assert.Same(definition, edge.Resolve().Machine);
        Assert.Throws<InvalidOperationException>(() => edge.To(draft));
        Assert.Equal(definition.Fingerprint, MachineDefinition.Deserialize(definition.CanonicalJson).Fingerprint);
        Assert.Contains("s0", definition.ToMermaid());
    }

    [Fact]
    public void GuardedAdmissionSurvivesPersistenceAndDistinguishesUncertainty()
    {
        var guard = Cohesive.Relations.Authoring.MachineGuardCompiler.Compile<Facts, Command>((facts, input) => facts.Count > 0 && input.Allowed);
        var definition = new MachineBuilder("guarded", "1").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done", guard: guard).Build();
        var loaded = MachineDefinition.Deserialize(definition.CanonicalJson);
        Assert.Equal(MachineDecisionKind.Accepted, loaded.Evaluate("draft", "submit", FactsValue(2), CommandValue(true)).Kind);
        Assert.Equal(MachineDecisionKind.Disabled, loaded.Evaluate("draft", "submit", FactsValue(0), CommandValue(true)).Kind);
        Assert.Equal(MachineDecisionKind.Indeterminate, loaded.Evaluate("draft", "submit", ObservationValue.EmptyObject, CommandValue(true)).Kind);
        Assert.Equal(MachineDecisionKind.UnknownAction, loaded.Evaluate("draft", "missing", FactsValue(2), CommandValue(true)).Kind);
        var duplicate = new MachineBuilder("ambiguous", "1").State("draft").State("done").Initial("draft").Final("done")
            .Edge("one", "draft", "done", "submit", guard).Edge("two", "draft", "done", "submit", guard).Build();
        var ambiguous = duplicate.Evaluate("draft", "submit", FactsValue(2), CommandValue(true));
        Assert.Equal(MachineDecisionKind.Ambiguous, ambiguous.Kind);
        Assert.Null(ambiguous.Edge);
    }

    [Fact]
    public void CapturedExecutableStateIsRejected()
    {
        var captured = new Facts(4);
        Assert.ThrowsAny<Exception>(() => Cohesive.Relations.Authoring.MachineGuardCompiler.Compile<Facts, Command>((facts, input) => facts.Count > captured.Count));
    }

    [Fact]
    public void CoherenceReturnsCounterexamplesAndHonorsExplorationBound()
    {
        var machine = new MachineBuilder("lifecycle", "1").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done").Build();
        var relation = new MachineCoherenceBuilder(machine, machine).Allow("draft", "draft").Allow("done", "done");
        var failure = relation.Analyze();
        Assert.Equal(MachineProofState.Disproven, failure.State);
        Assert.Single(failure.Trace);
        relation.Allow("done", "draft").Allow("draft", "done");
        Assert.Equal(MachineProofState.Unknown, relation.Analyze(maximumPairs: 1).State);
        Assert.Equal(MachineProofState.Proven, relation.Analyze().State);
        var document = relation.Build("coherence", "1");
        var restored = ExecutionDefinitionJsonSerializer.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(document));
        Assert.Equal(machine.Document.Reference, restored.GetDefinition<MachineCoherencePayload>().Left);
    }

    [Fact]
    public void TypedDiscriminatorUsesExactMachineAuthority()
    {
        var machine = new MachineBuilder("order", "1").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done").Build();
        var links = new MachineDiscriminatorBinding<Order, string>(machine, order => order.Status)
            .Map("draft", "draft").Map("done", "done").Build();
        Assert.Equal(machine.Document.Reference, Assert.Single(links.Edges).Machine);
        Assert.Single(links.Edges[0].Assignments);
        Assert.Throws<ArgumentException>(() => new MachineDiscriminatorBinding<Order, string>(machine, order => order.Status)
            .Map("draft", "same").Map("done", "same").Build());
        Assert.Throws<ArgumentException>(() => new MachinePredicateBinding<Order, Command>(machine)
            .State("draft", (order, input) => input.Allowed));
    }

    [Fact]
    public void RelationshipPersistenceRejectsRevisionDrift()
    {
        var machine = new MachineBuilder("order", "1").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done").Build();
        var projection = new MachineProjectionBuilder(machine, machine)
            .State("draft", "draft").State("done", "done").Edge("submit", "submit").Build("identity", "1");
        var json = ExecutionDefinitionJsonSerializer.Serialize(projection);
        Assert.Equal(projection.Metadata.Fingerprint, MachineRelationships.ReadProjection(json, machine, machine).Metadata.Fingerprint);
        var drift = new MachineBuilder("order", "2").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done").Build();
        Assert.Throws<InvalidOperationException>(() => MachineRelationships.ReadProjection(json, drift, machine));
    }

    [Fact]
    public void DiscriminatorWriteCannotBypassMovementWhenBindingIsSupplied()
    {
        var machine = new MachineBuilder("order", "1").State("draft").State("done").Initial("draft").Final("done")
            .Edge("submit", "draft", "done").Build();
        var links = new MachineDiscriminatorBinding<Order, string>(machine, order => order.Status)
            .Map("draft", "draft").Map("done", "done").Build();
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var definition = new TransitionDefinition(new(new ObjectTypeRef([])),
            new(new ObjectTypeRef([new("Status", text.Type!)])), text, [],
            new SequenceTransitionNode(new("root"),
                [new UpdateTransitionNode(new("bypass"), FieldPath.FromField("Status"), new SetTransitionPatch(Expr.Const("done"))),
                 new OutcomeTransitionNode(new("outcome"), TransitionOutcomeDisposition.Applied, Expr.Const("ok"))]));
        var document = TransitionDefinitionDocuments.Create(new("bypass"), new("1"), definition, new(new("tests"), new("tests")));
        var result = TransitionStaticCompiler.Compile(document, machineLinks: links);
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == TransitionCompilationDiagnosticCodes.MachineConfigurationMismatch);
    }

    [Fact]
    public void TypedAndStructuralAuthoringHaveIdenticalSemanticFingerprints()
    {
        var author = Machine.Define("equivalent", "1");
        var draft = author.State("draft").Initial();
        var done = author.State("done").Final();
        author.Edge("submit").From(draft).To(done);
        var structural = new MachineBuilder("equivalent", "1").State("done").State("draft")
            .Final("done").Initial("draft").Edge("submit", "draft", "done").Build();
        Assert.Equal(structural.Fingerprint, author.Build().Fingerprint);
        Assert.NotEmpty(author.Build().Document.Metadata.SourceMap.Entries);
    }

    [Fact]
    public void ForgedPayloadCannotKeepItsOriginalFingerprint()
    {
        var machine = new MachineBuilder("integrity", "1").State("draft").State("done")
            .Initial("draft").Final("done").Edge("submit", "draft", "done").Build();
        Assert.ThrowsAny<Exception>(() => MachineDefinition.Deserialize(machine.CanonicalJson.Replace("submit", "forged")));
    }

    [Theory]
    [InlineData("initial", "machines.initial.count")]
    [InlineData("duplicate", "machines.state.duplicate")]
    [InlineData("deadEnd", "machines.state.deadEnd")]
    public void IncompleteDraftsHaveStructuredDiagnostics(string problem, string code)
    {
        var builder = new MachineBuilder("invalid", "1").State("draft");
        if (problem != "initial") builder.Initial("draft");
        if (problem == "duplicate") builder.State("draft");
        Assert.Contains(builder.Validate(), diagnostic => diagnostic.Code == code);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Theory]
    [InlineData("done", TransitionDecisionKind.Applied)]
    [InlineData("draft", TransitionDecisionKind.InvalidDefinition)]
    public void PredicateTargetsAreCheckedAfterAllPatches(string finalStatus, TransitionDecisionKind expected)
    {
        var machine = new MachineBuilder("order", "1").State("draft").State("done").Initial("draft").Final("done")
            .Edge("submit", "draft", "done").Build();
        var links = new MachinePredicateBinding<Order, Command>(machine)
            .State("draft", (order, input) => order.Status == "draft")
            .State("done", (order, input) => order.Status == "done").Build();
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var observation = new ValueContract(new ObjectTypeRef([new("Status", text.Type!)]));
        var input = new ValueContract(new ObjectTypeRef([new("Allowed", new ScalarTypeRef(ScalarTypeKind.Bool))]));
        var definition = new TransitionDefinition(input, observation, text, [], new SequenceTransitionNode(new("root"),
            [new MoveMachineTransitionNode(new("move"), machine.Document.Reference, new("submit"), Expr.Const("illegal")),
             new UpdateTransitionNode(new("set"), FieldPath.FromField("Status"), new SetTransitionPatch(Expr.Const(finalStatus))),
             new OutcomeTransitionNode(new("outcome"), TransitionOutcomeDisposition.Applied, Expr.Const("ok"))]));
        var document = TransitionDefinitionDocuments.Create(new("submit"), new("1"), definition, new(new("tests"), new("tests")));
        var compiled = TransitionStaticCompiler.Compile(document, machineLinks: links);
        Assert.True(compiled.IsSuccessful, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
        var decision = TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("operation"),
            PortableValue.Concrete(input, CommandValue(true)),
            PortableValue.Concrete(observation, ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["Status"] = ObservationValue.FromString("draft") })));
        Assert.Equal(expected, decision.Kind);
        if (expected == TransitionDecisionKind.InvalidDefinition) Assert.Empty(decision.Patch);
    }
}
