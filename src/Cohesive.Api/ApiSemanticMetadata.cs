using Cohesive.Execution;

namespace Cohesive.Api;

/// <summary>
/// Declares one transport-neutral authorization requirement for an API operation.
/// </summary>
public sealed record ApiAuthorizationRequirement
{
    /// <summary>Creates an authorization requirement.</summary>
    /// <param name="id">Stable requirement identity interpreted by an authorization adapter.</param>
    /// <param name="description">Optional human-readable explanation of the requirement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or consists only of white-space characters.
    /// </exception>
    public ApiAuthorizationRequirement(string id, string? description = null)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>Stable requirement identity interpreted by authorization projections.</summary>
    public string Id { get; }

    /// <summary>Optional human-readable explanation of the requirement.</summary>
    public string? Description { get; }
}

/// <summary>
/// Attributes an API operation to one exact construct owned by another semantic authority.
/// </summary>
/// <remarks>
/// One operation may carry references to multiple authorities, such as its API declaration and
/// the execution-kernel contract that it projects. The authority, schema version, and semantic
/// path form the stable coordinate; optional source provenance explains where that construct was
/// authored or derived.
/// </remarks>
public sealed record ApiSemanticReference
{
    /// <summary>Creates a semantic reference.</summary>
    /// <param name="authority">Stable identity of the semantic owner.</param>
    /// <param name="schemaVersion">Exact schema version under which <paramref name="path"/> is interpreted.</param>
    /// <param name="path">Canonical path to the referenced construct within the authority's schema.</param>
    /// <param name="source">Optional attribution to the producer source for the referenced construct.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is empty or white space, <paramref name="schemaVersion"/> is default or empty,
    /// or <paramref name="path"/> is default or empty.
    /// </exception>
    public ApiSemanticReference(
        string authority,
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionSemanticPath path,
        ExecutionSourceProvenance? source = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
        {
            throw new ArgumentException(
                "An API semantic reference requires a non-default schema version.",
                nameof(schemaVersion));
        }

        if (path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "An API semantic reference requires a non-default semantic path.",
                nameof(path));
        }

        SchemaVersion = schemaVersion;
        Path = path;
        Source = source;
    }

    /// <summary>Stable identity of the semantic owner.</summary>
    public string Authority { get; }

    /// <summary>Exact schema version under which <see cref="Path"/> is interpreted.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Canonical path to the referenced construct.</summary>
    public ExecutionSemanticPath Path { get; }

    /// <summary>Optional attribution to the source from which the construct was produced.</summary>
    public ExecutionSourceProvenance? Source { get; }
}
