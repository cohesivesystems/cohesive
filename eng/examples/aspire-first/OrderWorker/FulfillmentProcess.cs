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
public static class InventoryTransitions
{
    /// <summary>Checks stock and decrements it atomically with the operation receipt.</summary>
    public static Transition<InventoryItem, FulfillOrder, StockResult> Reserve { get; } =
        TransitionAuthoring.Create<InventoryItem, FulfillOrder, StockResult>(FulfillmentDomain.Inventory.Definition.Shape,
            id: new("fulfillment/reserve"), revision: new("1"), transition => transition
                .Requires((item, input) => item.Sku == input.Sku && input.Quantity > 0,
                    (_, _) => new StockResult(false, "Invalid reservation."))
                .Requires((item, input) => item.Available >= input.Quantity,
                    (_, _) => new StockResult(false, "Insufficient stock."))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available - input.Quantity)
                .Return(new StockResult(true, "Stock reserved.")));

    /// <summary>Compensates a previously accepted reservation in this process.</summary>
    public static Transition<InventoryItem, FulfillOrder, StockResult> Release { get; } =
        TransitionAuthoring.Create<InventoryItem, FulfillOrder, StockResult>(FulfillmentDomain.Inventory.Definition.Shape,
            id: new("fulfillment/release"), revision: new("1"), transition => transition
                .Requires((item, input) => item.Sku == input.Sku && input.Quantity > 0,
                    (_, _) => new StockResult(false, "Invalid release."))
                .Set(new("stock/update"), item => item.Available, (item, input) => item.Available + input.Quantity)
                .Return(new StockResult(true, "Stock released.")));
}

/// <summary>Canonical multi-entity sequence. This syntax is compiled into Process IR, never executed as C#.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class FulfillmentProcess
{
    /// <summary>Stable authoring provenance for the process and its hosted query.</summary>
    public static ExecutionProvenance Provenance { get; } = new(new("aspire-first", "1"),
        new("fulfillment/process"), DocumentOrigin.Generated);

    // A native point read is an explicit hosted query; no SQL or repository call enters the Process IR.
    /// <summary>Explicit native inventory point-read contract.</summary>
    public static HostedQuery<FulfillOrder, InventoryAvailability> Stock { get; } =
        HostedQuery<FulfillOrder, InventoryAvailability>.Create(new("fulfillment/stock"), new("1"),
            new("aspire-first.inventory-point-read", "1"), FulfillmentDemo.LocalPartition, Provenance);

    /// <summary>Generated portable sequence, including explicit domain compensation.</summary>
    public static Process<FulfillOrder, FulfillmentResult> Definition { get; } = Define(new(
        new("fulfillment/submit"), new("1"), ProcessRecoveryPolicy.ContinueAttempt, Provenance));

    static async ProcessTask<FulfillmentResult> Run(ProcessContext process, FulfillOrder input)
    {
        var stock = await process.Query(Stock, input);
        if (!stock.Exists)
            return new FulfillmentResult("Rejected", "Inventory does not exist.");
        // The query is advisory. A retry may observe less stock after its own prior reservation;
        // exact operation replay and the authoritative transition decide whether to mutate.
        var reserved = await process.Transition<StockResult>(InventoryTransitions.Reserve.Reference, input.Sku, input);
        if (!reserved.Accepted)
            return new FulfillmentResult("Rejected", reserved.Reason);
        var submitted = await process.Transition<SubmitOrderResult>(OrderTransitions.Submit.Reference,
            input.OrderId, new SubmitOrder(input.OrderId));
        if (submitted.Accepted)
            return new FulfillmentResult("Submitted", "Stock reserved and order submitted.");
        var released = await process.Transition<StockResult>(InventoryTransitions.Release.Reference, input.Sku, input);
        if (!released.Accepted)
            return new FulfillmentResult("NeedsAttention", "Order rejected; stock compensation rejected.");
        return new FulfillmentResult("Rejected", "Order rejected; stock released.");
    }
}
