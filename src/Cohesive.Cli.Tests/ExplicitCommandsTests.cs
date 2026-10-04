using Cohesive.Cli.Testing;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli.Tests;

public sealed class ExplicitCommandsTests
{
    [Fact]
    public async Task RootDefaults_RunWithoutArguments_AndHelpDoesNotExecute()
    {
        var calls = 0;
        var app = new CliApplication("Historical bars").WithoutEnvironmentVariables();
        var root = app.RootCommand(new Dictionary<string, CliOption>
        {
            ["instrument"] = CliOption.For(defaultValue: "AMD.XNAS", description: "Instrument identifier"),
            ["limit"] = CliOption.For(defaultValue: 20)
        });
        root.OnExecute(context =>
        {
            calls++;
            Assert.Equal("AMD.XNAS", context.Configuration.Get<string>("instrument"));
            Assert.Equal(20, context.Configuration.Get<int>("limit"));
            return 0;
        });
        Assert.True(app.ShouldHandle([]));
        Assert.Equal(0, await app.RunAsync([]));
        var help = await CliApplicationTestHarness.InvokeAsync(app, ["--help"]);
        Assert.Contains("Historical bars", help.StandardOutput);
        Assert.Contains("--instrument", help.StandardOutput);
        Assert.Contains("Instrument identifier", help.StandardOutput);
        Assert.Equal(1, calls);
        Assert.Equal(typeof(int), root.Parameters.Single(parameter => parameter.ConfigurationKey == "limit").ParameterType);
    }

    [Fact]
    public async Task Precedence_DefaultsProvidersEnvironmentCli_AndEnvironmentIsReadPerInvocation()
    {
        var variable = $"COHESIVE_EXPLICIT_{Guid.NewGuid():N}";
        var app = new CliApplication().WithConfiguration(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["limit"] = "30" }));
        var captured = 0;
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["limit"] = CliOption.For(defaultValue: 20, environmentVariable: variable)
        }).OnExecute(context => { captured = context.Configuration.Get<int>("limit"); return 0; });
        try
        {
            Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
            Assert.Equal(30, captured);
            Environment.SetEnvironmentVariable(variable, "40");
            Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
            Assert.Equal(40, captured);
            Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["--limit", "50"])).ExitCode);
            Assert.Equal(50, captured);
            app.WithoutEnvironmentVariables();
            Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
            Assert.Equal(30, captured);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("--limit", "bad")]
    [InlineData("--instrument", "unsupported")]
    public async Task InvalidValues_DoNotExecute(string option, string value)
    {
        var called = false;
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["limit"] = CliOption.For(defaultValue: 20),
            ["instrument"] = CliOption.For(defaultValue: "AMD") with { AllowedValues = ["AMD", "NVDA"] }
        }).OnExecute(_ => { called = true; return 0; });
        var result = await CliApplicationTestHarness.InvokeAsync(app, [option, value]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.ErrorOutput);
        Assert.False(called);
    }

    [Fact]
    public async Task RequiredValue_CanComeFromConfiguration_AndMissingValueFails()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var called = false;
        var root = app.RootCommand(new Dictionary<string, CliOption>
        {
            ["instrument"] = CliOption.For<string>() with { Required = true }
        }).OnExecute(_ => { called = true; return 0; });
        var missing = await CliApplicationTestHarness.InvokeAsync(app, []);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Missing required", missing.ErrorOutput);
        Assert.False(called);
        root.ConfigureConfiguration(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["instrument"] = "AMD" }));
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
        Assert.True(called);
    }

    [Theory]
    [InlineData(new string[] {}, true)]
    [InlineData(new string[] { "--json" }, true)]
    [InlineData(new string[] { "--json", "false" }, false)]
    public async Task BooleanOptions_PreserveDefaultsAndAcceptExplicitFalse(string[] args, bool expected)
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption> { ["json"] = CliOption.For(defaultValue: true) })
            .OnExecute(context => { Assert.Equal(expected, context.Configuration.Get<bool>("json")); return 0; });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, args)).ExitCode);
    }

    [Fact]
    public async Task TypedAndExplicitRoots_UseEquivalentConversionAndValidation()
    {
        var typed = new CliApplication().WithoutEnvironmentVariables();
        var explicitApp = new CliApplication().WithoutEnvironmentVariables();
        EquivalentOptions? captured = null;
        typed.RootCommand<EquivalentOptions>().OnExecute(context => { captured = context.Configuration; return 0; });
        explicitApp.RootCommand(new Dictionary<string, CliOption>
        {
            ["limit"] = CliOption.For(defaultValue: 20),
            ["duration"] = CliOption.For(defaultValue: TimeSpan.FromSeconds(5)),
            ["mode"] = CliOption.For(defaultValue: DayOfWeek.Monday)
        }).OnExecute(context =>
        {
            Assert.Equal(captured!.Limit, context.Configuration.Get<int>("limit"));
            Assert.Equal(captured.Duration, context.Configuration.Get<TimeSpan>("duration"));
            Assert.Equal(captured.Mode, context.Configuration.Get<DayOfWeek>("mode"));
            return 0;
        });
        string[] args = ["--limit", "42", "--duration", "00:00:12", "--mode", "Friday"];
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(typed, args)).ExitCode);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(explicitApp, args)).ExitCode);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(typed, [])).ExitCode);
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(explicitApp, [])).ExitCode);
        var typedError = await CliApplicationTestHarness.InvokeAsync(typed, ["--limit", "bad"]);
        var explicitError = await CliApplicationTestHarness.InvokeAsync(explicitApp, ["--limit", "bad"]);
        Assert.Equal(typedError.ExitCode, explicitError.ExitCode);
        Assert.Contains("could not be parsed as 'Int32'", explicitError.ErrorOutput);
    }

    [Fact]
    public async Task NamedCommandAndExplicitSubcommand_PositionalCollectionsAndCheckedAccess()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        var parent = app.Command<EquivalentOptions>("tools");
        var command = parent.SubCommand("bars", new Dictionary<string, CliOption>
        {
            ["instrument"] = CliOption.For<string>() with { Required = true },
            ["fields"] = CliOption.For<string[]>()
        });
        command.Argument("instrument");
        command.OnExecute(context =>
        {
            Assert.Equal("AMD", context.Configuration.Get<string>("instrument"));
            Assert.Equal(["open", "close"], context.Configuration.Get<string[]>("fields")!);
            Assert.Throws<KeyNotFoundException>(() => context.Configuration.Get<int>("missing"));
            Assert.Throws<InvalidOperationException>(() => context.Configuration.Get<int>("instrument"));
            return 0;
        });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app,
            ["tools", "bars", "AMD", "--fields", "open", "close"])).ExitCode);
    }

    [Fact]
    public async Task DeclarationsAndDefaults_AreSnapshotted_AndCollectionsAreDefensivelyRead()
    {
        string[] defaults = ["open"];
        var options = new Dictionary<string, CliOption> { ["fields"] = CliOption.For(defaultValue: defaults) };
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(options).OnExecute(context =>
        {
            var fields = context.Configuration.Get<string[]>("fields")!;
            Assert.Equal("open", fields[0]);
            fields[0] = "changed";
            Assert.Equal("open", context.Configuration.Get<string[]>("fields")![0]);
            return 0;
        });
        defaults[0] = "changed";
        options.Clear();
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, [])).ExitCode);
    }

    [Fact]
    public async Task CollectionDefault_IsReplacedAsAWholeByExplicitInput()
    {
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.RootCommand(new Dictionary<string, CliOption>
        {
            ["fields"] = CliOption.For(defaultValue: new string[] { "open", "high", "low" })
        }).OnExecute(context =>
        {
            Assert.Equal(["close"], context.Configuration.Get<string[]>("fields")!);
            return 0;
        });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["--fields", "close"])).ExitCode);
    }

    [Fact]
    public async Task ExplicitNamedCommands_CoexistWithTypedRootAndSharedMiddleware()
    {
        var calls = 0;
        var app = new CliApplication().WithoutEnvironmentVariables();
        app.Use<CliValues>(async (context, next) => { calls++; return await next(context); });
        app.RootCommand<EquivalentOptions>().OnExecute(_ => 0);
        app.Command("root", new Dictionary<string, CliOption> { ["limit"] = CliOption.For(defaultValue: 20) })
            .Validate(values => values.Get<int>("limit") > 0 ? Array.Empty<string>() : ["Limit must be positive."])
            .OnExecute(context => { Assert.Equal(42, context.Configuration.Get<int>("limit")); return 0; });
        Assert.Equal(0, (await CliApplicationTestHarness.InvokeAsync(app, ["root", "--limit", "42"])).ExitCode);
        var invalid = await CliApplicationTestHarness.InvokeAsync(app, ["root", "--limit", "0"]);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("Limit must be positive.", invalid.ErrorOutput);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidDeclarationsAndDuplicateRoots_FailAtRegistration()
    {
        var app = new CliApplication();
        Assert.Throws<ArgumentException>(() => app.RootCommand(new Dictionary<string, CliOption>
            { ["--limit"] = CliOption.For<int>() }));
        Assert.Throws<ArgumentException>(() => app.RootCommand(new Dictionary<string, CliOption>
            { ["limit"] = CliOption.For<int>(), ["LIMIT"] = CliOption.For<int>() }));
        var root = app.RootCommand(new Dictionary<string, CliOption>());
        Assert.Throws<InvalidOperationException>(() => app.RootCommand<EquivalentOptions>());
        Assert.Throws<InvalidOperationException>(() => root.ConfigureParameters(_ => { }));
    }

    sealed class EquivalentOptions
    {
        public int Limit { get; init; } = 20;
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
        public DayOfWeek Mode { get; init; } = DayOfWeek.Monday;
    }
}
