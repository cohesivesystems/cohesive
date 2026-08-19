using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

static class RelationDtoMapperTestFixture
{
    public static RelationQueryExecutionResult RewriteRelation(
        RelationQueryExecutionResult execution,
        Func<RelationQueryOutputRow, RelationQueryOutputRow> rewriteRow,
        RelationId? relation = null,
        QualifiedShapeId? shape = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(rewriteRow);
        var source = execution.Relation
            ?? throw new ArgumentException("The execution must contain a relation terminal.", nameof(execution));
        var rewrittenRows = source.Rows.Select(rewriteRow).ToImmutableArray();
        var rewrittenShape = shape ?? source.Shape;
        var terminal = new RelationQueryRelationResult(
            relation ?? source.Relation,
            rewrittenShape,
            source.Mode,
            source.State,
            rewrittenRows);
        return new(
            execution.Status,
            execution.Evidence,
            execution.RequirementGapAnalysis,
            terminal,
            queryResults: [],
            execution.Diagnostics);
    }

    public static RelationQueryOutputRow RewriteValue(
        RelationQueryOutputRow source,
        ObservationValue value,
        QualifiedShapeId? shape = null) => new(
        shape ?? source.Shape,
        value,
        source.Identity,
        source.Root,
        source.InputOccurrences,
        source.UnresolvedGaps);

    public static async Task<FederatedScenario> ExecuteFederatedAsync()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: 2);
        var source = Assert.Single(compilation.Plan.InputContract.Sources);
        var placement = compilation.Placement.Bindings.Single(binding => binding.Input == source.Input.Id);
        var selections = source.Fields.ToDictionary(
            static field => field.Input.Field.Path,
            field =>
            {
                var fieldPlacement = placement.Fields.Single(candidate => candidate.Input == field.Input.Id);
                return new RelationQuerySourceReadField(
                    field.Input.Id,
                    field.Input.Field.Path,
                    fieldPlacement.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput);
            });
        var supplied = new RelationQuerySuppliedSourceInput(
            source.Input.Id,
            RelationQueryLogicalPartitionIdentity.WholeSource,
            RelationQueryEvidenceCompleteness.Complete,
            [
                SuppliedLoad("load-1", "customer-1", "equipment-1", selections),
                SuppliedLoad("load-2", "customer-2", "equipment-2", selections)
            ],
            "tests/relation-dto-mapper/supplied-loads");

        var readers = CreateFederatedReaders(compilation);
        var result = await new RelationQueryPhysicalExecutor(readers).ExecuteAsync(new(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("tests/relation-dto-mapper/federated"),
            suppliedSources: [supplied],
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan)));
        return new(compilation.Plan, result);
    }

    public static NumericWideningScenario CreateNumericWideningScenario()
    {
        QueryNodeId projectionNode = new("project-numeric-widening");
        Cohesive.Relations.IR.RelationDefinition definition = new(
            new("dto-numeric-widening"),
            new("DtoNumericWidening"),
            new(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    projectionNode,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.AggregateBinding,
                    LoadCustomerRelationFixture.LoadAggregateShapeId,
                    [
                        new(
                            new("assign-widening-customer-name"),
                            LoadCustomerRelationFixture.AggregateCustomerNamePath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath)),
                        new(
                            new("assign-widening-total-amount"),
                            LoadCustomerRelationFixture.AggregateTotalAmountPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadAmountPath)),
                        new(
                            new("assign-widening-load-count"),
                            LoadCustomerRelationFixture.AggregateLoadCountPath,
                            Expr.Const(1L))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                projectionNode,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(
                    LoadCustomerRelationFixture.AggregateBinding,
                    LoadCustomerRelationFixture.AggregateCustomerNamePath)));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            RelationQueryDocument.FromDefinition(definition),
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            compilation.IsSuccessful,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var source = Assert.Single(plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>());
        var occurrence = new RelationQueryObservationOccurrence(
            new("widening-load-1"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-1");
        var fields = plan.RequirementGraph.Inputs
            .OfType<RelationQueryFieldInput>()
            .Select(input => new RelationQueryFieldEvidence(
                input.Id,
                occurrence.Id,
                RelationQueryFieldEvidenceState.Value,
                input.Field.Path == LoadCustomerRelationFixture.LoadStatusPath
                    ? ObservationValue.FromString("Available")
                    : input.Field.Path == LoadCustomerRelationFixture.LoadAmountPath
                        ? ObservationValue.FromDecimal(12.5m)
                        : throw new InvalidOperationException(
                            $"Unexpected numeric-widening input field '{input.Field.Path}'.")))
            .ToImmutableArray();
        var capabilities = plan.RequirementGraph.Inputs
            .OfType<RelationQueryCapabilityInput>()
            .Select(static input => new RelationQueryCapabilityEvidence(
                input.Id,
                RelationQueryCapabilityEvidenceState.Available,
                "tests/relation-dto-mapper/numeric-widening"))
            .ToImmutableArray();
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/relation-dto-mapper/numeric-widening"),
            plan,
            sources:
            [
                new(
                    source.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [occurrence])
            ],
            fields: fields,
            capabilities: capabilities);
        var execution = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence));
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, execution.Status);
        return new(plan, execution);
    }

    static RelationQuerySourceReadObservation SuppliedLoad(
        string id,
        string customerId,
        string equipmentId,
        IReadOnlyDictionary<FieldPath, RelationQuerySourceReadField> selections) => new(
        id,
        FederatedLoadRelationFixture.LoadShapeId,
        [
            .. selections.Select(pair => new RelationQuerySourceReadFieldResult(
                pair.Value,
                RelationQuerySourceReadFieldState.Value,
                pair.Key == FederatedLoadRelationFixture.LoadIdPath
                    ? ObservationValue.FromString(id)
                    : pair.Key == FederatedLoadRelationFixture.LoadCustomerIdPath
                        ? ObservationValue.FromString(customerId)
                        : pair.Key == FederatedLoadRelationFixture.LoadEquipmentIdPath
                            ? ObservationValue.FromString(equipmentId)
                            : throw new InvalidOperationException($"Unexpected supplied Load field '{pair.Key}'.")))
        ]);

    static ImmutableArray<IRelationQuerySourceReader> CreateFederatedReaders(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation)
    {
        var loads = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource);
        var customers = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource);
        var equipment = FederatedLoadPhysicalExecutionFixture.Source(
            compilation,
            FederatedLoadPhysicalExecutionFixture.EquipmentSource);
        return
        [
            Reader(loads, []),
            Reader(
                customers,
                [
                    Customer("customer-1", "Customer One", "Priority"),
                    Customer("customer-2", "Customer Two", "Standard")
                ]),
            Reader(
                equipment,
                [
                    Equipment("equipment-1", "TRUCK-001", "Tractor"),
                    Equipment("equipment-2", "TRAILER-002", "Trailer")
                ])
        ];
    }

    static DeterministicRelationQuerySourceReader Reader(
        Cohesive.Relations.Physical.RelationQuerySourceInstance source,
        ImmutableArray<DeterministicRelationQuerySourceReader.SourceRow> rows) => new(
        new(
            source.Id,
            source.ExecutionDomain,
            source.TargetProfile,
            RelationQueryLogicalPartitionIdentity.WholeSource),
        rows);

    static DeterministicRelationQuerySourceReader.SourceRow Customer(
        string id,
        string name,
        string type) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (FederatedLoadRelationFixture.CustomerIdPath, ObservationValue.FromString(id)),
        (FederatedLoadRelationFixture.CustomerNamePath, ObservationValue.FromString(name)),
        (FederatedLoadRelationFixture.CustomerTypePath, ObservationValue.FromString(type)));

    static DeterministicRelationQuerySourceReader.SourceRow Equipment(
        string id,
        string number,
        string type) => DeterministicRelationQuerySourceReader.SourceRow.Create(
        id,
        (FederatedLoadRelationFixture.EquipmentIdPath, ObservationValue.FromString(id)),
        (FederatedLoadRelationFixture.EquipmentNumberPath, ObservationValue.FromString(number)),
        (FederatedLoadRelationFixture.EquipmentTypePath, ObservationValue.FromString(type)));

    internal sealed record FederatedScenario(
        CompiledRelationQueryPlan Plan,
        RelationQueryPhysicalExecutionResult Result);

    internal sealed record NumericWideningScenario(
        CompiledRelationQueryPlan Plan,
        RelationQueryExecutionResult Execution);
}

sealed record FederatedLoadSearchDto(string Id, string? CustomerName, string? EquipmentNumber);
