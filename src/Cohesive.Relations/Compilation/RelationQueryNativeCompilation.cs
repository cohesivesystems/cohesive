using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Compilation;

/// <summary>Stable identity of one native-compilation result branch within a compiled plan.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryNativeResultBranchId
{
    /// <summary>Creates a native result-branch identity.</summary>
    /// <param name="value">Stable ordinal identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryNativeResultBranchId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable ordinal identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Semantic result kind exposed to a target-native compiler.</summary>
public enum RelationQueryNativeResultKind
{
    /// <summary>Rows emitted by a canonical relation terminal.</summary>
    RelationRows = 0,

    /// <summary>Rows emitted by a named canonical query result.</summary>
    QueryRows = 1,

    /// <summary>Aggregation rows emitted by a named canonical query result.</summary>
    QueryAggregation = 2
}

/// <summary>One demanded terminal branch prepared for target-native compilation.</summary>
public sealed record RelationQueryNativeResultBranch
{
    /// <summary>Creates one normalized demanded terminal branch.</summary>
    /// <param name="id">Stable branch identity.</param>
    /// <param name="kind">Semantic result kind.</param>
    /// <param name="node">Retained node producing the result.</param>
    /// <param name="binding">Binding containing each result value.</param>
    /// <param name="shape">Graph-qualified result shape.</param>
    /// <param name="outputs">One or more demanded outputs represented by this branch.</param>
    /// <param name="fields">Demanded result fields.</param>
    /// <param name="relation">Canonical relation identity for a relation branch.</param>
    /// <param name="queryResult">Canonical query-result identity for a query branch.</param>
    /// <exception cref="ArgumentException">An identity, shape, output, field, or terminal-kind combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryNativeResultBranch(
        RelationQueryNativeResultBranchId id,
        RelationQueryNativeResultKind kind,
        QueryNodeId node,
        ValueBindingId binding,
        QualifiedShapeId shape,
        ImmutableArray<RelationQueryOutputReference> outputs,
        ImmutableArray<RelationQueryFieldReference> fields,
        RelationId? relation = null,
        QueryResultId? queryResult = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported native result kind.");
        if (string.IsNullOrWhiteSpace(node.Value) || string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A native result branch requires non-default node and binding identities.", nameof(node));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A native result branch requires a graph-qualified shape.", nameof(shape));
        if (kind == RelationQueryNativeResultKind.RelationRows != (relation is not null)
            || kind == RelationQueryNativeResultKind.RelationRows == (queryResult is not null))
        {
            throw new ArgumentException("Native result kind conflicts with its canonical terminal identity.", nameof(kind));
        }

        var normalizedOutputs = outputs.IsDefault ? [] : outputs;
        if (normalizedOutputs.IsDefaultOrEmpty || normalizedOutputs.Any(static output => output is null))
            throw new ArgumentException("A native result branch requires one or more demanded outputs.", nameof(outputs));
        var normalizedFields = fields.IsDefault ? [] : fields;
        if (normalizedFields.Any(static field => !RelationQueryContractOrdering.IsValid(field)))
            throw new ArgumentException("Native result fields must be valid graph-qualified field references.", nameof(fields));

        Id = id;
        Kind = kind;
        Node = node;
        Binding = binding;
        Shape = shape;
        Relation = relation;
        QueryResult = queryResult;
        Outputs = [.. normalizedOutputs.OrderBy(static output => output.Id.Value, StringComparer.Ordinal)];
        Fields = RelationQueryContractOrdering.NormalizeFields(normalizedFields);
    }

    /// <summary>Stable branch identity derived from the canonical terminal identity.</summary>
    public RelationQueryNativeResultBranchId Id { get; }

    /// <summary>Semantic result kind.</summary>
    public RelationQueryNativeResultKind Kind { get; }

    /// <summary>Retained logical node producing the result.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Binding containing each emitted result value.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Canonical semantic result shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Canonical relation identity for <see cref="RelationQueryNativeResultKind.RelationRows"/>.</summary>
    public RelationId? Relation { get; }

    /// <summary>Canonical named query-result identity for query branches.</summary>
    public QueryResultId? QueryResult { get; }

    /// <summary>Demanded row and field outputs in stable output-identity order.</summary>
    public ImmutableArray<RelationQueryOutputReference> Outputs { get; }

    /// <summary>Demanded result fields in canonical field order.</summary>
    public ImmutableArray<RelationQueryFieldReference> Fields { get; }
}

/// <summary>Outcome category returned by a target-native compiler.</summary>
public enum RelationQueryNativeCompilationStatus
{
    /// <summary>Every selected branch was compiled with exact declared semantics.</summary>
    Exact = 0,

    /// <summary>Inputs were valid, but at least one selected branch is unsupported or unavailable.</summary>
    Unsupported = 1,

    /// <summary>Stale, inconsistent, or malformed inputs prevent trustworthy compilation.</summary>
    Invalid = 2
}

/// <summary>Structured diagnostic shared by target-native compilers.</summary>
public sealed record RelationQueryNativeCompilationDiagnostic
{
    /// <summary>Creates an attributable native-compilation diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="branch">Affected result branch, or <see langword="null"/>.</param>
    /// <param name="node">Affected logical node, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="requirement">Affected realization requirement, or <see langword="null"/>.</param>
    /// <param name="capabilityEvidence">Affected target capability evidence, or <see langword="null"/>.</param>
    /// <param name="operatingBoundary">Affected operating boundary, or <see langword="null"/>.</param>
    /// <param name="override">Affected explicit realization override, or <see langword="null"/>.</param>
    /// <param name="semanticSite">Affected semantic expression or assignment site, or <see langword="null"/>.</param>
    /// <param name="contextEvidence">Affected contextual adapter evidence, or <see langword="null"/>.</param>
    /// <param name="field">Affected semantic field path, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected source-placement binding, or <see langword="null"/>.</param>
    /// <param name="bindingSetting">Affected adapter-binding setting, or <see langword="null"/>.</param>
    /// <param name="resolution">Actionable resolution guidance, or <see langword="null"/>.</param>
    /// <param name="configurationOrigin">
    /// Configuration-precedence tier that supplied the attributed setting, or <see langword="null"/>.
    /// </param>
    /// <param name="configurationAuthority">
    /// Stable declaration, profile, convention, or adapter authority paired with
    /// <paramref name="configurationOrigin"/>, or <see langword="null"/>.
    /// </param>
    /// <param name="adapterDecisionCode">
    /// Stable adapter-owned decision code explaining a contextual failure, or <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required string, supplied identity, field path, semantic site, binding setting, configuration authority,
    /// or resolution is empty, configuration origin and authority are not supplied together, or
    /// <paramref name="adapterDecisionCode"/> is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="severity"/> or <paramref name="configurationOrigin"/> is unsupported.
    /// </exception>
    public RelationQueryNativeCompilationDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryNativeResultBranchId? branch = null,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        RelationQueryRealizationRequirementId? requirement = null,
        RelationQueryTargetCapabilityEvidenceId? capabilityEvidence = null,
        RelationQueryOperatingBoundaryId? operatingBoundary = null,
        RelationQueryRealizationOverrideId? @override = null,
        string? semanticSite = null,
        RelationQueryContextEvidenceId? contextEvidence = null,
        FieldPath? field = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        string? bindingSetting = null,
        string? resolution = null,
        EffectiveConfigurationOrigin? configurationOrigin = null,
        string? configurationAuthority = null,
        RelationQueryAdapterDecisionCode? adapterDecisionCode = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        if (branch is { } branchId && string.IsNullOrWhiteSpace(branchId.Value))
            throw new ArgumentException("A diagnostic branch identity cannot be empty.", nameof(branch));
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("A diagnostic node identity cannot be empty.", nameof(node));
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("A diagnostic input identity cannot be empty.", nameof(input));
        if (requirement is { } requirementId && string.IsNullOrWhiteSpace(requirementId.Value))
            throw new ArgumentException("A diagnostic requirement identity cannot be empty.", nameof(requirement));
        if (capabilityEvidence is { } capabilityId && string.IsNullOrWhiteSpace(capabilityId.Value))
            throw new ArgumentException("A diagnostic capability-evidence identity cannot be empty.", nameof(capabilityEvidence));
        if (operatingBoundary is { } boundaryId && string.IsNullOrWhiteSpace(boundaryId.Value))
            throw new ArgumentException("A diagnostic operating-boundary identity cannot be empty.", nameof(operatingBoundary));
        if (@override is { } overrideId && string.IsNullOrWhiteSpace(overrideId.Value))
            throw new ArgumentException("A diagnostic override identity cannot be empty.", nameof(@override));
        if (semanticSite is not null && string.IsNullOrWhiteSpace(semanticSite))
            throw new ArgumentException("A diagnostic semantic site cannot be empty.", nameof(semanticSite));
        if (contextEvidence is { } contextId && string.IsNullOrWhiteSpace(contextId.Value))
            throw new ArgumentException("A diagnostic contextual-evidence identity cannot be empty.", nameof(contextEvidence));
        if (field is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("A diagnostic field path cannot be empty.", nameof(field));
        if (placementBinding is { } placementId && string.IsNullOrWhiteSpace(placementId.Value))
            throw new ArgumentException("A diagnostic placement-binding identity cannot be empty.", nameof(placementBinding));
        if (bindingSetting is not null && string.IsNullOrWhiteSpace(bindingSetting))
            throw new ArgumentException("A diagnostic binding setting cannot be empty.", nameof(bindingSetting));
        if (resolution is not null && string.IsNullOrWhiteSpace(resolution))
            throw new ArgumentException("A diagnostic resolution cannot be empty.", nameof(resolution));
        if (configurationOrigin is { } origin && !Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurationOrigin),
                configurationOrigin,
                "Unsupported diagnostic configuration origin.");
        }
        if (configurationAuthority is not null && string.IsNullOrWhiteSpace(configurationAuthority))
            throw new ArgumentException("A diagnostic configuration authority cannot be empty.", nameof(configurationAuthority));
        if ((configurationOrigin is null) != (configurationAuthority is null))
        {
            throw new ArgumentException(
                "Diagnostic configuration origin and authority must be supplied together.",
                nameof(configurationOrigin));
        }
        if (adapterDecisionCode is { } decisionCode && string.IsNullOrWhiteSpace(decisionCode.Value))
            throw new ArgumentException("A diagnostic adapter decision code cannot be default.", nameof(adapterDecisionCode));

        Severity = severity;
        Branch = branch;
        Node = node;
        Input = input;
        Requirement = requirement;
        CapabilityEvidence = capabilityEvidence;
        OperatingBoundary = operatingBoundary;
        Override = @override;
        SemanticSite = semanticSite;
        ContextEvidence = contextEvidence;
        Field = field;
        PlacementBinding = placementBinding;
        BindingSetting = bindingSetting;
        Resolution = resolution;
        ConfigurationOrigin = configurationOrigin;
        ConfigurationAuthority = configurationAuthority;
        AdapterDecisionCode = adapterDecisionCode;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>Affected result branch, or <see langword="null"/>.</summary>
    public RelationQueryNativeResultBranchId? Branch { get; }

    /// <summary>Affected logical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected realization requirement, or <see langword="null"/>.</summary>
    public RelationQueryRealizationRequirementId? Requirement { get; }

    /// <summary>Affected target capability evidence, or <see langword="null"/>.</summary>
    public RelationQueryTargetCapabilityEvidenceId? CapabilityEvidence { get; }

    /// <summary>Affected operating boundary, or <see langword="null"/>.</summary>
    public RelationQueryOperatingBoundaryId? OperatingBoundary { get; }

    /// <summary>Affected explicit realization override, or <see langword="null"/>.</summary>
    public RelationQueryRealizationOverrideId? Override { get; }

    /// <summary>Affected semantic expression or assignment site, or <see langword="null"/>.</summary>
    public string? SemanticSite { get; }

    /// <summary>Affected contextual adapter evidence, or <see langword="null"/>.</summary>
    public RelationQueryContextEvidenceId? ContextEvidence { get; }

    /// <summary>Affected semantic field path, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected source-placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected adapter-binding setting, or <see langword="null"/>.</summary>
    public string? BindingSetting { get; }

    /// <summary>Actionable resolution guidance, or <see langword="null"/>.</summary>
    public string? Resolution { get; }

    /// <summary>Configuration-precedence tier that supplied the attributed setting, or <see langword="null"/>.</summary>
    public EffectiveConfigurationOrigin? ConfigurationOrigin { get; }

    /// <summary>Stable declaration, profile, convention, or adapter authority, or <see langword="null"/>.</summary>
    public string? ConfigurationAuthority { get; }

    /// <summary>Stable adapter-owned decision code explaining a contextual failure, or <see langword="null"/>.</summary>
    public RelationQueryAdapterDecisionCode? AdapterDecisionCode { get; }

    /// <summary>Projects an unsuccessful bound-realization report into native-compilation diagnostics.</summary>
    /// <param name="report">Unavailable or invalid bound-realization report.</param>
    /// <returns>
    /// Context-attributed diagnostics with at least one error, suitable for an unsuccessful native-compilation result.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="report"/> is realizable.</exception>
    public static ImmutableArray<RelationQueryNativeCompilationDiagnostic> FromBoundRealizationFailure(
        RelationQueryBoundRealizationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.IsRealizable)
            throw new ArgumentException("A realizable bound report has no native-compilation failure.", nameof(report));

        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        foreach (var diagnostic in report.ProfileFeasibility.Diagnostics
                     .Concat(report.Diagnostics)
                     .Distinct())
        {
            diagnostics.Add(new(
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Branch,
                diagnostic.Node,
                diagnostic.Input,
                diagnostic.Requirement,
                diagnostic.CapabilityEvidence,
                diagnostic.OperatingBoundary,
                diagnostic.Override,
                diagnostic.SemanticSite,
                diagnostic.ContextEvidence,
                diagnostic.Field,
                diagnostic.PlacementBinding,
                diagnostic.BindingSetting,
                diagnostic.Resolution,
                diagnostic.ConfigurationOrigin,
                diagnostic.ConfigurationAuthority,
                diagnostic.AdapterDecisionCode));
        }
        if (!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationUnavailable,
                DiagnosticSeverity.Error,
                $"The bound-realization status is '{report.Status}' and does not authorize native compilation."));
        }
        return diagnostics.ToImmutable();
    }
}

/// <summary>Stable shared diagnostic codes for target-native compilation inputs.</summary>
public static class RelationQueryNativeCompilationDiagnosticCodes
{
    /// <summary>The realization report does not describe the exact compiled plan.</summary>
    public const string RealizationPlanMismatch = "REL2280";

    /// <summary>The source placement does not describe the exact compiled plan.</summary>
    public const string PlacementPlanMismatch = "REL2281";

    /// <summary>The realization report does not prove every demanded requirement.</summary>
    public const string RealizationUnavailable = "REL2282";

    /// <summary>The bound-realization report does not describe the exact source placement.</summary>
    public const string BoundRealizationPlacementMismatch = "REL2283";

    /// <summary>The exact bound-realization report is unavailable or invalid.</summary>
    public const string BoundRealizationUnavailable = "REL2284";

    /// <summary>The supplied bound-realization proof cannot be reproduced from its exact contextual inputs.</summary>
    public const string BoundRealizationProofInvalid = "REL2285";
}

/// <summary>
/// Exact target-neutral inputs and selected terminal branches supplied to a backend-native compiler.
/// </summary>
public sealed class RelationQueryNativeCompilationRequest
{
    readonly ImmutableArray<RelationQueryNativeCompilationDiagnostic> validationDiagnostics;

    /// <summary>Creates a target-native compilation request.</summary>
    /// <param name="plan">Successful demand-scoped static plan.</param>
    /// <param name="boundRealization">
    /// Exact contextual realization report authorizing target lowering; plan, placement, and availability are checked
    /// by <see cref="ValidateInputs"/>.
    /// </param>
    /// <param name="placement">
    /// Physical source placement intended for target lowering; exact plan alignment is checked by
    /// <see cref="ValidateInputs"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="boundRealization"/>, or <paramref name="placement"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The bound report selects a branch absent from the compiled execution slice.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A shape snapshot from <paramref name="plan"/> cannot be represented by the compiled-plan canonicalization
    /// profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot from <paramref name="plan"/> cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot from <paramref name="plan"/> contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public RelationQueryNativeCompilationRequest(
        CompiledRelationQueryPlan plan,
        RelationQueryBoundRealizationReport boundRealization,
        RelationQuerySourcePlacement placement)
    {
        Plan = Guard.RequireNotNull(plan);
        BoundRealization = Guard.RequireNotNull(boundRealization);
        Placement = Guard.RequireNotNull(placement);
        PlanReference = RelationQueryCompiledPlanReference.From(plan);
        var available = CreateBranches(plan.ExecutionSlice);
        var availableById = available.ToDictionary(static branch => branch.Id);
        RelationQueryNativeResultBranchId? unknown = null;
        foreach (var branch in BoundRealization.Branches)
        {
            if (availableById.ContainsKey(branch))
                continue;
            unknown = branch;
            break;
        }
        if (unknown is not null)
        {
            throw new ArgumentException(
                "Bound realization selected a native branch absent from the compiled plan.",
                nameof(boundRealization));
        }
        Branches =
        [
            .. BoundRealization.Branches.OrderBy(static branch => branch.Value, StringComparer.Ordinal)
                .Select(branch => availableById[branch])
        ];
        Selection = RelationQueryCompilationSelection.Create(Plan, ProfileFeasibility, Placement, Branches);
        validationDiagnostics = ValidateInputsCore();
    }

    /// <summary>Successful target-independent static plan.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Exact portable reference computed from <see cref="Plan"/>.</summary>
    public RelationQueryCompiledPlanReference PlanReference { get; }

    /// <summary>Exact contextual realization proof authorizing native lowering.</summary>
    public RelationQueryBoundRealizationReport BoundRealization { get; }

    /// <summary>Profile-level target feasibility qualified by <see cref="BoundRealization"/>.</summary>
    public RelationQueryRealizationReport ProfileFeasibility => BoundRealization.ProfileFeasibility;

    /// <summary>Physical source placement interpreted by the target adapter.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    /// <summary>Selected demanded branches in stable branch-identity order.</summary>
    public ImmutableArray<RelationQueryNativeResultBranch> Branches { get; }

    /// <summary>
    /// Deterministic per-branch and union scope selected from the exact plan, profile requirements, and placement.
    /// </summary>
    public RelationQueryCompilationSelection Selection { get; }

    /// <summary>Validates exact plan alignment and realization availability without invoking a target compiler.</summary>
    /// <returns>Structured error diagnostics; an empty array means the shared inputs are valid.</returns>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateInputs() => validationDiagnostics;

    ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateInputsCore()
    {
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        diagnostics.AddRange(RelationQueryNativeCompilationInputValidator.Validate(
            Plan,
            ProfileFeasibility,
            Placement,
            requireRealizable: true));
        if (!Equals(BoundRealization.Placement, Placement.Fingerprint))
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationPlacementMismatch,
                DiagnosticSeverity.Error,
                "The bound-realization report does not describe the exact source placement."));
        }
        if (!BoundRealization.IsRealizable)
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationUnavailable,
                DiagnosticSeverity.Error,
                $"The bound-realization report status is '{BoundRealization.Status}' and cannot authorize native compilation."));
        }
        var reproduced = RelationQueryBoundRealizationCompiler.Compile(
            new(
                Plan,
                ProfileFeasibility,
                Placement,
                BoundRealization.Branches),
            BoundRealization.Evidence);
        if (!Equals(reproduced.Fingerprint, BoundRealization.Fingerprint))
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationProofInvalid,
                DiagnosticSeverity.Error,
                "The supplied bound-realization proof cannot be reproduced from its exact plan, profile, "
                + "placement, branch selection, and adapter evidence.",
                resolution: "Re-run contextual realization and use the resulting immutable report without modification."));
        }
        return diagnostics.ToImmutable();
    }

    internal static ImmutableArray<RelationQueryNativeResultBranch> CreateBranches(RelationQueryExecutionSlice slice)
    {
        if (slice.RelationOutput is { } relation)
        {
            return
            [
                new(
                    new($"relation:{relation.Relation.Value}"),
                    RelationQueryNativeResultKind.RelationRows,
                    relation.Definition.Node,
                    relation.Binding,
                    relation.Definition.Shape,
                    relation.Outputs,
                    relation.Fields,
                    relation: relation.Relation)
            ];
        }

        return
        [
            .. slice.QueryResults.Select(branch => new RelationQueryNativeResultBranch(
                    new($"query:{branch.Id.Value}"),
                    branch.Definition is RowsQueryResultDefinition
                        ? RelationQueryNativeResultKind.QueryRows
                        : RelationQueryNativeResultKind.QueryAggregation,
                    branch.Definition.Input,
                    branch.Binding,
                    branch.Shape,
                    branch.Outputs,
                    branch.Fields,
                    queryResult: branch.Id))
                .OrderBy(static branch => branch.Id.Value, StringComparer.Ordinal)
        ];
    }
}

/// <summary>Attributable final realization decision retained by one target-native derived artifact.</summary>
public sealed record RelationQueryNativeCompilationDecisionReference
{
    /// <summary>Creates target-native provenance for one final realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="kind">Final realization classification.</param>
    /// <param name="capabilityEvidence">
    /// Target capability evidence used by the decision. Native, composed, and constrained decisions require at
    /// least one evidence identity; override decisions may omit evidence.
    /// </param>
    /// <param name="compositionRules">
    /// Composition rules used by the decision. Composed decisions require at least one rule, native and override
    /// decisions prohibit rules, and constrained decisions may retain rules.
    /// </param>
    /// <param name="override">
    /// Explicit override identity. This is required only when <paramref name="kind"/> is
    /// <see cref="RelationQueryRealizationDecisionKind.Override"/> and otherwise must be <see langword="null"/>.
    /// </param>
    /// <param name="operatingBoundaries">
    /// Validated operating boundaries used by the decision. Only constrained and override decisions may retain
    /// boundaries.
    /// </param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the decision.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default or repeated; <paramref name="kind"/> is unavailable; required capability evidence
    /// or composition rules are absent; composition rules or operating boundaries are supplied for an incompatible
    /// decision kind; or <paramref name="override"/> conflicts with <paramref name="kind"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> or a member of <paramref name="preservedGuarantees"/> is unsupported.
    /// </exception>
    public RelationQueryNativeCompilationDecisionReference(
        RelationQueryRealizationRequirementId requirement,
        RelationQueryRealizationDecisionKind kind,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryCompositionRuleId> compositionRules = default,
        RelationQueryRealizationOverrideId? @override = null,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("Native compilation decision provenance requires a requirement identity.", nameof(requirement));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported realization decision kind.");
        if (kind == RelationQueryRealizationDecisionKind.Unavailable)
            throw new ArgumentException("A native artifact cannot retain an unavailable final decision as proof.", nameof(kind));
        if ((kind == RelationQueryRealizationDecisionKind.Override) != (@override is not null))
            throw new ArgumentException("Only an override decision can retain an override identity, and it must retain one.", nameof(@override));
        if (@override is { } overrideId && string.IsNullOrWhiteSpace(overrideId.Value))
            throw new ArgumentException("A retained override identity cannot be default.", nameof(@override));

        Requirement = requirement;
        Kind = kind;
        CapabilityEvidence = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            capabilityEvidence,
            static value => value.Value,
            nameof(capabilityEvidence));
        CompositionRules = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            compositionRules,
            static value => value.Value,
            nameof(compositionRules));
        Override = @override;
        OperatingBoundaries = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            operatingBoundaries,
            static value => value.Value,
            nameof(operatingBoundaries));
        PreservedGuarantees = NormalizeGuarantees(preservedGuarantees);

        if (kind is not RelationQueryRealizationDecisionKind.Override && CapabilityEvidence.IsDefaultOrEmpty)
            throw new ArgumentException("A retained native, composed, or constrained decision requires capability evidence.", nameof(capabilityEvidence));
        if ((kind == RelationQueryRealizationDecisionKind.Composed) != !CompositionRules.IsDefaultOrEmpty
            && kind != RelationQueryRealizationDecisionKind.Constrained)
        {
            throw new ArgumentException("Composition rules are required by composed decisions and unavailable to unrelated decision kinds.", nameof(compositionRules));
        }
        if (kind is not (RelationQueryRealizationDecisionKind.Constrained or RelationQueryRealizationDecisionKind.Override)
            && !OperatingBoundaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Only constrained or override decisions can retain validated operating boundaries.", nameof(operatingBoundaries));
        }
    }

    /// <summary>Demand-scoped requirement receiving the decision.</summary>
    public RelationQueryRealizationRequirementId Requirement { get; }

    /// <summary>Final realization classification.</summary>
    public RelationQueryRealizationDecisionKind Kind { get; }

    /// <summary>Target capability evidence used by the decision.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Composition rules used by a composed or constrained decision.</summary>
    public ImmutableArray<RelationQueryCompositionRuleId> CompositionRules { get; }

    /// <summary>Explicit override identity, or <see langword="null"/>.</summary>
    public RelationQueryRealizationOverrideId? Override { get; }

    /// <summary>Validated operating boundaries used by the decision.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Guarantees explicitly preserved by the decision.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    static ImmutableArray<RelationQueryGuaranteeCapabilityKind> NormalizeGuarantees(
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> guarantees)
    {
        var normalized = guarantees.IsDefault ? [] : guarantees;
        if (normalized.Any(static guarantee => !Enum.IsDefined(guarantee)))
            throw new ArgumentOutOfRangeException(nameof(guarantees), "Decision provenance contains an unsupported guarantee.");
        return [.. normalized.Distinct().Order()];
    }
}

/// <summary>Target-neutral provenance carried by one backend-native derived artifact.</summary>
public sealed record RelationQueryNativeCompilationProvenance
{
    /// <summary>Creates exact provenance for one target-native branch artifact.</summary>
    /// <param name="plan">Exact demand-scoped compiled-plan reference.</param>
    /// <param name="branch">Compiled terminal branch.</param>
    /// <param name="target">Interpretation target identity.</param>
    /// <param name="targetProfile">Exact target capability-profile identity.</param>
    /// <param name="realization">Exact realization-report fingerprint.</param>
    /// <param name="boundRealization">Exact bound-realization report fingerprint.</param>
    /// <param name="placement">Exact source-placement fingerprint.</param>
    /// <param name="adapterBinding">Exact target-neutral adapter-binding reference.</param>
    /// <param name="contextEvidence">Contextual assessment evidence used by this branch.</param>
    /// <param name="compilerProfile">Target compiler profile identity.</param>
    /// <param name="conventionSetVersion">Convention set applied during lowering.</param>
    /// <param name="coveredNodes">One or more logical nodes covered by the artifact.</param>
    /// <param name="coveredAssignments">Projection, grouping, and aggregate assignments covered by the artifact.</param>
    /// <param name="inputFields">
    /// Exact compiled field inputs read by the artifact. Every identity must belong to <paramref name="plan"/>.
    /// </param>
    /// <param name="realizationDecisions">Exact final realization decisions used by the artifact.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="realization"/>, <paramref name="boundRealization"/>,
    /// <paramref name="placement"/>, <paramref name="adapterBinding"/>, <paramref name="compilerProfile"/>, or
    /// <paramref name="conventionSetVersion"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A required string or identity is empty; <paramref name="coveredNodes"/> or
    /// <paramref name="contextEvidence"/> or <paramref name="realizationDecisions"/> is empty; a collection contains
    /// a <see langword="null"/> entry or a default or repeated identity; <paramref name="adapterBinding"/> does not
    /// have exact target, profile, plan, and placement affinity; or <paramref name="inputFields"/> contains an
    /// identity absent from <paramref name="plan"/>.
    /// </exception>
    public RelationQueryNativeCompilationProvenance(
        RelationQueryCompiledPlanReference plan,
        RelationQueryNativeResultBranchId branch,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        RelationQueryRealizationFingerprint realization,
        RelationQueryBoundRealizationFingerprint boundRealization,
        RelationQuerySourcePlacementFingerprint placement,
        RelationQueryAdapterBindingReference adapterBinding,
        ImmutableArray<RelationQueryContextEvidenceId> contextEvidence,
        string compilerProfile,
        string conventionSetVersion,
        ImmutableArray<QueryNodeId> coveredNodes,
        ImmutableArray<QueryAssignmentId> coveredAssignments,
        ImmutableArray<RelationQueryInputId> inputFields,
        ImmutableArray<RelationQueryNativeCompilationDecisionReference> realizationDecisions)
    {
        Plan = Guard.RequireNotNull(plan);
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("Native compilation provenance requires a branch identity.", nameof(branch));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("Native compilation provenance requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(targetProfile.Value))
            throw new ArgumentException("Native compilation provenance requires a target-profile identity.", nameof(targetProfile));
        Branch = branch;
        Target = target;
        TargetProfile = targetProfile;
        Realization = Guard.RequireNotNull(realization);
        BoundRealization = Guard.RequireNotNull(boundRealization);
        Placement = Guard.RequireNotNull(placement);
        AdapterBinding = Guard.RequireNotNull(adapterBinding);
        if (Target != AdapterBinding.Target)
            throw new ArgumentException("Native compilation target must match the adapter-binding target.", nameof(target));
        if (TargetProfile != AdapterBinding.TargetProfile)
        {
            throw new ArgumentException(
                "Native compilation target profile must match the adapter-binding target profile.",
                nameof(targetProfile));
        }
        var expectedPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(Plan);
        if (!Equals(AdapterBinding.CompiledPlanFingerprint, expectedPlanFingerprint))
        {
            throw new ArgumentException(
                "Native compilation adapter binding must have exact compiled-plan affinity.",
                nameof(adapterBinding));
        }
        if (!Equals(AdapterBinding.PlacementFingerprint, Placement))
        {
            throw new ArgumentException(
                "Native compilation adapter binding must have exact source-placement affinity.",
                nameof(adapterBinding));
        }
        ContextEvidence = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            contextEvidence,
            static item => item.Value,
            nameof(contextEvidence),
            requireNonEmpty: true);
        CompilerProfile = Guard.RequireNotNullOrWhiteSpace(compilerProfile);
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
        CoveredNodes = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            coveredNodes,
            static item => item.Value,
            nameof(coveredNodes),
            requireNonEmpty: true);
        CoveredAssignments = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            coveredAssignments,
            static item => item.Value,
            nameof(coveredAssignments));
        InputFields = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            inputFields,
            static item => item.Value,
            nameof(inputFields));
        var planInputs = Plan.Inputs.ToHashSet();
        if (InputFields.Any(input => !planInputs.Contains(input)))
            throw new ArgumentException("Native compilation provenance inputs must belong to the compiled plan.", nameof(inputFields));
        RealizationDecisions = NormalizeDecisions(realizationDecisions);
        if (RealizationDecisions.IsDefaultOrEmpty)
            throw new ArgumentException("Native compilation provenance requires at least one final realization decision.", nameof(realizationDecisions));
        CapabilityEvidence =
        [
            .. RealizationDecisions.SelectMany(static decision => decision.CapabilityEvidence)
                .Distinct()
                .OrderBy(static evidence => evidence.Value, StringComparer.Ordinal)
        ];
        OperatingBoundaries =
        [
            .. RealizationDecisions.SelectMany(static decision => decision.OperatingBoundaries)
                .Distinct()
                .OrderBy(static boundary => boundary.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Exact demand-scoped compiled-plan reference.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Compiled terminal branch.</summary>
    public RelationQueryNativeResultBranchId Branch { get; }

    /// <summary>Interpretation target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Exact target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Exact realization-report fingerprint.</summary>
    public RelationQueryRealizationFingerprint Realization { get; }

    /// <summary>Exact bound-realization report fingerprint.</summary>
    public RelationQueryBoundRealizationFingerprint BoundRealization { get; }

    /// <summary>Exact source-placement fingerprint.</summary>
    public RelationQuerySourcePlacementFingerprint Placement { get; }

    /// <summary>Exact target-neutral adapter-binding reference.</summary>
    public RelationQueryAdapterBindingReference AdapterBinding { get; }

    /// <summary>Contextual assessment evidence used by this branch in stable identity order.</summary>
    public ImmutableArray<RelationQueryContextEvidenceId> ContextEvidence { get; }

    /// <summary>Target compiler profile identity.</summary>
    public string CompilerProfile { get; }

    /// <summary>Convention set applied during lowering.</summary>
    public string ConventionSetVersion { get; }

    /// <summary>Logical nodes covered by the artifact in stable identity order.</summary>
    public ImmutableArray<QueryNodeId> CoveredNodes { get; }

    /// <summary>Covered projection, grouping, and aggregate assignments in stable identity order.</summary>
    public ImmutableArray<QueryAssignmentId> CoveredAssignments { get; }

    /// <summary>Exact compiled field inputs read by the artifact in stable identity order.</summary>
    public ImmutableArray<RelationQueryInputId> InputFields { get; }

    /// <summary>Exact final realization decisions used by the artifact in stable requirement order.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDecisionReference> RealizationDecisions { get; }

    /// <summary>
    /// Distinct capability evidence derived from <see cref="RealizationDecisions"/> in stable identity order.
    /// </summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>
    /// Distinct operating boundaries derived from <see cref="RealizationDecisions"/> in stable identity order.
    /// </summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    static ImmutableArray<RelationQueryNativeCompilationDecisionReference> NormalizeDecisions(
        ImmutableArray<RelationQueryNativeCompilationDecisionReference> decisions)
    {
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.Any(static decision => decision is null))
            throw new ArgumentException("Native compilation decision provenance cannot contain null entries.", nameof(decisions));
        if (normalized.GroupBy(static decision => decision.Requirement).Any(static group => group.Count() > 1))
            throw new ArgumentException("Native compilation decision provenance cannot repeat a requirement.", nameof(decisions));
        return [.. normalized.OrderBy(static decision => decision.Requirement.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Creates target-neutral provenance from one validated native-compilation request branch.</summary>
public static class RelationQueryNativeCompilationProvenanceFactory
{
    /// <summary>
    /// Creates exact provenance for one selected branch and retains only realization decisions that contribute to
    /// that branch's demanded outputs.
    /// </summary>
    /// <param name="request">Validated native-compilation request supplying the exact plan and proof artifacts.</param>
    /// <param name="branch">Selected result-branch identity from <paramref name="request"/>.</param>
    /// <param name="compilerProfile">Target compiler implementation/profile identity.</param>
    /// <param name="conventionSetVersion">Convention set applied during target lowering.</param>
    /// <param name="coveredNodes">One or more logical nodes covered by the native artifact.</param>
    /// <param name="coveredAssignments">Projection, grouping, and aggregate assignments covered by the artifact.</param>
    /// <param name="inputFields">Exact compiled field inputs read by the artifact.</param>
    /// <returns>
    /// Target-neutral native-compilation provenance normalized by the
    /// <see cref="RelationQueryNativeCompilationProvenance"/> contract.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/>, <paramref name="compilerProfile"/>, or
    /// <paramref name="conventionSetVersion"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="branch"/> is default or is not selected by <paramref name="request"/>; a compiler or
    /// convention identity is empty; a provenance collection contains an invalid or repeated identity;
    /// <paramref name="coveredNodes"/> is empty or contains a node outside the selected branch; a covered assignment
    /// does not belong to a covered branch node; or <paramref name="inputFields"/> contains an input not read by the
    /// selected branch.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The request has stale or unrealizable inputs, or a branch-relevant realization decision is unavailable.
    /// </exception>
    public static RelationQueryNativeCompilationProvenance Create(
        RelationQueryNativeCompilationRequest request,
        RelationQueryNativeResultBranchId branch,
        string compilerProfile,
        string conventionSetVersion,
        ImmutableArray<QueryNodeId> coveredNodes,
        ImmutableArray<QueryAssignmentId> coveredAssignments,
        ImmutableArray<RelationQueryInputId> inputFields)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("Native compilation provenance requires a branch identity.", nameof(branch));

        var selectedBranch = request.Branches.SingleOrDefault(candidate => candidate.Id == branch)
            ?? throw new ArgumentException(
                "Native compilation provenance requires a branch selected by the request.",
                nameof(branch));
        var branchSelection = request.Selection.GetBranch(branch);
        var inputDiagnostics = request.ValidateInputs();
        if (!inputDiagnostics.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                $"Native compilation provenance requires valid and realizable inputs: {string.Join(
                    "; ",
                    inputDiagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
        }

        var normalizedCoveredNodes = coveredNodes.IsDefault ? [] : coveredNodes;
        if (normalizedCoveredNodes.Any(node => !branchSelection.ContainsNode(node)))
        {
            throw new ArgumentException(
                "Native compilation provenance can cover only nodes reachable by the selected branch.",
                nameof(coveredNodes));
        }
        var coveredNodeSet = normalizedCoveredNodes.ToHashSet();
        var coveredAssignmentCandidates = request.Plan.ExecutionSlice.Nodes
            .Where(node => coveredNodeSet.Contains(node.Id))
            .SelectMany(static node => node.ProjectionAssignments
                .Select(static assignment => assignment.Definition.Id)
                .Concat(node.AggregateGroupings.Select(static grouping => grouping.Definition.Id))
                .Concat(node.AggregateAssignments.Select(static assignment => assignment.Definition.Id)))
            .ToHashSet();
        var normalizedAssignments = coveredAssignments.IsDefault ? [] : coveredAssignments;
        if (normalizedAssignments.Any(assignment => !coveredAssignmentCandidates.Contains(assignment)))
        {
            throw new ArgumentException(
                "Native compilation provenance can cover only demanded assignments belonging to covered branch nodes.",
                nameof(coveredAssignments));
        }

        var branchInputFields = branchSelection.Fields.Select(static field => field.Input.Id).ToHashSet();
        var normalizedInputFields = inputFields.IsDefault ? [] : inputFields;
        if (normalizedInputFields.Any(input => !branchInputFields.Contains(input)))
        {
            throw new ArgumentException(
                "Native compilation provenance can retain only compiled field inputs read by the selected branch.",
                nameof(inputFields));
        }
        var decisionReferences = request.ProfileFeasibility.Decisions
            .Where(decision => branchSelection.ContainsRequirement(decision.Requirement))
            .Select(CreateDecisionReference)
            .ToImmutableArray();
        return new(
            request.PlanReference,
            selectedBranch.Id,
            request.ProfileFeasibility.TargetProfile.Target,
            request.ProfileFeasibility.TargetProfile.Id,
            request.ProfileFeasibility.Fingerprint,
            request.BoundRealization.Fingerprint,
            request.Placement.Fingerprint,
            request.BoundRealization.Evidence.Binding,
            [
                .. request.BoundRealization.Evidence.Assessments
                    .Where(assessment => assessment.Branch == selectedBranch.Id
                                         && branchSelection.ContainsRequirement(assessment.Requirement))
                    .Select(static assessment => assessment.Id)
            ],
            compilerProfile,
            conventionSetVersion,
            coveredNodes,
            coveredAssignments,
            inputFields,
            decisionReferences);
    }

    /// <summary>Projects one successful realization decision into compact native-artifact proof metadata.</summary>
    /// <param name="decision">Successful final realization decision to retain.</param>
    /// <returns>A normalized native-compilation decision reference preserving the decision's exact proof.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="decision"/> is unavailable and therefore cannot prove a native artifact.
    /// </exception>
    public static RelationQueryNativeCompilationDecisionReference CreateDecisionReference(
        RelationQueryRealizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision switch
        {
            NativeRelationQueryRealizationDecision native => new(
                native.Requirement,
                native.Kind,
                native.GetCapabilityEvidence(),
                preservedGuarantees: native.GetPreservedGuarantees()),
            ComposedRelationQueryRealizationDecision composed => new(
                composed.Requirement,
                composed.Kind,
                composed.GetCapabilityEvidence(),
                composed.GetCompositionRules(),
                preservedGuarantees: composed.GetPreservedGuarantees()),
            ConstrainedRelationQueryRealizationDecision constrained => new(
                constrained.Requirement,
                constrained.Kind,
                constrained.GetCapabilityEvidence(),
                constrained.GetCompositionRules(),
                operatingBoundaries:
                [.. constrained.GetBoundaryValidations().Select(static validation => validation.Boundary)],
                preservedGuarantees: constrained.GetPreservedGuarantees()),
            OverrideRelationQueryRealizationDecision overridden => new(
                overridden.Requirement,
                overridden.Kind,
                overridden.GetCapabilityEvidence(),
                @override: overridden.Override,
                operatingBoundaries:
                [.. overridden.GetBoundaryValidations().Select(static validation => validation.Boundary)],
                preservedGuarantees: overridden.GetPreservedGuarantees()),
            UnavailableRelationQueryRealizationDecision => throw new InvalidOperationException(
                "An unavailable realization decision cannot prove target-native artifact provenance."),
            _ => throw new InvalidOperationException(
                $"Realization decision '{decision.GetType().Name}' cannot prove target-native artifact provenance.")
        };
    }
}
