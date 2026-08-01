using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Realization;

/// <summary>Overall outcome of matching one compiled plan to one target profile and policy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryRealizationStatus
{
    /// <summary>Every demanded requirement has an exact permitted realization.</summary>
    Realizable = 0,

    /// <summary>At least one demanded requirement has no exact permitted realization.</summary>
    NotRealizable = 1,

    /// <summary>Invalid or conflicting realization inputs prevent a trustworthy match result.</summary>
    Invalid = 2
}

/// <summary>Structured diagnostic emitted while projecting or matching realization requirements.</summary>
public sealed record RelationQueryRealizationDiagnostic
{
    /// <summary>Creates an attributable realization diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Effective severity after compiler policy.</param>
    /// <param name="message">Human-readable explanation without sensitive target payloads.</param>
    /// <param name="requirement">Affected realization requirement, or <see langword="null"/>.</param>
    /// <param name="capabilityEvidence">Affected target capability evidence, or <see langword="null"/>.</param>
    /// <param name="compositionRule">Affected composition rule, or <see langword="null"/>.</param>
    /// <param name="operatingBoundary">Affected operating boundary, or <see langword="null"/>.</param>
    /// <param name="override">Affected explicit override, or <see langword="null"/>.</param>
    /// <param name="node">Affected logical node, or <see langword="null"/>.</param>
    /// <param name="semanticSite">Affected semantic site, or <see langword="null"/>.</param>
    /// <param name="contextEvidence">Affected contextual adapter evidence, or <see langword="null"/>.</param>
    /// <param name="branch">Affected selected result branch, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="field">Affected semantic field path, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected source-placement binding, or <see langword="null"/>.</param>
    /// <param name="bindingSetting">Affected adapter-binding configuration setting, or <see langword="null"/>.</param>
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
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A required string or supplied identity is empty or white space, or configuration origin and authority are
    /// not supplied together, or <paramref name="adapterDecisionCode"/> is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="severity"/> or <paramref name="configurationOrigin"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryRealizationRequirementId? requirement = null,
        RelationQueryTargetCapabilityEvidenceId? capabilityEvidence = null,
        RelationQueryCompositionRuleId? compositionRule = null,
        RelationQueryOperatingBoundaryId? operatingBoundary = null,
        RelationQueryRealizationOverrideId? @override = null,
        QueryNodeId? node = null,
        string? semanticSite = null,
        RelationQueryContextEvidenceId? contextEvidence = null,
        RelationQueryNativeResultBranchId? branch = null,
        RelationQueryInputId? input = null,
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
        RequireOptional(requirement?.Value, nameof(requirement));
        RequireOptional(capabilityEvidence?.Value, nameof(capabilityEvidence));
        RequireOptional(compositionRule?.Value, nameof(compositionRule));
        RequireOptional(operatingBoundary?.Value, nameof(operatingBoundary));
        RequireOptional(@override?.Value, nameof(@override));
        RequireOptional(node?.Value, nameof(node));
        RequireOptional(semanticSite, nameof(semanticSite));
        RequireOptional(contextEvidence?.Value, nameof(contextEvidence));
        RequireOptional(branch?.Value, nameof(branch));
        RequireOptional(input?.Value, nameof(input));
        if (field is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An optional diagnostic field path cannot be empty.", nameof(field));
        RequireOptional(placementBinding?.Value, nameof(placementBinding));
        RequireOptional(bindingSetting, nameof(bindingSetting));
        RequireOptional(resolution, nameof(resolution));
        if (configurationOrigin is { } origin && !Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurationOrigin),
                configurationOrigin,
                "Unsupported diagnostic configuration origin.");
        }
        RequireOptional(configurationAuthority, nameof(configurationAuthority));
        if ((configurationOrigin is null) != (configurationAuthority is null))
        {
            throw new ArgumentException(
                "Diagnostic configuration origin and authority must be supplied together.",
                nameof(configurationOrigin));
        }
        if (adapterDecisionCode is { } decisionCode && string.IsNullOrWhiteSpace(decisionCode.Value))
            throw new ArgumentException("A diagnostic adapter decision code cannot be default.", nameof(adapterDecisionCode));

        Severity = severity;
        Requirement = requirement;
        CapabilityEvidence = capabilityEvidence;
        CompositionRule = compositionRule;
        OperatingBoundary = operatingBoundary;
        Override = @override;
        Node = node;
        SemanticSite = semanticSite;
        ContextEvidence = contextEvidence;
        Branch = branch;
        Input = input;
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

    /// <summary>Effective severity after compiler policy.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation without sensitive target payloads.</summary>
    public string Message { get; }

    /// <summary>Affected realization requirement, or <see langword="null"/>.</summary>
    public RelationQueryRealizationRequirementId? Requirement { get; }

    /// <summary>Affected target capability evidence, or <see langword="null"/>.</summary>
    public RelationQueryTargetCapabilityEvidenceId? CapabilityEvidence { get; }

    /// <summary>Affected composition rule, or <see langword="null"/>.</summary>
    public RelationQueryCompositionRuleId? CompositionRule { get; }

    /// <summary>Affected operating boundary, or <see langword="null"/>.</summary>
    public RelationQueryOperatingBoundaryId? OperatingBoundary { get; }

    /// <summary>Affected explicit override, or <see langword="null"/>.</summary>
    public RelationQueryRealizationOverrideId? Override { get; }

    /// <summary>Affected logical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Affected semantic site, or <see langword="null"/>.</summary>
    public string? SemanticSite { get; }

    /// <summary>Affected contextual adapter evidence, or <see langword="null"/>.</summary>
    public RelationQueryContextEvidenceId? ContextEvidence { get; }

    /// <summary>Affected selected result branch, or <see langword="null"/>.</summary>
    public RelationQueryNativeResultBranchId? Branch { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected semantic field path, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected source-placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected adapter-binding configuration setting, or <see langword="null"/>.</summary>
    public string? BindingSetting { get; }

    /// <summary>Actionable resolution guidance, or <see langword="null"/>.</summary>
    public string? Resolution { get; }

    /// <summary>Configuration-precedence tier that supplied the attributed setting, or <see langword="null"/>.</summary>
    public EffectiveConfigurationOrigin? ConfigurationOrigin { get; }

    /// <summary>Stable declaration, profile, convention, or adapter authority, or <see langword="null"/>.</summary>
    public string? ConfigurationAuthority { get; }

    /// <summary>Stable adapter-owned decision code explaining a contextual failure, or <see langword="null"/>.</summary>
    public RelationQueryAdapterDecisionCode? AdapterDecisionCode { get; }

    static void RequireOptional(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional diagnostic identity cannot be empty.", parameterName);
    }
}

/// <summary>Stable machine-readable realization planning diagnostic codes.</summary>
public static class RelationQueryRealizationDiagnosticCodes
{
    /// <summary>A demanded semantic requirement has no exact permitted realization.</summary>
    public const string RequirementUnavailable = "REL2001";

    /// <summary>The target profile does not support the compiled definition schema or compiler profile.</summary>
    public const string TargetProfileVersionUnsupported = "REL2002";

    /// <summary>Target capability evidence conflicts with another assertion.</summary>
    public const string CapabilityEvidenceConflict = "REL2003";

    /// <summary>Target capability evidence is incomplete or invalid.</summary>
    public const string CapabilityEvidenceInvalid = "REL2004";

    /// <summary>A composition rule is incomplete, cyclic, or references invalid inputs.</summary>
    public const string CompositionRuleInvalid = "REL2005";

    /// <summary>Several equally preferred exact strategies remain ambiguous.</summary>
    public const string StrategyAmbiguous = "REL2006";

    /// <summary>A required operating boundary was not declared.</summary>
    public const string OperatingBoundaryMissing = "REL2007";

    /// <summary>A declared operating boundary could not be validated.</summary>
    public const string OperatingBoundaryInvalid = "REL2008";

    /// <summary>An explicit override is missing, stale, or invalid.</summary>
    public const string OverrideInvalid = "REL2009";

    /// <summary>Realization compiler policy is invalid or rejects the only exact strategy.</summary>
    public const string PolicyInvalid = "REL2010";

    /// <summary>A projected realization requirement is incomplete or contradictory.</summary>
    public const string RequirementInvalid = "REL2011";

    /// <summary>A final decision is missing, duplicated, or inconsistent with its requirement.</summary>
    public const string DecisionInvalid = "REL2012";

    /// <summary>Exact contextual evidence cannot preserve a profile-feasible requirement.</summary>
    public const string ContextUnavailable = "REL2013";

    /// <summary>Contextual placement or binding evidence is stale, malformed, or contradictory.</summary>
    public const string ContextInvalid = "REL2014";

    /// <summary>An adapter projection omits evidence required to make an exact prediction.</summary>
    public const string ContextEvidenceIncomplete = "REL2015";

    /// <summary>The contextual binding does not have exact plan, placement, target, or profile affinity.</summary>
    public const string ContextAffinityMismatch = "REL2016";
}

/// <summary>
/// Portable derived artifact explaining how one target profile and policy realize one demand-scoped compiled plan.
/// </summary>
public sealed class RelationQueryRealizationReport
{
    /// <summary>Creates a realization report.</summary>
    /// <param name="plan">Exact compiled-plan provenance consumed by realization.</param>
    /// <param name="targetProfile">Exact target capability profile consumed by matching.</param>
    /// <param name="policy">Exact compiler policy, conventions, rules, and overrides consumed by matching.</param>
    /// <param name="requirements">Complete deterministically ordered demand-scoped semantic requirements.</param>
    /// <param name="decisions">Exactly one final decision for every requirement.</param>
    /// <param name="diagnostics">Structured realization diagnostics.</param>
    /// <param name="status">Overall realization outcome.</param>
    /// <param name="fingerprint">Deterministic identity of the derived report.</param>
    /// <param name="observability">
    /// Explicit runtime result observability contract. The default value requires strict exact contributor
    /// provenance for compatibility with callers that predate explicit observability.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="targetProfile"/>, <paramref name="policy"/>, or
    /// <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requirements"/> is empty; a collection contains null entries or duplicate identities;
    /// decisions do not correspond one-to-one with requirements; a decision does not constitute a valid proof
    /// against the supplied target profile and policy; <paramref name="status"/> conflicts with decisions or
    /// diagnostics; or <paramref name="fingerprint"/> does not match the normalized report content.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryRealizationReport(
        RelationQueryCompiledPlanReference plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics,
        RelationQueryRealizationStatus status,
        RelationQueryRealizationFingerprint fingerprint,
        RelationQueryResultObservability observability = default)
    {
        Plan = Guard.RequireNotNull(plan);
        TargetProfile = Guard.RequireNotNull(targetProfile);
        Policy = Guard.RequireNotNull(policy);
        Observability = observability;
        Requirements = NormalizeRequirements(requirements);
        Decisions = NormalizeDecisions(decisions, Requirements);
        Diagnostics = NormalizeDiagnostics(diagnostics);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported realization status.");
        var suppliedFingerprint = Guard.RequireNotNull(fingerprint);

        ValidateTargetProfileDiagnostics(TargetProfile, Diagnostics);
        ValidateInputDeclarationDiagnostics(Requirements, Policy, Diagnostics);
        ValidateDecisionSemantics(Plan, Requirements, Decisions, TargetProfile, Policy);
        ValidateStatus(status, Decisions, Diagnostics);
        var expectedFingerprint = RelationQueryRealizationFingerprinter.Compute(
            Plan,
            TargetProfile,
            Policy,
            Observability,
            Requirements,
            Decisions,
            Diagnostics,
            status);
        if (!Equals(suppliedFingerprint, expectedFingerprint))
        {
            throw new ArgumentException(
                "The supplied realization fingerprint does not match the normalized report content.",
                nameof(fingerprint));
        }

        Fingerprint = suppliedFingerprint;
        Status = status;
    }

    /// <summary>Exact compiled-plan provenance consumed by realization.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact target capability profile consumed by matching.</summary>
    public RelationQueryTargetCapabilityProfile TargetProfile { get; }

    /// <summary>Exact compiler policy, conventions, rules, and overrides consumed by matching.</summary>
    public RelationQueryRealizationPolicy Policy { get; }

    /// <summary>Runtime result observability contract realized by this report.</summary>
    public RelationQueryResultObservability Observability { get; }

    /// <summary>Complete demand-scoped semantic requirements in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryRealizationRequirement> Requirements { get; }

    /// <summary>Exactly one final decision for every requirement in deterministic requirement order.</summary>
    public ImmutableArray<RelationQueryRealizationDecision> Decisions { get; }

    /// <summary>Structured diagnostics in deterministic attribution order.</summary>
    public ImmutableArray<RelationQueryRealizationDiagnostic> Diagnostics { get; }

    /// <summary>Overall realization outcome.</summary>
    public RelationQueryRealizationStatus Status { get; }

    /// <summary>Deterministic identity of this derived report.</summary>
    public RelationQueryRealizationFingerprint Fingerprint { get; }

    /// <summary>Whether every demanded requirement has an exact permitted realization.</summary>
    [JsonIgnore]
    public bool IsRealizable => Status == RelationQueryRealizationStatus.Realizable;

    static ImmutableArray<RelationQueryRealizationRequirement> NormalizeRequirements(
        ImmutableArray<RelationQueryRealizationRequirement> requirements)
    {
        var normalized = requirements.IsDefault ? [] : requirements;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one realization requirement is required.", nameof(requirements));
        if (normalized.Any(static requirement => requirement is null))
            throw new ArgumentException("Realization requirements cannot contain null entries.", nameof(requirements));
        if (normalized.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization requirements cannot repeat an identity.", nameof(requirements));
        return [.. normalized.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryRealizationDecision> NormalizeDecisions(
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        ImmutableArray<RelationQueryRealizationRequirement> requirements)
    {
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.Any(static decision => decision is null))
            throw new ArgumentException("Realization decisions cannot contain null entries.", nameof(decisions));
        if (normalized.GroupBy(static decision => decision.Requirement).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization decisions cannot repeat a requirement identity.", nameof(decisions));
        var expected = requirements.Select(static requirement => requirement.Id).ToHashSet();
        var actual = normalized.Select(static decision => decision.Requirement).ToHashSet();
        if (!expected.SetEquals(actual))
            throw new ArgumentException("Every realization requirement must have exactly one final decision.", nameof(decisions));
        return [.. normalized.OrderBy(static decision => decision.Requirement.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryRealizationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Realization diagnostics cannot contain null entries.", nameof(diagnostics));
        return
        [
            .. normalized
                .OrderBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.CompositionRule?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Override?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.BindingSetting ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.ConfigurationOrigin is { } origin ? (int)origin : -1)
                .ThenBy(static diagnostic => diagnostic.ConfigurationAuthority ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.AdapterDecisionCode?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.ContextEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];
    }

    static void ValidateDecisionSemantics(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy)
    {
        var supportsPlan = targetProfile.SupportedDefinitionSchemaVersions.Contains(
                               plan.DefinitionSchemaVersion,
                               StringComparer.Ordinal)
                           && targetProfile.SupportedCompilerProfiles.Contains(
                               plan.CompilerProfile,
                               StringComparer.Ordinal);
        if (!supportsPlan && decisions.Any(static decision =>
                decision.Kind != CapabilityRealizationKind.Unavailable))
        {
            throw InvalidDecision(
                "A target profile that does not support the compiled plan cannot carry an available realization decision.");
        }

        var requirementsById = requirements.ToDictionary(static requirement => requirement.Id);
        var profileAnalysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(targetProfile);
        if (!profileAnalysis.Issues.IsDefaultOrEmpty)
        {
            foreach (var decision in decisions)
            {
                var unavailable = decision as UnavailableRelationQueryRealizationDecision
                    ?? throw InvalidDecision(
                        "An invalid target capability profile cannot carry an available realization decision.");
                var requirement = requirementsById[decision.Requirement];
                if (unavailable.Reason != RelationQueryUnavailableReason.CapabilityEvidenceInvalid
                    || unavailable.MissingCapabilities.Length != 1
                    || !Equals(unavailable.MissingCapabilities[0], requirement.Capability))
                {
                    throw InvalidDecision(
                        "An invalid target capability profile requires a fail-closed unavailable decision for every requirement.");
                }
            }
            return;
        }

        var evidenceById = profileAnalysis.Evidence;
        var boundariesById = profileAnalysis.Boundaries;
        var rulesById = policy.CompositionRules.ToDictionary(static item => item.Id);
        var overridesById = policy.Overrides.ToDictionary(static item => item.Id);

        foreach (var decision in decisions)
        {
            var requirement = requirementsById[decision.Requirement];
            var applicableOverrides = policy.Overrides
                .Where(item => item.Requirement == requirement.Id)
                .ToImmutableArray();
            if (!applicableOverrides.IsDefaultOrEmpty
                && decision is NativeRelationQueryRealizationDecision
                    or ComposedRelationQueryRealizationDecision
                    or ConstrainedRelationQueryRealizationDecision)
            {
                throw InvalidDecision(
                    "An available target strategy cannot bypass an explicit requirement override.");
            }
            if (decision is OverrideRelationQueryRealizationDecision && applicableOverrides.Length != 1)
                throw InvalidDecision("An override decision requires exactly one applicable policy override.");

            switch (decision)
            {
                case NativeRelationQueryRealizationDecision native:
                    {
                        var proof = ValidateNativeProof(
                            requirement,
                            native.CapabilityEvidence,
                            native.PreservedGuarantees,
                            [],
                            evidenceById);
                        if (proof.Boundaries.Count != 0)
                            throw InvalidDecision("A native decision cannot depend on an operating boundary.");
                        break;
                    }
                case ComposedRelationQueryRealizationDecision composed:
                    {
                        var proof = ValidateCompositionProof(
                            requirement,
                            composed.CompositionRules,
                            composed.CapabilityEvidence,
                            composed.PreservedGuarantees,
                            [],
                            evidenceById,
                            rulesById,
                            policy);
                        if (proof.Boundaries.Count != 0)
                            throw InvalidDecision("A composed decision with operating boundaries must be classified as constrained.");
                        break;
                    }
                case ConstrainedRelationQueryRealizationDecision constrained:
                    {
                        if (policy.ConstrainedRealizations != RelationQueryConstrainedRealizationPolicy.AllowValidated)
                            throw InvalidDecision("Compiler policy does not permit constrained realization decisions.");

                        var proof = constrained.CompositionRules.IsDefaultOrEmpty
                            ? ValidateNativeProof(
                                requirement,
                                constrained.CapabilityEvidence,
                                constrained.PreservedGuarantees,
                                constrained.BoundaryValidations,
                                evidenceById)
                            : ValidateCompositionProof(
                                requirement,
                                constrained.CompositionRules,
                                constrained.CapabilityEvidence,
                                constrained.PreservedGuarantees,
                                constrained.BoundaryValidations,
                                evidenceById,
                                rulesById,
                                policy);
                        if (proof.Boundaries.Count == 0)
                            throw InvalidDecision("A constrained decision requires proof that depends on an operating boundary.");
                        ValidateBoundaryArtifacts(
                            requirement,
                            proof.Boundaries,
                            constrained.BoundaryValidations,
                            constrained.CapabilityEvidence,
                            evidenceById,
                            boundariesById);
                        break;
                    }
                case OverrideRelationQueryRealizationDecision overridden:
                    ValidateOverride(
                        requirement,
                        overridden,
                        policy,
                        evidenceById,
                        boundariesById,
                        overridesById);
                    break;
                case UnavailableRelationQueryRealizationDecision:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(decisions),
                        decision,
                        "Unsupported realization decision variant.");
            }
        }
    }

    static void ValidateTargetProfileDiagnostics(
        RelationQueryTargetCapabilityProfile targetProfile,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var analysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(targetProfile);
        foreach (var issue in analysis.Issues)
        {
            if (!diagnostics.Any(diagnostic =>
                    string.Equals(diagnostic.Code, issue.Code, StringComparison.Ordinal)
                    && diagnostic.Severity == DiagnosticSeverity.Error
                    && diagnostic.CapabilityEvidence == issue.CapabilityEvidence
                    && diagnostic.OperatingBoundary == issue.OperatingBoundary))
            {
                throw new ArgumentException(
                    "Invalid target-profile declarations require matching structured error diagnostics.",
                    nameof(diagnostics));
            }
        }
    }

    static void ValidateInputDeclarationDiagnostics(
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        RelationQueryRealizationPolicy policy,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        foreach (var requirement in requirements.Where(requirement =>
                     RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(requirement.Capability)
                     is not null))
        {
            RequireErrorDiagnostic(
                diagnostics,
                RelationQueryRealizationDiagnosticCodes.RequirementInvalid,
                requirement: requirement.Id);
        }

        foreach (var rule in policy.CompositionRules.Where(rule =>
                     RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(rule.ProvidedCapability)
                     is not null
                     || rule.RequiredCapabilities.Any(capability =>
                         RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(capability) is not null)))
        {
            RequireErrorDiagnostic(
                diagnostics,
                RelationQueryRealizationDiagnosticCodes.CompositionRuleInvalid,
                compositionRule: rule.Id);
        }

        foreach (var selection in policy.CompositionRuleSelections.Where(selection =>
                     RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(selection.Capability)
                     is not null))
        {
            RequireErrorDiagnostic(
                diagnostics,
                RelationQueryRealizationDiagnosticCodes.PolicyInvalid,
                compositionRule: selection.Rule);
        }

        foreach (var @override in policy.Overrides.Where(@override =>
                     RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(@override.ExpectedCapability)
                     is not null))
        {
            RequireErrorDiagnostic(
                diagnostics,
                RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                @override: @override.Id);
        }
    }

    static void RequireErrorDiagnostic(
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics,
        string code,
        RelationQueryRealizationRequirementId? requirement = null,
        RelationQueryCompositionRuleId? compositionRule = null,
        RelationQueryRealizationOverrideId? @override = null)
    {
        if (diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, code, StringComparison.Ordinal)
                && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Requirement == requirement
                && diagnostic.CompositionRule == compositionRule
                && diagnostic.Override == @override))
        {
            return;
        }

        throw new ArgumentException(
            "Invalid realization declarations require matching structured error diagnostics.",
            nameof(diagnostics));
    }

    static ProofSummary ValidateNativeProof(
        RelationQueryRealizationRequirement requirement,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> evidenceIds,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> boundaryValidations,
        IReadOnlyDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidenceById)
    {
        var evidence = ResolveEvidence(evidenceIds, evidenceById);
        var direct = evidence.Where(item => Equals(item.Capability, requirement.Capability)).ToImmutableArray();
        if (direct.IsDefaultOrEmpty)
            throw InvalidDecision("A native decision requires evidence for the exact demanded capability.");

        var boundaryKeys = direct
            .Select(static item => RelationQueryRealizationOrdering.SequenceKey(
                item.OperatingBoundaries.Select(static boundary => boundary.Value)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (boundaryKeys != 1)
            throw InvalidDecision("A native decision cannot combine alternative evidence with different operating boundaries.");

        var directIds = direct.Select(static item => item.Id).ToHashSet();
        var validatorIds = boundaryValidations
            .Where(static validation => validation.CapabilityEvidence is not null)
            .Select(static validation => validation.CapabilityEvidence!.Value)
            .ToHashSet();
        var preserved = preservedGuarantees.ToHashSet();
        foreach (var item in evidence.Where(item => !directIds.Contains(item.Id)))
        {
            var supportsGuarantee = item.Capability is GuaranteeRelationQueryCapability guarantee
                                    && preserved.Contains(guarantee.Kind);
            if (!supportsGuarantee && !validatorIds.Contains(item.Id))
                throw InvalidDecision("A native decision contains evidence unrelated to its capability proof.");
        }

        foreach (var guarantee in preservedGuarantees)
        {
            if (!evidence.Any(item => item.Capability is GuaranteeRelationQueryCapability supported
                                      && supported.Kind == guarantee))
            {
                throw InvalidDecision($"A native decision does not carry evidence for preserved guarantee '{guarantee}'.");
            }
        }

        ValidateRequiredGuarantees(requirement, preservedGuarantees);
        return new(evidence.SelectMany(static item => item.OperatingBoundaries).ToHashSet());
    }

    static ProofSummary ValidateCompositionProof(
        RelationQueryRealizationRequirement requirement,
        ImmutableArray<RelationQueryCompositionRuleId> ruleIds,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> evidenceIds,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> boundaryValidations,
        IReadOnlyDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidenceById,
        IReadOnlyDictionary<RelationQueryCompositionRuleId, RelationQueryCompositionRule> rulesById,
        RelationQueryRealizationPolicy policy)
    {
        var evidence = ResolveEvidence(evidenceIds, evidenceById);
        var rules = ruleIds.Select(ruleId => rulesById.TryGetValue(ruleId, out var rule)
                ? rule
                : throw InvalidDecision($"A decision references unknown composition rule '{ruleId.Value}'."))
            .ToImmutableArray();
        var rootRules = rules.Where(rule => Equals(rule.ProvidedCapability, requirement.Capability)).ToImmutableArray();
        if (rootRules.Length != 1)
            throw InvalidDecision("A composed decision requires exactly one root rule for the demanded capability.");

        var rulesByCapability = rules
            .GroupBy(static rule => rule.ProvidedCapability)
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        if (rulesByCapability.Values.Any(static providers => providers.Length != 1))
            throw InvalidDecision("A composed decision must select exactly one rule for each provided capability.");
        ValidateCompositionAcyclic(requirement.Capability, rulesByCapability, []);

        HashSet<RelationQueryCapability> reachableCapabilities = [];
        HashSet<RelationQueryCompositionRuleId> reachableRules = [];
        Queue<RelationQueryCapability> pending = new([requirement.Capability]);
        while (pending.TryDequeue(out var capability))
        {
            if (!reachableCapabilities.Add(capability)
                || !rulesByCapability.TryGetValue(capability, out var providers))
            {
                continue;
            }

            foreach (var provider in providers)
            {
                reachableRules.Add(provider.Id);
                foreach (var required in provider.RequiredCapabilities)
                    pending.Enqueue(required);
            }
        }

        if (!reachableRules.SetEquals(ruleIds))
            throw InvalidDecision("A composed decision contains a rule outside the root proof closure.");

        foreach (var selection in policy.CompositionRuleSelections.Where(selection =>
                     reachableCapabilities.Contains(selection.Capability)))
        {
            if (rulesByCapability.TryGetValue(selection.Capability, out var providers)
                && providers.Any(provider => provider.Id != selection.Rule))
            {
                throw InvalidDecision("A composed decision conflicts with an explicit composition-rule selection.");
            }
        }

        var validatorIds = boundaryValidations
            .Where(static validation => validation.CapabilityEvidence is not null)
            .Select(static validation => validation.CapabilityEvidence!.Value)
            .ToHashSet();
        foreach (var capability in reachableCapabilities)
        {
            var hasRule = rulesByCapability.ContainsKey(capability);
            var hasEvidence = evidence.Any(item => Equals(item.Capability, capability));
            if (!hasRule && !hasEvidence)
                throw InvalidDecision("A composed decision does not close over every required capability.");
        }

        foreach (var item in evidence)
        {
            if (!reachableCapabilities.Contains(item.Capability) && !validatorIds.Contains(item.Id))
                throw InvalidDecision("A composed decision contains evidence outside the root proof closure.");
            if (Equals(item.Capability, requirement.Capability) && !validatorIds.Contains(item.Id))
                throw InvalidDecision("A composed decision cannot also use direct root-capability evidence.");
        }

        var rootGuarantees = rootRules[0].PreservedGuarantees.ToHashSet();
        if (!rootGuarantees.SetEquals(preservedGuarantees))
            throw InvalidDecision("A composed decision's preserved guarantees do not match its root rule.");
        ValidateRequiredGuarantees(requirement, preservedGuarantees);

        HashSet<RelationQueryOperatingBoundaryId> boundaries =
        [
            .. evidence.SelectMany(static item => item.OperatingBoundaries),
            .. rules.SelectMany(static rule => rule.RequiredOperatingBoundaries)
        ];
        return new(boundaries);
    }

    static void ValidateCompositionAcyclic(
        RelationQueryCapability capability,
        IReadOnlyDictionary<RelationQueryCapability, ImmutableArray<RelationQueryCompositionRule>> rulesByCapability,
        ImmutableHashSet<RelationQueryCapability> stack)
    {
        if (!rulesByCapability.TryGetValue(capability, out var providers))
            return;
        if (stack.Contains(capability))
            throw InvalidDecision("A composed decision contains a capability cycle.");

        var next = stack.Add(capability);
        foreach (var provider in providers)
        {
            foreach (var required in provider.RequiredCapabilities)
                ValidateCompositionAcyclic(required, rulesByCapability, next);
        }
    }

    static void ValidateOverride(
        RelationQueryRealizationRequirement requirement,
        OverrideRelationQueryRealizationDecision decision,
        RelationQueryRealizationPolicy policy,
        IReadOnlyDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidenceById,
        IReadOnlyDictionary<RelationQueryOperatingBoundaryId, RelationQueryOperatingBoundary> boundariesById,
        IReadOnlyDictionary<RelationQueryRealizationOverrideId, RelationQueryRealizationOverride> overridesById)
    {
        if (!overridesById.TryGetValue(decision.Override, out var declared))
            throw InvalidDecision($"An override decision references unknown override '{decision.Override.Value}'.");
        var applicable = policy.Overrides.Where(item => item.Requirement == requirement.Id).ToImmutableArray();
        if (applicable.Length != 1 || applicable[0].Id != declared.Id)
            throw InvalidDecision("An override decision requires exactly one applicable policy override.");
        if (declared.Requirement != requirement.Id || !Equals(declared.ExpectedCapability, requirement.Capability))
            throw InvalidDecision("An override decision references a stale requirement or expected capability.");
        if (!declared.CapabilityEvidence.ToHashSet().SetEquals(decision.CapabilityEvidence))
            throw InvalidDecision("An override decision's capability evidence differs from its policy declaration.");
        if (!declared.PreservedGuarantees.ToHashSet().SetEquals(decision.PreservedGuarantees))
            throw InvalidDecision("An override decision's preserved guarantees differ from its policy declaration.");

        var validationBoundaries = decision.BoundaryValidations
            .Select(static validation => validation.Boundary)
            .ToHashSet();
        if (!declared.OperatingBoundaries.ToHashSet().SetEquals(validationBoundaries))
            throw InvalidDecision("An override decision's boundary validations differ from its policy declaration.");
        if (validationBoundaries.Count != 0
            && policy.ConstrainedRealizations != RelationQueryConstrainedRealizationPolicy.AllowValidated)
        {
            throw InvalidDecision("Compiler policy does not permit a boundary-constrained override.");
        }

        var evidence = ResolveEvidence(decision.CapabilityEvidence, evidenceById);
        var undeclaredEvidenceBoundary = evidence
            .SelectMany(static item => item.OperatingBoundaries)
            .FirstOrDefault(boundary => !validationBoundaries.Contains(boundary));
        if (!string.IsNullOrWhiteSpace(undeclaredEvidenceBoundary.Value))
            throw InvalidDecision("An override omits an operating boundary required by its target evidence.");

        ValidateRequiredGuarantees(requirement, decision.PreservedGuarantees);
        ValidateBoundaryArtifacts(
            requirement,
            validationBoundaries,
            decision.BoundaryValidations,
            decision.CapabilityEvidence,
            evidenceById,
            boundariesById);
    }

    static void ValidateBoundaryArtifacts(
        RelationQueryRealizationRequirement requirement,
        IReadOnlySet<RelationQueryOperatingBoundaryId> expectedBoundaries,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> validations,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> decisionEvidence,
        IReadOnlyDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidenceById,
        IReadOnlyDictionary<RelationQueryOperatingBoundaryId, RelationQueryOperatingBoundary> boundariesById)
    {
        var actualBoundaries = validations.Select(static validation => validation.Boundary).ToHashSet();
        if (!actualBoundaries.SetEquals(expectedBoundaries))
            throw InvalidDecision("Boundary-validation artifacts do not exactly cover the proof's operating boundaries.");

        var selectedEvidence = decisionEvidence.ToHashSet();
        foreach (var validation in validations)
        {
            if (!boundariesById.TryGetValue(validation.Boundary, out var boundary))
                throw InvalidDecision($"A decision validates unknown operating boundary '{validation.Boundary.Value}'.");

            var expectedFactKind = boundary.Kind switch
            {
                RelationQueryOperatingBoundaryKind.MaximumFieldPathDepth =>
                    RelationQueryRealizationStaticFactKind.FieldPathDepth,
                RelationQueryOperatingBoundaryKind.MaximumExpressionDepth =>
                    RelationQueryRealizationStaticFactKind.ExpressionDepth,
                RelationQueryOperatingBoundaryKind.MaximumPageSize =>
                    RelationQueryRealizationStaticFactKind.PageSize,
                _ => (RelationQueryRealizationStaticFactKind?)null
            };
            var fact = expectedFactKind is { } factKind
                ? requirement.StaticFacts.FirstOrDefault(candidate => candidate.Kind == factKind)
                : null;

            if (fact is not null)
            {
                if (boundary.Limit is not { } limit || fact.Value > limit)
                    throw InvalidDecision("A boundary-validation artifact contradicts the requirement's static plan facts.");
                if (validation.Kind != RelationQueryOperatingBoundaryValidationKind.StaticPlanFact
                    || validation.MeasuredValue != fact.Value)
                {
                    throw InvalidDecision("A statically provable boundary must carry its exact attributable plan measurement.");
                }
                continue;
            }

            if (validation.Kind != RelationQueryOperatingBoundaryValidationKind.TargetEnforced
                || validation.CapabilityEvidence is not { } validatorId
                || !selectedEvidence.Contains(validatorId)
                || !evidenceById.TryGetValue(validatorId, out var validator)
                || validator.Capability is not OperatingBoundaryValidationRelationQueryCapability capability
                || capability.Boundary != validation.Boundary
                || !validator.OperatingBoundaries.IsDefaultOrEmpty)
            {
                throw InvalidDecision(
                    "A target-enforced boundary requires selected, unbounded evidence for that exact boundary.");
            }
        }
    }

    static ImmutableArray<RelationQueryTargetCapabilityEvidence> ResolveEvidence(
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> ids,
        IReadOnlyDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidenceById) =>
        [
            .. ids.Select(id => evidenceById.TryGetValue(id, out var evidence)
                ? evidence
                : throw InvalidDecision($"A decision references unknown target capability evidence '{id.Value}'."))
        ];

    static void ValidateRequiredGuarantees(
        RelationQueryRealizationRequirement requirement,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees)
    {
        var preserved = preservedGuarantees.ToHashSet();
        if (requirement.RequiredGuarantees.Any(guarantee => !preserved.Contains(guarantee)))
            throw InvalidDecision("A realization decision does not preserve every guarantee required by its requirement.");
    }

    static ArgumentException InvalidDecision(string message) => new(message, "decisions");

    readonly record struct ProofSummary(IReadOnlySet<RelationQueryOperatingBoundaryId> Boundaries);

    static void ValidateStatus(
        RelationQueryRealizationStatus status,
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var hasUnavailable = decisions.Any(static decision =>
            decision.Kind == CapabilityRealizationKind.Unavailable);
        var hasError = diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (status == RelationQueryRealizationStatus.Realizable && (hasUnavailable || hasError))
            throw new ArgumentException("A realizable report cannot contain unavailable decisions or error diagnostics.", nameof(status));
        if (status == RelationQueryRealizationStatus.NotRealizable && !hasUnavailable)
            throw new ArgumentException("A non-realizable report requires at least one unavailable decision.", nameof(status));
        if (status == RelationQueryRealizationStatus.Invalid && !hasError)
            throw new ArgumentException("An invalid report requires at least one error diagnostic.", nameof(status));
    }
}
