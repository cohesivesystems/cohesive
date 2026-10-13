namespace AspireFirst;

/// <summary>Native resource names shared by AppHost provisioning and workload connection lookup.</summary>
internal static class FulfillmentResources
{
    /// <summary>Local order database connection name.</summary>
    public const string DatabaseName = "orders";
    /// <summary>Optional independently placed inventory database connection name.</summary>
    public const string InventoryDatabaseName = "inventory";
}
