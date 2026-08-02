using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Base wire contract shared by typed materialization rebuild-planning fingerprints.</summary>
public abstract record MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates and validates a typed rebuild-planning fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Domain-separated canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or the digest is not 64 lowercase hexadecimal characters.</exception>
    protected MaterializationRebuildPlanningFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = MaterializationContract.RequireUnicodeIdentity(algorithm, nameof(algorithm));
        Canonicalization = MaterializationContract.RequireUnicodeIdentity(canonicalization, nameof(canonicalization));
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));
        if (value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A rebuild-planning fingerprint must be a 64-character lowercase hexadecimal digest.", nameof(value));
        }
    }

    /// <summary>Digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Domain-separated canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Typed fingerprint of one explicit or Relations-backed placement selector.</summary>
public sealed record MaterializationPlacementSelectionFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a placement-selector fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationPlacementSelectionFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Typed fingerprint of one canonical rebuild request.</summary>
public sealed record MaterializationRebuildRequestFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a rebuild-request fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationRebuildRequestFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Typed fingerprint of one complete frozen membership artifact.</summary>
public sealed record MaterializationRebuildMembershipFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a rebuild-membership fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationRebuildMembershipFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Typed fingerprint of one independently promoted target placement slice.</summary>
public sealed record MaterializationPlacementSliceFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a placement-slice fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationPlacementSliceFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Typed fingerprint of one canonical target-placement plan.</summary>
public sealed record MaterializationTargetPlacementPlanFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a target-placement fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationTargetPlacementPlanFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Typed fingerprint of one fully linked rebuild plan set.</summary>
public sealed record MaterializationRebuildPlanSetFingerprint : MaterializationRebuildPlanningFingerprint
{
    /// <summary>Creates a rebuild plan-set fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is absent or ill-formed Unicode, or <paramref name="value"/> is not a 64-character lowercase hexadecimal digest.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlanSetFingerprint(string algorithm, string canonicalization, string value)
        : base(algorithm, canonicalization, value) { }
}

/// <summary>Domain-separated canonical SHA-256 fingerprints for rebuild-planning IR.</summary>
public static class MaterializationRebuildPlanningFingerprinters
{
    /// <summary>Digest algorithm used by every v1 rebuild-planning fingerprint.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Computes the fingerprint of one exact placement selector.</summary>
    /// <param name="selection">Explicit or Relations-backed selector.</param>
    /// <returns>A domain-separated selector fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical selector content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical selector content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical selector content has no portable representation.</exception>
    public static MaterializationPlacementSelectionFingerprint ComputeSelection(
        MaterializationPlacementSubjectSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        const string canonicalization = "cohesive-materialization-placement-selection/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new SelectionInput(
            "cohesive-materialization-placement-selection/v1",
            selection)));
    }

    /// <summary>Computes the fingerprint of every rebuild-request semantic and provenance field.</summary>
    /// <param name="request">Normalized rebuild request.</param>
    /// <returns>A domain-separated request fingerprint excluding its self-fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical request content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical request content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical request content has no portable representation.</exception>
    public static MaterializationRebuildRequestFingerprint ComputeRequest(MaterializationRebuildRequestDocument request)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string canonicalization = "cohesive-materialization-rebuild-request/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new RequestInput(
                request.SchemaVersion,
                request.Materialization,
                request.Selection,
                request.Placement,
                request.Scheduling,
                request.Promotion,
                request.Provenance)));
    }

    /// <summary>Computes the fingerprint of complete frozen membership and authority evidence.</summary>
    /// <param name="membership">Normalized membership evidence.</param>
    /// <returns>A domain-separated membership fingerprint excluding its self-fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="membership"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical membership content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical membership content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical membership content has no portable representation.</exception>
    public static MaterializationRebuildMembershipFingerprint ComputeMembership(
        MaterializationRebuildMembershipEvidence membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        const string canonicalization = "cohesive-materialization-rebuild-membership/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new MembershipInput(
                membership.SchemaVersion,
                membership.Materialization,
                membership.Selector,
                membership.Members,
                membership.Authority,
                membership.Provenance)));
    }

    /// <summary>Computes the routing/promotion fingerprint of one placement slice.</summary>
    /// <param name="slice">Normalized placement slice.</param>
    /// <returns>A fingerprint excluding physical capacity and enclosing plan-set identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slice"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical slice content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical slice content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical slice content has no portable representation.</exception>
    public static MaterializationPlacementSliceFingerprint ComputePlacementSlice(
        MaterializationPlacementSliceReference slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        const string canonicalization = "cohesive-materialization-placement-slice/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new PlacementSliceInput(
                slice.SchemaVersion,
                slice.Id,
                slice.Materialization,
                slice.Membership,
                slice.Pool,
                slice.Target,
                slice.Subjects)));
    }

    /// <summary>Computes the fingerprint of exact placement, physical capacity evidence, and provenance.</summary>
    /// <param name="placement">Normalized target-placement plan.</param>
    /// <returns>A domain-separated placement-plan fingerprint excluding its self-fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical placement content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical placement content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical placement content has no portable representation.</exception>
    public static MaterializationTargetPlacementPlanFingerprint ComputePlacementPlan(
        MaterializationTargetPlacementPlan placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        const string canonicalization = "cohesive-materialization-target-placement-plan/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new PlacementPlanInput(
                placement.SchemaVersion,
                placement.Materialization,
                placement.Membership,
                placement.BackendPool,
                placement.Slices,
                placement.CapacityDomains,
                placement.CapacityBindings,
                placement.Provenance)));
    }

    /// <summary>Computes the fingerprint of one complete linked rebuild plan set.</summary>
    /// <param name="planSet">Normalized linked plan set.</param>
    /// <returns>A domain-separated plan-set fingerprint excluding its self-fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical plan-set content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical plan-set content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical plan-set content has no portable representation.</exception>
    public static MaterializationRebuildPlanSetFingerprint ComputePlanSet(MaterializationRebuildPlanSet planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        const string canonicalization = "cohesive-materialization-rebuild-plan-set/v1-c14n/v1";
        return new(Algorithm, canonicalization, Digest(new PlanSetInput(
                planSet.SchemaVersion,
                planSet.Request,
                planSet.Membership,
                planSet.Placement,
                planSet.Scheduling,
                planSet.Promotion,
                planSet.LeafPlans,
                planSet.Provenance)));
    }

    static string Digest<TInput>(TInput input)
        where TInput : class
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(input, MaterializationJsonSerializer.CreateOptions());
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    sealed record SelectionInput(string SchemaVersion, MaterializationPlacementSubjectSelection Selection);

    sealed record RequestInput(
        string SchemaVersion,
        MaterializationDocument Materialization,
        MaterializationPlacementSubjectSelection Selection,
        MaterializationRebuildPlacementPolicy Placement,
        MaterializationRebuildSchedulingPolicy Scheduling,
        MaterializationRebuildPromotionPolicy Promotion,
        ExecutionProvenance Provenance);

    sealed record MembershipInput(
        string SchemaVersion,
        MaterializationDefinitionReference Materialization,
        MaterializationPlacementSelectionFingerprint Selector,
        ImmutableArray<MaterializationPlacementSubjectId> Members,
        MaterializationRebuildMembershipAuthority Authority,
        ExecutionProvenance Provenance);

    sealed record PlacementSliceInput(
        string SchemaVersion,
        MaterializationPlacementSliceId Id,
        MaterializationDefinitionReference Materialization,
        MaterializationRebuildMembershipFingerprint Membership,
        MaterializationBackendPoolReference Pool,
        MaterializationTargetId Target,
        ImmutableArray<MaterializationPlacementSubjectId> Subjects);

    sealed record PlacementPlanInput(
        string SchemaVersion,
        MaterializationDefinitionReference Materialization,
        MaterializationRebuildMembershipFingerprint Membership,
        MaterializationBackendPoolDocument BackendPool,
        ImmutableArray<MaterializationPlacementSliceReference> Slices,
        ImmutableArray<MaterializationPhysicalCapacityDomain> CapacityDomains,
        ImmutableArray<MaterializationPlacementSliceCapacityBinding> CapacityBindings,
        ExecutionProvenance Provenance);

    sealed record PlanSetInput(
        string SchemaVersion,
        MaterializationRebuildRequestReference Request,
        MaterializationRebuildMembershipEvidence Membership,
        MaterializationTargetPlacementPlan Placement,
        MaterializationRebuildSchedulingRealization Scheduling,
        MaterializationRebuildPromotionPolicy Promotion,
        ImmutableArray<MaterializationRebuildLeafPlanBinding> LeafPlans,
        ExecutionProvenance Provenance);
}

internal static class MaterializationRebuildPlanningContract
{
    internal static ImmutableArray<MaterializationPlacementSubjectId> NormalizeSubjects(
        ImmutableArray<MaterializationPlacementSubjectId> subjects,
        string parameterName,
        bool allowEmpty)
    {
        if (!allowEmpty && subjects.IsDefaultOrEmpty)
            throw new ArgumentException("At least one placement subject is required.", parameterName);
        if (subjects.Any(static subject => string.IsNullOrWhiteSpace(subject.Value)))
            throw new ArgumentException("Placement subjects cannot contain default identities.", parameterName);
        if (subjects.GroupBy(static subject => subject).Any(static group => group.Count() > 1))
            throw new ArgumentException("Placement subjects cannot repeat an identity.", parameterName);

        var canonical = true;
        for (var index = 1; index < subjects.Length; index++)
        {
            if (string.Compare(subjects[index - 1].Value, subjects[index].Value, StringComparison.Ordinal) > 0)
            {
                canonical = false;
                break;
            }
        }
        return canonical ? subjects : [.. subjects.OrderBy(static subject => subject.Value, StringComparer.Ordinal)];
    }

}
