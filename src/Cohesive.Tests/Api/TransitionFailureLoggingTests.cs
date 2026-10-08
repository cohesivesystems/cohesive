using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Identity;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Tests.ExecutionKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cohesive.Tests.Api;

public sealed class TransitionFailureLoggingTests
{
    [Fact]
    public async Task Unstarted_host_never_subscribes_including_when_disposed()
    {
        var (process, fail) = await CreateProcess();
        var logs = new FailureLogger();
        using (var host = BuildHost(process, logs)) await fail();
        await fail();
        Assert.Empty(logs.Messages);
    }

    [Fact]
    public async Task Repeated_registration_subscribes_once_and_stop_releases_it()
    {
        var (process, fail) = await CreateProcess();
        var logs = new FailureLogger();
        using var host = BuildHost(process, logs);
        await host.StartAsync();
        await fail();
        Assert.Contains("run/1", Assert.Single(logs.Messages));
        await host.StopAsync();
        await fail();
        Assert.Single(logs.Messages);
    }

    [Fact]
    public async Task Disposal_without_stop_releases_only_that_hosts_subscription()
    {
        var (process, fail) = await CreateProcess();
        var firstLogs = new FailureLogger();
        var secondLogs = new FailureLogger();
        using var first = BuildHost(process, firstLogs);
        using var second = BuildHost(process, secondLogs);
        await first.StartAsync();
        await second.StartAsync();
        await fail();
        Assert.Single(firstLogs.Messages);
        Assert.Single(secondLogs.Messages);
        first.Dispose();
        await fail();
        Assert.Single(firstLogs.Messages);
        Assert.Equal(2, secondLogs.Messages.Count);
    }

    [Fact]
    public async Task Disabled_debug_does_not_invoke_logger()
    {
        var (process, fail) = await CreateProcess();
        var logs = new FailureLogger { Enabled = false };
        using var host = BuildHost(process, logs);
        await host.StartAsync();
        await fail();
        Assert.Empty(logs.Messages);
        Assert.True(logs.EnabledChecks > 0);
    }

    static IHost BuildHost(HostedServiceProcess process, FailureLogger logs)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        builder.Services.AddCohesiveTransitionFailureLogging(process);
        builder.Services.AddCohesiveTransitionFailureLogging(process);
        return builder.Build();
    }

    public sealed record Input(string Token);

    static async Task<(HostedServiceProcess Process, Func<Task> Fail)> CreateProcess()
    {
        var entity = RunControlFixture.Entity;
        var native = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var repository = new TypedEntityRepository<RunControl>(
            new EntityTransitionCapturedTokenTests.ReadBoundaryRepository(native, false) { FailCommit = true });
        var actor = new PrincipalRef("tester", PrincipalKind.User);
        var scope = new ScopeRef("tenant/a", "tenant", PartitionKey: "tenant/a");
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient), Grants: [new(actor, scope, ["run"], "tests")]));
        var original = await native.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var transition = TransitionAuthoring.Create<RunControl, Input, string>(entity.Shape,
            id: new("logging-fence"), revision: new("1"), body => body.Set(new("status"), item => item.Status, "processed").Return("done"));
        var document = ProcessDefinitionDocuments.Create(new("logging-process"), new("1"),
            new(transition.Definition.Input, transition.Definition.Outcome, new("invoke"), [
                new InvokeTransitionProcessNode(new("invoke"), transition.Reference, Expr.Const("run/1"), Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("next"), new("return")))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt), RunControlFixture.Provenance);
        var process = Service.Define(new("service"), new("1"), RunControlFixture.Provenance).Operation("run").Require(new("run"))
            .Run(ProcessAuthoring.Project<Input, string>(document), bindings => bindings
                .Transition(transition, repository, expectedConcurrencyTokenField: nameof(Input.Token)))
            .Build("tests", TimeSpan.FromSeconds(2), new IdentityServiceInvocationAuthorization("tenant", new(nameof(RunControl.Tenant))));
        return (process, async () =>
        {
            var result = await process.Runtime.ExecuteProcessAsync(context, "run", new(new("run"), new("attempt")),
                ObservationValue.FromObject(new Input(original.ConcurrencyToken.Value)));
            Assert.Equal(Cohesive.Api.ApiResultKind.DomainError, result.Kind);
        });
    }

    sealed class FailureLogger : ILoggerProvider, ILogger
    {
        public bool Enabled { get; init; } = true;
        public int EnabledChecks { get; private set; }
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => categoryName == "Cohesive.Storage.Processes.TransitionFailures"
            ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public bool IsEnabled(LogLevel logLevel) { EnabledChecks++; return Enabled; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
        public void Dispose() { }
    }
}
