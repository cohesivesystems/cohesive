using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Top-level semantic domain model definition.
/// </summary>
public sealed record DomainModelDefinition
{
    /// <summary>
    /// Creates a domain model definition.
    /// </summary>
    [JsonConstructor]
    public DomainModelDefinition(
        ImmutableArray<EntityDefinition> entities,
        string? version = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Entities = entities.IsDefault ? [] : entities;
        Version = version;
        Annotations = AnnotationMap.Normalize(annotations);

        if (Entities.IsDefaultOrEmpty)
            throw new ArgumentException(message: "Domain model requires at least one entity.", paramName: nameof(entities));

        var duplicateEntity = Entities.TryGetDuplicateByKey(x => x.Name.Value, StringComparer.Ordinal);
        if (duplicateEntity is not null)
            throw new ArgumentException(message: $"Domain model contains duplicate entity '{duplicateEntity.Name.Value}'.", paramName: nameof(entities));
    }

    /// <summary>
    /// All entity definitions in the domain.
    /// </summary>
    public ImmutableArray<EntityDefinition> Entities { get; init; }
    
    /// <summary>
    /// Optional model version string.
    /// </summary>
    public string? Version { get; init; }
    
    /// <summary>
    /// Optional metadata extensions.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}
