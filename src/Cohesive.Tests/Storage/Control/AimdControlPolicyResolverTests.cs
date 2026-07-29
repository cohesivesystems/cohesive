using Cohesive.Control;
using Cohesive.Model;

namespace Cohesive.Tests.Storage.Control;

public sealed class AimdControlPolicyResolverTests
{
    [Fact]
    public void Resolve_WithNoLayers_UsesStableAttributableFrameworkDefaults()
    {
        var first = AimdControlPolicyResolver.Resolve(ControlActuatorKind.Concurrency);
        var second = AimdControlPolicyResolver.Resolve(ControlActuatorKind.Concurrency);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(new ControlQuantity(1, ControlUnit.Count), first.AdditiveIncrease);
        Assert.Equal(5_000, first.MultiplicativeDecreaseBasisPoints);
        Assert.Equal(5, first.HealthyObservationCount);
        Assert.Equal(30_000, first.RecoveryCooldownMilliseconds);
        Assert.Equal(5_000, first.MinimumDwellMilliseconds);
        Assert.Equal(60_000, first.MaximumObservationAgeMilliseconds);
        Assert.Equal(1, first.MinimumSampleCount);
        Assert.Equal(
            [
                AimdControlPolicySettingsNames.AdditiveIncrease,
                AimdControlPolicySettingsNames.HealthyObservationCount,
                AimdControlPolicySettingsNames.MaximumObservationAge,
                AimdControlPolicySettingsNames.MinimumDwell,
                AimdControlPolicySettingsNames.MinimumSampleCount,
                AimdControlPolicySettingsNames.MultiplicativeDecrease,
                AimdControlPolicySettingsNames.RecoveryCooldown
            ],
            first.Configuration.Select(static decision => decision.Setting));
        Assert.All(first.Configuration, decision =>
        {
            Assert.Equal(EffectiveConfigurationOrigin.FrameworkDefault, decision.Origin);
            Assert.Equal(AimdControlPolicyResolver.FrameworkConventionAuthority, decision.Authority);
        });
    }

    [Fact]
    public void Resolve_AppliesPrecedenceIndependentlyForEverySetting()
    {
        var adapter = Layer(
            EffectiveConfigurationOrigin.AdapterConvention,
            "elastic/control-v8",
            new(
                additiveIncrease: 2,
                multiplicativeDecreaseBasisPoints: 7_500,
                recoveryCooldownMilliseconds: 20_000));
        var scoped = Layer(
            EffectiveConfigurationOrigin.ScopedProfile,
            "indexing/profile-v3",
            new(
                additiveIncrease: 3,
                healthyObservationCount: 4,
                minimumSampleCount: 8));
        var explicitLayer = Layer(
            EffectiveConfigurationOrigin.Explicit,
            "indexing/write-loop",
            new(
                additiveIncrease: 7,
                maximumObservationAgeMilliseconds: 90_000));

        var policy = AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.Concurrency,
            adapter,
            scoped,
            explicitLayer);

        Assert.Equal(7, policy.AdditiveIncrease.Value);
        Assert.Equal(7_500, policy.MultiplicativeDecreaseBasisPoints);
        Assert.Equal(4, policy.HealthyObservationCount);
        Assert.Equal(20_000, policy.RecoveryCooldownMilliseconds);
        Assert.Equal(5_000, policy.MinimumDwellMilliseconds);
        Assert.Equal(90_000, policy.MaximumObservationAgeMilliseconds);
        Assert.Equal(8, policy.MinimumSampleCount);

        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.AdditiveIncrease,
            EffectiveConfigurationOrigin.Explicit,
            "indexing/write-loop");
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.MultiplicativeDecrease,
            EffectiveConfigurationOrigin.AdapterConvention,
            "elastic/control-v8");
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.HealthyObservationCount,
            EffectiveConfigurationOrigin.ScopedProfile,
            "indexing/profile-v3");
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.RecoveryCooldown,
            EffectiveConfigurationOrigin.AdapterConvention,
            "elastic/control-v8");
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.MinimumDwell,
            EffectiveConfigurationOrigin.FrameworkDefault,
            AimdControlPolicyResolver.FrameworkConventionAuthority);
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.MaximumObservationAge,
            EffectiveConfigurationOrigin.Explicit,
            "indexing/write-loop");
        AssertDecision(
            policy,
            AimdControlPolicySettingsNames.MinimumSampleCount,
            EffectiveConfigurationOrigin.ScopedProfile,
            "indexing/profile-v3");
    }

    [Fact]
    public void Resolve_IsIndependentOfLayerInputOrder()
    {
        var adapter = Layer(
            EffectiveConfigurationOrigin.AdapterConvention,
            "adapter/v1",
            new(additiveIncrease: 2, minimumDwellMilliseconds: 1_000));
        var scoped = Layer(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/v1",
            new(additiveIncrease: 4, healthyObservationCount: 2));
        var explicitLayer = Layer(
            EffectiveConfigurationOrigin.Explicit,
            "local/v1",
            new(additiveIncrease: 8, minimumSampleCount: 10));

        var forward = AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.BatchItems,
            adapter,
            scoped,
            explicitLayer);
        var reverse = AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.BatchItems,
            explicitLayer,
            scoped,
            adapter);

        Assert.Equal(forward, reverse);
        Assert.Equal(forward.GetHashCode(), reverse.GetHashCode());
        Assert.Equal(ControlUnit.Count, forward.AdditiveIncrease.Unit);
    }

    [Fact]
    public void Resolve_RejectsAmbiguousDuplicatePrecedenceTiers()
    {
        var first = Layer(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/one",
            new(additiveIncrease: 2));
        var second = Layer(
            EffectiveConfigurationOrigin.ScopedProfile,
            "profile/two",
            new(minimumSampleCount: 2));

        Assert.Throws<ArgumentException>(() => AimdControlPolicyResolver.Resolve(
            ControlActuatorKind.Concurrency,
            first,
            second));
        Assert.Throws<ArgumentException>(() => new AimdControlPolicyLayer(
            EffectiveConfigurationOrigin.AdapterConvention,
            "adapter/v1",
            new AimdControlPolicySettings()));
    }

    static AimdControlPolicyLayer Layer(
        EffectiveConfigurationOrigin origin,
        string authority,
        AimdControlPolicySettings settings) =>
        new(origin, authority, settings);

    static void AssertDecision(
        AimdControlPolicy policy,
        string setting,
        EffectiveConfigurationOrigin expectedOrigin,
        string expectedAuthority)
    {
        var decision = Assert.Single(policy.Configuration, decision => decision.Setting == setting);
        Assert.Equal(expectedOrigin, decision.Origin);
        Assert.Equal(expectedAuthority, decision.Authority);
    }
}
