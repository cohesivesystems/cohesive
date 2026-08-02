using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable setting names attributed by a rebuild plan-set scheduling realization.</summary>
public static class MaterializationRebuildSchedulingSettingNames
{
    /// <summary>Effective maximum leaf starts per scheduler activation.</summary>
    public const string MaximumStartsPerActivation = "maximumStartsPerActivation";

    /// <summary>Effective maximum concurrently active leaf rebuilds.</summary>
    public const string MaximumParallelism = "maximumParallelism";

    internal static ImmutableArray<string> All { get; } = [MaximumParallelism, MaximumStartsPerActivation];
}

/// <summary>Effective bounded scheduling realization for one frozen placement plan.</summary>
public sealed record MaterializationRebuildSchedulingRealization
{
    /// <summary>Creates one fully attributed effective scheduling realization.</summary>
    /// <param name="maximumStartsPerActivation">Effective maximum leaf starts per activation; zero only for no slices.</param>
    /// <param name="maximumParallelism">Effective concurrent leaf bound; zero only for no slices.</param>
    /// <param name="configuration">One effective-configuration decision for each known setting.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is negative.</exception>
    /// <exception cref="ArgumentException">Configuration is null, duplicated, or incomplete.</exception>
    [JsonConstructor]
    public MaterializationRebuildSchedulingRealization(
        int maximumStartsPerActivation,
        int maximumParallelism,
        ImmutableArray<EffectiveConfigurationDecision> configuration)
    {
        if (maximumStartsPerActivation < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumStartsPerActivation), maximumStartsPerActivation, "An effective start bound cannot be negative.");
        if (maximumParallelism < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumParallelism), maximumParallelism, "An effective parallelism bound cannot be negative.");
        if (configuration.IsDefault || configuration.Any(static decision => decision is null)
            || configuration.GroupBy(static decision => decision.Setting).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Scheduling realization requires unique non-null attribution.", nameof(configuration));
        }

        var settings = configuration.Select(static decision => decision.Setting).Order(StringComparer.Ordinal).ToArray();
        if (!settings.SequenceEqual(MaterializationRebuildSchedulingSettingNames.All))
            throw new ArgumentException("Scheduling realization must attribute every known setting exactly once.", nameof(configuration));

        MaximumStartsPerActivation = maximumStartsPerActivation;
        MaximumParallelism = maximumParallelism;
        Configuration = [.. configuration.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)];
    }

    /// <summary>Effective maximum leaf starts per scheduler activation.</summary>
    public int MaximumStartsPerActivation { get; }

    /// <summary>Effective maximum concurrently active leaf rebuilds.</summary>
    public int MaximumParallelism { get; }

    /// <summary>Complete effective-configuration attribution in canonical setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Compares scheduling realizations structurally, including attribution.</summary>
    /// <param name="other">Realization to compare.</param>
    /// <returns><see langword="true"/> when bounds and every canonical attribution are equal.</returns>
    public bool Equals(MaterializationRebuildSchedulingRealization? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && MaximumStartsPerActivation == other.MaximumStartsPerActivation
        && MaximumParallelism == other.MaximumParallelism
        && Configuration.SequenceEqual(other.Configuration);

    /// <summary>Returns a structural hash code for bounds and attribution.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MaximumStartsPerActivation);
        hash.Add(MaximumParallelism);
        foreach (var decision in Configuration)
            hash.Add(decision);
        return hash.ToHashCode();
    }
}

/// <summary>Canonical exact binding from one independently promoted placement slice to one leaf plan.</summary>
public sealed record MaterializationRebuildLeafPlanBinding
{
    /// <summary>Creates one exact placement-slice to leaf-plan binding.</summary>
    /// <param name="slice">Independently fingerprinted placement authority.</param>
    /// <param name="leafPlan">Exact persisted one-target rebuild-plan reference.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="leafPlan"/> carries another placement slice.</exception>
    [JsonConstructor]
    public MaterializationRebuildLeafPlanBinding(
        MaterializationPlacementSliceReference slice,
        MaterializationRebuildPlanReference leafPlan)
    {
        Slice = slice ?? throw new ArgumentNullException(nameof(slice));
        LeafPlan = leafPlan ?? throw new ArgumentNullException(nameof(leafPlan));
        if (leafPlan.PlacementSlice != slice.Fingerprint)
        {
            throw new ArgumentException(
                "A leaf-plan reference must retain the exact placement slice to which it is bound.",
                nameof(leafPlan));
        }
    }

    /// <summary>Independently fingerprinted placement authority.</summary>
    public MaterializationPlacementSliceReference Slice { get; }

    /// <summary>Exact persisted one-target rebuild-plan reference.</summary>
    public MaterializationRebuildPlanReference LeafPlan { get; }
}

/// <summary>Canonical linked realization of one rebuild request over frozen membership and explicit placement.</summary>
/// <remarks>
/// The request is retained by exact content-addressed reference. This constructor validates the self-contained
/// evidence chain; <see cref="MaterializationRebuildPlanSetLinker.Link"/> and
/// <see cref="MaterializationRebuildPlanSetLinker.ValidateReplay"/> resolve the request document and additionally
/// verify selector, pool, scheduling, promotion, and leaf-plan affinity.
/// </remarks>
public sealed class MaterializationRebuildPlanSet : IEquatable<MaterializationRebuildPlanSet>
{
    /// <summary>Current portable rebuild plan-set schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan-set/v2";

    /// <summary>Creates and verifies one complete linked rebuild plan set.</summary>
    /// <param name="schemaVersion">Exact portable plan-set schema.</param>
    /// <param name="request">Exact persisted rebuild-request authority.</param>
    /// <param name="membership">Complete frozen selection evidence.</param>
    /// <param name="placement">Exact subject-to-target placement and physical capacity evidence.</param>
    /// <param name="scheduling">Effective bounded outer scheduling realization.</param>
    /// <param name="promotion">Required cross-target visibility coordination.</param>
    /// <param name="leafPlans">Exact no-gap/no-overlap slice-to-leaf references.</param>
    /// <param name="provenance">Compiler and source attribution for the linked plan set.</param>
    /// <param name="fingerprint">Persisted plan-set fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, affinity, schedule, coverage, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical plan-set content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical plan-set content contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Canonical plan-set content has no portable representation.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSet(
        string schemaVersion,
        MaterializationRebuildRequestReference request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationTargetPlacementPlan placement,
        MaterializationRebuildSchedulingRealization scheduling,
        MaterializationRebuildPromotionPolicy promotion,
        ImmutableArray<MaterializationRebuildLeafPlanBinding> leafPlans,
        ExecutionProvenance provenance,
        MaterializationRebuildPlanSetFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild plan-set schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Membership = membership ?? throw new ArgumentNullException(nameof(membership));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        Scheduling = scheduling ?? throw new ArgumentNullException(nameof(scheduling));
        Promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        if (request.Materialization != membership.Materialization
            || request.Materialization != placement.Materialization
            || membership.Fingerprint != placement.Membership)
        {
            throw new ArgumentException("Request, frozen membership, and placement must retain exact affinity.");
        }

        if (membership.Authority.Completeness != MaterializationRebuildMembershipCompleteness.Complete)
            throw new ArgumentException("A rebuild plan set requires complete authoritative membership.", nameof(membership));
        var placedMembers = placement.Slices.SelectMany(static slice => slice.Subjects).ToHashSet();
        if (!placedMembers.SetEquals(membership.Members))
        {
            throw new ArgumentException(
                "Placement slices must cover every and only frozen membership subject exactly once.",
                nameof(placement));
        }
        ValidateSchedule(placement, scheduling);
        LeafPlans = NormalizeLeafPlans(leafPlans.IsDefault ? [] : leafPlans, placement.Slices);

        var computed = MaterializationRebuildPlanningFingerprinters.ComputePlanSet(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The rebuild plan-set fingerprint does not match canonical content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable rebuild plan-set schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact persisted rebuild-request authority.</summary>
    public MaterializationRebuildRequestReference Request { get; }

    /// <summary>Complete frozen selection evidence.</summary>
    public MaterializationRebuildMembershipEvidence Membership { get; }

    /// <summary>Exact subject-to-target placement and physical capacity evidence.</summary>
    public MaterializationTargetPlacementPlan Placement { get; }

    /// <summary>Effective bounded outer scheduling realization.</summary>
    public MaterializationRebuildSchedulingRealization Scheduling { get; }

    /// <summary>Required cross-target visibility coordination.</summary>
    public MaterializationRebuildPromotionPolicy Promotion { get; }

    /// <summary>Exact slice-to-leaf references in canonical placement-slice order.</summary>
    public ImmutableArray<MaterializationRebuildLeafPlanBinding> LeafPlans { get; }

    /// <summary>Compiler and source attribution for the linked plan set.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of request, evidence, realization, coordination, and leaf bindings.</summary>
    public MaterializationRebuildPlanSetFingerprint Fingerprint { get; }

    /// <summary>Compares plan sets by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Plan set to compare.</param>
    /// <returns><see langword="true"/> when both plan sets have identical canonical content.</returns>
    public bool Equals(MaterializationRebuildPlanSet? other) =>
        ReferenceEquals(this, other) || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Compares an object with this plan set by canonical fingerprint.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a canonically equal plan set.</returns>
    public override bool Equals(object? obj) =>
        obj is MaterializationRebuildPlanSet other && Equals(other);

    /// <summary>Returns a hash code derived from the canonical plan-set fingerprint.</summary>
    /// <returns>A stable hash for canonically equal plan sets.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();

    static void ValidateSchedule(
        MaterializationTargetPlacementPlan placement,
        MaterializationRebuildSchedulingRealization scheduling)
    {
        if (placement.Slices.IsEmpty)
        {
            if (scheduling.MaximumStartsPerActivation != 0 || scheduling.MaximumParallelism != 0)
                throw new ArgumentException("An empty placement requires a zero-work scheduling realization.", nameof(scheduling));
            return;
        }

        var physicalMaximum = placement.CapacityDomains.Sum(static domain => (long)domain.MaximumParallelism);
        if (scheduling.MaximumStartsPerActivation <= 0
            || scheduling.MaximumStartsPerActivation > placement.Slices.Length
            || scheduling.MaximumParallelism <= 0
            || scheduling.MaximumParallelism > placement.Slices.Length
            || scheduling.MaximumParallelism > physicalMaximum)
        {
            throw new ArgumentException("Effective scheduling exceeds placement work or physical capacity bounds.", nameof(scheduling));
        }
    }

    static ImmutableArray<MaterializationRebuildLeafPlanBinding> NormalizeLeafPlans(
        ImmutableArray<MaterializationRebuildLeafPlanBinding> bindings,
        ImmutableArray<MaterializationPlacementSliceReference> slices)
    {
        if (bindings.Any(static binding => binding is null)
            || bindings.GroupBy(static binding => binding.Slice.Id).Any(static group => group.Count() > 1)
            || bindings.GroupBy(static binding => binding.LeafPlan.Plan).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Leaf bindings must be non-null, one per slice, and use each leaf plan once.", nameof(bindings));
        }

        var expected = slices.ToDictionary(static slice => slice.Id);
        if (bindings.Length != expected.Count
            || bindings.Any(binding => !expected.TryGetValue(binding.Slice.Id, out var slice)
                                       || slice.Fingerprint != binding.Slice.Fingerprint))
        {
            throw new ArgumentException("Leaf bindings must cover every exact canonical placement slice once.", nameof(bindings));
        }

        var bindingsBySlice = bindings.ToDictionary(static binding => binding.Slice.Id);
        var ordered = ImmutableArray.CreateBuilder<MaterializationRebuildLeafPlanBinding>(slices.Length);
        foreach (var slice in slices)
            ordered.Add(bindingsBySlice[slice.Id]);
        return ordered.MoveToImmutable();
    }
}

/// <summary>Exact portable reference to one persisted linked rebuild plan set.</summary>
public sealed record MaterializationRebuildPlanSetReference
{
    /// <summary>Current durable plan-set reference schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan-set-reference/v1";

    /// <summary>Creates or deserializes one exact plan-set reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="request">Exact request authority realized by the plan set.</param>
    /// <param name="planSet">Exact persisted plan-set fingerprint.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema is unsupported.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSetReference(
        string schemaVersion,
        MaterializationRebuildRequestReference request,
        MaterializationRebuildPlanSetFingerprint planSet)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild plan-set reference schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
    }

    /// <summary>Exact durable plan-set reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact request authority realized by the plan set.</summary>
    public MaterializationRebuildRequestReference Request { get; }

    /// <summary>Exact persisted plan-set fingerprint.</summary>
    public MaterializationRebuildPlanSetFingerprint PlanSet { get; }

    /// <summary>Creates a reference to one verified plan set.</summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <returns>An exact plan-set reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    public static MaterializationRebuildPlanSetReference FromPlanSet(MaterializationRebuildPlanSet planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        return new(CurrentSchemaVersion, planSet.Request, planSet.Fingerprint);
    }
}

/// <summary>
/// Durable execution claim for one exact leaf of one content-addressed rebuild plan set.
/// </summary>
/// <remarks>
/// The constructor proves internal affinity among the request, leaf reference, and placement slice, but a plan-set
/// reference cannot by itself prove that the binding is a member of the referenced document. Producers should use
/// <see cref="FromPlanSet"/>. Every execution or promotion resolver must reproduce the full fingerprinted plan set and
/// verify the claim before target I/O.
/// </remarks>
public sealed record MaterializationRebuildLeafExecutionAuthority
{
    /// <summary>Current durable leaf-execution-authority schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-leaf-execution-authority/v1";

    /// <summary>Creates or deserializes one internally consistent linked-leaf execution claim.</summary>
    /// <param name="schemaVersion">Exact durable authority schema.</param>
    /// <param name="planSet">Exact linked plan-set authority.</param>
    /// <param name="binding">Exact leaf-plan and full placement-slice binding retained by that plan set.</param>
    /// <exception cref="ArgumentNullException">A required authority is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported or the binding addresses another materialization than the plan-set request.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildLeafExecutionAuthority(
        string schemaVersion,
        MaterializationRebuildPlanSetReference planSet,
        MaterializationRebuildLeafPlanBinding binding)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Leaf execution-authority schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        PlanSet = planSet ?? throw new ArgumentNullException(nameof(planSet));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (planSet.Request.Materialization != binding.Slice.Materialization)
        {
            throw new ArgumentException(
                "A leaf execution authority must address the exact materialization retained by its plan-set request.",
                nameof(binding));
        }
    }

    /// <summary>Exact durable authority schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact content-addressed plan set claimed to contain the leaf.</summary>
    public MaterializationRebuildPlanSetReference PlanSet { get; }

    /// <summary>Exact leaf-plan and full placement-slice binding to verify against <see cref="PlanSet"/>.</summary>
    public MaterializationRebuildLeafPlanBinding Binding { get; }

    /// <summary>Exact persisted leaf-plan reference.</summary>
    [JsonIgnore]
    public MaterializationRebuildPlanReference LeafPlan => Binding.LeafPlan;

    /// <summary>Full independently promoted placement authority bound to the leaf.</summary>
    [JsonIgnore]
    public MaterializationPlacementSliceReference PlacementSlice => Binding.Slice;

    /// <summary>Creates an authority only when a verified plan set contains the exact supplied leaf and slice.</summary>
    /// <param name="planSet">Canonical constructor-verified linked plan set.</param>
    /// <param name="leafPlan">Canonical constructor-verified leaf plan.</param>
    /// <returns>The exact linked leaf execution authority.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="leafPlan"/> is detached from <paramref name="planSet"/>, its full placement slice differs, or
    /// its target descriptor is not the exact member pinned by the plan set's backend pool.
    /// </exception>
    public static MaterializationRebuildLeafExecutionAuthority FromPlanSet(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlan leafPlan)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(leafPlan);
        var leafReference = MaterializationRebuildPlanReference.FromPlan(leafPlan);
        var binding = planSet.LeafPlans.SingleOrDefault(candidate =>
            candidate.LeafPlan == leafReference
            && candidate.Slice.Equals(leafPlan.PlacementSlice));
        if (binding is null)
        {
            throw new ArgumentException(
                "The rebuild leaf and its full placement slice are not linked by the supplied verified plan set.",
                nameof(leafPlan));
        }
        var pinnedTarget = planSet.Placement.BackendPool.Definition.Members.SingleOrDefault(
            member => member.Id == binding.Slice.Target);
        if (pinnedTarget is null || !MaterializationContract.CanonicalEquals(pinnedTarget, leafPlan.Target))
        {
            throw new ArgumentException(
                "The rebuild leaf target differs from the exact descriptor pinned by its linked backend pool.",
                nameof(leafPlan));
        }

        return new(
            CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            binding);
    }
}
