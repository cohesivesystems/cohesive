using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;

namespace Cohesive.Storage;

/// <summary>
/// Canonical source reader that projects one semantic query view from one exact persisted entity observation type.
/// </summary>
public interface IEntityRelationQuerySourceReader : IRelationQuerySourceReader
{
    /// <summary>Exact graph-qualified semantic source-view shape projected by the reader.</summary>
    QualifiedShapeId Shape { get; }

    /// <summary>Exact graph-qualified entity observation retained by the backing repository.</summary>
    QualifiedShapeId PersistedObservationType { get; }
}
