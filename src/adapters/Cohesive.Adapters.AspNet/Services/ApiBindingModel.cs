using Cohesive.Api.Execution.Services;
using Cohesive.Prelude;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Constructor-authored API implementation recipes; activation validates coverage without resolving storage.</summary>
/// <remarks>Use ServiceApiBindingsBuilder directly when inheritance is unnecessary. Native preparation is explicit and host-owned.</remarks>
public abstract class ApiBindingModel : IAuthoringModel
{
    /// <summary>Construction-only implementation builder, shared with direct authoring.</summary>
    protected ServiceApiBindingsBuilder Bindings { get; } = new();
    /// <summary>Portable selection evidence for infrastructure preparation.</summary>
    public ServiceImplementationPlan Plan => Bindings.Plan;
    void IAuthoringModel.Complete() => Bindings.Complete();
    /// <summary>Resolves runtime bindings once for the supplied host and persistence.</summary>
    /// <param name="services">Host services.</param><param name="persistence">Caller-owned native persistence.</param>
    /// <returns>Prepared HTTP bindings.</returns>
    public ServiceApiBindings Build(IServiceCollection services, IServicePersistence persistence) => Bindings.Build(services, persistence);
}
