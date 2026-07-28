using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Identifies the frontend, importer, inference engine, or compiler that produced execution IR.
/// </summary>
public sealed record ExecutionProducerProvenance
{
    /// <summary>Creates producer provenance.</summary>
    /// <param name="producer">Stable producer identity independent of a host-language runtime type.</param>
    /// <param name="version">Optional producer version.</param>
    /// <exception cref="ArgumentNullException"><paramref name="producer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="producer"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionProducerProvenance(string producer, string? version = null)
    {
        Producer = Guard.RequireNotNullOrWhiteSpace(producer);
        Version = version.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Stable producer identity independent of a host-language runtime type.</summary>
    public string Producer { get; }

    /// <summary>Optional producer version, or <see langword="null"/> when it is unknown.</summary>
    public string? Version { get; }
}

/// <summary>
/// Portable attribution to the producer input from which execution IR was derived.
/// </summary>
public sealed record ExecutionSourceProvenance
{
    /// <summary>Creates source provenance.</summary>
    /// <param name="reference">
    /// Producer-defined stable reference to the source document or construct.
    /// </param>
    /// <param name="semanticPath">
    /// Optional semantic path locating the derived construct in canonical execution IR.
    /// </param>
    /// <param name="description">Optional human-readable source description.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="reference"/> is empty or consists only of white-space characters, or
    /// <paramref name="semanticPath"/> contains a default uninitialized path.
    /// </exception>
    [JsonConstructor]
    public ExecutionSourceProvenance(
        string reference,
        ExecutionSemanticPath? semanticPath = null,
        string? description = null)
    {
        if (semanticPath is { Segments.IsDefaultOrEmpty: true })
        {
            throw new ArgumentException(
                "Source provenance cannot contain a default execution semantic path.",
                nameof(semanticPath));
        }

        Reference = Guard.RequireNotNullOrWhiteSpace(reference);
        SemanticPath = semanticPath;
        Description = description.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Producer-defined stable reference to the source document or construct.</summary>
    public string Reference { get; }

    /// <summary>
    /// Optional semantic path locating the derived construct in canonical execution IR.
    /// </summary>
    public ExecutionSemanticPath? SemanticPath { get; }

    /// <summary>Optional human-readable source description.</summary>
    public string? Description { get; }
}

/// <summary>
/// Required producer and source attribution for persisted execution semantics.
/// </summary>
public sealed record ExecutionProvenance
{
    /// <summary>Creates execution provenance.</summary>
    /// <param name="producer">Producer responsible for the canonical execution IR.</param>
    /// <param name="source">Portable reference to the producer input.</param>
    /// <param name="origin">Coarse origin category of the persisted semantics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="producer"/> or <paramref name="source"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="origin"/> is not a defined <see cref="DocumentOrigin"/> value.
    /// </exception>
    [JsonConstructor]
    public ExecutionProvenance(
        ExecutionProducerProvenance producer,
        ExecutionSourceProvenance source,
        DocumentOrigin origin = DocumentOrigin.Unknown)
    {
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported execution document origin.");

        Producer = Guard.RequireNotNull(producer);
        Source = Guard.RequireNotNull(source);
        Origin = origin;
    }

    /// <summary>Producer responsible for the canonical execution IR.</summary>
    public ExecutionProducerProvenance Producer { get; }

    /// <summary>Portable reference to the producer input.</summary>
    public ExecutionSourceProvenance Source { get; }

    /// <summary>Coarse origin category of the persisted semantics.</summary>
    public DocumentOrigin Origin { get; }
}
