using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Api.Services;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Derived endpoint link; the original operation and canonical references remain authoritative.</summary>
/// <param name="Endpoint">Original API operation, including transport and response semantics.</param>
/// <param name="Execution">Selected query, process or service reference; null when not supplied by this binding.</param>
/// <param name="Repository">Entity observation contract; null for non-entity bindings.</param>
public sealed record ServiceEndpointImplementation(ApiOperation Endpoint, ExecutionDefinitionReference? Execution, ValueContract? Repository)
{
    /// <summary>Admitted original service operation, when the endpoint runs or polls a process.</summary>
    public ServiceEndpointOperation? Service { get; internal init; }
}

/// <summary>Exact service declaration and selected operation admitted together with an endpoint.</summary>
public sealed class ServiceEndpointOperation
{
    internal ServiceEndpointOperation(ExecutionDefinitionDocument declaration, ServiceOperation operation)
    { Declaration = declaration; Operation = operation; }
    /// <summary>Original canonical service document; no execution policy is copied or inferred.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Original selected operation, including its lifetime, completion and authorization requirements.</summary>
    public ServiceOperation Operation { get; }
}

/// <summary>Payload-free query ownership and admitted consistency, projected from successful offline preparation.</summary>
/// <param name="Query">Exact selected query definition.</param>
/// <param name="Plan">Exact compiled input/shape/catalog/demand identity.</param>
/// <param name="Sources">All source and traversal shapes required by the plan.</param>
/// <param name="Storage">Contributing storage model name; a display label, not a new semantic identity.</param>
/// <param name="Consistency">Declared consistency accepted by the provider's offline preparation.</param>
/// <param name="Read">Selected serving backend and guarantee, when declared by the composition; not a native readiness attestation.</param>
/// <param name="MaximumRows">Complete-result row bound.</param>
/// <param name="MaximumBytes">Decoded-value byte bound.</param>
public sealed record ServiceQueryPlacement(ExecutionDefinitionReference Query, RelationQueryCompiledPlanReference Plan,
    ImmutableArray<QualifiedShapeId> Sources, string Storage, RelationQueryConsistency Consistency, int MaximumRows, long MaximumBytes, ServiceQueryReadSelection? Read = null);

/// <summary>Canonical entity contract and its admitted storage owner.</summary>
/// <param name="Observation">Original observation contract.</param><param name="Storage">Contributing model label.</param>
public sealed record ServiceRepositoryPlacement(ValueContract Observation, string Storage);

/// <summary>Inspection projection of successful offline admission. No native clients, credentials or runtime observations are retained.</summary>
/// <remarks>Not a new executable authority or a claim of deployment readiness. Canonical referenced documents own fingerprints.</remarks>
/// <param name="Queries">Query placements in ordinal definition identity order.</param>
/// <param name="Repositories">Repository placements in ordinal contract order.</param>
public sealed record ServiceStorageExplanation(ImmutableArray<ServiceQueryPlacement> Queries,
    ImmutableArray<ServiceRepositoryPlacement> Repositories);

/// <summary>Inspection of an admitted fixed or flag-based serving selection; never a runtime decision.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ServiceQueryReadBinding), "fixed")]
[JsonDerivedType(typeof(ServiceQueryFeatureSelection), "featureFlag")]
public abstract record ServiceQueryReadSelection
{
    private protected ServiceQueryReadSelection() { }
    /// <summary>Minimum guarantee across every candidate.</summary>
    public abstract RelationQueryConsistency Consistency { get; }
}

/// <summary>Serving selection owned by a storage composition, independent of source-entity placement.</summary>
public sealed record ServiceQueryReadBinding : ServiceQueryReadSelection
{
    /// <summary>Declares a serving backend and its existing logical resource identity.</summary>
    /// <param name="backend">Provider label; not a capability attestation.</param>
    /// <param name="resource">Existing native resource or materialization target identity.</param>
    /// <param name="consistency">Minimum consistency guaranteed by the selected reader.</param>
    public ServiceQueryReadBinding(string backend, string resource, RelationQueryConsistency consistency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (!Enum.IsDefined(consistency)) throw new ArgumentOutOfRangeException(nameof(consistency));
        Backend = backend; Resource = resource; Consistency = consistency;
    }
    /// <summary>Fixed provider label.</summary>
    public string Backend { get; }
    /// <summary>Fixed serving resource.</summary>
    public string Resource { get; }
    /// <summary>Declared minimum reader guarantee, checked against endpoint demand and the bound reader.</summary>
    public override RelationQueryConsistency Consistency { get; }
}

/// <summary>A flag selecting between two fixed candidates. There is no single selected Backend or Resource.</summary>
public sealed record ServiceQueryFeatureSelection : ServiceQueryReadSelection
{
    /// <summary>Retains the declared candidates without claiming a current flag value.</summary>
    /// <param name="flag">Flag key.</param><param name="disabled">Native reader selected for false.</param>
    /// <param name="enabled">Alternate reader selected for true.</param>
    /// <exception cref="ServiceBindingValidationException">The native reader is weaker than the alternate guarantee.</exception>
    public ServiceQueryFeatureSelection(string flag, ServiceQueryReadBinding disabled, ServiceQueryReadBinding enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        ArgumentNullException.ThrowIfNull(disabled); ArgumentNullException.ThrowIfNull(enabled);
        ServiceQueryConsistencyValidation.Require(disabled.Consistency, enabled.Consistency);
        Flag = flag; Disabled = disabled; Enabled = enabled;
    }
    /// <summary>Provider flag key.</summary>
    public string Flag { get; }
    /// <summary>False candidate.</summary>
    public ServiceQueryReadBinding Disabled { get; }
    /// <summary>True candidate.</summary>
    public ServiceQueryReadBinding Enabled { get; }
    /// <inheritdoc />
    public override RelationQueryConsistency Consistency => Enabled.Consistency;
}
