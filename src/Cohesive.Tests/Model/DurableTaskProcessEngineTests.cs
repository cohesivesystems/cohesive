using DurableTask.Core;
using DurableTask.Core.Serializing;

namespace Cohesive.Tests.Model;

public sealed class DurableTaskProcessEngineTests
{
    [Fact]
    public async Task StartAsync_RegistersProcessAndReturnsStartResult()
    {
        var now = new DateTimeOffset(2026, 03, 21, 19, 30, 00, TimeSpan.Zero);
        var context = CreateOperationContext(now);
        var process = new ProcessDefinition(
            name: "durable-process",
            entryNode: "end",
            nodes: [new EndNode("end", _ => "done")]);

        var converter = CreateConverter();
        var hub = new FakeDurableTaskProcessHubClient();
        var definitions = new DurableTaskProcessDefinitionRegistry();
        var engine = new DurableTaskProcessEngine(
            client: hub,
            definitions: definitions,
            dataConverter: converter);

        var started = await engine.StartAsync(
            context,
            process,
            parameters: new Dictionary<string, object?> { ["routeId"] = "route-1" },
            runOptions: new() { ProcessId = "proc-123" });

        Assert.Equal("proc-123", started.ProcessId);
        Assert.Equal(process.Name, started.ProcessName);
        Assert.True(definitions.TryGet(process.Name, out var registered));
        Assert.Same(process, registered);
        Assert.Equal("proc-123", hub.LastInstanceId);
        Assert.Equal(process.Name, hub.LastRequest?.ProcessName);
        Assert.Equal("route-1", hub.LastRequest?.Parameters?["routeId"]);
        Assert.Equal(now, started.StartedAtUtc);
    }

    [Fact]
    public async Task GetStatusAsync_UsesCustomWaitingStatus()
    {
        var context = CreateOperationContext();
        var converter = CreateConverter();
        var state = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Running,
            CustomStatus: converter.Serialize(new DurableTaskProcessOrchestrationStatus(
                ProcessName: "durable-process",
                Status: ProcessExecutionStatus.Waiting,
                CurrentNode: "wait",
                CurrentPlace: "default",
                Wait: new(
                    WaitType: ProcessWaitType.ExternalEvent,
                    NodeName: "wait",
                    Key: "approval:1",
                    Timeout: null,
                    CaptureVar: null,
                    NextNode: "end"))),
            Input: converter.Serialize(new DurableTaskProcessRequest(
                ProcessName: "durable-process",
                Parameters: new Dictionary<string, object?>(),
                RunOptions: new() { ProcessId = "proc-123" })),
            Output: null,
            CreatedTime: new DateTime(2026, 03, 21, 18, 00, 00, DateTimeKind.Utc),
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 05, 00, DateTimeKind.Utc),
            CompletedTime: default);

        var engine = new DurableTaskProcessEngine(
            client: new FakeDurableTaskProcessHubClient(state: state),
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: converter);

        var status = await engine.GetStatusAsync(context, "proc-123");

        Assert.NotNull(status);
        Assert.Equal("proc-123", status.ProcessId);
        Assert.Equal("durable-process", status.ProcessName);
        Assert.Equal(ProcessExecutionStatus.Waiting, status.Status);
        Assert.Equal(new DateTimeOffset(2026, 03, 21, 18, 00, 00, TimeSpan.Zero), status.StartedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 03, 21, 18, 05, 00, TimeSpan.Zero), status.UpdatedAtUtc);
        Assert.Null(status.CompletedAtUtc);
        Assert.False(status.IsTerminal);
    }

    [Fact]
    public async Task GetStatusAsync_UsesTerminalRuntimeStatusWhenCustomStatusIsStale()
    {
        var context = CreateOperationContext();
        var converter = CreateConverter();
        var state = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Failed,
            CustomStatus: converter.Serialize(new DurableTaskProcessOrchestrationStatus(
                ProcessName: "durable-process",
                Status: ProcessExecutionStatus.Running,
                CurrentNode: "compile",
                CurrentPlace: "default",
                Wait: null)),
            Input: converter.Serialize(new DurableTaskProcessRequest(
                ProcessName: "durable-process",
                Parameters: new Dictionary<string, object?> { ["input"] = new SamplePayload("edi-1", 1) },
                RunOptions: new() { ProcessId = "proc-123" })),
            Output: null,
            CreatedTime: new DateTime(2026, 03, 21, 18, 00, 00, DateTimeKind.Utc),
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 05, 00, DateTimeKind.Utc),
            CompletedTime: new DateTime(2026, 03, 21, 18, 05, 00, DateTimeKind.Utc),
            FailureDetails: new(
                errorType: typeof(InvalidOperationException).FullName!,
                errorMessage: "Compilation failed.",
                stackTrace: "stack",
                innerFailure: null,
                isNonRetriable: false));

        var engine = new DurableTaskProcessEngine(
            client: new FakeDurableTaskProcessHubClient(state: state),
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: converter);

        var status = await engine.GetStatusAsync(context, "proc-123");

        Assert.NotNull(status);
        Assert.Equal(ProcessExecutionStatus.Failed, status.Status);
        Assert.True(status.IsTerminal);
        Assert.NotNull(status.Parameters);
        Assert.Equal("Compilation failed.", status.FailureMessage);
        Assert.NotNull(status.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, status.Error.ErrorType);
    }

    [Fact]
    public async Task SignalAsync_ForwardsDurableEvent()
    {
        var context = CreateOperationContext();
        var hub = new FakeDurableTaskProcessHubClient();
        var engine = new DurableTaskProcessEngine(
            client: hub,
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: CreateConverter());

        await engine.SignalAsync(context, "proc-123", "approval:1", true);

        Assert.Equal("proc-123", hub.LastSignaledInstanceId);
        Assert.Equal(DurableTaskProcessEngine.SignalEventName, hub.LastEventName);
        var signal = Assert.IsType<DurableTaskProcessSignal>(hub.LastEventPayload);
        Assert.Equal("approval:1", signal.Key);
        Assert.Equal(true, signal.Payload);
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsDurableResult()
    {
        var context = CreateOperationContext();
        var process = new ProcessDefinition(
            name: "durable-process",
            entryNode: "end",
            nodes: [new EndNode("end", _ => "done")]);

        var expected = new ProcessRunResult(
            ProcessId: "proc-123",
            ProcessName: process.Name,
            Result: "done",
            FinalPlace: "default",
            Variables: new Dictionary<string, object?>(StringComparer.Ordinal),
            Transitions: [],
            ExecutedEffects: [],
            PendingEffects: [],
            DeadLetters: []);

        var converter = CreateConverter();
        var running = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Running,
            CustomStatus: null,
            Input: converter.Serialize(new DurableTaskProcessRequest(
                ProcessName: process.Name,
                Parameters: new Dictionary<string, object?>(),
                RunOptions: new() { ProcessId = "proc-123" })),
            Output: null,
            CreatedTime: new DateTime(2026, 03, 21, 18, 00, 00, DateTimeKind.Utc),
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 01, 00, DateTimeKind.Utc),
            CompletedTime: default);
        var completed = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Completed,
            CustomStatus: null,
            Input: running.Input,
            Output: converter.Serialize(expected),
            CreatedTime: running.CreatedTime,
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc),
            CompletedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc));

        var engine = new DurableTaskProcessEngine(
            client: new FakeDurableTaskProcessHubClient(state: running, completionState: completed),
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: converter);

        var result = await engine.WaitForCompletionAsync(context, "proc-123");

        Assert.Equal(expected.ProcessId, result.ProcessId);
        Assert.Equal(expected.ProcessName, result.ProcessName);
        Assert.Equal(expected.Result, result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TypedProcessDefinition_ReturnsTypedDurableResult()
    {
        var context = CreateOperationContext();
        var process = new TypedProcessDefinition<string, string>(
            definition: new(
                name: "durable-process",
                entryNode: "end",
                nodes: [new EndNode("end", _ => "done")]),
            inputParameterName: "name"
            );

        var expected = new ProcessRunResult(
            ProcessId: "proc-typed",
            ProcessName: process.Definition.Name,
            Result: "done",
            FinalPlace: "default",
            Variables: new Dictionary<string, object?>(StringComparer.Ordinal),
            Transitions: [],
            ExecutedEffects: [],
            PendingEffects: [],
            DeadLetters: []
            );

        var converter = CreateConverter();
        var running = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Running,
            CustomStatus: null,
            Input: converter.Serialize(new DurableTaskProcessRequest(
                ProcessName: process.Definition.Name,
                Parameters: new Dictionary<string, object?> { ["name"] = "alice" },
                RunOptions: new() { ProcessId = "proc-typed" })),
            Output: null,
            CreatedTime: new DateTime(2026, 03, 21, 18, 00, 00, DateTimeKind.Utc),
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 01, 00, DateTimeKind.Utc),
            CompletedTime: default
            );
        var completed = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Completed,
            CustomStatus: null,
            Input: running.Input,
            Output: converter.Serialize(expected),
            CreatedTime: running.CreatedTime,
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc),
            CompletedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc)
            );

        var hub = new FakeDurableTaskProcessHubClient(state: running, completionState: completed);
        IProcessEngine engine = new DurableTaskProcessEngine(
            client: hub,
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: converter);

        var result = await engine.ExecuteAsync(
            context,
            process,
            "alice",
            runOptions: new() { ProcessId = "proc-typed" });

        Assert.Equal("done", result.Result);
        Assert.Equal("alice", hub.LastRequest?.Parameters?["name"]);
    }

    [Fact]
    public async Task ExecuteAsync_GeneratedProcessType_ReturnsTypedDurableResult()
    {
        var context = CreateOperationContext();
        var expected = new ProcessRunResult(
            ProcessId: "proc-generated",
            ProcessName: "Echo",
            Result: "alice",
            FinalPlace: "default",
            Variables: new Dictionary<string, object?>(StringComparer.Ordinal),
            Transitions: [],
            ExecutedEffects: [],
            PendingEffects: [],
            DeadLetters: []
            );

        var converter = CreateConverter();
        var running = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Running,
            CustomStatus: null,
            Input: converter.Serialize(new DurableTaskProcessRequest(
                ProcessName: expected.ProcessName,
                Parameters: new Dictionary<string, object?> { ["input"] = "alice" },
                RunOptions: new() { ProcessId = "proc-generated" })),
            Output: null,
            CreatedTime: new DateTime(2026, 03, 21, 18, 00, 00, DateTimeKind.Utc),
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 01, 00, DateTimeKind.Utc),
            CompletedTime: default
            );
        var completed = new DurableTaskProcessHubState(
            Status: OrchestrationStatus.Completed,
            CustomStatus: null,
            Input: running.Input,
            Output: converter.Serialize(expected),
            CreatedTime: running.CreatedTime,
            LastUpdatedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc),
            CompletedTime: new DateTime(2026, 03, 21, 18, 02, 00, DateTimeKind.Utc)
            );

        var hub = new FakeDurableTaskProcessHubClient(state: running, completionState: completed);
        IProcessEngine engine = new DurableTaskProcessEngine(
            client: hub,
            definitions: new DurableTaskProcessDefinitionRegistry(),
            dataConverter: converter);

        var result = await engine.ExecuteAsync(
            context,
            new EchoProcess(),
            "alice",
            runOptions: new() { ProcessId = "proc-generated" });

        Assert.Equal("alice", result.Result);
        Assert.Equal("Echo", result.ProcessName);
        Assert.Equal("alice", hub.LastRequest?.Parameters?["input"]);
    }

    [Fact]
    public void DurableTaskSystemTextJsonDataConverter_RoundTripsObjectPayloadsWithRuntimeTypes()
    {
        var converter = CreateConverter();
        var payload = new SamplePayload("alice", 3);
        var effectPayload = new SamplePayload("effect", 4);
        var result = new ProcessRunResult(
            ProcessId: "proc-123",
            ProcessName: "durable-process",
            Result: payload,
            FinalPlace: "default",
            Variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["count"] = 5,
                ["payload"] = payload
            },
            Transitions: [],
            ExecutedEffects: [new(EffectRequest.Named("notify"), effectPayload, null)],
            PendingEffects: [],
            DeadLetters: []
        );

        var serialized = converter.Serialize(result);
        var deserialized = Assert.IsType<ProcessRunResult>(converter.Deserialize(serialized, typeof(ProcessRunResult)));

        Assert.Equal(payload, Assert.IsType<SamplePayload>(deserialized.Result));
        Assert.Equal(5, Assert.IsType<int>(deserialized.Variables["count"]));
        Assert.Equal(payload, Assert.IsType<SamplePayload>(deserialized.Variables["payload"]));
        var executed = Assert.Single(deserialized.ExecutedEffects);
        Assert.Equal(effectPayload, Assert.IsType<SamplePayload>(executed.Result));
    }

    [Fact]
    public void DurableTaskSystemTextJsonDataConverter_DeserializesLegacyNewtonsoftTypeMetadata()
    {
        var payloadType = typeof(SamplePayload);
        var intType = typeof(int);
        var payloadTypeName = $"{payloadType.FullName}, {payloadType.Assembly.GetName().Name}";
        var intTypeName = $"{intType.FullName}, {intType.Assembly.GetName().Name}";
        var converter = CreateConverter();
        var json =
            $$"""
            {
              "ProcessId": "proc-123",
              "ProcessName": "durable-process",
              "Result": {
                "$type": "{{payloadTypeName}}",
                "Value": "alice",
                "Count": 3
              },
              "FinalPlace": "default",
              "Variables": {
                "count": {
                  "$type": "{{intTypeName}}",
                  "$value": 5
                }
              },
              "Transitions": [],
              "ExecutedEffects": [],
              "PendingEffects": [],
              "DeadLetters": []
            }
            """;

        var deserialized = Assert.IsType<ProcessRunResult>(converter.Deserialize(json, typeof(ProcessRunResult)));

        Assert.Equal(new SamplePayload("alice", 3), Assert.IsType<SamplePayload>(deserialized.Result));
        Assert.Equal(5, Assert.IsType<int>(deserialized.Variables["count"]));
    }

    [Fact]
    public void Register_DifferentDefinitionWithSameName_IsRejected()
    {
        var first = new ProcessDefinition(
            name: "duplicate",
            entryNode: "end",
            nodes: [new EndNode("end")]);
        var second = new ProcessDefinition(
            name: "duplicate",
            entryNode: "done",
            nodes: [new EndNode("done")]);

        var registry = new DurableTaskProcessDefinitionRegistry();
        registry.Register(first);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(second));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    static DataConverter CreateConverter()
    {
        return new DurableTaskSystemTextJsonDataConverter();
    }

    sealed class FakeDurableTaskProcessHubClient(
        DurableTaskProcessHubState? state = null,
        DurableTaskProcessHubState? completionState = null) : IDurableTaskProcessHubClient
    {
        public string? LastInstanceId { get; private set; }

        public DurableTaskProcessRequest? LastRequest { get; private set; }

        public string? LastSignaledInstanceId { get; private set; }

        public string? LastEventName { get; private set; }

        public object? LastEventPayload { get; private set; }

        public Task StartAsync(string orchestrationName, string orchestrationVersion, string instanceId, DurableTaskProcessRequest request)
        {
            LastInstanceId = instanceId;
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task<DurableTaskProcessHubState?> GetStateAsync(string instanceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        public Task<DurableTaskProcessHubState?> WaitForCompletionAsync(string instanceId, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(completionState ?? state);
        }

        public Task RaiseEventAsync(string instanceId, string eventName, object? payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastSignaledInstanceId = instanceId;
            LastEventName = eventName;
            LastEventPayload = payload;
            return Task.CompletedTask;
        }
    }

    static OperationContext CreateOperationContext(DateTimeOffset? utcNow = null)
    {
        return OperationContext.Create(
            timeProvider: utcNow is null ? TimeProvider.System : new FixedTimeProvider(utcNow.Value));
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    public sealed record SamplePayload(string Value, int Count);
}

[GenerateProcessDefinition(nameof(Echo))]
public partial class EchoProcess : IProcessDefinition<string, string>
{
    async ProcessTask<string> Echo(ProcessAuthoringContext<string, string> flow, string input)
    {
        return flow.Return(input);
    }
}
