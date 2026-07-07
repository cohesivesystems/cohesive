using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Json.Schema;

namespace Cohesive.Adapters.Json;

/// <summary>
/// Structural JSON Schema validator for portable semantic documents.
/// </summary>
public static class JsonSchemaDocumentValidator
{
    /// <summary>
    /// Validates a JSON document against a schema provider.
    /// </summary>
    public static DocumentValidationResult ValidateJson(
        string json,
        IJsonSchemaProvider provider,
        string diagnosticCodePrefix
        )
    {
        ArgumentNullException.ThrowIfNull(provider);

        return ValidateJson(json, provider.Schema, diagnosticCodePrefix);
    }

    /// <summary>
    /// Validates a JSON document against the provided JSON Schema.
    /// </summary>
    public static DocumentValidationResult ValidateJson(
        string json,
        JsonSchema schema,
        string diagnosticCodePrefix
        )
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCodePrefix);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Error($"{diagnosticCodePrefix}.json", ex.Message, "$");
        }

        using (document)
        {
            var results = schema.Evaluate(
                document.RootElement,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                    RequireFormatValidation = true
                });

            if (results.IsValid)
                return DocumentValidationResult.Valid;

            results.ToList();

            List<DocumentValidationDiagnostic> diagnostics = [];
            CollectDiagnostics(results, diagnosticCodePrefix, diagnostics);

            return diagnostics.Count == 0
                ? Error($"{diagnosticCodePrefix}.schemaViolation", "Document does not match the JSON Schema.", "$")
                : DocumentValidationResult.FromDiagnostics(diagnostics);
        }
    }

    static void CollectDiagnostics(
        EvaluationResults result,
        string diagnosticCodePrefix,
        List<DocumentValidationDiagnostic> diagnostics
        )
    {
        if (result.Errors is { Count: > 0 })
        {
            foreach (var error in result.Errors)
            {
                diagnostics.Add(new(
                    Code: $"{diagnosticCodePrefix}.{NormalizeErrorCode(error.Key)}",
                    Severity: DiagnosticSeverity.Error,
                    Message: error.Value,
                    Location: result.InstanceLocation.ToString(),
                    SchemaLocation: result.EvaluationPath.ToString()
                    ));
            }
        }

        if (result.Details is null)
            return;

        foreach (var child in result.Details)
            CollectDiagnostics(child, diagnosticCodePrefix, diagnostics);
    }

    static string NormalizeErrorCode(string code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? "schemaViolation"
            : code.Replace('-', '.');
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location
                )
        ]);
}
