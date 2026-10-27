using AspireFirst.Orders;
using Cohesive.Application;

namespace AspireFirst.Materialization;

/// <summary>Offline composition sharing the live application's queries with its materialization declarations.</summary>
public sealed class OrderMaterializationApplication : ApplicationModel
{
    /// <summary>Existing domain, API and storage composition; live bindings are unchanged.</summary>
    public required FulfillmentApplication Orders { get; init; }
    /// <summary>Materializations resolved from the same query model instance.</summary>
    public required FulfillmentMaterializations Materializations { get; init; }
}
