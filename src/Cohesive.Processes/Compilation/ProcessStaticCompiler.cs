using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Processes.Compilation;

/// <summary>Stable diagnostics emitted by target-independent Process compilation.</summary>
public static class ProcessCompilationDiagnosticCodes
{
    /// <summary>The definition carries a semantic extension not implemented by the reference plan.</summary>
    public const string ExtensionUnsupported = "processes.compilation.extension.unsupported";
}

/// <summary>Compiles canonical Process IR into a deterministic target-independent executable index.</summary>
/// <remarks>
/// Compilation performs no I/O and selects no orchestration or storage backend. Canonical Process validation remains
/// the semantic authority; this compiler only admits a validated document and indexes its closed node table.
/// </remarks>
public static class ProcessStaticCompiler
{
    /// <summary>Compiles one exact Process definition with explicit external semantic linking evidence.</summary>
    /// <param name="document">Canonical shared execution-definition document.</param>
    /// <param name="context">Exact linked definitions, interaction contracts, and optional shape evidence.</param>
    /// <returns>A complete executable plan, or structured canonical validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical semantic content cannot be decoded using the strict wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime type.</exception>
    public static ProcessCompilationResult Compile(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var validation = ProcessDefinitionDocuments.Validate(document, context);
        if (!validation.IsValid)
            return new(document, definition: null, plan: null, validation);

        if (!document.Extensions.IsDefaultOrEmpty)
        {
            var diagnostics = document.Extensions
                .Select((extension, index) => new DocumentValidationDiagnostic(
                    ProcessCompilationDiagnosticCodes.ExtensionUnsupported,
                    DiagnosticSeverity.Error,
                    $"Semantic extension '{extension.Id.Value}' version '{extension.SchemaVersion.Value}' "
                    + "is not implemented by the Process reference interpreter.",
                    $"/extensions/{index}"))
                .OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal);
            return new(
                document,
                definition: document.GetDefinition<CanonicalProcessDefinition>(),
                plan: null,
                DocumentValidationResult.FromDiagnostics(diagnostics));
        }

        var definition = document.GetDefinition<CanonicalProcessDefinition>();
        return new(
            document,
            definition,
            new CompiledProcessPlan(document, definition, context),
            validation);
    }
}
