using System.Globalization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Semantic validator for portable shape graph documents.
/// </summary>
public static class ShapeGraphDocumentSemanticValidator
{
    /// <summary>
    /// Validates semantic invariants that are outside JSON Schema's job.
    /// </summary>
    /// <param name="document">Shape-graph document to validate.</param>
    /// <returns>Structured semantic diagnostics in deterministic declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(ShapeGraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentValidationDiagnostic> diagnostics = [];

        if (!string.Equals(document.SchemaVersion, ShapeGraphDocument.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "shapeGraph.schemaVersion.unsupported",
                DiagnosticSeverity.Error,
                $"Unsupported shape graph document schema version '{document.SchemaVersion}'.",
                "/schemaVersion"
                )
            );
        }

        if (document.Graph is null)
        {
            diagnostics.Add(new(
                "shapeGraph.graph.missing",
                DiagnosticSeverity.Error,
                "A shape graph document must contain a graph.",
                "/graph"));
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        if (string.IsNullOrWhiteSpace(document.Graph.Id.Value))
        {
            diagnostics.Add(new(
                "shapeGraph.id.missing",
                DiagnosticSeverity.Error,
                "Shape graph must declare a non-empty stable id.",
                "/graph/id"
                )
            );
        }

        if (document.Graph.Shapes.IsDefaultOrEmpty)
        {
            diagnostics.Add(new(
                "shapeGraph.shapes.empty",
                DiagnosticSeverity.Error,
                "Shape graph must contain at least one shape.",
                "/graph/shapes"
                )
            );
        }

        ValidateFieldValueMetadata(document.Graph, diagnostics);

        foreach (var graphDiagnostic in document.Graph.Diagnostics)
        {
            diagnostics.Add(new(
                Code: $"shapeGraph.{graphDiagnostic.Id.Value}",
                Severity: graphDiagnostic.Severity,
                Message: graphDiagnostic.Message,
                Location: LocationFor(graphDiagnostic)
                )
            );
        }

        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateFieldValueMetadata(
        ShapeGraph graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var shape in graph.Shapes)
        {
            foreach (var field in shape.Fields)
            {
                AddFieldValueDiagnostics(
                    field.Type,
                    field.Cardinality,
                    field.Presence,
                    field.Nullability,
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}'",
                    $"/graph/shapes/{Encode(shape.Id.Value)}/fields/{Encode(field.Name.Value)}",
                    diagnostics);
            }
        }

        foreach (var structural in graph.NamedTypes.OfType<TypeDefinition.Structural>())
        {
            foreach (var field in structural.Fields)
            {
                AddFieldValueDiagnostics(
                    field.Type,
                    field.Cardinality,
                    field.Presence,
                    field.Nullability,
                    $"Structural type '{structural.Id.Value}' field '{field.Name.Value}'",
                    $"/graph/namedTypes/{Encode(structural.Id.Value)}/fields/{Encode(field.Name.Value)}",
                    diagnostics);
            }
        }
    }

    static void AddFieldValueDiagnostics(
        TypeRef? type,
        FieldCardinality cardinality,
        FieldPresence presence,
        FieldNullability nullability,
        string subject,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (type is null)
        {
            diagnostics.Add(new(
                "shapeGraph.field.type.missing",
                DiagnosticSeverity.Error,
                $"{subject} must declare a semantic type.",
                $"{location}/type"));
        }
        if (!Enum.IsDefined(cardinality))
        {
            diagnostics.Add(new(
                "shapeGraph.field.cardinality.invalid",
                DiagnosticSeverity.Error,
                $"{subject} has unsupported cardinality '{((int)cardinality).ToString(CultureInfo.InvariantCulture)}'.",
                $"{location}/cardinality"));
        }
        if (!Enum.IsDefined(presence))
        {
            diagnostics.Add(new(
                "shapeGraph.field.presence.invalid",
                DiagnosticSeverity.Error,
                $"{subject} has unsupported presence '{((int)presence).ToString(CultureInfo.InvariantCulture)}'.",
                $"{location}/presence"));
        }
        if (!Enum.IsDefined(nullability))
        {
            diagnostics.Add(new(
                "shapeGraph.field.nullability.invalid",
                DiagnosticSeverity.Error,
                $"{subject} has unsupported nullability '{((int)nullability).ToString(CultureInfo.InvariantCulture)}'.",
                $"{location}/nullability"));
        }
    }

    static string Encode(string value) => Uri.EscapeDataString(value);

    static string? LocationFor(GraphDiagnostic diagnostic)
    {
        if (diagnostic.ShapeId is not null)
            return $"/graph/shapes/{diagnostic.ShapeId.Value}";

        if (diagnostic.TypeId is not null)
            return $"/graph/namedTypes/{diagnostic.TypeId.Value}";

        return null;
    }
}
