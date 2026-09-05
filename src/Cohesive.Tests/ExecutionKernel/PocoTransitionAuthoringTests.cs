using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PocoTransitionAuthoringTests
{
    [Fact]
    public void PocoAndExplicitEntityAuthoringProduceIdenticalCanonicalBytes()
    {
        var shape = ObjectEntityDefinition.For<Run>(new("run-control")).Shape;
        var poco = TransitionAuthoring.Create<Run, Approve, string>(shape, Metadata(), transition => transition
            .Invariant(new("valid"), state => state.Status != "invalid")
            .Requires(new("eligible"), (state, input) => state.Eligible, (state, input) => "rejected")
            .Set(new("approve"), state => state.Status, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, (state, input) => state.Status));
        var explicitEntity = TransitionAuthoring.Create<ExplicitRun, Approve, string>(shape, Metadata(), transition => transition
            .Invariant(new("valid"), state => state.Status != "invalid")
            .Requires(new("eligible"), (state, input) => state.Eligible, (state, input) => "rejected")
            .Set(new("approve"), state => state.Status, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, (state, input) => state.Status));

        Assert.True(poco.IsValid, Format(poco.Validation));
        Assert.True(explicitEntity.IsValid, Format(explicitEntity.Validation));
        Assert.Equal(explicitEntity.Definition, poco.Definition);
        Assert.Equal(explicitEntity.Reference.Fingerprint, poco.Reference.Fingerprint);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(explicitEntity.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(poco.Document));

        var direct = new Cohesive.Transitions.IR.TransitionDefinition(
            input: new(new ObjectTypeRef([new("Approved", new ScalarTypeRef(ScalarTypeKind.Bool))])),
            observation: ValueContract.FromShape(shape),
            outcome: new(new ScalarTypeRef(ScalarTypeKind.String)),
            preconditions: [new(new("eligible"), Expr.Field("Eligible"), Expr.Const("rejected"))],
            body: new(new("body"),
            [
                new UpdateTransitionNode(new("approve"), FieldPath.FromField("Status"), new SetTransitionPatch(Expr.Const("approved"))),
                new OutcomeTransitionNode(new("result"), TransitionOutcomeDisposition.Applied, Expr.Field("Status"))
            ]),
            invariants: [new(new("valid"), Expr.Ne(Expr.Field("Status"), Expr.Const("invalid")))]);
        var directDocument = TransitionDefinitionDocuments.Create(Metadata().DefinitionId, Metadata().RevisionId, direct, Metadata().Provenance);
        Assert.Equal(direct, poco.Definition);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(directDocument),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(poco.Document));
    }

    [Theory]
    [InlineData(true, TransitionDecisionKind.Applied, "approved")]
    [InlineData(false, TransitionDecisionKind.AdmissionRejected, "pending")]
    public void ImmutableRecordExecutionPreservesPriorState(bool eligible, TransitionDecisionKind kind, string status)
    {
        var definition = ObjectEntityDefinition.For<Run>(new("run-control"));
        var authored = TransitionAuthoring.Create<Run, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Requires(new("eligible"), (state, input) => state.Eligible, (state, input) => "rejected")
            .Set(new("approve"), state => state.Status, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, (state, input) => state.Status));
        var original = new Run(eligible, "pending");
        var plan = Compile(authored);
        var state = ObservationValue.FromObject(original);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("activation/1"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Approve(true))),
            PortableValue.Concrete(plan.Definition.Observation, state));
        var candidate = TransitionStateProjector.Apply(state, decision);
        var observation = Observation.Create(definition.StateShape, candidate);
        var result = ObservationMaterializer.For<Run>(definition.StateShape).Compile().Materialize(observation);

        Assert.Equal(kind, decision.Kind);
        Assert.Equal(status, result.Status);
        Assert.Equal("pending", original.Status);
        Assert.NotSame(original, result);
    }

    [Fact]
    public void SerializedNestedSelectorsAddressTheCanonicalFieldPaths()
    {
        var definition = ObjectEntityDefinition.For<NestedRun>();
        var authored = TransitionAuthoring.Create<NestedRun, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Set(new("approve"), state => state.Details.Status, (state, input) => "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, (state, input) => state.Details.Status));
        var original = new NestedRun(new RunDetails("pending"));
        var plan = Compile(authored);
        var state = ObservationValue.FromObject(original);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("activation/1"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Approve(true))),
            PortableValue.Concrete(plan.Definition.Observation, state));

        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Equal("control.run_status", Assert.Single(decision.Patch).Path.ToString());
        // Expressions read the invocation observation; the ordered patch produces the candidate state.
        Assert.Equal("pending", decision.Outcome?.Value?.String);
        var observation = Observation.Create(definition.StateShape, TransitionStateProjector.Apply(state, decision));
        var mapped = ObservationMaterializer.For<NestedRun>(definition.StateShape).Compile().Materialize(observation);
        Assert.Equal("approved", mapped.Details.Status);
        Assert.Equal("pending", original.Details.Status);
    }

    [Fact]
    public void ExplicitScalarIdContractsAreUsedInNestedInputsAndOutcomes()
    {
        var mappings = new Dictionary<Type, TypeRef> { [typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.String) };
        var mapper = new DefaultClrTypeRefMapper(mappings);
        mappings[typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.Int64);
        var definition = ObjectEntityDefinition.For<IdentifiedRun>(new("run-control"), mapper);
        var authored = TransitionAuthoring.Create<IdentifiedRun, IdentifiedInput, RunId>(definition.Shape, Metadata(),
            transition => transition.Return(new("result"), TransitionOutcomeDisposition.Applied, (state, input) => state.Id),
            typeRefMapper: mapper);
        var plan = Compile(authored);
        var id = new RunId("run-1");
        var state = ObservationValue.FromObject(new IdentifiedRun(id, "pending"));
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("activation/1"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new IdentifiedInput(id))),
            PortableValue.Concrete(plan.Definition.Observation, state));

        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(plan.Definition.Outcome.Type).Kind);
        Assert.Equal("run-1", decision.Outcome?.Value?.String);
        var result = ObservationMaterializer.For<IdentifiedRun>(definition.StateShape).Compile()
            .Materialize(Observation.Create(definition.StateShape, state));
        Assert.Equal(id, result.Id);
    }

    [Fact]
    public void UndeclaredNestedMembersAndCapturedValuesFailDuringAuthoring()
    {
        var definition = ObjectEntityDefinition.For<NestedRun>();
        Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<NestedRun, Approve, string>(definition.Shape, Metadata(), transition => transition
                .Set(new("invalid"), state => state.Details.Status.Length, 1)));
        var captured = "approved";
        Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<NestedRun, Approve, string>(definition.Shape, Metadata(), transition => transition
                .Set(new("captured"), state => state.Details.Status, (state, input) => captured)));
    }

    [Fact]
    public void EntityDefinitionsSortSemanticNamesAndRejectAmbiguousBindings()
    {
        var definition = ObjectEntityDefinition.For<RenamedFields>(new("stable-entity"));
        Assert.Equal(["a", "z"], definition.Fields.Select(field => field.Name.Value));
        Assert.Equal("stable-entity", definition.Name.Value);
        Assert.Throws<InvalidOperationException>(() => ObjectEntityDefinition.For<DuplicateFields>());
    }

    [Fact]
    public void InvariantFailureDiscardsTheCandidatePatch()
    {
        var definition = ObjectEntityDefinition.For<Run>();
        var plan = Compile(TransitionAuthoring.Create<Run, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Invariant(new("valid"), state => state.Status != "invalid")
            .Set(new("invalidate"), state => state.Status, "invalid")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "changed")));
        var state = ObservationValue.FromObject(new Run(true, "pending"));
        var decision = Decide(plan, new Approve(true), state);

        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.InvariantViolated);
        Assert.Empty(decision.Patch);
        Assert.Equal(state, TransitionStateProjector.Apply(state, decision));
    }

    [Fact]
    public void CollectionPatchesAndAnOrdinaryCountPropertyUseTheirDeclaredContracts()
    {
        var definition = ObjectEntityDefinition.For<RunHistory>();
        var plan = Compile(TransitionAuthoring.Create<RunHistory, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Invariant(new("aligned"), state => state.Count == state.Events.Count)
            .Increment(new("count"), state => state.Count, (state, input) => 1)
            .Append(new("append"), state => state.Events, (state, input) => "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "approved")));
        var original = new RunHistory(0, []);
        var decision = Decide(plan, new Approve(true), ObservationValue.FromObject(original));
        var candidate = Observation.Create(definition.StateShape,
            TransitionStateProjector.Apply(ObservationValue.FromObject(original), decision));
        var result = ObservationMaterializer.For<RunHistory>(definition.StateShape).Compile().Materialize(candidate);

        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Equal(1, result.Count);
        Assert.Equal(["approved"], result.Events);
        Assert.Empty(original.Events);
    }

    [Fact]
    public void PresenceAndNullabilityRemainIndependentCanonicalConstraints()
    {
        EntityDefinition definition = new(new("notes"),
        [new(new("Note"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Required, nullability: FieldNullability.Nullable)]);
        var nullable = Compile(TransitionAuthoring.Create<RunNote, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Set(new("clear"), state => state.Note, (string?)null)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "cleared")));
        var state = ObservationValue.FromObject(new RunNote("old"));
        var cleared = TransitionStateProjector.Apply(state, Decide(nullable, new Approve(true), state));
        Assert.True(cleared.TryGetField(FieldPath.FromField("Note"), out var note));
        Assert.Equal(ObservationValueKind.Null, note.Kind);

        var requiredRemoval = TransitionAuthoring.Create<RunNote, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Remove(new("remove"), state => state.Note)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "removed"));
        Assert.False(requiredRemoval.Compile().IsSuccessful);

        Shape optionalShape = new(definition.Shape.Id,
            [definition.Fields[0] with { Presence = FieldPresence.Optional, Nullability = FieldNullability.NonNullable }],
            role: ShapeRoles.Entity);
        var optionalRemoval = Compile(TransitionAuthoring.Create<RunNote, Approve, string>(optionalShape, Metadata(), transition => transition
            .Remove(new("remove"), state => state.Note)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "removed")));
        var removed = TransitionStateProjector.Apply(state, Decide(optionalRemoval, new Approve(true), state));
        Assert.False(removed.TryGetField(FieldPath.FromField("Note"), out _));
    }

    [Fact]
    public void ConfiguredSemanticNamesSurviveClrRenamesAndDoNotPolluteDefaultDefinitions()
    {
        EntityDefinition definition = new(new("run-control"),
        [new(new("status"), new ScalarTypeRef(ScalarTypeKind.String))]);
        var before = TransitionAuthoring.Create<OldRun, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Set(new("approve"), state => state.Status, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "approved"),
            memberPathResolver: static (root, members) => FieldPath.FromField("status"));
        var after = TransitionAuthoring.Create<RenamedRun, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Set(new("approve"), state => state.CurrentStatus, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "approved"),
            memberPathResolver: static (root, members) => FieldPath.FromField("status"));

        Assert.Equal(before.Reference.Fingerprint, after.Reference.Fingerprint);
        var observation = Observation.Create(definition.StateShape,
            ObservationValue.FromObject(new Dictionary<string, object> { ["status"] = "approved" }));
        var result = ObservationMaterializer.For<RenamedRun>(definition.StateShape)
            .Map("status", state => state.CurrentStatus).Compile().Materialize(observation);
        Assert.Equal("approved", result.CurrentStatus);
        Assert.Equal("CurrentStatus", Assert.Single(ObjectEntityDefinition.For<RenamedRun>().Fields).Name.Value);
    }

    [Fact]
    public void UndeclaredConvertersAndMismatchedScalarMappingsCannotCompile()
    {
        var mapper = new DefaultClrTypeRefMapper();
        var opaque = Assert.IsType<OpaqueRuntimeTypeRef>(mapper.Map(typeof(RunId), null));
        Assert.Equal(TypeInferenceDiagnosticReasons.UnsupportedValueConverter, opaque.InferenceDiagnostic?.Reason);
        var inferred = ObjectEntityDefinition.For<IdentifiedRun>();
        var unsupported = TransitionAuthoring.Create<IdentifiedRun, Approve, RunId>(inferred.Shape, Metadata(), transition => transition
            .Return(new("result"), TransitionOutcomeDisposition.NoChange, (state, input) => state.Id));
        Assert.False(unsupported.Compile().IsSuccessful);

        var stringMapper = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef> { [typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.String) });
        var integerMapper = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef> { [typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.Int64) });
        var definition = ObjectEntityDefinition.For<IdentifiedRun>(new("run-control"), stringMapper);
        var mismatch = TransitionAuthoring.Create<IdentifiedRun, Approve, RunId>(definition.Shape, Metadata(), transition => transition
            .Return(new("result"), TransitionOutcomeDisposition.NoChange, (state, input) => state.Id), typeRefMapper: integerMapper);
        Assert.False(mismatch.Compile().IsSuccessful);
    }

    [Fact]
    public void IdentityFieldsCannotBePatched()
    {
        EntityDefinition definition = new(new("run-control"),
            [new(new("Status"), new ScalarTypeRef(ScalarTypeKind.String), role: FieldRole.Identity)]);
        Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<OldRun, Approve, string>(definition.Shape, Metadata(), transition => transition
                .Set(new("change"), state => state.Status, "changed")));
    }

    [Fact]
    public void CustomClrOperatorsDoNotBecomePortableEqualityOrConversions()
    {
        var mapper = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef> { [typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.String) });
        var definition = ObjectEntityDefinition.For<IdentifiedRun>(new("run-control"), mapper);
        Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<IdentifiedRun, IdentifiedInput, string>(definition.Shape, Metadata(), transition => transition
                .Requires(new("same"), (state, input) => state.Id == input.Id, (state, input) => "different"), typeRefMapper: mapper));
        Assert.Throws<TransitionExpressionTranslationException>(() =>
            TransitionAuthoring.Create<IdentifiedRun, IdentifiedInput, string>(definition.Shape, Metadata(), transition => transition
                .Return(new("result"), TransitionOutcomeDisposition.NoChange, (state, input) => (string)state.Id), typeRefMapper: mapper));
    }

    [Fact]
    public void EnumWireNamesRemainCanonicalAcrossObservationAndMaterialization()
    {
        var definition = ObjectEntityDefinition.For<RunWithStatus>();
        var plan = Compile(TransitionAuthoring.Create<RunWithStatus, Approve, string>(definition.Shape, Metadata(), transition => transition
            .Requires(new("pending"), (state, input) => state.Status == RunStatus.Pending, (state, input) => "not-pending")
            .Set(new("approve"), state => state.Status, RunStatus.Approved)
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "approved")));
        var state = ObservationValue.FromObject(new RunWithStatus(RunStatus.Pending));
        var decision = Decide(plan, new Approve(true), state);
        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Equal("approved-run", Assert.Single(decision.Patch).After.Value?.String);
        var materialized = ObservationMaterializer.For<RunWithStatus>(definition.StateShape).Compile()
            .Materialize(Observation.Create(definition.StateShape, TransitionStateProjector.Apply(state, decision)));
        Assert.Equal(RunStatus.Approved, materialized.Status);
    }

    static TransitionDecision Decide<TInput>(CompiledTransitionPlan plan, TInput input, ObservationValue state) =>
        TransitionReferenceInterpreter.DecideFullState(plan, new("activation/1"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(input)),
            PortableValue.Concrete(plan.Definition.Observation, state));

    static CompiledTransitionPlan Compile<TState, TInput, TOutcome>(Transition<TState, TInput, TOutcome> authored)
        where TState : notnull
    {
        Assert.True(authored.IsValid, Format(authored.Validation));
        var compilation = authored.Compile();
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        return Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
    }

    static string Format(DocumentValidationResult validation) => string.Join(Environment.NewLine,
        validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    static TransitionAuthoringMetadata Metadata() => new(new("transition/run/approve"), new("revision/1"), new("body"),
        new(new(TransitionAuthoring.Producer), new("tests/ito-adoption"), DocumentOrigin.Generated));

    sealed record Run(bool Eligible, string Status);
    sealed record Approve(bool Approved);
    sealed record NestedRun([property: JsonPropertyName("control")] RunDetails Details);
    sealed record RunDetails([property: JsonPropertyName("run_status")] string Status);
    sealed record RunHistory(int Count, IReadOnlyList<string> Events);
    sealed record RunNote(string? Note);
    sealed record OldRun(string Status);
    sealed record RenamedRun(string CurrentStatus);
    sealed record RunWithStatus(RunStatus Status);
    [JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
    enum RunStatus
    {
        [JsonStringEnumMemberName("pending-run")] Pending,
        [JsonStringEnumMemberName("approved-run")] Approved
    }
    sealed record IdentifiedRun(RunId Id, string Status);
    sealed record IdentifiedInput(RunId Id);
    sealed record RenamedFields([property: JsonPropertyName("z")] string A, [property: JsonPropertyName("a")] string Z);
    sealed record DuplicateFields([property: JsonPropertyName("same")] string A, [property: JsonPropertyName("same")] string B);

    [JsonConverter(typeof(RunIdConverter))]
    sealed record RunId(string Value)
    {
        public static explicit operator string(RunId id) => id.Value;
    }
    sealed class RunIdConverter : JsonConverter<RunId>
    {
        public override RunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    sealed class ExplicitRun : Entity<ExplicitRun>
    {
        public ExplicitRun()
        {
            Eligible = MutableField<bool>(nameof(Eligible));
            Status = MutableField<string>(nameof(Status));
        }
        public Field<bool> Eligible { get; }
        public Field<string> Status { get; }
    }
}
