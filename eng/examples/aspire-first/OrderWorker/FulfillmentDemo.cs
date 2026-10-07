using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace AspireFirst.Orders;

/// <summary>Local demonstration policy, separate from domain semantics and native storage mappings.</summary>
public static class FulfillmentDemo
{
    /// <summary>Single demonstration partition; not tenant authorization.</summary>
    public const string LocalPartition = "local";

    /// <summary>Creates validated order state for an ID-only order registration.</summary>
    /// <param name="id">Order identity, normalized to standard GUID text.</param>
    /// <returns>A complete canonical write; no optimistic-concurrency precondition is asserted.</returns>
    public static EntityWriteRequest RegisterOrder(Guid id)
    {
        var identity = id.ToString("D");
        return new(FulfillmentDomain.Orders.Definition.CreateState(identity, new Order(identity, LocalPartition), version: 1).Snapshot);
    }
}
