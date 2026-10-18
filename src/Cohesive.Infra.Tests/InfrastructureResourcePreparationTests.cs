using System.Collections.Immutable;
using Cohesive.Infra;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureResourcePreparationTests
{
    sealed record Preparation(ImmutableArray<InfrastructureCapabilityId> Requirements) : IInfrastructureResourcePreparation;
    static readonly InfrastructureNodeId Store = new("store");

    [Fact]
    public void Preparation_is_local_to_authoring_and_derives_canonical_requirements()
    {
        var preparation = new Preparation([new("capability/read")]);
        var authored = Infrastructure.Define(new("test"), new("1"), new("bindings"),
            infra => infra.Resource(Store).Persistent().Prepared(preparation));
        Assert.Same(preparation, authored.RequireResourcePreparation<Preparation>(Store));
        Assert.Equal("capability/read", Assert.Single(Assert.Single(authored.Definition.Definition.Resources).Requirements).Capability.Value);
        var portable = new InfrastructureAuthoringResult(authored.Definition, authored.BindingElaborationProfile);
        Assert.Empty(portable.ResourcePreparations);
        Assert.Equal(authored.Definition.Fingerprint, portable.Definition.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => portable.RequireResourcePreparation<Preparation>(Store));
    }

    [Fact]
    public void Failed_attachment_does_not_leave_partial_requirements_or_evidence()
    {
        var authored = Infrastructure.Define(new("test"), new("1"), new("bindings"), infra =>
        {
            var resource = infra.Resource(Store).Persistent().Requires(new("existing"));
            Assert.Throws<ArgumentException>(() => resource.Prepared(new Preparation(default)));
            Assert.Throws<ArgumentException>(() => resource.Prepared(new Preparation([new("extra"), new("existing")])));
            resource.Prepared(new Preparation([new("valid")]));
            Assert.Throws<InvalidOperationException>(() => resource.Prepared(new Preparation([new("second")])));
        });
        var capabilities = Assert.Single(authored.Definition.Definition.Resources).Requirements.Select(item => item.Capability.Value).ToArray();
        Assert.Contains("existing", capabilities);
        Assert.Contains("valid", capabilities);
        Assert.Equal(2, capabilities.Length);
    }
}
