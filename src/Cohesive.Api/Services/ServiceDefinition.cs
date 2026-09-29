using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Cohesive.Model.Serialization;

namespace Cohesive.Api.Services;

/// <summary>One exposed semantic operation, independent of transport and deployment objects.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ServiceTransitionOperation), "transition")]
[JsonDerivedType(typeof(ServiceQueryOperation), "query")]
[JsonDerivedType(typeof(ServiceProcessOperation), "process")]
[JsonDerivedType(typeof(ServiceProcessEntityResultOperation), "processEntityResult")]
[JsonDerivedType(typeof(ServiceProcessResultOperation), "processResult")]
[JsonDerivedType(typeof(ServiceProcessControlOperation), "processControl")]
public abstract record ServiceOperation
{
    /// <summary>Normalizes common operation identity and authorization requirements.</summary>
    /// <exception cref="ArgumentException">Identity is empty or requirements contain null or duplicate identities.</exception>
    private protected ServiceOperation(string id, ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var requirements = authorizationRequirements.IsDefault ? [] : authorizationRequirements;
        if (requirements.Any(requirement => requirement is null)
            || requirements.Select(requirement => requirement.Id).Distinct(StringComparer.Ordinal).Count() != requirements.Length)
            throw new ArgumentException("Authorization requirements must have unique identities.", nameof(authorizationRequirements));
        Id = id;
        AuthorizationRequirements = [.. requirements.OrderBy(requirement => requirement.Id, StringComparer.Ordinal)];
    }

    /// <summary>Stable operation identity within the service; projected API identity derives from this value.</summary>
    public string Id { get; }
    /// <summary>Transport-neutral requirements interpreted by every invocation's authority binding.</summary>
    public ImmutableArray<ApiAuthorizationRequirement> AuthorizationRequirements { get; }

    /// <summary>Compares the shared operation semantics by value, including the operation family.</summary>
    public virtual bool Equals(ServiceOperation? other) => ReferenceEquals(this, other)
        || other is not null && EqualityContract == other.EqualityContract && Id == other.Id
        && AuthorizationRequirements.SequenceEqual(other.AuthorizationRequirements);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EqualityContract); hash.Add(Id);
        foreach (var requirement in AuthorizationRequirements) hash.Add(requirement);
        return hash.ToHashCode();
    }
}

/// <summary>Exposes one exact Transition over one entity authority; referenced contracts remain authoritative.</summary>
public sealed record ServiceTransitionOperation : ServiceOperation
{
    /// <summary>Declares an entity operation and its requirements.</summary>
    /// <exception cref="ArgumentException">An identity is empty or authorization requirements are invalid.</exception>
    /// <exception cref="ArgumentNullException">The exact Transition reference is null.</exception>
    [JsonConstructor]
    public ServiceTransitionOperation(string id, QualifiedShapeId entity, ExecutionDefinitionReference transition,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default)
        : base(id, authorizationRequirements)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (string.IsNullOrWhiteSpace(entity.GraphId.Value) || string.IsNullOrWhiteSpace(entity.ShapeId.Value))
            throw new ArgumentException("An exact entity state shape is required.", nameof(entity));
        Entity = entity;
        Transition = transition;
    }

    /// <summary>Graph-qualified entity state authority admitted by the binding.</summary>
    public QualifiedShapeId Entity { get; }
    /// <summary>Exact behavior definition; contracts and behavior are not copied into the service.</summary>
    public ExecutionDefinitionReference Transition { get; }
}

/// <summary>Exposes an exact relation/query definition whose parameters and results remain authoritative.</summary>
public sealed record ServiceQueryOperation : ServiceOperation
{
    /// <summary>Declares a read operation and the requirements enforced before source acquisition.</summary>
    /// <exception cref="ArgumentException">Identity or authorization requirements are invalid.</exception>
    /// <exception cref="ArgumentNullException">The exact query reference is null.</exception>
    [JsonConstructor]
    public ServiceQueryOperation(string id, ExecutionDefinitionReference query, QueryParameterId scopeParameter,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default)
        : base(id, authorizationRequirements)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        if (string.IsNullOrWhiteSpace(scopeParameter.Value))
            throw new ArgumentException("A query operation requires an explicit authorized-scope parameter.", nameof(scopeParameter));
        ScopeParameter = scopeParameter;
    }

    /// <summary>Required string parameter populated from trusted logical scope, never caller input.</summary>
    public QueryParameterId ScopeParameter { get; }

    /// <summary>Exact native relation/query authority, including its semantic fingerprint.</summary>
    public ExecutionDefinitionReference Query { get; }
}

/// <summary>Exposes admission to an exact Process; its graph owns sequencing and recovery semantics.</summary>
public sealed record ServiceProcessOperation : ServiceOperation
{
    /// <summary>Declares a Process entry operation and its requirements.</summary>
    /// <exception cref="ArgumentException">Identity or authorization requirements are invalid.</exception>
    /// <exception cref="ArgumentNullException">The exact Process reference is null.</exception>
    [JsonConstructor]
    public ServiceProcessOperation(string id, ExecutionDefinitionReference process,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default,
        ServiceProcessExecution? execution = null)
        : base(id, authorizationRequirements)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Execution = execution;
    }

    /// <summary>Explicit lifetime and completion policy. Omission preserves native durable start admission.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceProcessExecution? Execution { get; }

    /// <summary>Exact Process authority; the service does not copy its graph, contracts or control lifecycle.</summary>
    public ExecutionDefinitionReference Process { get; }
}

/// <summary>Reads the canonical terminal value of one exact Process, independently of entity receipts.</summary>
/// <remarks>Requirements authorize disclosure of the Process output within the admitted logical scope.
/// Processes exposing resource-sensitive output must declare a suitably restricted result-read capability.</remarks>
public sealed record ServiceProcessResultOperation : ServiceOperation
{
    /// <summary>Declares an independently authorized terminal-value read; the Process owns its output contract.</summary>
    /// <exception cref="ArgumentNullException">The exact Process reference is null.</exception>
    [JsonConstructor]
    public ServiceProcessResultOperation(string id, ExecutionDefinitionReference process,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default,
        ExecutionDefinitionReference? resultClassifier = null)
        : base(id, authorizationRequirements)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        ResultClassifier = resultClassifier;
    }

    /// <summary>Optional exact deterministic Query classifying the public terminal value before disclosure.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionDefinitionReference? ResultClassifier { get; }

    /// <summary>Exact Process authority supplying the result contract and retained terminal value.</summary>
    public ExecutionDefinitionReference Process { get; }
}

/// <summary>Reads the exact entity receipt produced at one declared node of a terminal Process attempt.</summary>
/// <remarks>The Process owns execution. This operation selects retained commit evidence for a response;
/// it neither reruns the Process nor substitutes current entity state when evidence is unavailable.</remarks>
public sealed record ServiceProcessEntityResultOperation : ServiceOperation
{
    /// <summary>Declares an independently authorized committed-entity result read.</summary>
    /// <param name="id">Service operation identity.</param>
    /// <param name="process">Exact Process definition.</param>
    /// <param name="commitNode">Transition invocation node whose single terminal-attempt receipt supplies the response.</param>
    /// <param name="entity">Exact entity state shape exposed by the result binding.</param>
    /// <param name="authorizationRequirements">Requirements for reading this result, independent of start admission.</param>
    /// <param name="resultClassifier">Optional exact Query returning standard service classification.</param>
    /// <exception cref="ArgumentException">A node or entity identity is empty.</exception>
    /// <exception cref="ArgumentNullException">The Process reference is null.</exception>
    [JsonConstructor]
    public ServiceProcessEntityResultOperation(string id, ExecutionDefinitionReference process,
        ExecutionNodeId commitNode, QualifiedShapeId entity,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default,
        ExecutionDefinitionReference? resultClassifier = null) : base(id, authorizationRequirements)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentException.ThrowIfNullOrWhiteSpace(commitNode.Value);
        if (string.IsNullOrWhiteSpace(entity.GraphId.Value) || string.IsNullOrWhiteSpace(entity.ShapeId.Value))
            throw new ArgumentException("An exact entity state shape is required.", nameof(entity));
        CommitNode = commitNode;
        Entity = entity;
        ResultClassifier = resultClassifier;
    }
    /// <summary>Optional exact deterministic Query classifying the terminal value before receipt resolution.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionDefinitionReference? ResultClassifier { get; }
    /// <summary>Exact Process authority from which evidence must be read.</summary>
    public ExecutionDefinitionReference Process { get; }
    /// <summary>Declared Transition invocation node; ambiguous repeated occurrences cannot produce a single entity response.</summary>
    public ExecutionNodeId CommitNode { get; }
    /// <summary>Graph-qualified entity state authority.</summary>
    public QualifiedShapeId Entity { get; }
}

/// <summary>Exposes one native lifecycle action restricted to an exact Process definition.</summary>
public sealed record ServiceProcessControlOperation : ServiceOperation
{
    /// <summary>Declares a control action using the existing canonical Process-control vocabulary.</summary>
    /// <exception cref="ArgumentException">Identity, action or requirements are invalid.</exception>
    /// <exception cref="ArgumentNullException">The Process reference is null.</exception>
    [JsonConstructor]
    public ServiceProcessControlOperation(string id, ExecutionDefinitionReference process, string action,
        ImmutableArray<ApiAuthorizationRequirement> authorizationRequirements = default)
        : base(id, authorizationRequirements)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _ = ExecutionControlWireNames.CommandPath(action);
        Action = action;
    }

    /// <summary>Exact Process definition admitted by this operation.</summary>
    public ExecutionDefinitionReference Process { get; }
    /// <summary>Canonical native command action; no service-specific control enumeration is introduced.</summary>
    public string Action { get; }
}

/// <summary>
/// Portable service composition. Identity, revision, fingerprint and provenance belong to the shared
/// execution-definition envelope. Referenced definitions own behavior and execution contracts.
/// </summary>
public sealed record ServiceDefinition
{
    /// <summary>Creates the exposed operation set in deterministic ordinal identity order.</summary>
    /// <exception cref="ArgumentException">The set is empty, contains null or repeats an operation identity.</exception>
    [JsonConstructor]
    public ServiceDefinition(ImmutableArray<ServiceOperation> operations)
    {
        if (operations.IsDefaultOrEmpty || operations.Any(operation => operation is null)
            || operations.Select(operation => operation.Id).Distinct(StringComparer.Ordinal).Count() != operations.Length)
            throw new ArgumentException("A service requires a nonempty set of uniquely identified operations.", nameof(operations));
        Operations = [.. operations.OrderBy(operation => operation.Id, StringComparer.Ordinal)];
    }

    /// <summary>Canonical exposed operation order; these declarations own service membership.</summary>
    public ImmutableArray<ServiceOperation> Operations { get; }

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
