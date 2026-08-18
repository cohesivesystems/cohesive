using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Storage.Materialization;

/// <summary>Entry point for deterministic C# authoring of canonical materialization definitions.</summary>
public static class Materialization
{
    /// <summary>Begins authoring one relation-derived materialization definition.</summary>
    /// <param name="id">Stable logical materialization identity.</param>
    /// <param name="relation">Exact Relations compilation request retained by the canonical definition.</param>
    /// <param name="output">Canonical complete relation output selected for materialization.</param>
    /// <returns>A fresh builder that lowers exclusively to canonical <see cref="MaterializationDefinition"/> IR.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="output"/> is default, Relations compilation fails, or the output is
    /// absent from the compiled plan.
    /// </exception>
    /// <exception cref="InvalidOperationException">A Relations semantic snapshot has no canonical representation.</exception>
    /// <exception cref="NotSupportedException">A Relations semantic snapshot contains an unsupported value.</exception>
    public static MaterializationDefinitionBuilder Define(
        MaterializationId id,
        RelationQueryCompilationRequest relation,
        RelationQueryOutputId output) =>
        new(id, relation, output);
}

/// <summary>
/// Mutable C# authoring projection that deterministically lowers declared policies and profiles into canonical
/// materialization IR.
/// </summary>
/// <remarks>
/// The builder and its convenience profiles are not semantic authorities. <see cref="Build"/> produces an immutable
/// <see cref="MaterializationDefinition"/> and authoritative <see cref="MaterializationDefinitionValidator"/>
/// diagnostics. No callback, provider type, endpoint, or ambient configuration is retained.
/// </remarks>
public sealed class MaterializationDefinitionBuilder
{
    readonly MaterializationId id;
    readonly MaterializationRelationReference relation;
    readonly CompiledRelationQueryPlan plan;
    MaterializationUpdatePolicy? updatePolicy;
    MaterializationFailurePolicy? failurePolicy;
    MaterializationFreshnessPolicy? freshnessPolicy;
    ExecutionProvenance? provenance;
    ImmutableArray<MaterializationSourceRequirement>? explicitSources;
    AuthoringBounds? relationRebuildBounds;
    ImmutableArray<MaterializationCapabilityRequirement>? explicitTargetCapabilities;
    AuthoringBounds? generationalIndexBounds;
    ImmutableArray<ControlLoopDefinition> controlLoops = [];
    ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> controlWorkloads = [];
    bool controlsConfigured;

    internal MaterializationDefinitionBuilder(
        MaterializationId id,
        RelationQueryCompilationRequest relation,
        RelationQueryOutputId output)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A materialization authoring session requires a stable identity.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(relation);
        this.id = id;
        this.relation = MaterializationRelationReference.From(relation, output, out plan);
    }

    /// <summary>Declares the synchronization modes, convergence protocol, and target idempotency contract.</summary>
    /// <param name="policy">Canonical update policy to retain.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An update policy was already declared.</exception>
    public MaterializationDefinitionBuilder WithUpdatePolicy(MaterializationUpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureUnset(updatePolicy, "The materialization update policy is already configured.");
        updatePolicy = policy;
        return this;
    }

    /// <summary>
    /// Derives every bounded rebuild read and continuation requirement from the exact compiled Relations acquisition
    /// contract.
    /// </summary>
    /// <param name="maximumItems">Largest requested item count for one source read.</param>
    /// <param name="maximumBytes">Largest requested encoded-byte count for one source read.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A supplied bound is not positive or portable.</exception>
    /// <exception cref="InvalidOperationException">Source requirements were already configured.</exception>
    public MaterializationDefinitionBuilder WithBoundedRelationRebuildSources(
        long maximumItems,
        long maximumBytes)
    {
        EnsureSourcesUnset();
        relationRebuildBounds = AuthoringBounds.Create(maximumItems, maximumBytes);
        return this;
    }

    /// <summary>Declares complete custom source requirements when a convenience profile is insufficient.</summary>
    /// <param name="sources">One requirement set for every exact Relations acquisition input.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">Source requirements were already configured.</exception>
    public MaterializationDefinitionBuilder WithSources(
        ImmutableArray<MaterializationSourceRequirement> sources)
    {
        EnsureSourcesUnset();
        explicitSources = sources.IsDefault ? [] : sources;
        return this;
    }

    /// <summary>
    /// Declares the canonical capability closure for an isolated, validated, atomically promoted index generation.
    /// </summary>
    /// <param name="maximumItems">Largest requested item count for one target bulk.</param>
    /// <param name="maximumBytes">Largest requested encoded-byte count for one target bulk.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A supplied bound is not positive or portable.</exception>
    /// <exception cref="InvalidOperationException">Target requirements were already configured.</exception>
    public MaterializationDefinitionBuilder WithGenerationalIndexTarget(
        long maximumItems,
        long maximumBytes)
    {
        EnsureTargetUnset();
        generationalIndexBounds = AuthoringBounds.Create(maximumItems, maximumBytes);
        return this;
    }

    /// <summary>Declares complete custom target requirements when a convenience profile is insufficient.</summary>
    /// <param name="capabilities">Backend-independent target capability requirements.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">Target requirements were already configured.</exception>
    public MaterializationDefinitionBuilder WithTargetCapabilities(
        ImmutableArray<MaterializationCapabilityRequirement> capabilities)
    {
        EnsureTargetUnset();
        explicitTargetCapabilities = capabilities.IsDefault ? [] : capabilities;
        return this;
    }

    /// <summary>Declares bounded operation retry and exhausted-attempt behavior.</summary>
    /// <param name="policy">Canonical failure policy to retain.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A failure policy was already declared.</exception>
    public MaterializationDefinitionBuilder WithFailurePolicy(MaterializationFailurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureUnset(failurePolicy, "The materialization failure policy is already configured.");
        failurePolicy = policy;
        return this;
    }

    /// <summary>Declares end-to-end visible freshness requirements.</summary>
    /// <param name="policy">Canonical freshness policy to retain.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A freshness policy was already declared.</exception>
    public MaterializationDefinitionBuilder WithFreshnessPolicy(MaterializationFreshnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureUnset(freshnessPolicy, "The materialization freshness policy is already configured.");
        freshnessPolicy = policy;
        return this;
    }

    /// <summary>Declares portable Control loops and their explicit rebuild or realtime workload bindings.</summary>
    /// <param name="loops">Portable Control definitions.</param>
    /// <param name="workloads">Exactly one workload binding for every supplied loop.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">Control loops were already declared.</exception>
    public MaterializationDefinitionBuilder WithControls(
        ImmutableArray<ControlLoopDefinition> loops,
        ImmutableArray<MaterializationIndexSyncControlWorkloadBinding> workloads = default)
    {
        if (controlsConfigured)
        {
            throw new InvalidOperationException("Materialization Control loops are already configured.");
        }

        controlLoops = loops.IsDefault ? [] : loops;
        controlWorkloads = workloads.IsDefault ? [] : workloads;
        controlsConfigured = true;
        return this;
    }

    /// <summary>Declares required producer and source attribution for the canonical definition.</summary>
    /// <param name="value">Canonical execution provenance to retain.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Provenance was already declared.</exception>
    public MaterializationDefinitionBuilder WithProvenance(ExecutionProvenance value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureUnset(provenance, "Materialization provenance is already configured.");
        provenance = value;
        return this;
    }

    /// <summary>Lowers the complete authoring state into canonical immutable IR and authoritative validation.</summary>
    /// <returns>The canonical definition and its deterministic semantic validation.</returns>
    /// <exception cref="InvalidOperationException">
    /// An update, source, target, failure, freshness, or provenance declaration is absent.
    /// </exception>
    /// <exception cref="ArgumentException">A lowered canonical constructor invariant is invalid.</exception>
    /// <exception cref="NotSupportedException">Canonical validation encounters an unsupported semantic value.</exception>
    public MaterializationDefinitionAuthoringResult Build()
    {
        var declaredUpdatePolicy = updatePolicy
            ?? throw Missing("an update policy", nameof(WithUpdatePolicy));
        var sources = explicitSources
            ?? (relationRebuildBounds is { } sourceBounds
                ? CreateRelationRebuildSources(declaredUpdatePolicy, sourceBounds)
                : throw Missing("source requirements", nameof(WithBoundedRelationRebuildSources)));
        var targetCapabilities = explicitTargetCapabilities
            ?? (generationalIndexBounds is { } targetBounds
                ? CreateGenerationalIndexTarget(declaredUpdatePolicy, targetBounds)
                : throw Missing("target requirements", nameof(WithGenerationalIndexTarget)));
        var declaredFailurePolicy = failurePolicy
            ?? throw Missing("a failure policy", nameof(WithFailurePolicy));
        var declaredFreshnessPolicy = freshnessPolicy
            ?? throw Missing("a freshness policy", nameof(WithFreshnessPolicy));
        var declaredProvenance = provenance
            ?? throw Missing("provenance", nameof(WithProvenance));

        MaterializationDefinition definition = new(
            id,
            relation,
            sources,
            targetCapabilities,
            declaredUpdatePolicy,
            declaredFailurePolicy,
            declaredFreshnessPolicy,
            controlLoops,
            declaredProvenance,
            controlWorkloads);
        return new(definition, MaterializationDefinitionValidator.Validate(definition));
    }

    ImmutableArray<MaterializationSourceRequirement> CreateRelationRebuildSources(
        MaterializationUpdatePolicy policy,
        AuthoringBounds bounds)
    {
        var inputs = MaterializationSourceAcquisitionCatalog.GetInputs(plan);
        var sources = ImmutableArray.CreateBuilder<MaterializationSourceRequirement>(inputs.Length);
        var readLimits = ReadLimits(bounds);
        foreach (var input in inputs)
        {
            if (!MaterializationSourceAcquisitionCatalog.TryGetReadCapability(plan, input, out var readCapability))
            {
                throw new InvalidOperationException(
                    $"Compiled Relations acquisition input '{input.Value}' has no materialization read projection.");
            }

            var readGuarantees = RequiredGuarantees(policy, readCapability);
            ImmutableArray<MaterializationGuaranteeKind> continuationGuarantees = policy.Consistency switch
            {
                MaterializationConsistencyKind.CoordinatedSnapshot =>
                    [MaterializationGuaranteeKind.StableOrdering, MaterializationGuaranteeKind.CoordinatedSnapshot],
                MaterializationConsistencyKind.Reconciliation =>
                    [MaterializationGuaranteeKind.StableOrdering, MaterializationGuaranteeKind.Reconciliation],
                _ => [MaterializationGuaranteeKind.StableOrdering]
            };

            sources.Add(new(
                input,
                [
                    new(
                        new($"{input.Value}/read"),
                        readCapability,
                        readGuarantees,
                        readLimits,
                        MaterializationSynchronizationMode.Rebuild),
                    new(
                        new($"{input.Value}/continuation"),
                        MaterializationCapabilityKind.SourceContinuation,
                        continuationGuarantees,
                        [],
                        MaterializationSynchronizationMode.Rebuild)
                ]));
        }

        return sources.MoveToImmutable();
    }

    static ImmutableArray<MaterializationCapabilityRequirement> CreateGenerationalIndexTarget(
        MaterializationUpdatePolicy policy,
        AuthoringBounds bounds)
    {
        Dictionary<MaterializationCapabilityKind, MaterializationSynchronizationMode> modesByCapability = [];
        foreach (var mode in EnumerateModes(policy.SupportedModes))
        {
            foreach (var capability in MaterializationDefinitionValidator.GetRequiredTargetCapabilities(policy, mode))
            {
                modesByCapability[capability] = modesByCapability.GetValueOrDefault(capability) | mode;
            }
        }

        var requirements = ImmutableArray.CreateBuilder<MaterializationCapabilityRequirement>(modesByCapability.Count);
        foreach (var (capability, modes) in modesByCapability.OrderBy(static pair => TargetId(pair.Key), StringComparer.Ordinal))
        {
            requirements.Add(new(
                new(TargetId(capability)),
                capability,
                RequiredGuarantees(policy, capability),
                capability is MaterializationCapabilityKind.TargetBulkUpsert
                    or MaterializationCapabilityKind.TargetBulkDelete
                    or MaterializationCapabilityKind.TargetPerItemOutcomes
                    ? WriteLimits(bounds)
                    : [],
                modes));
        }

        return requirements.MoveToImmutable();
    }

    static ImmutableArray<MaterializationGuaranteeKind> RequiredGuarantees(
        MaterializationUpdatePolicy policy,
        MaterializationCapabilityKind capability) =>
        [.. MaterializationDefinitionValidator.GetRequiredGuarantees(policy, capability)];

    static ImmutableArray<MaterializationOperatingLimit> ReadLimits(AuthoringBounds bounds) =>
    [
        new(MaterializationLimitKind.ReadItems, bounds.MaximumItems),
        new(MaterializationLimitKind.ReadBytes, bounds.MaximumBytes)
    ];

    static ImmutableArray<MaterializationOperatingLimit> WriteLimits(AuthoringBounds bounds) =>
    [
        new(MaterializationLimitKind.WriteItems, bounds.MaximumItems),
        new(MaterializationLimitKind.WriteBytes, bounds.MaximumBytes)
    ];

    static IEnumerable<MaterializationSynchronizationMode> EnumerateModes(
        MaterializationSynchronizationMode modes)
    {
        if ((modes & MaterializationSynchronizationMode.Rebuild) != 0)
        {
            yield return MaterializationSynchronizationMode.Rebuild;
        }
        if ((modes & MaterializationSynchronizationMode.Incremental) != 0)
        {
            yield return MaterializationSynchronizationMode.Incremental;
        }
    }

    static string TargetId(MaterializationCapabilityKind capability) => capability switch
    {
        MaterializationCapabilityKind.TargetGenerationIsolation => "target/isolation",
        MaterializationCapabilityKind.TargetBulkUpsert => "target/upsert",
        MaterializationCapabilityKind.TargetBulkDelete => "target/delete",
        MaterializationCapabilityKind.TargetPerItemOutcomes => "target/outcomes",
        MaterializationCapabilityKind.TargetSeal => "target/seal",
        MaterializationCapabilityKind.TargetValidation => "target/validation",
        MaterializationCapabilityKind.TargetFencedPromotion => "target/promotion",
        MaterializationCapabilityKind.TargetGenerationAbandonment => "target/abandonment",
        MaterializationCapabilityKind.TargetRetirement => "target/retirement",
        MaterializationCapabilityKind.TargetCleanup => "target/cleanup",
        _ => throw new ArgumentOutOfRangeException(
            nameof(capability),
            capability,
            "The generational index target profile does not support this capability.")
    };

    void EnsureSourcesUnset()
    {
        if (explicitSources is not null || relationRebuildBounds is not null)
        {
            throw new InvalidOperationException("Materialization source requirements are already configured.");
        }
    }

    void EnsureTargetUnset()
    {
        if (explicitTargetCapabilities is not null || generationalIndexBounds is not null)
        {
            throw new InvalidOperationException("Materialization target requirements are already configured.");
        }
    }

    static void EnsureUnset<T>(T? value, string message) where T : class
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message);
        }
    }

    static InvalidOperationException Missing(string declaration, string method) =>
        new($"Materialization authoring requires {declaration}; call {method} before Build.");

    readonly record struct AuthoringBounds(long MaximumItems, long MaximumBytes)
    {
        public static AuthoringBounds Create(long maximumItems, long maximumBytes) => new(
            MaterializationContract.RequirePortablePositiveBound(maximumItems, nameof(maximumItems)),
            MaterializationContract.RequirePortablePositiveBound(maximumBytes, nameof(maximumBytes)));
    }
}

/// <summary>Canonical materialization definition and authoritative validation produced by one authoring terminal.</summary>
public sealed class MaterializationDefinitionAuthoringResult
{
    internal MaterializationDefinitionAuthoringResult(
        MaterializationDefinition definition,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Validation = validation;
    }

    /// <summary>Canonical immutable materialization definition.</summary>
    public MaterializationDefinition Definition { get; }

    /// <summary>Authoritative canonical definition validation.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether the canonical definition passed semantic validation.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Creates a validated, fingerprinted current-version persistence envelope.</summary>
    /// <returns>A canonical materialization document containing <see cref="Definition"/>.</returns>
    /// <exception cref="ArgumentException"><see cref="Definition"/> fails canonical validation.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime value.</exception>
    public MaterializationDocument CreateDocument() => MaterializationDocument.FromDefinition(Definition);
}
