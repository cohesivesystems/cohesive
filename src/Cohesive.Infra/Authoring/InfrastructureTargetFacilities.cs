using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra;

/// <summary>Fluent producer for canonical target-facility manifests.</summary>
public static class InfrastructureTargetFacilities
{
    /// <summary>Materializes one deterministic target-facility manifest.</summary>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="profileId">Stable versioned capability-profile identity.</param>
    /// <param name="target">Exact target interpreter identity.</param>
    /// <param name="variant">Coherent target variant.</param>
    /// <param name="supportedDefinitionSchemaVersions">Infrastructure definition schemas understood by the target.</param>
    /// <param name="configure">Synchronous facility, capability-rule, and operating-boundary declaration.</param>
    /// <returns>An immutable, normalized, exactly fingerprinted target-facility manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied identity, collection, or authored manifest invariant is invalid.</exception>
    public static InfrastructureTargetFacilityManifest Define(
        InfrastructureTargetFacilityManifestId id,
        InfrastructureCapabilityProfileId profileId,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        Action<InfrastructureTargetFacilityManifestBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new InfrastructureTargetFacilityManifestBuilder(
            id,
            profileId,
            target,
            variant,
            supportedDefinitionSchemaVersions);
        configure(builder);
        return builder.Build();
    }
}

/// <summary>Fluent producer for one canonical target-facility manifest.</summary>
public sealed class InfrastructureTargetFacilityManifestBuilder
{
    readonly InfrastructureTargetFacilityManifestId id;
    readonly InfrastructureCapabilityProfileId profileId;
    readonly InfrastructureTargetId target;
    readonly InfrastructureCapabilityVariantId variant;
    readonly ImmutableArray<string> supportedDefinitionSchemaVersions;
    readonly List<InfrastructureTargetFacilityBuilder> facilities = [];
    readonly List<InfrastructureCapabilityRule> rules = [];
    readonly List<InfrastructureOperatingBoundary> operatingBoundaries = [];
    readonly List<InfrastructureSourceProvenance> sourceMap = [];

    internal InfrastructureTargetFacilityManifestBuilder(
        InfrastructureTargetFacilityManifestId id,
        InfrastructureCapabilityProfileId profileId,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<string> supportedDefinitionSchemaVersions)
    {
        this.id = id;
        this.profileId = profileId;
        this.target = target;
        this.variant = variant;
        this.supportedDefinitionSchemaVersions = supportedDefinitionSchemaVersions;
    }

    /// <summary>Declares one selectable workload facility.</summary>
    /// <param name="facility">Stable target-local facility identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>A builder that attaches leaf capability evidence to the facility.</returns>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is default.</exception>
    public InfrastructureTargetFacilityBuilder Workload(
        InfrastructureTargetFacilityId facility,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Add(facility, InfrastructureNodeKind.Workload, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares one selectable resource facility.</summary>
    /// <param name="facility">Stable target-local facility identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>A builder that attaches leaf capability evidence to the facility.</returns>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is default.</exception>
    public InfrastructureTargetFacilityBuilder Resource(
        InfrastructureTargetFacilityId facility,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Add(facility, InfrastructureNodeKind.Resource, sourceFile, sourceLine, sourceMember);

    /// <summary>Adds one cross-facility capability-composition rule.</summary>
    /// <param name="rule">Immutable attributable capability rule.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This manifest builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    public InfrastructureTargetFacilityManifestBuilder Composes(
        InfrastructureCapabilityRule rule,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(rule);
        rules.Add(rule);
        sourceMap.Add(Capture(InfrastructureSourceReferences.CapabilityRule(rule.Id), sourceFile, sourceLine, sourceMember));
        return this;
    }

    /// <summary>Adds one target operating boundary referenced by evidence or composition rules.</summary>
    /// <param name="boundary">Immutable attributable operating boundary.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This manifest builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="boundary"/> is <see langword="null"/>.</exception>
    public InfrastructureTargetFacilityManifestBuilder Within(
        InfrastructureOperatingBoundary boundary,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(boundary);
        operatingBoundaries.Add(boundary);
        sourceMap.Add(Capture(InfrastructureSourceReferences.OperatingBoundary(boundary.Id), sourceFile, sourceLine, sourceMember));
        return this;
    }

    internal InfrastructureTargetFacilityManifest Build()
    {
        var materializedFacilities = facilities.Select(static facility => facility.Build()).ToImmutableArray();
        var evidence = facilities.SelectMany(static facility => facility.Evidence).ToImmutableArray();
        var profile = new InfrastructureCapabilityProfile(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            profileId,
            target,
            supportedDefinitionSchemaVersions,
            [new(variant, evidence, [.. rules], [.. operatingBoundaries])]);
        return new(
            InfrastructureTargetFacilityManifest.CurrentSchemaVersion,
            id,
            profile,
            variant,
            materializedFacilities,
            sourceMap: new([.. sourceMap]));
    }

    InfrastructureTargetFacilityBuilder Add(
        InfrastructureTargetFacilityId facility,
        InfrastructureNodeKind nodeKind,
        string sourceFile,
        int sourceLine,
        string sourceMember)
    {
        if (string.IsNullOrWhiteSpace(facility.Value))
            throw new ArgumentException("A target facility requires a stable identity.", nameof(facility));
        sourceMap.Add(Capture(InfrastructureSourceReferences.Facility(facility), sourceFile, sourceLine, sourceMember));
        var builder = new InfrastructureTargetFacilityBuilder(facility, nodeKind, id, sourceMap);
        facilities.Add(builder);
        return builder;
    }

    InfrastructureSourceProvenance Capture(
        SourceReference subject,
        string sourceFile,
        int sourceLine,
        string sourceMember) => InfrastructureAuthoringSource.Capture(
            subject,
            InfrastructureSourceReferences.TargetFacilityManifest(id),
            sourceFile,
            sourceLine,
            sourceMember);
}

/// <summary>Fluent producer for one canonical target facility.</summary>
public sealed class InfrastructureTargetFacilityBuilder
{
    readonly InfrastructureTargetFacilityId id;
    readonly InfrastructureNodeKind nodeKind;
    readonly List<InfrastructureCapabilityEvidence> evidence = [];
    readonly InfrastructureTargetFacilityManifestId manifest;
    readonly List<InfrastructureSourceProvenance> sourceMap;

    internal InfrastructureTargetFacilityBuilder(
        InfrastructureTargetFacilityId id,
        InfrastructureNodeKind nodeKind,
        InfrastructureTargetFacilityManifestId manifest,
        List<InfrastructureSourceProvenance> sourceMap)
    {
        this.id = id;
        this.nodeKind = nodeKind;
        this.manifest = manifest;
        this.sourceMap = sourceMap;
    }

    /// <summary>Attaches one native or constrained leaf capability assertion to this facility.</summary>
    /// <param name="capabilityEvidence">Immutable attributable target evidence.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for non-semantic attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for non-semantic attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for non-semantic attribution.</param>
    /// <returns>This facility builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilityEvidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="capabilityEvidence"/> is composed evidence.</exception>
    public InfrastructureTargetFacilityBuilder Provides(
        InfrastructureCapabilityEvidence capabilityEvidence,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(capabilityEvidence);
        if (capabilityEvidence.Realization == CapabilityRealizationKind.Composed)
        {
            throw new ArgumentException(
                "Facility evidence must be native or constrained; use a manifest capability rule for composition.",
                nameof(capabilityEvidence));
        }
        evidence.Add(capabilityEvidence);
        sourceMap.Add(InfrastructureAuthoringSource.Capture(
            InfrastructureSourceReferences.CapabilityEvidence(capabilityEvidence.Id),
            InfrastructureSourceReferences.TargetFacilityManifest(manifest),
            sourceFile,
            sourceLine,
            sourceMember));
        return this;
    }

    internal IReadOnlyList<InfrastructureCapabilityEvidence> Evidence => evidence;

    internal InfrastructureTargetFacility Build() => new(
        id,
        nodeKind,
        [.. evidence.Select(static item => item.Id)]);
}
