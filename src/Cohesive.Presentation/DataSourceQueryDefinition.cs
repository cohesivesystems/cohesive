using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Declares the semantic query capability exposed by a queryable data source.
/// </summary>
/// <param name="ModelShape">
/// Shape or runtime model that represents the executable query capability behind the data source.
/// </param>
/// <param name="ValueShape">
/// Optional interaction value shape edited by a projected form or other client-side query surface.
/// </param>
/// <param name="RequestShape">
/// Optional endpoint request shape used by the operational binding that realizes the query.
/// </param>
/// <param name="ResponseShape">Optional response envelope shape returned by the operational binding.</param>
/// <param name="ItemShape">Optional item shape produced by the query result.</param>
/// <param name="FactDataSourceIds">Reference or fact data sources that can enrich query editing.</param>
/// <param name="Fields">Semantic query fields exposed for projection, validation, and lowering.</param>
/// <param name="EndpointBindings">Operational endpoint bindings that can realize this query.</param>
/// <param name="Annotations">Open annotations for query-model-level extension data.</param>
/// <param name="Pagination">Optional pagination semantics for collection-shaped query results.</param>
public sealed record DataSourceQueryDefinition(
    string ModelShape,
    string? ValueShape,
    string? RequestShape,
    string? ResponseShape,
    string? ItemShape,
    string[] FactDataSourceIds,
    DataSourceQueryFieldDefinition[] Fields,
    DataSourceQueryEndpointBindingDefinition[] EndpointBindings,
    PresentationAnnotationDefinition[] Annotations,
    DataSourcePaginationDefinition? Pagination = null
);

/// <summary>
/// Declares how a queryable data source pages its result collection.
/// </summary>
/// <param name="Kind">Pagination state family used by the query.</param>
/// <param name="DefaultPageSize">Default page size projected by clients when no page size is supplied.</param>
/// <param name="Request">Request-shape field bindings used to send pagination state.</param>
/// <param name="Response">Response-shape field bindings used to read pagination state.</param>
/// <param name="Url">Optional URL synchronization policy for clients with addressable navigation.</param>
/// <param name="Annotations">Open annotations for pagination-level extension data.</param>
public sealed record DataSourcePaginationDefinition(
    DataSourcePaginationKind Kind,
    int? DefaultPageSize,
    DataSourcePaginationRequestBindingDefinition Request,
    DataSourcePaginationResponseBindingDefinition Response,
    DataSourcePaginationUrlPolicyDefinition? Url,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies pagination state families for collection queries.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataSourcePaginationKind
{
    Cursor = 0,
    Offset = 1,
    PageNumber = 2
}

/// <summary>
/// Request-shape fields used to transmit pagination state to an endpoint.
/// </summary>
/// <param name="LimitField">Maximum item count field.</param>
/// <param name="CursorField">Cursor field for cursor pagination.</param>
/// <param name="OffsetField">Offset field for skip/limit pagination.</param>
/// <param name="SkipField">Alternate skip field for skip/limit pagination.</param>
/// <param name="PageField">Page field for page-number pagination.</param>
/// <param name="PageNumberField">Alternate page-number field for page-number pagination.</param>
public sealed record DataSourcePaginationRequestBindingDefinition(
    string? LimitField = null,
    string? CursorField = null,
    string? OffsetField = null,
    string? SkipField = null,
    string? PageField = null,
    string? PageNumberField = null
);

/// <summary>
/// Response-shape fields used to interpret pagination state from an endpoint result.
/// </summary>
/// <param name="CursorField">Cursor field used to request the next page.</param>
/// <param name="HasNextPageField">Boolean field indicating that another page exists.</param>
/// <param name="LimitField">Applied page-size field.</param>
/// <param name="OffsetField">Applied offset field.</param>
/// <param name="PageField">Applied page field.</param>
/// <param name="PageNumberField">Applied page-number field.</param>
/// <param name="TotalCountField">Total matching item count field.</param>
/// <param name="TotalCountDataSourceId">
/// Optional data source that supplies <paramref name="TotalCountField"/> when
/// the count is produced by a summary or synchronized data source instead of
/// the paged collection response.
/// </param>
public sealed record DataSourcePaginationResponseBindingDefinition(
    string? CursorField = null,
    string? HasNextPageField = null,
    string? LimitField = null,
    string? OffsetField = null,
    string? PageField = null,
    string? PageNumberField = null,
    string? TotalCountField = null,
    string? TotalCountDataSourceId = null
);

/// <summary>
/// Declares whether pagination state should be encoded into client-visible URLs.
/// </summary>
/// <param name="IsEnabled">Whether URL synchronization is enabled.</param>
/// <param name="ParameterPrefix">Prefix used for page, page-size, cursor, and offset parameters.</param>
public sealed record DataSourcePaginationUrlPolicyDefinition(
    bool IsEnabled,
    string? ParameterPrefix = null
);

/// <summary>
/// Describes one projected query field and the paths it occupies across semantic, value, and transport shapes.
/// </summary>
/// <param name="Id">Stable field identifier within the query definition.</param>
/// <param name="Name">Human-readable field name.</param>
/// <param name="ModelPaths">Executable query model paths affected by this field.</param>
/// <param name="ValuePath">Optional interaction value path edited by the client.</param>
/// <param name="RequestPaths">Optional endpoint request paths populated by this field.</param>
/// <param name="Operators">Supported predicate operators or operation intents.</param>
/// <param name="FieldId">Optional presentation field definition that describes rendering/editing semantics.</param>
/// <param name="ChoiceDataSourceId">Optional fact data source that supplies selectable values.</param>
/// <param name="ChoiceItemsPath">Optional path within the choice data source result that contains selectable values.</param>
/// <param name="Transform">Optional semantic transform used when projecting or lowering this field.</param>
/// <param name="Annotations">Open annotations for field-level extension data.</param>
public sealed record DataSourceQueryFieldDefinition(
    string Id,
    string Name,
    string[] ModelPaths,
    string? ValuePath,
    string[] RequestPaths,
    string[] Operators,
    string? FieldId,
    string? ChoiceDataSourceId,
    string? ChoiceItemsPath,
    string? Transform,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds a semantic data-source query to an endpoint that operationally realizes it.
/// </summary>
/// <param name="EndpointId">Endpoint identifier used to execute the query.</param>
/// <param name="RequestShape">Endpoint request shape.</param>
/// <param name="ResponseShape">Optional endpoint response envelope shape.</param>
/// <param name="ItemsPath">Optional path to result items within the response envelope.</param>
/// <param name="Lowerings">Explicit lowerings needed to move between value, request, and query model shapes.</param>
/// <param name="Annotations">Open annotations for endpoint-binding-level extension data.</param>
public sealed record DataSourceQueryEndpointBindingDefinition(
    string EndpointId,
    string RequestShape,
    string? ResponseShape,
    string? ItemsPath,
    QueryLoweringDefinition[] Lowerings,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares a directed lowering between two query-related shapes.
/// </summary>
/// <param name="Id">Stable lowering identifier.</param>
/// <param name="Kind">Kind of query lowering.</param>
/// <param name="SourceShape">Source shape for the lowering.</param>
/// <param name="TargetShape">Target shape produced by the lowering.</param>
/// <param name="FieldBindings">Field-level bindings that realize the lowering.</param>
/// <param name="Annotations">Open annotations for lowering-level extension data.</param>
public sealed record QueryLoweringDefinition(
    string Id,
    QueryLoweringKind Kind,
    string SourceShape,
    string TargetShape,
    QueryFieldBindingDefinition[] FieldBindings,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Maps one source query value path to one target query value path.
/// </summary>
/// <param name="SourcePath">Source path read during lowering.</param>
/// <param name="TargetPath">Target path written during lowering.</param>
/// <param name="FieldId">Optional presentation field definition associated with this binding.</param>
/// <param name="Transform">Optional transform identifier applied while lowering.</param>
/// <param name="DefaultValue">Optional literal default used when the source path is absent.</param>
/// <param name="Annotations">Open annotations for field-binding-level extension data.</param>
public sealed record QueryFieldBindingDefinition(
    string SourcePath,
    string TargetPath,
    string? FieldId,
    string? Transform,
    string? DefaultValue,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies how two query-related shapes are connected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryLoweringKind
{
    /// <summary>
    /// Lowers client-side interaction state into an endpoint request shape.
    /// </summary>
    PresentationValueToEndpointRequest = 0,

    /// <summary>
    /// Lowers an endpoint request shape into the executable semantic query model.
    /// </summary>
    EndpointRequestToQueryModel = 1,

    /// <summary>
    /// Lowers the executable semantic query model into an endpoint request shape.
    /// </summary>
    QueryModelToEndpointRequest = 2
}
