namespace Cohesive.Presentation;

/// <summary>
/// Provides stable semantic selectors that can be projected into frontend test
/// attributes or generated test plans.
/// </summary>
public static class PresentationTestSelectors
{
    /// <summary>Attribute containing a presentation view identifier.</summary>
    public const string ViewIdAttribute = "data-presentation-view-id";

    /// <summary>Attribute containing a presentation action identifier.</summary>
    public const string ActionIdAttribute = "data-presentation-action-id";

    /// <summary>Attribute containing a presentation field identifier.</summary>
    public const string FieldIdAttribute = "data-presentation-field-id";

    /// <summary>Attribute containing a presentation flow identifier.</summary>
    public const string FlowIdAttribute = "data-presentation-flow-id";

    /// <summary>Attribute containing a presentation flow state identifier.</summary>
    public const string FlowStateIdAttribute = "data-presentation-flow-state-id";

    /// <summary>Attribute containing a presentation route identifier.</summary>
    public const string RouteIdAttribute = "data-presentation-route-id";

    /// <summary>Attribute containing a presentation collection chrome slot identifier.</summary>
    public const string CollectionSlotIdAttribute = "data-presentation-collection-slot-id";

    /// <summary>Attribute containing a presentation row identifier.</summary>
    public const string RowIdAttribute = "data-presentation-row-id";

    /// <summary>Attribute containing a presentation input form identifier.</summary>
    public const string FormIdAttribute = "data-presentation-form-id";

    /// <summary>Attribute containing a presentation document projection identifier.</summary>
    public const string ProjectionIdAttribute = "data-presentation-projection-id";

    /// <summary>
    /// Creates a CSS selector for an exact semantic data attribute match.
    /// </summary>
    /// <param name="attribute">The semantic data attribute name.</param>
    /// <param name="value">The expected attribute value.</param>
    public static string AttributeEquals(string attribute, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return $"[{attribute}=\"{EscapeCssAttributeValue(value)}\"]";
    }

    /// <summary>
    /// Creates a CSS selector for a presentation view.
    /// </summary>
    /// <param name="viewId">The presentation view identifier.</param>
    public static string View(string viewId) => AttributeEquals(ViewIdAttribute, viewId);

    /// <summary>
    /// Creates a CSS selector for a presentation action.
    /// </summary>
    /// <param name="actionId">The presentation action identifier.</param>
    public static string Action(string actionId) => AttributeEquals(ActionIdAttribute, actionId);

    /// <summary>
    /// Creates a CSS selector for a presentation field.
    /// </summary>
    /// <param name="fieldId">The presentation field identifier.</param>
    public static string Field(string fieldId) => AttributeEquals(FieldIdAttribute, fieldId);

    /// <summary>
    /// Creates a CSS selector for a presentation flow.
    /// </summary>
    /// <param name="flowId">The presentation flow identifier.</param>
    public static string Flow(string flowId) => AttributeEquals(FlowIdAttribute, flowId);

    /// <summary>
    /// Creates a CSS selector for a presentation route.
    /// </summary>
    /// <param name="routeId">The presentation route identifier.</param>
    public static string Route(string routeId) => AttributeEquals(RouteIdAttribute, routeId);

    /// <summary>
    /// Creates a CSS selector for a presentation collection chrome slot.
    /// </summary>
    /// <param name="slotId">The collection chrome slot identifier.</param>
    public static string CollectionSlot(string slotId) =>
        AttributeEquals(CollectionSlotIdAttribute, slotId);

    /// <summary>
    /// Creates a CSS selector for a presentation collection row.
    /// </summary>
    /// <param name="rowId">The collection row identifier.</param>
    public static string Row(string rowId) => AttributeEquals(RowIdAttribute, rowId);

    /// <summary>
    /// Creates a CSS selector for a presentation input form.
    /// </summary>
    /// <param name="formId">The input form identifier.</param>
    public static string Form(string formId) => AttributeEquals(FormIdAttribute, formId);

    /// <summary>
    /// Creates a CSS selector for a document projection surface.
    /// </summary>
    /// <param name="projectionId">The document projection identifier.</param>
    public static string Projection(string projectionId) =>
        AttributeEquals(ProjectionIdAttribute, projectionId);

    /// <summary>
    /// Creates a CSS selector for a state inside a presentation flow.
    /// </summary>
    /// <param name="flowId">The presentation flow identifier.</param>
    /// <param name="stateId">The flow state identifier.</param>
    public static string FlowState(string flowId, string stateId) =>
        $"{Flow(flowId)}{AttributeEquals(FlowStateIdAttribute, stateId)}";

    static string EscapeCssAttributeValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
