using System.Text.Json;

namespace Cohesive.Presentation;

/// <summary>
/// Defines an open presentation annotation.
/// </summary>
/// <param name="Name">Annotation name.</param>
/// <param name="Value">Annotation value.</param>
public sealed record PresentationAnnotationDefinition(
    string Name,
    JsonElement Value
);