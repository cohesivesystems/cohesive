using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>Exact or conservative completeness promised by one materialization impact route.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationImpactPrecision
{
    /// <summary>The route returns every and only root that can have changed.</summary>
    Exact = 0,

    /// <summary>The route returns a bounded superset of roots that can have changed.</summary>
    Conservative = 1
}

/// <summary>Closed impact-routing strategies understood by the materialization runtime.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationImpactStrategyKind
{
    /// <summary>The changed observation is itself one relation root.</summary>
    DirectRoot = 0,

    /// <summary>Canonical relationships are traversed toward roots using bounded inverse operations.</summary>
    InverseTraversal = 1,

    /// <summary>A durable contributor-to-root ledger resolves prior and current root associations.</summary>
    ContributorLedger = 2,

    /// <summary>The complete bounded root set is invalidated explicitly.</summary>
    BoundedGlobalInvalidation = 3
}

/// <summary>Runtime lineage that must be retained or acquired to execute an impact route without false negatives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationImpactLineageKind
{
    /// <summary>The stable changed-observation identity is the affected root identity.</summary>
    ChangedSubjectIdentity = 0,

    /// <summary>The changed contributor identity is inverted through complete relationship lookups.</summary>
    ContributorIdentity = 1,

    /// <summary>Both prior and current relationship-reference values are required for deletes and moves.</summary>
    BeforeAndAfterRelationshipReferences = 2,

    /// <summary>Durable prior associations are unioned with roots resolved from current canonical relationship state.</summary>
    PriorLedgerAndCurrentRelationshipState = 3,

    /// <summary>A complete bounded enumeration of every relation root is required.</summary>
    CompleteRootSet = 4
}

/// <summary>Primitive used by one inverse-impact relationship step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationInverseImpactOperationKind
{
    /// <summary>Query reference-bearing observations whose canonical relationship key equals a contributor identity.</summary>
    PredicateLookup = 0,

    /// <summary>Extract prior and current target identities from the contributor's canonical relationship reference.</summary>
    BeforeAndAfterReferenceExtraction = 1,

    /// <summary>Extract the current target identity from the changed contributor's after state.</summary>
    AfterRelationshipReferenceExtraction = 2,

    /// <summary>Extract a target identity from a current reference-bearing observation produced by a preceding step.</summary>
    CurrentRelationshipReferenceExtraction = 3
}

/// <summary>Stable, versioned identity of one materialization impact-planning policy.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationImpactPlanningPolicyId
{
    /// <summary>Creates a planning-policy identity.</summary>
    /// <param name="value">Stable identity that changes when planning semantics change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationImpactPlanningPolicyId(string value) =>
        Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable policy identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Explicit strategy preference and hard work bounds used by impact compilation.</summary>
public sealed record MaterializationImpactPlanningPolicy
{
    /// <summary>Creates one bounded impact-planning policy.</summary>
    /// <param name="id">Stable, versioned policy identity.</param>
    /// <param name="strategyPreference">
    /// Permitted non-direct strategies in preference order. Direct-root routing is always selected when sound.
    /// </param>
    /// <param name="maximumAffectedRoots">Maximum exact roots admitted for one contributor change.</param>
    /// <param name="maximumReadBytes">Maximum encoded bytes admitted by each impact read.</param>
    /// <param name="maximumLedgerWriteBytes">
    /// Maximum encoded bytes admitted by one complete contributor-ledger replacement; required only when that
    /// strategy is permitted.
    /// </param>
    /// <param name="maximumGlobalRoots">
    /// Maximum complete root-set size admitted by global invalidation; required only when that strategy is permitted.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The identity is default, a strategy repeats, direct-root routing is listed, or a ledger/global bound conflicts
    /// with the permitted strategies.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A strategy is unsupported or a supplied bound is not positive or portable.
    /// </exception>
    [JsonConstructor]
    public MaterializationImpactPlanningPolicy(
        MaterializationImpactPlanningPolicyId id,
        ImmutableArray<MaterializationImpactStrategyKind> strategyPreference,
        long maximumAffectedRoots,
        long maximumReadBytes,
        long? maximumLedgerWriteBytes = null,
        long? maximumGlobalRoots = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An impact-planning policy requires a stable identity.", nameof(id));
        }

        var normalized = strategyPreference.IsDefault ? [] : strategyPreference;
        foreach (var strategy in normalized)
        {
            if (!Enum.IsDefined(strategy))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(strategyPreference),
                    strategy,
                    "Unsupported materialization impact strategy.");
            }

            if (strategy == MaterializationImpactStrategyKind.DirectRoot)
            {
                throw new ArgumentException(
                    "Direct-root routing is semantic and cannot be ordered as a fallback preference.",
                    nameof(strategyPreference));
            }
        }

        if (normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("An impact-planning policy cannot repeat a strategy.", nameof(strategyPreference));
        }

        MaximumAffectedRoots = MaterializationContract.RequirePortablePositiveBound(
            maximumAffectedRoots,
            nameof(maximumAffectedRoots));
        MaximumReadBytes = MaterializationContract.RequirePortablePositiveBound(
            maximumReadBytes,
            nameof(maximumReadBytes));
        var permitsLedger = normalized.Contains(MaterializationImpactStrategyKind.ContributorLedger);
        if (permitsLedger != (maximumLedgerWriteBytes is not null))
        {
            throw new ArgumentException(
                "Contributor-ledger routing and its positive encoded-write bound must be declared together.",
                nameof(maximumLedgerWriteBytes));
        }

        MaximumLedgerWriteBytes = maximumLedgerWriteBytes is { } ledgerWriteBytes
            ? MaterializationContract.RequirePortablePositiveBound(
                ledgerWriteBytes,
                nameof(maximumLedgerWriteBytes))
            : null;
        var permitsGlobal = normalized.Contains(MaterializationImpactStrategyKind.BoundedGlobalInvalidation);
        if (permitsGlobal != (maximumGlobalRoots is not null))
        {
            throw new ArgumentException(
                "Bounded global invalidation and its positive total-root bound must be declared together.",
                nameof(maximumGlobalRoots));
        }

        Id = id;
        StrategyPreference = normalized;
        MaximumGlobalRoots = maximumGlobalRoots is { } global
            ? MaterializationContract.RequirePortablePositiveBound(global, nameof(maximumGlobalRoots))
            : null;
    }

    /// <summary>Stable, versioned policy identity.</summary>
    public MaterializationImpactPlanningPolicyId Id { get; }

    /// <summary>Permitted non-direct strategies in explicit preference order.</summary>
    public ImmutableArray<MaterializationImpactStrategyKind> StrategyPreference { get; }

    /// <summary>Maximum exact roots admitted for one contributor change.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumAffectedRoots { get; }

    /// <summary>Maximum encoded bytes admitted by each impact read.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumReadBytes { get; }

    /// <summary>Optional maximum encoded bytes admitted by one complete contributor-ledger replacement.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MaximumLedgerWriteBytes { get; }

    /// <summary>Optional maximum complete root-set size admitted by global invalidation.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MaximumGlobalRoots { get; }

}

/// <summary>Reference to one capability requirement that authorizes an impact strategy.</summary>
public sealed record MaterializationImpactCapabilityReference
{
    /// <summary>Creates an impact capability reference.</summary>
    /// <param name="role">Endpoint role that owns the requirement.</param>
    /// <param name="requirement">Stable requirement identity in the fenced materialization definition.</param>
    /// <param name="sourceInput">Canonical Relations acquisition input for a source requirement.</param>
    /// <exception cref="ArgumentException">An identity is default or the endpoint role conflicts with source attribution.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationImpactCapabilityReference(
        MaterializationEndpointRole role,
        MaterializationCapabilityRequirementId requirement,
        RelationQueryInputId? sourceInput = null)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported materialization endpoint role.");
        }

        if (string.IsNullOrWhiteSpace(requirement.Value))
        {
            throw new ArgumentException("An impact capability reference requires a stable requirement.", nameof(requirement));
        }

        if ((role == MaterializationEndpointRole.Source) != (sourceInput is not null))
        {
            throw new ArgumentException(
                "Source capability references require one canonical input; target references cannot declare one.",
                nameof(sourceInput));
        }

        if (sourceInput is { } input && string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A source capability reference cannot use a default input.", nameof(sourceInput));
        }

        Role = role;
        Requirement = requirement;
        SourceInput = sourceInput;
    }

    /// <summary>Endpoint role that owns the requirement.</summary>
    public MaterializationEndpointRole Role { get; }

    /// <summary>Stable requirement identity in the fenced materialization definition.</summary>
    public MaterializationCapabilityRequirementId Requirement { get; }

    /// <summary>Canonical Relations acquisition input for a source requirement.</summary>
    public RelationQueryInputId? SourceInput { get; }
}

/// <summary>One portable canonical relationship operation in a contributor-to-root impact path.</summary>
/// <remarks>
/// A predicate step is an executable semantic template, not a claim that the retained relation-query physical plan
/// already contains the reverse placement. Materialization realization must bind an auxiliary relationship-key
/// lookup for <see cref="ReferenceSourceInput"/> from the canonical <see cref="RelationshipInput"/> and prove the
/// referenced capability requirement before execution.
/// </remarks>
public sealed record MaterializationInverseImpactStep
{
    /// <summary>Creates one inverse-impact step.</summary>
    /// <param name="relationshipInput">Canonical Relations relationship-input identity.</param>
    /// <param name="referenceSourceInput">
    /// Acquisition role that supplies the reference-bearing observations for this operation.
    /// </param>
    /// <param name="operation">Bounded operation used to move one hop toward relation roots.</param>
    /// <exception cref="ArgumentException">An input identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationInverseImpactStep(
        RelationQueryInputId relationshipInput,
        RelationQueryInputId referenceSourceInput,
        MaterializationInverseImpactOperationKind operation)
    {
        if (string.IsNullOrWhiteSpace(relationshipInput.Value))
        {
            throw new ArgumentException("An inverse-impact step requires a relationship input.", nameof(relationshipInput));
        }

        if (string.IsNullOrWhiteSpace(referenceSourceInput.Value))
        {
            throw new ArgumentException(
                "An inverse-impact step requires a reference-source input.",
                nameof(referenceSourceInput));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported inverse-impact operation.");
        }

        RelationshipInput = relationshipInput;
        ReferenceSourceInput = referenceSourceInput;
        Operation = operation;
    }

    /// <summary>Canonical Relations relationship-input identity.</summary>
    public RelationQueryInputId RelationshipInput { get; }

    /// <summary>
    /// Acquisition role that supplies reference-bearing observations. Predicate operations lower an auxiliary
    /// relationship-key query against this role; extraction operations consume observations already in the route.
    /// </summary>
    public RelationQueryInputId ReferenceSourceInput { get; }

    /// <summary>Bounded operation used to move one hop toward relation roots.</summary>
    public MaterializationInverseImpactOperationKind Operation { get; }
}

/// <summary>Closed executable strategy selected for one materialization change input.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$impactStrategy")]
[JsonDerivedType(typeof(MaterializationDirectRootImpactStrategy), "directRoot")]
[JsonDerivedType(typeof(MaterializationInverseTraversalImpactStrategy), "inverseTraversal")]
[JsonDerivedType(typeof(MaterializationContributorLedgerImpactStrategy), "contributorLedger")]
[JsonDerivedType(typeof(MaterializationBoundedGlobalImpactStrategy), "boundedGlobalInvalidation")]
public abstract record MaterializationImpactStrategy
{
    private protected MaterializationImpactStrategy(
        MaterializationImpactStrategyKind kind,
        MaterializationImpactLineageKind lineage)
    {
        Kind = kind;
        Lineage = lineage;
    }

    /// <summary>Closed strategy kind.</summary>
    [JsonIgnore]
    public MaterializationImpactStrategyKind Kind { get; }

    /// <summary>Lineage required to execute the strategy without false negatives.</summary>
    public MaterializationImpactLineageKind Lineage { get; }
}

/// <summary>Exact route from a changed root observation to that same root's work.</summary>
public sealed record MaterializationDirectRootImpactStrategy : MaterializationImpactStrategy
{
    /// <summary>Creates a direct-root strategy.</summary>
    /// <param name="rootInput">Canonical relation-root source input.</param>
    /// <exception cref="ArgumentException"><paramref name="rootInput"/> is default.</exception>
    [JsonConstructor]
    public MaterializationDirectRootImpactStrategy(RelationQueryInputId rootInput)
        : base(MaterializationImpactStrategyKind.DirectRoot, MaterializationImpactLineageKind.ChangedSubjectIdentity)
    {
        if (string.IsNullOrWhiteSpace(rootInput.Value))
        {
            throw new ArgumentException("A direct-root strategy requires a root input.", nameof(rootInput));
        }

        RootInput = rootInput;
    }

    /// <summary>Canonical relation-root source input.</summary>
    public RelationQueryInputId RootInput { get; }
}

/// <summary>Exact contributor route that follows canonical relationships toward relation roots.</summary>
public sealed record MaterializationInverseTraversalImpactStrategy : MaterializationImpactStrategy
{
    /// <summary>Creates an inverse-traversal strategy.</summary>
    /// <param name="steps">Relationship steps ordered from the changed contributor toward roots.</param>
    /// <param name="lineage">Contributor identity or prior/current relationship-reference lineage.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="steps"/> is empty, contains null, repeats a relationship, has an invalid operation sequence,
    /// or its first operation contradicts <paramref name="lineage"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineage"/> is unsupported for inverse traversal.</exception>
    [JsonConstructor]
    public MaterializationInverseTraversalImpactStrategy(
        ImmutableArray<MaterializationInverseImpactStep> steps,
        MaterializationImpactLineageKind lineage)
        : base(MaterializationImpactStrategyKind.InverseTraversal, RequireLineage(lineage))
    {
        Steps = MaterializationInverseImpactPathContract.RequireInverseTraversal(
            steps,
            lineage,
            stepsParameterName: nameof(steps),
            lineageParameterName: nameof(lineage));
    }

    /// <summary>Relationship steps ordered from the changed contributor toward roots.</summary>
    public ImmutableArray<MaterializationInverseImpactStep> Steps { get; }

    static MaterializationImpactLineageKind RequireLineage(MaterializationImpactLineageKind lineage) => lineage switch
    {
        MaterializationImpactLineageKind.ContributorIdentity => lineage,
        MaterializationImpactLineageKind.BeforeAndAfterRelationshipReferences => lineage,
        _ => throw new ArgumentOutOfRangeException(
            nameof(lineage),
            lineage,
            "Inverse traversal requires contributor-identity or before-and-after relationship lineage.")
    };
}

/// <summary>Exact union of durable prior associations and roots resolved from current canonical relationship state.</summary>
public sealed record MaterializationContributorLedgerImpactStrategy : MaterializationImpactStrategy
{
    /// <summary>Creates a contributor-ledger plus current-state strategy.</summary>
    /// <param name="contributorInput">Canonical change input used in the durable ledger key.</param>
    /// <param name="currentRootSteps">
    /// Canonical relationship steps that resolve roots from the change after state; their result is unioned with
    /// complete prior ledger associations.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="contributorInput"/> is default, or <paramref name="currentRootSteps"/> is empty, contains
    /// null, repeats a relationship, or has an invalid current-state operation sequence.
    /// </exception>
    [JsonConstructor]
    public MaterializationContributorLedgerImpactStrategy(
        RelationQueryInputId contributorInput,
        ImmutableArray<MaterializationInverseImpactStep> currentRootSteps)
        : base(
            MaterializationImpactStrategyKind.ContributorLedger,
            MaterializationImpactLineageKind.PriorLedgerAndCurrentRelationshipState)
    {
        if (string.IsNullOrWhiteSpace(contributorInput.Value))
        {
            throw new ArgumentException("A contributor-ledger strategy requires a contributor input.", nameof(contributorInput));
        }

        ContributorInput = contributorInput;
        CurrentRootSteps = MaterializationInverseImpactPathContract.RequireCurrentTraversal(
            currentRootSteps,
            parameterName: nameof(currentRootSteps));
    }

    /// <summary>Canonical change input used with shape and contributor identity in the durable ledger key.</summary>
    public RelationQueryInputId ContributorInput { get; }

    /// <summary>Canonical current-state relationship steps whose roots are unioned with prior ledger roots.</summary>
    public ImmutableArray<MaterializationInverseImpactStep> CurrentRootSteps { get; }
}

/// <summary>Explicit conservative route that invalidates one complete bounded relation-root set.</summary>
public sealed record MaterializationBoundedGlobalImpactStrategy : MaterializationImpactStrategy
{
    /// <summary>Creates a bounded global-invalidation strategy.</summary>
    /// <param name="rootInput">Canonical relation-root source input to enumerate completely.</param>
    /// <exception cref="ArgumentException"><paramref name="rootInput"/> is default.</exception>
    [JsonConstructor]
    public MaterializationBoundedGlobalImpactStrategy(RelationQueryInputId rootInput)
        : base(MaterializationImpactStrategyKind.BoundedGlobalInvalidation, MaterializationImpactLineageKind.CompleteRootSet)
    {
        if (string.IsNullOrWhiteSpace(rootInput.Value))
        {
            throw new ArgumentException("A bounded global strategy requires a root input.", nameof(rootInput));
        }

        RootInput = rootInput;
    }

    /// <summary>Canonical relation-root source input to enumerate completely.</summary>
    public RelationQueryInputId RootInput { get; }
}

static class MaterializationInverseImpactPathContract
{
    public static ImmutableArray<MaterializationInverseImpactStep> RequireInverseTraversal(
        ImmutableArray<MaterializationInverseImpactStep> steps,
        MaterializationImpactLineageKind lineage,
        string stepsParameterName,
        string lineageParameterName)
    {
        var normalized = Normalize(steps, stepsParameterName);
        var expectedLineage = normalized[0].Operation switch
        {
            MaterializationInverseImpactOperationKind.PredicateLookup =>
                MaterializationImpactLineageKind.ContributorIdentity,
            MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction =>
                MaterializationImpactLineageKind.BeforeAndAfterRelationshipReferences,
            _ => throw new ArgumentException(
                "Exact inverse traversal must begin with predicate lookup or before-and-after reference extraction.",
                stepsParameterName)
        };
        if (lineage != expectedLineage)
        {
            throw new ArgumentException(
                $"Inverse traversal operation '{normalized[0].Operation}' requires lineage '{expectedLineage}'.",
                lineageParameterName);
        }

        ValidateContinuations(
            normalized,
            firstExtraction: MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction,
            parameterName: stepsParameterName);
        return normalized;
    }

    public static ImmutableArray<MaterializationInverseImpactStep> RequireCurrentTraversal(
        ImmutableArray<MaterializationInverseImpactStep> steps,
        string parameterName)
    {
        var normalized = Normalize(steps, parameterName);
        if (normalized[0].Operation is not (
                MaterializationInverseImpactOperationKind.PredicateLookup
                or MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction))
        {
            throw new ArgumentException(
                "Current-root traversal must begin with predicate lookup or after-state reference extraction.",
                parameterName);
        }

        ValidateContinuations(
            normalized,
            firstExtraction: MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction,
            parameterName: parameterName);
        return normalized;
    }

    static ImmutableArray<MaterializationInverseImpactStep> Normalize(
        ImmutableArray<MaterializationInverseImpactStep> steps,
        string parameterName)
    {
        var normalized = steps.IsDefault ? [] : steps;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static step => step is null))
        {
            throw new ArgumentException("An impact traversal requires non-null relationship steps.", parameterName);
        }

        if (normalized.GroupBy(static step => step.RelationshipInput).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("An impact traversal cannot repeat a relationship input.", parameterName);
        }

        return normalized;
    }

    static void ValidateContinuations(
        ImmutableArray<MaterializationInverseImpactStep> steps,
        MaterializationInverseImpactOperationKind firstExtraction,
        string parameterName)
    {
        for (var index = 0; index < steps.Length; index++)
        {
            var operation = steps[index].Operation;
            if (operation == MaterializationInverseImpactOperationKind.PredicateLookup)
            {
                continue;
            }

            if (operation == firstExtraction && index == 0)
            {
                continue;
            }

            if (operation == MaterializationInverseImpactOperationKind.CurrentRelationshipReferenceExtraction
                && index > 0
                && steps[index - 1].Operation == MaterializationInverseImpactOperationKind.PredicateLookup)
            {
                continue;
            }

            throw new ArgumentException(
                $"Impact traversal operation '{operation}' is not valid at step {index}.",
                parameterName);
        }
    }
}

/// <summary>Executable impact route for changes delivered from one canonical Relations acquisition input.</summary>
public sealed record MaterializationImpactRoute
{
    /// <summary>Creates one normalized impact route.</summary>
    /// <param name="changeInput">Canonical source or traversal input carried by change scopes.</param>
    /// <param name="changeShape">Graph-qualified shape delivered for that input.</param>
    /// <param name="dependencyInputs">
    /// Canonical manifest input identities whose effects make the selected materialization output dependent on the
    /// changed observation.
    /// </param>
    /// <param name="strategy">Executable root-resolution strategy.</param>
    /// <param name="precision">Exact or explicit conservative classification.</param>
    /// <param name="capabilities">Definition-fenced capability requirements that authorize the strategy.</param>
    /// <param name="maximumAffectedRoots">Hard maximum roots admitted for one change.</param>
    /// <param name="maximumReadBytes">Hard maximum encoded bytes admitted by each impact read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="strategy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, shape, collection, precision, or strategy invariant is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="precision"/> or the bound is invalid.</exception>
    [JsonConstructor]
    public MaterializationImpactRoute(
        RelationQueryInputId changeInput,
        QualifiedShapeId changeShape,
        ImmutableArray<RelationQueryInputId> dependencyInputs,
        MaterializationImpactStrategy strategy,
        MaterializationImpactPrecision precision,
        ImmutableArray<MaterializationImpactCapabilityReference> capabilities,
        long maximumAffectedRoots,
        long maximumReadBytes)
    {
        if (string.IsNullOrWhiteSpace(changeInput.Value))
        {
            throw new ArgumentException("An impact route requires a change input.", nameof(changeInput));
        }

        if (string.IsNullOrWhiteSpace(changeShape.GraphId.Value)
            || string.IsNullOrWhiteSpace(changeShape.ShapeId.Value))
        {
            throw new ArgumentException("An impact route requires a graph-qualified change shape.", nameof(changeShape));
        }

        var dependencies = dependencyInputs.IsDefault ? [] : dependencyInputs;
        if (dependencies.IsDefaultOrEmpty || dependencies.Any(static input => string.IsNullOrWhiteSpace(input.Value)))
        {
            throw new ArgumentException("An impact route requires non-default canonical dependency inputs.", nameof(dependencyInputs));
        }

        ArgumentNullException.ThrowIfNull(strategy);
        if (!Enum.IsDefined(precision))
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Unsupported impact precision.");
        }

        var expectedPrecision = strategy.Kind == MaterializationImpactStrategyKind.BoundedGlobalInvalidation
            ? MaterializationImpactPrecision.Conservative
            : MaterializationImpactPrecision.Exact;
        if (precision != expectedPrecision)
        {
            throw new ArgumentException("Only bounded global invalidation is conservative in the v1 impact plan.", nameof(precision));
        }

        _ = MaterializationContract.RequirePortablePositiveBound(
            maximumAffectedRoots,
            nameof(maximumAffectedRoots));
        _ = MaterializationContract.RequirePortablePositiveBound(maximumReadBytes, nameof(maximumReadBytes));

        if (strategy.Kind == MaterializationImpactStrategyKind.DirectRoot && maximumAffectedRoots != 1)
        {
            throw new ArgumentException("A direct-root route affects exactly one root.", nameof(maximumAffectedRoots));
        }

        var normalizedCapabilities = capabilities.IsDefault ? [] : capabilities;
        if (normalizedCapabilities.Any(static capability => capability is null))
        {
            throw new ArgumentException("Impact capability references cannot contain null entries.", nameof(capabilities));
        }

        if (normalizedCapabilities.GroupBy(static capability => capability.Requirement).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("An impact route cannot repeat a capability requirement.", nameof(capabilities));
        }

        ChangeInput = changeInput;
        ChangeShape = changeShape;
        DependencyInputs = [.. dependencies.Distinct().OrderBy(static input => input.Value, StringComparer.Ordinal)];
        Strategy = strategy;
        Precision = precision;
        Capabilities =
        [
            .. normalizedCapabilities
                .OrderBy(static capability => capability.Requirement.Value, StringComparer.Ordinal)
        ];
        MaximumAffectedRoots = maximumAffectedRoots;
        MaximumReadBytes = maximumReadBytes;
    }

    /// <summary>Canonical source or traversal input carried by change scopes.</summary>
    public RelationQueryInputId ChangeInput { get; }

    /// <summary>Graph-qualified shape delivered for the input.</summary>
    public QualifiedShapeId ChangeShape { get; }

    /// <summary>References into the canonical Relations dependency manifest in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryInputId> DependencyInputs { get; }

    /// <summary>Executable root-resolution strategy.</summary>
    public MaterializationImpactStrategy Strategy { get; }

    /// <summary>Exact or explicit conservative route classification.</summary>
    public MaterializationImpactPrecision Precision { get; }

    /// <summary>Definition-fenced capability requirements in deterministic identity order.</summary>
    public ImmutableArray<MaterializationImpactCapabilityReference> Capabilities { get; }

    /// <summary>Hard maximum roots admitted for one change; overflow must fail rather than truncate.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumAffectedRoots { get; }

    /// <summary>Hard maximum encoded bytes admitted by each impact read.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumReadBytes { get; }
}

/// <summary>Versioned cryptographic identity of one complete materialization impact plan.</summary>
public sealed record MaterializationImpactPlanFingerprint
{
    /// <summary>Creates an impact-plan fingerprint.</summary>
    /// <param name="algorithm">Digest algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lower-case hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component is empty or the digest is not lower-case hexadecimal.</exception>
    [JsonConstructor]
    public MaterializationImpactPlanFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = MaterializationContract.RequireUnicodeIdentity(algorithm, nameof(algorithm));
        Canonicalization = MaterializationContract.RequireUnicodeIdentity(canonicalization, nameof(canonicalization));
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));
        if (value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("An impact-plan fingerprint must be lower-case hexadecimal.", nameof(value));
        }
    }

    /// <summary>Digest algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lower-case hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>
/// Portable, deterministic Storage interpretation of one canonical Relations dependency manifest.
/// </summary>
/// <remarks>
/// Routes retain only references into the fenced compiled Relations plan. The requirement graph, dependency
/// manifest, relationship definitions, and traces remain owned exclusively by Cohesive.Relations.
/// </remarks>
public sealed class MaterializationImpactPlan
{
    /// <summary>Current portable impact-plan schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-impact-plan/v1";

    /// <summary>Creates and verifies one normalized impact plan.</summary>
    /// <param name="schemaVersion">Exact portable impact-plan schema version.</param>
    /// <param name="materialization">Stable logical materialization identity.</param>
    /// <param name="definitionFingerprint">Exact materialization-definition content fence.</param>
    /// <param name="relationPlan">Exact canonical Relations compiled-plan reference.</param>
    /// <param name="output">Selected complete relation output.</param>
    /// <param name="policy">Explicit strategy and bound policy used by compilation.</param>
    /// <param name="routes">One executable route for every relevant change input.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, identity, route set, plan affinity, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Plan content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Plan content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Plan content has no canonical JSON representation.</exception>
    [JsonConstructor]
    public MaterializationImpactPlan(
        string schemaVersion,
        MaterializationId materialization,
        ExecutionDefinitionFingerprint definitionFingerprint,
        RelationQueryCompiledPlanReference relationPlan,
        RelationQueryOutputReference output,
        MaterializationImpactPlanningPolicy policy,
        ImmutableArray<MaterializationImpactRoute> routes,
        MaterializationImpactPlanFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported materialization impact-plan schema version '{schemaVersion}'.", nameof(schemaVersion));
        }

        if (string.IsNullOrWhiteSpace(materialization.Value))
        {
            throw new ArgumentException("An impact plan requires a materialization identity.", nameof(materialization));
        }

        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        RelationPlan = Guard.RequireNotNull(relationPlan);
        Output = Guard.RequireNotNull(output);
        Policy = Guard.RequireNotNull(policy);

        var normalizedRoutes = routes.IsDefault ? [] : routes;
        if (normalizedRoutes.IsDefaultOrEmpty || normalizedRoutes.Any(static route => route is null))
        {
            throw new ArgumentException("An impact plan requires non-null routes.", nameof(routes));
        }

        if (normalizedRoutes.GroupBy(static route => route.ChangeInput).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("An impact plan cannot repeat a change input.", nameof(routes));
        }

        if (normalizedRoutes.Any(route => !relationPlan.Inputs.Contains(route.ChangeInput))
            || normalizedRoutes.SelectMany(static route => route.DependencyInputs)
            .Any(input => !relationPlan.Inputs.Contains(input)))
        {
            throw new ArgumentException("Every impact change and dependency input must reference the exact Relations plan.", nameof(routes));
        }

        foreach (var route in normalizedRoutes)
        {
            ValidateStrategyReferences(route, relationPlan, policy, nameof(routes));
        }

        Materialization = materialization;
        Routes = [.. normalizedRoutes.OrderBy(static route => route.ChangeInput.Value, StringComparer.Ordinal)];
        var computed = MaterializationImpactPlanFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
        {
            throw new ArgumentException("The impact-plan fingerprint does not match normalized content.", nameof(fingerprint));
        }

        Fingerprint = computed;
    }

    /// <summary>Exact portable impact-plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact materialization-definition content fence.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Exact canonical Relations compiled-plan reference.</summary>
    public RelationQueryCompiledPlanReference RelationPlan { get; }

    /// <summary>Selected complete relation output.</summary>
    public RelationQueryOutputReference Output { get; }

    /// <summary>Explicit strategy and bound policy used by compilation.</summary>
    public MaterializationImpactPlanningPolicy Policy { get; }

    /// <summary>Executable routes in deterministic canonical change-input order.</summary>
    public ImmutableArray<MaterializationImpactRoute> Routes { get; }

    /// <summary>Deterministic fingerprint of every execution-affecting plan field.</summary>
    public MaterializationImpactPlanFingerprint Fingerprint { get; }

    /// <summary>Finds the route for one exact source change input.</summary>
    /// <param name="changeInput">Canonical input carried by a materialization change scope.</param>
    /// <param name="route">Matched route when one exists.</param>
    /// <returns><see langword="true"/> when the input can affect the materialized output.</returns>
    public bool TryGetRoute(RelationQueryInputId changeInput, out MaterializationImpactRoute? route)
    {
        route = Routes.FirstOrDefault(candidate => candidate.ChangeInput == changeInput);
        return route is not null;
    }

    static void ValidateStrategyReferences(
        MaterializationImpactRoute route,
        RelationQueryCompiledPlanReference relationPlan,
        MaterializationImpactPlanningPolicy policy,
        string parameterName)
    {
        bool Known(RelationQueryInputId input) => relationPlan.Inputs.Contains(input);
        var referencesAreKnown = route.Strategy switch
        {
            MaterializationDirectRootImpactStrategy direct => Known(direct.RootInput),
            MaterializationInverseTraversalImpactStrategy inverse => inverse.Steps.All(step =>
                Known(step.RelationshipInput) && Known(step.ReferenceSourceInput)),
            MaterializationContributorLedgerImpactStrategy ledger => Known(ledger.ContributorInput)
                && ledger.CurrentRootSteps.All(step =>
                    Known(step.RelationshipInput) && Known(step.ReferenceSourceInput)),
            MaterializationBoundedGlobalImpactStrategy global => Known(global.RootInput),
            _ => false
        };
        if (!referencesAreKnown)
        {
            throw new ArgumentException("Every impact strategy operand must reference the exact Relations plan.", parameterName);
        }

        var boundIsValid = route.Strategy.Kind switch
        {
            MaterializationImpactStrategyKind.DirectRoot => route.MaximumAffectedRoots == 1,
            MaterializationImpactStrategyKind.InverseTraversal
                or MaterializationImpactStrategyKind.ContributorLedger =>
                route.MaximumAffectedRoots <= policy.MaximumAffectedRoots,
            MaterializationImpactStrategyKind.BoundedGlobalInvalidation =>
                policy.MaximumGlobalRoots == route.MaximumAffectedRoots,
            _ => false
        };
        if (!boundIsValid || route.MaximumReadBytes > policy.MaximumReadBytes)
        {
            throw new ArgumentException("An impact route exceeds or conflicts with its planning-policy bound.", parameterName);
        }
    }
}
