using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class SubplanTests
{
    [Fact]
    public void Closed_projection_derives_one_native_join_and_only_one_remaining_join()
    {
        var query = ReservationAvailabilityQuery.Definition;
        var cut = RelationQuerySubplan.Compile(query.CompilationRequest, ReservationAvailabilityQuery.DemandProjection);
        Assert.Single(cut.Prefix.Plan!.Definition.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
        Assert.Empty(cut.Remainder.Plan!.Definition.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
        Assert.Single(cut.Remainder.Plan.Definition.Body.Nodes.OfType<JoinQueryNode>());
        Assert.DoesNotContain(cut.Remainder.Plan.Definition.Body.Nodes, node => cut.CoveredNodes.Contains(node.Id) && node.Id != cut.Cut.Id);
        using var db = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var native = new PostgresPersistenceRegistration(new(new("orders"), db, "test"))
            .Entity(FulfillmentDomain.Orders, OrderStorage.Mapping)
            .Entity(FulfillmentDomain.Reservations, FulfillmentStorage.Reservations)
            .Prepare(cut.Prefix.Request, 1000, 1_000_000);
        Assert.Equal(1, native.Artifact.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
        Assert.Equal(cut.Prefix.Request.DefinitionDocument.DefinitionFingerprint, native.Plan.DefinitionFingerprint);
    }

    [Fact]
    public void Rejects_terminal_and_nonprojection_cuts()
    {
        var query = (QueryDefinition)ReservationAvailabilityQuery.Definition.CompilationRequest.DefinitionDocument.Definition;
        Assert.Throws<ArgumentException>(() => RelationQuerySubplan.Compile(ReservationAvailabilityQuery.Definition.CompilationRequest, query.Results[0].Input));
        Assert.Throws<ArgumentException>(() => RelationQuerySubplan.Compile(ReservationAvailabilityQuery.Definition.CompilationRequest,
            query.Body.Nodes.OfType<SourceQueryNode>().First().Id));
    }
    [Fact]
    public void Hidden_binding_cannot_escape_projected_interface()
    {
        var request = ReservationAvailabilityQuery.Definition.CompilationRequest;
        var query = (QueryDefinition)request.DefinitionDocument.Definition;
        var cut = query.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == ReservationAvailabilityQuery.DemandProjection);
        var terminal = query.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == query.Results[0].Input);
        // The canonical projection already hides its input bindings. Preserve that
        // validation instead of inventing a second binding environment at the cut.
        var changed = terminal with { Assignments = terminal.Assignments.SetItem(0,
            terminal.Assignments[0] with { Value = cut.Assignments[0].Value }) };
        var leaked = query with { Body = new([.. query.Body.Nodes.Select(node => node.Id == terminal.Id ? changed : node)], query.Body.Parameters) };
        var error = Assert.Throws<ArgumentException>(() => RelationQueryDocument.FromDefinition(leaked));
        Assert.Contains("bindingMissing", error.Message);
    }
}
