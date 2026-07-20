using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Explain;

/// <summary>Canonical completion classification shared only by relation/query explain stages.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryExplainStageStatus
{
    /// <summary>The source phase produced its complete exact artifact.</summary>
    Complete = 0,

    /// <summary>The source phase could not produce an exact realization or plan.</summary>
    Unavailable = 1,

    /// <summary>The source phase rejected stale, malformed, or contradictory inputs.</summary>
    Invalid = 2,

    /// <summary>Runtime evaluation completed with attributable but inconclusive output.</summary>
    Incomplete = 3,

    /// <summary>Runtime evaluation or acquisition failed.</summary>
    Failed = 4
}

/// <summary>Versioned cryptographic identity of one canonical relation/query explain artifact.</summary>
public sealed record RelationQueryExplainFingerprint
{
    /// <summary>Creates an explain fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryExplainFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Portable normalized diagnostic projected from one lifecycle stage.</summary>
public sealed record RelationQueryExplainDiagnostic
{
    /// <summary>Creates a normalized explain diagnostic.</summary>
    /// <param name="stage">Stable explain-stage wire identity.</param>
    /// <param name="code">Stable source diagnostic code.</param>
    /// <param name="severity">Source diagnostic severity.</param>
    /// <param name="message">Human-readable source message excluded from semantic fingerprinting.</param>
    /// <param name="location">Document or semantic location, or <see langword="null"/>.</param>
    /// <param name="branch">Affected result branch, or <see langword="null"/>.</param>
    /// <param name="requirement">Affected realization requirement, or <see langword="null"/>.</param>
    /// <param name="node">Affected logical node, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="output">Affected demanded output, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected source placement, or <see langword="null"/>.</param>
    /// <param name="physicalStage">Affected physical stage, or <see langword="null"/>.</param>
    /// <param name="source">Affected physical source instance, or <see langword="null"/>.</param>
    /// <param name="capabilityEvidence">Affected capability evidence, or <see langword="null"/>.</param>
    /// <param name="operatingBoundary">Affected operating boundary, or <see langword="null"/>.</param>
    /// <param name="contextEvidence">Affected contextual evidence, or <see langword="null"/>.</param>
    /// <param name="semanticSite">Affected semantic site, or <see langword="null"/>.</param>
    /// <param name="compositionRule">Affected composition rule, or <see langword="null"/>.</param>
    /// <param name="override">Affected explicit realization override, or <see langword="null"/>.</param>
    /// <param name="field">Affected semantic field path, or <see langword="null"/>.</param>
    /// <param name="bindingSetting">Affected adapter-binding setting, or <see langword="null"/>.</param>
    /// <param name="resolution">Actionable resolution guidance, or <see langword="null"/>.</param>
    /// <param name="configurationOrigin">Effective configuration-precedence tier, or <see langword="null"/>.</param>
    /// <param name="configurationAuthority">
    /// Declaration, profile, convention, or adapter authority paired with <paramref name="configurationOrigin"/>.
    /// </param>
    /// <param name="adapterDecisionCode">Affected adapter-owned decision code, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stage"/>, <paramref name="code"/>, or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required or optional identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="severity"/> or <paramref name="configurationOrigin"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryExplainDiagnostic(
        string stage,
        string code,
        DiagnosticSeverity severity,
        string message,
        string? location = null,
        RelationQueryNativeResultBranchId? branch = null,
        RelationQueryRealizationRequirementId? requirement = null,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        RelationQueryOutputId? output = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        RelationQueryPhysicalStageId? physicalStage = null,
        RelationQuerySourceInstanceId? source = null,
        RelationQueryTargetCapabilityEvidenceId? capabilityEvidence = null,
        RelationQueryOperatingBoundaryId? operatingBoundary = null,
        RelationQueryContextEvidenceId? contextEvidence = null,
        string? semanticSite = null,
        RelationQueryCompositionRuleId? compositionRule = null,
        RelationQueryRealizationOverrideId? @override = null,
        FieldPath? field = null,
        string? bindingSetting = null,
        string? resolution = null,
        RelationQueryConfigurationValueOrigin? configurationOrigin = null,
        string? configurationAuthority = null,
        RelationQueryAdapterDecisionCode? adapterDecisionCode = null)
    {
        Stage = RelationQueryExplainStageWireNames.Require(stage);
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported explain diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        RequireOptional(location, nameof(location));
        RequireOptional(branch?.Value, nameof(branch));
        RequireOptional(requirement?.Value, nameof(requirement));
        RequireOptional(node?.Value, nameof(node));
        RequireOptional(input?.Value, nameof(input));
        RequireOptional(output?.Value, nameof(output));
        RequireOptional(placementBinding?.Value, nameof(placementBinding));
        RequireOptional(physicalStage?.Value, nameof(physicalStage));
        RequireOptional(source?.Value, nameof(source));
        RequireOptional(capabilityEvidence?.Value, nameof(capabilityEvidence));
        RequireOptional(operatingBoundary?.Value, nameof(operatingBoundary));
        RequireOptional(contextEvidence?.Value, nameof(contextEvidence));
        RequireOptional(semanticSite, nameof(semanticSite));
        RequireOptional(compositionRule?.Value, nameof(compositionRule));
        RequireOptional(@override?.Value, nameof(@override));
        if (field is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An explain diagnostic field path cannot be empty.", nameof(field));
        RequireOptional(bindingSetting, nameof(bindingSetting));
        RequireOptional(resolution, nameof(resolution));
        if (configurationOrigin is { } origin && !Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurationOrigin),
                configurationOrigin,
                "Unsupported explain diagnostic configuration origin.");
        }
        RequireOptional(configurationAuthority, nameof(configurationAuthority));
        if ((configurationOrigin is null) != (configurationAuthority is null))
        {
            throw new ArgumentException(
                "Explain diagnostic configuration origin and authority must be supplied together.",
                nameof(configurationOrigin));
        }
        if (adapterDecisionCode is { } decisionCode && string.IsNullOrWhiteSpace(decisionCode.Value))
            throw new ArgumentException("An explain diagnostic adapter decision code cannot be default.", nameof(adapterDecisionCode));
        Severity = severity;
        Location = location;
        Branch = branch;
        Requirement = requirement;
        Node = node;
        Input = input;
        Output = output;
        PlacementBinding = placementBinding;
        PhysicalStage = physicalStage;
        Source = source;
        CapabilityEvidence = capabilityEvidence;
        OperatingBoundary = operatingBoundary;
        ContextEvidence = contextEvidence;
        SemanticSite = semanticSite;
        CompositionRule = compositionRule;
        Override = @override;
        Field = field;
        BindingSetting = bindingSetting;
        Resolution = resolution;
        ConfigurationOrigin = configurationOrigin;
        ConfigurationAuthority = configurationAuthority;
        AdapterDecisionCode = adapterDecisionCode;
    }

    /// <summary>Stable explain-stage wire identity.</summary>
    public string Stage { get; }

    /// <summary>Stable source diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Source diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable source message excluded from semantic fingerprinting.</summary>
    public string Message { get; }

    /// <summary>Document or semantic location, or <see langword="null"/>.</summary>
    public string? Location { get; }

    /// <summary>Affected result branch, or <see langword="null"/>.</summary>
    public RelationQueryNativeResultBranchId? Branch { get; }

    /// <summary>Affected realization requirement, or <see langword="null"/>.</summary>
    public RelationQueryRealizationRequirementId? Requirement { get; }

    /// <summary>Affected logical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected demanded output, or <see langword="null"/>.</summary>
    public RelationQueryOutputId? Output { get; }

    /// <summary>Affected placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected physical stage, or <see langword="null"/>.</summary>
    public RelationQueryPhysicalStageId? PhysicalStage { get; }

    /// <summary>Affected physical source instance, or <see langword="null"/>.</summary>
    public RelationQuerySourceInstanceId? Source { get; }

    /// <summary>Affected capability evidence, or <see langword="null"/>.</summary>
    public RelationQueryTargetCapabilityEvidenceId? CapabilityEvidence { get; }

    /// <summary>Affected operating boundary, or <see langword="null"/>.</summary>
    public RelationQueryOperatingBoundaryId? OperatingBoundary { get; }

    /// <summary>Affected contextual evidence, or <see langword="null"/>.</summary>
    public RelationQueryContextEvidenceId? ContextEvidence { get; }

    /// <summary>Affected semantic site, or <see langword="null"/>.</summary>
    public string? SemanticSite { get; }

    /// <summary>Affected composition rule, or <see langword="null"/>.</summary>
    public RelationQueryCompositionRuleId? CompositionRule { get; }

    /// <summary>Affected explicit realization override, or <see langword="null"/>.</summary>
    public RelationQueryRealizationOverrideId? Override { get; }

    /// <summary>Affected semantic field path, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected adapter-binding setting, or <see langword="null"/>.</summary>
    public string? BindingSetting { get; }

    /// <summary>Actionable resolution guidance, or <see langword="null"/>.</summary>
    public string? Resolution { get; }

    /// <summary>Effective configuration-precedence tier, or <see langword="null"/>.</summary>
    public RelationQueryConfigurationValueOrigin? ConfigurationOrigin { get; }

    /// <summary>Configuration authority paired with <see cref="ConfigurationOrigin"/>, or <see langword="null"/>.</summary>
    public string? ConfigurationAuthority { get; }

    /// <summary>Affected adapter-owned decision code, or <see langword="null"/>.</summary>
    public RelationQueryAdapterDecisionCode? AdapterDecisionCode { get; }

    static void RequireOptional(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional explain diagnostic identity cannot be empty.", parameterName);
    }
}

/// <summary>Payload-free semantic category of one compiled requirement-graph input.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryExplainRequirementInputKind
{
    /// <summary>A shaped field value.</summary>
    Field = 0,

    /// <summary>A stable observation identity.</summary>
    ObservationIdentity = 1,

    /// <summary>A complete semantic source set.</summary>
    SourceSet = 2,

    /// <summary>A declared semantic relationship.</summary>
    Relationship = 3,

    /// <summary>An invocation parameter, with its declaration and default omitted.</summary>
    Parameter = 4,

    /// <summary>An expression or ambient evaluator capability.</summary>
    Capability = 5
}

/// <summary>Payload-free identity and category of one compiled requirement input.</summary>
public sealed record RelationQueryExplainRequirementInput
{
    /// <summary>Creates a sanitized requirement-input reference.</summary>
    /// <param name="id">Stable compiled input identity.</param>
    /// <param name="kind">Payload-free semantic input category.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryExplainRequirementInput(
        RelationQueryInputId id,
        RelationQueryExplainRequirementInputKind kind)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A sanitized requirement input requires an identity.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported sanitized requirement-input kind.");
        Id = id;
        Kind = kind;
    }

    /// <summary>Stable compiled input identity.</summary>
    public RelationQueryInputId Id { get; }

    /// <summary>Payload-free semantic input category.</summary>
    public RelationQueryExplainRequirementInputKind Kind { get; }
}

/// <summary>Sanitized input-to-output requirement edge without parameter defaults or source payloads.</summary>
public sealed record RelationQueryExplainRequirementEdge
{
    /// <summary>Creates a sanitized requirement edge.</summary>
    /// <param name="input">Required compiled input identity.</param>
    /// <param name="output">Affected demanded output identity.</param>
    /// <param name="effect">Semantic effect through which the input affects the output.</param>
    /// <param name="requirement">Whether acquisition of the input is required or optional.</param>
    /// <exception cref="ArgumentException"><paramref name="input"/> or <paramref name="output"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="effect"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryExplainRequirementEdge(
        RelationQueryInputId input,
        RelationQueryOutputId output,
        RelationQueryRequirementEffect effect,
        QueryInputRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A sanitized requirement edge requires an input identity.", nameof(input));
        if (string.IsNullOrWhiteSpace(output.Value))
            throw new ArgumentException("A sanitized requirement edge requires an output identity.", nameof(output));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unsupported requirement effect.");
        if (!Enum.IsDefined(requirement))
            throw new ArgumentOutOfRangeException(nameof(requirement), requirement, "Unsupported input requirement.");
        Input = input;
        Output = output;
        Effect = effect;
        Requirement = requirement;
    }

    /// <summary>Required compiled input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Affected demanded output identity.</summary>
    public RelationQueryOutputId Output { get; }

    /// <summary>Semantic effect through which the input affects the output.</summary>
    public RelationQueryRequirementEffect Effect { get; }

    /// <summary>Whether acquisition of the input is required or optional.</summary>
    public QueryInputRequirement Requirement { get; }
}

/// <summary>
/// Sanitized canonical requirement graph retaining attribution topology while excluding input declarations and values.
/// </summary>
public sealed record RelationQueryExplainRequirementGraph
{
    /// <summary>Creates and validates a sanitized requirement graph.</summary>
    /// <param name="inputs">Payload-free compiled input references.</param>
    /// <param name="outputs">Demanded output references.</param>
    /// <param name="edges">Sanitized input-to-output edges.</param>
    /// <exception cref="ArgumentException">
    /// A collection is empty, contains null, repeats an identity or edge, cites an unknown identity, or leaves an
    /// input orphaned.
    /// </exception>
    [JsonConstructor]
    public RelationQueryExplainRequirementGraph(
        ImmutableArray<RelationQueryExplainRequirementInput> inputs,
        ImmutableArray<RelationQueryOutputReference> outputs,
        ImmutableArray<RelationQueryExplainRequirementEdge> edges)
    {
        var normalizedInputs = inputs.IsDefault ? [] : inputs;
        var normalizedOutputs = outputs.IsDefault ? [] : outputs;
        var normalizedEdges = edges.IsDefault ? [] : edges;
        if (normalizedInputs.IsDefaultOrEmpty
            || normalizedOutputs.IsDefaultOrEmpty
            || normalizedInputs.Any(static input => input is null)
            || normalizedOutputs.Any(static output => output is null)
            || normalizedEdges.Any(static edge => edge is null))
        {
            throw new ArgumentException("A sanitized requirement graph requires non-null inputs and outputs.");
        }
        if (normalizedInputs.GroupBy(static input => input.Id).Any(static group => group.Count() > 1)
            || normalizedOutputs.GroupBy(static output => output.Id).Any(static group => group.Count() > 1)
            || normalizedEdges.GroupBy(static edge => (edge.Input, edge.Output, edge.Effect))
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Sanitized requirement graph identities and edges must be distinct.");
        }
        var inputIds = normalizedInputs.Select(static input => input.Id).ToHashSet();
        var outputIds = normalizedOutputs.Select(static output => output.Id).ToHashSet();
        if (normalizedEdges.Any(edge => !inputIds.Contains(edge.Input) || !outputIds.Contains(edge.Output)))
            throw new ArgumentException("Every sanitized requirement edge must resolve to retained input and output identities.");
        var referencedInputs = normalizedEdges.Select(static edge => edge.Input).ToHashSet();
        if (normalizedInputs.Any(input => !referencedInputs.Contains(input.Id)))
            throw new ArgumentException("A sanitized requirement graph cannot contain orphan inputs.", nameof(inputs));

        Inputs = [.. normalizedInputs.OrderBy(static input => input.Id.Value, StringComparer.Ordinal)];
        Outputs = [.. normalizedOutputs.OrderBy(static output => output.Id.Value, StringComparer.Ordinal)];
        Edges =
        [
            .. normalizedEdges
                .OrderBy(static edge => edge.Input.Value, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Output.Value, StringComparer.Ordinal)
                .ThenBy(static edge => (int)edge.Effect)
                .ThenBy(static edge => (int)edge.Requirement)
        ];
    }

    /// <summary>Payload-free compiled input references in stable identity order.</summary>
    public ImmutableArray<RelationQueryExplainRequirementInput> Inputs { get; }

    /// <summary>Demanded output references in stable identity order.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Sanitized input-to-output edges in deterministic order.</summary>
    public ImmutableArray<RelationQueryExplainRequirementEdge> Edges { get; }

    /// <summary>Projects a canonical requirement graph across the explain privacy boundary.</summary>
    /// <param name="graph">Canonical compiled requirement graph to sanitize.</param>
    /// <returns>A graph retaining identities, categories, outputs, effects, and requiredness without input payloads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The graph contains an unsupported input variant.</exception>
    public static RelationQueryExplainRequirementGraph From(RelationQueryRequirementGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new(
            [.. graph.Inputs.Select(static input => new RelationQueryExplainRequirementInput(input.Id, GetKind(input)))],
            graph.Outputs,
            [.. graph.Edges.Select(static edge => new RelationQueryExplainRequirementEdge(
                edge.Input.Id,
                edge.Output.Id,
                edge.Effect,
                edge.Requirement))]);
    }

    static RelationQueryExplainRequirementInputKind GetKind(RelationQueryRequirementInput input) =>
        input switch
        {
            RelationQueryFieldInput => RelationQueryExplainRequirementInputKind.Field,
            RelationQueryObservationIdentityInput => RelationQueryExplainRequirementInputKind.ObservationIdentity,
            RelationQuerySourceSetInput => RelationQueryExplainRequirementInputKind.SourceSet,
            RelationQueryRelationshipInput => RelationQueryExplainRequirementInputKind.Relationship,
            RelationQueryParameterInput => RelationQueryExplainRequirementInputKind.Parameter,
            RelationQueryCapabilityInput => RelationQueryExplainRequirementInputKind.Capability,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unsupported requirement-input variant.")
        };
}

/// <summary>Exact portable subset of a successful target-independent static plan used by explainability.</summary>
public sealed record RelationQueryStaticPlanExplanation
{
    /// <summary>Creates and validates a portable static-plan explanation.</summary>
    /// <param name="reference">Exact compiled-plan reference.</param>
    /// <param name="logicalPlan">Demand-scoped retained logical topology.</param>
    /// <param name="requirementGraph">Canonical input-to-output requirement graph.</param>
    /// <param name="branches">Demanded terminal branches.</param>
    /// <param name="observability">Result-observability contract used to project realization requirements.</param>
    /// <param name="realizationRequirements">Exact static realization requirements and guarantees.</param>
    /// <param name="realizationRequirementsFingerprint">
    /// Persisted requirement-set fingerprint to verify, or <see langword="null"/> to compute it.
    /// </param>
    /// <exception cref="ArgumentNullException">A required object is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Inputs, branches, nodes, outputs, realization requirements, guarantees, or their fingerprint conflict.
    /// </exception>
    [JsonConstructor]
    public RelationQueryStaticPlanExplanation(
        RelationQueryCompiledPlanReference reference,
        RelationQueryLogicalPlan logicalPlan,
        RelationQueryExplainRequirementGraph requirementGraph,
        ImmutableArray<RelationQueryNativeResultBranch> branches,
        RelationQueryResultObservability observability,
        ImmutableArray<RelationQueryRealizationRequirement> realizationRequirements,
        RelationQueryPlanComponentFingerprint? realizationRequirementsFingerprint = null)
    {
        Reference = Guard.RequireNotNull(reference);
        LogicalPlan = Guard.RequireNotNull(logicalPlan);
        RequirementGraph = Guard.RequireNotNull(requirementGraph);
        var normalized = branches.IsDefault ? [] : branches;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static branch => branch is null))
            throw new ArgumentException("A static plan explanation requires non-null result branches.", nameof(branches));
        if (normalized.GroupBy(static branch => branch.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Static plan branches cannot repeat an identity.", nameof(branches));
        Branches = [.. normalized.OrderBy(static branch => branch.Id.Value, StringComparer.Ordinal)];

        var graphInputs = RequirementGraph.Inputs.Select(static input => input.Id).OrderBy(static input => input.Value, StringComparer.Ordinal);
        if (!Reference.Inputs.SequenceEqual(graphInputs))
            throw new ArgumentException("The plan reference inputs do not match the requirement graph.", nameof(requirementGraph));
        var retained = LogicalPlan.RetainedNodes.ToHashSet();
        if (Branches.Any(branch => !retained.Contains(branch.Node)))
            throw new ArgumentException("Every result branch must be produced by a retained logical node.", nameof(branches));
        var outputs = RequirementGraph.Outputs.Select(static output => output.Id).ToHashSet();
        if (Branches.SelectMany(static branch => branch.Outputs).Any(output => !outputs.Contains(output.Id)))
            throw new ArgumentException("Every branch output must belong to the requirement graph.", nameof(branches));
        var branchOutputs = Branches.SelectMany(static branch => branch.Outputs).Select(static output => output.Id).ToHashSet();
        if (!branchOutputs.SetEquals(outputs))
            throw new ArgumentException("Result branches must cover every demanded requirement-graph output.", nameof(branches));

        Observability = observability;
        var requirements = realizationRequirements.IsDefault ? [] : realizationRequirements;
        if (requirements.IsDefaultOrEmpty || requirements.Any(static requirement => requirement is null))
            throw new ArgumentException("A static plan requires non-null realization requirements.", nameof(realizationRequirements));
        if (requirements.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Static realization requirements cannot repeat an identity.", nameof(realizationRequirements));
        RealizationRequirements =
        [
            .. requirements.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
        ];
        ValidateRequirements(RealizationRequirements, retained, Reference.Inputs.ToHashSet(), outputs);
        var computedRequirementsFingerprint = RelationQueryRealizationRequirementSetFingerprinter.Compute(
            Reference,
            Observability,
            RealizationRequirements);
        if (realizationRequirementsFingerprint is not null
            && !Equals(realizationRequirementsFingerprint, computedRequirementsFingerprint))
        {
            throw new ArgumentException(
                "The realization-requirement fingerprint does not match normalized static requirements.",
                nameof(realizationRequirementsFingerprint));
        }
        RealizationRequirementsFingerprint = computedRequirementsFingerprint;
    }

    /// <summary>Exact compiled-plan reference.</summary>
    public RelationQueryCompiledPlanReference Reference { get; }

    /// <summary>Demand-scoped retained logical topology.</summary>
    public RelationQueryLogicalPlan LogicalPlan { get; }

    /// <summary>Canonical input-to-output requirement graph.</summary>
    public RelationQueryExplainRequirementGraph RequirementGraph { get; }

    /// <summary>Demanded terminal branches in stable identity order.</summary>
    public ImmutableArray<RelationQueryNativeResultBranch> Branches { get; }

    /// <summary>Result-observability contract used to project realization requirements.</summary>
    public RelationQueryResultObservability Observability { get; }

    /// <summary>Exact static realization requirements and guarantees in stable identity order.</summary>
    public ImmutableArray<RelationQueryRealizationRequirement> RealizationRequirements { get; }

    /// <summary>Plan-affine fingerprint of <see cref="RealizationRequirements"/> and <see cref="Observability"/>.</summary>
    public RelationQueryPlanComponentFingerprint RealizationRequirementsFingerprint { get; }

    static void ValidateRequirements(
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        IReadOnlySet<QueryNodeId> nodes,
        IReadOnlySet<RelationQueryInputId> inputs,
        IReadOnlySet<RelationQueryOutputId> outputs)
    {
        foreach (var requirement in requirements)
        {
            if (requirement.Origin?.Input is { } input && !inputs.Contains(input))
                throw new ArgumentException("A realization requirement cites an unknown compiled input.", nameof(requirements));
            if (requirement.Origin?.Node is { } node && !nodes.Contains(node))
                throw new ArgumentException("A realization requirement cites an unknown retained node.", nameof(requirements));
            foreach (var use in requirement.Uses)
            {
                if (!outputs.Contains(use.Output.Id))
                    throw new ArgumentException("A realization requirement cites an unknown demanded output.", nameof(requirements));
                if (!nodes.Contains(use.Output.Node)
                    || use.Traces.SelectMany(static trace => trace.Steps).Any(step => !nodes.Contains(step.Node)))
                {
                    throw new ArgumentException("A realization requirement trace cites an unknown retained node.", nameof(requirements));
                }
            }
        }

        var guarantees = requirements
            .Select(static requirement => requirement.Capability)
            .OfType<GuaranteeRelationQueryCapability>()
            .Select(static capability => capability.Kind)
            .ToHashSet();
        if (requirements.SelectMany(static requirement => requirement.RequiredGuarantees)
            .Any(guarantee => !guarantees.Contains(guarantee)))
        {
            throw new ArgumentException(
                "Every required guarantee must have a retained plan-level guarantee requirement.",
                nameof(requirements));
        }
    }
}

/// <summary>Target-neutral fingerprint reference for one backend-native artifact.</summary>
public sealed record RelationQueryNativeArtifactFingerprintReference
{
    /// <summary>Creates a native artifact fingerprint reference.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryNativeArtifactFingerprintReference(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal value.</summary>
    public string Value { get; }
}

/// <summary>Target-neutral reference to one backend-native compiled branch artifact.</summary>
public sealed record RelationQueryNativeArtifactReference
{
    /// <summary>Creates a target-neutral native artifact reference.</summary>
    /// <param name="branch">Compiled branch identity.</param>
    /// <param name="artifactSchemaVersion">Backend artifact schema version, or <see langword="null"/>.</param>
    /// <param name="fingerprint">Backend artifact fingerprint.</param>
    /// <param name="provenance">Exact target-neutral native provenance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fingerprint"/> or <paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty or branch attribution conflicts.</exception>
    [JsonConstructor]
    public RelationQueryNativeArtifactReference(
        RelationQueryNativeResultBranchId branch,
        string? artifactSchemaVersion,
        RelationQueryNativeArtifactFingerprintReference fingerprint,
        RelationQueryNativeCompilationProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("A native artifact reference requires a branch identity.", nameof(branch));
        if (artifactSchemaVersion is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactSchemaVersion);
        Fingerprint = Guard.RequireNotNull(fingerprint);
        Provenance = Guard.RequireNotNull(provenance);
        if (branch != Provenance.Branch)
            throw new ArgumentException("Native artifact branch and provenance must agree.", nameof(provenance));
        Branch = branch;
        ArtifactSchemaVersion = artifactSchemaVersion;
    }

    /// <summary>Compiled branch identity.</summary>
    public RelationQueryNativeResultBranchId Branch { get; }

    /// <summary>Backend artifact schema version, or <see langword="null"/>.</summary>
    public string? ArtifactSchemaVersion { get; }

    /// <summary>Backend artifact fingerprint.</summary>
    public RelationQueryNativeArtifactFingerprintReference Fingerprint { get; }

    /// <summary>Exact target-neutral native provenance.</summary>
    public RelationQueryNativeCompilationProvenance Provenance { get; }
}

/// <summary>Sanitized exact attribution to one attempted backend-native compilation.</summary>
public sealed record RelationQueryNativeCompilationAttemptReference
{
    /// <summary>Creates exact target-neutral native-compilation attempt attribution.</summary>
    /// <param name="plan">Exact compiled plan supplied to native lowering.</param>
    /// <param name="profileFeasibility">Exact profile-feasibility fingerprint authorizing the attempt.</param>
    /// <param name="boundRealization">Exact contextual bound-realization fingerprint authorizing the attempt.</param>
    /// <param name="placement">Exact source-placement fingerprint supplied to native lowering.</param>
    /// <param name="adapterBinding">Exact adapter binding used by native lowering.</param>
    /// <param name="branches">Selected terminal branches supplied to native lowering.</param>
    /// <exception cref="ArgumentNullException">A required attribution object is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Branch attribution is empty, invalid, or repeated, or adapter-binding plan or placement affinity conflicts.
    /// </exception>
    [JsonConstructor]
    public RelationQueryNativeCompilationAttemptReference(
        RelationQueryCompiledPlanReference plan,
        RelationQueryRealizationFingerprint profileFeasibility,
        RelationQueryBoundRealizationFingerprint boundRealization,
        RelationQuerySourcePlacementFingerprint placement,
        RelationQueryAdapterBindingReference adapterBinding,
        ImmutableArray<RelationQueryNativeResultBranchId> branches)
    {
        Plan = Guard.RequireNotNull(plan);
        ProfileFeasibility = Guard.RequireNotNull(profileFeasibility);
        BoundRealization = Guard.RequireNotNull(boundRealization);
        Placement = Guard.RequireNotNull(placement);
        AdapterBinding = Guard.RequireNotNull(adapterBinding);
        var normalizedBranches = branches.IsDefault ? [] : branches;
        if (normalizedBranches.IsDefaultOrEmpty
            || normalizedBranches.Any(static branch => string.IsNullOrWhiteSpace(branch.Value))
            || normalizedBranches.Distinct().Count() != normalizedBranches.Length)
        {
            throw new ArgumentException(
                "A native-compilation attempt requires distinct non-default branches.",
                nameof(branches));
        }
        if (!Equals(
                AdapterBinding.CompiledPlanFingerprint,
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(Plan)))
        {
            throw new ArgumentException(
                "Native-compilation attempt binding does not identify the supplied compiled plan.",
                nameof(adapterBinding));
        }
        if (!Equals(AdapterBinding.PlacementFingerprint, Placement))
        {
            throw new ArgumentException(
                "Native-compilation attempt binding does not identify the supplied source placement.",
                nameof(adapterBinding));
        }
        Branches = [.. normalizedBranches.OrderBy(static branch => branch.Value, StringComparer.Ordinal)];
    }

    /// <summary>Exact compiled plan supplied to native lowering.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact profile-feasibility fingerprint authorizing the attempt.</summary>
    public RelationQueryRealizationFingerprint ProfileFeasibility { get; }

    /// <summary>Exact contextual bound-realization fingerprint authorizing the attempt.</summary>
    public RelationQueryBoundRealizationFingerprint BoundRealization { get; }

    /// <summary>Exact source-placement fingerprint supplied to native lowering.</summary>
    public RelationQuerySourcePlacementFingerprint Placement { get; }

    /// <summary>Exact adapter binding used by native lowering.</summary>
    public RelationQueryAdapterBindingReference AdapterBinding { get; }

    /// <summary>Selected terminal branches supplied to native lowering in stable identity order.</summary>
    public ImmutableArray<RelationQueryNativeResultBranchId> Branches { get; }

    /// <summary>Creates exact attempt attribution from a target-neutral native-compilation request.</summary>
    /// <param name="request">Native-compilation request whose immutable attribution is projected.</param>
    /// <returns>A sanitized attempt reference suitable for successful or failed explain stages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static RelationQueryNativeCompilationAttemptReference From(RelationQueryNativeCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(
            request.PlanReference,
            request.ProfileFeasibility.Fingerprint,
            request.BoundRealization.Fingerprint,
            request.Placement.Fingerprint,
            request.BoundRealization.Evidence.Binding,
            [.. request.Branches.Select(static branch => branch.Id)]);
    }
}

/// <summary>Adapter-neutral outcome projected from one backend-native compiler.</summary>
public sealed record RelationQueryNativeCompilationExplanation
{
    /// <summary>Creates a normalized native-compilation explanation.</summary>
    /// <param name="status">Native compilation status.</param>
    /// <param name="artifacts">Successfully compiled artifact references.</param>
    /// <param name="diagnostics">Target-neutral native diagnostics.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Status, artifacts, or diagnostics conflict.</exception>
    [JsonConstructor]
    public RelationQueryNativeCompilationExplanation(
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<RelationQueryNativeArtifactReference> artifacts,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported native compilation status.");
        var normalizedArtifacts = artifacts.IsDefault ? [] : artifacts;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedArtifacts.Any(static artifact => artifact is null)
            || normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Native explanation collections cannot contain null entries.");
        if (normalizedArtifacts.GroupBy(static artifact => artifact.Branch).Any(static group => group.Count() > 1))
            throw new ArgumentException("Native artifact references cannot repeat a branch.", nameof(artifacts));
        var hasErrors = normalizedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (status == RelationQueryNativeCompilationStatus.Exact && (normalizedArtifacts.IsDefaultOrEmpty || hasErrors))
            throw new ArgumentException("Exact native compilation requires artifacts and no errors.", nameof(status));
        if (status != RelationQueryNativeCompilationStatus.Exact && !hasErrors)
            throw new ArgumentException("Unsuccessful native compilation requires an error diagnostic.", nameof(status));
        Status = status;
        Artifacts = [.. normalizedArtifacts.OrderBy(static artifact => artifact.Branch.Value, StringComparer.Ordinal)];
        Diagnostics = RelationQueryExplainOrdering.OrderNativeDiagnostics(normalizedDiagnostics);
    }

    /// <summary>Native compilation status.</summary>
    public RelationQueryNativeCompilationStatus Status { get; }

    /// <summary>Successfully compiled artifact references in branch order.</summary>
    public ImmutableArray<RelationQueryNativeArtifactReference> Artifacts { get; }

    /// <summary>Target-neutral native diagnostics in deterministic order.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics { get; }
}

/// <summary>Sanitized runtime requirement-gap summary with no observation identities or values.</summary>
public sealed record RelationQueryExplainRequirementGapSummary
{
    /// <summary>Creates a grouped requirement-gap summary.</summary>
    /// <param name="cause">Portable causal classification.</param>
    /// <param name="input">Causal compiled input.</param>
    /// <param name="affectedOutputs">Affected demanded outputs.</param>
    /// <param name="occurrenceCount">Number of runtime gaps represented by the group.</param>
    /// <param name="suggestedResolutions">Portable resolution suggestions.</param>
    /// <exception cref="ArgumentException">An identity is default or repeated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum or count is invalid.</exception>
    [JsonConstructor]
    public RelationQueryExplainRequirementGapSummary(
        RelationRequirementGapCause cause,
        RelationQueryInputId input,
        ImmutableArray<RelationQueryOutputId> affectedOutputs,
        int occurrenceCount,
        ImmutableArray<RelationRequirementGapResolutionKind> suggestedResolutions)
    {
        if (!Enum.IsDefined(cause))
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported requirement-gap cause.");
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A requirement-gap summary requires an input identity.", nameof(input));
        if (occurrenceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(occurrenceCount), occurrenceCount, "Occurrence count must be positive.");
        var outputs = affectedOutputs.IsDefault ? [] : affectedOutputs;
        if (outputs.IsDefaultOrEmpty
            || outputs.Any(static output => string.IsNullOrWhiteSpace(output.Value))
            || outputs.Distinct().Count() != outputs.Length)
        {
            throw new ArgumentException(
                "Affected outputs must contain at least one distinct non-default identity.",
                nameof(affectedOutputs));
        }
        var resolutions = suggestedResolutions.IsDefault ? [] : suggestedResolutions;
        if (resolutions.Any(static resolution => !Enum.IsDefined(resolution)))
            throw new ArgumentOutOfRangeException(nameof(suggestedResolutions), "A suggested resolution is unsupported.");
        Cause = cause;
        Input = input;
        AffectedOutputs = [.. outputs.OrderBy(static output => output.Value, StringComparer.Ordinal)];
        OccurrenceCount = occurrenceCount;
        SuggestedResolutions = [.. resolutions.Distinct().Order()];
    }

    /// <summary>Portable causal classification.</summary>
    public RelationRequirementGapCause Cause { get; }

    /// <summary>Causal compiled input.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Affected demanded outputs in stable identity order.</summary>
    public ImmutableArray<RelationQueryOutputId> AffectedOutputs { get; }

    /// <summary>Number of runtime gaps represented by the group.</summary>
    public int OccurrenceCount { get; }

    /// <summary>Portable resolution suggestions in enum order.</summary>
    public ImmutableArray<RelationRequirementGapResolutionKind> SuggestedResolutions { get; }
}

/// <summary>Sanitized row-count summary for one demanded terminal branch.</summary>
public sealed record RelationQueryExplainResultSummary
{
    /// <summary>Creates a result-branch summary.</summary>
    /// <param name="branch">Canonical result branch.</param>
    /// <param name="kind">Rows or aggregation result kind.</param>
    /// <param name="shape">Graph-qualified result shape.</param>
    /// <param name="state">Runtime output disposition.</param>
    /// <param name="rowCount">Number of returned rows.</param>
    /// <exception cref="ArgumentException">A branch or shape identity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum or count is invalid.</exception>
    [JsonConstructor]
    public RelationQueryExplainResultSummary(
        RelationQueryNativeResultBranchId branch,
        RelationQueryExecutionResultKind kind,
        QualifiedShapeId shape,
        RelationQueryExecutionOutputState state,
        int rowCount)
    {
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("A result summary requires a branch identity.", nameof(branch));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported result kind.");
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A result summary requires a graph-qualified shape.", nameof(shape));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported result output state.");
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count cannot be negative.");
        Branch = branch;
        Kind = kind;
        Shape = shape;
        State = state;
        RowCount = rowCount;
    }

    /// <summary>Canonical result branch.</summary>
    public RelationQueryNativeResultBranchId Branch { get; }

    /// <summary>Rows or aggregation result kind.</summary>
    public RelationQueryExecutionResultKind Kind { get; }

    /// <summary>Graph-qualified result shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Runtime output disposition.</summary>
    public RelationQueryExecutionOutputState State { get; }

    /// <summary>Number of returned rows.</summary>
    public int RowCount { get; }
}

/// <summary>Versioned content identity of one sanitized runtime evaluation observation.</summary>
public sealed record RelationQueryEvaluationObservationFingerprint
{
    /// <summary>Creates an evaluation-observation fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryEvaluationObservationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Sanitized runtime summary with payload and observation identities removed.</summary>
public sealed record RelationQueryEvaluationExplanation
{
    /// <summary>Creates and verifies a sanitized evaluation summary.</summary>
    /// <param name="evaluation">Fingerprint of the canonical evaluation request that produced the observation.</param>
    /// <param name="plan">
    /// Exact semantic plan interpreted by the evaluation, or <see langword="null"/> when evaluation terminated at
    /// failed static compilation.
    /// </param>
    /// <param name="status">Canonical execution status.</param>
    /// <param name="results">Demanded terminal summaries.</param>
    /// <param name="requirementGaps">Grouped sanitized requirement gaps.</param>
    /// <param name="diagnostics">
    /// Sanitized evaluation, acquisition, and interpreter diagnostics with runtime identities and provider evidence
    /// references removed.
    /// </param>
    /// <param name="observationFingerprint">
    /// Persisted observation fingerprint to verify, or <see langword="null"/> to compute it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Collections conflict with the execution status.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The observation cannot be materialized as JSON.</exception>
    /// <exception cref="System.Text.Json.JsonException">The observation cannot be serialized as canonical JSON.</exception>
    /// <exception cref="NotSupportedException">The observation contains an unsupported serialization type.</exception>
    [JsonConstructor]
    public RelationQueryEvaluationExplanation(
        RelationQueryEvaluationFingerprint evaluation,
        RelationQueryCompiledPlanReference? plan,
        RelationQueryExecutionStatus status,
        ImmutableArray<RelationQueryExplainResultSummary> results,
        ImmutableArray<RelationQueryExplainRequirementGapSummary> requirementGaps,
        ImmutableArray<RelationQueryExplainDiagnostic> diagnostics = default,
        RelationQueryEvaluationObservationFingerprint? observationFingerprint = null)
    {
        Evaluation = Guard.RequireNotNull(evaluation);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported evaluation status.");
        var normalizedResults = results.IsDefault ? [] : results;
        var normalizedGaps = requirementGaps.IsDefault ? [] : requirementGaps;
        if (normalizedResults.Any(static result => result is null) || normalizedGaps.Any(static gap => gap is null))
            throw new ArgumentException("Evaluation explanation collections cannot contain null entries.");
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Evaluation diagnostics cannot contain null entries.", nameof(diagnostics));
        if (normalizedDiagnostics.Any(static diagnostic =>
                diagnostic.Stage != RelationQueryExplainStageWireNames.Evaluation))
        {
            throw new ArgumentException(
                "Evaluation diagnostics must carry evaluation-stage attribution.",
                nameof(diagnostics));
        }
        if (normalizedResults.GroupBy(static result => result.Branch).Any(static group => group.Count() > 1))
            throw new ArgumentException("Evaluation result summaries cannot repeat a branch.", nameof(results));
        if (status == RelationQueryExecutionStatus.Succeeded
            && (!normalizedGaps.IsDefaultOrEmpty
                || normalizedResults.Any(static result => result.State == RelationQueryExecutionOutputState.Incomplete)))
            throw new ArgumentException("A successful evaluation cannot retain unresolved requirement gaps.", nameof(status));
        if (plan is null
            && (status != RelationQueryExecutionStatus.Failed
                || !normalizedResults.IsDefaultOrEmpty
                || !normalizedGaps.IsDefaultOrEmpty))
        {
            throw new ArgumentException(
                "Evaluation without a static plan must be failed and cannot retain results or requirement gaps.",
                nameof(plan));
        }
        Plan = plan;
        Status = status;
        Results = [.. normalizedResults.OrderBy(static result => result.Branch.Value, StringComparer.Ordinal)];
        RequirementGaps = RelationQueryExplainOrdering.OrderGapSummaries(normalizedGaps);
        Diagnostics = RelationQueryExplainOrdering.OrderDiagnostics(normalizedDiagnostics);
        var computed = RelationQueryEvaluationObservationFingerprinter.Compute(this);
        if (observationFingerprint is not null && !Equals(observationFingerprint, computed))
        {
            throw new ArgumentException(
                "The evaluation-observation fingerprint does not match normalized runtime content.",
                nameof(observationFingerprint));
        }
        ObservationFingerprint = computed;
    }

    /// <summary>Fingerprint of the canonical evaluation request that produced the observation.</summary>
    public RelationQueryEvaluationFingerprint Evaluation { get; }

    /// <summary>Exact semantic plan interpreted by the evaluation.</summary>
    public RelationQueryCompiledPlanReference? Plan { get; }

    /// <summary>Canonical execution status.</summary>
    public RelationQueryExecutionStatus Status { get; }

    /// <summary>Demanded terminal summaries in branch order.</summary>
    public ImmutableArray<RelationQueryExplainResultSummary> Results { get; }

    /// <summary>Grouped sanitized requirement gaps in deterministic order.</summary>
    public ImmutableArray<RelationQueryExplainRequirementGapSummary> RequirementGaps { get; }

    /// <summary>Sanitized runtime diagnostics in deterministic attribution order.</summary>
    public ImmutableArray<RelationQueryExplainDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Integrity fingerprint of this sanitized runtime observation, separate from deterministic explain identity.
    /// </summary>
    public RelationQueryEvaluationObservationFingerprint ObservationFingerprint { get; }
}

/// <summary>Base contract for one explicit canonical explain stage.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryExplainStageWireNames.Discriminator)]
[JsonDerivedType(typeof(RelationQueryStaticCompilationExplainStage), RelationQueryExplainStageWireNames.StaticCompilation)]
[JsonDerivedType(typeof(RelationQueryProfileFeasibilityExplainStage), RelationQueryExplainStageWireNames.ProfileFeasibility)]
[JsonDerivedType(typeof(RelationQuerySourcePlacementExplainStage), RelationQueryExplainStageWireNames.SourcePlacement)]
[JsonDerivedType(typeof(RelationQueryBoundRealizationExplainStage), RelationQueryExplainStageWireNames.BoundRealization)]
[JsonDerivedType(typeof(RelationQueryPhysicalPlanningExplainStage), RelationQueryExplainStageWireNames.PhysicalPlanning)]
[JsonDerivedType(typeof(RelationQueryNativeCompilationExplainStage), RelationQueryExplainStageWireNames.NativeCompilation)]
[JsonDerivedType(typeof(RelationQueryEvaluationExplainStage), RelationQueryExplainStageWireNames.Evaluation)]
public abstract record RelationQueryExplainStage
{
    /// <summary>Initializes one normalized explain lifecycle stage.</summary>
    /// <param name="status">Lifecycle disposition validated by the concrete stage.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    private protected RelationQueryExplainStage(RelationQueryExplainStageStatus status)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported explain stage status.");
        Status = status;
    }

    /// <summary>Normalized lifecycle disposition validated against the retained source artifact.</summary>
    public RelationQueryExplainStageStatus Status { get; }

    internal abstract string WireName { get; }
}

/// <summary>Explain stage for target-independent static compilation.</summary>
public sealed record RelationQueryStaticCompilationExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a static-compilation explain stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="request">Sanitized semantic reference to the static compilation request.</param>
    /// <param name="plan">Successful static plan, or <see langword="null"/>.</param>
    /// <param name="diagnostics">Static compilation diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Status, plan, request, or diagnostics conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryStaticCompilationExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryCompilationRequestReference request,
        RelationQueryStaticPlanExplanation? plan,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(status)
    {
        Request = Guard.RequireNotNull(request);
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Static diagnostics cannot contain null entries.", nameof(diagnostics));
        var hasErrors = normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if ((status == RelationQueryExplainStageStatus.Complete) != (plan is not null && !hasErrors))
            throw new ArgumentException("Static explain status must agree with plan availability and diagnostics.", nameof(status));
        if (status != RelationQueryExplainStageStatus.Complete && status != RelationQueryExplainStageStatus.Invalid)
            throw new ArgumentException("Static compilation can be complete or invalid.", nameof(status));
        if (plan is not null && !Equals(Request, RelationQueryCompilationRequestReference.From(plan.Reference)))
            throw new ArgumentException("Static request attribution does not belong to the retained plan.", nameof(request));
        Plan = plan;
        Diagnostics = [.. normalized.OrderBy(static diagnostic => diagnostic.Location ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => (int)diagnostic.Severity)];
    }

    /// <summary>Sanitized semantic reference to the static compilation request.</summary>
    public RelationQueryCompilationRequestReference Request { get; }

    /// <summary>Successful static plan, or <see langword="null"/>.</summary>
    public RelationQueryStaticPlanExplanation? Plan { get; }

    /// <summary>Static compilation diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.StaticCompilation;
}

/// <summary>Explain stage for target-profile feasibility.</summary>
public sealed record RelationQueryProfileFeasibilityExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a profile-feasibility stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="report">Exact profile realization report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> conflicts with <paramref name="report"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryProfileFeasibilityExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryRealizationReport report)
        : base(status)
    {
        Report = Guard.RequireNotNull(report);
        RelationQueryExplainStageStatus expected = RelationQueryExplainStatus.FromRealization(report.Status);
        if (status != expected)
            throw new ArgumentException("Profile explain status conflicts with the realization report.", nameof(status));
    }

    /// <summary>Exact profile realization report.</summary>
    public RelationQueryRealizationReport Report { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.ProfileFeasibility;
}

/// <summary>Explain stage for exact source placement.</summary>
public sealed record RelationQuerySourcePlacementExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a source-placement stage.</summary>
    /// <param name="status">
    /// Normalized completion status. Invalid records an internally valid placement whose plan affinity was rejected
    /// by the enclosing lifecycle.
    /// </param>
    /// <param name="placement">Exact portable source placement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is neither complete nor invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySourcePlacementExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQuerySourcePlacement placement)
        : base(status)
    {
        if (status is not (RelationQueryExplainStageStatus.Complete or RelationQueryExplainStageStatus.Invalid))
        {
            throw new ArgumentException(
                "A retained source placement stage can be complete or invalid.",
                nameof(status));
        }
        Placement = Guard.RequireNotNull(placement);
    }

    /// <summary>Exact portable source placement.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.SourcePlacement;
}

/// <summary>Explain stage for exact contextual bound realization.</summary>
public sealed record RelationQueryBoundRealizationExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a bound-realization stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="report">Exact bound-realization report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> conflicts with <paramref name="report"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryBoundRealizationExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryBoundRealizationReport report)
        : base(status)
    {
        Report = Guard.RequireNotNull(report);
        if (status != RelationQueryExplainStatus.FromRealization(report.Status))
            throw new ArgumentException("Bound explain status conflicts with the bound-realization report.", nameof(status));
    }

    /// <summary>Exact bound-realization report.</summary>
    public RelationQueryBoundRealizationReport Report { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.BoundRealization;
}

/// <summary>Explain stage for deterministic physical planning.</summary>
public sealed record RelationQueryPhysicalPlanningExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a physical-planning stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="plan">Exact semantic plan reference supplied to planning.</param>
    /// <param name="realization">Exact profile-realization fingerprint supplied to planning.</param>
    /// <param name="placement">Exact placement fingerprint supplied to planning.</param>
    /// <param name="policy">Exact policy, or <see langword="null"/> when a retained terminal evaluation did not expose failed planning inputs.</param>
    /// <param name="result">Physical-planning result.</param>
    /// <exception cref="ArgumentNullException">A required object is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Status or affinity conflicts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalPlanningExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryCompiledPlanReference plan,
        RelationQueryRealizationFingerprint realization,
        RelationQuerySourcePlacementFingerprint placement,
        RelationQueryPhysicalPlanningPolicy? policy,
        RelationQueryPhysicalPlanningResult result)
        : base(status)
    {
        Plan = Guard.RequireNotNull(plan);
        Realization = Guard.RequireNotNull(realization);
        Placement = Guard.RequireNotNull(placement);
        Result = Guard.RequireNotNull(result);
        if (status != RelationQueryExplainStatus.FromPhysical(result.Status))
            throw new ArgumentException("Physical explain status conflicts with the planning result.", nameof(status));
        if (result.Plan is { } compiled)
        {
            if (!RelationQueryExplainAffinity.SamePlan(plan, compiled.Plan)
                || !Equals(realization, compiled.Realization)
                || !Equals(placement, compiled.Placement.Fingerprint))
                throw new ArgumentException("Physical plan affinity conflicts with the explain stage.", nameof(result));
            if (policy is not null && !RelationQueryExplainAffinity.SamePolicy(policy, compiled.Policy))
                throw new ArgumentException("Physical planning policy conflicts with the compiled plan.", nameof(policy));
            Policy = compiled.Policy;
        }
        else
        {
            Policy = policy;
        }
    }

    /// <summary>Exact semantic plan reference supplied to planning.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact profile-realization fingerprint supplied to planning.</summary>
    public RelationQueryRealizationFingerprint Realization { get; }

    /// <summary>Exact placement fingerprint supplied to planning.</summary>
    public RelationQuerySourcePlacementFingerprint Placement { get; }

    /// <summary>Exact planning policy when retained by the source boundary.</summary>
    public RelationQueryPhysicalPlanningPolicy? Policy { get; }

    /// <summary>Physical-planning result.</summary>
    public RelationQueryPhysicalPlanningResult Result { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.PhysicalPlanning;
}

/// <summary>Explain stage for backend-native compilation.</summary>
public sealed record RelationQueryNativeCompilationExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a native-compilation stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="attempt">Exact sanitized attribution to the native-compilation attempt.</param>
    /// <param name="compilation">Adapter-neutral native compilation evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="attempt"/> or <paramref name="compilation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> conflicts with <paramref name="compilation"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryNativeCompilationExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryNativeCompilationAttemptReference attempt,
        RelationQueryNativeCompilationExplanation compilation)
        : base(status)
    {
        Attempt = Guard.RequireNotNull(attempt);
        Compilation = Guard.RequireNotNull(compilation);
        if (status != RelationQueryExplainStatus.FromNative(compilation.Status))
            throw new ArgumentException("Native explain status conflicts with the compilation evidence.", nameof(status));
    }

    /// <summary>Exact sanitized attribution to the native-compilation attempt.</summary>
    public RelationQueryNativeCompilationAttemptReference Attempt { get; }

    /// <summary>Adapter-neutral native compilation evidence.</summary>
    public RelationQueryNativeCompilationExplanation Compilation { get; }

    /// <summary>Creates an attributed explain stage from the exact request and its adapter-neutral result.</summary>
    /// <param name="request">Native-compilation request that was attempted.</param>
    /// <param name="compilation">Adapter-neutral result of the attempt.</param>
    /// <returns>An attributed native-compilation explain stage.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The result status or retained attribution is inconsistent.</exception>
    public static RelationQueryNativeCompilationExplainStage Create(
        RelationQueryNativeCompilationRequest request,
        RelationQueryNativeCompilationExplanation compilation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(compilation);
        return new(
            RelationQueryExplainStatus.FromNative(compilation.Status),
            RelationQueryNativeCompilationAttemptReference.From(request),
            compilation);
    }

    internal override string WireName => RelationQueryExplainStageWireNames.NativeCompilation;
}

/// <summary>Explain stage for completed or failed runtime evaluation.</summary>
public sealed record RelationQueryEvaluationExplainStage : RelationQueryExplainStage
{
    /// <summary>Creates a runtime-evaluation stage.</summary>
    /// <param name="status">Normalized completion status.</param>
    /// <param name="evaluation">Sanitized runtime evaluation observation with its own integrity identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> conflicts with <paramref name="evaluation"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryEvaluationExplainStage(
        RelationQueryExplainStageStatus status,
        RelationQueryEvaluationExplanation evaluation)
        : base(status)
    {
        Evaluation = Guard.RequireNotNull(evaluation);
        if (status != RelationQueryExplainStatus.FromExecution(evaluation.Status))
            throw new ArgumentException("Evaluation explain status conflicts with the execution summary.", nameof(status));
    }

    /// <summary>Sanitized runtime evaluation observation with its own integrity identity.</summary>
    public RelationQueryEvaluationExplanation Evaluation { get; }

    internal override string WireName => RelationQueryExplainStageWireNames.Evaluation;
}

/// <summary>
/// Portable explanation spanning every available lifecycle stage with deterministic compilation identity and
/// separately fingerprinted runtime evaluation observations.
/// </summary>
public sealed class RelationQueryExplainArtifact
{
    /// <summary>Current portable explain schema version.</summary>
    public const string CurrentSchemaVersion = "relation-query-explain/v1";

    /// <summary>Creates and verifies a canonical explain artifact.</summary>
    /// <param name="schemaVersion">Portable explain schema version.</param>
    /// <param name="stages">Available stage projections.</param>
    /// <param name="diagnostics">Persisted normalized diagnostics, or a default array to derive them.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <param name="capabilitySummary">
    /// Persisted capability summary to verify, or <see langword="null"/> to derive the latest available profile or
    /// bound summary. It remains <see langword="null"/> when neither stage is present.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="schemaVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, stages, diagnostics, affinity, or fingerprint conflict.</exception>
    /// <exception cref="InvalidOperationException">Normalized explain content cannot be materialized as JSON.</exception>
    /// <exception cref="System.Text.Json.JsonException">Normalized explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Normalized explain content contains an unsupported serialization type.</exception>
    [JsonConstructor]
    public RelationQueryExplainArtifact(
        string schemaVersion,
        ImmutableArray<RelationQueryExplainStage> stages,
        ImmutableArray<RelationQueryExplainDiagnostic> diagnostics = default,
        RelationQueryExplainFingerprint? fingerprint = null,
        RelationQueryCapabilitySummary? capabilitySummary = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported relation/query explain schema version '{SchemaVersion}'.", nameof(schemaVersion));
        Stages = RelationQueryExplainStageValidator.NormalizeAndValidate(stages);
        var derivedCapabilitySummary = DeriveCapabilitySummary(Stages);
        if (capabilitySummary is not null
            && (derivedCapabilitySummary is null || !derivedCapabilitySummary.HasSameSemantics(capabilitySummary)))
        {
            throw new ArgumentException(
                "Persisted capability summary does not match the latest retained realization stage.",
                nameof(capabilitySummary));
        }
        CapabilitySummary = derivedCapabilitySummary;
        var projectedDiagnostics = RelationQueryExplainDiagnosticProjector.Project(Stages);
        if (!diagnostics.IsDefault && !diagnostics.SequenceEqual(projectedDiagnostics))
            throw new ArgumentException("Persisted explain diagnostics do not match stage artifacts.", nameof(diagnostics));
        Diagnostics = projectedDiagnostics;
        var computed = RelationQueryExplainFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
            throw new ArgumentException("The explain fingerprint does not match normalized semantic content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Portable explain schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Available stages in canonical display order.</summary>
    public ImmutableArray<RelationQueryExplainStage> Stages { get; }

    /// <summary>
    /// Capability index derived from the latest bound or profile-feasibility stage, or <see langword="null"/> when
    /// neither stage is retained.
    /// </summary>
    public RelationQueryCapabilitySummary? CapabilitySummary { get; }

    /// <summary>Normalized diagnostics in stable stage and attribution order.</summary>
    public ImmutableArray<RelationQueryExplainDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Deterministic semantic fingerprint of retained compilation stages, excluding prose and the evaluation stage.
    /// </summary>
    public RelationQueryExplainFingerprint Fingerprint { get; }

    static RelationQueryCapabilitySummary? DeriveCapabilitySummary(
        ImmutableArray<RelationQueryExplainStage> stages)
    {
        var bound = stages.OfType<RelationQueryBoundRealizationExplainStage>().SingleOrDefault();
        if (bound is not null)
            return RelationQueryCapabilitySummaryProjector.Project(bound.Report);
        var profile = stages.OfType<RelationQueryProfileFeasibilityExplainStage>().SingleOrDefault();
        return profile is null
            ? null
            : RelationQueryCapabilitySummaryProjector.Project(profile.Report);
    }
}

/// <summary>Stable wire names for relation/query explain stages.</summary>
public static class RelationQueryExplainStageWireNames
{
    /// <summary>Polymorphic stage discriminator property.</summary>
    public const string Discriminator = "$stage";

    /// <summary>Static compilation stage.</summary>
    public const string StaticCompilation = "staticCompilation";

    /// <summary>Profile feasibility stage.</summary>
    public const string ProfileFeasibility = "profileFeasibility";

    /// <summary>Source placement stage.</summary>
    public const string SourcePlacement = "sourcePlacement";

    /// <summary>Bound realization stage.</summary>
    public const string BoundRealization = "boundRealization";

    /// <summary>Physical planning stage.</summary>
    public const string PhysicalPlanning = "physicalPlanning";

    /// <summary>Native compilation stage.</summary>
    public const string NativeCompilation = "nativeCompilation";

    /// <summary>Runtime evaluation stage.</summary>
    public const string Evaluation = "evaluation";

    /// <summary>Validates one stable stage wire name.</summary>
    /// <param name="value">Stage wire name to validate.</param>
    /// <returns>The validated stage wire name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is unknown.</exception>
    public static string Require(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Rank(value) >= 0
            ? value
            : throw new ArgumentException($"Unknown relation/query explain stage '{value}'.", nameof(value));
    }

    internal static int Rank(string value) => value switch
    {
        StaticCompilation => 0,
        ProfileFeasibility => 1,
        SourcePlacement => 2,
        BoundRealization => 3,
        PhysicalPlanning => 4,
        NativeCompilation => 5,
        Evaluation => 6,
        _ => -1
    };
}

static class RelationQueryExplainStatus
{
    public static RelationQueryExplainStageStatus FromRealization(RelationQueryRealizationStatus status) => status switch
    {
        RelationQueryRealizationStatus.Realizable => RelationQueryExplainStageStatus.Complete,
        RelationQueryRealizationStatus.NotRealizable => RelationQueryExplainStageStatus.Unavailable,
        RelationQueryRealizationStatus.Invalid => RelationQueryExplainStageStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported realization status.")
    };

    public static RelationQueryExplainStageStatus FromPhysical(RelationQueryPhysicalPlanningStatus status) => status switch
    {
        RelationQueryPhysicalPlanningStatus.Planned => RelationQueryExplainStageStatus.Complete,
        RelationQueryPhysicalPlanningStatus.Unavailable => RelationQueryExplainStageStatus.Unavailable,
        RelationQueryPhysicalPlanningStatus.Invalid => RelationQueryExplainStageStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported physical planning status.")
    };

    public static RelationQueryExplainStageStatus FromNative(RelationQueryNativeCompilationStatus status) => status switch
    {
        RelationQueryNativeCompilationStatus.Exact => RelationQueryExplainStageStatus.Complete,
        RelationQueryNativeCompilationStatus.Unsupported => RelationQueryExplainStageStatus.Unavailable,
        RelationQueryNativeCompilationStatus.Invalid => RelationQueryExplainStageStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported native compilation status.")
    };

    public static RelationQueryExplainStageStatus FromExecution(RelationQueryExecutionStatus status) => status switch
    {
        RelationQueryExecutionStatus.Succeeded => RelationQueryExplainStageStatus.Complete,
        RelationQueryExecutionStatus.Incomplete => RelationQueryExplainStageStatus.Incomplete,
        RelationQueryExecutionStatus.Failed => RelationQueryExplainStageStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported execution status.")
    };
}

static class RelationQueryExplainAffinity
{
    public static bool SamePlan(RelationQueryCompiledPlanReference left, RelationQueryCompiledPlanReference right) =>
        Equals(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(left),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(right));

    public static bool SamePolicy(
        RelationQueryPhysicalPlanningPolicy left,
        RelationQueryPhysicalPlanningPolicy right) =>
        left.Id == right.Id
        && string.Equals(left.ConventionSetVersion, right.ConventionSetVersion, StringComparison.Ordinal)
        && left.MaximumBatchSize == right.MaximumBatchSize
        && left.MaximumBufferedRows == right.MaximumBufferedRows
        && left.MaximumLocalRows == right.MaximumLocalRows
        && left.MaximumFanOut == right.MaximumFanOut
        && left.MaximumReferenceKeysPerObservation == right.MaximumReferenceKeysPerObservation
        && left.MaximumConcurrency == right.MaximumConcurrency
        && left.LoweringSelections.SequenceEqual(right.LoweringSelections);
}

static class RelationQueryExplainOrdering
{
    public static ImmutableArray<RelationQueryExplainDiagnostic> OrderDiagnostics(
        ImmutableArray<RelationQueryExplainDiagnostic> diagnostics) =>
    [
        .. diagnostics.OrderBy(static diagnostic => RelationQueryExplainStageWireNames.Rank(diagnostic.Stage))
            .ThenBy(static diagnostic => diagnostic.Location ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => (int)diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Output?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.PhysicalStage?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Source?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.CompositionRule?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Override?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ContextEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.BindingSetting ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ConfigurationOrigin is { } origin ? (int)origin : -1)
            .ThenBy(static diagnostic => diagnostic.ConfigurationAuthority ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.AdapterDecisionCode?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Resolution ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
    ];

    public static ImmutableArray<RelationQueryNativeCompilationDiagnostic> OrderNativeDiagnostics(
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics) =>
    [
        .. diagnostics.OrderBy(static diagnostic => diagnostic.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Override?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ContextEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.BindingSetting ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.ConfigurationOrigin is { } origin ? (int)origin : -1)
            .ThenBy(static diagnostic => diagnostic.ConfigurationAuthority ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.AdapterDecisionCode?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Resolution ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => (int)diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
    ];

    public static ImmutableArray<RelationQueryExplainRequirementGapSummary> OrderGapSummaries(
        ImmutableArray<RelationQueryExplainRequirementGapSummary> gaps) =>
    [
        .. gaps.OrderBy(static gap => gap.Input.Value, StringComparer.Ordinal)
            .ThenBy(static gap => (int)gap.Cause)
            .ThenBy(static gap => string.Join('\u001f', gap.AffectedOutputs.Select(static output => output.Value)), StringComparer.Ordinal)
    ];
}
