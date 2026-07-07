using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Entry point for building <see cref="DomainModelDefinition"/> instances.
/// </summary>
public static class DomainModelDsl
{
    /// <summary>
    /// Builds a domain model using the fluent builder API.
    /// </summary>
    public static DomainModelDefinition Define(Action<DomainModelBuilder> configure)
    {
        var builder = new DomainModelBuilder();
        configure(obj: builder);
        return builder.Build();
    }
}