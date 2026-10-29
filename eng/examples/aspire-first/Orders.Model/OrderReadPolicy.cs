using Cohesive.Relations.Execution;

namespace AspireFirst.Orders;

/// <summary>Explicit endpoint realization; does not change the portable OrderApi contract.</summary>
public enum OrderDetailsReadSource
{
    /// <summary>Evaluate the canonical relation against current PostgreSQL state.</summary>
    Live,
    /// <summary>Read an operator-selected asynchronous generation; read-your-writes is not promised.</summary>
    Materialized
}

/// <summary>Startup selection, shared by AppHost admission and worker registration.</summary>
public sealed record OrderReadPolicy
{
    /// <summary>Defaults to live reads; rollback requires only selecting Live and restarting the worker.</summary>
    public OrderDetailsReadSource Source { get; init; }
    /// <summary>Explicit consent to asynchronous visibility, required for Materialized.</summary>
    public bool AcceptEventualConsistency { get; init; }

    /// <summary>Validates the selection before native resources are prepared.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Source)) throw new ArgumentException("Unknown OrderReads:Source.");
        if (Source == OrderDetailsReadSource.Materialized && !AcceptEventualConsistency)
            throw new ArgumentException("Materialized order details require OrderReads:AcceptEventualConsistency=true.");
    }

    /// <summary>Required query consistency for the selected endpoint realization.</summary>
    public RelationQueryConsistency Consistency => Source == OrderDetailsReadSource.Materialized
        ? RelationQueryConsistency.Eventual : RelationQueryConsistency.IndependentSources;
}
