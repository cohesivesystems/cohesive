using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Explain;

/// <summary>Computes the canonical semantic identity of a relation/query explain artifact.</summary>
public static class RelationQueryExplainFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical explain profile identifier.</summary>
    public const string Canonicalization = "relation-query-explain/v1-c14n/v2";

    /// <summary>Computes the canonical fingerprint of one normalized explain artifact.</summary>
    /// <param name="artifact">Explain artifact whose fingerprint is computed.</param>
    /// <returns>
    /// A SHA-256 fingerprint over deterministic compilation-stage content, excluding the persisted fingerprint,
    /// runtime evaluation stage, evaluation diagnostics, and human-readable diagnostic prose.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The artifact cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">The artifact contains content that cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The artifact contains an unsupported serialization type.</exception>
    public static RelationQueryExplainFingerprint Compute(RelationQueryExplainArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var options = RelationQueryExplainJsonSerializer.CreateOptions();
        var root = JsonSerializer.SerializeToNode(artifact, options) as JsonObject
            ?? throw new InvalidOperationException("Failed to materialize canonical relation/query explain JSON.");
        root.Remove("fingerprint");
        RemoveEvaluationObservations(root);
        RemoveNonSemanticProse(root);
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            root,
            options,
            static _ => CanonicalJsonArrayOrdering.Sequence);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static void RemoveEvaluationObservations(JsonObject root)
    {
        if (root["stages"] is JsonArray stages)
        {
            for (var index = stages.Count - 1; index >= 0; index--)
            {
                if (stages[index] is JsonObject stage
                    && string.Equals(
                        stage[RelationQueryExplainStageWireNames.Discriminator]?.GetValue<string>(),
                        RelationQueryExplainStageWireNames.Evaluation,
                        StringComparison.Ordinal))
                {
                    stages.RemoveAt(index);
                }
            }
        }

        if (root["diagnostics"] is JsonArray diagnostics)
        {
            for (var index = diagnostics.Count - 1; index >= 0; index--)
            {
                if (diagnostics[index] is JsonObject diagnostic
                    && string.Equals(
                        diagnostic["stage"]?.GetValue<string>(),
                        RelationQueryExplainStageWireNames.Evaluation,
                        StringComparison.Ordinal))
                {
                    diagnostics.RemoveAt(index);
                }
            }
        }
    }

    internal static void RemoveNonSemanticProse(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                value.Remove("message");
                value.Remove("description");
                value.Remove("resolution");
                value.Remove("evidenceReference");
                foreach (var property in value.ToArray())
                    RemoveNonSemanticProse(property.Value);
                break;
            case JsonArray array:
                foreach (var item in array)
                    RemoveNonSemanticProse(item);
                break;
        }
    }
}

/// <summary>Computes integrity identities for sanitized runtime evaluation observations.</summary>
public static class RelationQueryEvaluationObservationFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical evaluation-observation profile identifier.</summary>
    public const string Canonicalization = "relation-query-evaluation-observation/v1-c14n/v1";

    /// <summary>Computes the integrity fingerprint of one normalized evaluation observation.</summary>
    /// <param name="evaluation">Sanitized evaluation observation to fingerprint.</param>
    /// <returns>A SHA-256 fingerprint over observation attribution, status, results, gaps, and typed diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The observation cannot be materialized as JSON.</exception>
    /// <exception cref="JsonException">The observation cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The observation contains an unsupported serialization type.</exception>
    public static RelationQueryEvaluationObservationFingerprint Compute(
        RelationQueryEvaluationExplanation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var options = RelationQueryExplainJsonSerializer.CreateOptions();
        var root = JsonSerializer.SerializeToNode(evaluation, options) as JsonObject
            ?? throw new InvalidOperationException("Failed to materialize a canonical evaluation observation.");
        root.Remove("observationFingerprint");
        RelationQueryExplainFingerprinter.RemoveNonSemanticProse(root);
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            root,
            options,
            static _ => CanonicalJsonArrayOrdering.Sequence);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

}

static class RelationQueryExplainStageValidator
{
    public static ImmutableArray<RelationQueryExplainStage> NormalizeAndValidate(
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        var normalized = stages.IsDefault ? [] : stages;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("An explain artifact requires a static-compilation stage.", nameof(stages));
        if (normalized.Any(static stage => stage is null))
            throw new ArgumentException("Explain stages cannot contain null entries.", nameof(stages));
        if (normalized.GroupBy(static stage => stage.WireName, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Explain stages cannot repeat a lifecycle stage.", nameof(stages));

        ImmutableArray<RelationQueryExplainStage> ordered =
        [
            .. normalized.OrderBy(static stage => RelationQueryExplainStageWireNames.Rank(stage.WireName))
        ];
        if (ordered[0] is not RelationQueryStaticCompilationExplainStage staticStage)
            throw new ArgumentException("The static-compilation stage must be present and first.", nameof(stages));
        if (staticStage.Status != RelationQueryExplainStageStatus.Complete)
        {
            if (ordered.Length == 1)
                return ordered;
            if (ordered.Length != 2
                || ordered[1] is not RelationQueryEvaluationExplainStage
                {
                    Status: RelationQueryExplainStageStatus.Failed,
                    Evaluation.Plan: null
                })
            {
                throw new ArgumentException(
                    "Failed static compilation can retain only a terminal failed evaluation without a plan.",
                    nameof(stages));
            }
            ValidatePlanlessEvaluation((RelationQueryEvaluationExplainStage)ordered[1], stages);
            return ordered;
        }

        var plan = staticStage.Plan!.Reference;
        RelationQueryProfileFeasibilityExplainStage? profile = null;
        RelationQuerySourcePlacementExplainStage? placement = null;
        RelationQueryBoundRealizationExplainStage? bound = null;
        RelationQueryPhysicalPlanningExplainStage? physical = null;

        foreach (var stage in ordered)
        {
            switch (stage)
            {
                case RelationQueryStaticCompilationExplainStage:
                    break;
                case RelationQueryProfileFeasibilityExplainStage value:
                    RequirePlan(plan, value.Report.Plan, value.WireName);
                    var staticPlan = staticStage.Plan;
                    var profileRequirementsFingerprint = RelationQueryRealizationRequirementSetFingerprinter.Compute(
                        value.Report.Plan,
                        value.Report.Observability,
                        value.Report.Requirements);
                    if (value.Report.Observability != staticPlan.Observability
                        || !Equals(profileRequirementsFingerprint, staticPlan.RealizationRequirementsFingerprint))
                    {
                        throw new ArgumentException(
                            "Profile feasibility does not interpret the retained static requirements and observability contract.",
                            nameof(stages));
                    }
                    profile = value;
                    break;
                case RelationQuerySourcePlacementExplainStage value:
                    var placementMatches = RelationQueryExplainAffinity.SamePlan(plan, value.Placement.Plan);
                    if (placementMatches != (value.Status == RelationQueryExplainStageStatus.Complete))
                    {
                        throw new ArgumentException(
                            "Source-placement status must identify whether its compiled-plan affinity is valid.",
                            nameof(stages));
                    }
                    placement = value;
                    break;
                case RelationQueryBoundRealizationExplainStage value:
                    RequireCompleteProfile(profile, value.WireName);
                    RequireCompletePlacement(placement, value.WireName);
                    RequirePlan(plan, value.Report.ProfileFeasibility.Plan, value.WireName);
                    if (!Equals(profile!.Report.Fingerprint, value.Report.ProfileFeasibility.Fingerprint)
                        || !Equals(placement!.Placement.Fingerprint, value.Report.Placement))
                    {
                        throw new ArgumentException(
                            "Bound realization does not belong to the retained profile and placement.",
                            nameof(stages));
                    }
                    var knownBranches = staticStage.Plan.Branches.Select(static branch => branch.Id).ToHashSet();
                    if (value.Report.Branches.Any(branch => !knownBranches.Contains(branch)))
                        throw new ArgumentException("Bound realization contains an unknown result branch.", nameof(stages));
                    bound = value;
                    break;
                case RelationQueryPhysicalPlanningExplainStage value:
                    RequireCompleteProfile(profile, value.WireName);
                    RequirePlacement(placement, value.WireName);
                    RequirePlan(plan, value.Plan, value.WireName);
                    if (!Equals(profile!.Report.Fingerprint, value.Realization)
                        || !Equals(placement!.Placement.Fingerprint, value.Placement))
                    {
                        throw new ArgumentException(
                            "Physical planning does not belong to the retained profile and placement.",
                            nameof(stages));
                    }
                    if (value.Status == RelationQueryExplainStageStatus.Complete
                        && placement.Status != RelationQueryExplainStageStatus.Complete)
                    {
                        throw new ArgumentException(
                            "Successful physical planning requires a plan-affine source placement.",
                            nameof(stages));
                    }
                    physical = value;
                    break;
                case RelationQueryNativeCompilationExplainStage value:
                    RequireNativeAffinity(staticStage, profile, placement, bound, value, stages);
                    break;
                case RelationQueryEvaluationExplainStage value:
                    ValidateEvaluation(
                        staticStage.Plan,
                        profile,
                        placement,
                        bound,
                        physical,
                        value,
                        stages);
                    break;
                default:
                    throw new ArgumentException($"Unsupported explain stage '{stage.GetType().Name}'.", nameof(stages));
            }
        }

        return ordered;
    }

    static void ValidatePlanlessEvaluation(
        RelationQueryEvaluationExplainStage evaluation,
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        if (evaluation.Evaluation.Diagnostics.Any(static diagnostic =>
                diagnostic.Branch is not null
                || diagnostic.Requirement is not null
                || diagnostic.Node is not null
                || diagnostic.Input is not null
                || diagnostic.Output is not null
                || diagnostic.PlacementBinding is not null
                || diagnostic.PhysicalStage is not null
                || diagnostic.Source is not null
                || diagnostic.CapabilityEvidence is not null
                || diagnostic.OperatingBoundary is not null
                || diagnostic.ContextEvidence is not null
                || diagnostic.SemanticSite is not null
                || diagnostic.CompositionRule is not null
                || diagnostic.Override is not null
                || diagnostic.Field is not null
                || diagnostic.BindingSetting is not null
                || diagnostic.ConfigurationOrigin is not null
                || diagnostic.ConfigurationAuthority is not null
                || diagnostic.AdapterDecisionCode is not null))
        {
            throw new ArgumentException(
                "Evaluation after failed static compilation cannot cite plan-scoped attribution.",
                nameof(stages));
        }
    }

    static void ValidateEvaluation(
        RelationQueryStaticPlanExplanation staticPlan,
        RelationQueryProfileFeasibilityExplainStage? profile,
        RelationQuerySourcePlacementExplainStage? placement,
        RelationQueryBoundRealizationExplainStage? bound,
        RelationQueryPhysicalPlanningExplainStage? physical,
        RelationQueryEvaluationExplainStage evaluation,
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        if (evaluation.Evaluation.Plan is null)
        {
            throw new ArgumentException(
                "Evaluation after successful static compilation requires exact plan attribution.",
                nameof(stages));
        }
        RequirePlan(staticPlan.Reference, evaluation.Evaluation.Plan, evaluation.WireName);

        var branches = staticPlan.Branches.ToDictionary(static branch => branch.Id);
        foreach (var result in evaluation.Evaluation.Results)
        {
            if (!branches.TryGetValue(result.Branch, out var branch)
                || result.Shape != branch.Shape
                || result.Kind != ToExecutionKind(branch.Kind))
            {
                throw new ArgumentException(
                    "Evaluation result kind, shape, or branch attribution does not match the retained static branch.",
                    nameof(stages));
            }
        }

        var resultBranches = evaluation.Evaluation.Results.Select(static result => result.Branch).ToHashSet();
        if (evaluation.Evaluation.Status is RelationQueryExecutionStatus.Succeeded
                or RelationQueryExecutionStatus.Incomplete
            && !resultBranches.SetEquals(branches.Keys))
        {
            throw new ArgumentException(
                "A successful or incomplete evaluation must summarize every demanded terminal branch.",
                nameof(stages));
        }
        if (evaluation.Evaluation.Status == RelationQueryExecutionStatus.Failed
            && resultBranches.Count != 0
            && !resultBranches.SetEquals(branches.Keys))
        {
            throw new ArgumentException(
                "A failed evaluation can retain either no results or complete branch-attributed results.",
                nameof(stages));
        }

        var inputs = staticPlan.RequirementGraph.Inputs.Select(static input => input.Id).ToHashSet();
        var outputs = staticPlan.RequirementGraph.Outputs.Select(static output => output.Id).ToHashSet();
        var edges = staticPlan.RequirementGraph.Edges
            .Select(static edge => (edge.Input, edge.Output))
            .ToHashSet();
        foreach (var gap in evaluation.Evaluation.RequirementGaps)
        {
            if (!inputs.Contains(gap.Input)
                || gap.AffectedOutputs.Any(output => !outputs.Contains(output) || !edges.Contains((gap.Input, output))))
            {
                throw new ArgumentException(
                    "An evaluation requirement gap does not resolve to a retained input-to-output edge.",
                    nameof(stages));
            }
        }

        if (evaluation.Status is RelationQueryExplainStageStatus.Complete or RelationQueryExplainStageStatus.Incomplete
            && physical?.Status != RelationQueryExplainStageStatus.Complete)
        {
            throw new ArgumentException(
                "A complete or incomplete evaluation requires a successful physical plan.",
                nameof(stages));
        }

        var nodes = staticPlan.LogicalPlan.RetainedNodes.ToHashSet();
        var requirements = staticPlan.RealizationRequirements.Select(static requirement => requirement.Id).ToHashSet();
        var placementBindings = placement?.Placement.Bindings.Select(static binding => binding.Id).ToHashSet() ?? [];
        var sources = placement?.Placement.SourceInstances.Select(static source => source.Id).ToHashSet() ?? [];
        var physicalStages = physical?.Result.Plan?.Stages.Select(static stage => stage.Id).ToHashSet() ?? [];
        var capabilityEvidence = profile?.Report.TargetProfile.Capabilities.Select(static item => item.Id).ToHashSet() ?? [];
        var boundaries = profile?.Report.TargetProfile.OperatingBoundaries.Select(static item => item.Id).ToHashSet() ?? [];
        var compositionRules = profile?.Report.Policy.CompositionRules.Select(static item => item.Id).ToHashSet() ?? [];
        var overrides = profile?.Report.Policy.Overrides.Select(static item => item.Id).ToHashSet() ?? [];
        var context = bound?.Report.Evidence.Assessments.ToDictionary(static item => item.Id)
            ?? new Dictionary<RelationQueryContextEvidenceId, RelationQueryBoundRequirementAssessment>();
        var knownFields = staticPlan.RealizationRequirements
            .Select(static requirement => requirement.Origin?.FieldPath?.ToString())
            .Concat(staticPlan.RealizationRequirements.SelectMany(static requirement => requirement.Uses)
                .Select(static use => use.Output.Field?.Path.ToString()))
            .Concat(staticPlan.Branches.SelectMany(static branch => branch.Fields)
                .Select(static field => field.Path.ToString()))
            .Concat(context.Values.Select(static assessment => assessment.Field?.ToString()))
            .Where(static field => field is not null)
            .ToHashSet(StringComparer.Ordinal);
        var bindingSettings = context.Values
            .SelectMany(static assessment => new[] { assessment.ConfigurationSetting, assessment.FailedConfigurationSetting })
            .Concat(bound?.Report.Evidence.Binding.ConfigurationDecisions.Select(static decision => decision.Setting) ?? [])
            .Where(static setting => setting is not null)
            .ToHashSet(StringComparer.Ordinal);
        var configurationPairs = (placement?.Placement.ConfigurationDecisions ?? [])
            .Concat(bound?.Report.Evidence.Binding.ConfigurationDecisions ?? [])
            .Select(static decision => (decision.Origin, decision.Authority))
            .Concat(context.Values.Select(static assessment => (assessment.Origin, assessment.Authority)))
            .ToHashSet();
        var adapterDecisionCodes = context.Values
            .Where(static assessment => assessment.AdapterDecisionCode is not null)
            .Select(static assessment => assessment.AdapterDecisionCode!.Value)
            .ToHashSet();
        foreach (var diagnostic in evaluation.Evaluation.Diagnostics)
        {
            if (diagnostic.Branch is { } branch && !branches.ContainsKey(branch)
                || diagnostic.Node is { } node && !nodes.Contains(node)
                || diagnostic.Input is { } input && !inputs.Contains(input)
                || diagnostic.Output is { } output && !outputs.Contains(output)
                || diagnostic.Requirement is { } requirement && !requirements.Contains(requirement)
                || diagnostic.PlacementBinding is { } placementBinding && !placementBindings.Contains(placementBinding)
                || diagnostic.PhysicalStage is { } physicalStage && !physicalStages.Contains(physicalStage)
                || diagnostic.Source is { } source && !sources.Contains(source)
                || diagnostic.CapabilityEvidence is { } evidence && !capabilityEvidence.Contains(evidence)
                || diagnostic.OperatingBoundary is { } boundary && !boundaries.Contains(boundary)
                || diagnostic.ContextEvidence is { } contextEvidence && !context.ContainsKey(contextEvidence)
                || diagnostic.CompositionRule is { } compositionRule && !compositionRules.Contains(compositionRule)
                || diagnostic.Override is { } @override && !overrides.Contains(@override)
                || diagnostic.Field is { } field && !knownFields.Contains(field.ToString())
                || diagnostic.BindingSetting is { } bindingSetting && !bindingSettings.Contains(bindingSetting)
                || diagnostic.ConfigurationOrigin is { } origin
                    && !configurationPairs.Contains((origin, diagnostic.ConfigurationAuthority!))
                || diagnostic.AdapterDecisionCode is { } adapterDecisionCode
                    && !adapterDecisionCodes.Contains(adapterDecisionCode))
            {
                throw new ArgumentException(
                    "An evaluation diagnostic contains attribution absent from the retained lifecycle artifacts.",
                    nameof(stages));
            }

            if (diagnostic.Branch is { } diagnosticBranch
                && diagnostic.Output is { } diagnosticOutput
                && !branches[diagnosticBranch].Outputs.Any(output => output.Id == diagnosticOutput))
            {
                throw new ArgumentException(
                    "An evaluation diagnostic output belongs to a different result branch.",
                    nameof(stages));
            }
            if (diagnostic.ContextEvidence is { } contextId
                && diagnostic.Branch is { } contextBranch
                && context[contextId].Branch != contextBranch)
            {
                throw new ArgumentException(
                    "An evaluation diagnostic contextual evidence belongs to a different branch.",
                    nameof(stages));
            }
        }
    }

    static RelationQueryExecutionResultKind ToExecutionKind(RelationQueryNativeResultKind kind) =>
        kind switch
        {
            RelationQueryNativeResultKind.RelationRows or RelationQueryNativeResultKind.QueryRows =>
                RelationQueryExecutionResultKind.Rows,
            RelationQueryNativeResultKind.QueryAggregation => RelationQueryExecutionResultKind.Aggregation,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported native result kind.")
        };

    static void RequireNativeAffinity(
        RelationQueryStaticCompilationExplainStage staticStage,
        RelationQueryProfileFeasibilityExplainStage? profile,
        RelationQuerySourcePlacementExplainStage? placement,
        RelationQueryBoundRealizationExplainStage? bound,
        RelationQueryNativeCompilationExplainStage native,
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        RequireCompleteProfile(profile, native.WireName);
        RequireCompletePlacement(placement, native.WireName);
        if (bound is null)
            throw new ArgumentException("Native compilation requires bound realization.", nameof(stages));

        var attempt = native.Attempt;
        if (!RelationQueryExplainAffinity.SamePlan(staticStage.Plan!.Reference, attempt.Plan)
            || !Equals(profile!.Report.Fingerprint, attempt.ProfileFeasibility)
            || !Equals(bound.Report.Fingerprint, attempt.BoundRealization)
            || !Equals(placement!.Placement.Fingerprint, attempt.Placement)
            || !attempt.AdapterBinding.HasSameSemantics(bound.Report.Evidence.Binding)
            || attempt.AdapterBinding.Target != profile.Report.TargetProfile.Target
            || attempt.AdapterBinding.TargetProfile != profile.Report.TargetProfile.Id
            || !attempt.Branches.SequenceEqual(bound.Report.Branches))
        {
            throw new ArgumentException(
                "Native compilation attempt does not belong to the retained plan, profile, bound realization, placement, binding, and branch selection.",
                nameof(stages));
        }

        var knownBranches = staticStage.Plan.Branches.Select(static branch => branch.Id).ToHashSet();
        if (attempt.Branches.Any(branch => !knownBranches.Contains(branch)))
            throw new ArgumentException("Native compilation attempt contains an unknown result branch.", nameof(stages));
        if (bound.Status != RelationQueryExplainStageStatus.Complete
            && !native.Compilation.Artifacts.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Native artifacts require a complete contextual bound realization.",
                nameof(stages));
        }

        var assessments = bound.Report.Evidence.Assessments;
        var decisions = profile.Report.Decisions.ToDictionary(static decision => decision.Requirement);
        var logicalNodes = staticStage.Plan.LogicalPlan.RetainedNodes.ToHashSet();
        var knownAssignments = staticStage.Plan.RealizationRequirements
            .SelectMany(static requirement => requirement.Uses)
            .SelectMany(static use => use.Traces)
            .SelectMany(static trace => trace.Steps)
            .Where(static step => step.Assignment is not null)
            .Select(static step => step.Assignment!.Value)
            .ToHashSet();
        foreach (var artifact in native.Compilation.Artifacts)
        {
            var provenance = artifact.Provenance;
            if (!knownBranches.Contains(artifact.Branch)
                || !attempt.Branches.Contains(artifact.Branch)
                || !RelationQueryExplainAffinity.SamePlan(attempt.Plan, provenance.Plan)
                || !Equals(attempt.ProfileFeasibility, provenance.Realization)
                || !Equals(attempt.BoundRealization, provenance.BoundRealization)
                || !Equals(attempt.Placement, provenance.Placement)
                || !attempt.AdapterBinding.HasSameSemantics(provenance.AdapterBinding)
                || provenance.Target != profile.Report.TargetProfile.Target
                || provenance.TargetProfile != profile.Report.TargetProfile.Id)
            {
                throw new ArgumentException(
                    "A native artifact does not belong to the retained plan, branch, realization, binding, and placement.",
                    nameof(stages));
            }

            var reachableNodes = GetReachableNodes(staticStage.Plan.LogicalPlan, artifact.Branch, staticStage.Plan.Branches);
            if (provenance.CoveredNodes.Any(node => !logicalNodes.Contains(node) || !reachableNodes.Contains(node))
                || provenance.CoveredAssignments.Any(assignment => !knownAssignments.Contains(assignment)))
            {
                throw new ArgumentException(
                    "Native artifact coverage does not belong to its retained logical branch.",
                    nameof(stages));
            }

            var branchAssessments = assessments
                .Where(assessment => assessment.Branch == artifact.Branch)
                .ToImmutableArray();
            var expectedDecisions = branchAssessments
                .Select(static assessment => assessment.Requirement)
                .Distinct()
                .OrderBy(static requirement => requirement.Value, StringComparer.Ordinal)
                .Select(requirement => decisions.TryGetValue(requirement, out var decision)
                    ? RelationQueryNativeCompilationProvenanceFactory.CreateDecisionReference(decision)
                    : null)
                .ToArray();
            if (expectedDecisions.Any(static decision => decision is null)
                || provenance.RealizationDecisions.Length != expectedDecisions.Length
                || !provenance.RealizationDecisions.Zip(expectedDecisions)
                    .All(static pair => SameDecision(pair.First, pair.Second!)))
            {
                throw new ArgumentException(
                    "Native artifact realization decisions do not resolve to the retained profile report.",
                    nameof(stages));
            }

            var expectedContext = branchAssessments
                .Select(static assessment => assessment.Id)
                .OrderBy(static id => id.Value, StringComparer.Ordinal);
            if (!provenance.ContextEvidence.SequenceEqual(expectedContext))
            {
                throw new ArgumentException(
                    "Native artifact contextual evidence does not resolve to the retained bound realization.",
                    nameof(stages));
            }
        }

        ValidateNativeDiagnostics(
            native.Compilation.Diagnostics,
            staticStage.Plan,
            profile.Report,
            placement.Placement,
            bound.Report,
            attempt);

        if (native.Status == RelationQueryExplainStageStatus.Complete
            && (bound.Status != RelationQueryExplainStageStatus.Complete
                || !native.Compilation.Artifacts.Select(static artifact => artifact.Branch)
                    .SequenceEqual(attempt.Branches)))
        {
            throw new ArgumentException(
                "Exact native compilation must cover every selected bound branch.",
                nameof(stages));
        }
    }

    static HashSet<QueryNodeId> GetReachableNodes(
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryNativeResultBranchId branch,
        ImmutableArray<RelationQueryNativeResultBranch> branches)
    {
        var terminal = branches.Single(candidate => candidate.Id == branch).Node;
        var nodes = logicalPlan.Nodes.ToDictionary(static node => node.Node);
        HashSet<QueryNodeId> reachable = [];
        Stack<QueryNodeId> pending = new();
        pending.Push(terminal);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current))
                continue;
            foreach (var input in nodes[current].EffectiveInputs)
                pending.Push(input);
        }
        return reachable;
    }

    static bool SameDecision(
        RelationQueryNativeCompilationDecisionReference left,
        RelationQueryNativeCompilationDecisionReference right) =>
        left.Requirement == right.Requirement
        && left.Kind == right.Kind
        && left.CapabilityEvidence.SequenceEqual(right.CapabilityEvidence)
        && left.CompositionRules.SequenceEqual(right.CompositionRules)
        && left.Override == right.Override
        && left.OperatingBoundaries.SequenceEqual(right.OperatingBoundaries)
        && left.PreservedGuarantees.SequenceEqual(right.PreservedGuarantees);

    static void ValidateNativeDiagnostics(
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics,
        RelationQueryStaticPlanExplanation plan,
        RelationQueryRealizationReport profile,
        RelationQuerySourcePlacement placement,
        RelationQueryBoundRealizationReport bound,
        RelationQueryNativeCompilationAttemptReference attempt)
    {
        var branches = attempt.Branches.ToHashSet();
        var nodes = plan.LogicalPlan.RetainedNodes.ToHashSet();
        var inputs = plan.Reference.Inputs.ToHashSet();
        var requirements = profile.Requirements.Select(static requirement => requirement.Id).ToHashSet();
        var capabilityEvidence = profile.TargetProfile.Capabilities.Select(static evidence => evidence.Id).ToHashSet();
        var boundaries = profile.TargetProfile.OperatingBoundaries.Select(static boundary => boundary.Id).ToHashSet();
        var overrides = profile.Policy.Overrides.Select(static item => item.Id).ToHashSet();
        var context = bound.Evidence.Assessments.ToDictionary(static assessment => assessment.Id);
        var placementBindings = placement.Bindings.Select(static binding => binding.Id).ToHashSet();
        var knownFields = plan.RealizationRequirements
            .Select(static requirement => requirement.Origin?.FieldPath?.ToString())
            .Concat(plan.RealizationRequirements.SelectMany(static requirement => requirement.Uses)
                .Select(static use => use.Output.Field?.Path.ToString()))
            .Concat(plan.Branches.SelectMany(static branch => branch.Fields)
                .Select(static field => field.Path.ToString()))
            .Concat(context.Values.Select(static assessment => assessment.Field?.ToString()))
            .Where(static field => field is not null)
            .ToHashSet(StringComparer.Ordinal);
        var bindingSettings = context.Values
            .SelectMany(static assessment => new[] { assessment.ConfigurationSetting, assessment.FailedConfigurationSetting })
            .Concat(bound.Evidence.Binding.ConfigurationDecisions.Select(static decision => decision.Setting))
            .Where(static setting => setting is not null)
            .ToHashSet(StringComparer.Ordinal);
        var configurationPairs = placement.ConfigurationDecisions
            .Concat(bound.Evidence.Binding.ConfigurationDecisions)
            .Select(static decision => (decision.Origin, decision.Authority))
            .Concat(context.Values.Select(static assessment => (assessment.Origin, assessment.Authority)))
            .ToHashSet();
        var adapterDecisionCodes = context.Values
            .Where(static assessment => assessment.AdapterDecisionCode is not null)
            .Select(static assessment => assessment.AdapterDecisionCode!.Value)
            .ToHashSet();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Branch is { } branch && !branches.Contains(branch)
                || diagnostic.Node is { } node && !nodes.Contains(node)
                || diagnostic.Input is { } input && !inputs.Contains(input)
                || diagnostic.Requirement is { } requirement && !requirements.Contains(requirement)
                || diagnostic.CapabilityEvidence is { } evidence && !capabilityEvidence.Contains(evidence)
                || diagnostic.OperatingBoundary is { } boundary && !boundaries.Contains(boundary)
                || diagnostic.Override is { } @override && !overrides.Contains(@override)
                || diagnostic.PlacementBinding is { } placementBinding && !placementBindings.Contains(placementBinding)
                || diagnostic.ContextEvidence is { } contextEvidence && !context.ContainsKey(contextEvidence)
                || diagnostic.Field is { } field && !knownFields.Contains(field.ToString())
                || diagnostic.BindingSetting is { } bindingSetting && !bindingSettings.Contains(bindingSetting)
                || diagnostic.ConfigurationOrigin is { } origin
                    && !configurationPairs.Contains((origin, diagnostic.ConfigurationAuthority!))
                || diagnostic.AdapterDecisionCode is { } adapterDecisionCode
                    && !adapterDecisionCodes.Contains(adapterDecisionCode))
            {
                throw new ArgumentException(
                    "A native diagnostic contains attribution absent from the retained lifecycle artifacts.",
                    nameof(diagnostics));
            }
            if (diagnostic.ContextEvidence is { } contextId
                && diagnostic.Branch is { } diagnosticBranch
                && context[contextId].Branch != diagnosticBranch)
            {
                throw new ArgumentException(
                    "A native diagnostic contextual-evidence identity belongs to a different branch.",
                    nameof(diagnostics));
            }
        }
    }

    static void RequireCompleteProfile(RelationQueryProfileFeasibilityExplainStage? profile, string stage)
    {
        if (profile?.Status != RelationQueryExplainStageStatus.Complete)
            throw new ArgumentException($"Explain stage '{stage}' requires a complete profile-feasibility stage.");
    }

    static void RequirePlacement(RelationQuerySourcePlacementExplainStage? placement, string stage)
    {
        if (placement is null)
            throw new ArgumentException($"Explain stage '{stage}' requires source placement.");
    }

    static void RequireCompletePlacement(RelationQuerySourcePlacementExplainStage? placement, string stage)
    {
        if (placement?.Status != RelationQueryExplainStageStatus.Complete)
            throw new ArgumentException($"Explain stage '{stage}' requires plan-affine source placement.");
    }

    static void RequirePlan(
        RelationQueryCompiledPlanReference expected,
        RelationQueryCompiledPlanReference actual,
        string stage)
    {
        if (!RelationQueryExplainAffinity.SamePlan(expected, actual))
            throw new ArgumentException($"Explain stage '{stage}' cites a stale or foreign compiled plan.");
    }
}

static class RelationQueryExplainDiagnosticProjector
{
    public static ImmutableArray<RelationQueryExplainDiagnostic> Project(
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        List<RelationQueryExplainDiagnostic> projected = [];
        foreach (var stage in stages)
        {
            switch (stage)
            {
                case RelationQueryStaticCompilationExplainStage value:
                    foreach (var diagnostic in value.Diagnostics)
                        projected.Add(FromDocument(value.WireName, diagnostic));
                    break;
                case RelationQueryProfileFeasibilityExplainStage value:
                    AddRealization(projected, value.WireName, value.Report.Diagnostics);
                    break;
                case RelationQueryBoundRealizationExplainStage value:
                    AddRealization(projected, value.WireName, value.Report.Diagnostics);
                    break;
                case RelationQueryPhysicalPlanningExplainStage value:
                    foreach (var diagnostic in value.Result.Diagnostics)
                        projected.Add(FromPhysical(value.WireName, diagnostic));
                    break;
                case RelationQueryNativeCompilationExplainStage value:
                    foreach (var diagnostic in value.Compilation.Diagnostics)
                        projected.Add(FromNative(value.WireName, diagnostic));
                    break;
                case RelationQueryEvaluationExplainStage value:
                    projected.AddRange(value.Evaluation.Diagnostics);
                    break;
            }
        }

        return RelationQueryExplainOrdering.OrderDiagnostics([.. projected]);
    }

    static RelationQueryExplainDiagnostic FromDocument(string stage, DocumentValidationDiagnostic diagnostic) =>
        new(stage, diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Location);

    static void AddRealization(
        List<RelationQueryExplainDiagnostic> destination,
        string stage,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            destination.Add(new(
                stage,
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message,
                branch: diagnostic.Branch,
                requirement: diagnostic.Requirement,
                node: diagnostic.Node,
                input: diagnostic.Input,
                placementBinding: diagnostic.PlacementBinding,
                capabilityEvidence: diagnostic.CapabilityEvidence,
                operatingBoundary: diagnostic.OperatingBoundary,
                contextEvidence: diagnostic.ContextEvidence,
                semanticSite: diagnostic.SemanticSite,
                compositionRule: diagnostic.CompositionRule,
                @override: diagnostic.Override,
                field: diagnostic.Field,
                bindingSetting: diagnostic.BindingSetting,
                resolution: diagnostic.Resolution,
                configurationOrigin: diagnostic.ConfigurationOrigin,
                configurationAuthority: diagnostic.ConfigurationAuthority,
                adapterDecisionCode: diagnostic.AdapterDecisionCode));
        }
    }

    static RelationQueryExplainDiagnostic FromPhysical(
        string stage,
        RelationQueryPhysicalPlanningDiagnostic diagnostic) =>
        new(
            stage,
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.Message,
            requirement: diagnostic.Requirement,
            input: diagnostic.Input,
            placementBinding: diagnostic.PlacementBinding,
            physicalStage: diagnostic.Stage);

    static RelationQueryExplainDiagnostic FromNative(
        string stage,
        RelationQueryNativeCompilationDiagnostic diagnostic) =>
        new(
            stage,
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.Message,
            branch: diagnostic.Branch,
            requirement: diagnostic.Requirement,
            node: diagnostic.Node,
            input: diagnostic.Input,
            placementBinding: diagnostic.PlacementBinding,
            capabilityEvidence: diagnostic.CapabilityEvidence,
            operatingBoundary: diagnostic.OperatingBoundary,
            contextEvidence: diagnostic.ContextEvidence,
            semanticSite: diagnostic.SemanticSite,
            @override: diagnostic.Override,
            field: diagnostic.Field,
            bindingSetting: diagnostic.BindingSetting,
            resolution: diagnostic.Resolution,
            configurationOrigin: diagnostic.ConfigurationOrigin,
            configurationAuthority: diagnostic.ConfigurationAuthority,
            adapterDecisionCode: diagnostic.AdapterDecisionCode);
}
