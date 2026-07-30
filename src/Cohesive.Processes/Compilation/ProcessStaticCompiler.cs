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

    /// <summary>A whole-definition atomic demand crosses a durable wait or activation boundary.</summary>
    public const string AtomicScopeCrossesDurableBoundary = "processes.compilation.atomicScope.crossesDurableBoundary";

    /// <summary>
    /// A whole-definition atomic demand contains an external interaction or child invocation, or may produce an
    /// interaction through an emission-capable host operation.
    /// </summary>
    public const string AtomicScopeContainsExternalInteraction = "processes.compilation.atomicScope.containsExternalInteraction";
}

/// <summary>Compiles canonical Process IR into a deterministic target-independent executable index.</summary>
/// <remarks>
/// Compilation performs no I/O and selects no orchestration or storage backend. Canonical Process validation remains
/// the semantic authority; this compiler derives one effect/resource summary, admits explicit guarantee demands,
/// structurally preflights explicit demands, and indexes the validated closed node table without introducing another
/// persisted model. Successful target-independent compilation does not prove physical target capability.
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
        ProcessDefinitionValidationContext context) =>
        Compile(document, context, ProcessCompilationOptions.Default);

    /// <summary>
    /// Compiles one exact Process definition with explicit external evidence and retained realization demands.
    /// </summary>
    /// <param name="document">Canonical shared execution-definition document.</param>
    /// <param name="context">Exact linked definitions, interaction contracts, and optional shape evidence.</param>
    /// <param name="options">
    /// Explicit compiler demands. Whole-definition scope is the only analysis-demand scope; the default requests
    /// no whole-Process atomicity and leaves per-activation durable commit guarantees unchanged. Authored arbitrary
    /// scope regions remain deferred.
    /// </param>
    /// <returns>
    /// A structurally eligible target-independent plan, or structured canonical and scope-analysis diagnostics.
    /// A downstream realization compiler must still prove target capabilities for retained demands.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/>, <paramref name="context"/>, or <paramref name="options"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical semantic content has no stable JSON representation.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical semantic content cannot be decoded using the strict wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Canonical semantic content contains an unsupported runtime type.</exception>
    public static ProcessCompilationResult Compile(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext context,
        ProcessCompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

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
        var effectSummary = ProcessEffectAnalyzer.Analyze(definition);
        var scopeValidation = ProcessScopeAnalyzer.Validate(document, definition, effectSummary, options);
        if (!scopeValidation.IsValid)
        {
            var diagnostics = validation.Diagnostics
                .Concat(scopeValidation.Diagnostics)
                .OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal);
            return new(
                document,
                definition,
                plan: null,
                DocumentValidationResult.FromDiagnostics(diagnostics));
        }

        return new(
            document,
            definition,
            new CompiledProcessPlan(document, definition, context, options, effectSummary),
            validation);
    }
}
