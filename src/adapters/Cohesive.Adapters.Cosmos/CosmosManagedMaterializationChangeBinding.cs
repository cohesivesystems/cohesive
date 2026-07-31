using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Persisted Cosmos observation-envelope family selected by a managed materialization source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CosmosManagedMaterializationDocumentKind
{
    /// <summary>Current entity documents whose semantic identity normally comes from <c>observationId</c>.</summary>
    Entity = 0,

    /// <summary>
    /// Transactional outbox documents whose message identity normally comes from <c>id</c> and whose entity payload
    /// and message metadata may both be projected.
    /// </summary>
    Outbox = 1
}

/// <summary>
/// Explicit semantic filter binding one managed Cosmos change processor to entity or outbox documents.
/// </summary>
/// <remarks>
/// The binding is deliberately separate from the processor name and worker instance. It participates in semantic
/// scope and delivery identity, while lease ownership never does. <see cref="PersistedObservationType"/> is
/// graph-qualified even though the persisted envelope stores only its shape component in <c>observationType</c>.
/// It may differ from the managed reader's projected shape.
/// </remarks>
public sealed record CosmosManagedMaterializationChangeBinding
{
    /// <summary>Creates one explicit entity or outbox document binding.</summary>
    /// <param name="kind">Entity or outbox envelope semantics.</param>
    /// <param name="documentKind">Exact persisted <c>documentKind</c> discriminator.</param>
    /// <param name="persistedObservationType">
    /// Exact graph-qualified type whose shape component is stored in matching envelopes. For outbox documents this
    /// may be the embedded entity type and therefore differ from the reader's projected message shape.
    /// </param>
    /// <param name="streamName">
    /// Optional exact outbox stream filter. Entity bindings must omit it; a null outbox stream selects every stream.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="documentKind"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, <paramref name="documentKind"/> or <paramref name="streamName"/> is empty, or an
    /// entity binding supplies a stream filter.
    /// </exception>
    [JsonConstructor]
    public CosmosManagedMaterializationChangeBinding(
        CosmosManagedMaterializationDocumentKind kind,
        string documentKind,
        QualifiedShapeId persistedObservationType,
        string? streamName = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported managed Cosmos document kind.");
        }

        DocumentKind = Guard.RequireNotNullOrWhiteSpace(documentKind);
        if (string.IsNullOrWhiteSpace(persistedObservationType.GraphId.Value)
            || string.IsNullOrWhiteSpace(persistedObservationType.ShapeId.Value))
        {
            throw new ArgumentException(
                "A managed Cosmos binding requires a graph-qualified observation type.",
                nameof(persistedObservationType));
        }

        if (streamName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        }

        if (kind == CosmosManagedMaterializationDocumentKind.Entity && streamName is not null)
        {
            throw new ArgumentException(
                "An entity-document binding cannot carry an outbox stream filter.",
                nameof(streamName));
        }

        Kind = kind;
        PersistedObservationType = persistedObservationType;
        StreamName = streamName;
    }

    /// <summary>Entity or outbox envelope semantics.</summary>
    public CosmosManagedMaterializationDocumentKind Kind { get; }

    /// <summary>Exact persisted <c>documentKind</c> discriminator.</summary>
    public string DocumentKind { get; }

    /// <summary>Exact graph-qualified observation type stored in matching Cosmos envelopes.</summary>
    public QualifiedShapeId PersistedObservationType { get; }

    /// <summary>Optional exact outbox stream filter; null selects every stream.</summary>
    public string? StreamName { get; }
}
