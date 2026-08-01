using Cohesive.Model;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationBackendRoutingConfigurationTests
{
    [Fact]
    public void Resolve_WithSafeDefault_AttributesBothIndependentSettingsToFrameworkConvention()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [first, MaterializationBackendPoolTestFixture.Descriptor("target/b")],
            defaultTarget: first.Id);

        var configuration = MaterializationBackendRoutingConfigurationResolver.Resolve(definition);

        Assert.Equal(first.Id, configuration.ReadTarget);
        Assert.Equal(first.Id, configuration.WriteTarget);
        Assert.Equal(
            [
                MaterializationBackendRoutingSettingNames.ReadTarget,
                MaterializationBackendRoutingSettingNames.WriteTarget
            ],
            configuration.Configuration.Select(static decision => decision.Setting));
        Assert.All(configuration.Configuration, decision =>
        {
            Assert.Equal(EffectiveConfigurationOrigin.FrameworkDefault, decision.Origin);
            Assert.Equal(
                MaterializationBackendRoutingConfigurationResolver.FrameworkConventionAuthority,
                decision.Authority);
        });
    }

    [Fact]
    public void Resolve_AppliesPrecedenceIndependentlyPerReadAndWriteSetting()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var second = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var third = MaterializationBackendPoolTestFixture.Descriptor("target/c");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [first, second, third],
            defaultTarget: first.Id);
        MaterializationBackendRoutingConfigurationLayer adapter = new(
            EffectiveConfigurationOrigin.AdapterConvention,
            "elastic/routing-v1",
            new(readTarget: second.Id, writeTarget: second.Id));
        MaterializationBackendRoutingConfigurationLayer profile = new(
            EffectiveConfigurationOrigin.ScopedProfile,
            "search/profile-v3",
            new(writeTarget: third.Id));
        MaterializationBackendRoutingConfigurationLayer explicitLayer = new(
            EffectiveConfigurationOrigin.Explicit,
            "search/local-override",
            new(readTarget: third.Id));

        var resolved = MaterializationBackendRoutingConfigurationResolver.Resolve(
            definition,
            adapter,
            profile,
            explicitLayer);

        Assert.Equal(third.Id, resolved.ReadTarget);
        Assert.Equal(third.Id, resolved.WriteTarget);
        AssertDecision(
            resolved,
            MaterializationBackendRoutingSettingNames.ReadTarget,
            EffectiveConfigurationOrigin.Explicit,
            "search/local-override");
        AssertDecision(
            resolved,
            MaterializationBackendRoutingSettingNames.WriteTarget,
            EffectiveConfigurationOrigin.ScopedProfile,
            "search/profile-v3");

        MaterializationBackendRoutingConfigurationLayer writeOverride = new(
            EffectiveConfigurationOrigin.Explicit,
            "search/write-override",
            new(writeTarget: second.Id));
        var mixed = MaterializationBackendRoutingConfigurationResolver.Resolve(
            definition,
            writeOverride);
        Assert.Equal(first.Id, mixed.ReadTarget);
        Assert.Equal(second.Id, mixed.WriteTarget);
        AssertDecision(
            mixed,
            MaterializationBackendRoutingSettingNames.ReadTarget,
            EffectiveConfigurationOrigin.FrameworkDefault,
            MaterializationBackendRoutingConfigurationResolver.FrameworkConventionAuthority);
        AssertDecision(
            mixed,
            MaterializationBackendRoutingSettingNames.WriteTarget,
            EffectiveConfigurationOrigin.Explicit,
            "search/write-override");
    }

    [Fact]
    public void Resolve_IsIndependentOfLayerInputOrder()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var second = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: first.Id);
        MaterializationBackendRoutingConfigurationLayer adapter = new(
            EffectiveConfigurationOrigin.AdapterConvention,
            "adapter/v1",
            new(readTarget: second.Id, writeTarget: first.Id));
        MaterializationBackendRoutingConfigurationLayer explicitLayer = new(
            EffectiveConfigurationOrigin.Explicit,
            "local/v1",
            new(writeTarget: second.Id));

        var forward = MaterializationBackendRoutingConfigurationResolver.Resolve(
            definition,
            adapter,
            explicitLayer);
        var reverse = MaterializationBackendRoutingConfigurationResolver.Resolve(
            definition,
            explicitLayer,
            adapter);

        Assert.Equal(forward, reverse);
        Assert.Equal(forward.GetHashCode(), reverse.GetHashCode());
    }

    [Fact]
    public void Resolve_RequiresEverySettingWhenPoolDeclaresNoSafeDefault()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var second = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: null);
        MaterializationBackendRoutingConfigurationLayer incomplete = new(
            EffectiveConfigurationOrigin.Explicit,
            "local/read-only",
            new(readTarget: first.Id));
        MaterializationBackendRoutingConfigurationLayer complete = new(
            EffectiveConfigurationOrigin.Explicit,
            "local/complete",
            new(readTarget: first.Id, writeTarget: second.Id));

        Assert.Throws<InvalidOperationException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(definition));
        Assert.Throws<InvalidOperationException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(definition, incomplete));

        var resolved = MaterializationBackendRoutingConfigurationResolver.Resolve(definition, complete);
        Assert.Equal(first.Id, resolved.ReadTarget);
        Assert.Equal(second.Id, resolved.WriteTarget);
        Assert.All(
            resolved.Configuration,
            static decision => Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin));
    }

    [Fact]
    public void Resolve_ComposesDisjointSameTierLayersAndRejectsPerSettingAmbiguity()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [first],
            defaultTarget: first.Id);
        MaterializationBackendRoutingConfigurationLayer undeclared = new(
            EffectiveConfigurationOrigin.Explicit,
            "local/invalid",
            new(readTarget: new("target/missing")));
        MaterializationBackendRoutingConfigurationLayer profileOne = new(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/one",
            new(readTarget: first.Id));
        MaterializationBackendRoutingConfigurationLayer profileTwo = new(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/two",
            new(writeTarget: first.Id));
        MaterializationBackendRoutingConfigurationLayer conflictingProfile = new(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/conflicting",
            new(readTarget: first.Id));
        MaterializationBackendRoutingConfigurationLayer explicitRead = new(
            EffectiveConfigurationOrigin.Explicit,
            "explicit/read",
            new(readTarget: first.Id));

        Assert.Throws<ArgumentException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(definition, undeclared));
        var composed = MaterializationBackendRoutingConfigurationResolver.Resolve(
            definition,
            profileOne,
            profileTwo);
        Assert.Equal(first.Id, composed.ReadTarget);
        Assert.Equal(first.Id, composed.WriteTarget);
        Assert.Throws<ArgumentException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(
                definition,
                profileOne,
                conflictingProfile));
        Assert.Throws<ArgumentException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(
                definition,
                explicitRead,
                profileOne,
                conflictingProfile));
        Assert.Throws<ArgumentException>(() =>
            MaterializationBackendRoutingConfigurationResolver.Resolve(
                definition,
                profileOne,
                conflictingProfile,
                explicitRead));
        Assert.Throws<ArgumentException>(() => new MaterializationBackendRoutingConfigurationLayer(
            EffectiveConfigurationOrigin.AdapterConvention,
            "adapter/empty",
            new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterializationBackendRoutingConfigurationLayer(
            EffectiveConfigurationOrigin.FrameworkDefault,
            "framework/forged",
            new(readTarget: first.Id)));
    }

    static void AssertDecision(
        MaterializationBackendRoutingConfiguration configuration,
        string setting,
        EffectiveConfigurationOrigin origin,
        string authority)
    {
        var decision = Assert.Single(
            configuration.Configuration,
            candidate => candidate.Setting == setting);
        Assert.Equal(origin, decision.Origin);
        Assert.Equal(authority, decision.Authority);
    }
}
