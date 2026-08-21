using Cohesive.Infra.Configuration;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureConventionResolverTests
{
    [Fact]
    public void Resolution_uses_explicit_scoped_adapter_framework_precedence()
    {
        InfrastructureConfigurationSubject subject = new("environment/production");
        InfrastructureSettingId explicitSetting = new("explicit-wins");
        InfrastructureSettingId scopedSetting = new("scoped-wins");
        InfrastructureSettingId adapterSetting = new("adapter-wins");
        InfrastructureSettingId frameworkSetting = new("framework-wins");

        var resolution = InfrastructureConventionResolver.Resolve(
        [
            new(
                new("framework-defaults/v1"),
                [
                    Candidate(explicitSetting, "framework-explicit", EffectiveConfigurationOrigin.FrameworkDefault, "framework/v1"),
                    Candidate(scopedSetting, "framework-scoped", EffectiveConfigurationOrigin.FrameworkDefault, "framework/v1"),
                    Candidate(adapterSetting, "framework-adapter", EffectiveConfigurationOrigin.FrameworkDefault, "framework/v1"),
                    Candidate(frameworkSetting, "framework", EffectiveConfigurationOrigin.FrameworkDefault, "framework/v1")
                ]),
            new(
                new("adapter-defaults/v1"),
                [
                    Candidate(explicitSetting, "adapter-explicit", EffectiveConfigurationOrigin.AdapterConvention, "azure/v1"),
                    Candidate(scopedSetting, "adapter-scoped", EffectiveConfigurationOrigin.AdapterConvention, "azure/v1"),
                    Candidate(adapterSetting, "adapter", EffectiveConfigurationOrigin.AdapterConvention, "azure/v1")
                ]),
            new(
                new("production/v1"),
                [
                    Candidate(explicitSetting, "scoped-explicit", EffectiveConfigurationOrigin.ScopedProfile, "production/v1"),
                    Candidate(scopedSetting, "scoped", EffectiveConfigurationOrigin.ScopedProfile, "production/v1")
                ]),
            new(
                new("local-override/v1"),
                [Candidate(explicitSetting, "explicit", EffectiveConfigurationOrigin.Explicit, "definition/v7")])
        ]);

        Assert.True(resolution.IsValid);
        Assert.Empty(resolution.Diagnostics);
        Assert.Collection(
            resolution.Configuration,
            effective => AssertEffective(
                effective,
                adapterSetting,
                "adapter",
                EffectiveConfigurationOrigin.AdapterConvention,
                "azure/v1"),
            effective => AssertEffective(
                effective,
                explicitSetting,
                "explicit",
                EffectiveConfigurationOrigin.Explicit,
                "definition/v7"),
            effective => AssertEffective(
                effective,
                frameworkSetting,
                "framework",
                EffectiveConfigurationOrigin.FrameworkDefault,
                "framework/v1"),
            effective => AssertEffective(
                effective,
                scopedSetting,
                "scoped",
                EffectiveConfigurationOrigin.ScopedProfile,
                "production/v1"));

        InfrastructureConfigurationCandidate Candidate(
            InfrastructureSettingId setting,
            string value,
            EffectiveConfigurationOrigin origin,
            string authority) => new(subject, setting, value, origin, authority);

        static void AssertEffective(
            InfrastructureEffectiveConfiguration effective,
            InfrastructureSettingId setting,
            string value,
            EffectiveConfigurationOrigin origin,
            string authority)
        {
            Assert.Equal(setting, effective.Setting);
            Assert.Equal(value, effective.Value);
            Assert.Equal(origin, effective.Attribution.Origin);
            Assert.Equal(authority, effective.Attribution.Authority);
        }
    }

    [Fact]
    public void Equally_authoritative_conflicting_values_produce_a_structured_diagnostic()
    {
        InfrastructureConfigurationSubject subject = new("environment/production");
        InfrastructureSettingId setting = new("region");
        var east = new InfrastructureConventionProfile(
            new("production-east/v1"),
            [new(subject, setting, "eastus", EffectiveConfigurationOrigin.ScopedProfile, "production-east/v1")]);
        var west = new InfrastructureConventionProfile(
            new("production-west/v1"),
            [new(subject, setting, "westus", EffectiveConfigurationOrigin.ScopedProfile, "production-west/v1")]);

        var resolution = InfrastructureConventionResolver.Resolve([west, east]);

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Configuration);
        var diagnostic = Assert.Single(resolution.Diagnostics);
        Assert.Equal(InfrastructureConventionResolver.DiagnosticCodes.AmbiguousEffectiveValue, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/configuration/environment/production/region", diagnostic.Location);
        Assert.Equal("environment/production/region", diagnostic.SchemaLocation);
        Assert.Contains("production-east/v1", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("production-west/v1", diagnostic.Message, StringComparison.Ordinal);
        var evidence = Assert.IsType<Cohesive.Model.Serialization.DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal("infrastructure-convention-resolution", evidence.Stage);
        Assert.Equal("environment/production/region", evidence.Subject);
        Assert.Equal(
            ["production-east/v1", "production-west/v1"],
            evidence.SourceReferences.ToArray());
        Assert.Equal("one canonical value at authority tier 'ScopedProfile'", evidence.Expected);
        Assert.Equal("2 different canonical values from 2 authorities", evidence.Observed);
        Assert.Equal(2, evidence.ResolutionOptions.Length);
        var publicText = string.Join(
            "|",
            new[] { diagnostic.Message, evidence.Expected, evidence.Observed }
                .Where(static value => value is not null)
                .Concat(evidence.ResolutionOptions));
        Assert.DoesNotContain("eastus", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("westus", publicText, StringComparison.Ordinal);
    }

    [Fact]
    public void Equally_authoritative_equal_values_converge_with_deterministic_attribution()
    {
        InfrastructureConfigurationSubject subject = new("environment/production");
        InfrastructureSettingId setting = new("region");
        var later = new InfrastructureConventionProfile(
            new("profile/z"),
            [new(subject, setting, "eastus", EffectiveConfigurationOrigin.ScopedProfile, "authority/z")]);
        var earlier = new InfrastructureConventionProfile(
            new("profile/a"),
            [new(subject, setting, "eastus", EffectiveConfigurationOrigin.ScopedProfile, "authority/a")]);

        var resolution = InfrastructureConventionResolver.Resolve([later, earlier]);

        Assert.True(resolution.IsValid);
        Assert.Empty(resolution.Diagnostics);
        var effective = Assert.Single(resolution.Configuration);
        Assert.Equal("eastus", effective.Value);
        Assert.Equal("authority/a", effective.Attribution.Authority);
    }
}
