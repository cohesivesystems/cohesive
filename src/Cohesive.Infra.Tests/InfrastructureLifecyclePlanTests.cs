using Cohesive.Infra.Realization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureLifecyclePlanTests
{
    [Fact]
    public void One_interpreter_can_manage_while_other_interpreters_reference_the_same_resource()
    {
        InfrastructureNodeId store = new("store");
        var definition = Definition(new InfrastructureResourceDefinition(
            store,
            InfrastructureResourceLifecycle.Persistent));

        var plan = new InfrastructureLifecyclePlan(
            definition,
            [
                new(
                    store,
                    new("azure/cosmos/store"),
                    new("terraform"),
                    new("terraform/state/production"),
                    InfrastructureLifecycleDisposition.Managed),
                new(
                    store,
                    new("azure/cosmos/store"),
                    new("aspire"),
                    new("terraform/state/production"),
                    InfrastructureLifecycleDisposition.Referenced)
            ]);

        Assert.Equal(2, plan.Bindings.Length);
        Assert.Single(
            plan.Bindings,
            static binding => binding.Disposition == InfrastructureLifecycleDisposition.Managed);
    }

    [Fact]
    public void One_physical_resource_cannot_be_external_and_managed_through_different_logical_aliases()
    {
        InfrastructureNodeId externalRegistry = new("shared-registry-reference");
        InfrastructureNodeId managedRegistry = new("shared-registry-manager");
        var definition = Definition(
            new InfrastructureResourceDefinition(externalRegistry, InfrastructureResourceLifecycle.External),
            new InfrastructureResourceDefinition(managedRegistry, InfrastructureResourceLifecycle.Persistent));

        var exception = Assert.Throws<ArgumentException>(() => new InfrastructureLifecyclePlan(
            definition,
            [
                new(
                    externalRegistry,
                    new("azure/ml/registry/shared"),
                    new("aspire"),
                    new("azure/shared-state"),
                    InfrastructureLifecycleDisposition.Referenced),
                new(
                    managedRegistry,
                    new("azure/ml/registry/shared"),
                    new("pulumi"),
                    new("azure/shared-state"),
                    InfrastructureLifecycleDisposition.Managed)
            ]));

        Assert.Contains("cannot be external", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_interpreters_of_one_logical_resource_must_name_one_lifecycle_authority()
    {
        InfrastructureNodeId store = new("store");
        var definition = Definition(new InfrastructureResourceDefinition(
            store,
            InfrastructureResourceLifecycle.Persistent));

        var exception = Assert.Throws<ArgumentException>(() => new InfrastructureLifecyclePlan(
            definition,
            [
                new(
                    store,
                    new("azure/cosmos/store"),
                    new("terraform"),
                    new("terraform/state/production"),
                    InfrastructureLifecycleDisposition.Managed),
                new(
                    store,
                    new("azure/cosmos/store"),
                    new("aspire"),
                    new("aspire/apphost/local"),
                    InfrastructureLifecycleDisposition.Referenced)
            ]));

        Assert.Contains("inconsistent physical identities or lifecycle authorities", exception.Message, StringComparison.Ordinal);
    }

    static InfrastructureDefinitionDocument Definition(params InfrastructureResourceDefinition[] resources) =>
        InfrastructureDefinitionDocument.FromDefinition(new(
            new("lifecycle-test"),
            new("v1"),
            resources: [.. resources]));
}
