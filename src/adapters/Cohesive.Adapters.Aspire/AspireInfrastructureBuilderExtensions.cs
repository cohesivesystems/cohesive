using Aspire.Hosting;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

namespace Cohesive.Adapters.Aspire;

/// <summary>Attaches canonical requirements and prepared evidence to existing native Aspire resources.</summary>
public static class AspireInfrastructureBuilderExtensions
{
    /// <summary>Associates an activated infrastructure model without building or starting the AppHost.</summary>
    /// <param name="builder">Native AppHost owning all selected resources.</param>
    /// <param name="model">Offline model; its cached definition is prepared on first access.</param>
    /// <param name="id">Explicit deployment identity.</param>
    /// <param name="variant">Explicit environment or capability variant.</param>
    /// <param name="configure">Native associations; the builder freezes when this callback ends.</param>
    /// <returns>Canonical deployment diagnostics and original native resources.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An association or canonical declaration is invalid.</exception>
    /// <exception cref="InvalidOperationException">Model preparation or association authoring fails.</exception>
    public static AspireInfrastructureAssociation AttachCohesive(this IDistributedApplicationBuilder builder,
        InfrastructureModel model, InfrastructureTargetDeploymentManifestId id,
        InfrastructureCapabilityVariantId variant, Action<AspireInfrastructureAssociationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(model);
        return builder.AttachCohesive(model.Definition, id, variant, configure);
    }

    /// <summary>Associates directly authored infrastructure without requiring a model subclass.</summary>
    /// <param name="builder">Native AppHost owning all selected resources.</param>
    /// <param name="definition">Canonical artifacts and optional offline adapter preparations.</param>
    /// <param name="id">Explicit deployment identity.</param>
    /// <param name="variant">Explicit environment or capability variant.</param>
    /// <param name="configure">Native associations; the builder freezes when this callback ends.</param>
    /// <returns>Canonical deployment diagnostics and original native resources.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An association or canonical declaration is invalid.</exception>
    /// <exception cref="InvalidOperationException">Association authoring fails.</exception>
    public static AspireInfrastructureAssociation AttachCohesive(this IDistributedApplicationBuilder builder,
        InfrastructureAuthoringResult definition, InfrastructureTargetDeploymentManifestId id,
        InfrastructureCapabilityVariantId variant, Action<AspireInfrastructureAssociationBuilder> configure) =>
        AspireInfrastructureAssociation.Attach(builder, definition, id, variant, configure);
}
