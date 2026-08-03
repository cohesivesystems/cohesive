using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Identity;
using Cohesive.Transitions.Compilation;
using LegacyTransitionDefinition = Cohesive.Transitions.Model.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionAuthorityMigrationTests
{
    [Fact]
    public void ApiAndAspNetTransitionBoundaries_ConsumeOnlyExactCanonicalAuthority()
    {
        Assert.DoesNotContain(
            typeof(ApiOperation).GetProperties(),
            static property => property.PropertyType == typeof(LegacyTransitionDefinition));

        var bindings = typeof(EntityApiOperationBinding)
            .GetMethods()
            .Where(static method => method.Name == nameof(EntityApiOperationBinding.Transition))
            .ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, static binding =>
        {
            Assert.Contains(
                binding.GetParameters(),
                static parameter => parameter.ParameterType == typeof(CompiledTransitionPlan));
            Assert.DoesNotContain(
                binding.GetParameters(),
                static parameter => parameter.ParameterType == typeof(LegacyTransitionDefinition)
                    || string.Equals(parameter.Name, "transitionName", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void IdentityDomain_DoesNotRegisterFlatTransitionDefinitions()
    {
        Assert.Empty(IdentityDomainModel.Scope.Definition.Transitions);
        Assert.Empty(IdentityDomainModel.PrincipalAccount.Definition.Transitions);
        Assert.Empty(IdentityDomainModel.ScopeMembership.Definition.Transitions);
    }
}
