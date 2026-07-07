using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Diagnostic emitted while building/compiling a shape graph.
/// </summary>
public sealed record GraphDiagnostic
{
    /// <summary>
    /// Creates a graph diagnostic.
    /// </summary>
    [JsonConstructor]
    public GraphDiagnostic(
        DiagnosticId id,
        DiagnosticSeverity severity,
        string message,
        ShapeId? shapeId = null,
        string? fieldIdentity = null,
        TypeId? typeId = null
        )
    {
        Id = id;
        Severity = severity;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        ShapeId = shapeId;
        FieldIdentity = fieldIdentity.EmptyOrWhiteSpaceAsNull();
        TypeId = typeId;
    }

    /// <summary>
    /// Stable diagnostic id.
    /// </summary>
    public DiagnosticId Id { get; init; }

    /// <summary>
    /// Diagnostic severity.
    /// </summary>
    public DiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Human-readable diagnostic message.
    /// </summary>
    public string Message { get; init; }

    /// <summary>
    /// Optional shape scope.
    /// </summary>
    public ShapeId? ShapeId { get; init; }

    /// <summary>
    /// Optional field scope expressed as a canonical field identity.
    /// </summary>
    public string? FieldIdentity { get; init; }

    /// <summary>
    /// Optional type scope.
    /// </summary>
    public TypeId? TypeId { get; init; }
}
