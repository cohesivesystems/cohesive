using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a design-system binding available to a target adapter.
/// </summary>
/// <param name="Id">Stable design-system binding identifier.</param>
/// <param name="Name">Human-readable design-system binding name.</param>
/// <param name="Kind">Design-system binding kind.</param>
/// <param name="ComponentBindings">Component bindings available in the design system.</param>
/// <param name="Tones">Target-independent tone tokens supported by the design system.</param>
/// <param name="Annotations">Open annotations for design-system-level extension data.</param>
public sealed record DesignSystemBindingDefinition(
    string Id,
    string Name,
    DesignSystemKind Kind,
    PresentationBindingDefinition[] ComponentBindings,
    DesignToneDefinition[] Tones,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a target-independent semantic tone available in a design system.
/// </summary>
/// <param name="Id">Stable tone token.</param>
/// <param name="Label">Human-readable tone label.</param>
public sealed record DesignToneDefinition(
    string Id,
    string Label
);


/// <summary>
/// Classifies a design-system binding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DesignSystemKind
{
    /// <summary>Represents the react component stack option.</summary>
    ReactComponentStack = 0,
    /// <summary>Represents the web component stack option.</summary>
    WebComponentStack = 1,
    /// <summary>Represents the css framework option.</summary>
    CssFramework = 2,
    /// <summary>Represents the native component stack option.</summary>
    NativeComponentStack = 3
}
