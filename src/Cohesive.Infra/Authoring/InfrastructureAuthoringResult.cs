using Cohesive.Infra.Realization;

namespace Cohesive.Infra;

/// <summary>
/// Immutable output of one coordinated infrastructure-definition and binding-elaboration authoring session.
/// </summary>
/// <remarks>
/// This value groups two independently portable canonical artifacts for convenient publication. It is not a third
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

    /// <summary>Exactly fingerprinted canonical infrastructure definition.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Exactly fingerprinted canonical profile elaborating every coordinated binding contract.</summary>
    public InfrastructureBindingElaborationProfile BindingElaborationProfile { get; }
}
