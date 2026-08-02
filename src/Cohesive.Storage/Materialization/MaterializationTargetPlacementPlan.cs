using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable identity of one independently promoted target placement slice.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationPlacementSliceId
{
    /// <summary>Creates a placement-slice identity.</summary>
    /// <param name="value">Stable identity scoped to one placement plan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationPlacementSliceId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Stable placement-slice identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable placement-slice identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one shared physical scheduling-capacity domain.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationPhysicalCapacityDomainId
{
    /// <summary>Creates a physical capacity-domain identity.</summary>
    /// <param name="value">Stable physical admission-domain identity, independent of promotion identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationPhysicalCapacityDomainId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Stable physical admission-domain identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable capacity-domain identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Bounded physical concurrency evidence shared by one or more target slices.</summary>
public sealed record MaterializationPhysicalCapacityDomain
{
    /// <summary>Creates one physical scheduling-capacity domain.</summary>
    /// <param name="id">Stable physical admission-domain identity.</param>
    /// <param name="maximumParallelism">Maximum concurrently active leaf rebuilds in this domain.</param>
    /// <param name="evidenceReferences">Attributable adapter, deployment, profile, or override evidence.</param>
    /// <exception cref="ArgumentException">The identity or evidence is absent or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumParallelism"/> is not positive.</exception>
    [JsonConstructor]
    public MaterializationPhysicalCapacityDomain(
        MaterializationPhysicalCapacityDomainId id,
        int maximumParallelism,
        ImmutableArray<string> evidenceReferences)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        if (maximumParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumParallelism), maximumParallelism, "A capacity bound must be positive.");
        Id = id;
        MaximumParallelism = maximumParallelism;
        EvidenceReferences = MaterializationCapabilityOrdering.NormalizeStrings(
            evidenceReferences.IsDefault ? [] : evidenceReferences,
            nameof(evidenceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Stable physical admission-domain identity.</summary>
    public MaterializationPhysicalCapacityDomainId Id { get; }

    /// <summary>Maximum concurrently active leaf rebuilds in this domain.</summary>
    public int MaximumParallelism { get; }

    /// <summary>Attributable physical-capacity evidence in canonical ordinal order.</summary>
    public ImmutableArray<string> EvidenceReferences { get; }

    /// <summary>Compares capacity evidence structurally, including canonical proof references.</summary>
    /// <param name="other">Capacity domain to compare.</param>
    /// <returns><see langword="true"/> when identity, bound, and evidence are equal.</returns>
    public bool Equals(MaterializationPhysicalCapacityDomain? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && MaximumParallelism == other.MaximumParallelism
        && EvidenceReferences.SequenceEqual(other.EvidenceReferences);

    /// <summary>Returns a structural hash code for identity, bound, and evidence.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(MaximumParallelism);
        foreach (var reference in EvidenceReferences)
            hash.Add(reference, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Unfrozen candidate assignment of one selected subject to one target.</summary>
/// <remarks>The placement compiler, rather than this observation type, detects duplicate subjects structurally.</remarks>
public sealed record MaterializationTargetPlacementAssignment
{
    /// <summary>Creates one candidate subject-to-target assignment.</summary>
    /// <param name="subject">Selected provider-neutral placement subject.</param>
    /// <param name="target">Exact target intended to own that subject's independent generation namespace.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public MaterializationTargetPlacementAssignment(
        MaterializationPlacementSubjectId subject,
        MaterializationTargetId target)
    {
        MaterializationContract.RequireDefinedIdentity(subject.Value, nameof(subject));
        MaterializationContract.RequireDefinedIdentity(target.Value, nameof(target));
        Subject = subject;
        Target = target;
    }

    /// <summary>Selected provider-neutral placement subject.</summary>
    public MaterializationPlacementSubjectId Subject { get; }

    /// <summary>Exact target assigned to the subject.</summary>
    public MaterializationTargetId Target { get; }
}

/// <summary>Unfrozen candidate mapping from one target to a physical scheduling-capacity domain.</summary>
public sealed record MaterializationTargetCapacityAssignment
{
    /// <summary>Creates one candidate target-to-capacity-domain mapping.</summary>
    /// <param name="target">Exact placement target.</param>
    /// <param name="capacityDomain">Physical scheduling-capacity domain shared by the target.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public MaterializationTargetCapacityAssignment(
        MaterializationTargetId target,
        MaterializationPhysicalCapacityDomainId capacityDomain)
    {
        MaterializationContract.RequireDefinedIdentity(target.Value, nameof(target));
        MaterializationContract.RequireDefinedIdentity(capacityDomain.Value, nameof(capacityDomain));
        Target = target;
        CapacityDomain = capacityDomain;
    }

    /// <summary>Exact placement target.</summary>
    public MaterializationTargetId Target { get; }

    /// <summary>Physical scheduling-capacity domain shared by the target.</summary>
    public MaterializationPhysicalCapacityDomainId CapacityDomain { get; }
}

/// <summary>Independent, content-fenced placement slice for one target promotion namespace.</summary>
/// <remarks>
/// Capacity-domain identity is deliberately excluded. Changing physical scheduling evidence does not change the
/// target/subject routing and promotion authority to which a leaf rebuild plan is bound.
/// </remarks>
public sealed class MaterializationPlacementSliceReference : IEquatable<MaterializationPlacementSliceReference>
{
    /// <summary>Current durable placement-slice schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-placement-slice/v1";

    /// <summary>Creates and verifies one independently fingerprinted target slice.</summary>
    /// <param name="schemaVersion">Exact durable slice schema.</param>
    /// <param name="id">Stable slice identity.</param>
    /// <param name="materialization">Exact materialization definition.</param>
    /// <param name="membership">Exact frozen membership fingerprint from which this slice was drawn.</param>
    /// <param name="pool">Pinned backend-pool definition.</param>
    /// <param name="target">One independently promoted target namespace.</param>
    /// <param name="subjects">Exact non-empty subjects assigned to <paramref name="target"/>.</param>
    /// <param name="fingerprint">Persisted slice fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, identity, subjects, affinity, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical slice content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical slice content contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Canonical slice content has no portable representation.</exception>
    [JsonConstructor]
    public MaterializationPlacementSliceReference(
        string schemaVersion,
        MaterializationPlacementSliceId id,
        MaterializationDefinitionReference materialization,
        MaterializationRebuildMembershipFingerprint membership,
        MaterializationBackendPoolReference pool,
        MaterializationTargetId target,
        ImmutableArray<MaterializationPlacementSubjectId> subjects,
        MaterializationPlacementSliceFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Placement-slice schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(target.Value, nameof(target));
        Id = id;
        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        Membership = membership ?? throw new ArgumentNullException(nameof(membership));
        Pool = pool ?? throw new ArgumentNullException(nameof(pool));
        if (pool.Materialization != materialization)
            throw new ArgumentException("A placement slice requires a pool serving its exact materialization.", nameof(pool));
        Target = target;
        Subjects = MaterializationRebuildPlanningContract.NormalizeSubjects(
            subjects.IsDefault ? [] : subjects,
            nameof(subjects),
            allowEmpty: false);

        var computed = MaterializationRebuildPlanningFingerprinters.ComputePlacementSlice(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The placement-slice fingerprint does not match canonical content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact durable placement-slice schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable slice identity.</summary>
    public MaterializationPlacementSliceId Id { get; }

    /// <summary>Exact materialization definition.</summary>
    public MaterializationDefinitionReference Materialization { get; }

    /// <summary>Exact frozen membership fingerprint from which this slice was drawn.</summary>
    public MaterializationRebuildMembershipFingerprint Membership { get; }

    /// <summary>Pinned backend-pool definition.</summary>
    public MaterializationBackendPoolReference Pool { get; }

    /// <summary>One independently promoted target namespace.</summary>
    public MaterializationTargetId Target { get; }

    /// <summary>Exact subjects assigned to the target in canonical ordinal order.</summary>
    public ImmutableArray<MaterializationPlacementSubjectId> Subjects { get; }

    /// <summary>Deterministic fingerprint of materialization, membership, pool, target, and subject content.</summary>
    public MaterializationPlacementSliceFingerprint Fingerprint { get; }

    /// <summary>Compares slices by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Slice to compare.</param>
    /// <returns><see langword="true"/> when both slices have identical routing and promotion content.</returns>
    public bool Equals(MaterializationPlacementSliceReference? other) =>
        ReferenceEquals(this, other) || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Compares an object with this slice by canonical fingerprint.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a canonically equal slice.</returns>
    public override bool Equals(object? obj) =>
        obj is MaterializationPlacementSliceReference other && Equals(other);

    /// <summary>Returns a hash code derived from the canonical slice fingerprint.</summary>
    /// <returns>A stable hash for canonically equal slices.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();
}

/// <summary>Canonical mapping from one placement slice to separate physical scheduling evidence.</summary>
public sealed record MaterializationPlacementSliceCapacityBinding
{
    /// <summary>Creates one slice-to-capacity-domain binding.</summary>
    /// <param name="slice">Stable placement-slice identity.</param>
    /// <param name="capacityDomain">Physical scheduling-capacity domain.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public MaterializationPlacementSliceCapacityBinding(
        MaterializationPlacementSliceId slice,
        MaterializationPhysicalCapacityDomainId capacityDomain)
    {
        MaterializationContract.RequireDefinedIdentity(slice.Value, nameof(slice));
        MaterializationContract.RequireDefinedIdentity(capacityDomain.Value, nameof(capacityDomain));
        Slice = slice;
        CapacityDomain = capacityDomain;
    }

    /// <summary>Stable placement-slice identity.</summary>
    public MaterializationPlacementSliceId Slice { get; }

    /// <summary>Separate physical scheduling-capacity domain.</summary>
    public MaterializationPhysicalCapacityDomainId CapacityDomain { get; }
}

/// <summary>Canonical exact subject-to-target placement with separately mapped physical capacity evidence.</summary>
public sealed class MaterializationTargetPlacementPlan : IEquatable<MaterializationTargetPlacementPlan>
{
    /// <summary>Current portable target-placement schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-target-placement-plan/v1";

    /// <summary>Creates and verifies a canonical placement plan.</summary>
    /// <param name="schemaVersion">Exact portable placement-plan schema.</param>
    /// <param name="materialization">Exact materialization definition.</param>
    /// <param name="membership">Exact frozen membership fingerprint.</param>
    /// <param name="backendPool">Complete pinned backend-pool definition evidence.</param>
    /// <param name="slices">One independently fingerprinted, non-empty subject slice per selected target.</param>
    /// <param name="capacityDomains">Complete physical capacity-domain evidence.</param>
    /// <param name="capacityBindings">Exactly one separate capacity-domain binding per slice.</param>
    /// <param name="provenance">Producer and source attribution for placement decisions.</param>
    /// <param name="fingerprint">Persisted placement-plan fingerprint to verify, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, affinity, exact coverage, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical placement content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical placement content contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Canonical placement content has no portable representation.</exception>
    [JsonConstructor]
    public MaterializationTargetPlacementPlan(
        string schemaVersion,
        MaterializationDefinitionReference materialization,
        MaterializationRebuildMembershipFingerprint membership,
        MaterializationBackendPoolDocument backendPool,
        ImmutableArray<MaterializationPlacementSliceReference> slices,
        ImmutableArray<MaterializationPhysicalCapacityDomain> capacityDomains,
        ImmutableArray<MaterializationPlacementSliceCapacityBinding> capacityBindings,
        ExecutionProvenance provenance,
        MaterializationTargetPlacementPlanFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Target-placement schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        Membership = membership ?? throw new ArgumentNullException(nameof(membership));
        BackendPool = backendPool ?? throw new ArgumentNullException(nameof(backendPool));
        Pool = MaterializationBackendPoolReference.FromDocument(backendPool);
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (Pool.Materialization != materialization)
            throw new ArgumentException("The pinned backend pool must serve the exact placed materialization.", nameof(backendPool));

        Slices = NormalizeSlices(slices.IsDefault ? [] : slices, materialization, membership, Pool, backendPool);
        CapacityDomains = NormalizeCapacityDomains(capacityDomains.IsDefault ? [] : capacityDomains, Slices);
        CapacityBindings = NormalizeCapacityBindings(
            capacityBindings.IsDefault ? [] : capacityBindings,
            Slices,
            CapacityDomains);

        var computed = MaterializationRebuildPlanningFingerprinters.ComputePlacementPlan(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The target-placement fingerprint does not match canonical content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable target-placement schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact materialization definition.</summary>
    public MaterializationDefinitionReference Materialization { get; }

    /// <summary>Exact frozen membership fingerprint.</summary>
    public MaterializationRebuildMembershipFingerprint Membership { get; }

    /// <summary>Complete pinned backend-pool definition evidence.</summary>
    public MaterializationBackendPoolDocument BackendPool { get; }

    /// <summary>Exact backend-pool reference derived from <see cref="BackendPool"/>.</summary>
    [JsonIgnore]
    public MaterializationBackendPoolReference Pool { get; }

    /// <summary>One independently fingerprinted subject slice per selected target, in target order.</summary>
    public ImmutableArray<MaterializationPlacementSliceReference> Slices { get; }

    /// <summary>Complete physical capacity-domain evidence in canonical identity order.</summary>
    public ImmutableArray<MaterializationPhysicalCapacityDomain> CapacityDomains { get; }

    /// <summary>Exactly one separate capacity-domain binding per slice, in slice order.</summary>
    public ImmutableArray<MaterializationPlacementSliceCapacityBinding> CapacityBindings { get; }

    /// <summary>Producer and source attribution for placement decisions.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of placement, capacity, pool, and provenance content.</summary>
    public MaterializationTargetPlacementPlanFingerprint Fingerprint { get; }

    /// <summary>Compares placement plans by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Placement plan to compare.</param>
    /// <returns><see langword="true"/> when both plans have identical canonical content.</returns>
    public bool Equals(MaterializationTargetPlacementPlan? other) =>
        ReferenceEquals(this, other) || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Compares an object with this placement plan by canonical fingerprint.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a canonically equal placement plan.</returns>
    public override bool Equals(object? obj) =>
        obj is MaterializationTargetPlacementPlan other && Equals(other);

    /// <summary>Returns a hash code derived from the canonical placement-plan fingerprint.</summary>
    /// <returns>A stable hash for canonically equal placement plans.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();

    static ImmutableArray<MaterializationPlacementSliceReference> NormalizeSlices(
        ImmutableArray<MaterializationPlacementSliceReference> slices,
        MaterializationDefinitionReference materialization,
        MaterializationRebuildMembershipFingerprint membership,
        MaterializationBackendPoolReference pool,
        MaterializationBackendPoolDocument backendPool)
    {
        if (slices.Any(static slice => slice is null))
            throw new ArgumentException("Placement slices cannot contain null entries.", nameof(slices));
        if (slices.GroupBy(static slice => slice.Id).Any(static group => group.Count() > 1)
            || slices.GroupBy(static slice => slice.Target).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A placement plan requires one unique slice per target.", nameof(slices));
        }
        if (slices.SelectMany(static slice => slice.Subjects).GroupBy(static subject => subject).Any(static group => group.Count() > 1))
            throw new ArgumentException("A placement subject cannot appear in more than one target slice.", nameof(slices));

        var targets = backendPool.Definition.Members.Select(static member => member.Id).ToHashSet();
        foreach (var slice in slices)
        {
            if (slice.Materialization != materialization || slice.Membership != membership || slice.Pool != pool)
                throw new ArgumentException("Every placement slice must retain the plan's exact materialization, membership, and pool.", nameof(slices));
            if (!targets.Contains(slice.Target))
                throw new ArgumentException("Every placement slice target must belong to the pinned backend pool.", nameof(slices));
        }

        return [.. slices.OrderBy(static slice => slice.Target.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<MaterializationPhysicalCapacityDomain> NormalizeCapacityDomains(
        ImmutableArray<MaterializationPhysicalCapacityDomain> domains,
        ImmutableArray<MaterializationPlacementSliceReference> slices)
    {
        if (domains.Any(static domain => domain is null)
            || domains.GroupBy(static domain => domain.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Capacity domains must be non-null and uniquely identified.", nameof(domains));
        }
        if (slices.IsEmpty != domains.IsEmpty)
            throw new ArgumentException("Capacity-domain evidence exists exactly when the placement has slices.", nameof(domains));
        return [.. domains.OrderBy(static domain => domain.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<MaterializationPlacementSliceCapacityBinding> NormalizeCapacityBindings(
        ImmutableArray<MaterializationPlacementSliceCapacityBinding> bindings,
        ImmutableArray<MaterializationPlacementSliceReference> slices,
        ImmutableArray<MaterializationPhysicalCapacityDomain> domains)
    {
        if (bindings.Any(static binding => binding is null)
            || bindings.GroupBy(static binding => binding.Slice).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Capacity bindings must be non-null and identify every slice once.", nameof(bindings));
        }

        var sliceIds = slices.Select(static slice => slice.Id).ToHashSet();
        var domainIds = domains.Select(static domain => domain.Id).ToHashSet();
        if (!sliceIds.SetEquals(bindings.Select(static binding => binding.Slice))
            || bindings.Any(binding => !domainIds.Contains(binding.CapacityDomain))
            || !domainIds.SetEquals(bindings.Select(static binding => binding.CapacityDomain)))
        {
            throw new ArgumentException(
                "Capacity bindings must cover every slice and use every declared capacity domain without extras.",
                nameof(bindings));
        }

        var bindingsBySlice = bindings.ToDictionary(static binding => binding.Slice);
        var ordered = ImmutableArray.CreateBuilder<MaterializationPlacementSliceCapacityBinding>(slices.Length);
        foreach (var slice in slices)
            ordered.Add(bindingsBySlice[slice.Id]);
        return ordered.MoveToImmutable();
    }
}
