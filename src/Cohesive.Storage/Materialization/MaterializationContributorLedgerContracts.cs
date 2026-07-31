using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Stable generation- and plan-fenced namespace for durable contributor-to-root associations.
/// </summary>
public sealed record MaterializationContributorLedgerScope
{
    /// <summary>Creates one exact contributor-ledger namespace.</summary>
    /// <param name="materialization">Stable logical materialization identity.</param>
    /// <param name="generation">Exact isolated generation whose associations are represented.</param>
    /// <param name="definitionFingerprint">Exact materialization-definition content fence.</param>
    /// <param name="impactPlanFingerprint">Exact impact-plan content fence.</param>
    /// <exception cref="ArgumentNullException">A fingerprint is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A materialization or generation identity is default.</exception>
    [JsonConstructor]
    public MaterializationContributorLedgerScope(
        MaterializationId materialization,
        MaterializationGenerationId generation,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationImpactPlanFingerprint impactPlanFingerprint)
    {
        if (string.IsNullOrWhiteSpace(materialization.Value))
        {
            throw new ArgumentException("A contributor-ledger scope requires a materialization identity.", nameof(materialization));
        }

        if (string.IsNullOrWhiteSpace(generation.Value))
        {
            throw new ArgumentException("A contributor-ledger scope requires a generation identity.", nameof(generation));
        }

        Materialization = materialization;
        Generation = generation;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        ImpactPlanFingerprint = Guard.RequireNotNull(impactPlanFingerprint);
    }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact isolated generation whose associations are represented.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Exact materialization-definition content fence.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Exact impact-plan content fence.</summary>
    public MaterializationImpactPlanFingerprint ImpactPlanFingerprint { get; }
}

/// <summary>
/// Durable contributor key based on semantic identity rather than an evaluation-local occurrence identity.
/// </summary>
public sealed record MaterializationContributorLedgerKey
{
    /// <summary>Creates one stable contributor-ledger key.</summary>
    /// <param name="scope">Exact materialization, generation, definition, and impact-plan fence.</param>
    /// <param name="input">Canonical Relations acquisition role in which the contributor participated.</param>
    /// <param name="shape">Graph-qualified contributor shape.</param>
    /// <param name="contributorIdentity">
    /// Stable canonical contributor or relationship-target identity, including an identity that currently has no
    /// source observation.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="contributorIdentity"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An input or shape is default, or the contributor identity is empty.</exception>
    [JsonConstructor]
    public MaterializationContributorLedgerKey(
        MaterializationContributorLedgerScope scope,
        RelationQueryInputId input,
        QualifiedShapeId shape,
        string contributorIdentity)
    {
        Scope = Guard.RequireNotNull(scope);
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A contributor-ledger key requires a canonical input.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A contributor-ledger key requires a graph-qualified shape.", nameof(shape));
        }

        Input = input;
        Shape = shape;
        ContributorIdentity = MaterializationContract.RequireUnicodeIdentity(
            contributorIdentity,
            nameof(contributorIdentity));
    }

    /// <summary>Exact materialization, generation, definition, and impact-plan fence.</summary>
    public MaterializationContributorLedgerScope Scope { get; }

    /// <summary>Canonical Relations acquisition role in which the contributor participated.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Graph-qualified contributor shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>
    /// Stable canonical contributor or relationship-target identity, whether or not an observation currently exists.
    /// </summary>
    public string ContributorIdentity { get; }
}

/// <summary>Prior root and emitted-item lineage retained for one contributor association.</summary>
public sealed record MaterializationRootContribution
{
    /// <summary>Creates one contributor-to-root association.</summary>
    /// <param name="rootIdentity">Stable relation-root observation identity.</param>
    /// <param name="materializedItems">Previously emitted item identities for that root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rootIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The root identity is empty, or item identities contain a default or duplicate value.
    /// </exception>
    [JsonConstructor]
    public MaterializationRootContribution(
        string rootIdentity,
        ImmutableArray<MaterializationItemId> materializedItems = default)
    {
        RootIdentity = MaterializationContract.RequireUnicodeIdentity(rootIdentity, nameof(rootIdentity));
        var normalized = materializedItems.IsDefault ? [] : materializedItems;
        if (normalized.Any(static item => string.IsNullOrWhiteSpace(item.Value)))
        {
            throw new ArgumentException("A root contribution cannot contain a default item identity.", nameof(materializedItems));
        }

        if (normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("A root contribution cannot repeat an item identity.", nameof(materializedItems));
        }

        MaterializedItems = [.. normalized.OrderBy(static item => item.Value, StringComparer.Ordinal)];
    }

    /// <summary>Stable relation-root observation identity.</summary>
    public string RootIdentity { get; }

    /// <summary>Previously emitted item identities in deterministic order.</summary>
    public ImmutableArray<MaterializationItemId> MaterializedItems { get; }
}

/// <summary>Complete bounded durable associations retained for one stable contributor key.</summary>
public sealed record MaterializationContributorLedgerEntry
{
    /// <summary>Creates one complete contributor-ledger entry.</summary>
    /// <param name="key">Stable plan- and generation-fenced contributor key.</param>
    /// <param name="roots">Every prior root and emitted-item association for the contributor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Roots contain null or duplicate root identities.</exception>
    [JsonConstructor]
    public MaterializationContributorLedgerEntry(
        MaterializationContributorLedgerKey key,
        ImmutableArray<MaterializationRootContribution> roots)
    {
        Key = Guard.RequireNotNull(key);
        var normalized = roots.IsDefault ? [] : roots;
        if (normalized.Any(static root => root is null))
        {
            throw new ArgumentException("Contributor-ledger roots cannot contain null entries.", nameof(roots));
        }

        if (normalized.GroupBy(static root => root.RootIdentity, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A contributor-ledger entry cannot repeat a root identity.", nameof(roots));
        }

        Roots = [.. normalized.OrderBy(static root => root.RootIdentity, StringComparer.Ordinal)];
    }

    /// <summary>Stable plan- and generation-fenced contributor key.</summary>
    public MaterializationContributorLedgerKey Key { get; }

    /// <summary>Complete prior root and emitted-item associations in deterministic order.</summary>
    public ImmutableArray<MaterializationRootContribution> Roots { get; }
}
