using System.Text.Json.Serialization;
using Cohesive.Prelude;

namespace Cohesive.Presentation;

/// <summary>
/// Defines presentation semantics for a shape or entity field.
/// </summary>
/// <param name="Id">Stable field presentation identifier.</param>
/// <param name="Field">Shape or entity field path.</param>
/// <param name="Label">Human-readable field label.</param>
/// <param name="Description">Optional field description or help text.</param>
/// <param name="DisplayKind">Target-independent display intent.</param>
/// <param name="EditKind">Target-independent edit intent.</param>
/// <param name="Format">Optional formatting semantics.</param>
/// <param name="Display">Optional value display semantics that refine the display kind.</param>
/// <param name="Source">Optional value source binding for projected renderers and generated adapters.</param>
/// <param name="Capabilities">Field capabilities such as sorting, filtering, editing, or navigation.</param>
/// <param name="Design">Target-independent design intent for the field.</param>
/// <param name="Accessibility">Target-independent accessibility semantics for the field.</param>
/// <param name="Annotations">Open annotations for field-level extension data.</param>
/// <param name="Icon">Optional semantic icon identifier associated with the field.</param>
public sealed record FieldPresentationDefinition(
    string Id,
    string Field,
    string Label,
    string? Description,
    FieldDisplayKind DisplayKind,
    FieldEditKind EditKind,
    FormatDefinition? Format,
    FieldDisplayOptions? Display,
    FieldValueSourceDefinition? Source,
    string[] Capabilities,
    DesignIntent? Design,
    AccessibilityContract? Accessibility,
    PresentationAnnotationDefinition[] Annotations,
    string? Icon = null
);

/// <summary>
/// Refines target-independent field display behavior without binding the field
/// to a concrete frontend component.
/// </summary>
/// <param name="Tone">Semantic tone used by badges, status chips, and related compact value renderers.</param>
/// <param name="EmptyValueLabel">Optional label rendered when the field value is absent.</param>
/// <param name="ValueLabels">Optional labels for serialized scalar values.</param>
/// <param name="ValueTones">Optional semantic tones for serialized scalar values.</param>
/// <param name="ValueIcons">Optional semantic icons for serialized scalar values.</param>
/// <param name="ValuePrefix">Optional prefix applied to scalar value text after label resolution.</param>
/// <param name="LabelFieldPaths">Optional field paths used as human-facing labels for the primary field value.</param>
/// <param name="ToneFieldPaths">Optional field paths used as dynamic semantic tones.</param>
/// <param name="InlineBadges">Optional badge values rendered beside the primary field value.</param>
/// <param name="SupportingValues">Optional supporting value definitions rendered under the primary value.</param>
/// <param name="SupportingFieldPaths">Optional field paths rendered as supporting values under the primary value.</param>
/// <param name="FallbackFieldPaths">Optional field paths consulted when the primary value is absent.</param>
/// <param name="EntityReferenceFallback">How an entity reference should render when no navigation interpretation is available.</param>
/// <param name="JsonMode">Preferred JSON display mode for read-only JSON values.</param>
public sealed record FieldDisplayOptions(
    string? Tone = null,
    string? EmptyValueLabel = null,
    FieldValueLabelDefinition[]? ValueLabels = null,
    FieldValueToneDefinition[]? ValueTones = null,
    FieldValueIconDefinition[]? ValueIcons = null,
    string? ValuePrefix = null,
    string[]? LabelFieldPaths = null,
    string[]? ToneFieldPaths = null,
    FieldInlineBadgeDefinition[]? InlineBadges = null,
    FieldSupportingValueDefinition[]? SupportingValues = null,
    string[]? SupportingFieldPaths = null,
    string[]? FallbackFieldPaths = null,
    FieldEntityReferenceFallbackKind? EntityReferenceFallback = null,
    FieldJsonDisplayMode? JsonMode = null
);

/// <summary>
/// Defines a compact badge rendered beside a field's primary value.
/// </summary>
/// <param name="FieldPath">Field path that supplies the badge label.</param>
/// <param name="Tone">Optional static semantic tone for the badge.</param>
/// <param name="ToneFieldPath">Optional field path that supplies the badge tone.</param>
public sealed record FieldInlineBadgeDefinition(
    string FieldPath,
    string? Tone = null,
    string? ToneFieldPath = null
);

/// <summary>
/// Defines a supporting value rendered near a field's primary value.
/// </summary>
/// <param name="FieldPath">Field path that supplies the supporting value.</param>
/// <param name="Prefix">Optional prefix applied when the value is present.</param>
/// <param name="Suffix">Optional suffix applied when the value is present.</param>
/// <param name="Separator">Optional separator used when the value is an array.</param>
public sealed record FieldSupportingValueDefinition(
    string FieldPath,
    string? Prefix = null,
    string? Suffix = null,
    string? Separator = null
);

/// <summary>
/// Defines a human-facing label for a serialized scalar field value.
/// </summary>
/// <param name="Value">Serialized field value.</param>
/// <param name="Label">Human-facing value label.</param>
public sealed record FieldValueLabelDefinition(
    string Value,
    string Label
);

/// <summary>
/// Defines a semantic tone for a serialized scalar field value.
/// </summary>
/// <param name="Value">Serialized field value.</param>
/// <param name="Tone">Semantic tone applied when the value matches.</param>
public sealed record FieldValueToneDefinition(
    string Value,
    string Tone
);

/// <summary>
/// Defines a semantic icon for a serialized scalar field value.
/// </summary>
/// <param name="Value">Serialized field value.</param>
/// <param name="Icon">Semantic icon identifier applied when the value matches.</param>
public sealed record FieldValueIconDefinition(
    string Value,
    string Icon
);

/// <summary>
/// Classifies entity-reference rendering when no route binding can be resolved.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldEntityReferenceFallbackKind
{
    Code = 0,
    Badge = 1,
    Text = 2
}

/// <summary>
/// Classifies read-only JSON value rendering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldJsonDisplayMode
{
    Preformatted = 0,
    Inline = 1
}


/// <summary>
/// Presented field capabilities such as sorting, filtering, editing, or navigation.
/// </summary>
/// <param name="Name"></param>
public readonly record struct FieldPresentationCapability(string Name)
{
    public static readonly FieldPresentationCapability Navigate = new(nameof(Navigate).ToLowerInvariant());
    public static readonly FieldPresentationCapability Edit = new(nameof(Edit).ToLowerInvariant());
    public static readonly FieldPresentationCapability Filter = new(nameof(Filter).ToLowerInvariant());
    public static readonly FieldPresentationCapability Sort = new(nameof(Sort).ToLowerInvariant());
    public static readonly FieldPresentationCapability Aggregate = new(nameof(Aggregate).ToLowerInvariant());

    public static implicit operator string(FieldPresentationCapability capability) => capability.Name;
}

/// <summary>
/// Defines how a presented field obtains or derives its runtime value.
/// </summary>
/// <param name="Kind">Value-source classification.</param>
/// <param name="DataSourceId">Primary data source that exposes the field value.</param>
/// <param name="FieldPath">Field path within the primary data source result.</param>
/// <param name="SourceDataSourceIds">Input data sources used when the value is derived.</param>
/// <param name="ExpressionId">Optional expression that derives the value.</param>
/// <param name="Aggregation">Optional aggregate operation when the value is aggregated.</param>
/// <param name="Residency">Where the source value is evaluated or held.</param>
public sealed record FieldValueSourceDefinition(
    FieldValueSourceKind Kind,
    string DataSourceId,
    string FieldPath,
    string[] SourceDataSourceIds,
    string? ExpressionId,
    FieldAggregationKind? Aggregation,
    ResidencyHint? Residency
);

/// <summary>
/// Classifies how a field value is sourced.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldValueSourceKind
{
    DataSourceField = 0,
    LocalAggregate = 1,
    Expression = 2,
    LocalState = 3
}

/// <summary>
/// Classifies aggregate computations used by field value sources.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldAggregationKind
{
    Count = 0,
    Sum = 1,
    Average = 2,
    Min = 3,
    Max = 4,
    Exists = 5
}

/// <summary>
/// Defines target-independent display formatting.
/// </summary>
/// <param name="Kind">Formatting kind.</param>
/// <param name="Pattern">Optional formatting pattern.</param>
/// <param name="TimeZone">Optional time zone identifier used by temporal formatting.</param>
/// <param name="Unit">Optional unit label or unit identifier.</param>
public sealed record FormatDefinition(
    FormatKind Kind,
    string? Pattern = null,
    string? TimeZone = null,
    string? Unit = null
);


/// <summary>
/// Classifies formatting behavior for values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormatKind
{
    Text = 0,
    Number = 1,
    Date = 2,
    DateTime = 3,
    Time = 4,
    Currency = 5,
    Percent = 6,
    Unit = 7,
    Duration = 8,
    RelativeTime = 9,
    Json = 10,
    Markdown = 11,
    Code = 12,
    Diff = 13
}

/// <summary>
/// Classifies target-independent field display intent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldDisplayKind
{
    Text = 0,
    Number = 1,
    Date = 2,
    DateTime = 3,
    Currency = 4,
    Badge = 5,
    Status = 6,
    Link = 7,
    EntityReference = 8,
    Code = 9,
    Markdown = 10,
    Json = 11,
    Diff = 12,
    Sparkline = 13,
    Metric = 14,
    Avatar = 15,
    Boolean = 16,
    Choice = 17
}

/// <summary>
/// Classifies target-independent field edit intent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldEditKind
{
    ReadOnly = 0,
    Text = 1,
    Number = 2,
    Date = 3,
    DateTime = 4,
    Select = 5,
    MultiSelect = 6,
    Checkbox = 7,
    Toggle = 8,
    JsonEditor = 9,
    CodeEditor = 10,
    MarkdownEditor = 11,
    EntityReference = 12,
    Choice = 13,
    Boolean = 14
}
