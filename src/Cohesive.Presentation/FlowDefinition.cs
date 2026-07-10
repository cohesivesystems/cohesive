using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a statechart-like interaction flow.
/// </summary>
/// <param name="Id">Stable flow identifier.</param>
/// <param name="Name">Human-readable flow name.</param>
/// <param name="InitialStateId">Identifier of the initial flow state.</param>
/// <param name="States">States in the flow.</param>
/// <param name="Transitions">Transitions between flow states.</param>
/// <param name="Variables">Variables carried by the flow.</param>
/// <param name="Residency">Hint for where the flow state is evaluated or held.</param>
/// <param name="Annotations">Open annotations for flow-level extension data.</param>
public sealed record FlowDefinition(
    string Id,
    string Name,
    string InitialStateId,
    FlowStateDefinition[] States,
    FlowTransitionDefinition[] Transitions,
    FlowVariableDefinition[] Variables,
    ResidencyHint Residency,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a flow state.
/// </summary>
/// <param name="Id">Stable flow state identifier scoped to the flow.</param>
/// <param name="Name">Human-readable flow state name.</param>
/// <param name="Kind">Flow state kind.</param>
/// <param name="ViewId">Optional view identifier associated with the state.</param>
public sealed record FlowStateDefinition(
    string Id,
    string Name,
    FlowStateKind Kind,
    string? ViewId = null
);


/// <summary>
/// Classifies a flow state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowStateKind
{
    /// <summary>Represents the idle option.</summary>
    Idle = 0,
    /// <summary>Represents the pending option.</summary>
    Pending = 1,
    /// <summary>Represents the prompt option.</summary>
    Prompt = 2,
    /// <summary>Represents the process option.</summary>
    Process = 3,
    /// <summary>Represents the terminal option.</summary>
    Terminal = 4,
    /// <summary>Represents the error option.</summary>
    Error = 5
}


/// <summary>
/// Defines a flow transition.
/// </summary>
/// <param name="Id">Stable transition identifier scoped to the flow.</param>
/// <param name="FromStateId">Source state identifier.</param>
/// <param name="ToStateId">Target state identifier.</param>
/// <param name="Event">Event that triggers the transition.</param>
/// <param name="ActionId">Optional action identifier associated with the transition.</param>
/// <param name="Guard">Optional guard expression controlling transition availability.</param>
public sealed record FlowTransitionDefinition(
    string Id,
    string FromStateId,
    string ToStateId,
    string Event,
    string? ActionId = null,
    string? Guard = null
);

/// <summary>
/// Defines a flow variable.
/// </summary>
/// <param name="Name">Variable name.</param>
/// <param name="Type">Variable type name.</param>
/// <param name="InitialValue">Optional initial value encoded as text.</param>
public sealed record FlowVariableDefinition(
    string Name,
    string Type,
    string? InitialValue = null
);
