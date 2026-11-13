using Cohesive.Model;
namespace Cohesive.Authoring;

/// <summary>
/// Identifies the producer input from which one structural authoring decision was lowered.
/// </summary>
/// <remarks>
/// The reference is intentionally independent of C# expression trees so importers, generators,
/// Ari, and future host-language frontends can preserve their own source identities without
/// changing canonical semantics.
/// </remarks>
public sealed record AuthoringSource
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
    public AuthoringSource(
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
