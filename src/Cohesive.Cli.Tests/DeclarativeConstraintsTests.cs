using Cohesive.Cli.Testing;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli.Tests;

public sealed class DeclarativeConstraintsTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1", true)]
    [InlineData("10", true)]
    [InlineData("11", false)]
    public async Task TypedAndExplicitNumericRules_Agree(string raw, bool valid)
    {
        var typed = new CliApplication().WithoutEnvironmentVariables();
        var explicitApp = new CliApplication().WithoutEnvironmentVariables();
        var typedCalled = false;
        var explicitCalled = false;
        var command = typed.RootCommand<Options>();
        command.Map(options => options.Limit).WithCliName("count");
        command.RequirePositive(options => options.Limit).RequireRange(options => options.Limit, maximum: 10)
            .OnExecute(_ => { typedCalled = true; return 0; });
        explicitApp.RootCommand(new Dictionary<string, CliOption> { ["count"] = CliOption.For(defaultValue: 20) })
            .Constrain(CliConstraint.Positive("count"))
            .Constrain(CliConstraint.Range("count", maximum: 10))
            .OnExecute(_ => { explicitCalled = true; return 0; });
        var typedResult = await CliApplicationTestHarness.InvokeAsync(typed, ["--count", raw]);
        var explicitResult = await CliApplicationTestHarness.InvokeAsync(explicitApp, ["--count", raw]);
        Assert.Equal(valid ? 0 : 1, typedResult.ExitCode);
        Assert.Equal(typedResult.ExitCode, explicitResult.ExitCode);
        Assert.Equal(typedResult.ErrorOutput, explicitResult.ErrorOutput);
        Assert.Equal(valid, typedCalled);
        Assert.Equal(valid, explicitCalled);
        Assert.Equal(2, command.Constraints.Length);
        Assert.Equal(CliConstraint.Rule.Positive, command.Constraints[0].Kind);
    }

    [Fact]
    public async Task Constraints_ValidateDefaultsAndProviderValues_NotJustCliTokens()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var called = false;
        var root = app.RootCommand(new Dictionary<string, CliOption> { ["limit"] = CliOption.For(defaultValue: 0) })
            .Constrain(CliConstraint.Positive("limit"))
            .OnExecute(_ => { called = true; return 0; });
        var invalid = await CliApplicationTestHarness.InvokeAsync(app, []);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("must be positive", invalid.ErrorOutput);
        Assert.False(called);
        root.ConfigureConfiguration(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["limit"] = "5" }));
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
        Assert.True(called);
    }

    [Theory]
    [InlineData(new string[] {}, false)]
    [InlineData(new string[] { "--json" }, true)]
    [InlineData(new string[] { "--csv", "file" }, true)]
    [InlineData(new string[] { "--json", "--csv", "file" }, false)]
    public async Task SelectionRules_UseBooleanTruthAndNonemptyStrings(string[] args, bool valid)
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["json"] = CliOption.For(defaultValue: false), ["csv"] = CliOption.For<string>()
        }).Constrain(CliConstraint.ExactlyOne("json", "csv")).OnExecute(_ => 0);
        Assert.Equal(valid ? 0 : 1, (await CliApplicationTestHarness.InvokeAsync(app, args)).ExitCode);
    }

    [Theory]
    [InlineData(new string[] {}, true)]
    [InlineData(new string[] { "--upload" }, false)]
    [InlineData(new string[] { "--upload", "--token", "value" }, true)]
    [InlineData(new string[] { "--upload", "--offline", "--token", "value" }, false)]
    public async Task ConditionalRequirementsAndMutualExclusion_Compose(string[] args, bool valid)
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["upload"] = CliOption.For<bool>(), ["offline"] = CliOption.For<bool>(),
            ["token"] = CliOption.For<string>() with { Sensitive = true }
        }).Constrain(CliConstraint.Requires("upload", "token"))
            .Constrain(CliConstraint.AtMostOne("upload", "offline")).OnExecute(_ => 0);
        Assert.Equal(valid ? 0 : 1, (await CliApplicationTestHarness.InvokeAsync(app, args)).ExitCode);
    }

    [Fact]
    public async Task HelpUsesEffectiveNamesAndDeclaresRules()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var command = app.RootCommand<Options>();
        command.Map(options => options.Limit).WithCliName("count");
        command.RequirePositive(options => options.Limit).OnExecute(_ => 0);
        var help = await CliApplicationTestHarness.InvokeAsync(app, ["--help"]);
        Assert.Contains("'--count' must be positive.", help.StandardOutput);
        Assert.DoesNotContain("--limit", help.StandardOutput);
    }

    [Fact]
    public void UnsupportedDeclarations_FailBeforeInvocation()
    {
        var app = new CliApplication();
        var root = app.RootCommand(new Dictionary<string, CliOption>
        {
            ["text"] = CliOption.For<string>(), ["number"] = CliOption.For<double>(), ["day"] = CliOption.For<DayOfWeek>()
        });
        Assert.Throws<ArgumentException>(() => root.Constrain(CliConstraint.Positive("missing")));
        Assert.Throws<ArgumentException>(() => root.Constrain(CliConstraint.Positive("text")));
        Assert.Throws<ArgumentException>(() => root.Constrain(CliConstraint.Positive("number")));
        Assert.Throws<ArgumentException>(() => root.Constrain(CliConstraint.Positive("day")));
        Assert.Throws<ArgumentException>(() => CliConstraint.Range("number", minimum: 10, maximum: 1));
        Assert.Throws<ArgumentException>(() => CliConstraint.ExactlyOne("text"));
        Assert.Throws<ArgumentException>(() => CliConstraint.Requires("text", "TEXT"));
    }

    [Fact]
    public async Task OptionalNumbersAreSkippedAndDecimalBoundsAreInclusive()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption> { ["amount"] = CliOption.For<decimal?>() })
            .Constrain(CliConstraint.Range("amount", minimum: 0.5m, maximum: 1.5m)).OnExecute(_ => 0);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["--amount", "0.5"])).ExitCode);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["--amount", "1.5"])).ExitCode);
        Assert.Equal(1, (await CliApplicationTestHarness.InvokeAsync(app, ["--amount", "1.5001"])).ExitCode);
    }

    [Fact]
    public async Task ConstraintIdentity_SurvivesSubsequentConfigurationKeyOverrides()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var root = app.RootCommand<Options>().RequirePositive(options => options.Limit).OnExecute(_ => 0);
        root.Map(options => options.Limit).WithNameOverride("rows");
        Assert.Equal("rows", Assert.Single(Assert.Single(root.Constraints).Parameters));
        var failure = await CliApplicationTestHarness.InvokeAsync(app, ["--rows", "0"]);
        Assert.Equal(1, failure.ExitCode);
        Assert.Contains("'--rows' must be positive", failure.ErrorOutput);
    }

    sealed class Options
    {
        [ConfigurationParameter("limit")]
        public int Limit { get; init; } = 20;
    }
}
