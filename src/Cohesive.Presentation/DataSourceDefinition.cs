using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a data source consumed by views and actions.
/// </summary>
/// <param name="Id">Stable data source identifier.</param>
/// <param name="Name">Human-readable data source name.</param>
/// <param name="Kind">Semantic data source kind.</param>
/// <param name="ResultShape">Shape or contract name produced by the data source.</param>
/// <param name="Parameters">Parameters accepted by the data source.</param>
/// <param name="DefaultSort">Default ordering semantics for the data source.</param>
/// <param name="Cache">Optional cache policy for the data source.</param>
/// <param name="Invalidation">Optional invalidation policy for the data source.</param>
/// <param name="Residency">Optional hint for where the data source is evaluated or held.</param>
/// <param name="Binding">Optional adapter binding used to realize the data source.</param>
/// <param name="Annotations">Open annotations for data-source-level extension data.</param>
/// <param name="Query">Optional query capability and lowering metadata for queryable data sources.</param>
/// <param name="Aggregation">Optional aggregate query that derives this data source from another source.</param>
public sealed record DataSourceDefinition(
    string Id,
    string Name,
    DataSourceKind Kind,
    string ResultShape,
    ParameterDefinition[] Parameters,
    SortDefinition[] DefaultSort,
    CachePolicy? Cache,
    InvalidationPolicy? Invalidation,
    ResidencyHint? Residency,
    PresentationBindingDefinition? Binding,
    PresentationAnnotationDefinition[] Annotations,
    DataSourceQueryDefinition? Query = null,
    DataSourceAggregateQuery? Aggregation = null
);


/// <summary>
/// Defines invalidation semantics for a presentation data source.
/// </summary>
/// <param name="DataSourceIds">Data source identifiers invalidated by this policy.</param>
/// <param name="ActionIds">Action identifiers that trigger invalidation.</param>
/// <param name="EntityIds">Entity identifiers whose changes trigger invalidation.</param>
public sealed record InvalidationPolicy(
    string[] DataSourceIds,
    string[] ActionIds,
    string[] EntityIds
);

/// <summary>
/// Defines a default data-source sort.
/// </summary>
/// <param name="Field">Field path to sort by.</param>
/// <param name="Descending">Whether the sort is descending.</param>
public sealed record SortDefinition(
    string Field,
    bool Descending = false
);

/// <summary>
/// Classifies where and how a presentation data source obtains data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataSourceKind
{
    /// <summary>
    /// Loads a single entity or resource by identity.
    /// </summary>
    EntityById = 0,

    /// <summary>
    /// Evaluates a relation-oriented query over semantic relationship data.
    /// </summary>
    RelationQuery = 1,

    /// <summary>
    /// Evaluates a query that returns a collection of items.
    /// </summary>
    CollectionQuery = 2,

    /// <summary>
    /// Evaluates a search-oriented query over indexed or searchable data.
    /// </summary>
    SearchQuery = 3,

    /// <summary>
    /// Computes aggregate values from another data source or query.
    /// </summary>
    AggregateQuery = 4,

    /// <summary>
    /// Streams values produced by an effect or long-running operation.
    /// </summary>
    EffectStream = 5,

    /// <summary>
    /// Reads or holds client-local presentation runtime state.
    /// </summary>
    LocalState = 6,

    /// <summary>
    /// Reads data from a remote API endpoint without more specific query semantics.
    /// </summary>
    RemoteApi = 7,

    /// <summary>
    /// Provides static reference values such as enumerations, lookup data, or fixed options.
    /// </summary>
    StaticReferenceData = 8,

    /// <summary>
    /// Provides values collected from prompt or user-input state.
    /// </summary>
    PromptInput = 9,

    /// <summary>
    /// Derives values from the current presentation selection.
    /// </summary>
    SelectionDerived = 10,

    /// <summary>
    /// Provides navigation state or navigation-derived values.
    /// </summary>
    Navigation = 11,

    /// <summary>
    /// Provides presentation module metadata or module-derived values.
    /// </summary>
    Module = 12,

    /// <summary>
    /// Provides metadata that describes an entity or resource shape.
    /// </summary>
    EntityMetadata = 13,

    /// <summary>
    /// Provides preview data produced for prompt-driven or draft document flows.
    /// </summary>
    PromptPreview = 14
}


/// <summary>
/// Defines cache behavior for a presentation data source.
/// </summary>
/// <param name="Kind">Cache strategy kind.</param>
/// <param name="StaleAfterSeconds">Optional stale-after interval in seconds.</param>
public sealed record CachePolicy(
    CachePolicyKind Kind,
    int? StaleAfterSeconds = null
);

/// <summary>
/// Classifies data-source cache behavior.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CachePolicyKind
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the react query option.</summary>
    ReactQuery = 1,
    /// <summary>Represents the stale while revalidate option.</summary>
    StaleWhileRevalidate = 2,
    /// <summary>Represents the session option.</summary>
    Session = 3,
    /// <summary>Represents the memory option.</summary>
    Memory = 4
}
