using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>Explicit association between a semantic composition rule and its physical lowering.</summary>
public sealed record RelationQueryPhysicalLoweringSelection
{
    /// <summary>Creates a physical-lowering selection.</summary>
    /// <param name="compositionRule">ARI-127 composition rule selected by realization.</param>
    /// <param name="physicalLowering">Versioned physical lowering that implements the selected rule.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalLoweringSelection(
        RelationQueryCompositionRuleId compositionRule,
        RelationQueryPhysicalLoweringRuleId physicalLowering)
    {
        if (string.IsNullOrWhiteSpace(compositionRule.Value))
            throw new ArgumentException("A lowering selection requires a composition rule.", nameof(compositionRule));
        if (string.IsNullOrWhiteSpace(physicalLowering.Value))
            throw new ArgumentException("A lowering selection requires a physical lowering.", nameof(physicalLowering));
        CompositionRule = compositionRule;
        PhysicalLowering = physicalLowering;
    }

    /// <summary>ARI-127 composition rule selected by realization.</summary>
    public RelationQueryCompositionRuleId CompositionRule { get; }

    /// <summary>Versioned physical lowering that implements the selected rule.</summary>
    public RelationQueryPhysicalLoweringRuleId PhysicalLowering { get; }
}

/// <summary>Explicit bounded policy used to compile and execute a physical plan.</summary>
public sealed class RelationQueryPhysicalPlanningPolicy
{
    /// <summary>Creates a bounded physical-planning policy.</summary>
    /// <param name="id">Stable, versioned policy identity.</param>
    /// <param name="conventionSetVersion">Version of deterministic planning conventions.</param>
    /// <param name="maximumBatchSize">Maximum keys issued in one lookup request.</param>
    /// <param name="maximumBufferedRows">Maximum cumulative rows retained by one stage.</param>
    /// <param name="maximumLocalRows">Maximum rows admitted to local processing.</param>
    /// <param name="maximumFanOut">Maximum related rows accepted per source occurrence.</param>
    /// <param name="maximumReferenceKeysPerObservation">
    /// Maximum relationship-reference keys extracted from one acquired observation.
    /// </param>
    /// <param name="maximumConcurrency">Maximum independent acquisition operations scheduled concurrently.</param>
    /// <param name="loweringSelections">Explicit mappings from selected realization rules to physical lowerings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="conventionSetVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or lowering selections conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive or is not portable.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalPlanningPolicy(
        RelationQueryPhysicalPlanningPolicyId id,
        string conventionSetVersion,
        long maximumBatchSize,
        long maximumBufferedRows,
        long maximumLocalRows,
        long maximumFanOut,
        long maximumReferenceKeysPerObservation,
        long maximumConcurrency,
        ImmutableArray<RelationQueryPhysicalLoweringSelection> loweringSelections = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A physical-planning policy requires an identity.", nameof(id));
        Id = id;
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
        MaximumBatchSize = RelationQuerySourcePlacementLimits.RequireLimit(maximumBatchSize, nameof(maximumBatchSize));
        MaximumBufferedRows = RelationQuerySourcePlacementLimits.RequireLimit(maximumBufferedRows, nameof(maximumBufferedRows));
        MaximumLocalRows = RelationQuerySourcePlacementLimits.RequireLimit(maximumLocalRows, nameof(maximumLocalRows));
        MaximumFanOut = RelationQuerySourcePlacementLimits.RequireLimit(maximumFanOut, nameof(maximumFanOut));
        MaximumReferenceKeysPerObservation = RelationQuerySourcePlacementLimits.RequireLimit(
            maximumReferenceKeysPerObservation,
            nameof(maximumReferenceKeysPerObservation));
        MaximumConcurrency = RelationQuerySourcePlacementLimits.RequireLimit(maximumConcurrency, nameof(maximumConcurrency));
        var normalized = loweringSelections.IsDefault ? [] : loweringSelections;
        if (normalized.Any(static selection => selection is null))
            throw new ArgumentException("Lowering selections cannot contain null entries.", nameof(loweringSelections));
        if (normalized.GroupBy(static selection => selection.CompositionRule).Any(static group => group.Count() > 1))
            throw new ArgumentException("A composition rule cannot select more than one physical lowering.", nameof(loweringSelections));
        LoweringSelections = [.. normalized.OrderBy(static selection => selection.CompositionRule.Value, StringComparer.Ordinal)];
    }

    /// <summary>Stable, versioned policy identity.</summary>
    public RelationQueryPhysicalPlanningPolicyId Id { get; }

    /// <summary>Version of deterministic planning conventions.</summary>
    public string ConventionSetVersion { get; }

    /// <summary>Maximum keys issued in one lookup request.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBatchSize { get; }

    /// <summary>Maximum cumulative rows retained by one stage.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBufferedRows { get; }

    /// <summary>Maximum rows admitted to local processing.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumLocalRows { get; }

    /// <summary>Maximum related rows accepted per source occurrence.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumFanOut { get; }

    /// <summary>Maximum relationship-reference keys extracted from one acquired observation.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumReferenceKeysPerObservation { get; }

    /// <summary>Maximum acquisition operations scheduled concurrently.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumConcurrency { get; }

    /// <summary>Explicit rule-to-lowering mappings in composition-rule order.</summary>
    public ImmutableArray<RelationQueryPhysicalLoweringSelection> LoweringSelections { get; }
}

/// <summary>Closed operator kind used by the v1 portable physical plan.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryPhysicalStageKind
{
    /// <summary>Consumes observations supplied by the execution request.</summary>
    SuppliedInput = 0,

    /// <summary>Reads a bounded semantic source set.</summary>
    SourceRead = 1,

    /// <summary>Retains exactly selected compiled fields.</summary>
    ExactFieldProjection = 2,

    /// <summary>Extracts relationship reference or identity keys.</summary>
    RelationshipKeyExtraction = 3,

    /// <summary>Deduplicates acquisition keys while retaining correlation provenance.</summary>
    KeyDeduplication = 4,

    /// <summary>Reads related observations by stable identity in bounded batches.</summary>
    BatchedIdentityLookup = 5,

    /// <summary>Reads inverse relationship matches by key predicate in bounded batches.</summary>
    BatchedPredicateLookup = 6,

    /// <summary>Correlates acquired observations to source occurrences locally.</summary>
    LocalCorrelation = 7,

    /// <summary>Assembles authoritative canonical runtime evidence.</summary>
    RuntimeEvidenceAssembly = 8,

    /// <summary>Delegates residual semantics to the canonical reference interpreter.</summary>
    ReferenceInterpreterTerminal = 9
}

/// <summary>Qualified reference to capability evidence in one exact source profile.</summary>
public sealed record RelationQueryPhysicalCapabilityEvidenceReference
{
    /// <summary>Creates a qualified source capability-evidence reference.</summary>
    /// <param name="source">Physical source instance whose profile owns the evidence.</param>
    /// <param name="target">Interpretation target identity.</param>
    /// <param name="profile">Target-profile identity.</param>
    /// <param name="evidence">Capability-evidence identity within the profile.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalCapabilityEvidenceReference(
        RelationQuerySourceInstanceId source,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId profile,
        RelationQueryTargetCapabilityEvidenceId evidence)
    {
        if (string.IsNullOrWhiteSpace(source.Value) || string.IsNullOrWhiteSpace(target.Value)
            || string.IsNullOrWhiteSpace(profile.Value) || string.IsNullOrWhiteSpace(evidence.Value))
            throw new ArgumentException("A physical evidence reference requires complete identities.", nameof(evidence));
        Source = source;
        Target = target;
        Profile = profile;
        Evidence = evidence;
    }

    /// <summary>Physical source instance whose profile owns the evidence.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Interpretation target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Target-profile identity.</summary>
    public RelationQueryTargetProfileId Profile { get; }

    /// <summary>Capability-evidence identity within the profile.</summary>
    public RelationQueryTargetCapabilityEvidenceId Evidence { get; }
}

/// <summary>Complete attribution carried by one physical stage.</summary>
public sealed record RelationQueryPhysicalStageProvenance
{
    /// <summary>Creates physical-stage provenance.</summary>
    /// <param name="nodes">Canonical logical nodes contributing to the stage.</param>
    /// <param name="inputs">Compiled input-contract identities contributing to the stage.</param>
    /// <param name="requirements">ARI-127 realization requirements authorizing the stage.</param>
    /// <param name="capabilityEvidence">Qualified source capability evidence used by the stage.</param>
    /// <param name="compositionRules">Selected ARI-127 composition rules used by lowering.</param>
    /// <param name="operatingBoundaries">Validated operating boundaries enforced by the stage.</param>
    /// <param name="placementBindings">Source-placement bindings consumed by the stage.</param>
    /// <param name="loweringRule">Versioned physical lowering rule, or <see langword="null"/>.</param>
    /// <param name="policyDecisions">Attributable physical-policy decisions.</param>
    /// <exception cref="ArgumentException">A collection contains null/default or duplicate entries, or no semantic attribution is supplied.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalStageProvenance(
        ImmutableArray<QueryNodeId> nodes = default,
        ImmutableArray<RelationQueryInputId> inputs = default,
        ImmutableArray<RelationQueryRealizationRequirementId> requirements = default,
        ImmutableArray<RelationQueryPhysicalCapabilityEvidenceReference> capabilityEvidence = default,
        ImmutableArray<RelationQueryCompositionRuleId> compositionRules = default,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQuerySourcePlacementBindingId> placementBindings = default,
        RelationQueryPhysicalLoweringRuleId? loweringRule = null,
        ImmutableArray<RelationQueryPhysicalPlanningDecisionId> policyDecisions = default)
    {
        Nodes = Normalize(nodes, static value => value.Value, nameof(nodes));
        Inputs = Normalize(inputs, static value => value.Value, nameof(inputs));
        Requirements = Normalize(requirements, static value => value.Value, nameof(requirements));
        CapabilityEvidence = Normalize(
            capabilityEvidence,
            static value => EvidenceKey(value),
            nameof(capabilityEvidence));
        CompositionRules = Normalize(compositionRules, static value => value.Value, nameof(compositionRules));
        OperatingBoundaries = Normalize(operatingBoundaries, static value => value.Value, nameof(operatingBoundaries));
        PlacementBindings = Normalize(placementBindings, static value => value.Value, nameof(placementBindings));
        PolicyDecisions = Normalize(policyDecisions, static value => value.Value, nameof(policyDecisions));
        if (loweringRule is { } lowering && string.IsNullOrWhiteSpace(lowering.Value))
            throw new ArgumentException("A physical lowering-rule identity cannot be default.", nameof(loweringRule));
        if (Nodes.IsDefaultOrEmpty && Inputs.IsDefaultOrEmpty && Requirements.IsDefaultOrEmpty)
            throw new ArgumentException("Physical-stage provenance requires semantic node, input, or realization attribution.", nameof(inputs));
        LoweringRule = loweringRule;
    }

    /// <summary>Canonical logical nodes contributing to the stage.</summary>
    public ImmutableArray<QueryNodeId> Nodes { get; }

    /// <summary>Compiled input-contract identities contributing to the stage.</summary>
    public ImmutableArray<RelationQueryInputId> Inputs { get; }

    /// <summary>ARI-127 realization requirements authorizing the stage.</summary>
    public ImmutableArray<RelationQueryRealizationRequirementId> Requirements { get; }

    /// <summary>Qualified source capability evidence used by the stage.</summary>
    public ImmutableArray<RelationQueryPhysicalCapabilityEvidenceReference> CapabilityEvidence { get; }

    /// <summary>Selected ARI-127 composition rules used by lowering.</summary>
    public ImmutableArray<RelationQueryCompositionRuleId> CompositionRules { get; }

    /// <summary>Validated operating boundaries enforced by the stage.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Source-placement bindings consumed by the stage.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBindingId> PlacementBindings { get; }

    /// <summary>Versioned physical lowering rule, or <see langword="null"/>.</summary>
    public RelationQueryPhysicalLoweringRuleId? LoweringRule { get; }

    /// <summary>Attributable physical-policy decisions.</summary>
    public ImmutableArray<RelationQueryPhysicalPlanningDecisionId> PolicyDecisions { get; }

    static string EvidenceKey(RelationQueryPhysicalCapabilityEvidenceReference value) => string.Concat(
        value.Source.Value.Length.ToString(CultureInfo.InvariantCulture), ":", value.Source.Value,
        value.Target.Value.Length.ToString(CultureInfo.InvariantCulture), ":", value.Target.Value,
        value.Profile.Value.Length.ToString(CultureInfo.InvariantCulture), ":", value.Profile.Value,
        value.Evidence.Value.Length.ToString(CultureInfo.InvariantCulture), ":", value.Evidence.Value);

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(value => value is null || string.IsNullOrWhiteSpace(key(value))))
            throw new ArgumentException("Provenance collections cannot contain null or default entries.", parameterName);
        var keyed = normalized.Select(value => (Value: value, Key: key(value))).ToArray();
        if (keyed.GroupBy(static item => item.Key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Provenance collections cannot contain duplicate identities.", parameterName);
        return [.. keyed.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => item.Value)];
    }
}

/// <summary>One deterministic operator in a compiled physical stage graph.</summary>
public sealed record RelationQueryPhysicalStage
{
    /// <summary>Creates and structurally validates one physical stage.</summary>
    /// <param name="id">Stable physical-stage identity.</param>
    /// <param name="kind">Closed physical operator kind.</param>
    /// <param name="dependencies">Upstream physical stages.</param>
    /// <param name="placementBinding">Placed source consumed by the stage, or <see langword="null"/>.</param>
    /// <param name="semanticInputs">Exact compiled inputs consumed or produced by the stage.</param>
    /// <param name="requestedFields">Exact compiled field inputs requested from a source.</param>
    /// <param name="batchSize">Positive lookup batch size for a batched lookup stage.</param>
    /// <param name="provenance">Complete semantic, realization, placement, and policy attribution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The stage shape is incompatible with its operator kind or a collection conflicts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> or <paramref name="batchSize"/> is invalid.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalStage(
        RelationQueryPhysicalStageId id,
        RelationQueryPhysicalStageKind kind,
        ImmutableArray<RelationQueryPhysicalStageId> dependencies,
        RelationQuerySourcePlacementBindingId? placementBinding,
        ImmutableArray<RelationQueryInputId> semanticInputs,
        ImmutableArray<RelationQueryInputId> requestedFields,
        long? batchSize,
        RelationQueryPhysicalStageProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A physical stage requires an identity.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported physical-stage kind.");
        Id = id;
        Kind = kind;
        Dependencies = Normalize(dependencies, static value => value.Value, nameof(dependencies));
        SemanticInputs = Normalize(semanticInputs, static value => value.Value, nameof(semanticInputs));
        RequestedFields = Normalize(requestedFields, static value => value.Value, nameof(requestedFields));
        if (placementBinding is { } placed && string.IsNullOrWhiteSpace(placed.Value))
            throw new ArgumentException("A placement-binding identity cannot be default.", nameof(placementBinding));
        if (batchSize is not null)
            RelationQuerySourcePlacementLimits.RequireLimit(batchSize.Value, nameof(batchSize));
        PlacementBinding = placementBinding;
        BatchSize = batchSize;
        Provenance = Guard.RequireNotNull(provenance);
        ValidateShape();
    }

    /// <summary>Stable physical-stage identity.</summary>
    public RelationQueryPhysicalStageId Id { get; }

    /// <summary>Closed physical operator kind.</summary>
    public RelationQueryPhysicalStageKind Kind { get; }

    /// <summary>Upstream physical stages in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryPhysicalStageId> Dependencies { get; }

    /// <summary>Placed source consumed by the stage, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Exact compiled inputs consumed or produced by the stage.</summary>
    public ImmutableArray<RelationQueryInputId> SemanticInputs { get; }

    /// <summary>Exact compiled field inputs requested from a source.</summary>
    public ImmutableArray<RelationQueryInputId> RequestedFields { get; }

    /// <summary>Positive lookup batch size, or <see langword="null"/> for a non-batched stage.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? BatchSize { get; }

    /// <summary>Complete semantic, realization, placement, and policy attribution.</summary>
    public RelationQueryPhysicalStageProvenance Provenance { get; }

    void ValidateShape()
    {
        var requiresPlacement = Kind is RelationQueryPhysicalStageKind.SuppliedInput
            or RelationQueryPhysicalStageKind.SourceRead
            or RelationQueryPhysicalStageKind.BatchedIdentityLookup
            or RelationQueryPhysicalStageKind.BatchedPredicateLookup;
        if (requiresPlacement != (PlacementBinding is not null))
            throw new ArgumentException("The physical-stage kind has incompatible source-placement attribution.", nameof(PlacementBinding));
        var isBatched = Kind is RelationQueryPhysicalStageKind.BatchedIdentityLookup
            or RelationQueryPhysicalStageKind.BatchedPredicateLookup;
        if (isBatched != (BatchSize is not null))
            throw new ArgumentException("Only a batched lookup stage requires a batch size.", nameof(BatchSize));

        var expectedDependencies = Kind switch
        {
            RelationQueryPhysicalStageKind.SuppliedInput or RelationQueryPhysicalStageKind.SourceRead => 0,
            RelationQueryPhysicalStageKind.LocalCorrelation => 2,
            RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly => -1,
            _ => 1
        };
        if (expectedDependencies >= 0 && Dependencies.Length != expectedDependencies)
            throw new ArgumentException($"Physical stage '{Kind}' requires {expectedDependencies} dependencies.", nameof(Dependencies));
        if (Kind == RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly && Dependencies.IsDefaultOrEmpty)
            throw new ArgumentException("Runtime-evidence assembly requires at least one dependency.", nameof(Dependencies));
        if (Kind == RelationQueryPhysicalStageKind.ExactFieldProjection && RequestedFields.IsDefaultOrEmpty)
            throw new ArgumentException("An exact field-projection stage requires selected compiled fields.", nameof(RequestedFields));
        if (Kind is not (RelationQueryPhysicalStageKind.SourceRead
                or RelationQueryPhysicalStageKind.ExactFieldProjection
                or RelationQueryPhysicalStageKind.BatchedIdentityLookup
                or RelationQueryPhysicalStageKind.BatchedPredicateLookup)
            && !RequestedFields.IsDefaultOrEmpty)
            throw new ArgumentException("This physical-stage kind cannot request source fields.", nameof(RequestedFields));
    }

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(value => string.IsNullOrWhiteSpace(key(value))))
            throw new ArgumentException("Physical-stage collections cannot contain default identities.", parameterName);
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("Physical-stage collections cannot contain duplicates.", parameterName);
        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}

/// <summary>Structured physical-planning diagnostic.</summary>
public sealed record RelationQueryPhysicalPlanningDiagnostic
{
    /// <summary>Creates an attributable physical-planning diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Effective diagnostic severity.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="stage">Affected physical stage, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected placement binding, or <see langword="null"/>.</param>
    /// <param name="requirement">Affected realization requirement, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required string or supplied identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalPlanningDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryInputId? input = null,
        RelationQueryPhysicalStageId? stage = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        RelationQueryRealizationRequirementId? requirement = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        RequireOptional(input?.Value, nameof(input));
        RequireOptional(stage?.Value, nameof(stage));
        RequireOptional(placementBinding?.Value, nameof(placementBinding));
        RequireOptional(requirement?.Value, nameof(requirement));
        Severity = severity;
        Input = input;
        Stage = stage;
        PlacementBinding = placementBinding;
        Requirement = requirement;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Effective diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected physical stage, or <see langword="null"/>.</summary>
    public RelationQueryPhysicalStageId? Stage { get; }

    /// <summary>Affected placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected realization requirement, or <see langword="null"/>.</summary>
    public RelationQueryRealizationRequirementId? Requirement { get; }

    static void RequireOptional(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional diagnostic identity cannot be default.", parameterName);
    }
}

/// <summary>Stable machine-readable physical-planning diagnostic codes.</summary>
public static class RelationQueryPhysicalPlanningDiagnosticCodes
{
    /// <summary>A required source placement is missing.</summary>
    public const string PlacementMissing = "REL2101";

    /// <summary>Several source placements conflict or remain ambiguous.</summary>
    public const string PlacementAmbiguous = "REL2102";

    /// <summary>A placement or source profile is stale or incompatible.</summary>
    public const string PlacementMismatch = "REL2103";

    /// <summary>The supplied realization report is invalid, unavailable, or stale.</summary>
    public const string RealizationInvalid = "REL2104";

    /// <summary>Required target or source capability evidence is unavailable.</summary>
    public const string CapabilityEvidenceMissing = "REL2105";

    /// <summary>No registered physical lowering implements the selected realization.</summary>
    public const string LoweringUnavailable = "REL2106";

    /// <summary>A required operating boundary is absent or unvalidated.</summary>
    public const string OperatingBoundaryInvalid = "REL2107";

    /// <summary>The proposed physical plan would perform unbounded local work.</summary>
    public const string LocalWorkUnbounded = "REL2108";

    /// <summary>Physical stage provenance is incomplete or inconsistent.</summary>
    public const string StageProvenanceInvalid = "REL2109";

    /// <summary>Physical-planning policy is invalid or rejects every exact plan.</summary>
    public const string PolicyInvalid = "REL2110";

    /// <summary>A physical source reader does not match its compiled source instance.</summary>
    public const string SourceReaderMismatch = "REL2111";

    /// <summary>A cross-source join cannot be lowered exactly within declared bounds.</summary>
    public const string CrossSourceJoinUnsupported = "REL2112";
}

/// <summary>Successful deterministic physical plan for one exact semantic and realization input.</summary>
public sealed class CompiledRelationQueryPhysicalPlan
{
    /// <summary>Current portable physical-plan schema version.</summary>
    public const string CurrentSchemaVersion = "relation-query-physical-plan/v1";

    /// <summary>Creates a structurally validated compiled physical plan.</summary>
    /// <param name="schemaVersion">Portable physical-plan schema version.</param>
    /// <param name="plan">Exact semantic compiled-plan reference.</param>
    /// <param name="realization">Exact ARI-127 realization-report fingerprint.</param>
    /// <param name="placement">Exact source-placement artifact.</param>
    /// <param name="policy">Exact physical-planning policy.</param>
    /// <param name="stages">Complete physical stage graph.</param>
    /// <param name="terminal">Reference-interpreter terminal stage.</param>
    /// <param name="diagnostics">Non-error diagnostics retained with the successful plan.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentException">An input is stale, a graph invariant fails, diagnostics contain errors, or the fingerprint is stale.</exception>
    /// <exception cref="ArgumentNullException">A required string or object is <see langword="null"/>.</exception>
    [JsonConstructor]
    public CompiledRelationQueryPhysicalPlan(
        string schemaVersion,
        RelationQueryCompiledPlanReference plan,
        RelationQueryRealizationFingerprint realization,
        RelationQuerySourcePlacement placement,
        RelationQueryPhysicalPlanningPolicy policy,
        ImmutableArray<RelationQueryPhysicalStage> stages,
        RelationQueryPhysicalStageId terminal,
        ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> diagnostics = default,
        RelationQueryPhysicalPlanFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported physical-plan schema version '{SchemaVersion}'.", nameof(schemaVersion));
        Plan = Guard.RequireNotNull(plan);
        Realization = Guard.RequireNotNull(realization);
        Placement = Guard.RequireNotNull(placement);
        Policy = Guard.RequireNotNull(policy);
        if (!SamePlan(Plan, Placement.Plan))
            throw new ArgumentException("Physical placement does not belong to the compiled plan.", nameof(placement));
        var normalizedStages = stages.IsDefault ? [] : stages;
        if (normalizedStages.IsDefaultOrEmpty || normalizedStages.Any(static stage => stage is null))
            throw new ArgumentException("A physical plan requires non-null stages.", nameof(stages));
        if (normalizedStages.GroupBy(static stage => stage.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Physical stages cannot repeat an identity.", nameof(stages));
        Stages = [.. normalizedStages.OrderBy(static stage => stage.Id.Value, StringComparer.Ordinal)];
        EvaluationOrder = ValidateGraph(Stages);
        if (string.IsNullOrWhiteSpace(terminal.Value))
            throw new ArgumentException("A physical plan requires a terminal identity.", nameof(terminal));
        var terminals = Stages.Where(static stage => stage.Kind == RelationQueryPhysicalStageKind.ReferenceInterpreterTerminal).ToArray();
        if (terminals.Length != 1 || terminals[0].Id != terminal)
            throw new ArgumentException("A physical plan requires exactly one declared reference-interpreter terminal.", nameof(terminal));
        if (Stages.Count(static stage => stage.Kind == RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly) != 1)
            throw new ArgumentException("A physical plan requires exactly one runtime-evidence assembly stage.", nameof(stages));
        var placementIds = Placement.Bindings.Select(static binding => binding.Id).ToHashSet();
        if (Stages.Any(stage => stage.PlacementBinding is { } binding && !placementIds.Contains(binding)))
            throw new ArgumentException("A physical stage references an unknown placement binding.", nameof(stages));
        if (Stages.SelectMany(static stage => stage.Provenance.PlacementBindings)
            .Any(binding => !placementIds.Contains(binding)))
            throw new ArgumentException("Physical-stage provenance references an unknown placement binding.", nameof(stages));
        if (Stages.Any(stage => stage.PlacementBinding is { } binding
            && !stage.Provenance.PlacementBindings.Contains(binding)))
            throw new ArgumentException("A source-backed physical stage must attribute its placement binding.", nameof(stages));
        if (Placement.Bindings.Any(static binding => binding.Partition is not null))
            throw new ArgumentException("The v1 physical plan cannot preserve partition selectors.", nameof(placement));
        if (Stages.SelectMany(static stage => stage.SemanticInputs.Concat(stage.RequestedFields))
            .Any(input => !Plan.Inputs.Contains(input)))
            throw new ArgumentException("A physical stage references an input absent from the compiled plan.", nameof(stages));
        if (Stages.SelectMany(static stage => stage.Provenance.Inputs).Any(input => !Plan.Inputs.Contains(input)))
            throw new ArgumentException("Physical-stage provenance references an input absent from the compiled plan.", nameof(stages));
        if (Stages.Any(stage => stage.SemanticInputs.Concat(stage.RequestedFields)
            .Any(input => !stage.Provenance.Inputs.Contains(input))))
            throw new ArgumentException("Every stage input must be retained in its semantic provenance.", nameof(stages));
        ValidateCapabilityEvidence(Stages, Placement, Policy);
        ValidateLoweringSelections(Stages, Policy);
        if (Stages.Any(stage => stage.BatchSize > Policy.MaximumBatchSize))
            throw new ArgumentException("A stage batch size exceeds physical-planning policy.", nameof(stages));
        Diagnostics = NormalizeDiagnostics(diagnostics);
        if (Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("An executable physical plan cannot retain error diagnostics.", nameof(diagnostics));
        Terminal = terminal;

        var computed = RelationQueryPhysicalPlanFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
            throw new ArgumentException("The physical-plan fingerprint does not match normalized content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    static bool SamePlan(RelationQueryCompiledPlanReference left, RelationQueryCompiledPlanReference right) =>
        string.Equals(left.CompilerProfile, right.CompilerProfile, StringComparison.Ordinal)
        && string.Equals(left.DefinitionSchemaVersion, right.DefinitionSchemaVersion, StringComparison.Ordinal)
        && Equals(left.DefinitionFingerprint, right.DefinitionFingerprint)
        && Equals(left.ShapeSnapshotsFingerprint, right.ShapeSnapshotsFingerprint)
        && Equals(left.RelationshipCatalogFingerprint, right.RelationshipCatalogFingerprint)
        && Equals(left.DemandFingerprint, right.DemandFingerprint)
        && left.Inputs.SequenceEqual(right.Inputs);

    static void ValidateCapabilityEvidence(
        ImmutableArray<RelationQueryPhysicalStage> stages,
        RelationQuerySourcePlacement placement,
        RelationQueryPhysicalPlanningPolicy policy)
    {
        var sources = placement.SourceInstances.ToDictionary(static source => source.Id);
        var bindings = placement.Bindings.ToDictionary(static binding => binding.Id);
        foreach (var stage in stages)
        foreach (var reference in stage.Provenance.CapabilityEvidence)
        {
            RelationQueryTargetCapabilityEvidence[] matchingEvidence = sources.TryGetValue(
                reference.Source,
                out var candidateSource)
                ? [.. candidateSource.TargetProfile.Capabilities.Where(evidence => evidence.Id == reference.Evidence)]
                : [];
            if (!sources.TryGetValue(reference.Source, out var source)
                || source.TargetProfile.Target != reference.Target
                || source.TargetProfile.Id != reference.Profile
                || matchingEvidence.Length != 1)
            {
                throw new ArgumentException(
                    "Physical-stage provenance references capability evidence outside the placed source profile.",
                    nameof(stages));
            }
            var evidence = matchingEvidence[0];

            var binding = stage.Provenance.PlacementBindings
                .Select(id => bindings.GetValueOrDefault(id))
                .SingleOrDefault(candidate => candidate?.Source == source.Id);
            var analysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(source.TargetProfile);
            if (binding is null
                || !evidence.OperatingBoundaries.All(stage.Provenance.OperatingBoundaries.Contains)
                || evidence.Capability is OperatingBoundaryValidationRelationQueryCapability validation
                    && !stage.Provenance.OperatingBoundaries.Contains(validation.Boundary)
                || !RelationQueryPhysicalBoundaryEvaluator.IsCompatible(
                    evidence,
                    analysis.Boundaries,
                    source,
                    binding,
                    policy,
                    stage.BatchSize))
            {
                throw new ArgumentException(
                    "Physical-stage capability evidence is missing a satisfied operating-boundary proof.",
                    nameof(stages));
            }
        }
    }

    static void ValidateLoweringSelections(
        ImmutableArray<RelationQueryPhysicalStage> stages,
        RelationQueryPhysicalPlanningPolicy policy)
    {
        var selections = policy.LoweringSelections.ToDictionary(static selection => selection.CompositionRule);
        foreach (var provenance in stages.Select(static stage => stage.Provenance))
        {
            foreach (var rule in provenance.CompositionRules)
            {
                if (!selections.TryGetValue(rule, out var selection)
                    || provenance.LoweringRule is not { } lowering
                    || selection.PhysicalLowering != lowering)
                {
                    throw new ArgumentException(
                        "A physical stage using a composition rule must cite its policy-selected lowering.",
                        nameof(stages));
                }
            }
        }
    }

    /// <summary>Portable physical-plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact semantic compiled-plan reference.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact ARI-127 realization-report fingerprint.</summary>
    public RelationQueryRealizationFingerprint Realization { get; }

    /// <summary>Exact source-placement artifact.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    /// <summary>Exact physical-planning policy.</summary>
    public RelationQueryPhysicalPlanningPolicy Policy { get; }

    /// <summary>Physical stages in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryPhysicalStage> Stages { get; }

    /// <summary>Dependency-first deterministic stage evaluation order.</summary>
    public ImmutableArray<RelationQueryPhysicalStageId> EvaluationOrder { get; }

    /// <summary>Reference-interpreter terminal stage.</summary>
    public RelationQueryPhysicalStageId Terminal { get; }

    /// <summary>Non-error diagnostics retained with the successful plan.</summary>
    public ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> Diagnostics { get; }

    /// <summary>Deterministic identity of the compiled physical plan.</summary>
    public RelationQueryPhysicalPlanFingerprint Fingerprint { get; }

    static ImmutableArray<RelationQueryPhysicalStageId> ValidateGraph(ImmutableArray<RelationQueryPhysicalStage> stages)
    {
        var ids = stages.Select(static stage => stage.Id).ToHashSet();
        if (stages.SelectMany(static stage => stage.Dependencies).Any(dependency => !ids.Contains(dependency)))
            throw new ArgumentException("Every physical-stage dependency must reference a declared stage.", nameof(stages));
        if (stages.Any(stage => stage.Dependencies.Contains(stage.Id)))
            throw new ArgumentException("A physical stage cannot depend on itself.", nameof(stages));

        var remaining = stages.ToDictionary(static stage => stage.Id, static stage => stage.Dependencies.Length);
        Dictionary<RelationQueryPhysicalStageId, List<RelationQueryPhysicalStageId>> consumers = [];
        foreach (var stage in stages)
            foreach (var dependency in stage.Dependencies)
            {
                if (!consumers.TryGetValue(dependency, out var found))
                    consumers.Add(dependency, found = []);
                found.Add(stage.Id);
            }
        SortedSet<RelationQueryPhysicalStageId> ready = new(
            remaining.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            Comparer<RelationQueryPhysicalStageId>.Create(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value)));
        ImmutableArray<RelationQueryPhysicalStageId>.Builder order = ImmutableArray.CreateBuilder<RelationQueryPhysicalStageId>(stages.Length);
        while (ready.Count != 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            order.Add(next);
            if (!consumers.TryGetValue(next, out var downstream))
                continue;
            foreach (var consumer in downstream.OrderBy(static value => value.Value, StringComparer.Ordinal))
            {
                if (--remaining[consumer] == 0)
                    ready.Add(consumer);
            }
        }
        if (order.Count != stages.Length)
            throw new ArgumentException("The physical-stage graph must be acyclic.", nameof(stages));
        return order.ToImmutable();
    }

    static ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> NormalizeDiagnostics(
        ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Physical-plan diagnostics cannot contain null entries.", nameof(diagnostics));
        return
        [
            .. normalized.OrderBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Stage?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
        ];
    }
}

/// <summary>Overall outcome of deterministic physical planning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryPhysicalPlanningStatus
{
    /// <summary>A complete executable physical plan was produced.</summary>
    Planned = 0,

    /// <summary>No exact bounded physical plan is available.</summary>
    Unavailable = 1,

    /// <summary>Invalid or conflicting planning inputs prevent a trustworthy result.</summary>
    Invalid = 2
}

/// <summary>Structured result of attempting deterministic physical planning.</summary>
public sealed class RelationQueryPhysicalPlanningResult
{
    /// <summary>Creates a physical-planning result.</summary>
    /// <param name="status">Overall planning outcome.</param>
    /// <param name="plan">Compiled plan for <see cref="RelationQueryPhysicalPlanningStatus.Planned"/>.</param>
    /// <param name="diagnostics">Structured planning diagnostics.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Status, plan, and diagnostics are inconsistent or diagnostics contain null.</exception>
    [JsonConstructor]
    public RelationQueryPhysicalPlanningResult(
        RelationQueryPhysicalPlanningStatus status,
        CompiledRelationQueryPhysicalPlan? plan,
        ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported physical-planning status.");
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Planning diagnostics cannot contain null entries.", nameof(diagnostics));
        if ((status == RelationQueryPhysicalPlanningStatus.Planned) != (plan is not null))
            throw new ArgumentException("Only a planned result can contain a compiled physical plan.", nameof(plan));
        if (status == RelationQueryPhysicalPlanningStatus.Planned
            && normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("A planned result cannot contain error diagnostics.", nameof(diagnostics));
        if (status != RelationQueryPhysicalPlanningStatus.Planned
            && !normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("An unavailable or invalid result requires an error diagnostic.", nameof(diagnostics));
        Status = status;
        Plan = plan;
        Diagnostics = [.. normalized.OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)];
    }

    /// <summary>Overall planning outcome.</summary>
    public RelationQueryPhysicalPlanningStatus Status { get; }

    /// <summary>Compiled physical plan, or <see langword="null"/> when planning failed.</summary>
    public CompiledRelationQueryPhysicalPlan? Plan { get; }

    /// <summary>Structured planning diagnostics.</summary>
    public ImmutableArray<RelationQueryPhysicalPlanningDiagnostic> Diagnostics { get; }

    /// <summary>Whether planning produced an executable physical plan.</summary>
    [JsonIgnore]
    public bool IsSuccessful => Status == RelationQueryPhysicalPlanningStatus.Planned && Plan is not null;
}
