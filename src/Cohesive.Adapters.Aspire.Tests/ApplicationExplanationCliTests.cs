using System.Text;
using Aspire.Hosting;
using Cohesive.Application;
using Cohesive.Cli;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ApplicationExplanationCliTests
{
    static ApplicationExplanationDocument Report()
    {
        var model = TestModels.Infrastructure();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return model.Explain(model.Configure(builder));
    }

    [Fact]
    public async Task Explain_passes_native_arguments_and_writes_complete_typed_report()
    {
        var report = Report();
        var calls = 0;
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddApplicationExplanation((args, token) =>
        {
            calls++;
            Assert.Equal(["--EntityState:Authority=Events", "--environment", "dev"], args);
            Assert.False(token.IsCancellationRequested);
            return report;
        });
        using var output = new MemoryStream();
        using var error = new StringWriter();
        var io = new CommandIo(Stream.Null, output, error);
        Assert.Equal(0, await cli.InvokeAsync(["explain", "--", "--EntityState:Authority=Events", "--environment", "dev"], io));
        Assert.Equal(1, calls);
        Assert.Equal(report.ToJson().Trim(), Encoding.UTF8.GetString(output.ToArray()).Trim());
    }

    [Fact]
    public async Task No_native_arguments_are_forwarded_as_an_empty_list()
    {
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddApplicationExplanation((args, _) =>
        {
            Assert.NotNull(args);
            Assert.Empty(args);
            return Report();
        });
        Assert.Equal(0, await cli.InvokeAsync(["explain"], CommandIo.Null()));
    }

    [Fact]
    public async Task Help_does_not_activate_application()
    {
        var cli = new CliApplication("test").AddApplicationExplanation((_, _) => throw new InvalidOperationException("must not construct"));
        using var output = new MemoryStream();
        using var error = new StringWriter();
        Assert.Equal(0, await cli.InvokeAsync(["explain", "--help"], new CommandIo(Stream.Null, output, error)));
        Assert.Contains("host-arguments", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Unknown_command_fails_before_activation()
    {
        var calls = 0;
        var cli = new CliApplication("test").AddApplicationExplanation((_, _) => { calls++; return Report(); });
        Assert.NotEqual(0, await cli.InvokeAsync(["unknown-command"], CommandIo.Null()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Normal_host_arguments_bypass_explanation_unchanged()
    {
        var cli = new CliApplication("test").AddApplicationExplanation((_, _) => throw new InvalidOperationException());
        string[] args = ["--environment", "dev"];
        Assert.Equal(7, await cli.RunOrFallbackAsync(args, (forwarded, _) =>
        {
            Assert.Same(args, forwarded);
            return Task.FromResult(7);
        }));
    }

    [Fact]
    public async Task Admission_failure_writes_no_partial_report()
    {
        var cli = new CliApplication("test").AddApplicationExplanation((_, _) => throw new ArgumentException("invalid plan"));
        using var output = new MemoryStream();
        using var error = new StringWriter();
        Assert.NotEqual(0, await cli.InvokeAsync(["explain"], new CommandIo(Stream.Null, output, error)));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Cancelled_invocation_never_activates_application()
    {
        var calls = 0;
        var cli = new CliApplication("test").AddApplicationExplanation((_, _) => { calls++; return Report(); });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { Assert.NotEqual(0, await cli.InvokeAsync(["explain"], CommandIo.Null(), cancelled.Token)); }
        catch (OperationCanceledException) { }
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Delimiter_passes_help_to_native_configuration_without_consuming_it()
    {
        var called = false;
        var cli = new CliApplication("test").WithoutEnvironmentVariables().AddApplicationExplanation((args, _) =>
        {
            called = true;
            Assert.Equal(["--help"], args);
            return Report();
        });
        Assert.Equal(0, await cli.InvokeAsync(["explain", "--", "--help"], CommandIo.Null()));
        Assert.True(called);
    }

    [Fact]
    public void Application_composition_does_not_depend_on_generic_host_lifecycle()
    {
        var assembly = typeof(ModelActivator).Assembly;
        Assert.Equal("Cohesive.Application", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Cohesive.Host"
            || reference.Name!.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(Cohesive.Host.Services.HostProcessWorker).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Cohesive.Application");
    }

}
