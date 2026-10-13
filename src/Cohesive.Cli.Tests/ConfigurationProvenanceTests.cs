using Cohesive.Cli.Testing;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli.Tests;

public sealed class ConfigurationProvenanceTests
{
    [Fact]
    public async Task ExplicitProvenance_TracksPrecedenceAndMappedEnvironmentPerInvocation()
    {
        var variable = $"COHESIVE_PROVENANCE_{Guid.NewGuid():N}";
        var app = new CliApplication().WithConfiguration(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["limit"] = "30" }));
        CliParameterProvenance? captured = null;
        var command = app.RootCommand(new Dictionary<string, CliOption>
        {
            ["limit"] = CliOption.For(defaultValue: 20, environmentVariable: variable)
        }).OnExecute(context => { captured = Assert.Single(context.ConfigurationProvenance); return 0; });
        try
        {
            await Invoke([]);
            Assert.Equal(CliConfigurationSourceKind.ApplicationConfiguration, Assert.Single(captured!.Origins).Kind);
            command.ConfigureConfiguration(builder => builder.AddInMemoryCollection(
                new Dictionary<string, string?> { ["limit"] = "35" }));
            await Invoke([]);
            Assert.Equal(CliConfigurationSourceKind.CommandConfiguration, Assert.Single(captured!.Origins).Kind);
            Environment.SetEnvironmentVariable(variable, "40");
            await Invoke([]);
            Assert.Equal("40", captured!.Value);
            Assert.Equal(variable, Assert.Single(captured.Origins).Source);
            Assert.Equal(CliConfigurationSourceKind.Environment, captured.Origins[0].Kind);
            await Invoke(["--limit", "050"]);
            Assert.Equal("50", captured!.Value);
            Assert.Equal(CliConfigurationSourceKind.CommandLine, Assert.Single(captured.Origins).Kind);
            app.WithoutEnvironmentVariables();
            await Invoke([]);
            Assert.Equal("35", captured!.Value);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }

        async Task Invoke(string[] args) => Assert.Equal(0,
            (await CliApplicationTestHarness.InvokeAsync(app, args)).ExitCode);
    }

    [Fact]
    public async Task Collections_ReportWinningSourceForEachConsumedElement()
    {
        var app = new CliApplication().WithoutEnvironmentVariables().WithConfiguration(builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["fields:0"] = "open", ["fields:1"] = "close"
            }));
        app.RootCommand(new Dictionary<string, CliOption> { ["fields"] = CliOption.For<string[]>() })
            .OnExecute(context =>
            {
                var provenance = Assert.Single(context.ConfigurationProvenance);
                Assert.Equal("[\"high\",\"close\"]", provenance.Value);
                Assert.Equal(2, provenance.Origins.Length);
                Assert.Equal("fields:0", provenance.Origins[0].Key);
                Assert.Equal(CliConfigurationSourceKind.CommandLine, provenance.Origins[0].Kind);
                Assert.Equal(CliConfigurationSourceKind.ApplicationConfiguration, provenance.Origins[1].Kind);
                return 0;
            });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["--fields", "high"])).ExitCode);
    }

    [Fact]
    public async Task TypedFallbackAndDeclarationDefault_AreDistinguished()
    {
        var typed = new CliApplication().WithoutEnvironmentVariables();
        typed.RootCommand<TypedOptions>().OnExecute(context =>
        {
            var limit = context.ConfigurationProvenance.Single(parameter => parameter.ConfigurationKey == "limit");
            Assert.Equal("20", limit.Value);
            Assert.Equal(CliConfigurationSourceKind.ClrDefault, Assert.Single(limit.Origins).Kind);
            var copy = new DerivedContext(context);
            Assert.Equal(context.ConfigurationProvenance, copy.ConfigurationProvenance);
            return 0;
        });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(typed, ["--limit", " "])).ExitCode);
        var explicitApp = new CliApplication().WithoutEnvironmentVariables();
        explicitApp.RootCommand(new Dictionary<string, CliOption> { ["limit"] = CliOption.For(defaultValue: 20) })
            .OnExecute(context =>
            {
                Assert.Equal(CliConfigurationSourceKind.DeclarationDefault,
                    Assert.Single(Assert.Single(context.ConfigurationProvenance).Origins).Kind);
                return 0;
            });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(explicitApp, [])).ExitCode);
    }

    [Fact]
    public async Task Explanation_RedactsSecretsAndBypassesApplicationExecution()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var called = false;
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["token"] = CliOption.For(defaultValue: "super-secret") with { Sensitive = true },
            ["limit"] = CliOption.For(defaultValue: 0)
        }).WithConfigurationExplanation()
            .Constrain(CliConstraint.Positive("limit"))
            .Use((_, _) => { called = true; throw new InvalidOperationException(); })
            .Validate(_ => { called = true; return new[] { "validator" }; })
            .OnExecute(_ => { called = true; return 0; });
        var result = await CliApplicationTestHarness.InvokeAsync(app, ["--explain-configuration"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--limit = 0", result.StandardOutput);
        Assert.Contains("<redacted>", result.StandardOutput);
        Assert.Contains("DeclarationDefault", result.StandardOutput);
        Assert.DoesNotContain("super-secret", result.StandardOutput + result.ErrorOutput);
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensitiveAllowedValues_DoNotLeakInHelpOrFailures(bool positional)
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var command = app.RootCommand(new Dictionary<string, CliOption>
        {
            ["token"] = CliOption.For<string>() with { Sensitive = true, AllowedValues = ["allowed-secret"] }
        }).OnExecute(_ => 0);
        if (positional) command.Argument("token");
        var help = await CliApplicationTestHarness.InvokeAsync(app, ["--help"]);
        Assert.DoesNotContain("allowed-secret", help.StandardOutput);
        var failure = await CliApplicationTestHarness.InvokeAsync(app,
            positional ? ["invalid-secret"] : ["--token", "invalid-secret"]);
        Assert.Equal(1, failure.ExitCode);
        Assert.DoesNotContain("invalid-secret", failure.ErrorOutput);
        Assert.DoesNotContain("allowed-secret", failure.ErrorOutput);
        Assert.Contains("<redacted>", failure.ErrorOutput);
    }

    [Fact]
    public async Task TypedSensitivity_ProjectsAttributesAndFluentOverrides()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var command = app.RootCommand<TypedOptions>().WithConfigurationExplanation();
        command.Map(options => options.Limit).AsSensitive();
        command.OnExecute(_ => 0);
        Assert.True(command.Parameters.Single(parameter => parameter.ConfigurationKey == "limit").Sensitive);
        var explanation = await CliApplicationTestHarness.InvokeAsync(app, ["--explain-configuration"]);
        Assert.DoesNotContain("typed-secret", explanation.StandardOutput);
        Assert.Contains("--limit = <redacted>", explanation.StandardOutput);
    }

    [Fact]
    public async Task SensitiveBoolean_InvalidRawValueIsNotEchoed()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["flag"] = CliOption.For<bool>() with { Sensitive = true }
        }).OnExecute(_ => 0);
        var result = await CliApplicationTestHarness.InvokeAsync(app, ["--flag", "invalid-secret"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("invalid-secret", result.ErrorOutput);
    }

    [Fact]
    public async Task Explanation_CanRunBeforeAHandlerIsAttached()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption> { ["limit"] = CliOption.For(defaultValue: 20) })
            .WithConfigurationExplanation();
        var result = await CliApplicationTestHarness.InvokeAsync(app, ["--explain-configuration"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--limit = 20", result.StandardOutput);
    }

    [Theory]
    [InlineData(new string[] {}, true)]
    [InlineData(new string[] { "--flag" }, true)]
    [InlineData(new string[] { "--flag", "false" }, false)]
    public async Task SensitiveBooleans_PreserveNormalSwitchAndDefaultSemantics(string[] args, bool expected)
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["flag"] = CliOption.For(defaultValue: true) with { Sensitive = true }
        }).OnExecute(context => { Assert.Equal(expected, context.Configuration.Get<bool>("flag")); return 0; });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, args)).ExitCode);
    }

    [Fact]
    public async Task AbsentOptionalValue_IsNotAttributedToADeclarationDefault()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption> { ["limit"] = CliOption.For<int>() })
            .OnExecute(context =>
            {
                var explanation = Assert.Single(context.ConfigurationProvenance);
                Assert.Null(explanation.Value);
                Assert.Equal(CliConfigurationSourceKind.Absent, Assert.Single(explanation.Origins).Kind);
                return 0;
            });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
    }

    [Fact]
    public async Task CollectionFallback_DoesNotClaimIgnoredScalarOrEmptyChildSources()
    {
        var app = new CliApplication().WithoutEnvironmentVariables().WithConfiguration(builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["fields"] = "ignored", ["fields:0"] = " "
            }));
        app.RootCommand<TypedOptions>().OnExecute(context =>
        {
            var provenance = context.ConfigurationProvenance.Single(parameter => parameter.ConfigurationKey == "fields");
            Assert.Equal("""["initialized"]""", provenance.Value);
            Assert.Equal(CliConfigurationSourceKind.ClrDefault, Assert.Single(provenance.Origins).Kind);
            return 0;
        });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
    }

    sealed class TypedOptions
    {
        [ConfigurationParameter("limit")]
        public int Limit { get; init; } = 20;
        [ConfigurationParameter("token", Sensitive = true)]
        public string Token { get; init; } = "typed-secret";
        [ConfigurationParameter("fields")]
        public string[] Fields { get; init; } = ["initialized"];
    }

    sealed class DerivedContext(CliCommandContext<TypedOptions> source) : CliCommandContext<TypedOptions>(source);
}
