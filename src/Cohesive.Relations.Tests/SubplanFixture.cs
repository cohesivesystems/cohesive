using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Tests;

internal static class SubplanFixture
{
    public static RelationQuery<string, ReservationAvailability[]> Definition { get; }
    public static QueryNodeId DemandProjection { get; }
    static SubplanFixture()
    {
        var query = RelationQuery.Expression();
        var id = query.Parameter<string>("id");
        var rows = query.Where(query.Source<ReservationDemand>(), row => row.OrderId == id.Value);
        var demand = query.Project(rows.Node, (ReservationDemand row) =>
            new ReservationDemand(row.OrderId, row.ReservationId, row.Sku, row.Quantity), rows.Binding);
        DemandProjection = demand.Node.Id;
        var stock = query.Source<SubplanStock>();
        var joined = query.Join(demand.Node, stock.Node, JoinKind.Left,
            (row, item) => row.Sku == item.Sku, demand.Binding, stock.Binding);
        var result = query.Project(joined, (ReservationDemand row, SubplanStock item) =>
            new ReservationAvailability(row.OrderId, row.ReservationId, row.Sku, row.Quantity, item.Available),
            demand.Binding, stock.Binding);
        Definition = query.BuildQuery(new("test/subplan"), new("Subplan"), result, id, rows => rows.ToArray());
    }
}
public sealed record ReservationDemand(string OrderId, string? ReservationId, string? Sku, int? Quantity);
public sealed record ReservationAvailability(string OrderId, string? ReservationId, string? Sku, int? Quantity, int? Available);
public sealed record SubplanStock(string Sku, int Available);
