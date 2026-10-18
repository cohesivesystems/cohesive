using Cohesive.Infra.Realization;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Infra;

/// <summary>
/// Immutable output of one coordinated infrastructure-definition and binding-elaboration authoring session.
/// </summary>
/// <remarks>
/// This value groups two independently portable canonical artifacts and optional setup-only adapter preparations. It is not a third
/// semantic authority: consumers validate, persist, and reference <see cref="Definition"/> and
/// <see cref="BindingElaborationProfile"/> through their existing exact schemas and fingerprints.
/// </remarks>
public sealed record InfrastructureAuthoringResult
{
    /// <summary>Creates a coordinated authoring result from its two canonical artifacts.</summary>
    /// <param name="definition">Exactly fingerprinted infrastructure-definition document.</param>
    /// <param name="bindingElaborationProfile">Exactly fingerprinted binding-elaboration profile.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="bindingElaborationProfile"/> is <see langword="null"/>.
    /// </exception>
    public InfrastructureAuthoringResult(
        InfrastructureDefinitionDocument definition,
        InfrastructureBindingElaborationProfile bindingElaborationProfile)
    {
        Definition = Guard.RequireNotNull(definition);
        BindingElaborationProfile = Guard.RequireNotNull(bindingElaborationProfile);
    }

    internal InfrastructureAuthoringResult(InfrastructureDefinitionDocument definition,
        InfrastructureBindingElaborationProfile profile,
        ImmutableDictionary<InfrastructureNodeId, IInfrastructureResourcePreparation> preparations)
        : this(definition, profile) => ResourcePreparations = preparations;

    /// <summary>Immutable, setup-only adapter attachments; never part of the portable canonical artifacts.</summary>
    [JsonIgnore]
    public ImmutableDictionary<InfrastructureNodeId, IInfrastructureResourcePreparation> ResourcePreparations { get; }
        = ImmutableDictionary<InfrastructureNodeId, IInfrastructureResourcePreparation>.Empty;

    /// <summary>Requires an exact adapter preparation for a declared resource.</summary>
    /// <typeparam name="T">Expected adapter preparation type.</typeparam>
    /// <param name="node">Canonical resource identity.</param>
    /// <returns>The preparation retained by this authoring session.</returns>
    /// <exception cref="InvalidOperationException">No preparation of the requested type exists for the node.</exception>
    public T RequireResourcePreparation<T>(InfrastructureNodeId node) where T : class, IInfrastructureResourcePreparation =>
        ResourcePreparations.TryGetValue(node, out var preparation) && preparation is T typed ? typed
            : throw new InvalidOperationException($"Resource '{node.Value}' has no {typeof(T).Name} preparation.");

    /// <summary>Exactly fingerprinted canonical infrastructure definition.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Exactly fingerprinted canonical profile elaborating every coordinated binding contract.</summary>
    public InfrastructureBindingElaborationProfile BindingElaborationProfile { get; }
}


/// <summary>Immutable adapter preparation whose capabilities become resource requirements.</summary>
/// <remarks>Owned by one authoring result, with no runtime services or credentials. Canonical requirements
/// and eventual implementation evidence remain the portable authorities; this attachment is setup-only.</remarks>
public interface IInfrastructureResourcePreparation
{
    /// <summary>Provider-neutral requirements derived from the prepared declarations.</summary>
    ImmutableArray<InfrastructureCapabilityId> Requirements { get; }
}
