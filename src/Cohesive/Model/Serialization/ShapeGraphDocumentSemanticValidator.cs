namespace Cohesive.Model.Serialization;

/// <summary>
/// Semantic validator for portable shape graph documents.
/// </summary>
public static class ShapeGraphDocumentSemanticValidator
{
    /// <summary>
    /// Validates semantic invariants that are outside JSON Schema's job.
    /// </summary>
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

    static string? LocationFor(GraphDiagnostic diagnostic)
    {
        if (diagnostic.ShapeId is not null)
            return $"/graph/shapes/{diagnostic.ShapeId.Value}";

        if (diagnostic.TypeId is not null)
            return $"/graph/namedTypes/{diagnostic.TypeId.Value}";

        return null;
    }
}
