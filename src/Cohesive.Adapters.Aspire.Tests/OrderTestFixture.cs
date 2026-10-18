using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace AspireFirst.Orders;

/// <summary>Canonical order setup used only by integration fixtures.</summary>
internal static class OrderTestFixture
{
    /// <summary>Creates validated order state for an ID-only order registration.</summary>
    /// <param name="domain">Canonical order authority.</param>
    /// <param name="id">Order identity, normalized to standard GUID text.</param>
    /// <returns>A complete canonical write; no optimistic-concurrency precondition is asserted.</returns>
    public static EntityWriteRequest RegisterOrder(FulfillmentDomain domain, Guid id)
    {
        var identity = id.ToString("D");
        return new(domain.Orders.Definition.CreateState(identity, new Order(identity, FulfillmentStoragePolicy.LocalPartition), version: 1).Snapshot);
    }
}
