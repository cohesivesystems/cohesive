using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Compilation;

/// <summary>All-or-nothing admission evidence for a selected canonical Process dependency closure.</summary>
public sealed class ProcessClosureCompilationResult
{
    internal ProcessClosureCompilationResult(
        ImmutableArray<CompiledProcessPlan> plans,
        DocumentValidationResult validation,
        ExecutionDefinitionReference? failedDefinition = null)
    {
        Plans = plans;
        Validation = validation;
        FailedDefinition = failedDefinition;
    }

    /// <summary>Bottom-up plans; empty on failure so partial preparation cannot be admitted.</summary>
    public ImmutableArray<CompiledProcessPlan> Plans { get; }

    /// <summary>Canonical validation or exact dependency resolution diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>The exact definition whose resolution or compilation failed, otherwise null.</summary>
    public ExecutionDefinitionReference? FailedDefinition { get; }

    /// <summary>Whether every selected root and reachable child compiled successfully.</summary>
    public bool IsSuccessful => Validation.IsValid;
}

public static partial class ProcessStaticCompiler
{
    /// <summary>Compiles selected roots and their authored child closure once in dependency order.</summary>
    /// <param name="roots">Exact deployment roots; unrelated catalog definitions are not compiled.</param>
    /// <param name="documents">Integrity-checked canonical documents used to resolve exact child references.</param>
    /// <param name="externalContext">Non-Process linking evidence, interactions, and optional shapes.</param>
    /// <param name="options">Demands applied to every definition; defaults to ordinary canonical compilation.</param>
    /// <returns>All reachable plans or structured diagnostics with no admissible partial plans.</returns>
    /// <remarks>
    /// Performs no I/O. Child edges come exclusively from canonical Process nodes. Successful child evidence
    /// is reused within this call; the caller owns reuse across calls and target capability admission.
    /// Every resolved document still passes complete canonical compilation validation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">External evidence contains Process links competing with the documents.</exception>
    public static ProcessClosureCompilationResult CompileClosure(
        IEnumerable<ExecutionDefinitionReference> roots,
        ExecutionDefinitionDocumentCatalog documents,
        ProcessDefinitionValidationContext externalContext,
        ProcessCompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(externalContext);
        if (externalContext.DefinitionLinks.Any(static link => link.Kind == ProcessDefinitionLinkKind.Process))
            throw new ArgumentException("Process dependency evidence must be derived from the canonical documents.", nameof(externalContext));
        options ??= ProcessCompilationOptions.Default;
        var plans = new Dictionary<ExecutionDefinitionReference, CompiledProcessPlan>();
        var ordered = ImmutableArray.CreateBuilder<CompiledProcessPlan>();
        var active = new HashSet<ExecutionDefinitionReference>();
        var definitions = new Dictionary<ExecutionDefinitionReference, ProcessDefinition>();
        var dependencies = new Dictionary<ExecutionDefinitionReference, ImmutableArray<ExecutionDefinitionReference>>();
        var work = new Stack<(ExecutionDefinitionReference Reference, bool Complete)>();
        foreach (var root in roots.Distinct().OrderByDescending(static reference => reference,
                     Comparer<ExecutionDefinitionReference>.Create(ExecutionDefinitionReference.CompareCanonical)))
            work.Push((root, false));
        while (work.TryPop(out var item))
        {
            if (plans.ContainsKey(item.Reference))
                continue;
            var resolution = documents.ValidateReference(item.Reference, "/processDependencies", out var document);
            if (!resolution.IsValid || document is null)
                return Failed(resolution, item.Reference);
            if (document.Kind != ProcessDefinitionDocuments.Kind)
                return Failed(Compile(document, externalContext, options).Validation, item.Reference);
            if (item.Complete)
            {
                var descendants = new HashSet<ExecutionDefinitionReference>();
                var pending = new Stack<ExecutionDefinitionReference>(dependencies[item.Reference]);
                while (pending.TryPop(out var dependency))
                {
                    if (!descendants.Add(dependency))
                        continue;
                    foreach (var child in plans[dependency].DefinitionLink.ProcessDependencies)
                        pending.Push(child);
                }
                var context = new ProcessDefinitionValidationContext(
                    externalContext.DefinitionLinks.Concat(descendants.Select(reference => plans[reference].DefinitionLink)),
                    externalContext.InteractionContracts,
                    externalContext.ShapeGraph);
                var result = CompileCore(document, context, options, definitions[item.Reference]);
                if (!result.IsSuccessful)
                    return Failed(result.Validation, item.Reference);
                plans.Add(item.Reference, result.Plan!);
                ordered.Add(result.Plan!);
                active.Remove(item.Reference);
                continue;
            }
            if (active.Any(reference => reference.DefinitionId == item.Reference.DefinitionId
                                        && reference.RevisionId == item.Reference.RevisionId))
                return Failed(DocumentValidationResult.FromDiagnostics([
                    new DocumentValidationDiagnostic(
                        ProcessDefinitionDiagnosticCodes.ProcessRecursionUnsupported,
                        DiagnosticSeverity.Error,
                        $"Process dependency cycle reaches '{item.Reference.DefinitionId.Value}' revision '{item.Reference.RevisionId.Value}'.",
                        "/processDependencies")
                ]), item.Reference);
            ProcessDefinition definition;
            try
            {
                definition = document.GetDefinition<ProcessDefinition>();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException
                                              or InvalidOperationException or NotSupportedException
                                              or FormatException or OverflowException)
            {
                return Failed(Compile(document, externalContext, options).Validation, item.Reference);
            }
            definitions.Add(item.Reference, definition);
            active.Add(item.Reference);
            work.Push((item.Reference, true));
            dependencies.Add(item.Reference, definition.GetProcessDependencies());
            foreach (var dependency in dependencies[item.Reference].Reverse())
                work.Push((dependency, false));
        }
        return new(ordered.ToImmutable(), DocumentValidationResult.Valid);

        static ProcessClosureCompilationResult Failed(DocumentValidationResult validation, ExecutionDefinitionReference reference) => new([], validation, reference);
    }
}
