using System.Collections.Immutable;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Identifies the producer input from which one structural authoring decision was lowered.
/// </summary>
/// <remarks>
/// The reference is intentionally independent of C# expression trees so importers, generators,
/// Ari, and future host-language frontends can preserve their own source identities without
/// changing canonical relation/query semantics.
/// </remarks>
public sealed record RelationQueryAuthoringSource
{
    /// <summary>Creates an authoring source reference.</summary>
    /// <param name="producer">Stable identity of the frontend or producer.</param>
    /// <param name="reference">Producer-defined stable reference to the source construct.</param>
    /// <param name="description">Optional human-readable description of the source construct.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="producer"/> or <paramref name="reference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="producer"/> or <paramref name="reference"/> is empty or consists only of
    /// white-space characters.
    /// </exception>
    public RelationQueryAuthoringSource(
        string producer,
        string reference,
        string? description = null)
    {
        Producer = Guard.RequireNotNullOrWhiteSpace(producer);
        Reference = Guard.RequireNotNullOrWhiteSpace(reference);
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable identity of the frontend or producer.</summary>
    public string Producer { get; init; }

    /// <summary>Producer-defined stable reference to the source construct.</summary>
    public string Reference { get; init; }

    /// <summary>Optional human-readable description of the source construct.</summary>
    public string? Description { get; init; }
}

/// <summary>Category of identity assigned by the structural authoring core.</summary>
public enum RelationQueryAuthoringIdentityKind
{
    /// <summary>Logical query-node identity.</summary>
    Node = 0,

    /// <summary>Semantic value-binding identity.</summary>
    Binding = 1,

    /// <summary>Query-parameter identity.</summary>
    Parameter = 2,

    /// <summary>Projection, grouping, or aggregation-assignment identity.</summary>
    Assignment = 3,

    /// <summary>Named query-result identity.</summary>
    Result = 4
}

/// <summary>Origin of an identity selected by the structural authoring core.</summary>
public enum RelationQueryAuthoringIdentityOrigin
{
    /// <summary>The producer supplied the identity explicitly.</summary>
    Explicit = 0,

    /// <summary>The structural identity convention derived the identity.</summary>
    Convention = 1
}

/// <summary>Inspectable attribution for one identity retained in canonical IR.</summary>
public sealed record RelationQueryAuthoringIdentityDecision
{
    /// <summary>Creates an identity-attribution decision.</summary>
    /// <param name="kind">Category of canonical identity.</param>
    /// <param name="value">Canonical identity value.</param>
    /// <param name="origin">Whether the identity was explicit or convention-derived.</param>
    /// <param name="convention">
    /// Convention version when <paramref name="origin"/> is
    /// <see cref="RelationQueryAuthoringIdentityOrigin.Convention"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="source">Optional producer source responsible for the identity decision.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or white space, a convention-derived decision omits
    /// <paramref name="convention"/>, or an explicit decision declares a convention.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> or <paramref name="origin"/> is unsupported.
    /// </exception>
    public RelationQueryAuthoringIdentityDecision(
        RelationQueryAuthoringIdentityKind kind,
        string value,
        RelationQueryAuthoringIdentityOrigin origin,
        string? convention = null,
        RelationQueryAuthoringSource? source = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported authoring identity kind.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported authoring identity origin.");

        value = Guard.RequireNotNullOrWhiteSpace(value);
        convention = convention.TrimmedEmptyOrWhiteSpaceAs();
        if (origin == RelationQueryAuthoringIdentityOrigin.Convention && convention is null)
            throw new ArgumentException("A convention-derived identity requires a convention version.", nameof(convention));
        if (origin == RelationQueryAuthoringIdentityOrigin.Explicit && convention is not null)
            throw new ArgumentException("An explicit identity cannot declare a convention version.", nameof(convention));

        Kind = kind;
        Value = value;
        Origin = origin;
        Convention = convention;
        Source = source;
    }

    /// <summary>Category of canonical identity.</summary>
    public RelationQueryAuthoringIdentityKind Kind { get; init; }

    /// <summary>Canonical identity value.</summary>
    public string Value { get; init; }

    /// <summary>Whether the producer supplied or the convention derived the identity.</summary>
    public RelationQueryAuthoringIdentityOrigin Origin { get; init; }

    /// <summary>Convention version for a convention-derived identity.</summary>
    public string? Convention { get; init; }

    /// <summary>Optional producer source responsible for the identity decision.</summary>
    public RelationQueryAuthoringSource? Source { get; init; }
}

/// <summary>Kind of generated semantic construct carrying producer-source provenance.</summary>
public enum RelationQueryAuthoringDecisionKind
{
    /// <summary>Logical query node.</summary>
    Node = 0,

    /// <summary>Semantic value binding.</summary>
    Binding = 1,

    /// <summary>Query parameter.</summary>
    Parameter = 2,

    /// <summary>Projection, grouping, or aggregation assignment.</summary>
    Assignment = 3,

    /// <summary>Named query result.</summary>
    Result = 4,

    /// <summary>Semantic expression site within another generated construct.</summary>
    Expression = 5,

    /// <summary>Relation or query terminal.</summary>
    Terminal = 6
}

/// <summary>Producer-source attribution for one generated structural decision.</summary>
public sealed record RelationQueryAuthoringSourceDecision
{
    /// <summary>Creates a producer-source attribution.</summary>
    /// <param name="kind">Kind of generated construct.</param>
    /// <param name="target">Stable identity of the owning canonical construct.</param>
    /// <param name="source">Producer source from which the construct was lowered.</param>
    /// <param name="role">Optional stable role of a site within the owning construct.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public RelationQueryAuthoringSourceDecision(
        RelationQueryAuthoringDecisionKind kind,
        string target,
        RelationQueryAuthoringSource source,
        string? role = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported authoring decision kind.");

        Kind = kind;
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Source = Guard.RequireNotNull(source);
        Role = role.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Kind of generated construct.</summary>
    public RelationQueryAuthoringDecisionKind Kind { get; init; }

    /// <summary>Stable identity of the owning canonical construct.</summary>
    public string Target { get; init; }

    /// <summary>Stable role of a site within the owning construct.</summary>
    public string? Role { get; init; }

    /// <summary>Producer source from which the construct was lowered.</summary>
    public RelationQueryAuthoringSource Source { get; init; }
}

/// <summary>
/// Non-semantic construction provenance produced alongside a canonical relation/query definition.
/// </summary>
/// <remarks>
/// The manifest is deliberately outside the canonical IR. It explains authoring decisions without
/// changing semantic fingerprints or introducing a second relation/query model.
/// </remarks>
public sealed record RelationQueryAuthoringManifest
{
    /// <summary>Creates a normalized authoring manifest.</summary>
    /// <param name="identities">Identity-origin decisions made while constructing the definition.</param>
    /// <param name="sources">Producer-source decisions made while constructing the definition.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="identities"/> or <paramref name="sources"/> contains a <see langword="null"/> entry.
    /// </exception>
    public RelationQueryAuthoringManifest(
        ImmutableArray<RelationQueryAuthoringIdentityDecision> identities = default,
        ImmutableArray<RelationQueryAuthoringSourceDecision> sources = default)
    {
        if (!identities.IsDefault && identities.Any(static decision => decision is null))
            throw new ArgumentException("Identity decisions cannot contain null entries.", nameof(identities));
        if (!sources.IsDefault && sources.Any(static decision => decision is null))
            throw new ArgumentException("Source decisions cannot contain null entries.", nameof(sources));

        Identities = identities.IsDefault
            ? []
            :
            [
                .. identities
                    .OrderBy(static decision => decision.Kind)
                    .ThenBy(static decision => decision.Value, StringComparer.Ordinal)
            ];
        Sources = sources.IsDefault
            ? []
            :
            [
                .. sources
                    .OrderBy(static decision => decision.Kind)
                    .ThenBy(static decision => decision.Target, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Role, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Source.Producer, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Source.Reference, StringComparer.Ordinal)
            ];
    }

    /// <summary>Identity-origin decisions retained for the authored definition.</summary>
    public ImmutableArray<RelationQueryAuthoringIdentityDecision> Identities { get; init; }

    /// <summary>Producer-source decisions retained for the authored definition.</summary>
    public ImmutableArray<RelationQueryAuthoringSourceDecision> Sources { get; init; }
}
