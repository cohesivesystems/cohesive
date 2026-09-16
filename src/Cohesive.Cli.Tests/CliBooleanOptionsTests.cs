using Cohesive.Cli.Testing;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli.Tests;

public sealed class CliBooleanOptionsTests
{
    [Theory]
    [InlineData(new[] { "show", "--json", "input" }, "True:")]
    [InlineData(new[] { "show", "input", "--json", "false" }, "False:")]
    [InlineData(new[] { "show", "input", "--optional" }, "False:True")]
    [InlineData(new[] { "show", "input" }, "False:")]
    public async Task SwitchesBindWithoutConsumingFollowingPositionals(string[] args, string expected)
    {
        var app = Create();
        var result = await CliApplicationTestHarness.InvokeAsync(app, args);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + Environment.NewLine, result.StandardOutput);
    }

    [Fact]
    public async Task OmittedSwitchPreservesConfiguredValueAndExplicitFalseOverridesIt()
    {
        var app = Create().WithConfiguration(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Json"] = "true" }));
        var omitted = await CliApplicationTestHarness.InvokeAsync(app, ["show", "input"]);
        Assert.Equal("True:" + Environment.NewLine, omitted.StandardOutput);
        var explicitFalse = await CliApplicationTestHarness.InvokeAsync(app, ["show", "input", "--json", "false"]);
        Assert.Equal("False:" + Environment.NewLine, explicitFalse.StandardOutput);
        Assert.Equal(0, explicitFalse.ExitCode);
    }

    [Fact]
    public async Task EnvironmentBindingCanBeDisabledAndReenabled()
    {
        var prefix = "COHESIVE_CLI_TEST_" + Guid.NewGuid().ToString("N") + "_";
        Environment.SetEnvironmentVariable(prefix + "Json", "true");
        try
        {
            var app = Create().WithEnvironmentVariablePrefix(prefix).WithoutEnvironmentVariables();
            var disabled = await CliApplicationTestHarness.InvokeAsync(app, ["show", "input"]);
            Assert.Equal("False:" + Environment.NewLine, disabled.StandardOutput);
            app.WithEnvironmentVariablePrefix(prefix);
            var enabled = await CliApplicationTestHarness.InvokeAsync(app, ["show", "input"]);
            Assert.Equal("True:" + Environment.NewLine, enabled.StandardOutput);
        }
        finally { Environment.SetEnvironmentVariable(prefix + "Json", null); }
    }

    static CliApplication Create()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var command = app.Command<Options>("show").OnExecute(context =>
        {
            Assert.Equal("input", context.Configuration.Input);
            context.Io.WriteLine($"{context.Configuration.Json}:{context.Configuration.Optional}");
            return 0;
        });
        command.Argument(options => options.Input);
        return app;
    }

    sealed record Options
    {
        [ConfigurationParameter(Required = true)]
        public string Input { get; init; } = "";
        public bool Json { get; init; }
        public bool? Optional { get; init; }
    }
}
