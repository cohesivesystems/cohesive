using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

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
        var report = RelationQueryRealizationCompiler.Compile(
            plan,
            RelationQueryInMemoryTargetProfile.Create(temporalCapabilities),
            RelationQueryInMemoryTargetProfile.Policy);
        return Analyze(report, evaluation);
    }

    public static ImmutableArray<RelationRuntimeDiagnostic> Analyze(
        RelationQueryRealizationReport report,
        RelationQueryEvaluationId evaluation)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.IsRealizable)
            return [];

        var requirements = report.Requirements.ToDictionary(static requirement => requirement.Id);
        var allUnavailable = report.Decisions
            .OfType<UnavailableRelationQueryRealizationDecision>()
            .Select(decision => new UnsupportedRequirement(decision, requirements[decision.Requirement]))
            .ToImmutableArray();
        var unavailableRequirementIds = allUnavailable
            .Select(static item => item.Requirement.Id)
            .ToHashSet();
        var planningCauses = report.Diagnostics
            .Where(static diagnostic => diagnostic.Requirement is not null)
            .GroupBy(static diagnostic => diagnostic.Requirement!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
                    .ToImmutableArray());
        var unavailable = SuppressAmbientDuplicates(allUnavailable);
        unavailable = SuppressOccurrenceReconstructionDuplicates(unavailable);

        List<RelationRuntimeDiagnostic> diagnostics = [];
        HashSet<SupportIssueKey> issues = [];
        foreach (var item in unavailable)
        {
            var origin = RuntimeOrigin(item.Requirement);
            Add(
                item.Requirement.Origin?.Input,
                origin.Node,
                origin.SemanticSite,
                Describe(
                    item,
                    planningCauses.GetValueOrDefault(item.Requirement.Id, [])));
        }

        foreach (var diagnostic in report.Diagnostics.Where(diagnostic =>
                     diagnostic.Severity == DiagnosticSeverity.Error
                     && (diagnostic.Requirement is null
                         || !unavailableRequirementIds.Contains(diagnostic.Requirement.Value))))
        {
            requirements.TryGetValue(
                diagnostic.Requirement ?? default,
                out var requirement);
            var origin = requirement is null
                ? new RuntimeRequirementOrigin(diagnostic.Node, diagnostic.SemanticSite)
                : RuntimeOrigin(requirement);
            Add(
                requirement?.Origin?.Input,
                diagnostic.Node ?? origin.Node,
                diagnostic.SemanticSite ?? origin.SemanticSite,
                $"Canonical in-memory realization failed because of planning diagnostic "
                + $"'{diagnostic.Code}': {diagnostic.Message}");
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
            string? semanticSite,
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

    static ImmutableArray<UnsupportedRequirement> SuppressAmbientDuplicates(
        ImmutableArray<UnsupportedRequirement> unavailable)
    {
        var operationSites = unavailable
            .Where(static item => item.Requirement.Capability is ExpressionRelationQueryCapability
            {
                RequirementKind: ExprCapabilityRequirementKind.Operation
            })
            .Select(static item => ExpressionSiteKey(item.Requirement))
            .ToHashSet();
        return
        [
            .. unavailable.Where(item =>
                item.Requirement.Capability is not ExpressionRelationQueryCapability
                {
                    RequirementKind: ExprCapabilityRequirementKind.Ambient
                }
                || !operationSites.Contains(ExpressionSiteKey(item.Requirement)))
        ];
    }

    static ImmutableArray<UnsupportedRequirement> SuppressOccurrenceReconstructionDuplicates(
        ImmutableArray<UnsupportedRequirement> unavailable)
    {
        return
        [
            .. unavailable.Where(item =>
            {
                if (item.Requirement.Capability is not StructuralRelationQueryCapability
                    {
                        Role: RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction
                    }
                    || item.Requirement.Origin is not { Input: { } input, FieldPath: { } path })
                {
                    return true;
                }

                return !unavailable.Any(other =>
                    other.Requirement.Id != item.Requirement.Id
                    && other.Requirement.Capability is StructuralRelationQueryCapability
                    {
                        Role: not RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction
                    }
                    && other.Requirement.Origin?.Input == input
                    && other.Requirement.Origin?.FieldPath == path);
            })
        ];
    }

    static (string Node, string Site, string Path) ExpressionSiteKey(
        RelationQueryRealizationRequirement requirement) =>
        (
            requirement.Origin?.Node?.Value ?? string.Empty,
            requirement.Origin?.SemanticSite ?? string.Empty,
            requirement.Origin?.ExpressionPath ?? string.Empty
        );

    static RuntimeRequirementOrigin RuntimeOrigin(RelationQueryRealizationRequirement requirement)
    {
        if (requirement.Capability is StructuralRelationQueryCapability
            {
                Role: RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction
            })
        {
            var expressionStep = requirement.Uses
                .SelectMany(static use => use.Traces)
                .SelectMany(static trace => trace.Steps)
                .Where(static step => step.Kind == RelationQueryRealizationTraceStepKind.ExpressionSite)
                .OrderBy(static step => step.ExpressionSite?.Value ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault();
            if (expressionStep is not null)
                return new(expressionStep.Node, expressionStep.ExpressionSite?.Value);
        }

        return new(requirement.Origin?.Node, requirement.Origin?.SemanticSite);
    }

    static string Describe(
        UnsupportedRequirement item,
        ImmutableArray<RelationQueryRealizationDiagnostic> planningCauses)
    {
        var requirement = item.Requirement;
        var message = requirement.Capability switch
        {
            TemporalRelationQueryCapability temporal =>
                "Canonical in-memory interpretation does not support temporal execution capability "
                + $"'{temporal.Capability}' required by temporal join node "
                + $"'{requirement.Origin?.Node?.Value ?? "unknown"}'.",
            ExpressionRelationQueryCapability expression =>
                "Canonical in-memory interpretation does not support expression capability "
                + $"'{expression.Capability.Value}'.",
            StructuralRelationQueryCapability structural =>
                "Canonical in-memory interpretation does not support "
                + (structural.PathKind is RelationQueryStructuralPathKind.CollectionElement
                    or RelationQueryStructuralPathKind.NestedCollectionElement
                    ? "collection-element "
                    : string.Empty)
                + $"structural capability '{structural.Role}/{structural.PathKind}'"
                + (requirement.Origin?.FieldPath is { } path ? $" for path '{path}'." : "."),
            LogicalRelationQueryCapability logical =>
                $"Canonical in-memory interpretation does not support logical capability '{logical.Kind}'.",
            GuaranteeRelationQueryCapability guarantee =>
                $"Canonical in-memory interpretation does not preserve required guarantee '{guarantee.Kind}'.",
            PrimitiveRelationQueryCapability primitive =>
                $"Canonical in-memory interpretation does not provide primitive capability '{primitive.Kind}'.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(requirement),
                requirement.Capability,
                "Unsupported realization capability variant.")
        };
        var codes = planningCauses
            .Select(static diagnostic => diagnostic.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0)
            return $"{message} Realization reason: {item.Decision.Reason}.";

        var label = codes.Length == 1 ? "cause" : "causes";
        return $"{message} Planning {label}: {string.Join(", ", codes)}; "
            + $"realization reason: {item.Decision.Reason}.";
    }

    readonly record struct SupportIssueKey(
        string? Input,
        string? Node,
        string? SemanticSite,
        string Message);

    readonly record struct UnsupportedRequirement(
        UnavailableRelationQueryRealizationDecision Decision,
        RelationQueryRealizationRequirement Requirement);

    readonly record struct RuntimeRequirementOrigin(
        QueryNodeId? Node,
        string? SemanticSite);
}
