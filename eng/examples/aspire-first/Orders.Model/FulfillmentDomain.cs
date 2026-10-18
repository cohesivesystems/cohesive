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
    public FulfillmentDomain() : base(revision: "3")
    {
        Orders = Entity<Order>("example/order");
        Inventory = Entity<InventoryItem>("example/inventory");
        Reservations = Entity<Reservation>("example/reservation");
        ReservationOrder = Reservations.References(reservation => reservation.OrderId, Orders);
        ReservationItem = Reservations.References(reservation => reservation.Sku, Inventory);
        OrderTransitions = new(Orders);
        InventoryTransitions = new(Inventory);
        ReservationTransitions = new(Reservations);
    }

    /// <summary>Canonical order behavior, authored once against this domain's order entity.</summary>
    public OrderTransitions OrderTransitions { get; }
    /// <summary>Canonical stock behavior, authored once against this domain's inventory entity.</summary>
    public InventoryTransitions InventoryTransitions { get; }

    /// <summary>Canonical reservation creation, sharing the domain reservation entity.</summary>
    public ReservationTransitions ReservationTransitions { get; }

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

    /// <summary>Declares guarded order operations against the supplied entity.</summary>
    /// <param name="orders">Canonical order entity authority.</param>
    public OrderTransitions(DomainEntity<Order> orders)
    {
        Submit = orders.Transition<SubmitOrderResult>(
            id: new("example/order/submit"), revision: new("4"),
            transition => transition
                .Requires(order => order.Status == OrderStatus.Draft, order => new SubmitOrderResult(order.Status, SubmissionOutcome.NotDraft))
                .Set(order => order.Status, OrderStatus.Submitted)
                .Return(new SubmitOrderResult(OrderStatus.Submitted, SubmissionOutcome.Submitted))
            );
    }
}

/// <summary>One-item fulfillment intent. The order must already exist.</summary>
/// <param name="OrderId">Existing order identity.</param>
/// <param name="Sku">Inventory identity selected by the process binding.</param>
/// <param name="Quantity">Requested positive quantity.</param>
public sealed record FulfillOrder(string OrderId, string Sku, int Quantity);
/// <summary>Closed terminal status independent of transport behavior.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FulfillmentStatus>))]
public enum FulfillmentStatus
{
    /// <summary>Order submitted and reservation recorded.</summary>
    Submitted,
    /// <summary>A domain decision rejected fulfillment.</summary>
    Rejected,
    /// <summary>A follow-up transition rejected after an earlier commit.</summary>
    NeedsAttention
}
/// <summary>Closed explanation of the fulfillment decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FulfillmentOutcome>))]
public enum FulfillmentOutcome
{
    /// <summary>Stock reserved, order submitted and reservation created.</summary>
    ReservedAndSubmitted,
    /// <summary>No inventory entity exists for the requested SKU.</summary>
    InventoryMissing,
    /// <summary>Quantity must be positive.</summary>
    InvalidQuantity,
    /// <summary>Available stock cannot satisfy the request.</summary>
    InsufficientStock,
    /// <summary>Order submission rejected; reserved stock was released.</summary>
    OrderRejected,
    /// <summary>Stock release rejected and requires reconciliation.</summary>
    CompensationRejected,
    /// <summary>Reservation creation rejected after order submission.</summary>
    ReservationRejected
}
/// <summary>Typed public terminal result; infrastructure interruption is not a domain outcome.</summary>
/// <param name="Status">Terminal workflow status.</param>
/// <param name="Outcome">Closed explanation of the terminal decision.</param>
public sealed record FulfillmentResult(FulfillmentStatus Status, FulfillmentOutcome Outcome);
/// <summary>Quantity-only inventory intent; the binding selects the authoritative SKU.</summary>
/// <param name="Quantity">Positive units to reserve or release.</param>
public sealed record StockQuantity(int Quantity);
/// <summary>Closed stock mutation outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StockOutcome>))]
public enum StockOutcome
{
    /// <summary>Stock was decremented.</summary>
    Reserved,
    /// <summary>Previously reserved stock was returned.</summary>
    Released,
    /// <summary>Quantity must be positive.</summary>
    InvalidQuantity,
    /// <summary>Available stock cannot satisfy the request.</summary>
    InsufficientStock
}
/// <summary>Pure stock mutation result.</summary>
/// <param name="Outcome">Closed decision returned by the stock transition.</param>
public sealed record StockResult(StockOutcome Outcome);
/// <summary>Closed reservation creation outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReservationCreationOutcome>))]
public enum ReservationCreationOutcome
{
    /// <summary>The reservation fact was created.</summary>
    Created,
    /// <summary>Reservation quantity must be positive.</summary>
    InvalidQuantity
}

/// <summary>Reservations are created through the same conditional commit and receipt protocol as other entities.</summary>
public sealed class ReservationTransitions
{
    /// <summary>Creates an absent reservation; retries resolve the original creation receipt.</summary>
    public Transition<Reservation, Reservation, ReservationCreationOutcome> Create { get; }
    /// <summary>Declares reservation creation against the canonical entity.</summary>
    /// <param name="reservations">Canonical reservation entity.</param>
    public ReservationTransitions(DomainEntity<Reservation> reservations)
    {
        Create = reservations.Transition<Reservation, ReservationCreationOutcome>(
            id: new("fulfillment/reservation/create"), revision: new("1"), transition => transition
                .CreatesFrom(new("reservation/initialize"), input => new Reservation(input.Id, input.Partition, input.OrderId, input.Sku, input.Quantity))
                .Requires((reservation, _) => reservation.Quantity > 0, (_, _) => ReservationCreationOutcome.InvalidQuantity)
                .Return(ReservationCreationOutcome.Created));
    }
}

/// <summary>Pure guarded stock decisions, independent of persistence and orchestration.</summary>
public sealed class InventoryTransitions
{
    /// <summary>Checks stock and decrements it atomically with the operation receipt.</summary>
    public Transition<InventoryItem, StockQuantity, StockResult> Reserve { get; }
    /// <summary>Compensates a previously accepted reservation in this process.</summary>
    public Transition<InventoryItem, StockQuantity, StockResult> Release { get; }

    /// <summary>Declares guarded stock operations.</summary>
    /// <param name="inventory">Canonical inventory entity authority.</param>
    public InventoryTransitions(DomainEntity<InventoryItem> inventory)
    {
        Reserve = inventory.Transition<StockQuantity, StockResult>(
            id: new("fulfillment/reserve"), revision: new("2"), transition => transition
                .Requires((item, input) => input.Quantity > 0,
                    (_, _) => new StockResult(StockOutcome.InvalidQuantity))
                .Requires((item, input) => item.Available >= input.Quantity,
                    (_, _) => new StockResult(StockOutcome.InsufficientStock))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available - input.Quantity)
                .Return(new StockResult(StockOutcome.Reserved)));

        Release = inventory.Transition<StockQuantity, StockResult>(
            id: new("fulfillment/release"), revision: new("2"), transition => transition
                .Requires((item, input) => input.Quantity > 0,
                    (_, _) => new StockResult(StockOutcome.InvalidQuantity))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available + input.Quantity)
                .Return(new StockResult(StockOutcome.Released)));
    }
}

/// <summary>Canonical multi-entity sequence. This syntax is compiled into Process IR, never executed as C#.</summary>
/// <param name="domain">Entity and transition authority used by the process.</param>
[Process(id: "fulfillment/submit", revision: "6")]
public sealed partial class FulfillmentProcess(FulfillmentDomain domain) : ProcessModel<FulfillOrder, FulfillmentResult>
{
    /// <inheritdoc />
    protected override async ProcessTask<FulfillmentResult> Execute(ProcessContext process, FulfillOrder input)
    {
        var stock = await process.PointRead(domain.Inventory, input.Sku,
            policy: new ProcessReadPolicy(timeout: TimeSpan.FromSeconds(2)));
        if (stock.Entity == null)
            return new FulfillmentResult(FulfillmentStatus.Rejected, FulfillmentOutcome.InventoryMissing);
        // The query is advisory. A retry may observe less stock after its own prior reservation;
        // exact operation replay and the authoritative transition decide whether to mutate.
        var reserved = await process.Transition(domain.InventoryTransitions.Reserve, input.Sku, new StockQuantity(input.Quantity));
        if (reserved.Outcome == StockOutcome.InvalidQuantity)
            return new FulfillmentResult(FulfillmentStatus.Rejected, FulfillmentOutcome.InvalidQuantity);
        if (reserved.Outcome == StockOutcome.InsufficientStock)
            return new FulfillmentResult(FulfillmentStatus.Rejected, FulfillmentOutcome.InsufficientStock);
        var submitted = await process.Transition(domain.OrderTransitions.Submit, input.OrderId);
        if (submitted.Outcome == SubmissionOutcome.Submitted)
        {
            // This one-item workflow owns one reservation per order. Creation is a receipted transition,
            // so replay after order submission can complete this step without reserving inventory again.
            var reservation = await process.Transition(domain.ReservationTransitions.Create, input.OrderId,
                new Reservation(input.OrderId, stock.Entity.Partition, input.OrderId, input.Sku, input.Quantity));
            if (reservation != ReservationCreationOutcome.Created)
                return new FulfillmentResult(FulfillmentStatus.NeedsAttention, FulfillmentOutcome.ReservationRejected);
            return new FulfillmentResult(FulfillmentStatus.Submitted, FulfillmentOutcome.ReservedAndSubmitted);
        }
        var released = await process.Transition(domain.InventoryTransitions.Release, input.Sku, new StockQuantity(input.Quantity));
        if (released.Outcome != StockOutcome.Released)
            return new FulfillmentResult(FulfillmentStatus.NeedsAttention, FulfillmentOutcome.CompensationRejected);
        return new FulfillmentResult(FulfillmentStatus.Rejected, FulfillmentOutcome.OrderRejected);
    }
}
