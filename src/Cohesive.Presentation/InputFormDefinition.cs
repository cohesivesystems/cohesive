using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a target-independent input form that edits a draft interaction value.
/// </summary>
/// <remarks>
/// Input forms own the reusable interaction surface: shape anchors, field and
/// group structure, validation policy, enrichment hooks, and actions. Specialized
/// forms such as query forms attach a target binding to the same form instead
/// of re-declaring those interaction concerns.
/// </remarks>
/// <param name="Id">Stable form identifier.</param>
/// <param name="Name">Human-readable form name.</param>
/// <param name="Description">Optional description for authoring and tooling.</param>
/// <param name="StateDataSourceId">Local data source that stores the draft form value.</param>
/// <param name="SharedStateId">Optional canonical interaction state shared with views or result surfaces.</param>
/// <param name="Shapes">Semantic shape references used by validation, lowering, enrichment, and result rendering.</param>
/// <param name="Groups">Logical field groups used by renderers to choose platform-specific layout.</param>
/// <param name="Fields">Form fields accepted by the input form.</param>
/// <param name="Target">The semantic target that receives the lowered input.</param>
/// <param name="Validation">Validation sources and declared diagnostics for the form.</param>
/// <param name="Enrichment">Optional backend enrichment contract for draft facts, suggestions, and diagnostics.</param>
/// <param name="Suggestions">Suggestion sources available to form fields.</param>
/// <param name="Actions">Action placements exposed by the form, such as apply and reset.</param>
/// <param name="Annotations">Open annotations for form-level extension data.</param>
/// <param name="DefaultValues">Optional value bindings used to initialize visible or hidden draft fields from runtime context.</param>
public sealed record InputFormDefinition(
    string Id,
    string Name,
    string? Description,
    string StateDataSourceId,
    string? SharedStateId,
    InputFormShapeRefsDefinition Shapes,
    InputFormGroupDefinition[] Groups,
    InputFormFieldDefinition[] Fields,
    InputFormTargetDefinition Target,
    InputFormValidationDefinition Validation,
    InputFormEnrichmentDefinition? Enrichment,
    InputFormSuggestionSourceDefinition[] Suggestions,
    ActionPlacementDefinition[] Actions,
    PresentationAnnotationDefinition[] Annotations,
    InputFormDefaultValueBindingDefinition[]? DefaultValues = null
);

/// <summary>
/// Binds a presentation value into an input-form draft value during initialization.
/// </summary>
/// <param name="TargetPath">Path in the draft value object to initialize.</param>
/// <param name="Source">Presentation value resolved against the form's runtime context.</param>
/// <param name="OmitWhenNull">Whether null or unresolved values should leave the target path absent.</param>
/// <param name="Annotations">Open annotations for default binding extension data.</param>
public sealed record InputFormDefaultValueBindingDefinition(
    string TargetPath,
    PresentationValueDefinition Source,
    bool OmitWhenNull,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares the semantic shapes involved in editing and lowering a form.
/// </summary>
/// <param name="SubjectShape">Shape of the semantic subject being queried or changed.</param>
/// <param name="ValueShape">Shape of the draft value edited by the form.</param>
/// <param name="TargetInputShape">Shape of the lowered query, transition, process, or endpoint input.</param>
/// <param name="ResultShape">Shape of records or resources produced by the target.</param>
/// <param name="FactsShape">Shape of backend-enriched facts used by choices, suggestions, or diagnostics.</param>
public sealed record InputFormShapeRefsDefinition(
    ShapeRefDefinition? SubjectShape,
    ShapeRefDefinition? ValueShape,
    ShapeRefDefinition? TargetInputShape,
    ShapeRefDefinition? ResultShape,
    ShapeRefDefinition? FactsShape
);

/// <summary>
/// Defines the semantic target that receives a lowered input form value.
/// </summary>
/// <param name="Kind">Target family for the form.</param>
/// <param name="Id">Stable target identifier scoped to the form.</param>
/// <param name="Name">Optional human-readable target name.</param>
/// <param name="DataSourceId">Optional target data source bound to the lowered input or result.</param>
/// <param name="EndpointId">Optional endpoint used by the target adapter.</param>
/// <param name="ProcessType">Optional process type invoked by the target.</param>
/// <param name="TransitionId">Optional entity transition invoked by the target.</param>
/// <param name="Annotations">Open annotations for target-level extension data.</param>
public sealed record InputFormTargetDefinition(
    InputFormTargetKind Kind,
    string Id,
    string? Name,
    string? DataSourceId,
    string? EndpointId,
    string? ProcessType,
    string? TransitionId,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies the target family for a form.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormTargetKind
{
    /// <summary>Represents the relation query option.</summary>
    RelationQuery = 0,
    /// <summary>Represents the entity transition option.</summary>
    EntityTransition = 1,
    /// <summary>Represents the process invocation option.</summary>
    ProcessInvocation = 2,
    /// <summary>Represents the endpoint request option.</summary>
    EndpointRequest = 3,
    /// <summary>Represents the enrichment option.</summary>
    Enrichment = 4,
    /// <summary>Represents the local state option.</summary>
    LocalState = 5
}

/// <summary>
/// Defines a logical group within an input form.
/// </summary>
/// <param name="Id">Stable group identifier scoped to the form.</param>
/// <param name="Name">Human-readable group name.</param>
/// <param name="Description">Optional description for authoring and tooling.</param>
/// <param name="FieldIds">Fields included in the group.</param>
/// <param name="Kind">Semantic role of the group in the interaction.</param>
/// <param name="Display">Target-independent display intent for the group.</param>
/// <param name="Design">Target-independent design intent for the group.</param>
/// <param name="Annotations">Open annotations for group-level extension data.</param>
public sealed record InputFormGroupDefinition(
    string Id,
    string Name,
    string? Description,
    string[] FieldIds,
    InputFormGroupKind Kind,
    InputFormGroupDisplayIntentDefinition Display,
    DesignIntent? Design,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies a form group by semantic purpose rather than by concrete widget.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormGroupKind
{
    /// <summary>Represents the section option.</summary>
    Section = 0,
    /// <summary>Represents the identity option.</summary>
    Identity = 1,
    /// <summary>Represents the lifecycle option.</summary>
    Lifecycle = 2,
    /// <summary>Represents the time option.</summary>
    Time = 3,
    /// <summary>Represents the advanced option.</summary>
    Advanced = 4,
    /// <summary>Represents the review option.</summary>
    Review = 5,
    /// <summary>Represents the step option.</summary>
    Step = 6,
    /// <summary>Represents the toolbar option.</summary>
    Toolbar = 7,
    /// <summary>Represents the sidebar option.</summary>
    Sidebar = 8,
    /// <summary>Represents the inline option.</summary>
    Inline = 9,
    /// <summary>Represents the facet region option.</summary>
    FacetRegion = 10
}

/// <summary>
/// Describes how a group wants to be presented while leaving exact layout to an adapter.
/// </summary>
/// <param name="Container">Preferred containment or disclosure pattern.</param>
/// <param name="Priority">Relative rendering priority inside the form.</param>
/// <param name="SemanticDensity">Semantic density requested by the group.</param>
/// <param name="Orientation">Optional orientation intent, such as horizontal or vertical.</param>
/// <param name="IsCollapsible">Whether the group may be collapsed by a renderer.</param>
/// <param name="IsDefaultCollapsed">Whether the group should start collapsed when collapsible.</param>
public sealed record InputFormGroupDisplayIntentDefinition(
    InputFormGroupContainerIntent Container,
    int Priority,
    string SemanticDensity,
    string? Orientation,
    bool IsCollapsible,
    bool IsDefaultCollapsed
);

/// <summary>
/// Declares a preferred group containment pattern.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormGroupContainerIntent
{
    /// <summary>Represents the inline option.</summary>
    Inline = 0,
    /// <summary>Represents the section option.</summary>
    Section = 1,
    /// <summary>Represents the panel option.</summary>
    Panel = 2,
    /// <summary>Represents the accordion option.</summary>
    Accordion = 3,
    /// <summary>Represents the tabs option.</summary>
    Tabs = 4,
    /// <summary>Represents the wizard step option.</summary>
    WizardStep = 5,
    /// <summary>Represents the sidebar option.</summary>
    Sidebar = 6,
    /// <summary>Represents the modal option.</summary>
    Modal = 7,
    /// <summary>Represents the toolbar option.</summary>
    Toolbar = 8,
    /// <summary>Represents the card option.</summary>
    Card = 9
}

/// <summary>
/// Defines one input accepted by a form.
/// </summary>
/// <param name="Id">Stable form field identifier scoped to the form.</param>
/// <param name="FieldId">Presentation field identifier that supplies label, formatting, and edit intent.</param>
/// <param name="Name">Human-readable field name.</param>
/// <param name="Kind">Logical input kind.</param>
/// <param name="ValuePath">Path in the form value object that stores this field.</param>
/// <param name="GroupId">Group that contains the field.</param>
/// <param name="IsRequired">Whether the field is required before submission.</param>
/// <param name="DefaultValue">Optional default value encoded as text.</param>
/// <param name="Placeholder">Optional placeholder text for simple textual inputs.</param>
/// <param name="Display">Optional target-independent control display options.</param>
/// <param name="ChoiceSource">Optional source of choices for choice-like inputs.</param>
/// <param name="TargetBindingIds">Target bindings driven by this field.</param>
/// <param name="SuggestionSourceIds">Suggestion sources available to this field.</param>
/// <param name="Design">Target-independent design intent for the field.</param>
/// <param name="Annotations">Open annotations for field-level extension data.</param>
public sealed record InputFormFieldDefinition(
    string Id,
    string FieldId,
    string Name,
    InputFormFieldKind Kind,
    string ValuePath,
    string GroupId,
    bool IsRequired,
    string? DefaultValue,
    string? Placeholder,
    InputFormFieldDisplayOptions? Display,
    InputFormChoiceSourceDefinition? ChoiceSource,
    string[] TargetBindingIds,
    string[] SuggestionSourceIds,
    DesignIntent? Design,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Refines target-independent input control behavior without binding a form
/// field to a concrete frontend component.
/// </summary>
/// <param name="Control">Optional preferred control interpretation for the logical input kind.</param>
/// <param name="EmptyValueLabel">Optional compact label shown when no value is selected.</param>
/// <param name="IncrementMinutes">Optional minute increment for date/time controls.</param>
/// <param name="ShowTimezone">Whether date/time controls should show timezone context.</param>
public sealed record InputFormFieldDisplayOptions(
    InputFormFieldControlKind? Control = null,
    string? EmptyValueLabel = null,
    int? IncrementMinutes = null,
    bool? ShowTimezone = null
);

/// <summary>
/// Classifies specialized control interpretations for an input form field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormFieldControlKind
{
    /// <summary>Represents the default option.</summary>
    Default = 0,
    /// <summary>Represents the date time filter option.</summary>
    DateTimeFilter = 1
}

/// <summary>
/// Classifies form input behavior independently of a concrete UI toolkit.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormFieldKind
{
    /// <summary>Represents the text option.</summary>
    Text = 0,
    /// <summary>Represents the select option.</summary>
    Select = 1,
    /// <summary>Represents the multi select option.</summary>
    MultiSelect = 2,
    /// <summary>Represents the date option.</summary>
    Date = 3,
    /// <summary>Represents the date time option.</summary>
    DateTime = 4,
    /// <summary>Represents the date time range option.</summary>
    DateTimeRange = 5,
    /// <summary>Represents the number option.</summary>
    Number = 6,
    /// <summary>Represents the number range option.</summary>
    NumberRange = 7,
    /// <summary>Represents the boolean option.</summary>
    Boolean = 8,
    /// <summary>Represents the dynamic facet region option.</summary>
    DynamicFacetRegion = 9,
    /// <summary>Represents the relation projection editor option.</summary>
    RelationProjectionEditor = 10,
    /// <summary>Represents the relation predicate editor option.</summary>
    RelationPredicateEditor = 11,
    /// <summary>Represents the relation aggregation editor option.</summary>
    RelationAggregationEditor = 12
}

/// <summary>
/// Describes how a form field obtains selectable choices.
/// </summary>
/// <param name="Kind">Choice source family.</param>
/// <param name="DataSourceId">Optional data source that supplies choices.</param>
/// <param name="FactsPath">Optional path to choices in enriched form facts.</param>
/// <param name="CollectionPath">Optional path to the choice collection in the source result.</param>
/// <param name="ValuePath">Path to the submitted value inside each choice item.</param>
/// <param name="LabelPath">Path to the human-readable label inside each choice item.</param>
/// <param name="TonePath">Optional path to a target-independent tone inside each choice item.</param>
/// <param name="DefaultSelection">How default selections are derived when the field has no explicit value.</param>
public sealed record InputFormChoiceSourceDefinition(
    InputFormChoiceSourceKind Kind,
    string? DataSourceId,
    string? FactsPath,
    string? CollectionPath,
    string ValuePath,
    string LabelPath,
    string? TonePath,
    InputFormChoiceDefaultSelection DefaultSelection
);

/// <summary>
/// Classifies where choice values come from.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormChoiceSourceKind
{
    /// <summary>Represents the static choices option.</summary>
    StaticChoices = 0,
    /// <summary>Represents the data source option.</summary>
    DataSource = 1,
    /// <summary>Represents the enriched facts option.</summary>
    EnrichedFacts = 2,
    /// <summary>Represents the entity lookup option.</summary>
    EntityLookup = 3,
    /// <summary>Represents the relation query option.</summary>
    RelationQuery = 4,
    /// <summary>Represents the ontology terms option.</summary>
    OntologyTerms = 5,
    /// <summary>Represents the recent values option.</summary>
    RecentValues = 6,
    /// <summary>Represents the endpoint option.</summary>
    Endpoint = 7,
    /// <summary>Represents the derived from context option.</summary>
    DerivedFromContext = 8
}

/// <summary>
/// Defines the default-selection strategy for choice-backed fields.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormChoiceDefaultSelection
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the all option.</summary>
    All = 1,
    /// <summary>Represents the first option.</summary>
    First = 2
}

/// <summary>
/// Declares validation sources that should be applied to the form.
/// </summary>
/// <param name="Sources">Validation layers evaluated for the form.</param>
/// <param name="StaticDiagnostics">Design-time diagnostics known from the form definition.</param>
/// <param name="Annotations">Open annotations for validation-level extension data.</param>
public sealed record InputFormValidationDefinition(
    InputFormValidationSourceDefinition[] Sources,
    InputFormValidationDiagnosticDefinition[] StaticDiagnostics,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares one validation layer for a form.
/// </summary>
/// <param name="Id">Stable validation source identifier.</param>
/// <param name="Kind">Validation source family.</param>
/// <param name="Name">Optional human-readable validation source name.</param>
/// <param name="EndpointId">Optional endpoint used to evaluate this source.</param>
/// <param name="BlockingByDefault">Whether diagnostics from this source block submission by default.</param>
/// <param name="Annotations">Open annotations for validation-source-level extension data.</param>
public sealed record InputFormValidationSourceDefinition(
    string Id,
    InputFormValidationSourceKind Kind,
    string? Name,
    string? EndpointId,
    bool BlockingByDefault,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies validation layers for form interactions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormValidationSourceKind
{
    /// <summary>Represents the shape option.</summary>
    Shape = 0,
    /// <summary>Represents the form option.</summary>
    Form = 1,
    /// <summary>Represents the relation query option.</summary>
    RelationQuery = 2,
    /// <summary>Represents the transition option.</summary>
    Transition = 3,
    /// <summary>Represents the process invocation option.</summary>
    ProcessInvocation = 4,
    /// <summary>Represents the endpoint request option.</summary>
    EndpointRequest = 5,
    /// <summary>Represents the capability planner option.</summary>
    CapabilityPlanner = 6,
    /// <summary>Represents the authorization option.</summary>
    Authorization = 7,
    /// <summary>Represents the enrichment option.</summary>
    Enrichment = 8
}

/// <summary>
/// Defines a normalized validation diagnostic for form authoring or runtime validation.
/// </summary>
/// <param name="Id">Stable diagnostic identifier.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Scope">Semantic scope targeted by the diagnostic.</param>
/// <param name="Path">Optional value, shape, or target path associated with the diagnostic.</param>
/// <param name="FieldId">Optional form field identifier associated with the diagnostic.</param>
/// <param name="GroupId">Optional form group identifier associated with the diagnostic.</param>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="Blocking">Whether the diagnostic blocks the target action.</param>
/// <param name="SourceId">Validation source that produced the diagnostic.</param>
/// <param name="Annotations">Open annotations for diagnostic-level extension data.</param>
public sealed record InputFormValidationDiagnosticDefinition(
    string Id,
    ValidationMarkerSeverity Severity,
    InputFormValidationScope Scope,
    string? Path,
    string? FieldId,
    string? GroupId,
    string Code,
    string Message,
    bool Blocking,
    string? SourceId,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies where a validation diagnostic is attached.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormValidationScope
{
    /// <summary>Represents the field option.</summary>
    Field = 0,
    /// <summary>Represents the group option.</summary>
    Group = 1,
    /// <summary>Represents the form option.</summary>
    Form = 2,
    /// <summary>Represents the target option.</summary>
    Target = 3,
    /// <summary>Represents the query option.</summary>
    Query = 4,
    /// <summary>Represents the transition option.</summary>
    Transition = 5,
    /// <summary>Represents the process option.</summary>
    Process = 6,
    /// <summary>Represents the endpoint option.</summary>
    Endpoint = 7,
    /// <summary>Represents the shape option.</summary>
    Shape = 8
}

/// <summary>
/// Declares a backend enrichment contract for a form draft.
/// </summary>
/// <param name="EndpointId">Endpoint that enriches the draft value, if any.</param>
/// <param name="RequestShape">Shape sent to the enrichment endpoint.</param>
/// <param name="ResultShape">Shape returned by the enrichment endpoint.</param>
/// <param name="Stages">Enrichment stages offered by the endpoint.</param>
/// <param name="Annotations">Open annotations for enrichment-level extension data.</param>
public sealed record InputFormEnrichmentDefinition(
    string? EndpointId,
    ShapeRefDefinition? RequestShape,
    ShapeRefDefinition? ResultShape,
    InputFormEnrichmentStageDefinition[] Stages,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines one enrichment stage that can normalize, resolve facts, suggest, validate, or lower a draft.
/// </summary>
/// <param name="Id">Stable stage identifier.</param>
/// <param name="Kind">Stage family.</param>
/// <param name="Name">Optional human-readable stage name.</param>
/// <param name="Annotations">Open annotations for stage-level extension data.</param>
public sealed record InputFormEnrichmentStageDefinition(
    string Id,
    InputFormEnrichmentStageKind Kind,
    string? Name,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies form enrichment stages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormEnrichmentStageKind
{
    /// <summary>Represents the normalize option.</summary>
    Normalize = 0,
    /// <summary>Represents the resolve facts option.</summary>
    ResolveFacts = 1,
    /// <summary>Represents the suggest option.</summary>
    Suggest = 2,
    /// <summary>Represents the validate option.</summary>
    Validate = 3,
    /// <summary>Represents the lower option.</summary>
    Lower = 4,
    /// <summary>Represents the estimate cost option.</summary>
    EstimateCost = 5
}

/// <summary>
/// Defines the shape of an enrichment result returned by a backend form adapter.
/// </summary>
/// <param name="FormId">Form that was enriched.</param>
/// <param name="StateId">Shared state instance or key that was enriched.</param>
/// <param name="ValueShape">Shape of the enriched draft value.</param>
/// <param name="FactsShape">Shape of returned enrichment facts.</param>
/// <param name="Diagnostics">Validation or capability diagnostics produced by enrichment.</param>
/// <param name="Suggestions">Suggestions produced by enrichment.</param>
/// <param name="Annotations">Open annotations for enrichment-result-level extension data.</param>
public sealed record InputFormEnrichmentResultDefinition(
    string FormId,
    string StateId,
    ShapeRefDefinition? ValueShape,
    ShapeRefDefinition? FactsShape,
    InputFormValidationDiagnosticDefinition[] Diagnostics,
    InputFormSuggestionDefinition[] Suggestions,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares a source that can produce suggestions for one or more form fields.
/// </summary>
/// <param name="Id">Stable suggestion source identifier.</param>
/// <param name="Kind">Suggestion source family.</param>
/// <param name="Name">Optional human-readable source name.</param>
/// <param name="DataSourceId">Optional data source used for suggestions.</param>
/// <param name="EndpointId">Optional endpoint used for suggestions.</param>
/// <param name="FactsPath">Optional facts path that contains suggestions.</param>
/// <param name="TriggerFieldIds">Fields that trigger suggestion refreshes.</param>
/// <param name="MinimumInputLength">Minimum input length before interactive suggestions should run.</param>
/// <param name="DebounceMilliseconds">Suggested debounce interval for interactive adapters.</param>
/// <param name="Writes">Value writes applied when a suggestion is accepted.</param>
/// <param name="Annotations">Open annotations for suggestion-source-level extension data.</param>
public sealed record InputFormSuggestionSourceDefinition(
    string Id,
    InputFormSuggestionSourceKind Kind,
    string? Name,
    string? DataSourceId,
    string? EndpointId,
    string? FactsPath,
    string[] TriggerFieldIds,
    int? MinimumInputLength,
    int? DebounceMilliseconds,
    InputFormSuggestionValueBindingDefinition[] Writes,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies form suggestion sources.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormSuggestionSourceKind
{
    /// <summary>Represents the static choices option.</summary>
    StaticChoices = 0,
    /// <summary>Represents the entity autocomplete option.</summary>
    EntityAutocomplete = 1,
    /// <summary>Represents the relation query option.</summary>
    RelationQuery = 2,
    /// <summary>Represents the ontology terms option.</summary>
    OntologyTerms = 3,
    /// <summary>Represents the recent values option.</summary>
    RecentValues = 4,
    /// <summary>Represents the endpoint option.</summary>
    Endpoint = 5,
    /// <summary>Represents the derived from context option.</summary>
    DerivedFromContext = 6,
    /// <summary>Represents the enriched facts option.</summary>
    EnrichedFacts = 7
}

/// <summary>
/// Defines one suggestion returned by a suggestion or enrichment source.
/// </summary>
/// <param name="Id">Stable suggestion identifier.</param>
/// <param name="SourceId">Suggestion source that produced the suggestion.</param>
/// <param name="Label">Human-readable suggestion label.</param>
/// <param name="Description">Optional suggestion description.</param>
/// <param name="Value">String-encoded primary value.</param>
/// <param name="Tone">Optional target-independent tone.</param>
/// <param name="Writes">Value writes applied when the suggestion is accepted.</param>
/// <param name="Annotations">Open annotations for suggestion-level extension data.</param>
public sealed record InputFormSuggestionDefinition(
    string Id,
    string SourceId,
    string Label,
    string? Description,
    string Value,
    string? Tone,
    InputFormSuggestionValueBindingDefinition[] Writes,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Maps an accepted suggestion to a write into draft value or facts.
/// </summary>
/// <param name="Target">Write target.</param>
/// <param name="Path">Path written by the suggestion.</param>
/// <param name="ValuePath">Optional path inside the suggestion payload used as the write value.</param>
public sealed record InputFormSuggestionValueBindingDefinition(
    InputFormSuggestionWriteTarget Target,
    string Path,
    string? ValuePath
);

/// <summary>
/// Classifies where accepted suggestions write values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputFormSuggestionWriteTarget
{
    /// <summary>Represents the draft value option.</summary>
    DraftValue = 0,
    /// <summary>Represents the facts option.</summary>
    Facts = 1,
    /// <summary>Represents the target input option.</summary>
    TargetInput = 2,
    /// <summary>Represents the query fragment option.</summary>
    QueryFragment = 3
}

/// <summary>
/// Defines a query-form specialization over a generic input form.
/// </summary>
/// <remarks>
/// The referenced <see cref="InputFormDefinition"/> owns fields, groups,
/// validation, enrichment, suggestions, and actions. The query form attaches
/// the relation-query target semantics: predicates, projections, aggregations,
/// result binding, endpoint lowering, and shared query state.
/// </remarks>
/// <param name="Id">Stable query form identifier.</param>
/// <param name="FormId">Generic input form that supplies the interaction surface.</param>
/// <param name="Target">Relation query target semantics attached to the form.</param>
/// <param name="Annotations">Open annotations for query-form-level extension data.</param>
public sealed record QueryFormDefinition(
    string Id,
    string FormId,
    RelationQueryFormTargetDefinition Target,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds an input form to a Relations-style query target.
/// </summary>
/// <param name="State">Canonical query state shared by the filter form and result surfaces.</param>
/// <param name="Predicates">Bindings from form fields to entity predicates.</param>
/// <param name="Projections">Relation projection bindings editable or emitted by the form.</param>
/// <param name="Aggregations">Relation aggregation bindings editable or emitted by the form.</param>
/// <param name="Result">The data source and endpoint request produced by the query form.</param>
/// <param name="Annotations">Open annotations for relation-query-target-level extension data.</param>
public sealed record RelationQueryFormTargetDefinition(
    QueryFormStateBindingDefinition State,
    QueryFormPredicateBindingDefinition[] Predicates,
    QueryFormProjectionBindingDefinition[] Projections,
    QueryFormAggregationBindingDefinition[] Aggregations,
    QueryFormResultBindingDefinition Result,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares the canonical query state shared across filter controls and result views.
/// </summary>
/// <param name="StateId">Stable shared query state identifier.</param>
/// <param name="DraftDataSourceId">Data source that stores draft filter/form input.</param>
/// <param name="AppliedDataSourceId">Optional data source that stores the applied canonical query value.</param>
/// <param name="ResultDataSourceId">Data source populated by the query result.</param>
/// <param name="SynchronizedDataSourceIds">Data sources that should read or react to this query state.</param>
/// <param name="Execution">Execution policy for the query state.</param>
/// <param name="History">Optional query-history policy.</param>
/// <param name="Annotations">Open annotations for query-state-level extension data.</param>
/// <param name="Url">Optional URL synchronization policy for query filters and pagination.</param>
public sealed record QueryFormStateBindingDefinition(
    string StateId,
    string DraftDataSourceId,
    string? AppliedDataSourceId,
    string ResultDataSourceId,
    string[] SynchronizedDataSourceIds,
    QueryFormExecutionPolicyDefinition Execution,
    QueryFormHistoryPolicyDefinition? History,
    PresentationAnnotationDefinition[] Annotations,
    QueryFormUrlPolicyDefinition? Url = null
);

/// <summary>
/// Declares whether query-form state should be synchronized with client-visible URLs.
/// </summary>
/// <param name="IsEnabled">Whether URL synchronization is enabled.</param>
/// <param name="ParameterPrefix">Optional prefix used for query-form URL parameters.</param>
/// <param name="IncludeAppliedFilters">Whether applied filter state should be encoded in the URL.</param>
/// <param name="IncludeDraftFilters">Whether draft filter state should be encoded in the URL.</param>
/// <param name="IncludePagination">Whether synchronized pagination state should be encoded in the URL.</param>
public sealed record QueryFormUrlPolicyDefinition(
    bool IsEnabled,
    string? ParameterPrefix = null,
    bool IncludeAppliedFilters = true,
    bool IncludeDraftFilters = false,
    bool IncludePagination = true
);

/// <summary>
/// Describes when and how a query form executes its target query.
/// </summary>
/// <param name="Mode">Execution mode requested by the form.</param>
/// <param name="DebounceMilliseconds">Debounce interval for live execution, when applicable.</param>
/// <param name="ExecuteOnInitialLoad">Whether the query should execute before user interaction.</param>
/// <param name="AllowBackgroundPreview">Whether adapters may run preview queries while the draft is changing.</param>
public sealed record QueryFormExecutionPolicyDefinition(
    QueryFormExecutionMode Mode,
    int? DebounceMilliseconds,
    bool ExecuteOnInitialLoad,
    bool AllowBackgroundPreview
);

/// <summary>
/// Classifies query execution mode.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryFormExecutionMode
{
    /// <summary>Represents the manual option.</summary>
    Manual = 0,
    /// <summary>Represents the live option.</summary>
    Live = 1,
    /// <summary>Represents the debounced live option.</summary>
    DebouncedLive = 2
}

/// <summary>
/// Declares how query history should be retained for the shared query state.
/// </summary>
/// <param name="IsEnabled">Whether query history is enabled.</param>
/// <param name="Capacity">Maximum number of history entries retained by the adapter.</param>
/// <param name="PersistHistory">Whether history should survive a new runtime session.</param>
public sealed record QueryFormHistoryPolicyDefinition(
    bool IsEnabled,
    int? Capacity,
    bool PersistHistory
);

/// <summary>
/// Binds one query-form field to a canonical relation-query predicate intent.
/// </summary>
/// <remarks>
/// The binding identifies a semantic field path and predicate family. Target
/// lowerers combine this template with runtime field values to build canonical
/// <see cref="Expr"/> nodes subject to the selected target's declared capabilities.
/// </remarks>
/// <param name="Id">Stable predicate binding identifier scoped to the query form.</param>
/// <param name="FieldId">Input-form field that supplies the runtime value.</param>
/// <param name="EntityField">Entity field path targeted by the predicate.</param>
/// <param name="Kind">Value predicate family used when the field has a value.</param>
/// <param name="Scope">Optional entity predicate scope.</param>
/// <param name="OmitWhenEmpty">Whether empty input should suppress the predicate.</param>
/// <param name="Annotations">Open annotations for predicate-binding-level extension data.</param>
public sealed record QueryFormPredicateBindingDefinition(
    string Id,
    string FieldId,
    FieldPath EntityField,
    QueryFormPredicateKind Kind,
    FieldPath? Scope,
    bool OmitWhenEmpty,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies the predicate intent produced by a query-form field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryFormPredicateKind
{
    /// <summary>Represents the exact option.</summary>
    Exact = 0,
    /// <summary>Represents the prefix option.</summary>
    Prefix = 1,
    /// <summary>Represents the contains option.</summary>
    Contains = 2,
    /// <summary>Represents the full text option.</summary>
    FullText = 3,
    /// <summary>Represents the in option.</summary>
    In = 4,
    /// <summary>Represents the date range option.</summary>
    DateRange = 5,
    /// <summary>Represents the number range option.</summary>
    NumberRange = 6,
    /// <summary>Represents the exists option.</summary>
    Exists = 7
}

/// <summary>
/// Binds form input or generated query fragments to a relation field projection.
/// </summary>
/// <param name="Id">Stable projection binding identifier scoped to the query form.</param>
/// <param name="FieldId">Optional form field that controls this projection.</param>
/// <param name="EntityField">Entity field path included in the projection.</param>
/// <param name="Alias">Optional projected field alias.</param>
/// <param name="IsDefault">Whether the projection is active by default.</param>
/// <param name="Annotations">Open annotations for projection-binding-level extension data.</param>
public sealed record QueryFormProjectionBindingDefinition(
    string Id,
    string? FieldId,
    FieldPath EntityField,
    string? Alias,
    bool IsDefault,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds form input or generated query fragments to a relation aggregation.
/// </summary>
/// <param name="Id">Stable aggregation binding identifier scoped to the query form.</param>
/// <param name="FieldId">Optional form field that controls this aggregation.</param>
/// <param name="EntityField">Entity field path aggregated by the query.</param>
/// <param name="Operator">Aggregation operator label.</param>
/// <param name="Alias">Optional aggregation alias.</param>
/// <param name="Annotations">Open annotations for aggregation-binding-level extension data.</param>
public sealed record QueryFormAggregationBindingDefinition(
    string Id,
    string? FieldId,
    FieldPath EntityField,
    string Operator,
    string? Alias,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines the result data source and endpoint request produced by a query form.
/// </summary>
/// <param name="DataSourceId">Data source populated by the query result.</param>
/// <param name="EndpointId">Endpoint used to execute the query when the result is remote.</param>
/// <param name="RequestShape">Shape name of the endpoint request.</param>
/// <param name="ResultShape">Shape name of the endpoint result.</param>
/// <param name="EntityId">Optional semantic entity identifier queried by the form.</param>
/// <param name="RequestBindings">Mappings from form values to endpoint request fields.</param>
/// <param name="DefaultLimit">Optional result limit applied when the query is lowered.</param>
/// <param name="Annotations">Open annotations for result-binding-level extension data.</param>
public sealed record QueryFormResultBindingDefinition(
    string DataSourceId,
    string EndpointId,
    string RequestShape,
    string ResultShape,
    string? EntityId,
    QueryFormEndpointParameterBindingDefinition[] RequestBindings,
    int? DefaultLimit,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Maps a query-form field value to a field on the endpoint request shape.
/// </summary>
/// <param name="RequestField">Property name on the endpoint request shape.</param>
/// <param name="FieldId">Input-form field that supplies the value.</param>
/// <param name="ValuePath">Optional nested path inside the field value.</param>
/// <param name="OmitWhenEmpty">Whether null, empty string, or empty collection values should be omitted.</param>
public sealed record QueryFormEndpointParameterBindingDefinition(
    string RequestField,
    string FieldId,
    string? ValuePath,
    bool OmitWhenEmpty
);
