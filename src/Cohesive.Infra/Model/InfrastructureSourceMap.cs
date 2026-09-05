using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra;

/// <summary>One non-semantic attribution from a canonical infrastructure construct to its producer source.</summary>
public sealed record InfrastructureSourceProvenance
{
    /// <summary>Creates one infrastructure source-map entry.</summary>
    /// <param name="subject">Canonical reference to the infrastructure construct that was produced.</param>
    /// <param name="source">Stable producer-defined source reference.</param>
    /// <param name="description">Optional human-readable source description.</param>
    /// <exception cref="ArgumentException">A reference is default or <paramref name="description"/> is white-space.</exception>
    [JsonConstructor]
    public InfrastructureSourceProvenance(
        SourceReference subject,
        SourceReference source,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("Infrastructure source provenance requires a canonical subject.", nameof(subject));
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("Infrastructure source provenance requires a source.", nameof(source));
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("An infrastructure source description cannot be white-space.", nameof(description));

        Subject = subject;
        Source = source;
        Description = description;
    }

    /// <summary>Canonical infrastructure construct produced by the source.</summary>
    public SourceReference Subject { get; }

    /// <summary>Stable producer-defined source reference.</summary>
    public SourceReference Source { get; }

    /// <summary>Optional human-readable source description.</summary>
    public string? Description { get; }
}

/// <summary>Normalized, non-semantic attribution for canonical infrastructure constructs.</summary>
/// <remarks>
/// Source maps are persisted and comparable provenance, but are deliberately excluded from semantic fingerprints.
/// A source-file move or line-number change therefore does not change the identity of an equivalent infrastructure
/// declaration.
/// </remarks>
public sealed record InfrastructureSourceMap
{
    /// <summary>Empty infrastructure source map.</summary>
    public static InfrastructureSourceMap Empty { get; } = new([]);

    /// <summary>Creates a normalized infrastructure source map.</summary>
    /// <param name="entries">Attributions to normalize by subject and source.</param>
    /// <exception cref="ArgumentException"><paramref name="entries"/> contains null or duplicate entries.</exception>
    [JsonConstructor]
    public InfrastructureSourceMap(ImmutableArray<InfrastructureSourceProvenance> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            Entries = [];
            return;
        }

        if (entries.Any(static entry => entry is null))
            throw new ArgumentException("Infrastructure source maps cannot contain null entries.", nameof(entries));

        var ordered = entries.Sort(static (left, right) =>
        {
            var comparison = left.Subject.CompareTo(right.Subject);
            if (comparison != 0)
                return comparison;

            comparison = left.Source.CompareTo(right.Source);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Description, right.Description);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1] == ordered[index])
            {
                throw new ArgumentException(
                    $"Infrastructure source mapping '{ordered[index].Subject}' to '{ordered[index].Source}' is duplicated.",
                    nameof(entries));
            }
        }

        Entries = ordered;
    }

    /// <summary>Source mappings in deterministic subject and source order.</summary>
    public ImmutableArray<InfrastructureSourceProvenance> Entries { get; }

    /// <summary>Resolves every distinct producer source for one exact canonical subject.</summary>
    /// <param name="subject">Canonical infrastructure construct whose sources are requested.</param>
    /// <returns>Distinct source references in deterministic order.</returns>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is default.</exception>
    public ImmutableArray<SourceReference> Resolve(SourceReference subject)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("Infrastructure source-map lookup requires a canonical subject.", nameof(subject));

        return
        [
            .. Entries
                .Where(entry => entry.Subject == subject)
                .Select(static entry => entry.Source)
                .Distinct()
        ];
    }

    /// <summary>Compares source maps by their normalized entries.</summary>
    /// <param name="other">Other source map.</param>
    /// <returns><see langword="true"/> when both maps contain the same entries.</returns>
    public bool Equals(InfrastructureSourceMap? other) =>
        ReferenceEquals(this, other)
        || other is not null && Entries.SequenceEqual(other.Entries);

    /// <summary>Returns a structural hash code for every source mapping.</summary>
    /// <returns>A hash code derived from the normalized entries.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }
}

/// <summary>Typed projections from infrastructure identities to canonical source-map subjects and evidence references.</summary>
public static class InfrastructureSourceReferences
{
    /// <summary>Projects a logical infrastructure node identity.</summary>
    /// <param name="node">Canonical workload or resource identity.</param>
    /// <returns>A canonical infrastructure-node reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default or cannot form a source reference.</exception>
    public static SourceReference Node(InfrastructureNodeId node) =>
        Create("infrastructure-node", node.Value, nameof(node));

    /// <summary>Projects a canonical readiness-dependency identity.</summary>
    /// <param name="dependency">Canonical readiness dependency.</param>
    /// <returns>A canonical infrastructure-readiness-dependency reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="dependency"/> is default or cannot form a source reference.</exception>
    public static SourceReference ReadinessDependency(InfrastructureReadinessDependencyId dependency) =>
        Create("infrastructure-readiness-dependency", dependency.Value, nameof(dependency));

    /// <summary>Projects an exact physical-resource identity.</summary>
    /// <param name="physicalResource">Exact physical resource.</param>
    /// <returns>A canonical infrastructure-physical-resource reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="physicalResource"/> is default or cannot form a source reference.</exception>
    public static SourceReference PhysicalResource(InfrastructurePhysicalResourceId physicalResource) =>
        Create("infrastructure-physical-resource", physicalResource.Value, nameof(physicalResource));

    /// <summary>Projects an interpretation-target identity.</summary>
    /// <param name="target">Canonical infrastructure target.</param>
    /// <returns>A canonical infrastructure-target reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is default or cannot form a source reference.</exception>
    public static SourceReference Target(InfrastructureTargetId target) =>
        Create("infrastructure-target", target.Value, nameof(target));

    /// <summary>Projects a target-facility identity.</summary>
    /// <param name="facility">Canonical target-local facility.</param>
    /// <returns>A canonical infrastructure-facility reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is default or cannot form a source reference.</exception>
    public static SourceReference Facility(InfrastructureTargetFacilityId facility) =>
        Create("infrastructure-facility", facility.Value, nameof(facility));

    /// <summary>Projects a capability-evidence identity.</summary>
    /// <param name="evidence">Canonical capability-evidence identity.</param>
    /// <returns>A canonical infrastructure-capability-evidence reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="evidence"/> is default or cannot form a source reference.</exception>
    public static SourceReference CapabilityEvidence(InfrastructureCapabilityEvidenceId evidence) =>
        Create("infrastructure-capability-evidence", evidence.Value, nameof(evidence));

    /// <summary>Projects a capability-composition rule identity.</summary>
    /// <param name="rule">Canonical capability-rule identity.</param>
    /// <returns>A canonical infrastructure-capability-rule reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is default or cannot form a source reference.</exception>
    public static SourceReference CapabilityRule(InfrastructureCapabilityRuleId rule) =>
        Create("infrastructure-capability-rule", rule.Value, nameof(rule));

    /// <summary>Projects an operating-boundary identity.</summary>
    /// <param name="boundary">Canonical operating-boundary identity.</param>
    /// <returns>A canonical infrastructure-operating-boundary reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="boundary"/> is default or cannot form a source reference.</exception>
    public static SourceReference OperatingBoundary(InfrastructureOperatingBoundaryId boundary) =>
        Create("infrastructure-operating-boundary", boundary.Value, nameof(boundary));

    /// <summary>Projects a lifecycle-authority identity.</summary>
    /// <param name="authority">Canonical lifecycle authority.</param>
    /// <returns>A canonical infrastructure-lifecycle-authority reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is default or cannot form a source reference.</exception>
    public static SourceReference LifecycleAuthority(InfrastructureLifecycleAuthorityId authority) =>
        Create("infrastructure-lifecycle-authority", authority.Value, nameof(authority));

    /// <summary>Projects a target-facility manifest identity.</summary>
    /// <param name="manifest">Canonical target-facility manifest identity.</param>
    /// <returns>A canonical infrastructure-target-facility-manifest reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="manifest"/> is default or cannot form a source reference.</exception>
    public static SourceReference TargetFacilityManifest(InfrastructureTargetFacilityManifestId manifest) =>
        Create("infrastructure-target-facility-manifest", manifest.Value, nameof(manifest));

    /// <summary>Projects an exact target-facility manifest reference.</summary>
    /// <param name="manifest">Exact canonical target-facility manifest.</param>
    /// <returns>A canonical reference fenced by the manifest fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    public static SourceReference TargetFacilityManifest(InfrastructureTargetFacilityManifestReference manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Create(
            "infrastructure-target-facility-manifest",
            $"{manifest.Id.Value}#{manifest.Fingerprint.Algorithm}:{manifest.Fingerprint.Canonicalization}:{manifest.Fingerprint.Value}",
            nameof(manifest));
    }

    /// <summary>Projects a target-deployment manifest identity.</summary>
    /// <param name="manifest">Canonical target-deployment manifest identity.</param>
    /// <returns>A canonical infrastructure-target-deployment-manifest reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="manifest"/> is default or cannot form a source reference.</exception>
    public static SourceReference TargetDeploymentManifest(InfrastructureTargetDeploymentManifestId manifest) =>
        Create("infrastructure-target-deployment-manifest", manifest.Value, nameof(manifest));

    /// <summary>Projects an exact target-deployment manifest reference.</summary>
    /// <param name="manifest">Exact canonical target-deployment manifest.</param>
    /// <returns>A canonical reference fenced by the manifest fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    public static SourceReference TargetDeploymentManifest(InfrastructureTargetDeploymentManifestReference manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Create(
            "infrastructure-target-deployment-manifest",
            $"{manifest.Id.Value}#{manifest.Fingerprint.Algorithm}:{manifest.Fingerprint.Canonicalization}:{manifest.Fingerprint.Value}",
            nameof(manifest));
    }

    static SourceReference Create(string scheme, string identity, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("An infrastructure source reference requires a canonical identity.", parameterName);
        return SourceReference.Create(scheme, identity);
    }
}

static class InfrastructureAuthoringSource
{
    internal static InfrastructureSourceProvenance Capture(
        SourceReference subject,
        SourceReference root,
        string sourceFile,
        int sourceLine,
        string sourceMember)
    {
        var member = string.IsNullOrWhiteSpace(sourceMember) ? "unknown" : sourceMember;
        var fileName = string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFileName(sourceFile);
        var sourceIdentity = fileName is null
            ? root.Value
            : $"{root.Value}/{Uri.EscapeDataString(fileName)}";
        var identity = sourceLine > 0
            ? $"{sourceIdentity}#{member}:L{sourceLine}"
            : $"{sourceIdentity}#{member}";
        var description = fileName is null || sourceLine <= 0
            ? $"C# member {member}"
            : $"{fileName}:{sourceLine} ({member})";
        return new(subject, SourceReference.Create("csharp", identity), description);
    }
}
