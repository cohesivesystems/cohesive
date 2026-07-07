using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

/// <summary>
/// Tests for declarative transition execution.
/// </summary>
public sealed class DeclarativeEntityRuntimeTests
{
    [Fact]
    public void ExpressionTransition_InheritsDirectParameterTypeFromAssignedField()
    {
        var definition = OpaquePayloadEntity.Instance.Definition;
        var transition = Assert.Single(definition.Transitions, item => item.Name == nameof(OpaquePayloadEntity.Revise));
        var input = Assert.Single(transition.Inputs, item => item.Name == nameof(OpaquePayloadEntity.ReviseInput.Payload));
        var opaque = Assert.IsType<OpaqueRuntimeTypeRef>(input.Type);

        Assert.Equal(typeof(OpaquePayload).FullName, opaque.RuntimeType);
    }

    [Fact]
    public void Apply_ExecutesTransitionWithTypedInput_AndProducesEffects()
    {
        var orderEntity = BuildOrderEntity();
        var runtime = new DeclarativeEntityRuntime(orderEntity);
        var currentState = CreateInitialOrderState();

        var result = runtime.Apply(
            entityId: "order-1",
            state: currentState,
            version: 4,
            transitionName: "AssignCarrier",
            input: Input(("carrierId", "carrier-9")));

        Assert.Equal(5, result.NewVersion);
        Assert.Equal("AssignCarrier", result.TransitionName);
        Assert.Equal("Assigned", result.NewState.Fields["Status"].GetString());
        Assert.Equal("carrier-9", result.NewState.Fields["CarrierId"].GetString());
        Assert.Equal(2, result.NewState.Fields["StopCount"].GetInt32());

        var effect = Assert.Single(result.Effects);
        Assert.Equal("CarrierAssigned", effect.Name);
        Assert.Equal(ObservationValueKind.Object, effect.Payload.Kind);
        Assert.Equal("order-1", effect.Payload.GetProperty("orderId").GetString());
        Assert.Equal("carrier-9", effect.Payload.GetProperty("carrierId").GetString());
    }

    [Fact]
    public void Apply_ExposesReadWriteAndChangedFieldMetadata()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 4,
            transitionName: "AssignCarrier",
            input: Input(("carrierId", "carrier-9"))
            );

        Assert.Contains("Id", result.ReadFields);
        Assert.Contains("Status", result.ReadFields);
        Assert.Contains("CarrierId", result.ReadFields);
        Assert.Contains("Status", result.WriteFields);
        Assert.Contains("CarrierId", result.WriteFields);
        Assert.Contains("Status", result.ChangedFields);
        Assert.Contains("CarrierId", result.ChangedFields);
        Assert.Contains("StopCount", result.ChangedFields);
    }

    [Fact]
    public void TransitionPatchProjector_ProjectsDeclaredWritesAndChangedFields()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 4,
            transitionName: "AssignCarrier",
            input: Input(("carrierId", "carrier-9")));

        var declaredWritePatch = TransitionPatchProjector.ProjectDeclaredWritePatch(result);
        Assert.Equal(2, declaredWritePatch.Count);
        Assert.Equal("Assigned", declaredWritePatch["Status"].GetString());
        Assert.Equal("carrier-9", declaredWritePatch["CarrierId"].GetString());
        Assert.DoesNotContain("StopCount", declaredWritePatch.Keys);

        var changedFieldPatch = TransitionPatchProjector.ProjectChangedFieldPatch(result);
        Assert.Equal(3, changedFieldPatch.Count);
        Assert.Contains("Status", changedFieldPatch.Keys);
        Assert.Contains("CarrierId", changedFieldPatch.Keys);
        Assert.Contains("StopCount", changedFieldPatch.Keys);
    }

    [Fact]
    public void Apply_MissingRequiredTransitionParameter_Throws()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var ex = Assert.Throws<SemanticRuleViolationException>(
            testCode: () => runtime.Apply(
                entityId: "order-1",
                state: CreateInitialOrderState(),
                version: 0,
                transitionName: "AssignCarrier",
                input: Input())
            );

        Assert.Contains(expectedSubstring: "missing required parameter 'carrierId'", actualString: ex.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_TypeMismatchOnParameter_Throws()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var ex = Assert.Throws<SemanticRuleViolationException>(
            testCode: () => runtime.Apply(
                entityId: "order-1",
                state: CreateInitialOrderState(),
                version: 0,
                transitionName: "AssignCarrier",
                input: Input(("carrierId", 42))));

        Assert.Contains("transition parameter 'carrierId'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_NonObjectInput_Throws()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var ex = Assert.Throws<SemanticRuleViolationException>(
            testCode: () => runtime.Apply(
                entityId: "order-1",
                state: CreateInitialOrderState(),
                version: 0,
                transitionName: "AssignCarrier",
                input: ObservationValue.FromString("carrier-9")));

        Assert.Contains("expects input to be an object value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_TransitionViolatingInvariant_Throws()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        Assert.Throws<InvariantViolationException>(
            testCode: () => runtime.Apply(
                entityId: "order-1",
                state: CreateInitialOrderState(),
                version: 0,
                transitionName: "ForceAssignedWithoutCarrier"));
    }

    [Fact]
    public void Apply_OrPrecondition_ShortCircuitsRightHandSide()
    {
        var runtime = new DeclarativeEntityRuntime(BuildShortCircuitEntity(
            precondition: Expr.Or(
                left: Expr.Const(value: true),
                right: Expr.Call(function: "count", Expr.Const(value: 5)))));

        var result = runtime.Apply(
            entityId: "probe-1",
            state: CreateProbeState(),
            version: 7,
            transitionName: "Ping");

        Assert.Equal(expected: 8, actual: result.NewVersion);
    }

    [Fact]
    public void Apply_AndPrecondition_ShortCircuitsRightHandSide()
    {
        var runtime = new DeclarativeEntityRuntime(BuildShortCircuitEntity(
            precondition: Expr.And(
                left: Expr.Const(value: false),
                right: Expr.Call(function: "count", Expr.Const(value: 5)))));

        Assert.Throws<TransitionPreconditionException>(
            testCode: () => runtime.Apply(
                entityId: "probe-1",
                state: CreateProbeState(),
                version: 7,
                transitionName: "Ping"));
    }

    [Fact]
    public void Apply_ConditionalPrecondition_ShortCircuitsUnselectedBranch()
    {
        var entity = BuildShortCircuitEntity(
            precondition: Expr.If(
                test: Expr.Const(value: false),
                ifTrue: Expr.Call(function: "count", Expr.Const(value: 5)),
                ifFalse: Expr.Const(value: true)
            )
        );
        
        var runtime = new DeclarativeEntityRuntime(entity);

        var result = runtime.Apply(
            entityId: "probe-1",
            state: CreateProbeState(),
            version: 7,
            transitionName: "Ping"
            );

        Assert.Equal(expected: 8, actual: result.NewVersion);
    }

    [Fact]
    public void Apply_AppendFunction_AppendsCollectionElement()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 2,
            transitionName: "AppendStop",
            input: Input(("stopCode", "PDX")));

        var stops = result.NewState.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SFO", "SEA", "PDX"], stops);
        Assert.Equal(3, result.NewState.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public void Apply_InsertAtFunction_InsertsCollectionElementAtRequestedIndex()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 2,
            transitionName: "InsertStop",
            input: Input(("stopCode", "PDX"), ("position", 1)));

        var stops = result.NewState.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SFO", "PDX", "SEA"], stops);
        Assert.Equal(3, result.NewState.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public void Apply_AppendRangeFunction_AppendsCollectionElements()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 2,
            transitionName: "AppendStops",
            input: Input(("stopCodes", new[] { "PDX", "LAX" })));

        var stops = result.NewState.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SFO", "SEA", "PDX", "LAX"], stops);
        Assert.Equal(4, result.NewState.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public void Apply_InsertRangeAtFunction_InsertsCollectionElementsAtRequestedIndex()
    {
        var runtime = new DeclarativeEntityRuntime(BuildOrderEntity());

        var result = runtime.Apply(
            entityId: "order-1",
            state: CreateInitialOrderState(),
            version: 2,
            transitionName: "InsertStops",
            input: Input(("stopCodes", new[] { "PDX", "LAX" }), ("position", 1)));

        var stops = result.NewState.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SFO", "PDX", "LAX", "SEA"], stops);
        Assert.Equal(4, result.NewState.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public void Apply_EmitsEffectRequestWithContinuationMetadata()
    {
        var runtime = new DeclarativeEntityRuntime(BuildIntentEntity());

        var result = runtime.Apply(
            entityId: "route-1",
            state: CreateInitialIntentState(),
            version: 11,
            transitionName: "AddStop",
            input: Input(("stopCode", "RNO")));

        var request = Assert.Single(result.Effects);
        Assert.Equal("CalculateMileage", request.Name);
        Assert.Equal("ApplyMileage", request.Continuation?.TransitionName);
        Assert.Equal(1L, request.Payload.GetProperty("mileageRevision").GetInt64());

        var stops = result.NewState.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SFO", "RNO"], stops);
        Assert.Equal(0m, result.NewState.Fields["PlannedDistanceMiles"].GetDecimal());
    }

    [Fact]
    public void Apply_ContinuationTransition_AppliesMileageWhenRevisionMatches()
    {
        var runtime = new DeclarativeEntityRuntime(BuildIntentEntity());

        var addResult = runtime.Apply(
            entityId: "route-1",
            state: CreateInitialIntentState(),
            version: 11,
            transitionName: "AddStop",
            input: Input(("stopCode", "RNO")));

        var applyResult = runtime.Apply(
            entityId: "route-1",
            state: addResult.NewState,
            version: addResult.NewVersion,
            transitionName: "ApplyMileage",
            input: Input(("mileageRevision", 1), ("totalMiles", 487.25m)));

        Assert.Equal(487.25m, applyResult.NewState.Fields["PlannedDistanceMiles"].GetDecimal());
        Assert.Equal(13, applyResult.NewVersion);
    }

    [Fact]
    public void Apply_UnknownFunction_Throws()
    {
        var runtime = new DeclarativeEntityRuntime(BuildIoEntity());

        var ex = Assert.Throws<SemanticRuleViolationException>(
            () => runtime.Apply(
                entityId: "route-1",
                state: CreateInitialIoState(),
                version: 11,
                transitionName: "AddStop",
                input: Input(("stopCode", "RNO"))));

        Assert.Contains("Unsupported function 'resolveMiles'", ex.Message, StringComparison.Ordinal);
    }

    static ObservationValue Input(params (string Name, object? Value)[] values)
    {
        Dictionary<string, ObservationValue> input = new(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            input[name] = ObservationValue.FromObject(value);
        return ObservationValue.FromObject(input);
    }

    static EntityDefinition BuildOrderEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "Id", type: DomainTypes.String(), f => f.WriteOnce())
                .Field(name: "Status", type: DomainTypes.Enum(name: "OrderStatus", members: ["Draft", "Assigned", "Completed"]))
                .Field(name: "CarrierId", type: DomainTypes.String(), f => f.Optional())
                .Field(name: "Stops", type: DomainTypes.String(), f => f.Many())
                .Field(
                    name: "StopCount",
                    type: DomainTypes.Int32(),
                    f => f.Computed(expression: Expr.Call("count", Expr.Field("Stops")))
                    )
                .Invariant(
                    name: "AssignedRequiresCarrier",
                    Expr.Or(
                        Expr.Ne(Expr.Field("Status"), Expr.Const("Assigned")),
                        Expr.Ne(Expr.Field("CarrierId"), Expr.Null()))
                    )
                .Transition(
                    name: "AssignCarrier",
                    t => t
                        .Parameter(name: "carrierId", type: DomainTypes.String(), isRequired: true)
                        .Requires(
                            name: "StatusMustBeDraft",
                            Expr.Eq(left: Expr.Field("Status"), right: Expr.Const(value: "Draft")))
                        .Set("CarrierId", Expr.Param(name: "carrierId"))
                        .Set("Status", Expr.Const(value: "Assigned"))
                        .Emit(
                            name: "CarrierAssigned",
                            payload: Expr.Call(
                                "object",
                                Expr.Const(value: "orderId"),
                                Expr.Field("Id"),
                                Expr.Const(value: "carrierId"),
                                Expr.Param(name: "carrierId")))
                    )
                .Transition("ForceAssignedWithoutCarrier",
                    t => t.Set("Status", Expr.Const(value: "Assigned"))
                    )
                .Transition(
                    name: "AppendStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Add("Stops", Expr.Param(name: "stopCode"))
                    )
                .Transition(
                    name: "InsertStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Parameter(name: "position", type: DomainTypes.Int32(), isRequired: true)
                        .Insert("Stops", Expr.Param(name: "position"), Expr.Param(name: "stopCode"))
                    )
                .Transition(
                    name: "AppendStops",
                    t => t
                        .Parameter(name: "stopCodes", type: DomainTypes.Array(DomainTypes.String()), isRequired: true)
                        .AddRange("Stops", Expr.Param(name: "stopCodes"))
                    )
                .Transition(
                    name: "InsertStops",
                    t => t
                        .Parameter(name: "stopCodes", type: DomainTypes.Array(DomainTypes.String()), isRequired: true)
                        .Parameter(name: "position", type: DomainTypes.Int32(), isRequired: true)
                        .InsertRange("Stops", Expr.Param(name: "position"), Expr.Param(name: "stopCodes"))
                    )
            )
        );

        return Assert.Single(collection: model.Entities);
    }

    static EntityDefinition BuildIoEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Route", route => route
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
                .Field(name: "PlannedDistanceMiles", type: DomainTypes.Decimal())
                .Transition(
                    name: "AddStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Add("Stops", Expr.Param("stopCode"))
                        .Set("PlannedDistanceMiles", Expr.Call("resolveMiles", Expr.Field("Stops"))))
            ));

        return Assert.Single(model.Entities);
    }

    static EntityState CreateInitialOrderState()
    {
        return BuildOrderEntity().CreateState(new
        {
            Id = "order-1",
            Status = "Draft",
            CarrierId = (string?)null,
            Stops = new[] { "SFO", "SEA" },
            StopCount = 0
        });
    }

    static EntityState CreateInitialIoState()
    {
        return BuildIoEntity().CreateState(new
        {
            Stops = new[] { "SFO" },
            PlannedDistanceMiles = 0m
        });
    }

    static EntityDefinition BuildIntentEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Route", route => route
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
                .Field(name: "PlannedDistanceMiles", type: DomainTypes.Decimal())
                .Field(name: "MileageRevision", type: DomainTypes.Int32())
                .Transition(
                    name: "AddStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Add("Stops", Expr.Param("stopCode"))
                        .Set(
                            "MileageRevision",
                            valueExpression: new BinaryExpr(
                                BinaryOperator.Add,
                                Expr.Field("MileageRevision"),
                                Expr.Const(1)))
                        .Emit(
                            name: "CalculateMileage",
                            payload: Expr.Call(
                                "object",
                                Expr.Const("mileageRevision"),
                                Expr.Field("MileageRevision"),
                                Expr.Const("stops"),
                                Expr.Field("Stops")),
                            continuationTransition: "ApplyMileage"))
                .Transition(
                    name: "ApplyMileage",
                    t => t
                        .Parameter(name: "mileageRevision", type: DomainTypes.Int32(), isRequired: true)
                        .Parameter(name: "totalMiles", type: DomainTypes.Decimal(), isRequired: true)
                        .Requires(
                            name: "RevisionMatches",
                            expression: Expr.Eq(
                                Expr.Field("MileageRevision"),
                                Expr.Param("mileageRevision")))
                        .Set("PlannedDistanceMiles", Expr.Param("totalMiles")))
            ));

        return Assert.Single(model.Entities);
    }

    static EntityState CreateInitialIntentState()
    {
        return BuildIntentEntity().CreateState(new
        {
            Stops = new[] { "SFO" },
            PlannedDistanceMiles = 0m,
            MileageRevision = 0
        });
    }

    static EntityDefinition BuildShortCircuitEntity(Expr precondition)
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Probe", probe => probe
                .Field(name: "Status", type: DomainTypes.String())
                .Transition(name: "Ping", t => t.Requires(name: "Guard", expression: precondition))
            )
        );

        return Assert.Single(model.Entities);
    }

    static EntityState CreateProbeState()
    {
        return BuildShortCircuitEntity(precondition: Expr.Const(value: true)).CreateState(new
        {
            Status = "Ready"
        });
    }

    sealed record OpaquePayload(string Value);

    sealed class OpaquePayloadEntity : Entity<OpaquePayloadEntity>
    {
        public sealed record ReviseInput(OpaquePayload Payload);

        public OpaquePayloadEntity()
        {
            Payload = MutableField<OpaquePayload>(nameof(Payload), field => field.Opaque());

            Revise = Transition<ReviseInput>(nameof(Revise), t => t
                .Set(entity => entity.Payload, (_, input) => input.Payload));
        }

        public Field<OpaquePayload> Payload { get; }

        public Transition<OpaquePayloadEntity, ReviseInput> Revise { get; }
    }
}
