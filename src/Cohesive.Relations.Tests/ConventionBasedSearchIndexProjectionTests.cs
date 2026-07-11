using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Demonstrates ergonomic relation execution from DTOs and observed shapes.
/// </summary>
public sealed class ConventionBasedSearchIndexProjectionTests
{
    static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task RelationMappingRuntime_MinimalConfiguration_ExecuteObservedAsync_ProjectsObservation()
    {
        var (relation, runtime, inputs, expected) = CreateMinimalConfigurationFixture();
        
        var outputs = await runtime.ExecuteObservedAsync(relation, inputs);

        var output = Assert.Single(outputs);
        Assert.Equal(expected.OutputSchema, output.ShapeId);

        var outputDto = runtime.MappingContext.Map<SearchOrderDto>(output);
        AssertProjected(outputDto, expected.OutputDto);
    }

    [Fact]
    public async Task RelationMappingRuntime_MinimalConfiguration_ExecuteAsync_ProjectsDto()
    {
        var (relation, runtime, inputs, expected) = CreateMinimalConfigurationFixture();
        var dtoOutputs = await runtime.ExecuteAsync<SearchOrderDto>(relation, inputs);
        var dtoOutput = Assert.Single(dtoOutputs);
        AssertProjected(dtoOutput, expected.OutputDto);
    }

    [Fact]
    public async Task RelationMappingRuntime_ProgressiveOverrides_ExecuteObservedAsync_SupportsMixedInputs()
    {
        var fixture = CreateProgressiveOverridesFixture();
        var observedOutputs = await fixture.Runtime.ExecuteObservedAsync(fixture.Definition, fixture.RuntimeInputs);

        var observed = Assert.Single(observedOutputs);
        Assert.Equal(fixture.Expected.OutputSchema, observed.ShapeId);

        var projected = fixture.Runtime.MappingContext.Map<SearchOrderDto>(observed);
        AssertProjected(projected, fixture.Expected.OutputDto);
        Assert.NotEmpty(fixture.Listener.Traces);
    }

    [Fact]
    public async Task RelationMappingRuntime_ProgressiveOverrides_ExecuteAsync_SupportsMixedInputs()
    {
        var fixture = CreateProgressiveOverridesFixture();
        var dtoOutputs = await fixture.Runtime.ExecuteAsync<SearchOrderDto>(fixture.Definition, fixture.RuntimeInputs);

        var projected = Assert.Single(dtoOutputs);
        AssertProjected(projected, fixture.Expected.OutputDto);
        Assert.NotEmpty(fixture.Listener.Traces);
    }

    static void AssertProjected(SearchOrderDto projected, SearchOrderDto expected)
    {
        Assert.Equal(expected.OrderNumber, projected.OrderNumber);
        Assert.Equal(expected.Stops, projected.Stops.ToArray());
        Assert.Equal(expected.StopCount, projected.StopCount);
        Assert.Equal(expected.Customer, projected.Customer);
        Assert.Equal(expected.LaneContract, projected.LaneContract);
    }

    static RelationDefinition CreateSearchProjection()
    {
        return Relation<SearchOrderDto>
            .From<OrderDto>()
            .Join<CustomerDto>((order, customer) => order.CustomerId == customer.Id)
            .Join<ContractDto>((order, customer, contract) => order.ContractId == contract.Id)
            .Select((order, customer, contract) => new(
                OrderNumber: order.OrderNumber,
                Stops: order.Stops,
                StopCount: order.Stops.Count,
                Customer: new(
                    Id: order.CustomerId,
                    Name: customer.Name
                    ),
                LaneContract: new(
                    Id: order.ContractId,
                    Lane: contract.Lane,
                    Mode: contract.Mode,
                    Rate: contract.Rate
                    )
                )
            );
    }

    static MinimalConfigurationFixture CreateMinimalConfigurationFixture()
    {
        var sample = CreateMinimalConfigurationSample();
        return new(
            Definition: CreateSearchProjection(),
            Runtime: new(),
            ObjectInputs: ToObjectInputs(sample),
            Expected: ToExpected(sample)
            );
    }

    static ProgressiveOverridesFixture CreateProgressiveOverridesFixture()
    {
        var sample = CreateProgressiveOverridesSample();
        var listener = new CapturingExecutionListener();
        var mappingContext = new ShapeMappingContext
        {
            RequireJsonPropertyNameAttributeForFieldIdentity = true,
            ObservationObjectSerializerOptions = CaseInsensitiveJson
        };
        return new(
            Definition: CreateSearchProjection(),
            Runtime: new(
                mappingContext: mappingContext,
                relationExecutor: new RelationExecutor(options: new() { Listener = listener})
                ),
            RuntimeInputs: ToProgressiveRuntimeInputs(sample, mappingContext),
            Expected: ToExpected(sample),
            Listener: listener
            );
    }

    static SearchProjectionSample CreateMinimalConfigurationSample()
    {
        return new(
            Order: new(
                Id: "order-8821",
                OrderNumber: "ORD-8821",
                Stops: ["PICKUP", "MID", "DELIVERY"],
                CustomerId: "cust-9",
                ContractId: "contract-77"
                ),
            Customer: new(
                Id: "cust-9",
                Name: "Acme Foods"
                ),
            Contract: new(
                Id: "contract-77",
                Lane: "DAL-HOU",
                Mode: "Reefer",
                Rate: 1350.25m
                ),
            Projected: new(
                OrderNumber: "ORD-8821",
                Stops: ["PICKUP", "MID", "DELIVERY"],
                StopCount: 3,
                Customer: new(
                    Id: "cust-9",
                    Name: "Acme Foods"
                    ),
                LaneContract: new(
                    Id: "contract-77",
                    Lane: "DAL-HOU",
                    Mode: "Reefer",
                    Rate: 1350.25m
                    )
                )
            );
    }

    static SearchProjectionSample CreateProgressiveOverridesSample()
    {
        return new(
            Order: new(
                Id: "order-9901",
                OrderNumber: "ORD-9901",
                Stops: ["PICKUP", "DELIVERY"],
                CustomerId: "cust-44",
                ContractId: "contract-44"
                ),
            Customer: new(
                Id: "cust-44",
                Name: "Northwind"
                ),
            Contract: new(
                Id: "contract-44",
                Lane: "SEA-PDX",
                Mode: "DryVan",
                Rate: 920.5m
                ),
            Projected: new(
                OrderNumber: "ORD-9901",
                Stops: ["PICKUP", "DELIVERY"],
                StopCount: 2,
                Customer: new(
                    Id: "cust-44",
                    Name: "Northwind"
                    ),
                LaneContract: new(
                    Id: "contract-44",
                    Lane: "SEA-PDX",
                    Mode: "DryVan",
                    Rate: 920.5m
                    )
                )
            );
    }

    static IReadOnlyList<object> ToObjectInputs(SearchProjectionSample sample) => [sample.Order, sample.Customer, sample.Contract];

    static IReadOnlyList<RelationRuntimeInput> ToProgressiveRuntimeInputs(SearchProjectionSample sample, ShapeMappingContext mappingContext)
    {
        var orderObserved = mappingContext.Map(sample.Order);
        return
        [
            RelationRuntimeInput.From(orderObserved),
            RelationRuntimeInput.From(sample.Customer),
            RelationRuntimeInput.From(sample.Contract, schemaId: new ShapeId(nameof(ContractDto)))
        ];
    }

    static SearchProjectionExpected ToExpected(SearchProjectionSample sample)
        => new(OutputSchema: new(nameof(SearchOrderDto)), OutputDto: sample.Projected);

    sealed record SearchProjectionSample(
        OrderDto Order,
        CustomerDto Customer,
        ContractDto Contract,
        SearchOrderDto Projected
        );

    sealed record SearchProjectionExpected(
        ShapeId OutputSchema,
        SearchOrderDto OutputDto
        );

    sealed record MinimalConfigurationFixture(
        RelationDefinition Definition,
        RelationMappingRuntime Runtime,
        IReadOnlyList<object> ObjectInputs,
        SearchProjectionExpected Expected
        );

    sealed record ProgressiveOverridesFixture(
        RelationDefinition Definition,
        RelationMappingRuntime Runtime,
        IReadOnlyList<RelationRuntimeInput> RuntimeInputs,
        SearchProjectionExpected Expected,
        CapturingExecutionListener Listener
        );

    sealed record OrderDto(
        [property: JsonPropertyName("fld_order_id")] string Id,
        [property: JsonPropertyName("fld_order_number")] string OrderNumber,
        [property: JsonPropertyName("fld_order_stops")] IReadOnlyList<string> Stops,
        [property: JsonPropertyName("fld_customer_id")] string CustomerId,
        [property: JsonPropertyName("fld_contract_id")] string ContractId
        );

    sealed record CustomerDto(
        [property: JsonPropertyName("fld_customer_id")] string Id,
        [property: JsonPropertyName("fld_customer_name")] string Name
        );

    sealed record ContractDto(
        [property: JsonPropertyName("fld_contract_id")] string Id,
        [property: JsonPropertyName("fld_contract_lane")] string Lane,
        [property: JsonPropertyName("fld_contract_mode")] string Mode,
        [property: JsonPropertyName("fld_contract_rate")] decimal Rate
        );

    sealed record SearchOrderDto(
        [property: JsonPropertyName("idx_order_number")] string OrderNumber,
        [property: JsonPropertyName("idx_stops")] IReadOnlyList<string> Stops,
        [property: JsonPropertyName("idx_stop_count")] int StopCount,
        [property: JsonPropertyName("idx_customer")] SearchCustomerDto Customer,
        [property: JsonPropertyName("idx_lane_contract")] SearchLaneContractDto LaneContract
        );

    sealed record SearchCustomerDto(string Id, string Name);

    sealed record SearchLaneContractDto(string Id, string Lane, string Mode, decimal Rate);

    sealed class CapturingExecutionListener : IRelationExecutionListener
    {
        public List<RelationAssignmentTrace> Traces { get; } = [];

        public void OnAssignment(RelationAssignmentTrace trace) => Traces.Add(trace);
    }
}
