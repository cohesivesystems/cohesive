using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace AspireFirst.Orders;

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
[Process(id: "fulfillment/submit", revision: "3")]
public sealed partial class FulfillmentProcess : ProcessModel<FulfillOrder, FulfillmentResult>
{
    // A native point read is an explicit hosted query; no SQL or repository call enters the Process IR.
    /// <summary>Explicit native inventory point-read contract.</summary>
    public HostedQuery<FulfillOrder, InventoryAvailability> StockPointRead { get; }

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
        StockPointRead = HostedQuery<FulfillOrder, InventoryAvailability>.Create(
            new("fulfillment/stock"),
            new("1"),
            new("aspire-first.inventory-point-read", "1"),
            domain.Inventory.Definition.Shape.Id.Value,
            Provenance
            );

    }

    /// <inheritdoc />
    protected override async ProcessTask<FulfillmentResult> Execute(ProcessContext process, FulfillOrder input)
    {
        var stock = await process.Query(StockPointRead, input);
        if (!stock.Exists)
            return new FulfillmentResult("Rejected", "Inventory does not exist.");
        // The query is advisory. A retry may observe less stock after its own prior reservation;
        // exact operation replay and the authoritative transition decide whether to mutate.
        var reserved = await process.Transition<StockResult>(inventory.Reserve.Reference, input.Sku, input);
        if (!reserved.Accepted)
            return new FulfillmentResult("Rejected", reserved.Reason);
        var submitted = await process.Transition(orders.Submit, input.OrderId);
        if (submitted.Outcome == SubmissionOutcome.Submitted)
            return new FulfillmentResult("Submitted", "Stock reserved and order submitted.");
        var released = await process.Transition<StockResult>(inventory.Release.Reference, input.Sku, input);
        if (!released.Accepted)
            return new FulfillmentResult("NeedsAttention", "Order rejected; stock compensation rejected.");
        return new FulfillmentResult("Rejected", "Order rejected; stock released.");
    }
}
