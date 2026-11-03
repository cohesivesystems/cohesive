using Cohesive.Application.Cli;
using Cohesive.Cli;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests;

public sealed class MaterializationCliTests
{
    [Theory]
    [InlineData("initialize")]
    [InlineData("rebuild")]
    [InlineData("catch-up")]
    public async Task Help_does_not_prepare_native_resources(string command)
    {
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddMaterializationShadowCommands("test",
            _ => throw new Exception("unexpected initialization"), (_, _) => throw new Exception("unexpected binding"));
        Assert.Equal(0, await cli.InvokeAsync([command, "--help"], CommandIo.Null()));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("2026-10-27T10:00:00")]
    public async Task Invalid_start_is_rejected_before_native_binding(string started)
    {
        var calls = 0;
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddMaterializationShadowCommands("test",
            _ => Task.CompletedTask, (_, _) => { calls++; throw new Exception("unexpected binding"); });
        Assert.NotEqual(0, await cli.InvokeAsync(["rebuild", "--attempt", "a", "--started", started, "--invocation", "i"], CommandIo.Null()));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("rebuild", MaterializationShadowOperation.Rebuild)]
    [InlineData("catch-up", MaterializationShadowOperation.CatchUp)]
    public async Task Retry_receives_identical_attempt_and_activation(string command, MaterializationShadowOperation operation)
    {
        var invocations = new List<MaterializationShadowInvocation>();
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddMaterializationShadowCommands("test",
            _ => throw new Exception("unexpected initialization"), (invocation, _) =>
            {
                invocations.Add(invocation);
                throw new InvalidOperationException("Simulated failure before execution");
            });
        string[] args = [command, "--attempt", "a", "--started", "2026-10-27T10:00:00Z", "--invocation", "i"];
        Assert.NotEqual(0, await cli.InvokeAsync(args, CommandIo.Null()));
        Assert.NotEqual(0, await cli.InvokeAsync(args, CommandIo.Null()));
        Assert.Equal(2, invocations.Count);
        Assert.Equal(operation, invocations[0].Operation);
        Assert.Equal(invocations[0], invocations[1]);
    }

    [Fact]
    public async Task Incomplete_catch_up_reports_generation_and_non_success()
    {
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddMaterializationShadowCommands("test",
            _ => Task.CompletedTask, (_, _) => Task.FromResult(new MaterializationSynchronizationRunResult(
                MaterializationSynchronizationRunDisposition.WorkRemaining, new("generation/test"), [], null)));
        using var output = new MemoryStream();
        using var error = new StringWriter();
        Assert.Equal(2, await cli.InvokeAsync(["catch-up", "--attempt", "a", "--started", "2026-10-27T10:00:00Z", "--invocation", "i"],
            new CommandIo(Stream.Null, output, error)));
        using var json = System.Text.Json.JsonDocument.Parse(output.ToArray());
        Assert.Equal("generation/test", json.RootElement.GetProperty("generation").GetString());
        Assert.Equal("disabled", json.RootElement.GetProperty("promotion").GetString());
    }

    [Fact]
    public async Task Initialize_is_explicit_and_does_not_execute()
    {
        var calls = 0;
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddMaterializationShadowCommands("test",
            _ => { calls++; return Task.CompletedTask; }, (_, _) => throw new Exception("unexpected execution"));
        Assert.Equal(0, await cli.InvokeAsync(["initialize"], CommandIo.Null()));
        Assert.Equal(1, calls);
        Assert.NotEqual(0, await cli.InvokeAsync(["promote"], CommandIo.Null()));
        Assert.Equal(1, calls);
    }
}
