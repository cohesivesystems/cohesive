namespace Cohesive.Tests.Modeling;

/// <summary>
/// Tests for typed transition-expression authoring and translation.
/// </summary>
public sealed class TransitionExpressionDslTests
{
    [Fact]
    public void EntityBuilder_ExpressionTransitionOverload_AddsTransition()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "OrderId", type: DomainTypes.String(), configure: f => f.WriteOnce())
                .Field(name: "Status", type: DomainTypes.String())
                .Field(name: "CarrierId", type: DomainTypes.String(), configure: f => f.Optional())
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
                .Transition<OrderEntity, AssignCarrierArgs>(
                    name: "AssignCarrier",
                    configure: t => t
                        .Requires("StatusMustBeDraft", (e, _) => e.Status == "Draft")
                        .Set(field: e => e.CarrierId, value: (_, p) => p.CarrierId)
                        .Set(field: e => e.Status, value: (_, _) => "Assigned"))
            )
        );

        var entity = Assert.Single(model.Entities);
        var transition = Assert.Single(entity.Transitions);

        Assert.Equal("AssignCarrier", transition.Name);
        _ = Assert.Single(collection: transition.Preconditions);
        Assert.Equal(2, transition.Updates.Length);
    }

    [Fact]
    public void Compile_TranslatesTypedExpressionsToDeclarativeTransition()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, AssignCarrierArgs>(
            entityDefinition: entity,
            transitionName: "AssignCarrier",
            configure: t => t
                .Requires(name: "StatusMustBeDraft", predicate: (e, _) => e.Status == "Draft")
                .Set(field: e => e.CarrierId, value: (_, p) => p.CarrierId)
                .Set(field: e => e.Status, value: (_, _) => "Assigned")
                .Emit(
                    name: "CarrierAssigned",
                    payload: (e, p) => new
                    {
                        orderId = e.OrderId,
                        carrierId = p.CarrierId
                    })
            );

        Assert.Equal(expected: "AssignCarrier", actual: transition.Name);

        var parameter = Assert.Single(collection: transition.Inputs);
        Assert.Equal("CarrierId", actual: parameter.Name);
        Assert.Equal(DomainTypes.String(), actual: parameter.Type);
        Assert.True(parameter.IsRequired);

        var precondition = Assert.Single(collection: transition.Preconditions);
        var preconditionExpr = Assert.IsType<BinaryExpr>(precondition.Expression);
        Assert.Equal(BinaryOperator.Eq, actual: preconditionExpr.Operator);
        Assert.Equal("Status", ResolveFieldIdentity(Assert.IsType<FieldExpr>(preconditionExpr.Left)));
        Assert.Equal("Draft", actual: Assert.IsType<ConstantExpr>(preconditionExpr.Right).Value.GetString());

        Assert.Equal(2, actual: transition.Updates.Length);
        Assert.Equal("CarrierId", actual: transition.Updates[0].Field);
        Assert.Equal("CarrierId", actual: Assert.IsType<ParameterExpr>(transition.Updates[0].ValueExpression).Parameter);
        Assert.Equal("Status", actual: transition.Updates[1].Field);
        Assert.Equal("Assigned", actual: Assert.IsType<ConstantExpr>(transition.Updates[1].ValueExpression).Value.GetString());

        var effect = Assert.Single(collection: transition.Effects);
        Assert.Equal(expected: "CarrierAssigned", actual: effect.Name);
        var payload = Assert.IsType<CallExpr>(effect.Payload);
        Assert.Equal("object", actual: payload.Function);
        Assert.Equal(4, actual: payload.Arguments.Length);
        Assert.Equal("orderId", actual: Assert.IsType<ConstantExpr>(payload.Arguments[0]).Value.GetString());
        Assert.Equal("OrderId", ResolveFieldIdentity(Assert.IsType<FieldExpr>(payload.Arguments[1])));
        Assert.Equal("carrierId", actual: Assert.IsType<ConstantExpr>(payload.Arguments[2]).Value.GetString());
        Assert.Equal("CarrierId", actual: Assert.IsType<ParameterExpr>(payload.Arguments[3]).Parameter);
    }

    [Fact]
    public void Compile_TranslatesCountPropertyToFunctionCall()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, NoParameters>(
            entityDefinition: entity,
            transitionName: "ValidateStops",
            configure: t => t.Requires(
                name: "HasStops",
                predicate: (e, _) => e.Stops.Count > 0));

        var precondition = Assert.Single(collection: transition.Preconditions);
        var expr = Assert.IsType<BinaryExpr>(precondition.Expression);
        Assert.Equal(expected: BinaryOperator.Gt, actual: expr.Operator);
        var countCall = Assert.IsType<CallExpr>(expr.Left);
        Assert.Equal(expected: "count", actual: countCall.Function);
        var countArg = Assert.Single(collection: countCall.Arguments);
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(countArg)));
        Assert.Equal(expected: 0, actual: Assert.IsType<ConstantExpr>(expr.Right).Value.GetInt32());
    }

    [Fact]
    public void Compile_TranslatesFieldIsOneOfToOrChain()
    {
        var entity = BuildOrderEntityDefinition();
        var allowedStatuses = new[] { "Draft", "Assigned" };

        var transition = TransitionExpressionDsl.Compile<OrderEntity, NoParameters>(
            entityDefinition: entity,
            transitionName: "AllowActiveStatuses",
            configure: t => t.Requires(
                name: "StatusIsAllowed",
                predicate: (e, _) => e.Status.IsOneOf(allowedStatuses)));

        var precondition = Assert.Single(transition.Preconditions);
        var or = Assert.IsType<BinaryExpr>(precondition.Expression);
        Assert.Equal(BinaryOperator.Or, or.Operator);

        var left = Assert.IsType<BinaryExpr>(or.Left);
        Assert.Equal(BinaryOperator.Eq, left.Operator);
        Assert.Equal("Status", ResolveFieldIdentity(Assert.IsType<FieldExpr>(left.Left)));
        Assert.Equal("Draft", Assert.IsType<ConstantExpr>(left.Right).Value.GetString());

        var right = Assert.IsType<BinaryExpr>(or.Right);
        Assert.Equal(BinaryOperator.Eq, right.Operator);
        Assert.Equal("Status", ResolveFieldIdentity(Assert.IsType<FieldExpr>(right.Left)));
        Assert.Equal("Assigned", Assert.IsType<ConstantExpr>(right.Right).Value.GetString());
    }

    [Fact]
    public void Compile_TranslatesFieldIsNotOneOfToNegatedOrChain()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, NoParameters>(
            entityDefinition: entity,
            transitionName: "RejectTerminalStatuses",
            configure: t => t.Requires(
                name: "StatusIsNotTerminal",
                predicate: (e, _) => e.Status.IsNotOneOf("Completed", "Cancelled")));

        var precondition = Assert.Single(transition.Preconditions);
        var not = Assert.IsType<UnaryExpr>(precondition.Expression);
        Assert.Equal(UnaryOperator.Not, not.Operator);

        var or = Assert.IsType<BinaryExpr>(not.Operand);
        Assert.Equal(BinaryOperator.Or, or.Operator);
    }

    [Fact]
    public void Compile_TranslatesConditionalExpression()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, AssignCarrierArgs>(
            entityDefinition: entity,
            transitionName: "AssignCarrierFallback",
            configure: t => t.Set(
                field: e => e.Status,
                value: (e, p) => e.CarrierId == null ? "Unassigned" : p.CarrierId));

        var update = Assert.Single(collection: transition.Updates);
        var conditional = Assert.IsType<ConditionalExpr>(update.ValueExpression);

        var test = Assert.IsType<BinaryExpr>(conditional.Test);
        Assert.Equal(expected: BinaryOperator.Eq, actual: test.Operator);
        Assert.Equal("CarrierId", ResolveFieldIdentity(Assert.IsType<FieldExpr>(test.Left)));
        Assert.Equal(ObservationValueKind.Null, Assert.IsType<ConstantExpr>(test.Right).Value.Kind);

        Assert.Equal(
            expected: "Unassigned",
            actual: Assert.IsType<ConstantExpr>(conditional.IfTrue).Value.GetString());
        Assert.Equal(
            expected: "CarrierId",
            actual: Assert.IsType<ParameterExpr>(conditional.IfFalse).Parameter);
    }

    [Fact]
    public void Compile_TranslatesCollectionAddToAppendFunction()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, AssignCarrierArgs>(
            entityDefinition: entity,
            transitionName: "AppendStop",
            configure: t => t.Add(
                field: e => e.Stops,
                value: (_, p) => p.CarrierId));

        var update = Assert.Single(transition.Updates);
        Assert.Equal("Stops", update.Field);

        var appendCall = Assert.IsType<CallExpr>(update.ValueExpression);
        Assert.Equal("append", appendCall.Function);
        Assert.Equal(2, appendCall.Arguments.Length);
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(appendCall.Arguments[0])));
        Assert.Equal("CarrierId", Assert.IsType<ParameterExpr>(appendCall.Arguments[1]).Parameter);
    }

    [Fact]
    public void Compile_TranslatesCollectionInsertToInsertAtFunction()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopArgs>(
            entityDefinition: entity,
            transitionName: "InsertStop",
            configure: t => t.Insert(
                field: e => e.Stops,
                index: (_, p) => p.Position,
                value: (_, p) => p.StopCode));

        var update = Assert.Single(transition.Updates);
        Assert.Equal("Stops", update.Field);

        var insertCall = Assert.IsType<CallExpr>(update.ValueExpression);
        Assert.Equal("insertAt", insertCall.Function);
        Assert.Equal(3, insertCall.Arguments.Length);
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(insertCall.Arguments[0])));
        Assert.Equal("Position", Assert.IsType<ParameterExpr>(insertCall.Arguments[1]).Parameter);
        Assert.Equal("StopCode", Assert.IsType<ParameterExpr>(insertCall.Arguments[2]).Parameter);
    }

    [Fact]
    public void Compile_TranslatesCollectionAddRangeToAppendRangeFunction()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, AddStopsArgs>(
            entityDefinition: entity,
            transitionName: "AppendStops",
            configure: t => t.AddRange(
                field: e => e.Stops,
                values: (_, p) => p.StopCodes));

        var update = Assert.Single(transition.Updates);
        Assert.Equal("Stops", update.Field);

        var appendRangeCall = Assert.IsType<CallExpr>(update.ValueExpression);
        Assert.Equal("appendRange", appendRangeCall.Function);
        Assert.Equal(2, appendRangeCall.Arguments.Length);
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(appendRangeCall.Arguments[0])));
        Assert.Equal("StopCodes", Assert.IsType<ParameterExpr>(appendRangeCall.Arguments[1]).Parameter);
    }

    [Fact]
    public void Compile_TranslatesCollectionInsertRangeToInsertRangeAtFunction()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopsArgs>(
            entityDefinition: entity,
            transitionName: "InsertStops",
            configure: t => t.InsertRange(
                field: e => e.Stops,
                index: (_, p) => p.Position,
                values: (_, p) => p.StopCodes
                )
            );

        var update = Assert.Single(transition.Updates);
        Assert.Equal("Stops", update.Field);

        var insertRangeCall = Assert.IsType<CallExpr>(update.ValueExpression);
        Assert.Equal("insertRangeAt", insertRangeCall.Function);
        Assert.Equal(3, insertRangeCall.Arguments.Length);
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(insertRangeCall.Arguments[0])));
        Assert.Equal("Position", Assert.IsType<ParameterExpr>(insertRangeCall.Arguments[1]).Parameter);
        Assert.Equal("StopCodes", Assert.IsType<ParameterExpr>(insertRangeCall.Arguments[2]).Parameter);
    }

    [Fact]
    public void Compile_EmitWithContinuation_TranslatesContinuationMetadata()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopArgs>(
            entityDefinition: entity,
            transitionName: "RecalculateStatus",
            configure: t => t.Emit(
                name: "CalculateMileage",
                payload: (e, state, p) => new { entityId = state.EntityId.Value, revision = p.Position, stops = e.Stops.Get(state) },
                continuationTransition: "ApplyMileage"));

        var effect = Assert.Single(transition.Effects);
        Assert.Equal("CalculateMileage", effect.Name);
        Assert.Equal("ApplyMileage", effect.Continuation?.TransitionName);
        var payload = Assert.IsType<CallExpr>(effect.Payload);
        Assert.Equal("object", payload.Function);
        Assert.Equal("entityId", Assert.IsType<ConstantExpr>(payload.Arguments[0]).Value.GetString());
        Assert.Equal("entityId", Assert.IsType<CallExpr>(payload.Arguments[1]).Function);
        Assert.Equal("stops", Assert.IsType<ConstantExpr>(payload.Arguments[4]).Value.GetString());
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(payload.Arguments[5])));
    }

    [Fact]
    public void Compile_EmitSnapshot_TranslatesSnapshotEntityIdAndFieldReads()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopArgs>(
            entityDefinition: entity,
            transitionName: "RecalculateStatusFromSnapshot",
            configure: t => t.EmitSnapshot(
                name: "CalculateMileage",
                payload: (snapshot, p) => new
                {
                    entityId = snapshot.EntityId.Value,
                    revision = p.Position,
                    stops = snapshot.Get(e => e.Stops)
                },
                continuationTransition: "ApplyMileage")
            );

        var effect = Assert.Single(transition.Effects);
        Assert.Equal("CalculateMileage", effect.Name);
        Assert.Equal("ApplyMileage", effect.Continuation?.TransitionName);
        var payload = Assert.IsType<CallExpr>(effect.Payload);
        Assert.Equal("object", payload.Function);
        Assert.Equal("entityId", Assert.IsType<ConstantExpr>(payload.Arguments[0]).Value.GetString());
        Assert.Equal("entityId", Assert.IsType<CallExpr>(payload.Arguments[1]).Function);
        Assert.Equal("stops", Assert.IsType<ConstantExpr>(payload.Arguments[4]).Value.GetString());
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(payload.Arguments[5])));
    }

    [Fact]
    public void Compile_TypedEffectRequest_UsesRequestTypeNameAndTypedPayload()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopArgs>(
            entityDefinition: entity,
            transitionName: "QueueMileageCalculation",
            configure: t => t.Request<CalculateMileageRequest, MileageCalculatedResult>(
                request: (e, state, p) => new CalculateMileageRequest(
                    OrderId: state.EntityId.Value,
                    Position: p.Position,
                    Stops: e.Stops.Get(state))));

        var effect = Assert.Single(transition.Effects);
        Assert.Equal(CalculateMileageRequest.RequestName, effect.Name);

        var payload = Assert.IsType<CallExpr>(effect.Payload);
        Assert.Equal("object", payload.Function);
        Assert.Equal("OrderId", Assert.IsType<ConstantExpr>(payload.Arguments[0]).Value.GetString());
        Assert.Equal("entityId", Assert.IsType<CallExpr>(payload.Arguments[1]).Function);
        Assert.Equal("Position", Assert.IsType<ConstantExpr>(payload.Arguments[2]).Value.GetString());
        Assert.Equal("Stops", Assert.IsType<ConstantExpr>(payload.Arguments[4]).Value.GetString());
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(payload.Arguments[5])));
    }

    [Fact]
    public void Compile_TypedEffectRequest_FromSnapshot_UsesRequestTypeNameAndTypedPayload()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, InsertStopArgs>(
            entityDefinition: entity,
            transitionName: "QueueMileageCalculationFromSnapshot",
            configure: t => t.RequestSnapshot<CalculateMileageRequest, MileageCalculatedResult>(
                request: (snapshot, p) => new CalculateMileageRequest(
                    OrderId: snapshot.EntityId.Value,
                    Position: p.Position,
                    Stops: snapshot.Get(e => e.Stops))));

        var effect = Assert.Single(transition.Effects);
        Assert.Equal(CalculateMileageRequest.RequestName, effect.Name);

        var payload = Assert.IsType<CallExpr>(effect.Payload);
        Assert.Equal("object", payload.Function);
        Assert.Equal("OrderId", Assert.IsType<ConstantExpr>(payload.Arguments[0]).Value.GetString());
        Assert.Equal("entityId", Assert.IsType<CallExpr>(payload.Arguments[1]).Function);
        Assert.Equal("Position", Assert.IsType<ConstantExpr>(payload.Arguments[2]).Value.GetString());
        Assert.Equal("Stops", Assert.IsType<ConstantExpr>(payload.Arguments[4]).Value.GetString());
        Assert.Equal("Stops", ResolveFieldIdentity(Assert.IsType<FieldExpr>(payload.Arguments[5])));
    }

    [Fact]
    public void Compile_MapsNullableTransitionParametersAsOptional()
    {
        var entity = BuildOrderEntityDefinition();

        var transition = TransitionExpressionDsl.Compile<OrderEntity, ParameterShape>(
            entityDefinition: entity,
            transitionName: "Annotate",
            configure: t => t.Requires(name: "Always", predicate: (_, _) => true));

        var byName = transition.Inputs.ToDictionary(keySelector: x => x.Name, comparer: StringComparer.Ordinal);

        Assert.True(condition: byName["CarrierId"].IsRequired);
        Assert.False(condition: byName["Note"].IsRequired);
        Assert.True(condition: byName["RetryCount"].IsRequired);
        Assert.False(condition: byName["OptionalIndex"].IsRequired);
    }

    [Fact]
    public void Compile_UnsupportedMethodCall_Throws()
    {
        var entity = BuildOrderEntityDefinition();

        var ex = Assert.Throws<TransitionExpressionTranslationException>(
            testCode: () => TransitionExpressionDsl.Compile<OrderEntity, AssignCarrierArgs>(
                entityDefinition: entity,
                transitionName: "AssignCarrier",
                configure: t => t.Requires(
                    name: "StatusMustBeDraft",
                    predicate: (e, _) => ((string)e.Status).ToUpperInvariant() == "DRAFT")));

        Assert.Contains(expectedSubstring: "Unsupported method call", actualString: ex.Message, comparisonType: StringComparison.Ordinal);
    }

    static string ResolveFieldIdentity(FieldExpr expr)
    {
        if (expr.Path.TryGetTerminalFieldIdentity(out var fieldIdentity))
            return fieldIdentity;

        throw new InvalidOperationException("Field expression path did not contain a field segment.");
    }

    static EntityDefinition BuildOrderEntityDefinition()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Order", order => order
                .Field(name: "OrderId", type: DomainTypes.String(), configure: f => f.WriteOnce())
                .Field(name: "Status", type: DomainTypes.String())
                .Field(name: "CarrierId", type: DomainTypes.String(), configure: f => f.Optional())
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
            )
        );

        return Assert.Single(collection: model.Entities);
    }

    sealed class OrderEntity : Entity
    {
        static readonly FieldDefinition OrderIdDef = FieldDefinition.Create(
            name: new FieldName(value: nameof(OrderId)),
            type: DomainTypes.String(),
            mutability: FieldMutability.WriteOnce);

        static readonly FieldDefinition StatusDef = FieldDefinition.Create(
            name: new FieldName(value: nameof(Status)),
            type: DomainTypes.String());

        static readonly FieldDefinition CarrierIdDef = FieldDefinition.Create(
            name: new FieldName(value: nameof(CarrierId)),
            type: DomainTypes.String(),
            presence: FieldPresence.Optional);

        static readonly FieldDefinition StopsDef = FieldDefinition.Create(
            name: new FieldName(value: nameof(Stops)),
            type: DomainTypes.String(),
            cardinality: FieldCardinality.Many);

        public OrderEntity()
        {
            OrderId = Field(definition: OrderIdDef, initialValue: string.Empty);
            Status = Field(definition: StatusDef, initialValue: "Draft");
            CarrierId = Field<string?>(definition: CarrierIdDef, initialValue: null);
            Stops = Field<IReadOnlyList<string>>(definition: StopsDef, initialValue: []);
        }

        public Field<string> OrderId { get; }

        public Field<string> Status { get; }

        public Field<string?> CarrierId { get; }

        public Field<IReadOnlyList<string>> Stops { get; }
    }

    sealed record AssignCarrierArgs(string CarrierId);

    sealed record InsertStopArgs(string StopCode, int Position);

    sealed record AddStopsArgs(IReadOnlyList<string> StopCodes);

    sealed record InsertStopsArgs(IReadOnlyList<string> StopCodes, int Position);

    sealed record ParameterShape(string CarrierId, string? Note, int RetryCount, int? OptionalIndex);

    sealed record MileageCalculatedResult(decimal TotalMiles);

    sealed record CalculateMileageRequest(string OrderId, int Position, IReadOnlyList<string> Stops)
        : IEffectRequest<MileageCalculatedResult>
    {
        public static string RequestName => "CalculateMileage";
    }

    sealed record NoParameters;
}
