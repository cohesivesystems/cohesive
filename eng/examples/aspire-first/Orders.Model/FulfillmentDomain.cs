using System.Text.Json.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace AspireFirst.Orders;

/// <summary>One domain declaration; persistence and queries attach to these exact entity handles.</summary>
public sealed class FulfillmentDomain : DomainModel
{
    /// <summary>Canonical order identity field.</summary>
    public const string OrderIdField = "id";
    /// <summary>Inventory identity represented by the Cosmos observation ID.</summary>
    public const string InventoryIdField = "sku";
    /// <summary>Canonical partition field shared by the example entities.</summary>
    public const string PartitionField = "partition";
    /// <summary>Declares the shared entities and their canonical references.</summary>
    public FulfillmentDomain() : base(revision: "2")
    {
        Orders = Entity<Order>("example/order");
        Inventory = Entity<InventoryItem>("example/inventory");
        Reservations = Entity<Reservation>("example/reservation");
        ReservationOrder = Reservations.References(reservation => reservation.OrderId, Orders);
        ReservationItem = Reservations.References(reservation => reservation.Sku, Inventory);
    }

    /// <summary>Canonical order state authority.</summary>
    public DomainEntity<Order> Orders { get; }
    /// <summary>Canonical inventory state authority.</summary>
    public DomainEntity<InventoryItem> Inventory { get; }
    /// <summary>Canonical reservation state authority.</summary>
    public DomainEntity<Reservation> Reservations { get; }
    /// <summary>Reservation-to-order semantic reference, reusable by queries and later processes.</summary>
    public RelationQueryExpressionRelationship<Reservation, Order> ReservationOrder { get; }
    /// <summary>Reservation-to-inventory semantic reference.</summary>
    public RelationQueryExpressionRelationship<Reservation, InventoryItem> ReservationItem { get; }
}

/// <summary>Stock for one SKU in the local demo partition.</summary>
/// <param name="Sku">Inventory observation identity.</param>
/// <param name="Partition">Explicit local-demo partition.</param>
/// <param name="Available">Current available quantity, guarded by the reservation transition.</param>
public sealed record InventoryItem(
    [property: JsonPropertyName(FulfillmentDomain.InventoryIdField)] string Sku,
    [property: JsonPropertyName("partition")] string Partition,
    [property: JsonPropertyName("available")] int Available);

/// <summary>One reservation linking an order to a stock item; multiple reservations per order are permitted.</summary>
/// <param name="Id">Reservation observation identity.</param>
/// <param name="Partition">Explicit local-demo partition.</param>
/// <param name="OrderId">Referenced order observation identity.</param>
/// <param name="Sku">Referenced inventory observation identity.</param>
/// <param name="Quantity">Positive reserved quantity.</param>
public sealed record Reservation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("partition")] string Partition,
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName(FulfillmentDomain.InventoryIdField)] string Sku,
    [property: JsonPropertyName("quantity")] int Quantity);

/// <summary>POCO authoring source for the canonical order state.</summary>
/// <param name="Id">Order identity, serialized under its stable canonical field name.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Partition">Storage partition, not an authorization boundary.</param>
public sealed record Order(
    [property: JsonPropertyName(FulfillmentDomain.OrderIdField)] string Id,
    [property: JsonPropertyName(FulfillmentDomain.PartitionField)] string Partition,
    [property: JsonPropertyName("status")] OrderStatus Status = OrderStatus.Draft);

/// <summary>Closed lifecycle contract; serialized names are durable domain values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    /// <summary>Order may be submitted.</summary>
    [JsonStringEnumMemberName("Draft")] Draft,
    /// <summary>Order has been submitted.</summary>
    [JsonStringEnumMemberName("Submitted")] Submitted
}

/// <summary>Closed submission outcomes, independent of transport messages and status codes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubmissionOutcome>))]
public enum SubmissionOutcome
{
    /// <summary>The order was submitted.</summary>
    [JsonStringEnumMemberName("Submitted")] Submitted,
    /// <summary>The current lifecycle does not permit submission.</summary>
    [JsonStringEnumMemberName("NotDraft")] NotDraft
}

/// <summary>Domain outcome of an order submission attempt.</summary>
/// <param name="Status">Order lifecycle state reported by the decision.</param>
/// <param name="Outcome">Stable closed outcome; presentation text belongs to the API projection.</param>
public sealed record SubmitOrderResult(OrderStatus Status, SubmissionOutcome Outcome);

/// <summary>POCO-authored order lifecycle; preparation belongs to application binding.</summary>
public sealed class OrderTransitions
{
    /// <summary>Only draft orders may be submitted; a rejected decision produces no commit.</summary>
    public Transition<Order, Cohesive.Prelude.Unit, SubmitOrderResult> Submit { get; }

    /// <summary>Declares guarded order operations against the supplied domain.</summary>
    /// <param name="domain">Shared entity authority.</param>
    public OrderTransitions(FulfillmentDomain domain)
    {
        Submit = domain.Orders.Transition<SubmitOrderResult>(
            id: new("example/order/submit"), revision: new("4"),
            transition => transition
                .Requires(order => order.Status == OrderStatus.Draft, order => new SubmitOrderResult(order.Status, SubmissionOutcome.NotDraft))
                .Set(order => order.Status, OrderStatus.Submitted)
                .Return(new SubmitOrderResult(OrderStatus.Submitted, SubmissionOutcome.Submitted))
            );
    }
}

/// <summary>One-item fulfillment intent. The order must already exist.</summary>
public sealed record FulfillOrder(string OrderId, string Sku, int Quantity);
/// <summary>Public terminal outcome; physical interruption instead returns an uncertain-effect error.</summary>
public sealed record FulfillmentResult(string Status, string Reason);
/// <summary>Stock observation; the reservation transition remains authoritative for availability.</summary>
public sealed record InventoryAvailability(bool Exists, int Available);
/// <summary>Pure stock mutation outcome.</summary>
public sealed record StockResult(bool Accepted, string Reason);

/// <summary>Pure guarded stock decisions, independent of persistence and orchestration.</summary>
public sealed class InventoryTransitions
{
    /// <summary>Checks stock and decrements it atomically with the operation receipt.</summary>
    public Transition<InventoryItem, FulfillOrder, StockResult> Reserve { get; }
    /// <summary>Compensates a previously accepted reservation in this process.</summary>
    public Transition<InventoryItem, FulfillOrder, StockResult> Release { get; }

    /// <summary>Declares guarded stock operations.</summary>
    /// <param name="domain">Shared inventory authority.</param>
    public InventoryTransitions(FulfillmentDomain domain)
    {
        Reserve = domain.Inventory.Transition<FulfillOrder, StockResult>(
            id: new("fulfillment/reserve"), revision: new("1"), transition => transition
                .Requires((item, input) => item.Sku == input.Sku && input.Quantity > 0,
                    (_, _) => new StockResult(false, "Invalid reservation."))
                .Requires((item, input) => item.Available >= input.Quantity,
                    (_, _) => new StockResult(false, "Insufficient stock."))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available - input.Quantity)
                .Return(new StockResult(true, "Stock reserved.")));

        Release = domain.Inventory.Transition<FulfillOrder, StockResult>(
            id: new("fulfillment/release"), revision: new("1"), transition => transition
                .Requires((item, input) => item.Sku == input.Sku && input.Quantity > 0,
                    (_, _) => new StockResult(false, "Invalid release."))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available + input.Quantity)
                .Return(new StockResult(true, "Stock released.")));
    }
}

/// <summary>Canonical multi-entity sequence. This syntax is compiled into Process IR, never executed as C#.</summary>
[Process(id: "fulfillment/submit", revision: "4")]
public sealed partial class FulfillmentProcess : ProcessModel<FulfillOrder, FulfillmentResult>
{
    readonly EntityPointRead<InventoryItem, string, InventoryAvailability> readStock;

    readonly InventoryTransitions inventory;
    readonly OrderTransitions orders;

    /// <summary>Attaches immutable declarations before lazy process lowering.</summary>
    /// <param name="domain">Inventory authority used by the native point-read contract.</param>
    /// <param name="inventory">Stock transitions.</param>
    /// <param name="orders">Submission transition.</param>
    public FulfillmentProcess(FulfillmentDomain domain, InventoryTransitions inventory, OrderTransitions orders)
    {
        this.inventory = inventory;
        this.orders = orders;
        readStock = domain.Inventory.PointRead(
            id: new("fulfillment/stock"),
            revision: new("2"),
            result: item => new InventoryAvailability(item is not null, item?.Available ?? 0));
    }

    /// <inheritdoc />
    protected override async ProcessTask<FulfillmentResult> Execute(ProcessContext process, FulfillOrder input)
    {
        var stock = await process.Query(readStock, input.Sku);
        if (!stock.Exists)
            return new FulfillmentResult("Rejected", "Inventory does not exist.");
        // The query is advisory. A retry may observe less stock after its own prior reservation;
        // exact operation replay and the authoritative transition decide whether to mutate.
        var reserved = await process.Transition(inventory.Reserve, input.Sku, input);
        if (!reserved.Accepted)
            return new FulfillmentResult("Rejected", reserved.Reason);
        var submitted = await process.Transition(orders.Submit, input.OrderId);
        if (submitted.Outcome == SubmissionOutcome.Submitted)
            return new FulfillmentResult("Submitted", "Stock reserved and order submitted.");
        var released = await process.Transition(inventory.Release, input.Sku, input);
        if (!released.Accepted)
            return new FulfillmentResult("NeedsAttention", "Order rejected; stock compensation rejected.");
        return new FulfillmentResult("Rejected", "Order rejected; stock released.");
    }
}
