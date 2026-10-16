using System.Net;
using Cohesive.Host.Services;
using System.Net.Http.Json;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.OpenApi;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Cohesive.Tests.ExecutionKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class ServiceAcceptedProcessTests
{
    public sealed record Command(string Id, string Text);
    static readonly ExecutionProvenance Provenance = new(new("tests"), new("tests/accepted"), DocumentOrigin.Generated);

    [Fact]
    public void Builder_owned_pair_registers_both_exact_handles_and_retains_response_policy()
    {
        var fixture = new Fixture();
        var owner = Cohesive.Api.Api.Define();
        var response = ServiceProcessResponse.WaitForResult(TimeSpan.FromSeconds(1));
        var pair = owner.AcceptedProcess<Command, string>(fixture.Service, "start", "result", "/commands", "/results/{instanceId}", response);
        var definition = owner.Build();
        Assert.Equal(2, definition.Endpoints.Count);
        Assert.Same(pair.Start, definition.Endpoints[0]);
        Assert.Same(pair.Result, definition.Endpoints[1]);
        Assert.Same(response, pair.Response);
        Assert.Equal(pair.Start.Id, pair.Result.Operation.ResultOf);
        Assert.Equal(fixture.Api.Declaration.Reference, pair.Declaration.Reference);
    }

    [Fact]
    public void Pair_registration_is_atomic_when_second_endpoint_already_exists()
    {
        var fixture = new Fixture();
        var owner = Cohesive.Api.Api.Define();
        owner.Include(fixture.Api.Result);
        Assert.Throws<InvalidOperationException>(() => owner.AcceptedProcess<Command, string>(
            fixture.Service, "start", "result", "/commands", "/results/{instanceId}"));
        Assert.Same(fixture.Api.Result, Assert.Single(owner.Build().Endpoints));
    }

    [Fact]
    public void Pair_registration_cannot_mutate_a_frozen_owner()
    {
        var fixture = new Fixture();
        var owner = new PairApi();
        var frozen = owner.Definition;
        Assert.Throws<InvalidOperationException>(() => owner.Add(fixture.Service));
        Assert.Empty(frozen.Endpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_wait_is_independent_of_host_lifetime_and_retains_polling(bool wait)
    {
        var fixture = new Fixture(hosted: true, response: wait ? ServiceProcessResponse.WaitForResult(TimeSpan.FromSeconds(5)) : null);
        using var worker = fixture.Worker!;
        await worker.StartAsync(default);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User))));
        await using var app = builder.Build();
        app.MapServiceAcceptedProcess(fixture.Api, fixture.Runtime,
            (_, command) => (new(command.Id), new(command.Id), new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)),
            value => value.Value!.Value.GetRequiredString());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var command = new Command("host-instance", "echo-result");
        var response = await client.PostAsJsonAsync("/commands", command);
        Assert.Equal(wait ? HttpStatusCode.OK : HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User)));
        var terminal = await fixture.Runtime.ReadProcessResultAsync(context, "result", new(command.Id), TimeSpan.FromSeconds(5));
        Assert.True(terminal.Kind == ApiResultKind.Success, string.Join(";", terminal.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(command.Text, await client.GetFromJsonAsync<string>(response.Headers.Location));
        Assert.Contains(fixture.Api.Start.Operation.Results, result => result.Kind == ApiResultKind.Accepted);
        Assert.Equal(wait, fixture.Api.Start.Operation.Results.Any(result => result.Kind == ApiResultKind.Success));
        await app.StopAsync();
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task Caller_cancellation_and_wait_timeout_do_not_cancel_admitted_host_work()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = HostedQuery<string, string>.Create(new("blocked"), new("1"), new("test/blocked", "1"), "v1", Provenance);
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var process = ProcessAuthoring.Create<string, string>(
            new(new("blocked-process"), new("1"), new("query"), ProcessRecoveryPolicy.ContinueAttempt, Provenance), builder =>
            {
                var output = builder.Output<string>(new("output"), text);
                builder.EvaluateRelation(new("query"), query.Reference, builder.Input.Value,
                    builder.Continuation(builder.Edge(new("next"), new("return")), output));
                builder.Return(new("return"), output.Value);
            });
        var calls = 0;
        var prepared = Service.Define(new("prepare"), new("1"), Provenance).Operation("prepare").Run(process,
            host => host.Query(query, async (context, _, input) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                return input;
            })).Prepare();
        var service = Service.Define(new("blocked-service"), new("1"), Provenance)
            .Operation("start").Run(process).ReturnAfterAdmission(ProcessExecutionLifetime.Host)
            .Operation("result").ReadResultOf(process);
        using var worker = new HostProcessWorker(prepared, 1);
        var runtime = worker.Bind(service.Build(), "tests", new Authorization());
        await worker.StartAsync(default);
        using var request = new CancellationTokenSource();
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User)));
        var initial = await runtime.StartAsync(context.WithCancellationToken(request.Token), "start", new("one"), new("one"),
            new(new("one"), new("attempt")), ObservationValue.FromString("hello"));
        Assert.Equal(ApiResultKind.Accepted, initial.Kind);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.Cancel();
            var pending = await runtime.ReadProcessResultAsync(context, "result", new("one"), TimeSpan.FromMilliseconds(10));
            Assert.Equal(ApiResultKind.Accepted, pending.Kind);
            var replay = await runtime.StartAsync(context, "start", new("one"), new("one"),
                new(new("one"), new("attempt")), ObservationValue.FromString("hello"));
            Assert.Equal(ProcessStartDisposition.Replayed, replay.Outcome!.Disposition);
            Assert.Equal(1, calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(context, "start", new("two"), new("two"),
                new(new("two"), new("attempt")), ObservationValue.FromString("hello")).AsTask());
        }
        finally { release.TrySetResult(); }
        var completed = await runtime.ReadProcessResultAsync(context, "result", new("one"), TimeSpan.FromSeconds(5));
        Assert.Equal(ApiResultKind.Success, completed.Kind);
        Assert.Equal(1, calls);
        await worker.StopAsync(default);
        using var fresh = new HostProcessWorker(prepared, 1);
        var restarted = fresh.Bind(service.Build(), "tests", new Authorization());
        Assert.Equal(ApiResultKind.NotFound, (await restarted.ReadProcessResultAsync(context, "result", new("one"))).Kind);
    }

    [Fact]
    public async Task Bounded_HTTP_wait_returns_original_receipt_when_execution_is_pending()
    {
        var fixture = new Fixture(response: ServiceProcessResponse.WaitForResult(TimeSpan.FromMilliseconds(10)));
        fixture.Values.Current = ProcessExecutionValueReadResult.InProgress(new(fixture.Plan.DefinitionReference, new("pending")));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User))));
        await using var app = builder.Build();
        app.MapServiceAcceptedProcess(fixture.Api, fixture.Runtime,
            (_, command) => (new(command.Id), new(command.Id), new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)),
            value => value.Value!.Value.GetRequiredString());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var response = await client.PostAsJsonAsync("/commands", new Command("pending", "input"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<ServiceProcessReceipt>();
        Assert.Equal(response.Headers.Location!.OriginalString, receipt!.ResultLocation);
        Assert.Equal(1, fixture.Starts);
        fixture.Complete("pending");
        Assert.Equal("finished", await client.GetFromJsonAsync<string>(receipt.ResultLocation));
        Assert.Equal(1, fixture.Starts);
        await app.StopAsync();
    }

    [Fact]
    public void Host_lifetime_round_trips_in_canonical_service_document()
    {
        var fixture = new Fixture(hosted: true);
        using var worker = fixture.Worker!;
        Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(fixture.Api.Declaration), out var restored).IsValid);
        Assert.Equal(fixture.Api.Declaration.Reference, restored!.Reference);
        Assert.True(ServiceDefinitionDocuments.ValidateAndProject(restored, out var definition).IsValid);
        Assert.Equal(ProcessExecutionLifetime.Host, Assert.IsType<ServiceProcessOperation>(definition!.Operations.Single(operation => operation.Id == "start")).Execution!.Lifetime);
    }

    [Fact]
    public async Task API_wait_policy_rejects_nonwaiting_provider_before_registering_routes()
    {
        var fixture = new Fixture(response: ServiceProcessResponse.WaitForResult(TimeSpan.FromSeconds(1)));
        var runtime = new ServiceRuntime(fixture.Api.Declaration,
            [new ServiceProcessBinding("start", fixture.Plan, "tests", (_, _, _) => throw new InvalidOperationException("Must not dispatch.")),
             new ServiceProcessResultBinding("result", fixture.Plan, "tests", new NonWaitingValues())], fixture.Authorization);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var error = Assert.Throws<ServiceBindingValidationException>(() => app.MapServiceAcceptedProcess(fixture.Api, runtime,
            (_, command) => (new(command.Id), new(command.Id), new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)),
            value => value.Value!.Value.GetRequiredString()));
        Assert.Equal("services.binding.resultWaitingUnsupported", Assert.Single(error.Validation.Diagnostics).Code);
        Assert.Empty(((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public void Host_provider_cannot_claim_durable_execution()
    {
        var fixture = new Fixture(hosted: true);
        using var worker = fixture.Worker!;
        var durable = new Fixture();
        Assert.Throws<ServiceBindingValidationException>(() => worker.Bind(durable.Api.Declaration, "tests", durable.Authorization));
    }

    [Fact]
    public async Task Admission_replay_and_poll_share_exact_identity_without_restarting_execution()
    {
        var fixture = new Fixture();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User))));
        var models = new ServiceModelBuilder().Implement(fixture.Api,
            api => api.Bind(pair => pair, fixture.Runtime, (_, command) => (new(command.Id), new(command.Id),
                new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)), value => value.Value!.Value.GetRequiredString()));
        var model = models.Build(builder.Services, new NoPersistence());
        await using var app = builder.Build();
        app.UsePathBase("/demo");
        app.MapServices(model);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var command = new Command("instance/one%2F?", "private-input");
        var response = await client.PostAsJsonAsync("/demo/commands", command);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/demo/results/aW5zdGFuY2Uvb25lJTJGPw", response.Headers.Location!.OriginalString);
        var receipt = await response.Content.ReadFromJsonAsync<ServiceProcessReceipt>();
        Assert.Equal(ProcessStartDisposition.Accepted, receipt!.Admission.Disposition);
        Assert.Equal(response.Headers.Location.OriginalString, receipt.ResultLocation);
        Assert.DoesNotContain(command.Text, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Values.Reads);
        var replay = await client.PostAsJsonAsync("/demo/commands", command);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayReceipt = await replay.Content.ReadFromJsonAsync<ServiceProcessReceipt>();
        Assert.Equal(ProcessStartDisposition.Replayed, replayReceipt!.Admission.Disposition);
        Assert.Equal(receipt.Admission.Admission, replayReceipt.Admission.Admission);
        var conflicting = await client.PostAsJsonAsync("/demo/commands", command with { Text = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Null(conflicting.Headers.Location);
        fixture.Values.Current = ProcessExecutionValueReadResult.InProgress(new(fixture.Plan.DefinitionReference, new(command.Id)));
        Assert.Equal(HttpStatusCode.Accepted, (await client.GetAsync(receipt.ResultLocation)).StatusCode);
        fixture.Complete(command.Id);
        Assert.Equal("finished", await client.GetFromJsonAsync<string>(receipt.ResultLocation));
        fixture.Authorization.ReadAllowed = false;
        var reads = fixture.Values.Reads;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(receipt.ResultLocation)).StatusCode);
        Assert.Equal(reads, fixture.Values.Reads);
        Assert.Equal(3, fixture.Starts);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/demo/results/not!base64")).StatusCode);
        Assert.Equal(reads, fixture.Values.Reads);
        await app.StopAsync();
    }

    [Fact]
    public async Task Lost_response_is_not_retried_and_caller_can_replay_original_intent()
    {
        var fixture = new Fixture { ThrowAfterAdmission = true };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User))));
        await using var app = builder.Build();
        app.MapServiceAcceptedProcess(fixture.Api, fixture.Runtime,
            (_, command) => (new(command.Id), new(command.Id), new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)),
            value => value.Value!.Value.GetRequiredString());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var command = new Command("stable-id", "input");
        var lost = await client.PostAsJsonAsync("/commands", command);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Null(lost.Headers.Location);
        Assert.Equal(1, fixture.Starts);
        Assert.Equal(0, fixture.Values.Reads);
        fixture.ThrowAfterAdmission = false;
        var replay = await client.PostAsJsonAsync("/commands", command);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(ProcessStartDisposition.Replayed, (await replay.Content.ReadFromJsonAsync<ServiceProcessReceipt>())!.Admission.Disposition);
        Assert.Equal(2, fixture.Starts);
        await app.StopAsync();
    }

    [Fact]
    public void Complete_surface_rejects_a_dangling_poll_relationship_without_storage()
    {
        var fixture = new Fixture();
        var onlyPoll = new ApiDefinition([fixture.Api.Result]);
        var plan = new ServiceImplementationPlan();
        plan.Contribute(onlyPoll, () => plan.Implement(onlyPoll, fixture.Api.Result));
        var error = Assert.Throws<ServiceBindingValidationException>(() => plan.Definition);
        Assert.Equal("services.api.resultLinkInvalid", Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("failed", 422)]
    [InlineData("wrong-definition", 404)]
    [InlineData("unavailable", 500)]
    public async Task Poll_preserves_failure_contract_without_dispatch(string state, int expected)
    {
        var fixture = new Fixture();
        if (state == "failed") fixture.Complete("id", failed: true);
        if (state == "wrong-definition") fixture.Values.Current = ProcessExecutionValueReadResult.InProgress(
            new(new(fixture.Plan.DefinitionReference.DefinitionId, new("other"), fixture.Plan.DefinitionReference.Fingerprint), new("id")));
        if (state == "unavailable") fixture.Values.Current = ProcessExecutionValueReadResult.TerminalArtifactUnavailable(new(fixture.Plan.DefinitionReference, new("id")));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create().WithIdentityContext(new IdentityContext(new("caller", PrincipalKind.User))));
        await using var app = builder.Build();
        app.MapServiceAcceptedProcess(fixture.Api, fixture.Runtime,
            (_, command) => (new(command.Id), new(command.Id), new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)),
            value => value.Value!.Value.GetRequiredString());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        Assert.Equal(expected, (int)(await client.GetAsync("/results/aWQ")).StatusCode);
        Assert.Equal(0, fixture.Starts);
        await app.StopAsync();
    }

    [Fact]
    public void Contract_retains_link_and_requires_explicit_admission_of_exact_process()
    {
        var fixture = new Fixture();
        Assert.Equal(ApiResultKind.Accepted, fixture.Api.Start.Operation.PrimaryResult.Kind);
        Assert.Equal(fixture.Api.Start.Id, fixture.Api.Result.Operation.ResultOf);
        var emission = new OpenApiEmitter().Emit(fixture.Api.Definition);
        using var openapi = JsonDocument.Parse(emission.Documents[0].Text);
        Assert.Equal(fixture.Api.Start.Id.Value, openapi.RootElement.GetProperty("paths").GetProperty("/results/{instanceId}")
            .GetProperty("get").GetProperty("x-cohesive-result-of").GetString());
        Assert.Equal(fixture.Api.Start.Id, fixture.Api.Result.WithHttp(new("GET", "/other/{instanceId}", [], null)).Operation.ResultOf);
        var ephemeral = Service.Define(new("other"), new("1"), Provenance)
            .Operation("start").Run(fixture.Plan).ExecuteEphemerally(TimeSpan.FromSeconds(1))
            .Operation("result").ReadResultOf(ProcessAuthoring.Project<string, string>(fixture.Plan.Document));
        Assert.Throws<ServiceBindingValidationException>(() => ephemeral.AcceptedApi<Command, string>("start", "result", "/commands", "/results/{instanceId}"));
        var unrelated = Service.Define(new("other"), new("1"), Provenance)
            .Operation("start").Run(fixture.Plan).ReturnAfterDurableAdmission()
            .Operation("result").ReadResultOf(ProcessAuthoring.Project<string, string>(fixture.Plan.Document));
        var other = unrelated.AcceptedApi<Command, string>("start", "result", "/commands", "/results/{instanceId}");
        Assert.Throws<ServiceBindingValidationException>(() => new ServiceModelBuilder().Implement(other,
            api => api.Bind(pair => pair, fixture.Runtime, (_, command) => (new(command.Id), new(command.Id),
                new(new(command.Id), new("attempt")), ObservationValue.FromString(command.Text)), value => value.Value!.Value.GetRequiredString())));
    }

    [Fact]
    public void Poll_cannot_link_a_different_process_even_when_result_types_match()
    {
        var fixture = new Fixture();
        var other = ProcessDefinitionDocuments.Create(new("other-process"), new("1"),
            fixture.Plan.Definition, Provenance);
        var service = Service.Define(new("mixed"), new("1"), Provenance)
            .Operation("start").Run(fixture.Plan).ReturnAfterDurableAdmission()
            .Operation("result").ReadResultOf(ProcessAuthoring.Project<string, string>(other));
        var error = Assert.Throws<ServiceBindingValidationException>(() => service.AcceptedApi<Command, string>(
            "start", "result", "/commands", "/results/{instanceId}"));
        Assert.Equal("services.api.resultSourceMismatch", Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Theory]
    [InlineData("https://other/results/{instanceId}")]
    [InlineData("//other/{instanceId}")]
    [InlineData("/results/{id}")]
    [InlineData("/results/{instanceId}/{instanceId}")]
    [InlineData("/results/{instanceId}?token=bad")]
    public void Unsafe_or_ambiguous_poll_routes_fail_at_authoring(string route)
    {
        var fixture = new Fixture();
        Assert.Throws<ServiceBindingValidationException>(() => fixture.Service.AcceptedApi<Command, string>("start", "result", "/commands", route));
    }

    sealed class PairApi : ApiModel
    {
        public ServiceAcceptedProcessApi<Command, string> Add(ServiceBuilder service) =>
            Endpoints.AcceptedProcess<Command, string>(service, "start", "result", "/commands", "/results/{instanceId}");
    }

    sealed class Fixture
    {
        public CompiledProcessPlan Plan { get; }
        public ServiceBuilder Service { get; }
        public ServiceAcceptedProcessApi<Command, string> Api { get; }
        public ServiceRuntime Runtime { get; }
        public Values Values { get; } = new();
        public Authorization Authorization { get; } = new();
        public int Starts;
        public bool ThrowAfterAdmission;
        public HostProcessWorker? Worker { get; }
        public Fixture(bool hosted = false, ServiceProcessResponse? response = null)
        {
            var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
            var document = ProcessDefinitionDocuments.Create(new("echo"), new("1"),
                new(text, text, new("return"), [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
                    ProcessRecoveryPolicy.ContinueAttempt), Provenance);
            Plan = ProcessStaticCompiler.Compile(document, new()).Plan!;
            Service = Cohesive.Api.Execution.Services.Service.Define(new("accepted"), new("1"), Provenance)
                .Operation("start").Run(Plan).ReturnAfterAdmission(hosted ? ProcessExecutionLifetime.Host : ProcessExecutionLifetime.Durable)
                .Operation("result").ReadResultOf(ProcessAuthoring.Project<string, string>(document));
            Api = Service.AcceptedApi<Command, string>("start", "result", "/commands", "/results/{instanceId}", response);
            if (hosted)
            {
                var prepared = Service.PrepareProcess(ProcessAuthoring.Project<string, string>(document),
                    _ => throw new InvalidOperationException("Echo has no storage dependency."));
                Worker = new HostProcessWorker(prepared, 2);
                Runtime = Worker.Bind(Api.Declaration, "tests", Authorization);
                return;
            }
            var catalog = ExecutionControlApiCatalog.Create();
            var adapter = new InMemoryExecutionControlApiAdapter(ProcessControlTestFixture.Create().Catalog, catalog);
            Runtime = new(Api.Declaration,
                [new ServiceProcessBinding("start", Plan, "tests", async (context, request, invocation) =>
                {
                    Starts++;
                    var result = Assert.IsType<Cohesive.Execution.ProcessStartResult>((await adapter.DispatchAsync(context, catalog.Start, request, invocation)).Body);
                    if (ThrowAfterAdmission) throw new IOException("Simulated response loss after native receipt commit.");
                    return result;
                }), new ServiceProcessResultBinding("result", Plan, "tests", Values)], Authorization);
        }
        public void Complete(string id, bool failed = false) => Values.Current = ProcessExecutionValueReadResult.Available(new(
            Plan.DefinitionReference, new(id), terminalOutcome: new(failed ? ExecutionTerminalOutcomeKind.Failed : ExecutionTerminalOutcomeKind.Completed,
                DateTimeOffset.UtcNow, failed ? null : ExecutionStatusValue.Disclose(PortableValue.Concrete(Plan.Definition.Result, ObservationValue.FromString("finished")))),
            terminalContinuation: new(new(id), new("attempt")), evidence: []));
    }
    sealed class Authorization : IServiceInvocationAuthorization
    {
        public bool ReadAllowed = true;
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation) =>
            ValueTask.FromResult<ScopeRef?>(operation.Id == "result" && !ReadAllowed ? null : new("tenant", "tenant", PartitionKey: "tenant"));
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceOperation operation, EntitySnapshot snapshot) => ValueTask.FromResult(true);
    }
    sealed class NonWaitingValues : IProcessExecutionValueRepository
    {
        public ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(OperationContext context, InteractionAuthorityScope scope, ProcessInstanceId instance) =>
            throw new InvalidOperationException("Must not read during setup.");
    }
    sealed class Values : IProcessExecutionValueRepository, IProcessExecutionCompletionWaiter
    {
        public ProcessExecutionValueReadResult Current = ProcessExecutionValueReadResult.NotFound();
        public int Reads;
        public ValueTask<bool> WaitForCompletionAsync(OperationContext context, InteractionAuthorityScope scope, ProcessInstanceId instance, TimeSpan maximumWait) => ValueTask.FromResult(false);
        public ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(OperationContext context, InteractionAuthorityScope authorityScope, ProcessInstanceId processInstanceId)
        { Reads++; return ValueTask.FromResult(Current); }
    }
    sealed class NoPersistence : IServicePersistence
    {
        public IEntityRepository Repository(ValueContract observation) => throw new InvalidOperationException("No storage resolution expected.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes) =>
            throw new InvalidOperationException("No storage resolution expected.");
    }
}
