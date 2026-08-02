using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Structured diagnostics emitted while proving one atomic routing-manifest authority.</summary>
public static class MaterializationAtomicRoutingManifestDiagnosticCodes
{
    /// <summary>The selected plan set does not request globally atomic visibility.</summary>
    public const string PolicyMismatch = "materialization.routingManifest.atomic.policyMismatch";

    /// <summary>No routing-manifest capability evidence was supplied.</summary>
    public const string CapabilityUnavailable = "materialization.routingManifest.atomic.capabilityUnavailable";

    /// <summary>Capability evidence names another transaction authority.</summary>
    public const string AuthorityMismatch = "materialization.routingManifest.atomic.authorityMismatch";

    /// <summary>Capability evidence covers another placement scope.</summary>
    public const string ScopeMismatch = "materialization.routingManifest.atomic.scopeMismatch";

    /// <summary>Capability evidence does not cover both read and incremental-write routes.</summary>
    public const string RoutingSettingsIncomplete = "materialization.routingManifest.atomic.routingSettingsIncomplete";

    /// <summary>Capability evidence omits an atomicity, fencing, replay, or reconciliation guarantee.</summary>
    public const string GuaranteeUnavailable = "materialization.routingManifest.atomic.guaranteeUnavailable";

    /// <summary>The capability is composed from independently committing authorities.</summary>
    public const string RealizationWeaker = "materialization.routingManifest.atomic.realizationWeaker";
}

static class MaterializationAtomicRoutingManifestSemantics
{
    internal const string CapabilitySchema = "cohesive-materialization-routing-manifest-capability/v1";
    internal const string RequirementSchema = "cohesive-materialization-routing-manifest-requirement/v1";
    internal const string RealizationSchema = "cohesive-materialization-routing-manifest-realization/v1";
    internal const string EntrySchema = "cohesive-materialization-routing-manifest-entry/v1";
    internal const string SnapshotSchema = "cohesive-materialization-routing-manifest-snapshot/v1";
    internal const string RequestSchema = "cohesive-materialization-routing-manifest-request/v1";
    internal const string ReceiptSchema = "cohesive-materialization-routing-manifest-receipt/v1";
    internal const string ResultSchema = "cohesive-materialization-routing-manifest-result/v1";
    internal const string CommandDomain = "cohesive-materialization-routing-manifest-command/v1";
    internal const string ConfigurationAuthorityPrefix =
        "cohesive.storage/materialization-routing-manifest/configuration/v1/plan-set/";

    internal static ImmutableArray<string> RequiredSettings { get; } =
    [
        MaterializationBackendRoutingSettingNames.ReadTarget,
        MaterializationBackendRoutingSettingNames.WriteTarget
    ];

    internal static ImmutableArray<MaterializationGuaranteeKind> RequiredGuarantees { get; } =
    [
        MaterializationGuaranteeKind.Reconciliation,
        MaterializationGuaranteeKind.IdempotentWrite,
        MaterializationGuaranteeKind.AtomicPromotion,
        MaterializationGuaranteeKind.FencedPromotion
    ];

    internal static ImmutableArray<MaterializationPlacementSliceReference> NormalizeScope(
        ImmutableArray<MaterializationPlacementSliceReference> scope,
        string parameterName)
    {
        var normalized = scope.IsDefault ? [] : scope;
        if (normalized.IsEmpty
            || normalized.Any(static slice => slice is null)
            || normalized.GroupBy(static slice => slice.Id).Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "A routing-manifest scope requires one or more distinct non-null placement slices.",
                parameterName);
        }
        return [.. normalized.OrderBy(static slice => slice.Id.Value, StringComparer.Ordinal)];
    }

    internal static ImmutableArray<string> NormalizeSettings(
        ImmutableArray<string> settings,
        string parameterName)
    {
        var normalized = MaterializationCapabilityOrdering.NormalizeStrings(
            settings.IsDefault ? [] : settings,
            parameterName,
            requireNonEmpty: true);
        if (normalized.Any(setting => !RequiredSettings.Contains(setting, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Routing-manifest settings must be known read or write routes.", parameterName);
        }

        return normalized;
    }

    internal static bool SamePlanSet(
        MaterializationRebuildPlanSetReference left,
        MaterializationRebuildPlanSetReference right) =>
        left.PlanSet == right.PlanSet;

    internal static bool SameScope(
        IEnumerable<MaterializationPlacementSliceReference> left,
        IEnumerable<MaterializationPlacementSliceReference> right) =>
        left.Select(static slice => slice.Fingerprint)
            .SequenceEqual(right.Select(static slice => slice.Fingerprint));

    internal static bool SameEntries(
        ImmutableArray<MaterializationRoutingManifestEntry> left,
        ImmutableArray<MaterializationRoutingManifestEntry> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!MaterializationContract.CanonicalEquals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Canonical exact atomic-routing requirement derived from one rebuild plan set.</summary>
public sealed record MaterializationAtomicRoutingManifestRequirement
{
    /// <summary>Current portable requirement schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.RequirementSchema;

    /// <summary>Creates one exact complete-manifest atomicity demand.</summary>
    /// <param name="schemaVersion">Exact portable requirement schema.</param>
    /// <param name="planSet">Exact plan set whose complete route publication is required.</param>
    /// <param name="authority">Selected single transaction authority.</param>
    /// <param name="scope">Complete exact placement scope.</param>
    /// <param name="routingSettings">Required read and incremental-write routing settings.</param>
    /// <param name="guarantees">Required atomicity, fencing, replay, and reconciliation guarantees.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, authority, scope, settings, or guarantees are incomplete.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestRequirement(
        string schemaVersion,
        MaterializationRebuildPlanSetReference planSet,
        string authority,
        ImmutableArray<MaterializationPlacementSliceReference> scope,
        ImmutableArray<string> routingSettings,
        ImmutableArray<MaterializationGuaranteeKind> guarantees)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic manifest requirement schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        Authority = MaterializationContract.RequireUnicodeIdentity(authority, nameof(authority));
        Scope = MaterializationAtomicRoutingManifestSemantics.NormalizeScope(scope, nameof(scope));
        RoutingSettings = MaterializationAtomicRoutingManifestSemantics.NormalizeSettings(
            routingSettings,
            nameof(routingSettings));
        Guarantees = MaterializationCapabilityOrdering.NormalizeGuarantees(guarantees, nameof(guarantees));
        if (!RoutingSettings.SequenceEqual(MaterializationAtomicRoutingManifestSemantics.RequiredSettings)
            || !Guarantees.SequenceEqual(MaterializationAtomicRoutingManifestSemantics.RequiredGuarantees))
        {
            throw new ArgumentException(
                "An atomic manifest requirement must demand both routing settings and every required guarantee.",
                nameof(guarantees));
        }
    }

    /// <summary>Exact portable requirement schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact plan set whose complete route publication is required.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Selected single transaction authority.</summary>
    public string Authority { get; }

    /// <summary>Complete exact placement scope in canonical slice order.</summary>
    public ImmutableArray<MaterializationPlacementSliceReference> Scope { get; }

    /// <summary>Required read and incremental-write routing settings.</summary>
    public ImmutableArray<string> RoutingSettings { get; }

    /// <summary>Required atomicity, fencing, replay, and reconciliation guarantees.</summary>
    public ImmutableArray<MaterializationGuaranteeKind> Guarantees { get; }

    /// <summary>Derives the canonical atomic requirement for one exact plan set and selected authority.</summary>
    /// <param name="planSet">Canonical plan set requesting atomic visibility.</param>
    /// <param name="authority">Selected transaction authority.</param>
    /// <returns>The exact requirement over every plan-set placement slice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is invalid.</exception>
    public static MaterializationAtomicRoutingManifestRequirement FromPlanSet(
        MaterializationRebuildPlanSet planSet,
        string authority)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        return new(
            schemaVersion: CurrentSchemaVersion,
            planSet: MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            authority: authority,
            scope: [.. planSet.LeafPlans.Select(static binding => binding.Slice)],
            routingSettings: MaterializationAtomicRoutingManifestSemantics.RequiredSettings,
            guarantees: MaterializationAtomicRoutingManifestSemantics.RequiredGuarantees);
    }
}

/// <summary>Attributable capability evidence from one routing-manifest transaction authority.</summary>
public sealed record MaterializationAtomicRoutingManifestCapability
{
    /// <summary>Current portable capability schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.CapabilitySchema;

    /// <summary>Creates one capability assertion.</summary>
    /// <param name="schemaVersion">Exact portable capability schema.</param>
    /// <param name="authority">Stable transaction-authority identity.</param>
    /// <param name="scope">Placement slices the one authority can transact together.</param>
    /// <param name="routingSettings">Read and/or write routing settings covered by the transaction.</param>
    /// <param name="guarantees">Semantic guarantees preserved by the authority.</param>
    /// <param name="realization">How the authority realizes the capability.</param>
    /// <param name="evidenceReferences">Adapter, deployment, compiler, or override evidence.</param>
    /// <param name="provenance">Producer and source attribution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema, authority, scope, settings, guarantees, or evidence are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unavailable or unknown.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestCapability(
        string schemaVersion,
        string authority,
        ImmutableArray<MaterializationPlacementSliceReference> scope,
        ImmutableArray<string> routingSettings,
        ImmutableArray<MaterializationGuaranteeKind> guarantees,
        CapabilityRealizationKind realization,
        ImmutableArray<string> evidenceReferences,
        ExecutionProvenance provenance)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic manifest capability schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        Authority = MaterializationContract.RequireUnicodeIdentity(authority, nameof(authority));
        Scope = MaterializationAtomicRoutingManifestSemantics.NormalizeScope(scope, nameof(scope));
        RoutingSettings = MaterializationAtomicRoutingManifestSemantics.NormalizeSettings(
            routingSettings,
            nameof(routingSettings));
        Guarantees = MaterializationCapabilityOrdering.NormalizeGuarantees(guarantees, nameof(guarantees));
        if (!Enum.IsDefined(realization)
            || realization is CapabilityRealizationKind.Unavailable or CapabilityRealizationKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Capability evidence must be available and classified.");
        }
        EvidenceReferences = MaterializationCapabilityOrdering.NormalizeStrings(
            evidenceReferences.IsDefault ? [] : evidenceReferences,
            nameof(evidenceReferences),
            requireNonEmpty: true);
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Realization = realization;
    }

    /// <summary>Exact portable capability schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable single transaction-authority identity.</summary>
    public string Authority { get; }

    /// <summary>Placement slices the authority can transact together.</summary>
    public ImmutableArray<MaterializationPlacementSliceReference> Scope { get; }

    /// <summary>Read and/or write routing settings covered by the transaction.</summary>
    public ImmutableArray<string> RoutingSettings { get; }

    /// <summary>Semantic guarantees preserved by the authority.</summary>
    public ImmutableArray<MaterializationGuaranteeKind> Guarantees { get; }

    /// <summary>How the one authority realizes the capability.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Attributable adapter, deployment, compiler, or override evidence.</summary>
    public ImmutableArray<string> EvidenceReferences { get; }

    /// <summary>Producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Exact evidence-backed selection of one atomic manifest authority for one plan set.</summary>
public sealed record MaterializationAtomicRoutingManifestRealization
{
    /// <summary>Current portable realization schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.RealizationSchema;

    /// <summary>Creates one exact matched realization.</summary>
    /// <param name="schemaVersion">Exact portable realization schema.</param>
    /// <param name="requirement">Canonical complete-manifest requirement.</param>
    /// <param name="capability">Selected single-authority capability evidence.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Schema or selected evidence does not satisfy the requirement.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestRealization(
        string schemaVersion,
        MaterializationAtomicRoutingManifestRequirement requirement,
        MaterializationAtomicRoutingManifestCapability capability)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic manifest realization schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        if (!MaterializationAtomicRoutingManifestCompiler.Satisfies(requirement, capability))
        {
            throw new ArgumentException("Selected capability evidence does not satisfy the exact atomic manifest requirement.", nameof(capability));
        }
    }

    /// <summary>Exact portable realization schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical complete-manifest requirement.</summary>
    public MaterializationAtomicRoutingManifestRequirement Requirement { get; }

    /// <summary>Selected single-authority capability evidence.</summary>
    public MaterializationAtomicRoutingManifestCapability Capability { get; }
}

/// <summary>Matches one exact atomic manifest requirement to attributable authority evidence.</summary>
public static class MaterializationAtomicRoutingManifestCompiler
{
    /// <summary>Compiles one evidence-backed atomic manifest realization.</summary>
    /// <param name="planSet">Canonical plan set being realized.</param>
    /// <param name="requirement">Exact atomic capability requirement.</param>
    /// <param name="capability">Candidate authority evidence, or null when unavailable.</param>
    /// <returns>An exact realization or deterministic structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> or <paramref name="requirement"/> is null.</exception>
    public static MaterializationRebuildPlanningResult<MaterializationAtomicRoutingManifestRealization> Compile(
        MaterializationRebuildPlanSet planSet,
        MaterializationAtomicRoutingManifestRequirement requirement,
        MaterializationAtomicRoutingManifestCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(requirement);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var expectedPlanSet = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        var expectedScope = planSet.LeafPlans.Select(static binding => binding.Slice).ToArray();
        if (planSet.Promotion.Mode != MaterializationRebuildPromotionMode.AtomicVisibility)
        {
            diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.PolicyMismatch, requirement, null, "AtomicVisibility", planSet.Promotion.Mode.ToString()));
        }

        if (!MaterializationAtomicRoutingManifestSemantics.SamePlanSet(requirement.PlanSet, expectedPlanSet)
            || !MaterializationAtomicRoutingManifestSemantics.SameScope(requirement.Scope, expectedScope))
        {
            diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.ScopeMismatch, requirement, null, "exact plan-set scope", "detached requirement"));
        }

        if (capability is null)
        {
            diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.CapabilityUnavailable, requirement, null, requirement.Authority, "not supplied"));
        }
        else
        {
            if (!string.Equals(capability.Authority, requirement.Authority, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.AuthorityMismatch, requirement, capability, requirement.Authority, capability.Authority));
            }

            if (!MaterializationAtomicRoutingManifestSemantics.SameScope(
                    capability.Scope,
                    requirement.Scope))
            {
                diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.ScopeMismatch, requirement, capability, "complete exact placement scope", string.Join(',', capability.Scope.Select(static slice => slice.Id.Value))));
            }

            if (requirement.RoutingSettings.Any(setting => !capability.RoutingSettings.Contains(setting, StringComparer.Ordinal)))
            {
                diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.RoutingSettingsIncomplete, requirement, capability, string.Join(',', requirement.RoutingSettings), string.Join(',', capability.RoutingSettings)));
            }

            foreach (var guarantee in requirement.Guarantees.Where(guarantee => !capability.Guarantees.Contains(guarantee)))
            {
                diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.GuaranteeUnavailable, requirement, capability, guarantee.ToString(), string.Join(',', capability.Guarantees)));
            }

            if (capability.Realization == CapabilityRealizationKind.Composed)
            {
                diagnostics.Add(Error(MaterializationAtomicRoutingManifestDiagnosticCodes.RealizationWeaker, requirement, capability, "one atomic authority", CapabilityRealizationKind.Composed.ToString()));
            }
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        if (diagnostics.Count > 0)
        {
            return new(artifact: null, diagnostics: [.. diagnostics]);
        }

        return new(
            artifact: new(
                schemaVersion: MaterializationAtomicRoutingManifestRealization.CurrentSchemaVersion,
                requirement: requirement,
                capability: capability!),
            diagnostics: []);
    }

    internal static bool Satisfies(
        MaterializationAtomicRoutingManifestRequirement requirement,
        MaterializationAtomicRoutingManifestCapability capability) =>
        string.Equals(capability.Authority, requirement.Authority, StringComparison.Ordinal)
        && MaterializationAtomicRoutingManifestSemantics.SameScope(capability.Scope, requirement.Scope)
        && requirement.RoutingSettings.All(setting => capability.RoutingSettings.Contains(setting, StringComparer.Ordinal))
        && requirement.Guarantees.All(capability.Guarantees.Contains)
        && capability.Realization != CapabilityRealizationKind.Composed;

    static DocumentValidationDiagnostic Error(
        string code,
        MaterializationAtomicRoutingManifestRequirement requirement,
        MaterializationAtomicRoutingManifestCapability? capability,
        string expected,
        string observed) =>
        new(
            code,
            DiagnosticSeverity.Error,
            "One authority must prove an atomic compare-and-swap over the complete exact read/write routing manifest.",
            "/promotion/atomicManifest",
            Evidence: new(
                stage: "materialization-atomic-routing-manifest-capability-matching",
                subject: requirement.Authority,
                sourceReferences: capability?.EvidenceReferences ?? [requirement.PlanSet.PlanSet.Value],
                expected: expected,
                observed: observed));
}

/// <summary>One exact read/write selection in a complete routing manifest.</summary>
public sealed record MaterializationRoutingManifestEntry
{
    /// <summary>Current portable manifest-entry schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.EntrySchema;

    /// <summary>Creates one uninitialized or fully initialized placement selection.</summary>
    /// <param name="schemaVersion">Exact portable entry schema.</param>
    /// <param name="placementSlice">Exact placement authority.</param>
    /// <param name="read">Exact read route, or null before initialization.</param>
    /// <param name="write">Exact incremental-write route, or null before initialization.</param>
    /// <param name="readiness">Exact candidate-readiness evidence, or null before initialization.</param>
    /// <param name="configuration">Complete routing provenance, or null before initialization.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is null.</exception>
    /// <exception cref="ArgumentException">Schema, initialization state, placement, definition, or configuration is inconsistent.</exception>
    [JsonConstructor]
    public MaterializationRoutingManifestEntry(
        string schemaVersion,
        MaterializationPlacementSliceReference placementSlice,
        MaterializationBackendGenerationReference? read,
        MaterializationBackendGenerationReference? write,
        MaterializationReadyGenerationReference? readiness,
        MaterializationBackendRoutingConfiguration? configuration)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Routing manifest entry schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
        var initialized = read is not null && write is not null && readiness is not null && configuration is not null;
        if (!initialized && (read is not null || write is not null || readiness is not null || configuration is not null)
            || initialized
            && (readiness!.PlacementSlice != placementSlice
                || readiness.Generation != read!.GenerationId
                || read.TargetId != placementSlice.Target
                || write!.TargetId != placementSlice.Target
                || read.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint
                || write!.DefinitionFingerprint != placementSlice.Materialization.DefinitionFingerprint
                || configuration!.ReadTarget != read.TargetId
                || configuration.WriteTarget != write.TargetId))
        {
            throw new ArgumentException(
                "A manifest entry must be wholly uninitialized or retain exact read/write routes and configuration.",
                nameof(read));
        }
        Read = read;
        Write = write;
        Readiness = readiness;
        Configuration = configuration;
    }

    /// <summary>Exact portable entry schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact placement authority.</summary>
    public MaterializationPlacementSliceReference PlacementSlice { get; }

    /// <summary>Exact read route, or null before initialization.</summary>
    public MaterializationBackendGenerationReference? Read { get; }

    /// <summary>Exact incremental-write route, or null before initialization.</summary>
    public MaterializationBackendGenerationReference? Write { get; }

    /// <summary>Exact candidate-readiness evidence authorizing atomic publication, or null before initialization.</summary>
    public MaterializationReadyGenerationReference? Readiness { get; }

    /// <summary>Complete routing provenance, or null before initialization.</summary>
    public MaterializationBackendRoutingConfiguration? Configuration { get; }

    /// <summary>Whether both routing settings and their provenance are initialized.</summary>
    [JsonIgnore]
    public bool IsInitialized =>
        Read is not null && Write is not null && Readiness is not null && Configuration is not null;
}

/// <summary>Complete revisioned read/write routing state owned by one manifest authority.</summary>
public sealed record MaterializationRoutingManifestSnapshot
{
    /// <summary>Current portable manifest-snapshot schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.SnapshotSchema;

    /// <summary>Creates one complete exact manifest snapshot.</summary>
    /// <param name="schemaVersion">Exact portable snapshot schema.</param>
    /// <param name="authority">Single manifest authority.</param>
    /// <param name="planSet">Exact plan-set scope.</param>
    /// <param name="revision">Manifest-wide compare-and-swap revision.</param>
    /// <param name="latestFence">Latest accepted authority fence, or null before the first command.</param>
    /// <param name="entries">Every placement entry in canonical order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is null.</exception>
    /// <exception cref="ArgumentException">Schema, authority, revision, fence, or entry coverage is invalid.</exception>
    [JsonConstructor]
    public MaterializationRoutingManifestSnapshot(
        string schemaVersion,
        string authority,
        MaterializationRebuildPlanSetReference planSet,
        MaterializationBackendRoutingRevision revision,
        MaterializationBackendRoutingFence? latestFence,
        ImmutableArray<MaterializationRoutingManifestEntry> entries)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Routing manifest snapshot schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        Authority = MaterializationContract.RequireUnicodeIdentity(authority, nameof(authority));
        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        if (revision.Ordinal > 0 && latestFence is null)
        {
            throw new ArgumentException("A committed manifest must retain its latest accepted fence.", nameof(latestFence));
        }

        var normalized = entries.IsDefault ? [] : entries;
        if (normalized.IsEmpty
            || normalized.Any(static entry => entry is null)
            || normalized.GroupBy(static entry => entry.PlacementSlice.Id).Any(static group => group.Skip(1).Any())
            || normalized.Any(entry => entry.PlacementSlice.Materialization != planSet.Request.Materialization))
        {
            throw new ArgumentException("A manifest snapshot requires complete distinct entries for its exact materialization.", nameof(entries));
        }
        Revision = revision;
        LatestFence = latestFence;
        Entries = [.. normalized.OrderBy(static entry => entry.PlacementSlice.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>Exact portable snapshot schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Single manifest authority.</summary>
    public string Authority { get; }

    /// <summary>Exact plan-set scope.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Manifest-wide compare-and-swap revision.</summary>
    public MaterializationBackendRoutingRevision Revision { get; }

    /// <summary>Latest accepted authority fence, or null before the first command.</summary>
    public MaterializationBackendRoutingFence? LatestFence { get; }

    /// <summary>Every placement entry in canonical slice order.</summary>
    public ImmutableArray<MaterializationRoutingManifestEntry> Entries { get; }
}

/// <summary>Exact durable intent for one complete-manifest atomic compare-and-swap.</summary>
public sealed record MaterializationAtomicRoutingManifestRequest
{
    /// <summary>Current portable request schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.RequestSchema;

    /// <summary>Creates one replay-stable complete-manifest transaction intent.</summary>
    /// <param name="schemaVersion">Exact portable request schema.</param>
    /// <param name="realization">Exact evidence-backed capability selection.</param>
    /// <param name="prior">Complete expected prior manifest.</param>
    /// <param name="desiredEntries">Complete desired read/write manifest.</param>
    /// <param name="fence">Manifest-authority fence.</param>
    /// <param name="commandId">Stable idempotency identity.</param>
    /// <param name="issuedAtUtc">Stable UTC issuance boundary.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Schema, authority, scope, desired routes, command, fence, or chronology is inexact.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestRequest(
        string schemaVersion,
        MaterializationAtomicRoutingManifestRealization realization,
        MaterializationRoutingManifestSnapshot prior,
        ImmutableArray<MaterializationRoutingManifestEntry> desiredEntries,
        MaterializationBackendRoutingFence fence,
        MaterializationBackendRoutingCommandId commandId,
        DateTimeOffset issuedAtUtc)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic routing manifest request schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        Realization = realization ?? throw new ArgumentNullException(nameof(realization));
        Prior = prior ?? throw new ArgumentNullException(nameof(prior));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireDefinedIdentity(commandId.Value, nameof(commandId));
        MaterializationContract.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));
        var desired = desiredEntries.IsDefault ? [] : desiredEntries;
        var scope = realization.Requirement.Scope;
        if (!MaterializationAtomicRoutingManifestSemantics.SamePlanSet(
                prior.PlanSet,
                realization.Requirement.PlanSet)
            || !string.Equals(prior.Authority, realization.Requirement.Authority, StringComparison.Ordinal)
            || !MaterializationAtomicRoutingManifestSemantics.SameScope(
                prior.Entries.Select(static entry => entry.PlacementSlice),
                scope)
            || desired.Any(static entry => entry is null || !entry.IsInitialized)
            || !MaterializationAtomicRoutingManifestSemantics.SameScope(
                desired.Select(static entry => entry.PlacementSlice),
                scope)
            || desired.Any(entry => issuedAtUtc < entry.Readiness!.ReadyAtUtc))
        {
            throw new ArgumentException(
                "An atomic manifest request must replace the complete exact capability scope with activated read/write routes.",
                nameof(desiredEntries));
        }
        DesiredEntries = desired;
        Fence = fence;
        CommandId = commandId;
        IssuedAtUtc = issuedAtUtc;
    }

    /// <summary>Exact portable request schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact evidence-backed capability selection.</summary>
    public MaterializationAtomicRoutingManifestRealization Realization { get; }

    /// <summary>Complete expected prior manifest.</summary>
    public MaterializationRoutingManifestSnapshot Prior { get; }

    /// <summary>Complete desired read/write manifest.</summary>
    public ImmutableArray<MaterializationRoutingManifestEntry> DesiredEntries { get; }

    /// <summary>Manifest-wide optimistic revision.</summary>
    [JsonIgnore]
    public MaterializationBackendRoutingRevision ExpectedRevision => Prior.Revision;

    /// <summary>Manifest-authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>Stable idempotency identity.</summary>
    public MaterializationBackendRoutingCommandId CommandId { get; }

    /// <summary>Stable UTC issuance boundary.</summary>
    public DateTimeOffset IssuedAtUtc { get; }
}

/// <summary>Durable proof of one committed complete-manifest transaction.</summary>
public sealed record MaterializationAtomicRoutingManifestReceipt
{
    /// <summary>Current portable receipt schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.ReceiptSchema;

    /// <summary>Creates one exact committed receipt.</summary>
    /// <param name="schemaVersion">Exact portable receipt schema.</param>
    /// <param name="commandId">Committed command identity.</param>
    /// <param name="authority">Committing single authority.</param>
    /// <param name="planSet">Exact plan-set scope.</param>
    /// <param name="priorRevision">Compared prior manifest revision.</param>
    /// <param name="revision">Committed manifest revision.</param>
    /// <param name="fence">Committed authority fence.</param>
    /// <param name="committedAtUtc">UTC commit boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is null.</exception>
    /// <exception cref="ArgumentException">Schema, identity, revision, fence, or chronology is invalid.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestReceipt(
        string schemaVersion,
        MaterializationBackendRoutingCommandId commandId,
        string authority,
        MaterializationRebuildPlanSetReference planSet,
        MaterializationBackendRoutingRevision priorRevision,
        MaterializationBackendRoutingRevision revision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset committedAtUtc)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic routing manifest receipt schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        MaterializationContract.RequireDefinedIdentity(commandId.Value, nameof(commandId));
        Authority = MaterializationContract.RequireUnicodeIdentity(authority, nameof(authority));
        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        if (priorRevision.Ordinal == long.MaxValue || revision.Ordinal != priorRevision.Ordinal + 1)
        {
            throw new ArgumentException("An atomic manifest commit must advance the exact prior revision once.", nameof(revision));
        }

        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        CommandId = commandId;
        PriorRevision = priorRevision;
        Revision = revision;
        Fence = fence;
        CommittedAtUtc = committedAtUtc;
    }

    /// <summary>Exact portable receipt schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Committed command identity.</summary>
    public MaterializationBackendRoutingCommandId CommandId { get; }

    /// <summary>Committing single authority.</summary>
    public string Authority { get; }

    /// <summary>Exact plan-set scope.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Compared prior manifest revision.</summary>
    public MaterializationBackendRoutingRevision PriorRevision { get; }

    /// <summary>Committed manifest revision.</summary>
    public MaterializationBackendRoutingRevision Revision { get; }

    /// <summary>Committed authority fence.</summary>
    public MaterializationBackendRoutingFence Fence { get; }

    /// <summary>UTC commit boundary.</summary>
    public DateTimeOffset CommittedAtUtc { get; }
}

/// <summary>Observable outcome of one atomic routing-manifest command.</summary>
public sealed record MaterializationAtomicRoutingManifestResult
{
    /// <summary>Current portable result schema.</summary>
    public const string CurrentSchemaVersion = MaterializationAtomicRoutingManifestSemantics.ResultSchema;

    /// <summary>Creates one exact manifest transaction outcome.</summary>
    /// <param name="schemaVersion">Exact portable result schema.</param>
    /// <param name="disposition">Applied, replayed, or rejected disposition.</param>
    /// <param name="request">Exact persisted transaction intent.</param>
    /// <param name="snapshot">Current complete manifest after observation.</param>
    /// <param name="receipt">Exact commit receipt for applied or replayed commands.</param>
    /// <param name="detail">Optional rejection explanation.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Schema, disposition, scope, receipt, or result state is inconsistent.</exception>
    [JsonConstructor]
    public MaterializationAtomicRoutingManifestResult(
        string schemaVersion,
        MaterializationBackendRoutingDisposition disposition,
        MaterializationAtomicRoutingManifestRequest request,
        MaterializationRoutingManifestSnapshot snapshot,
        MaterializationAtomicRoutingManifestReceipt? receipt = null,
        string? detail = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Atomic routing manifest result schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported routing disposition.");
        }

        Request = request ?? throw new ArgumentNullException(nameof(request));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var committed = disposition is MaterializationBackendRoutingDisposition.Applied
            or MaterializationBackendRoutingDisposition.Replayed;
        if (!MaterializationAtomicRoutingManifestSemantics.SamePlanSet(
                snapshot.PlanSet,
                request.Realization.Requirement.PlanSet)
            || !string.Equals(snapshot.Authority, request.Realization.Requirement.Authority, StringComparison.Ordinal)
            || committed != (receipt is not null)
            || receipt is not null
            && (receipt.CommandId != request.CommandId
                || !string.Equals(receipt.Authority, snapshot.Authority, StringComparison.Ordinal)
                || !MaterializationAtomicRoutingManifestSemantics.SamePlanSet(receipt.PlanSet, snapshot.PlanSet)
                || receipt.PriorRevision != request.ExpectedRevision
                || receipt.Fence != request.Fence
                || receipt.CommittedAtUtc < request.IssuedAtUtc
                || snapshot.Revision.Ordinal < receipt.Revision.Ordinal)
            || disposition is MaterializationBackendRoutingDisposition.Applied
            && (snapshot.Revision != receipt!.Revision
                || !MaterializationAtomicRoutingManifestSemantics.SameEntries(
                    snapshot.Entries,
                    request.DesiredEntries))
            || !committed && string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("Atomic manifest result evidence contradicts its exact retained request.", nameof(snapshot));
        }
        Disposition = disposition;
        Receipt = receipt;
        Detail = detail;
    }

    /// <summary>Exact portable result schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Applied, replayed, or rejected disposition.</summary>
    public MaterializationBackendRoutingDisposition Disposition { get; }

    /// <summary>Exact persisted transaction intent.</summary>
    public MaterializationAtomicRoutingManifestRequest Request { get; }

    /// <summary>Current complete manifest after observation.</summary>
    public MaterializationRoutingManifestSnapshot Snapshot { get; }

    /// <summary>Exact commit receipt for applied or replayed commands.</summary>
    public MaterializationAtomicRoutingManifestReceipt? Receipt { get; }

    /// <summary>Optional rejection explanation.</summary>
    public string? Detail { get; }

    /// <summary>Whether every exact desired read/write route is currently visible.</summary>
    [JsonIgnore]
    public bool IsApplied => Receipt is not null
        && MaterializationAtomicRoutingManifestSemantics.SameEntries(
            Snapshot.Entries,
            Request.DesiredEntries);
}

/// <summary>Strict canonical JSON persistence for atomic routing-manifest contracts.</summary>
public static class MaterializationAtomicRoutingManifestJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact transaction request.</summary>
    /// <param name="request">Exact durable request.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="JsonException">The request cannot be serialized canonically.</exception>
    public static string SerializeRequest(MaterializationAtomicRoutingManifestRequest request) => Serialize(request);

    /// <summary>Deserializes one exact transaction request.</summary>
    /// <param name="json">Strict canonical request JSON.</param>
    /// <returns>The constructor-validated request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The document is malformed, open, noncanonical, or invalid.</exception>
    public static MaterializationAtomicRoutingManifestRequest DeserializeRequest(string json) =>
        Deserialize<MaterializationAtomicRoutingManifestRequest>(json, "atomic routing manifest request");

    /// <summary>Serializes one exact transaction result.</summary>
    /// <param name="result">Exact durable result.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <exception cref="JsonException">The result cannot be serialized canonically.</exception>
    public static string SerializeResult(MaterializationAtomicRoutingManifestResult result) => Serialize(result);

    /// <summary>Deserializes one exact transaction result.</summary>
    /// <param name="json">Strict canonical result JSON.</param>
    /// <returns>The constructor-validated result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The document is malformed, open, noncanonical, or invalid.</exception>
    public static MaterializationAtomicRoutingManifestResult DeserializeResult(string json) =>
        Deserialize<MaterializationAtomicRoutingManifestResult>(json, "atomic routing manifest result");

    internal static string Serialize<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(value, Options));
    }

    static T Deserialize<T>(string json, string role)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(json, Options, role, out T? value, out var error)
            && value is not null)
        {
            return value;
        }
        throw new JsonException(error.Message);
    }
}

/// <summary>One authority that owns the linearization point for a complete read/write routing manifest.</summary>
public interface IMaterializationAtomicRoutingManifestAuthority
{
    /// <summary>Attributable capability evidence implemented by this authority.</summary>
    MaterializationAtomicRoutingManifestCapability Capability { get; }

    /// <summary>Inspects the current complete manifest for one exact plan set.</summary>
    /// <param name="context">Operation context carrying cancellation and tracing.</param>
    /// <param name="planSet">Exact plan-set scope.</param>
    /// <returns>The current complete manifest.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The authority does not own <paramref name="planSet"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationRoutingManifestSnapshot> InspectAsync(
        OperationContext context,
        MaterializationRebuildPlanSetReference planSet);

    /// <summary>Atomically compares and replaces the entire exact read/write manifest.</summary>
    /// <param name="context">Operation context carrying cancellation and tracing.</param>
    /// <param name="request">Exact durable compare-and-swap intent.</param>
    /// <returns>Applied, replayed, or rejected exact transaction evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationAtomicRoutingManifestResult> CompareExchangeAsync(
        OperationContext context,
        MaterializationAtomicRoutingManifestRequest request);
}

/// <summary>Storage-owned construction and execution of one atomic plan-set routing-manifest transaction.</summary>
public sealed class MaterializationAtomicRoutingManifestExecutor
{
    readonly MaterializationRebuildPlanSet planSet;
    readonly MaterializationAtomicRoutingManifestRealization realization;
    readonly string realizationJson;

    /// <summary>Creates an executor for one exact compiled atomic realization.</summary>
    /// <param name="planSet">Canonical atomic-visibility plan set.</param>
    /// <param name="realization">Exact matched manifest capability.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The realization is detached or capability matching fails.</exception>
    public MaterializationAtomicRoutingManifestExecutor(
        MaterializationRebuildPlanSet planSet,
        MaterializationAtomicRoutingManifestRealization realization)
    {
        this.planSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        this.realization = realization ?? throw new ArgumentNullException(nameof(realization));
        var compiled = MaterializationAtomicRoutingManifestCompiler.Compile(
            planSet,
            realization.Requirement,
            realization.Capability);
        if (!compiled.IsSuccessful)
        {
            throw new ArgumentException("Atomic routing-manifest realization is detached from the exact plan set.", nameof(realization));
        }

        realizationJson = MaterializationAtomicRoutingManifestJsonSerializer.Serialize(realization);
    }

    /// <summary>Creates one replay-stable transaction intent from the exact ready barrier and prior manifest.</summary>
    /// <param name="barrier">Exact all-leaf readiness barrier.</param>
    /// <param name="prior">Complete prior manifest.</param>
    /// <param name="fence">Manifest-authority fence.</param>
    /// <param name="issuedAtUtc">Stable UTC issuance boundary.</param>
    /// <returns>A complete immutable compare-and-swap request.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Barrier, manifest, fence, or chronology is inexact.</exception>
    public MaterializationAtomicRoutingManifestRequest CreateRequest(
        MaterializationRebuildReadyBarrier barrier,
        MaterializationRoutingManifestSnapshot prior,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(prior);
        if (!MaterializationAtomicRoutingManifestSemantics.SamePlanSet(
                barrier.PlanSet,
                realization.Requirement.PlanSet)
            || !MaterializationAtomicRoutingManifestSemantics.SameScope(
                barrier.ReadyGenerations.Select(static ready => ready.PlacementSlice),
                realization.Requirement.Scope))
        {
            throw new ArgumentException("Atomic promotion requires the exact complete ready barrier.", nameof(barrier));
        }
        var desired = ImmutableArray.CreateBuilder<MaterializationRoutingManifestEntry>(
            barrier.ReadyGenerations.Length);
        foreach (var ready in barrier.ReadyGenerations)
        {
            MaterializationBackendGenerationReference generation = new(
                targetId: ready.PlacementSlice.Target,
                generationId: ready.Generation,
                definitionFingerprint: ready.PlacementSlice.Materialization.DefinitionFingerprint);
            var configuration = MaterializationBackendRoutingConfigurationResolver.Resolve(
                planSet.Placement.BackendPool.Definition,
                new MaterializationBackendRoutingConfigurationLayer(
                    origin: EffectiveConfigurationOrigin.Explicit,
                    authority: ConfigurationAuthority(realization.Requirement.PlanSet),
                    settings: new(
                        readTarget: ready.PlacementSlice.Target,
                        writeTarget: ready.PlacementSlice.Target)));
            desired.Add(new(
                schemaVersion: MaterializationRoutingManifestEntry.CurrentSchemaVersion,
                placementSlice: ready.PlacementSlice,
                read: generation,
                write: generation,
                readiness: ready,
                configuration: configuration));
        }
        var entries = desired.MoveToImmutable();
        return new(
            schemaVersion: MaterializationAtomicRoutingManifestRequest.CurrentSchemaVersion,
            realization: realization,
            prior: prior,
            desiredEntries: entries,
            fence: fence,
            commandId: CommandIdentity(prior, entries, fence, issuedAtUtc),
            issuedAtUtc: issuedAtUtc);
    }

    /// <summary>Applies or exactly replays one persisted complete-manifest transaction.</summary>
    /// <param name="context">Operation context carrying cancellation and tracing.</param>
    /// <param name="request">Exact persisted transaction intent.</param>
    /// <param name="authority">Single manifest transaction authority.</param>
    /// <returns>Exact transaction evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Request or authority capability is detached or substituted.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public ValueTask<MaterializationAtomicRoutingManifestResult> ExecuteAsync(
        OperationContext context,
        MaterializationAtomicRoutingManifestRequest request,
        IMaterializationAtomicRoutingManifestAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);
        if (!string.Equals(
                realizationJson,
                MaterializationAtomicRoutingManifestJsonSerializer.Serialize(request.Realization),
                StringComparison.Ordinal)
            || !string.Equals(
                MaterializationAtomicRoutingManifestJsonSerializer.Serialize(realization.Capability),
                MaterializationAtomicRoutingManifestJsonSerializer.Serialize(authority.Capability),
                StringComparison.Ordinal)
            || request.CommandId != CommandIdentity(
                request.Prior,
                request.DesiredEntries,
                request.Fence,
                request.IssuedAtUtc))
        {
            throw new ArgumentException("Atomic manifest request or authority capability is not the exact compiled realization.", nameof(request));
        }
        return authority.CompareExchangeAsync(context, request);
    }

    static string ConfigurationAuthority(MaterializationRebuildPlanSetReference planSet) =>
        MaterializationAtomicRoutingManifestSemantics.ConfigurationAuthorityPrefix + planSet.PlanSet.Value;

    MaterializationBackendRoutingCommandId CommandIdentity(
        MaterializationRoutingManifestSnapshot prior,
        ImmutableArray<MaterializationRoutingManifestEntry> desired,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc)
    {
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append(MaterializationAtomicRoutingManifestSemantics.CommandDomain);
        builder.Append(realization.Requirement.PlanSet.PlanSet.Value);
        builder.Append(realization.Requirement.Authority);
        builder.Append(MaterializationStableIdentity.Digest(realizationJson));
        builder.Append(prior.Revision.Value);
        builder.Append(fence.Value);
        builder.Append(issuedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var entry in desired)
        {
            builder.Append(entry.PlacementSlice.Fingerprint.Value);
            builder.Append(entry.Read!.TargetId.Value);
            builder.Append(entry.Read.GenerationId.Value);
            builder.Append(entry.Write!.TargetId.Value);
            builder.Append(entry.Write.GenerationId.Value);
        }
        return new($"materialization-routing-manifest/atomic/v1/{builder.Complete()}");
    }
}
