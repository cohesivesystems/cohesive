using Cohesive.Infra.Realization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureCoordinatedAuthoringTests
{
    [Fact]
    public void Coordinated_authoring_matches_direct_ir_and_derives_stable_binding_identity()
    {
        InfrastructureDefinitionId definitionId = new("system");
        InfrastructureRevisionId revision = new("v1");
        InfrastructureBindingElaborationProfileId profileId = new("system/bindings/v1");
        InfrastructureNodeId api = new("workloads/api");
        InfrastructureNodeId state = new("resources/state");
        InfrastructureBindingContractId repository = new("contracts/repository/v1");
        InfrastructureBindingElaborationRuleId repositoryRule = new("rules/repository/v1");
        InfrastructureCapabilityId endpoint = new("configuration/endpoint");
        InfrastructureCapabilityId authenticated = new("identity/workload-authentication");
        var bindingId = InfrastructureBindingDefinition.DeriveId(api, state, repository);

        var directDefinition = InfrastructureDefinitionDocument.FromDefinition(new(
            definitionId,
            revision,
            workloads: [new(api)],
            resources: [new(state, InfrastructureResourceLifecycle.Persistent)],
            bindings: [new(bindingId, api, state, repository)]));
        InfrastructureBindingElaborationProfile directProfile = new(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            profileId,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new(repositoryRule, repository, [authenticated, endpoint], ["spec://repository"])]);

        var fluent = Infrastructure.Define(definitionId, revision, profileId, infrastructure =>
        {
            var repositoryContract = infrastructure.Contract(repository, repositoryRule)
                .Requires(endpoint)
                .Requires(authenticated)
                .SourcedFrom("spec://repository");
            var apiWorkload = infrastructure.Workload(api);
            var stateResource = infrastructure.Resource(state).Persistent();

            infrastructure.Bind(apiWorkload).To(stateResource).As(repositoryContract);
        });
        var repeated = Infrastructure.Define(definitionId, revision, profileId, infrastructure =>
        {
            var repositoryContract = infrastructure.Contract(repository, repositoryRule)
                .Requires(authenticated)
                .Requires(endpoint)
                .SourcedFrom("spec://repository");
            var stateResource = infrastructure.Resource(state).Persistent();
            var apiWorkload = infrastructure.Workload(api);

            infrastructure.Bind(apiWorkload).To(stateResource).As(repositoryContract);
        });

        Assert.Equal(directDefinition, fluent.Definition);
        Assert.Equal(directProfile, fluent.BindingElaborationProfile);
        Assert.Equal(
            "bindings/workloads%2Fapi/to/resources%2Fstate/as/contracts%2Frepository%2Fv1",
            bindingId.Value);
        Assert.Equal(bindingId, Assert.Single(fluent.Definition.Definition.Bindings).Id);
        Assert.Equal(fluent, repeated);
    }

    [Fact]
    public void Coordinated_authoring_rejects_an_undeclared_binding_contract()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Infrastructure.Define(
            new("system"),
            new("v1"),
            new("system/bindings/v1"),
            infrastructure =>
            {
                var api = infrastructure.Workload(new("api"));
                var state = infrastructure.Resource(new("state")).Persistent();
                infrastructure.Bind(api).To(state).As(new("contracts/repository/v1"));
            }));

        Assert.Contains("has no coordinated elaboration declaration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinated_authoring_rejects_unused_and_incomplete_contract_declarations()
    {
        var unused = Assert.Throws<InvalidOperationException>(() => Infrastructure.Define(
            new("unused"),
            new("v1"),
            new("unused/bindings/v1"),
            infrastructure =>
            {
                _ = infrastructure.Contract(new("contracts/unused/v1"), new("rules/unused/v1"))
                    .Requires(new("unused"))
                    .SourcedFrom("spec://unused");
                _ = infrastructure.Workload(new("api"));
            }));
        Assert.Contains("is declared but is not used", unused.Message, StringComparison.Ordinal);

        var incomplete = Assert.Throws<InvalidOperationException>(() => Infrastructure.Define(
            new("incomplete"),
            new("v1"),
            new("incomplete/bindings/v1"),
            infrastructure =>
            {
                var contract = infrastructure.Contract(
                    new("contracts/incomplete/v1"),
                    new("rules/incomplete/v1"));
                var api = infrastructure.Workload(new("api"));
                var state = infrastructure.Resource(new("state")).Persistent();
                infrastructure.Bind(api).To(state).As(contract);
            }));
        Assert.Contains("must declare at least one capability obligation", incomplete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_handles_are_owned_by_one_authoring_session()
    {
        InfrastructureBindingContractHandle? foreign = null;
        _ = Infrastructure.Define(
            new("foreign"),
            new("v1"),
            new("foreign/bindings/v1"),
            infrastructure =>
            {
                foreign = infrastructure.Contract(new("contracts/repository/v1"), new("rules/repository/v1"))
                    .Requires(new("repository"))
                    .SourcedFrom("spec://repository");
                var api = infrastructure.Workload(new("api"));
                var state = infrastructure.Resource(new("state")).Persistent();
                infrastructure.Bind(api).To(state).As(foreign);
            });

        var error = Assert.Throws<ArgumentException>(() => Infrastructure.Define(
            new("local"),
            new("v1"),
            new("local/bindings/v1"),
            infrastructure =>
            {
                var api = infrastructure.Workload(new("api"));
                var state = infrastructure.Resource(new("state")).Persistent();
                infrastructure.Bind(api).To(state).As(foreign!);
            }));

        Assert.Equal("contract", error.ParamName);
    }

    [Fact]
    public void Conventional_binding_identity_rejects_a_duplicate_semantic_slot()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Infrastructure.Define(
            new("duplicate"),
            new("v1"),
            infrastructure =>
            {
                var api = infrastructure.Workload(new("api"));
                var state = infrastructure.Resource(new("state")).Persistent();
                InfrastructureBindingContractId repository = new("contracts/repository/v1");
                infrastructure.Bind(api).To(state).As(repository);
                infrastructure.Bind(api).To(state).As(repository);
            }));

        Assert.Contains("Conventional infrastructure binding identity", error.Message, StringComparison.Ordinal);
    }
}
