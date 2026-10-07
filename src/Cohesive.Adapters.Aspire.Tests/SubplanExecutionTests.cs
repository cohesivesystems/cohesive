using System.Collections.Immutable;
using AspireFirst.Orders;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class SubplanExecutionTests
{
    [Fact]
    public async Task Preparation_is_once_rows_are_invocation_local_and_duplicate_occurrences_survive()
    {
        var fixture = new Fixture();
        var reader = fixture.Prepare();
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => reader.ReadAsync("order-" + i)));
        Assert.Equal(1, fixture.Registrations);
        Assert.Equal(8, fixture.Prefix.Reads);
        Assert.Equal(8, fixture.InventoryReads);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(2, values[i].Length);
            Assert.All(values[i], row => Assert.Equal("order-" + i, row.OrderId));
            Assert.All(values[i], row => Assert.Null(row.Available));
        }
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("invalid-shape")]
    [InlineData("provider-failure")]
    public async Task Prefix_failure_does_not_read_remaining_backend_or_retry(string failure)
    {
        var fixture = new Fixture();
        fixture.Prefix.Execute = (_, _) => failure switch
        {
            "overflow" => Task.FromResult(Enumerable.Repeat(Row("order"), 5).ToImmutableArray()),
            "invalid-shape" => Task.FromResult(ImmutableArray.Create(ObservationValue.FromObject(42))),
            _ => throw new InvalidOperationException("provider failure")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Prepare().ReadAsync("order"));
        Assert.Equal(1, fixture.Prefix.Reads);
        Assert.Equal(0, fixture.InventoryReads);
    }

    [Fact]
    public async Task Cancellation_prevents_dispatch_and_incomplete_remaining_evidence_cannot_be_typed_success()
    {
        var fixture = new Fixture { InventoryState = RelationQuerySourceReadState.Partial };
        var reader = fixture.Prepare();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("order", new(true)));
        Assert.Equal(0, fixture.Prefix.Reads);
        var outcome = await reader.EvaluateAsync("order", new("partial"));
        Assert.NotNull(outcome.Remainder.PhysicalExecution);
        Assert.Contains(outcome.Remainder.PhysicalExecution.SourceReads, trace => trace.State == RelationQuerySourceReadState.Partial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync("order"));
        Assert.Equal(2, fixture.Prefix.Reads); // two explicit invocations, never an implicit retry
    }

    [Fact]
    public void Prefix_plan_affinity_is_checked_before_registration_or_IO()
    {
        var fixture = new Fixture();
        fixture.Prefix.Plan = RelationQueryCompiledPlanReference.From(fixture.Cut.Original.Plan!);
        Assert.Throws<ArgumentException>(() => fixture.Prepare());
        Assert.Equal(0, fixture.Registrations);
        Assert.Equal(0, fixture.Prefix.Reads);
    }

    [Fact]
    public void Projected_occurrences_cannot_claim_domain_key_uniqueness()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Prepare(claimSemanticIdentity: true));
        Assert.Equal(0, fixture.Registrations);
        Assert.Equal(0, fixture.Prefix.Reads);
    }

    static ObservationValue Row(string order) => ObservationValue.FromObject(new ReservationDemand(order, "reservation", "sku", 2));

    sealed class Fixture
    {
        public RelationQuerySubplan Cut { get; } = RelationQuerySubplan.Compile(
            ReservationAvailabilityQuery.Definition.CompilationRequest, ReservationAvailabilityQuery.DemandProjection);
        public PrefixReader Prefix { get; }
        public int Registrations;
        public int InventoryReads;
        public RelationQuerySourceReadState InventoryState = RelationQuerySourceReadState.Complete;

        public Fixture()
        {
            Prefix = new(RelationQueryCompiledPlanReference.From(Cut.Prefix.Plan!), async (parameters, cancellation) =>
            {
                await Task.Yield();
                cancellation.ThrowIfCancellationRequested();
                var row = Row(parameters.Single().Value.GetRequiredString());
                return [row, row]; // bag multiplicity must not collapse into domain identity
            });
        }

        public RelationQuerySubplanReader<string, ReservationAvailability[]> Prepare(bool claimSemanticIdentity = false)
        {
            var builder = RelationQueryPlacement.For(Cut.Remainder.Plan!);
            var projected = builder.Source("prefix", RelationQueryProjectedRowset.Profile, new("orders"), limits: new(4, 4, 4, 1));
            var external = builder.Source("inventory", RelationQueryProjectedRowset.Profile, new("inventory"), limits: new(4, 4, 4, 1));
            foreach (var input in Cut.Remainder.Plan!.InputContract.Sources)
            {
                if (input.Node == Cut.Cut.Id)
                {
                    var placed = builder.Place(input, projected);
                    if (claimSemanticIdentity) placed.Identity(Cut.Cut.Assignments[0].Target, "row");
                    else placed.Identity("row");
                    placed.FieldsBySemanticPath();
                }
                else
                    builder.Place(input, external).Identity(FieldPath.FromField(FulfillmentStorage.Inventory.IdentityField), "sku").FieldsBySemanticPath();
            }
            var placement = builder.Build().RequireValue();
            var partition = new RelationQueryLogicalPartitionIdentity("test/local");
            return new(ReservationAvailabilityQuery.Definition, Cut, Prefix, placement.Placement,
                new(new("test/subplan"), "test/v1", 4, 4, 4, 4, 4, 1), _ =>
                {
                    Registrations++;
                    return [new EmptyInventoryReader(new(external.Id, new("inventory"), RelationQueryProjectedRowset.Profile, partition), this)];
                }, partition);
        }
    }

    sealed class PrefixReader(RelationQueryCompiledPlanReference plan,
        Func<IReadOnlyDictionary<QueryParameterId, ObservationValue>, CancellationToken, Task<ImmutableArray<ObservationValue>>> execute) : IRelationQueryRowsReader
    {
        public RelationQueryCompiledPlanReference Plan { get; set; } = plan;
        public Func<IReadOnlyDictionary<QueryParameterId, ObservationValue>, CancellationToken, Task<ImmutableArray<ObservationValue>>> Execute = execute;
        public int Reads;
        public Task<ImmutableArray<ObservationValue>> ReadAsync(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Reads); return Execute(parameters, cancellationToken); }
    }

    sealed class EmptyInventoryReader(RelationQuerySourceReaderDescriptor descriptor, Fixture fixture) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor => descriptor;
        public ValueTask<RelationQuerySourceReadResult> ReadAsync(RelationQuerySourceReadRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref fixture.InventoryReads); return ValueTask.FromResult(new RelationQuerySourceReadResult(fixture.InventoryState)); }
    }
}
