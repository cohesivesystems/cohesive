namespace Cohesive.Relations.Execution;

/// <summary>Required or provided consistency of a complete query evaluation.</summary>
public enum RelationQueryConsistency
{
    /// <summary>Every read contributing to the result observes one shared snapshot.</summary>
    SingleSnapshot,
    /// <summary>Reads may observe different moments, including reads within one source. No shared snapshot is promised.</summary>
    IndependentSources,
    /// <summary>Reads an asynchronously maintained projection; source commits need not yet be visible. No read-your-writes or bounded-lag guarantee is implied.</summary>
    Eventual
}
