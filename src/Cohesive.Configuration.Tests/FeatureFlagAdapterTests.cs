using Cohesive.Adapters.FeatureManagement;
using Cohesive.Adapters.LaunchDarkly;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace Cohesive.Configuration.Tests;

public sealed class FeatureFlagAdapterTests
{
    [Fact]
    public async Task Microsoft_missing_and_removed_flags_fail_admission_and_evaluation()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"FeatureManagement\":{\"reads\":false}}");
            using var config = (ConfigurationRoot)new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonFile(path).Build();
            var registrations = new ServiceCollection().AddSingleton<IConfiguration>(config);
            registrations.AddCohesiveFeatureManagement<string>();
            registrations.AddCohesiveFeatureManagement<string>();
            using var services = registrations.BuildServiceProvider();
            var evaluator = Assert.Single(services.GetServices<IFeatureFlagEvaluator<string>>());
            await evaluator.RequireFlagAsync("reads");
            Assert.False(await evaluator.IsEnabledAsync("reads", "a"));
            await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.RequireFlagAsync("typo").AsTask());
            await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.IsEnabledAsync("typo", "a").AsTask());
            await File.WriteAllTextAsync(path, "{}");
            config.Reload();
            await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.RequireFlagAsync("reads").AsTask());
            await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.IsEnabledAsync("reads", "a").AsTask());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LaunchDarkly_admission_accepts_disabled_flags_and_rejects_missing_flags_without_targeting()
    {
        var data = TestData.DataSource(); data.Update(data.Flag("reads").VariationForAll(false));
        using var client = new LdClient(LaunchDarkly.Sdk.Server.Configuration.Builder("test-key").DataSource(data).Events(Components.NoEvents).Build());
        var evaluator = new LaunchDarklyFeatureFlagEvaluator<string>(client, _ => throw new Exception("Admission must not project request context."));
        await evaluator.RequireFlagAsync("reads");
        await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.RequireFlagAsync("typo").AsTask());
    }

    [Fact]
    public async Task Microsoft_manager_observes_reload_without_recreating_evaluator()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FeatureManagement:reads"] = "false" }).Build();
        var registrations = new ServiceCollection().AddSingleton<IConfiguration>(config);
        registrations.AddFeatureManagement();
        using var services = registrations.BuildServiceProvider();
        var evaluator = new FeatureManagementEvaluator<string>(services.GetRequiredService<IVariantFeatureManager>(), services.GetRequiredService<IFeatureDefinitionProvider>());
        Assert.False(await evaluator.IsEnabledAsync("reads", "a"));
        config["FeatureManagement:reads"] = "true";
        config.Reload();
        Assert.True(await evaluator.IsEnabledAsync("reads", "a"));
        config["FeatureManagement:reads"] = "false";
        config.Reload();
        Assert.False(await evaluator.IsEnabledAsync("reads", "a"));
    }

    [Fact]
    public async Task Microsoft_contextual_filter_gets_each_invocations_context()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FeatureManagement:reads:EnabledFor:0:Name"] = "Tenant" }).Build();
        var registrations = new ServiceCollection().AddSingleton<IConfiguration>(config);
        registrations.AddFeatureManagement().AddFeatureFilter<TenantFilter>();
        using var services = registrations.BuildServiceProvider();
        var evaluator = new FeatureManagementEvaluator<string>(services.GetRequiredService<IVariantFeatureManager>(), services.GetRequiredService<IFeatureDefinitionProvider>());
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
            (await evaluator.IsEnabledAsync("reads", i % 2 == 0 ? "enabled" : "disabled")) == (i % 2 == 0)));
        Assert.All(results, Assert.True);
    }

    [FilterAlias("Tenant")]
    sealed class TenantFilter : IContextualFeatureFilter<string>
    {
        public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context, string appContext) => Task.FromResult(appContext == "enabled");
    }

    [Fact]
    public async Task LaunchDarkly_native_updates_and_targeting_are_live_on_one_client()
    {
        var data = TestData.DataSource();
        data.Update(data.Flag("reads").VariationForAll(false));
        using var client = new LdClient(LaunchDarkly.Sdk.Server.Configuration.Builder("test-key").DataSource(data).Events(Components.NoEvents).Build());
        var evaluator = new LaunchDarklyFeatureFlagEvaluator<string>(client, Context.New);
        Assert.False(await evaluator.IsEnabledAsync("reads", "a"));
        data.Update(data.Flag("reads").VariationForAll(true));
        Assert.True(await evaluator.IsEnabledAsync("reads", "a"));
        data.Update(data.Flag("reads").VariationForAll(false).VariationForUser("a", true));
        Assert.True(await evaluator.IsEnabledAsync("reads", "a"));
        Assert.False(await evaluator.IsEnabledAsync("reads", "b"));
    }

    [Fact]
    public async Task LaunchDarkly_missing_flag_does_not_silently_use_disabled_fallback()
    {
        using var client = new LdClient(LaunchDarkly.Sdk.Server.Configuration.Builder("test-key")
            .DataSource(TestData.DataSource()).Events(Components.NoEvents).Build());
        var evaluator = new LaunchDarklyFeatureFlagEvaluator<string>(client, Context.New);
        await Assert.ThrowsAsync<FeatureFlagNotFoundException>(() => evaluator.IsEnabledAsync("missing", "a").AsTask());
    }

    [Fact]
    public async Task Cancelled_evaluation_does_not_project_targeting_context()
    {
        using var client = new LdClient(LaunchDarkly.Sdk.Server.Configuration.Builder("test-key")
            .DataSource(TestData.DataSource()).Events(Components.NoEvents).Build());
        var evaluator = new LaunchDarklyFeatureFlagEvaluator<string>(client, _ => throw new Exception("must not evaluate"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluator.IsEnabledAsync("reads", "a", new(true)).AsTask());
    }
}
