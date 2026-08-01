using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable identity of one coarse baseline-enumeration shard.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationRebuildShardId
{
    /// <summary>Creates a rebuild-shard identity.</summary>
    /// <param name="value">Stable identity retained across replay and retry of one Process attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public MaterializationRebuildShardId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable shard identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable shard identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one complete persisted rebuild realization plan.</summary>
public sealed record MaterializationRebuildPlanFingerprint
{
    /// <summary>Creates a rebuild-plan fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlanFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Explicit finite operating bounds for one baseline rebuild Process.</summary>
public sealed record MaterializationRebuildLimits
{
    /// <summary>Creates finite rebuild operating bounds.</summary>
    /// <param name="maximumPageItems">Maximum source observations in one page.</param>
    /// <param name="maximumPageBytes">Maximum canonical source bytes in one page.</param>
    /// <param name="maximumBulkItems">Maximum target mutations in one bulk request.</param>
    /// <param name="maximumBulkBytes">Maximum canonical target bytes in one bulk request.</param>
    /// <param name="maximumPagesPerShard">Maximum pages one shard may traverse before failing closed.</param>
    /// <param name="maximumStartsPerActivation">Maximum shard children the coordinator may start in one activation.</param>
    /// <param name="maximumParallelism">Maximum simultaneously active shard children.</param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied bound is not positive.</exception>
    [JsonConstructor]
    public MaterializationRebuildLimits(
        int maximumPageItems,
        long maximumPageBytes,
        int maximumBulkItems,
        long maximumBulkBytes,
        int maximumPagesPerShard,
        int maximumStartsPerActivation,
        int maximumParallelism)
    {
        if (maximumPageItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPageItems), maximumPageItems, "A page item bound must be positive.");
        if (maximumPageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPageBytes), maximumPageBytes, "A page byte bound must be positive.");
        if (maximumBulkItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBulkItems), maximumBulkItems, "A bulk item bound must be positive.");
        if (maximumBulkBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBulkBytes), maximumBulkBytes, "A bulk byte bound must be positive.");
        if (maximumPagesPerShard <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPagesPerShard), maximumPagesPerShard, "A shard page bound must be positive.");
        if (maximumStartsPerActivation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStartsPerActivation),
                maximumStartsPerActivation,
                "A per-activation start bound must be positive.");
        }
        if (maximumParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumParallelism), maximumParallelism, "A parallelism bound must be positive.");

        MaximumPageItems = maximumPageItems;
        MaximumPageBytes = maximumPageBytes;
        MaximumBulkItems = maximumBulkItems;
        MaximumBulkBytes = maximumBulkBytes;
        MaximumPagesPerShard = maximumPagesPerShard;
        MaximumStartsPerActivation = maximumStartsPerActivation;
        MaximumParallelism = maximumParallelism;
    }

    /// <summary>Maximum source observations in one page.</summary>
    public int MaximumPageItems { get; }

    /// <summary>Maximum canonical source bytes in one page.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumPageBytes { get; }

    /// <summary>Maximum target mutations in one bulk request.</summary>
    public int MaximumBulkItems { get; }

    /// <summary>Maximum canonical target bytes in one bulk request.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBulkBytes { get; }

    /// <summary>Maximum pages one shard may traverse before failing closed.</summary>
    public int MaximumPagesPerShard { get; }

    /// <summary>Maximum shard children the coordinator may start in one activation.</summary>
    public int MaximumStartsPerActivation { get; }

    /// <summary>Maximum simultaneously active shard children.</summary>
    public int MaximumParallelism { get; }
}

/// <summary>Exact capability realization of one canonical Relations acquisition input.</summary>
public sealed record MaterializationRebuildSourcePlan
{
    /// <summary>Creates one source realization pinned for a rebuild run.</summary>
    /// <param name="input">Canonical Relations acquisition input.</param>
    /// <param name="source">Exact physical source selected for the input.</param>
    /// <param name="profile">Complete attributable source capability evidence.</param>
    /// <param name="capabilityMatch">Deterministic rebuild-mode decisions for the input requirements.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, profile, or capability decision is inconsistent.</exception>
    [JsonConstructor]
    public MaterializationRebuildSourcePlan(
        RelationQueryInputId input,
        RelationQuerySourceInstanceId source,
        MaterializationCapabilityProfile profile,
        MaterializationCapabilityMatch capabilityMatch)
    {
        MaterializationContract.RequireDefinedIdentity(input.Value, nameof(input));
        MaterializationContract.RequireDefinedIdentity(source.Value, nameof(source));
        Profile = Guard.RequireNotNull(profile);
        CapabilityMatch = Guard.RequireNotNull(capabilityMatch);
        if (profile.Role != MaterializationEndpointRole.Source
            || !string.Equals(profile.Subject, source.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A rebuild source profile must describe the exact selected source.",
                nameof(profile));
        }
        if (!capabilityMatch.IsSatisfied)
            throw new ArgumentException("A rebuild source requires a satisfied capability match.", nameof(capabilityMatch));

        Input = input;
        Source = source;
    }

    /// <summary>Canonical Relations acquisition input.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Exact physical source selected for the input.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Complete attributable source capability evidence.</summary>
    public MaterializationCapabilityProfile Profile { get; }

    /// <summary>Deterministic rebuild-mode capability decisions.</summary>
    public MaterializationCapabilityMatch CapabilityMatch { get; }
}

/// <summary>One stable root-enumeration shard in a persisted rebuild plan.</summary>
public sealed record MaterializationRebuildShardPlan
{
    /// <summary>Creates one exact rebuild shard.</summary>
    /// <param name="id">Stable Process partition and progress identity.</param>
    /// <param name="scope">Exact physical-plan, placement, partition, and ordering scope.</param>
    /// <param name="read">Exact canonical Relations root read used for every page.</param>
    /// <param name="hydrationPhysicalPlan">
    /// Exact Relations physical lowering, realization, placements, and policy used to hydrate each root page.
    /// </param>
    /// <param name="changeChannel">
    /// Optional exact Channel realization-plan fingerprint governing the captured change boundary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/>, <paramref name="read"/>, or <paramref name="hydrationPhysicalPlan"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity is default or the read and scope are incompatible.</exception>
    [JsonConstructor]
    public MaterializationRebuildShardPlan(
        MaterializationRebuildShardId id,
        MaterializationSourceScope scope,
        RelationQuerySourceReadRequest read,
        RelationQueryPhysicalPlanFingerprint hydrationPhysicalPlan,
        ChannelRealizationPlanFingerprint? changeChannel = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        Scope = Guard.RequireNotNull(scope);
        Read = Guard.RequireNotNull(read);
        HydrationPhysicalPlan = Guard.RequireNotNull(hydrationPhysicalPlan);
        MaterializationSourceAcquisitionCatalog.RequireCompatibleRead(read, scope);
        Id = id;
        ChangeChannel = changeChannel;
    }

    /// <summary>Stable Process partition and progress identity.</summary>
    public MaterializationRebuildShardId Id { get; }

    /// <summary>Exact physical-plan, placement, partition, and ordering scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Exact canonical Relations root read used for every page.</summary>
    public RelationQuerySourceReadRequest Read { get; }

    /// <summary>
    /// Exact Relations physical-plan fingerprint pinning hydration realization, placements, lowering, and policy.
    /// </summary>
    public RelationQueryPhysicalPlanFingerprint HydrationPhysicalPlan { get; }

    /// <summary>Optional exact Channel realization-plan fingerprint for catch-up delivery.</summary>
    public ChannelRealizationPlanFingerprint? ChangeChannel { get; }
}

/// <summary>
/// Portable, fingerprinted, run-scoped realization plan for one baseline-plus-catch-up materialization rebuild.
/// </summary>
/// <remarks>
/// This plan contains only durable semantic and capability evidence. Runtime adapter instances, credentials,
/// delegates, leases, and SDK objects are resolved separately against its exact fingerprint.
/// </remarks>
public sealed record MaterializationRebuildPlan
{
    /// <summary>Current persisted rebuild-plan schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan/v1";

    /// <summary>Creates and fingerprints a rebuild realization plan.</summary>
    /// <param name="materialization">Exact canonical materialization document.</param>
    /// <param name="sources">One rebuild-mode capability realization for every declared Relations source input.</param>
    /// <param name="target">Exact candidate-generation target descriptor.</param>
    /// <param name="targetCapabilityMatch">Deterministic rebuild-mode target capability decisions.</param>
    /// <param name="shards">Finite stable root-enumeration shard catalog.</param>
    /// <param name="limits">Explicit finite page, bulk, activation, and parallelism bounds.</param>
    /// <param name="provenance">Compiler and source attribution for this realization.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A definition, capability, source, shard, or fingerprint invariant is invalid.</exception>
    public MaterializationRebuildPlan(
        MaterializationDocument materialization,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationCapabilityMatch targetCapabilityMatch,
        ImmutableArray<MaterializationRebuildShardPlan> shards,
        MaterializationRebuildLimits limits,
        ExecutionProvenance provenance)
        : this(
            schemaVersion: CurrentSchemaVersion,
            materialization: materialization,
            sources: sources,
            target: target,
            targetCapabilityMatch: targetCapabilityMatch,
            shards: shards,
            limits: limits,
            provenance: provenance,
            fingerprint: null)
    {
    }

    /// <summary>Creates or deserializes an exactly fingerprinted rebuild realization plan.</summary>
    /// <param name="schemaVersion">Exact persisted plan schema version.</param>
    /// <param name="materialization">Exact canonical materialization document.</param>
    /// <param name="sources">One rebuild-mode capability realization for every declared Relations source input.</param>
    /// <param name="target">Exact candidate-generation target descriptor.</param>
    /// <param name="targetCapabilityMatch">Deterministic rebuild-mode target capability decisions.</param>
    /// <param name="shards">Finite stable root-enumeration shard catalog.</param>
    /// <param name="limits">Explicit finite page, bulk, activation, and parallelism bounds.</param>
    /// <param name="provenance">Compiler and source attribution for this realization.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A definition, capability, source, shard, or fingerprint invariant is invalid.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlan(
        string schemaVersion,
        MaterializationDocument materialization,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationCapabilityMatch targetCapabilityMatch,
        ImmutableArray<MaterializationRebuildShardPlan> shards,
        MaterializationRebuildLimits limits,
        ExecutionProvenance provenance,
        MaterializationRebuildPlanFingerprint? fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Rebuild-plan schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Materialization = Guard.RequireNotNull(materialization);
        Target = Guard.RequireNotNull(target);
        TargetCapabilityMatch = Guard.RequireNotNull(targetCapabilityMatch);
        Limits = Guard.RequireNotNull(limits);
        Provenance = Guard.RequireNotNull(provenance);
        if (limits.MaximumStartsPerActivation != MaterializationRebuildProcessFactory.MaximumStartsPerActivation
            || limits.MaximumParallelism != MaterializationRebuildProcessFactory.MaximumParallelism)
        {
            throw new ArgumentException(
                "The v1 rebuild plan must retain the exact bounded coordinator admission policy.",
                nameof(limits));
        }
        ValidateMaterialization(materialization);
        if (target.MaterializationId != materialization.Definition.Id)
            throw new ArgumentException("The selected target must belong to the exact materialization.", nameof(target));
        if (!targetCapabilityMatch.IsSatisfied)
            throw new ArgumentException("A rebuild target requires a satisfied capability match.", nameof(targetCapabilityMatch));

        Sources = NormalizeSources(materialization.Definition, sources);
        ValidateSourceMatches(materialization.Definition, Sources);
        ValidateTargetMatch(materialization.Definition, target, targetCapabilityMatch);
        Shards = NormalizeShards(materialization.Definition, Sources, shards);
        if (Shards.Length > MaterializationRebuildProcessFactory.MaximumPartitions)
        {
            throw new ArgumentException(
                $"The v1 rebuild Process supports at most {MaterializationRebuildProcessFactory.MaximumPartitions} shards.",
                nameof(shards));
        }
        ValidateOperatingLimits(Sources, Target, Shards, Limits);

        var computed = MaterializationRebuildPlanFingerprinter.Compute(
            SchemaVersion,
            Materialization,
            Sources,
            Target,
            TargetCapabilityMatch,
            Shards,
            Limits,
            Provenance);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied rebuild-plan fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact canonical materialization document.</summary>
    public MaterializationDocument Materialization { get; }

    /// <summary>Source realizations in canonical Relations input order.</summary>
    public ImmutableArray<MaterializationRebuildSourcePlan> Sources { get; }

    /// <summary>Exact candidate-generation target descriptor.</summary>
    public MaterializationTargetDescriptor Target { get; }

    /// <summary>Deterministic rebuild-mode target capability decisions.</summary>
    public MaterializationCapabilityMatch TargetCapabilityMatch { get; }

    /// <summary>Finite stable shard catalog in ordinal shard-identity order.</summary>
    public ImmutableArray<MaterializationRebuildShardPlan> Shards { get; }

    /// <summary>Explicit finite operating bounds.</summary>
    public MaterializationRebuildLimits Limits { get; }

    /// <summary>Compiler and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of every execution-affecting plan field.</summary>
    public MaterializationRebuildPlanFingerprint Fingerprint { get; }

    /// <summary>Compares plans by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Plan to compare with this plan.</param>
    /// <returns><see langword="true"/> when both plans have identical canonical semantic content.</returns>
    public bool Equals(MaterializationRebuildPlan? other) =>
        ReferenceEquals(this, other)
        || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Returns a hash code derived from the canonical plan fingerprint.</summary>
    /// <returns>A hash code stable for semantically identical in-memory plans.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();

    static void ValidateMaterialization(MaterializationDocument materialization)
    {
        if (!string.Equals(materialization.SchemaVersion, MaterializationDocument.CurrentSchemaVersion, StringComparison.Ordinal)
            || materialization.DefinitionFingerprint != MaterializationDefinitionFingerprinter.Compute(materialization.Definition))
        {
            throw new ArgumentException("A rebuild plan requires an exact current materialization document.", nameof(materialization));
        }
        var definitionValidation = MaterializationDefinitionValidator.Validate(materialization.Definition);
        if (!definitionValidation.IsValid)
        {
            throw new ArgumentException(
                "A rebuild plan requires a semantically valid materialization definition: "
                + string.Join(
                    " ",
                    definitionValidation.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(materialization));
        }
        if ((materialization.Definition.UpdatePolicy.SupportedModes & MaterializationSynchronizationMode.Rebuild) == 0)
            throw new ArgumentException("The materialization definition does not support rebuild execution.", nameof(materialization));
        if (materialization.Definition.UpdatePolicy.Consistency != MaterializationConsistencyKind.BaselinePlusCatchUp)
        {
            throw new ArgumentException(
                "The v1 rebuild Process requires baseline-plus-catch-up consistency.",
                nameof(materialization));
        }
        if (materialization.Definition.FailurePolicy.ExhaustedDisposition
            != MaterializationFailureDisposition.Stop)
        {
            throw new ArgumentException(
                "The v1 rebuild Process requires stop-on-exhaustion failure semantics; durable quarantine is not yet interpreted.",
                nameof(materialization));
        }
        if (materialization.Definition.Relation.Output.Field is not null)
            throw new ArgumentException("A materialization rebuild requires a complete shaped Relations output.", nameof(materialization));
        var outputMode = (materialization.Definition.Relation.CompilationRequest.DefinitionDocument.Definition
            as RelationDefinition)?.Output.Mode;
        if (outputMode == RelationOutputMode.Set)
        {
            throw new ArgumentException(
                "The v1 rebuild Process cannot interpret whole-set Relations output semantics over independent bounded pages.",
                nameof(materialization));
        }
        if (outputMode == RelationOutputMode.ManyPerRoot)
        {
            throw new ArgumentException(
                "The v1 rebuild Process cannot finitely bound many-per-root hydration output expansion.",
                nameof(materialization));
        }
    }

    static ImmutableArray<MaterializationRebuildSourcePlan> NormalizeSources(
        MaterializationDefinition definition,
        ImmutableArray<MaterializationRebuildSourcePlan> sources)
    {
        var normalized = sources.IsDefault ? [] : sources;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static source => source is null))
            throw new ArgumentException("A rebuild plan requires non-null source realizations.", nameof(sources));
        if (normalized.GroupBy(static source => source.Input).Any(static group => group.Count() > 1))
            throw new ArgumentException("A rebuild plan cannot repeat a Relations source input.", nameof(sources));
        var expected = definition.Sources.Select(static source => source.Input).OrderBy(static input => input.Value, StringComparer.Ordinal);
        var observed = normalized.Select(static source => source.Input).OrderBy(static input => input.Value, StringComparer.Ordinal);
        if (!expected.SequenceEqual(observed))
            throw new ArgumentException("A rebuild plan must realize every exact declared source input once.", nameof(sources));
        return [.. normalized.OrderBy(static source => source.Input.Value, StringComparer.Ordinal)];
    }

    static void ValidateSourceMatches(
        MaterializationDefinition definition,
        ImmutableArray<MaterializationRebuildSourcePlan> sources)
    {
        foreach (var source in sources)
        {
            var requirements = definition.Sources.Single(candidate => candidate.Input == source.Input).Capabilities;
            var expected = MaterializationCapabilityMatcher.MatchForMode(
                requirements,
                source.Profile,
                MaterializationSynchronizationMode.Rebuild);
            if (!SameMatch(expected, source.CapabilityMatch))
                throw new ArgumentException("A rebuild source capability match is stale or forged.", nameof(sources));
        }
    }

    static void ValidateTargetMatch(
        MaterializationDefinition definition,
        MaterializationTargetDescriptor target,
        MaterializationCapabilityMatch targetCapabilityMatch)
    {
        var expected = MaterializationCapabilityMatcher.MatchForMode(
            definition.TargetCapabilities,
            target.Capabilities,
            MaterializationSynchronizationMode.Rebuild);
        if (!SameMatch(expected, targetCapabilityMatch))
            throw new ArgumentException("The rebuild target capability match is stale or forged.", nameof(targetCapabilityMatch));
    }

    static ImmutableArray<MaterializationRebuildShardPlan> NormalizeShards(
        MaterializationDefinition definition,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        ImmutableArray<MaterializationRebuildShardPlan> shards)
    {
        var normalized = shards.IsDefault ? [] : shards;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static shard => shard is null))
            throw new ArgumentException("A rebuild plan requires a finite non-empty shard catalog.", nameof(shards));
        if (normalized.GroupBy(static shard => shard.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("A rebuild plan cannot repeat a shard identity.", nameof(shards));
        if (normalized.GroupBy(static shard => shard.Scope).Any(static group => group.Count() > 1))
            throw new ArgumentException("A rebuild plan cannot repeat an exact source-feed scope.", nameof(shards));
        if (normalized.Any(shard => !sources.Any(source =>
                source.Input == shard.Scope.Input && source.Source == shard.Scope.Source)))
        {
            throw new ArgumentException("Every shard must use one exact pinned source realization.", nameof(shards));
        }

        var compilation = definition.Relation.Compile();
        if (!compilation.IsSuccessful || compilation.Plan is null)
            throw new ArgumentException("The exact materialization Relations plan no longer compiles.", nameof(definition));
        var rootInputs = compilation.Plan.InputContract.Sources
            .Where(static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            .Select(static source => source.Input.Id)
            .ToHashSet();
        if (normalized.Any(shard => !rootInputs.Contains(shard.Scope.Input)))
            throw new ArgumentException("Every rebuild shard must enumerate a canonical relation-root input.", nameof(shards));
        var realizedRootInputs = normalized.Select(static shard => shard.Scope.Input).ToHashSet();
        if (!rootInputs.SetEquals(realizedRootInputs))
        {
            throw new ArgumentException(
                "A rebuild plan must enumerate every canonical relation-root input at least once.",
                nameof(shards));
        }

        return [.. normalized.OrderBy(static shard => shard.Id.Value, StringComparer.Ordinal)];
    }

    static void ValidateOperatingLimits(
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        ImmutableArray<MaterializationRebuildShardPlan> shards,
        MaterializationRebuildLimits limits)
    {
        foreach (var shard in shards)
        {
            var source = sources.Single(candidate => candidate.Input == shard.Scope.Input);
            MaterializationCapabilityLimits.RequireSupportedBounds(
                source.Profile,
                MaterializationSourceAcquisitionCatalog.GetReadCapability(shard.Read.Constraint),
                MaterializationLimitKind.ReadItems,
                limits.MaximumPageItems,
                MaterializationLimitKind.ReadBytes,
                limits.MaximumPageBytes,
                nameof(limits));
        }

        ReadOnlySpan<MaterializationCapabilityKind> targetCapabilities =
        [
            MaterializationCapabilityKind.TargetBulkUpsert,
            MaterializationCapabilityKind.TargetPerItemOutcomes
        ];
        foreach (var capability in targetCapabilities)
        {
            MaterializationCapabilityLimits.RequireSupportedBounds(
                target.Capabilities,
                capability,
                MaterializationLimitKind.WriteItems,
                limits.MaximumBulkItems,
                MaterializationLimitKind.WriteBytes,
                limits.MaximumBulkBytes,
                nameof(limits));
        }
    }

    static bool SameMatch(MaterializationCapabilityMatch left, MaterializationCapabilityMatch right) =>
        StrictDocumentJson.GetCanonicalBytes(left, MaterializationJsonSerializer.CreateOptions())
            .AsSpan()
            .SequenceEqual(StrictDocumentJson.GetCanonicalBytes(right, MaterializationJsonSerializer.CreateOptions()));
}

/// <summary>Canonical fingerprinting for persisted materialization rebuild realization plans.</summary>
public static class MaterializationRebuildPlanFingerprinter
{
    /// <summary>Digest algorithm used by the v1 profile.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v1 rebuild-plan fence.</summary>
    public const string Canonicalization = "cohesive-materialization-rebuild-plan/v1-c14n/v1";

    /// <summary>Computes the fingerprint of one complete persisted rebuild plan.</summary>
    /// <param name="plan">Plan whose canonical content is fingerprinted.</param>
    /// <returns>A versioned SHA-256 fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static MaterializationRebuildPlanFingerprint Compute(MaterializationRebuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Compute(
            plan.SchemaVersion,
            plan.Materialization,
            plan.Sources,
            plan.Target,
            plan.TargetCapabilityMatch,
            plan.Shards,
            plan.Limits,
            plan.Provenance);
    }

    internal static MaterializationRebuildPlanFingerprint Compute(
        string schemaVersion,
        MaterializationDocument materialization,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationCapabilityMatch targetCapabilityMatch,
        ImmutableArray<MaterializationRebuildShardPlan> shards,
        MaterializationRebuildLimits limits,
        ExecutionProvenance provenance)
    {
        var content = new FingerprintContent(
            schemaVersion,
            materialization,
            sources,
            target,
            targetCapabilityMatch,
            shards,
            limits,
            provenance);
        var canonical = StrictDocumentJson.GetCanonicalBytes(content, MaterializationJsonSerializer.CreateOptions());
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintContent(
        string SchemaVersion,
        MaterializationDocument Materialization,
        ImmutableArray<MaterializationRebuildSourcePlan> Sources,
        MaterializationTargetDescriptor Target,
        MaterializationCapabilityMatch TargetCapabilityMatch,
        ImmutableArray<MaterializationRebuildShardPlan> Shards,
        MaterializationRebuildLimits Limits,
        ExecutionProvenance Provenance);
}
