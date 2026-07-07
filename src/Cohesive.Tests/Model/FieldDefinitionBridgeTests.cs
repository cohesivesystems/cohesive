using Cohesive.Model;

namespace Cohesive.Tests.Model;

/// <summary>
/// Tests for bridging runtime fields with field definitions.
/// </summary>
public sealed class FieldDefinitionBridgeTests
{
    [Fact]
    public void DefineField_AttachesFieldDefinitionToRuntimeField()
    {
        var entity = new BridgedCarrierEntity();
        var state = entity.CreateState("carrier-1");
        
        Assert.Equal(nameof(BridgedCarrierEntity.Capacity), entity.Capacity.Definition.Name.Value);
        Assert.Equal(nameof(BridgedCarrierEntity.Capacity), entity.Capacity.Name);

        var result = entity.UpdateCapacity.Apply(state, new BridgedCarrierEntity.CapacityValue(42));
        Assert.Equal(42, entity.Capacity.Get(result.NewState));
        var effect = Assert.Single(result.Effects);
        Assert.Equal("CapacityUpdated", effect.Name);
        Assert.Equal(ObservationValueKind.Object, effect.Payload.Kind);
        Assert.Equal("Capacity", effect.Payload.GetProperty("field").GetString());
        Assert.Equal(42L, effect.Payload.GetProperty("value").GetInt64());
    }

    [Fact]
    public void Entity_ExposesCompiledEntityDefinition()
    {
        var entity = new BridgedCarrierEntity();
        var definition = entity.Definition;

        Assert.Equal(new EntityTypeName(nameof(BridgedCarrierEntity)), definition.Name);
        Assert.Equal(3, definition.Fields.Length);
        Assert.Single(definition.Invariants);
        Assert.Equal(2, definition.Transitions.Length);
        Assert.Contains(definition.Transitions, x => x.Name == nameof(BridgedCarrierEntity.UpdateCapacity));
        Assert.Contains(definition.Transitions, x => x.Name == nameof(BridgedCarrierEntity.UpdateMaxCapacity));
    }

    [Fact]
    public void Entity_ReusesCompiledDefinitionAcrossInstancesOfSameClrType()
    {
        var first = new BridgedCarrierEntity();
        var second = new BridgedCarrierEntity();

        Assert.Same(first.Definition, second.Definition);
        Assert.Same(first.Capacity.Definition, second.Capacity.Definition);
        Assert.Same(first.UpdateCapacity.Definition, second.UpdateCapacity.Definition);
    }

    [Fact]
    public void EntityState_ExposesIdentityVersionAndObservationBackedState()
    {
        var entity = new BridgedCarrierEntity();
        var state = entity.CreateState("carrier-context-1");

        Assert.Equal("carrier-context-1", state.EntityId.Value);
        Assert.Equal(0, state.Version);
        Assert.Equal(10, state.Fields[nameof(BridgedCarrierEntity.Capacity)].GetInt32());

        var result = entity.UpdateCapacity.Apply(state, new BridgedCarrierEntity.CapacityValue(42));

        Assert.Equal(1, result.NewState.Version);
        Assert.Equal(42, result.NewState.Fields[nameof(BridgedCarrierEntity.Capacity)].GetInt32());
    }

    [Fact]
    public void DefineField_TypeMismatchBetweenFieldDefAndFieldType_Throws()
    {
        Assert.Throws<SemanticRuleViolationException>(() => new TypeMismatchEntity());
    }

    [Fact]
    public void DefineField_DuplicateFieldNames_Throws()
    {
        Assert.Throws<SemanticRuleViolationException>(() => new DuplicateIdEntity());
    }

    [Fact]
    public void WriteOnceField_CannotBeOverwrittenInsideTransition()
    {
        var entity = new BridgedCarrierEntity();
        var state = entity.CreateState("carrier-2");

        Assert.Throws<SemanticRuleViolationException>(() => entity.UpdateMaxCapacity.Apply(state, new BridgedCarrierEntity.CapacityValue(12)));
    }

    [Fact]
    public void RequiredField_NullInitialValue_Throws()
    {
        Assert.Throws<SemanticRuleViolationException>(() => new RequiredFieldNullEntity());
    }

    [Fact]
    public void DefineField_QuantityType_UsesStructuredQuantityWithoutBackingScalarField()
    {
        var entity = new QuantityRouteEntity();
        var state = entity.CreateState("route-1");
        var result = entity.UpdateDistance.Apply(state, new QuantityRouteEntity.DistanceInput(Distance.FromMiles(218m)));

        Assert.Equal(218m, entity.PlannedDistance.Get(result.NewState).Miles);
        Assert.Equal(1, result.NewVersion);
    }

    [Fact]
    public void DefineField_InferredDefinition_MapsClrShapeNullabilityAndCollectionCardinality()
    {
        var entity = new InferredTelemetryEntity();
        var definition = entity.Definition;

        var payload = Assert.Single(definition.Fields, x => x.Name.Value == nameof(InferredTelemetryEntity.Payload));
        Assert.Equal(FieldCardinality.Single, payload.Cardinality);
        var payloadType = Assert.IsType<ObjectTypeRef>(payload.Type);
        Assert.Contains(payloadType.Fields, x => x.Name == nameof(TelemetryPayload.Code) && x.Presence == FieldPresence.Required && x.Type == DomainTypes.String());
        Assert.Contains(payloadType.Fields, x => x.Name == nameof(TelemetryPayload.Depth) && x.Presence == FieldPresence.Required && x.Type == DomainTypes.Int32());
        Assert.Contains(payloadType.Fields, x => x.Name == nameof(TelemetryPayload.Note) && x.Presence == FieldPresence.Optional && x.Type == DomainTypes.String());
        Assert.Contains(payloadType.Fields, x => x.Name == nameof(TelemetryPayload.Score) && x.Presence == FieldPresence.Optional && x.Type == DomainTypes.Decimal());

        var samples = Assert.Single(definition.Fields, x => x.Name.Value == nameof(InferredTelemetryEntity.Samples));
        Assert.Equal(FieldCardinality.Many, samples.Cardinality);
        _ = Assert.IsType<ObjectTypeRef>(samples.Type);

        var optionalStatus = Assert.Single(definition.Fields, x => x.Name.Value == nameof(InferredTelemetryEntity.OptionalStatus));
        Assert.Equal(FieldPresence.Optional, optionalStatus.Presence);
        Assert.Equal(DomainTypes.String(), optionalStatus.Type);
    }

    [Fact]
    public void ComputedField_TypedStringConcatenation_LowersToConcatFunctionAndComputesState()
    {
        var entity = new PartitionedDatasetEntity();
        var definition = entity.Definition;
        var partitionKey = Assert.Single(definition.Fields, x => x.Name.Value == nameof(PartitionedDatasetEntity.PartitionKey));
        Assert.NotNull(partitionKey.Compute);
        var compute = Assert.IsType<CallExpr>(partitionKey.Compute!.Expression);

        Assert.Equal(ExprFunctionNames.Concat, compute.Function);
        Assert.Equal(3, compute.Arguments.Length);
        Assert.Equal(nameof(PartitionedDatasetEntity.Tenant), Assert.IsType<FieldExpr>(compute.Arguments[0]).Path.ToString());
        Assert.Equal(":", Assert.IsType<ConstantExpr>(compute.Arguments[1]).Value.GetString());
        Assert.Equal(nameof(PartitionedDatasetEntity.DatasetName), Assert.IsType<FieldExpr>(compute.Arguments[2]).Path.ToString());

        var state = entity.CreateState("dataset-1", new
        {
            Tenant = "acme",
            DatasetName = "edi-logistics-mappings"
        });

        Assert.Equal("acme:edi-logistics-mappings", entity.PartitionKey.Get(state));
    }

    [Fact]
    public void ComputedField_TypedCollectionCount_ComputesFromMaterializedState()
    {
        var entity = new CountedRouteEntity();
        var definition = entity.Definition;
        var stopCount = Assert.Single(definition.Fields, x => x.Name.Value == nameof(CountedRouteEntity.StopCount));
        Assert.NotNull(stopCount.Compute);
        var compute = Assert.IsType<CallExpr>(stopCount.Compute!.Expression);

        Assert.Equal(ExprFunctionNames.Count, compute.Function);
        Assert.Single(compute.Arguments);
        Assert.Equal(nameof(CountedRouteEntity.Stops), Assert.IsType<FieldExpr>(compute.Arguments[0]).Path.ToString());

        var state = entity.CreateState("route-1", new
        {
            Stops = new[] { "SFO", "SEA", "PDX" }
        });

        Assert.Equal(3, entity.StopCount.Get(state));
    }

    sealed class BridgedCarrierEntity : Entity
    {
        static readonly FieldDefinition CapacityDef = FieldDefinition.Create(
            name: new(nameof(Capacity)),
            type: DomainTypes.Int32()
            );
        
        static readonly FieldDefinition MaxCapacityDef = FieldDefinition.Create(
            name: new(nameof(MaxCapacity)),
            type: DomainTypes.Int32(),
            mutability: FieldMutability.WriteOnce
        );

        static readonly FieldDefinition TagsDef = FieldDefinition.Create(
            name: new(nameof(Tags)),
            type: DomainTypes.String(),
            cardinality: FieldCardinality.Many,
            presence: FieldPresence.Optional
            );

        public BridgedCarrierEntity()
        {
            Capacity = Field(CapacityDef, 10);
            MaxCapacity = Field(MaxCapacityDef, 20);
            Tags = Field<IReadOnlyList<string>>(TagsDef, []);

            UpdateMaxCapacity = Transition<BridgedCarrierEntity, CapacityValue>(
                nameof(UpdateMaxCapacity),
                t => t.Set(e => e.MaxCapacity, (_, p) => p.Value));

            UpdateCapacity = Transition<BridgedCarrierEntity, CapacityValue>(
                nameof(UpdateCapacity),
                t => t
                    .Set(e => e.Capacity, (_, p) => p.Value)
                    .Emit("CapacityUpdated", (e, p) => new { field = "Capacity", value = p.Value }));

            Invariant<BridgedCarrierEntity>(
                "CapacityNonNegative",
                e => e.Capacity >= 0 && e.MaxCapacity >= 0);

        }

        public Field<int> Capacity { get; }
        
        public Field<int> MaxCapacity { get; }
        
        public Field<IReadOnlyList<string>> Tags { get; }

        public Transition<BridgedCarrierEntity, CapacityValue> UpdateMaxCapacity { get; }

        public Transition<BridgedCarrierEntity, CapacityValue> UpdateCapacity { get; }

        public sealed record CapacityValue(int Value);
    }

    sealed class TypeMismatchEntity : Entity
    {
        static readonly FieldDefinition StringFieldDef = FieldDefinition.Create(
            name: new FieldName(value: "Value"),
            type: DomainTypes.String()
            );

        public TypeMismatchEntity()
        {
            // Intentionally mismatched generic type to verify compatibility checks.
            _ = Field(StringFieldDef, 5);
        }
    }

    sealed class PartitionedDatasetEntity : Entity<PartitionedDatasetEntity>
    {
        public PartitionedDatasetEntity()
        {
            Tenant = WriteOnceField<string>(nameof(Tenant));
            DatasetName = WriteOnceField<string>(nameof(DatasetName));
            PartitionKey = ComputedField(nameof(PartitionKey), x => x.Tenant + ":" + x.DatasetName);
        }

        public Field<string> Tenant { get; }

        public Field<string> DatasetName { get; }

        public Field<string> PartitionKey { get; }
    }

    sealed class CountedRouteEntity : Entity<CountedRouteEntity>
    {
        public CountedRouteEntity()
        {
            Stops = MutableField<string[]>(nameof(Stops));
            StopCount = ComputedField(nameof(StopCount), x => x.Stops.Count);
        }

        public Field<string[]> Stops { get; }

        public Field<int> StopCount { get; }
    }

    sealed class DuplicateIdEntity : Entity
    {
        static readonly FieldDefinition FirstDef = FieldDefinition.Create(
            name: new FieldName(value: "First"),
            type: DomainTypes.Int32()
            );

        static readonly FieldDefinition SecondDef = FieldDefinition.Create(
            name: new FieldName(value: "First"),
            type: DomainTypes.Int32()
            );

        public DuplicateIdEntity()
        {
            _ = Field(FirstDef, 1);
            _ = Field(SecondDef, 2);
        }
    }

    sealed class RequiredFieldNullEntity : Entity
    {
        static readonly FieldDefinition RequiredNameDef = FieldDefinition.Create(
            name: new FieldName(value: "Name"),
            type: DomainTypes.String(),
            presence: FieldPresence.Required
            );

        public RequiredFieldNullEntity()
        {
            _ = Field<string?>(RequiredNameDef, initialValue: null);
        }
    }

    sealed class QuantityRouteEntity : Entity
    {
        static readonly FieldDefinition PlannedDistanceDef = FieldDefinition.Create(
            name: new FieldName(nameof(PlannedDistance)),
            type: DomainTypes.Quantity(nameof(Distance)));

        public QuantityRouteEntity()
        {
            PlannedDistance = Field(PlannedDistanceDef, Distance.AdditiveIdentity);
            UpdateDistance = Transition<QuantityRouteEntity, DistanceInput>(
                name: nameof(UpdateDistance),
                configure: t => t.Set(e => e.PlannedDistance, (_, p) => p.Value));
        }

        public Field<Distance> PlannedDistance { get; }

        public Transition<QuantityRouteEntity, DistanceInput> UpdateDistance { get; }

        public sealed record DistanceInput(Distance Value);
    }

    sealed class InferredTelemetryEntity : Entity
    {
        public InferredTelemetryEntity()
        {
            Payload = Field<TelemetryPayload>(
                name: nameof(Payload),
                configure: field => field.WriteOnce());
            Samples = Field<TelemetryPayload[]>(
                name: nameof(Samples),
                configure: field => field.WriteOnce());
            OptionalStatus = Field<string?>(
                name: nameof(OptionalStatus),
                configure: field => field.WriteOnce());
        }

        public Field<TelemetryPayload> Payload { get; }

        public Field<TelemetryPayload[]> Samples { get; }

        public Field<string?> OptionalStatus { get; }
    }

    public readonly record struct TelemetryPayload
    {
        public required string Code { get; init; }

        public short Depth { get; init; }

        public string? Note { get; init; }

        public float? Score { get; init; }
    }
}
