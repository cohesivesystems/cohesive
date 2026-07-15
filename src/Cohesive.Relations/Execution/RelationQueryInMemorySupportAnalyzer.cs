using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

static class RelationQueryInMemorySupportAnalyzer
{
    public static ImmutableArray<RelationRuntimeDiagnostic> Analyze(
        CompiledRelationQueryPlan plan,
        RelationQueryEvaluationId evaluation,
        RelationQueryTemporalExecutionCapabilityProfile temporalCapabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(temporalCapabilities);

        List<RelationRuntimeDiagnostic> diagnostics = [];
        HashSet<SupportIssueKey> issues = [];
        var capabilityInputs = plan.RequirementGraph.Inputs
            .OfType<RelationQueryCapabilityInput>()
            .ToDictionary(static input => input.Capability);

        foreach (var requirement in plan.InputContract.TemporalCapabilities)
        {
            if (temporalCapabilities.Supports(requirement.Capability))
                continue;

            Add(
                requirement.Id,
                requirement.Node,
                requirement.SemanticSite,
                $"Canonical in-memory interpretation does not support temporal execution capability "
                + $"'{requirement.Capability}' required by temporal join node '{requirement.Node.Value}'.");
        }

        foreach (var site in plan.ExecutionSlice.ExpressionSites)
        {
            var unsupportedUses = site.Analysis.CapabilityUses
                .Where(use => !RelationQueryExpressionEvaluator.SupportedCapabilities.Supports(
                    use.Requirement.Capability))
                .GroupBy(static use => use.ExpressionPath, StringComparer.Ordinal)
                .SelectMany(static group =>
                {
                    var operationUses = group
                        .Where(static use =>
                            use.Requirement.Kind == ExprCapabilityRequirementKind.Operation)
                        .ToArray();
                    return operationUses.Length == 0
                        ? group.AsEnumerable()
                        : operationUses.AsEnumerable();
                })
                .GroupBy(static use => use.Requirement)
                .Select(static group => group.First())
                .OrderBy(static use => (int)use.Requirement.Kind)
                .ThenBy(static use => use.Requirement.Capability.Value, StringComparer.Ordinal);
            foreach (var use in unsupportedUses)
            {
                capabilityInputs.TryGetValue(use.Requirement, out var input);
                Add(
                    input?.Id,
                    site.Node,
                    site.Analysis.Site.Id.Value,
                    $"Canonical in-memory interpretation does not support expression capability "
                    + $"'{use.Requirement.Capability.Value}'.");
            }

            foreach (var field in site.Analysis.Requirements.Fields.Where(field =>
                         field.Root == ExprFieldRootKind.CurrentItem
                         && !RelationQueryExpressionEvaluator.SupportsFieldPath(field.Path)))
            {
                Add(
                    input: null,
                    site.Node,
                    site.Analysis.Site.Id.Value,
                    $"Canonical in-memory interpretation does not support collection-element field path "
                    + $"'{field.Path}' at expression site '{site.Analysis.Site.Id.Value}'.");
            }
        }

        foreach (var input in plan.RequirementGraph.Inputs
                     .OfType<RelationQueryFieldInput>()
                     .Where(static input =>
                         !RelationQueryExpressionEvaluator.SupportsFieldPath(input.Field.Path)))
        {
            var edges = plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == input.Id)
                .ToArray();
            var step = edges
                .SelectMany(static edge => edge.Traces)
                .SelectMany(static trace => trace.Steps)
                .Where(static candidate => candidate.ExpressionSite is not null)
                .OrderBy(static candidate => candidate.ExpressionSite!.Value.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            var node = step.ExpressionSite is null
                ? edges.Select(static edge => edge.Output.Node).FirstOrDefault()
                : step.Node;
            var semanticSite = step.ExpressionSite?.Value ?? input.Field.Path.ToString();
            Add(
                input.Id,
                node,
                semanticSite,
                $"Canonical in-memory interpretation cannot reconstruct collection-element field input "
                + $"'{input.Id.Value}' at path '{input.Field.Path}' from occurrence-scoped evidence.");
        }

        foreach (var node in plan.ExecutionSlice.Nodes)
        {
            foreach (var assignment in node.ProjectionAssignments.Where(static assignment =>
                         !RelationQueryExpressionEvaluator.SupportsFieldPath(assignment.Definition.Target)))
            {
                Add(
                    input: null,
                    node.Id,
                    assignment.ValueSite.Analysis.Site.Id.Value,
                    $"Canonical in-memory interpretation does not support collection-element projection target "
                    + $"'{assignment.Definition.Target}'.");
            }

            foreach (var grouping in node.AggregateGroupings.Where(static grouping =>
                         !RelationQueryExpressionEvaluator.SupportsFieldPath(grouping.Definition.Target)))
            {
                Add(
                    input: null,
                    node.Id,
                    grouping.KeySite.Analysis.Site.Id.Value,
                    $"Canonical in-memory interpretation does not support collection-element grouping target "
                    + $"'{grouping.Definition.Target}'.");
            }

            foreach (var assignment in node.AggregateAssignments.Where(static assignment =>
                         !RelationQueryExpressionEvaluator.SupportsFieldPath(assignment.Definition.Target)))
            {
                Add(
                    input: null,
                    node.Id,
                    assignment.ValueSite?.Analysis.Site.Id.Value
                        ?? $"{node.Id.Value}/aggregate/{assignment.Definition.Id.Value}/operation",
                    $"Canonical in-memory interpretation does not support collection-element aggregate target "
                    + $"'{assignment.Definition.Target}'.");
            }
        }

        return
        [
            .. diagnostics
                .OrderBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];

        void Add(
            RelationQueryInputId? input,
            QueryNodeId? node,
            string semanticSite,
            string message)
        {
            SupportIssueKey key = new(input?.Value, node?.Value, semanticSite, message);
            if (!issues.Add(key))
                return;

            diagnostics.Add(new(
                RelationRuntimeDiagnosticCodes.ExecutionTargetCapabilityUnsupported,
                DiagnosticSeverity.Error,
                message,
                evaluation,
                input: input,
                node: node,
                semanticSite: semanticSite));
        }
    }

    readonly record struct SupportIssueKey(
        string? Input,
        string? Node,
        string SemanticSite,
        string Message);
}
