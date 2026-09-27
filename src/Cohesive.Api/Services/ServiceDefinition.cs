using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Api.Services;

/// <summary>
/// Portable service operation exposing one exact Transition over one entity authority.
/// Input, observation and outcome contracts remain owned by the referenced Transition.
/// </summary>
/// <remarks>HTTP, repositories, policy evaluators and other host objects belong to runtime bindings.</remarks>
public sealed record ServiceTransitionOperation
{
    /// <summary>Declares one operation and the requirements every invocation must enforce.</summary>
    /// <exception cref="ArgumentException">An identity is empty or requirements repeat an identity.</exception>
    /// <exception cref="ArgumentNullException">The exact Transition reference is null.</exception>
    [JsonConstructor]
    public ServiceTransitionOperation(string id, QualifiedShapeId entity, ExecutionDefinitionReference transition,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(transition);
        if (string.IsNullOrWhiteSpace(entity.GraphId.Value) || string.IsNullOrWhiteSpace(entity.ShapeId.Value))
            throw new ArgumentException("An exact entity state shape is required.", nameof(entity));
        var requirements = authorizationRequirements.IsDefault ? [] : authorizationRequirements;
        if (requirements.Any(requirement => requirement is null)
            || requirements.Select(requirement => requirement.Id).Distinct(StringComparer.Ordinal).Count() != requirements.Length)
            throw new ArgumentException("Authorization requirements must have unique identities.", nameof(authorizationRequirements));
        Id = id;
        Entity = entity;
        Transition = transition;
        AuthorizationRequirements = [.. requirements.OrderBy(requirement => requirement.Id, StringComparer.Ordinal)];
    }

    /// <summary>Stable operation identity within the service; projected API identity derives from this value.</summary>
    public string Id { get; }
    /// <summary>Graph-qualified entity state authority admitted by the binding.</summary>
    public QualifiedShapeId Entity { get; }
    /// <summary>Exact behavior definition; no copy of its contracts or behavior is retained here.</summary>
    public ExecutionDefinitionReference Transition { get; }
    /// <summary>Transport-neutral requirements interpreted by the configured authority binding.</summary>
    public ImmutableArray<ApiAuthorizationRequirement> AuthorizationRequirements { get; }

    /// <summary>Compares complete persisted operation semantics by value.</summary>
    public bool Equals(ServiceTransitionOperation? other) => ReferenceEquals(this, other)
        || other is not null && Id == other.Id && Entity == other.Entity && Transition == other.Transition
        && AuthorizationRequirements.SequenceEqual(other.AuthorizationRequirements);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id); hash.Add(Entity); hash.Add(Transition);
        foreach (var requirement in AuthorizationRequirements) hash.Add(requirement);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Portable service composition. Identity, revision, fingerprint and provenance belong to the shared
/// execution-definition envelope. This initial profile exposes entity Transitions only.
/// </summary>
public sealed record ServiceDefinition
{
    /// <summary>Creates the exposed operation set in deterministic ordinal identity order.</summary>
    /// <exception cref="ArgumentException">The set is empty, contains null or repeats an operation identity.</exception>
    [JsonConstructor]
    public ServiceDefinition(ImmutableArray<ServiceTransitionOperation> operations)
    {
        if (operations.IsDefaultOrEmpty || operations.Any(operation => operation is null)
            || operations.Select(operation => operation.Id).Distinct(StringComparer.Ordinal).Count() != operations.Length)
            throw new ArgumentException("A service requires a nonempty set of uniquely identified operations.", nameof(operations));
        Operations = [.. operations.OrderBy(operation => operation.Id, StringComparer.Ordinal)];
    }

    /// <summary>Canonical exposed operation order; these declarations own service membership.</summary>
    public ImmutableArray<ServiceTransitionOperation> Operations { get; }

    /// <summary>Compares the canonical operation set by value.</summary>
    public bool Equals(ServiceDefinition? other) => ReferenceEquals(this, other)
        || other is not null && Operations.SequenceEqual(other.Operations);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var operation in Operations) hash.Add(operation);
        return hash.ToHashCode();
    }
}

/// <summary>Strict service projection through the existing execution document and fingerprint authority.</summary>
public static class ServiceDefinitionDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<ServiceDefinition> Projection = new(
        new("service"), "services.document.kindMismatch", "services.document.projectionInvalid",
        "services.document.wireNonCanonical", "Service operations must use their canonical ordinal order.");

    /// <summary>Stable shared execution-definition family for service compositions.</summary>
    public static ExecutionDefinitionKind Kind => Projection.Kind;

    /// <summary>Creates a portable, fingerprinted service declaration with required source attribution.</summary>
    /// <exception cref="ArgumentException">Identity or shared document metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">Definition or provenance is null.</exception>
    public static ExecutionDefinitionDocument Create(ExecutionDefinitionId id, ExecutionRevisionId revision,
        ServiceDefinition definition, ExecutionProvenance provenance) =>
        ExecutionDefinitionDocument.Create(Projection.Kind, id, revision, definition, provenance);

    /// <summary>Validates shared integrity and strict typed semantics before returning a usable declaration.</summary>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    public static DocumentValidationResult ValidateAndProject(ExecutionDefinitionDocument document,
        out ServiceDefinition? definition) => Projection.ValidateAndProject(
            ExecutionDefinitionDocumentValidator.Validate(document), document,
            _ => new DocumentValidationResult([]), out definition);
}
