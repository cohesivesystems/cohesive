using System.Text.Json.Nodes;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Cohesive.Tests.Model;

public sealed class ProcessEngineRuntimeTests
{
    [Fact]
    public async Task StartAsync_AndWaitForCompletionAsync_RunThroughLifecycle()
    {
        var context = CreateOperationContext();
        var waitAdapter = new InMemoryProcessWaitAdapter();
        IProcessEngine engine = new ProcessEngine(CreateRuntimeServices(
            transitionHost: new DeclarativeTransitionHost(),
            waitAdapter: waitAdapter));

        var process = new ProcessDefinition(
            name: "AwaitApprovalLifecycle",
            entryNode: "waitApproval",
            nodes:
            [
                new WaitNode(
                    name: "waitApproval",
                    waitType: ProcessWaitType.ExternalEvent,
                    keyExpression: _ => "approval:lifecycle",
                    captureVar: "approved",
                    nextNode: "end"
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<bool>("approved")
                    )
            ]);

        var started = await engine.StartAsync(
            context,
            process,
            runOptions: new() { ProcessId = "proc-lifecycle" });

        var running = await engine.GetStatusAsync(context, started.ProcessId);
        Assert.NotNull(running);
        Assert.True(
            running.Status is ProcessExecutionStatus.Running or ProcessExecutionStatus.Waiting);

        await engine.SignalAsync(context, started.ProcessId, "approval:lifecycle", true);

        var run = await engine.WaitForCompletionAsync(context, started.ProcessId);
        Assert.Equal(true, run.Result);

        var completed = await engine.GetStatusAsync(context, started.ProcessId);
        Assert.NotNull(completed);
        Assert.Equal(ProcessExecutionStatus.Completed, completed.Status);
        Assert.True(completed.IsTerminal);
    }

    [Fact]
    public void PlanNextStep_TimerWaitInsideMove_AdvancesThenWaitsThenCompletes()
    {
        var context = CreateOperationContext();
        var engine = new ProcessEngine(CreateRuntimeServices(transitionHost: new DeclarativeTransitionHost()))
            .RegisterPlace(new(
                name: "io",
                capabilities: [ProcessCapability.PureEvaluation, ProcessCapability.ExternalIO]
                ));

        var process = new ProcessDefinition(
            name: "DurableTimerInsideMove",
            entryNode: "moveToIo",
            nodes:
            [
                new MoveNode(
                    name: "moveToIo",
                    targetPlace: "io",
                    bodyNode: "waitForTimer",
                    nextNode: null
                    ),
                new WaitNode(
                    name: "waitForTimer",
                    waitType: ProcessWaitType.Timer,
                    keyExpression: _ => "timer:1",
                    timeoutExpression: _ => TimeSpan.FromMinutes(5),
                    captureVar: "timer",
                    nextNode: "end"
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<ProcessTimerFired>("timer").Key
                    )
            ]);

        var initial = engine.CreateCheckpoint(
            context,
            process,
            runOptions: new() { ProcessId = "proc-timer-slice" });

        var movePlan = engine.PlanNextStep(context, process, initial);
        Assert.Equal(ProcessExecutionPlanKind.Advance, movePlan.Kind);
        Assert.Equal("io", movePlan.Checkpoint.CurrentPlace);
        Assert.Equal("waitForTimer", movePlan.Checkpoint.CurrentNode);

        var waitPlan = engine.PlanNextStep(context, process, movePlan.Checkpoint);
        Assert.Equal(ProcessExecutionPlanKind.Wait, waitPlan.Kind);
        Assert.Equal(ProcessExecutionStatus.Waiting, waitPlan.Checkpoint.Status);
        Assert.Equal("io", waitPlan.Checkpoint.CurrentPlace);
        Assert.NotNull(waitPlan.Wait);
        Assert.Equal(ProcessWaitType.Timer, waitPlan.Wait!.WaitType);
        Assert.Equal("waitForTimer", waitPlan.Wait.NodeName);
        Assert.Equal("timer:1", waitPlan.Wait.Key);

        var resumed = engine.ResumeWait(
            context,
            process,
            waitPlan.Checkpoint,
            new ProcessTimerFired("timer:1", context.UtcNow.AddMinutes(5)));

        var completed = engine.PlanNextStep(context, process, resumed);
        Assert.Equal(ProcessExecutionPlanKind.Complete, completed.Kind);
        Assert.Equal(ProcessExecutionStatus.Completed, completed.Checkpoint.Status);
        Assert.Equal("timer:1", completed.Checkpoint.Result);
        Assert.Equal("default", completed.Checkpoint.CurrentPlace);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessDefinition_ExecutesTransactionsAndExplicitRequest()
    {
        var context = CreateOperationContext();
        var entityRef = new ProcessEntityRef(entityType: "Route", entityId: "route-1");
        
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(entityRef, CreateInitialMileageState(), version: 0);
        var repository = new TrackingEntityRepository(storage);

        var transitionHost = new DeclarativeTransitionHost().Register(BuildMileageEntity());
        var engine = new ProcessEngine(CreateRuntimeServices(
                transitionHost: transitionHost,
                entityRepository: repository,
                checkpointRepository: storage,
                transactionGateway: storage))
            .RegisterHandler(new StaticMileageHandler(totalMiles: 218m));

        var process = new ProcessDefinition(
            name: "AddStopWithMileage",
            entryNode: "txAdd",
            nodes:
            [
                new TransactionNode(
                    name: "txAdd",
                    scope: ProcessTransactionScope.SingleEntity(entityRef.EntityId),
                    onConflictPolicy: OnConflictPolicy.RetryWithBackoff(maxAttempts: 3),
                    bodyNode: "runAdd",
                    nextNode: "executeMileage"
                    ),
                new RunEntityTransitionNode(
                    name: "runAdd",
                    entityRefExpression: _ => entityRef,
                    transitionName: "AddStop",
                    inputExpression: _ => new { stopCode = "SEA" },
                    nextNode: null
                    ),
                new ExecuteEffectRequestNode(
                    name: "executeMileage",
                    requestExpression: _ => EffectRequest.Named(
                        name: CalculateMileageRequest.RequestName,
                        payload: new JsonObject { ["routeId"] = entityRef.EntityId }
                        ),
                    resultVariable: "mileage",
                    nextNode: "txApply"
                    ),
                new TransactionNode(
                    name: "txApply",
                    scope: ProcessTransactionScope.SingleEntity(entityRef.EntityId),
                    onConflictPolicy: OnConflictPolicy.RetryWithBackoff(maxAttempts: 3),
                    bodyNode: "applyMileage",
                    nextNode: "end"
                    ),
                new RunEntityTransitionNode(
                    name: "applyMileage",
                    entityRefExpression: _ => entityRef,
                    transitionName: "ApplyMileage",
                    inputExpression: ctx => ctx.RequireVariable<MileageResult>("mileage"),
                    nextNode: null
                    ),
                new EndNode("end")
            ]);

        var run = await engine.ExecuteAsync(context, process);

        Assert.Equal(2, run.Transitions.Count);
        Assert.Single(run.ExecutedEffects);

        var finalSnapshot = await storage.Get(context, entityRef);
        var stops = finalSnapshot.State.Fields["Stops"].Deserialize<string[]>();
        Assert.NotNull(stops);
        Assert.Equal(["SEA"], stops);
        Assert.Equal(218m, finalSnapshot.State.Fields["PlannedMiles"].GetDecimal());
        Assert.Equal(2, finalSnapshot.Version);
        Assert.Equal(2, repository.LoadCount);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessDefinitionBuilder_ThreadsTypedRequestOutputs()
    {
        var context = CreateOperationContext();
        var engine = new ProcessEngine(CreateRuntimeServices(new DeclarativeTransitionHost())).RegisterHandler(new FormatGreetingHandler());

        var process = new ProcessDefinitionBuilder("GreetingFlow")
            .AddEffectRequestNode(
                name: "greeting",
                requestExpression: ctx => new FormatGreetingRequest(ctx.RequireParameter<string>("name")),
                resultVariable: "greeting",
                nextNode: "end"
                )
            .AddEndNode(
                name: "end",
                resultExpression: ctx => $"{ctx.RequireParameter<string>("name")}:{ctx.RequireVariable<string>("greeting")}"
                )
            .Build();

        var run = await engine.ExecuteAsync(
            context,
            process,
            parameters: new Dictionary<string, object?> { ["name"] = "alice" });

        Assert.Equal("alice:hello alice", run.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TypedProcessDefinition_ReturnsTypedRunResult()
    {
        var context = CreateOperationContext();
        IProcessEngine engine = new ProcessEngine(CreateRuntimeServices(new DeclarativeTransitionHost()))
            .RegisterHandler(new FormatGreetingHandler());

        var process = new TypedProcessDefinition<string, string>(
            definition: new ProcessDefinitionBuilder("GreetingFlow")
                .AddEffectRequestNode(
                    name: "greeting",
                    requestExpression: ctx => new FormatGreetingRequest(ctx.RequireParameter<string>("name")),
                    resultVariable: "greeting",
                    nextNode: "end"
                    )
                .AddEndNode(
                    name: "end",
                    resultExpression: ctx => $"{ctx.RequireParameter<string>("name")}:{ctx.RequireVariable<string>("greeting")}"
                    )
                .Build(),
            inputParameterName: "name");

        var run = await engine.ExecuteAsync(context, process, "alice");

        Assert.Equal("alice:hello alice", run.Result);
        Assert.Equal("GreetingFlow", run.ProcessName);
    }

    [Fact]
    public async Task ExecuteAsync_InstanceProcessDefinition_UsesPerInstanceConfiguration()
    {
        var context = CreateOperationContext();
        IProcessEngine engine = new ProcessEngine(CreateRuntimeServices(new DeclarativeTransitionHost()));

        var first = await engine.ExecuteAsync(context, new ConfigurableSuffixProcess(":one"), "alice");
        var second = await engine.ExecuteAsync(context, new ConfigurableSuffixProcess(":two"), "alice");

        Assert.Equal("alice:one", first.Result);
        Assert.Equal("alice:two", second.Result);
    }

    [Fact]
    public async Task ExecuteAsync_GeneratedProcess_EarlyReturnBranch_SkipsFallthroughNodes()
    {
        var context = CreateOperationContext();
        IProcessEngine engine = new ProcessEngine(CreateRuntimeServices(new DeclarativeTransitionHost()));

        var skipped = await engine.ExecuteAsync(context, new EarlyReturnProcess(), "skip");
        var continued = await engine.ExecuteAsync(context, new EarlyReturnProcess(), "alice");

        Assert.Equal("skipped", skipped.Result);
        Assert.DoesNotContain("continued", skipped.Variables.Keys);

        Assert.Equal("alice:continued", continued.Result);
        Assert.Equal("alice:continued", continued.Variables["continued"]);
    }

    [Fact]
    public async Task ExecuteAsync_GeneratedProcess_IfElseBranch_RejoinsSharedContinuation()
    {
        var context = CreateOperationContext();
        IProcessEngine engine = new ProcessEngine(CreateRuntimeServices(new DeclarativeTransitionHost()));

        var left = await engine.ExecuteAsync(context, new IfElseContinuationProcess(), "left");
        var right = await engine.ExecuteAsync(context, new IfElseContinuationProcess(), "right");

        Assert.Equal("left:done", left.Result);
        Assert.Equal("left-branch", left.Variables["leftBranch"]);
        Assert.DoesNotContain("rightBranch", left.Variables.Keys);
        Assert.Equal("left:done", left.Variables["finalValue"]);

        Assert.Equal("right:done", right.Result);
        Assert.Equal("right-branch", right.Variables["rightBranch"]);
        Assert.DoesNotContain("leftBranch", right.Variables.Keys);
        Assert.Equal("right:done", right.Variables["finalValue"]);
    }

    [Fact]
    public async Task ExecuteAsync_TransactionConflict_RetriesWithBackoffPolicy()
    {
        var context = CreateOperationContext();
        var entityRef = new ProcessEntityRef(entityType: "Counter", entityId: "counter-1");
        
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(entityRef, CreateInitialCounterState(), version: 0);
        storage.QueueConflict(entityRef, count: 1);

        var transitionHost = new DeclarativeTransitionHost().Register(BuildCounterEntity());
        var engine = new ProcessEngine(CreateRuntimeServices(
            transitionHost: transitionHost,
            runtimeStorage: storage
            ));

        var process = new ProcessDefinition(
            name: "IncrementCounter",
            entryNode: "tx",
            nodes:
            [
                new TransactionNode(
                    name: "tx",
                    scope: ProcessTransactionScope.SingleEntity(entityRef.EntityId),
                    onConflictPolicy: OnConflictPolicy.RetryWithBackoff(maxAttempts: 2),
                    bodyNode: "increment",
                    nextNode: "end"),

                new RunEntityTransitionNode(
                    name: "increment",
                    entityRefExpression: _ => entityRef,
                    transitionName: "Increment",
                    inputExpression: _ => new { delta = 1 },
                    nextNode: null),

                new EndNode("end")
            ]);

        var run = await engine.ExecuteAsync(context, process);

        Assert.Single(run.Transitions);

        var finalSnapshot = await storage.Get(context, entityRef);
        Assert.Equal(1, finalSnapshot.State.Fields["Count"].GetInt32());
        Assert.Equal(1, finalSnapshot.Version);
    }

    [Fact]
    public async Task ExecuteAsync_MoveNode_EnforcesLocalityCapabilities()
    {
        var context = CreateOperationContext();
        var engine = new ProcessEngine(CreateRuntimeServices(transitionHost: new DeclarativeTransitionHost()))
            .RegisterHandler(new PingHandler())
            .RegisterPlace(new(
                name: "compute",
                capabilities: [ProcessCapability.PureEvaluation]
            ))
            .RegisterPlace(new(
                name: "io",
                capabilities: [ProcessCapability.PureEvaluation, ProcessCapability.ExternalIO] 
            ));

        var process = new ProcessDefinition(
            name: "MoveForIo",
            entryNode: "moveToIo",
            nodes:
            [
                new MoveNode(
                    name: "moveToIo",
                    targetPlace: "io",
                    bodyNode: "sendPing",
                    nextNode: "end"
                    ),
                new ExecuteEffectRequestNode(
                    name: "sendPing",
                    requestExpression: _ => EffectRequest.Named(
                        name: PingRequest.RequestName,
                        payload: new JsonObject()),
                    resultVariable: "ping",
                    nextNode: null
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<PingResponse>("ping").Message
                    )
            ]);

        var run = await engine.ExecuteAsync(
            context,
            process,
            runOptions: new() { InitialPlace = "compute" }
            );

        Assert.Equal("pong", run.Result);
    }

    [Fact]
    public async Task ExecuteAsync_ExternalEventWait_BranchesDeterministically()
    {
        var context = CreateOperationContext();
        var waitAdapter = new InMemoryProcessWaitAdapter();
        waitAdapter.PublishExternalEvent(key: "approval:load-1", payload: true);

        var engine = new ProcessEngine(CreateRuntimeServices(
            transitionHost: new DeclarativeTransitionHost(),
            waitAdapter: waitAdapter));
        var process = new ProcessDefinition(
            name: "AwaitApproval",
            entryNode: "waitApproval",
            nodes:
            [
                new WaitNode(
                    name: "waitApproval",
                    waitType: ProcessWaitType.ExternalEvent,
                    keyExpression: _ => "approval:load-1",
                    captureVar: "approved",
                    nextNode: "choose"
                    ),
                new BranchingNode(
                    name: "choose",
                    branches:
                    [
                        new(
                            Condition: ctx => ctx.RequireVariable<bool>("approved"),
                            Node: "approved")
                    ],
                    elseNode: "rejected"
                    ),
                new EndNode(
                    name: "approved",
                    resultExpression: _ => "approved"
                    ),
                new EndNode(
                    name: "rejected",
                    resultExpression: _ => "rejected"
                    )
            ]);

        var run = await engine.ExecuteAsync(context, process);

        Assert.Equal("approved", run.Result);
    }

    [Fact]
    public async Task StartAsync_WithoutCheckpointRepository_GetStatusUsesTrackedExecution()
    {
        var context = CreateOperationContext();
        IProcessEngine engine = new ProcessEngine(new ProcessRuntimeServices(
                transitionHost: new DeclarativeTransitionHost(),
                entityRepository: new InMemoryProcessStorageAdapter(),
                deadLetterSink: new InMemoryProcessDeadLetterSink()))
            .RegisterHandler(new FormatGreetingHandler());

        var process = new ProcessDefinitionBuilder("GreetingFlow")
            .AddEffectRequestNode(
                name: "greeting",
                requestExpression: ctx => new FormatGreetingRequest(ctx.RequireParameter<string>("name")),
                resultVariable: "greeting",
                nextNode: "end"
                )
            .AddEndNode(
                name: "end",
                resultExpression: ctx => $"{ctx.RequireParameter<string>("name")}:{ctx.RequireVariable<string>("greeting")}"
                )
            .Build();

        var started = await engine.StartAsync(
            context,
            process,
            parameters: new Dictionary<string, object?> { ["name"] = "alice" },
            runOptions: new() { ProcessId = "proc-no-checkpoint" });

        var run = await engine.WaitForCompletionAsync(context, started.ProcessId);
        var status = await engine.GetStatusAsync(context, started.ProcessId);

        Assert.Equal("alice:hello alice", run.Result);
        Assert.NotNull(status);
        Assert.Equal(ProcessExecutionStatus.Completed, status.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WaitNodeWithoutWaitAdapter_Throws()
    {
        var context = CreateOperationContext();
        var engine = new ProcessEngine(new ProcessRuntimeServices(
            transitionHost: new DeclarativeTransitionHost(),
            entityRepository: new InMemoryProcessStorageAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));

        var process = new ProcessDefinition(
            name: "AwaitApproval",
            entryNode: "waitApproval",
            nodes:
            [
                new WaitNode(
                    name: "waitApproval",
                    waitType: ProcessWaitType.ExternalEvent,
                    keyExpression: _ => "approval:missing",
                    captureVar: "approved",
                    nextNode: "end"
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<bool>("approved")
                    )
            ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(context, process));

        Assert.Contains(nameof(IProcessWaitAdapter), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_StaleContinuationToken_IsRejectedAndDeadLettered()
    {
        var context = CreateOperationContext();
        var entityRef = new ProcessEntityRef(entityType: "Route", entityId: "route-1");
        var storage = new InMemoryProcessStorageAdapter();
        storage.SeedEntity(entityRef, CreateInitialIntentState(), version: 0);

        var deadLetters = new InMemoryProcessDeadLetterSink();
        var transitionHost = new DeclarativeTransitionHost().Register(BuildIntentEntity());
        var engine = new ProcessEngine(CreateRuntimeServices(
                transitionHost: transitionHost,
                runtimeStorage: storage,
                deadLetterSink: deadLetters
            ))
            .RegisterHandler(new IntentMileageHandler());

        var process = new ProcessDefinition(
            name: "RejectStaleContinuation",
            entryNode: "firstAdd",
            nodes:
            [
                new RunEntityTransitionNode(
                    name: "firstAdd",
                    entityRefExpression: _ => entityRef,
                    transitionName: "AddStop",
                    inputExpression: _ => new { stopCode = "SAC" },
                    resultVariable: "firstTransition",
                    effectScheduling: ProcessEffectSchedulingMode.Deferred,
                    nextNode: "secondAdd"
                    ),
                new RunEntityTransitionNode(
                    name: "secondAdd",
                    entityRefExpression: _ => entityRef,
                    transitionName: "AddStop",
                    inputExpression: _ => new { stopCode = "RNO" },
                    effectScheduling: ProcessEffectSchedulingMode.Deferred,
                    nextNode: "executeFirstEffect"
                    ),
                new ExecuteEffectRequestNode(
                    name: "executeFirstEffect",
                    requestExpression: ctx =>
                    {
                        var first = ctx.RequireVariable<TransitionResult>("firstTransition");
                        return new ProcessRequestInvocation(
                            Request: Assert.Single(first.Effects),
                            ContinuationEntity: entityRef);
                    },
                    nextNode: "end"
                    ),
                new EndNode("end")
            ]);

        var ex = await Assert.ThrowsAsync<SemanticRuleViolationException>(
            () => engine.ExecuteAsync(context, process));

        Assert.Contains("snapshot token mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(deadLetters.DeadLetters);
    }

    [Fact]
    public async Task ExecuteAsync_WithLoggerFactory_EmitsProcessExecutionLogs()
    {
        var context = CreateOperationContext();
        var loggerFactory = new ListLoggerFactory();
        var engine = new ProcessEngine(CreateRuntimeServices(
            transitionHost: new DeclarativeTransitionHost(),
            loggerFactory: loggerFactory));

        var process = new ProcessDefinition(
            name: "LoggedProcess",
            entryNode: "compute",
            nodes:
            [
                new ComputeValueNode(
                    name: "compute",
                    valueExpression: _ => "hello",
                    resultVariable: "greeting",
                    nextNode: "end"
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<string>("greeting")
                    )
            ]);

        var run = await engine.ExecuteAsync(
            context,
            process,
            runOptions: new() { ProcessId = "proc-logging" });

        Assert.Equal("hello", run.Result);
        Assert.Contains(
            loggerFactory.Entries,
            x => x.Level == LogLevel.Information
                && x.Message.Contains("Starting process execution 'LoggedProcess' (proc-logging)", StringComparison.Ordinal));
        Assert.Contains(
            loggerFactory.Entries,
            x => x.Level == LogLevel.Debug
                && x.Message.Contains("Executing process node 'compute' (ComputeValueNode)", StringComparison.Ordinal));
        Assert.Contains(
            loggerFactory.Entries,
            x => x.Level == LogLevel.Information
                && x.Message.Contains("Completed process execution 'LoggedProcess' (proc-logging)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_PushesAmbientOperationContextToHandlers()
    {
        using var services = new ServiceCollection()
            .AddCohesiveOperationContext()
            .BuildServiceProvider();

        var operationContextFactory = services.GetRequiredService<IOperationContextFactory>();
        var operationContextAccessor = services.GetRequiredService<IOperationContextAccessor>();
        var operationContextScopeFactory = services.GetRequiredService<IOperationContextScopeFactory>();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")],
            authenticationType: "test"));
        var context = operationContextFactory
            .Create(principal: principal)
            .WithItem("CorrelationId", "corr-123");

        var handler = new CapturingPingHandler(operationContextAccessor);
        var engine = new ProcessEngine(CreateRuntimeServices(
                transitionHost: new DeclarativeTransitionHost(),
                operationContextScopeFactory: operationContextScopeFactory))
            .RegisterHandler(handler);

        var process = new ProcessDefinition(
            name: "AmbientContextPing",
            entryNode: "ping",
            nodes:
            [
                new ExecuteEffectRequestNode(
                    name: "ping",
                    requestExpression: _ => EffectRequest.Named(PingRequest.RequestName, new JsonObject()),
                    resultVariable: "ping",
                    nextNode: "end"
                    ),
                new EndNode(
                    name: "end",
                    resultExpression: ctx => ctx.RequireVariable<PingResponse>("ping").Message
                    )
            ]);

        Assert.Null(operationContextAccessor.Current);

        var run = await engine.ExecuteAsync(context, process);

        Assert.Equal("ambient-pong", run.Result);
        Assert.Null(operationContextAccessor.Current);
        Assert.Same(context, handler.ExplicitContext);
        Assert.Same(context, handler.AmbientContext);
        var ambientContext = Assert.IsType<OperationContext>(handler.AmbientContext);
        Assert.Equal("alice", ambientContext.Principal.Identity?.Name);
        Assert.True(ambientContext.TryGetItem<string>("CorrelationId", out var correlationId));
        Assert.Equal("corr-123", correlationId);
    }

    static EntityDefinition BuildMileageEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Route", route => route
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
                .Field(name: "PlannedMiles", type: DomainTypes.Decimal())
                .Transition(
                    name: "AddStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Add("Stops", Expr.Param("stopCode"))
                    )
                .Transition(
                    name: "ApplyMileage",
                    t => t
                        .Parameter(name: "TotalMiles", type: DomainTypes.Decimal(), isRequired: true)
                        .Set("PlannedMiles", Expr.Param("TotalMiles"))
                    )
            ));

        return Assert.Single(model.Entities);
    }

    static EntityDefinition BuildCounterEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Counter", counter => counter
                .Field(name: "Count", type: DomainTypes.Int32())
                .Transition(
                    name: "Increment",
                    t => t
                        .Parameter(name: "delta", type: DomainTypes.Int32(), isRequired: true)
                        .Set(
                            "Count",
                            valueExpression: new BinaryExpr(
                                BinaryOperator.Add,
                                Expr.Field("Count"),
                                Expr.Param("delta"))))
            ));

        return Assert.Single(model.Entities);
    }

    static EntityDefinition BuildIntentEntity()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity(name: "Route", route => route
                .Field(name: "Stops", type: DomainTypes.String(), configure: f => f.Many())
                .Field(name: "PlannedDistanceMiles", type: DomainTypes.Decimal())
                .Field(name: "MileageRevision", type: DomainTypes.Int32())
                .Transition(
                    name: "AddStop",
                    t => t
                        .Parameter(name: "stopCode", type: DomainTypes.String(), isRequired: true)
                        .Add("Stops", Expr.Param("stopCode"))
                        .Set(
                            "MileageRevision",
                            valueExpression: new BinaryExpr(
                                BinaryOperator.Add,
                                Expr.Field("MileageRevision"),
                                Expr.Const(1)))
                        .Emit(
                            name: IntentMileageRequest.RequestName,
                            payload: Expr.Call(
                                "object",
                                Expr.Const("mileageRevision"),
                                Expr.Field("MileageRevision"),
                                Expr.Const("stops"),
                                Expr.Field("Stops")),
                            continuationTransition: "ApplyMileage"))
                .Transition(
                    name: "ApplyMileage",
                    t => t
                        .Parameter(name: "mileageRevision", type: DomainTypes.Int32(), isRequired: true)
                        .Parameter(name: "totalMiles", type: DomainTypes.Decimal(), isRequired: true)
                        .Requires(
                            name: "RevisionMatches",
                            expression: Expr.Eq(
                                Expr.Field("MileageRevision"),
                                Expr.Param("mileageRevision")))
                        .Set("PlannedDistanceMiles", Expr.Param("totalMiles")))
            ));

        return Assert.Single(model.Entities);
    }

    static EntityState CreateInitialMileageState()
    {
        return BuildMileageEntity().CreateState(new
        {
            Stops = Array.Empty<string>(),
            PlannedMiles = 0m
        });
    }

    static EntityState CreateInitialCounterState()
    {
        return BuildCounterEntity().CreateState(new
        {
            Count = 0
        });
    }

    static EntityState CreateInitialIntentState()
    {
        return BuildIntentEntity().CreateState(new
        {
            Stops = new[] { "SFO" },
            PlannedDistanceMiles = 0m,
            MileageRevision = 0
        });
    }

    sealed record CalculateMileageRequest(string routeId) : IEffectRequest<MileageResult>
    {
        public static string RequestName => "CalculateMileage";
    }

    sealed record MileageResult(decimal TotalMiles);

    sealed class TrackingEntityRepository(IProcessEntityRepository inner) : IProcessEntityRepository
    {
        public int LoadCount { get; private set; }

        public Task<ProcessEntitySnapshot> Create(OperationContext context, ProcessEntityRef entity, EntityState state, string processId) =>
            inner.Create(context, entity, state, processId);

        public Task<ProcessEntitySnapshot> Get(OperationContext context, ProcessEntityRef entity, ProcessEntityReadOptions? options = null)
        {
            LoadCount++;
            return inner.Get(context, entity, options);
        }

        public Task Update(OperationContext context, ProcessEntityRef entity, TransitionResult transition, string processId, ProcessEntityWriteOptions options) =>
            inner.Update(context, entity, transition, processId, options);
    }

    sealed class StaticMileageHandler(decimal totalMiles) : IEffectHandler<CalculateMileageRequest, MileageResult>
    {
        public Task<MileageResult> HandleAsync(OperationContext context, CalculateMileageRequest request)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MileageResult(totalMiles));
        }
    }

    sealed record PingRequest() : IEffectRequest<PingResponse>
    {
        public static string RequestName => "Ping";
    }

    sealed record PingResponse(string Message);

    sealed record FormatGreetingRequest(string Name) : IEffectRequest<string>
    {
        public static string RequestName => "FormatGreeting";
    }

    sealed class PingHandler : IEffectHandler<PingRequest, PingResponse>
    {
        public Task<PingResponse> HandleAsync(OperationContext context, PingRequest request)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PingResponse("pong"));
        }
    }

    sealed class FormatGreetingHandler : IEffectHandler<FormatGreetingRequest, string>
    {
        public Task<string> HandleAsync(OperationContext context, FormatGreetingRequest request)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult($"hello {request.Name}");
        }
    }

    sealed record IntentMileageRequest(int mileageRevision, IReadOnlyList<string> stops)
        : IEffectRequest<IntentMileageResult>
    {
        public static string RequestName => "CalculateMileage";
    }

    sealed record IntentMileageResult(int mileageRevision, decimal totalMiles);

    sealed class IntentMileageHandler : IEffectHandler<IntentMileageRequest, IntentMileageResult>
    {
        public Task<IntentMileageResult> HandleAsync(OperationContext context, IntentMileageRequest request)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var totalMiles = request.stops.Count * 100m;
            return Task.FromResult(new IntentMileageResult(request.mileageRevision, totalMiles));
        }
    }

    sealed class CapturingPingHandler(IOperationContextAccessor operationContextAccessor)
        : IEffectHandler<PingRequest, PingResponse>
    {
        public OperationContext? ExplicitContext { get; private set; }

        public OperationContext? AmbientContext { get; private set; }

        public Task<PingResponse> HandleAsync(OperationContext context, PingRequest request)
        {
            ExplicitContext = context;
            AmbientContext = operationContextAccessor.Current;
            return Task.FromResult(new PingResponse("ambient-pong"));
        }
    }

    static OperationContext CreateOperationContext() => OperationContext.Create();

    static ProcessRuntimeServices CreateRuntimeServices(
        IProcessTransitionHost transitionHost,
        IProcessRuntimeStorage? runtimeStorage = null,
        IProcessWaitAdapter? waitAdapter = null,
        IProcessDeadLetterSink? deadLetterSink = null,
        IOperationContextScopeFactory? operationContextScopeFactory = null,
        ILoggerFactory? loggerFactory = null,
        ProcessEngineOptions? options = null)
    {
        var storage = runtimeStorage ?? new InMemoryProcessStorageAdapter();
        return new(
            transitionHost: transitionHost,
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: waitAdapter ?? new InMemoryProcessWaitAdapter(),
            deadLetterSink: deadLetterSink ?? new InMemoryProcessDeadLetterSink(),
            operationContextScopeFactory: operationContextScopeFactory,
            loggerFactory: loggerFactory,
            options: options);
    }

    static ProcessRuntimeServices CreateRuntimeServices(
        IProcessTransitionHost transitionHost,
        IProcessEntityRepository entityRepository,
        IProcessCheckpointRepository checkpointRepository,
        IProcessTransactionGateway transactionGateway,
        IProcessWaitAdapter? waitAdapter = null,
        IProcessDeadLetterSink? deadLetterSink = null,
        IOperationContextScopeFactory? operationContextScopeFactory = null,
        ILoggerFactory? loggerFactory = null,
        ProcessEngineOptions? options = null)
    {
        return new(
            transitionHost: transitionHost,
            entityRepository: entityRepository,
            checkpointRepository: checkpointRepository,
            transactionGateway: transactionGateway,
            waitAdapter: waitAdapter ?? new InMemoryProcessWaitAdapter(),
            deadLetterSink: deadLetterSink ?? new InMemoryProcessDeadLetterSink(),
            operationContextScopeFactory: operationContextScopeFactory,
            loggerFactory: loggerFactory,
            options: options);
    }

    sealed record TestLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    sealed class ListLoggerFactory : ILoggerFactory
    {
        readonly List<TestLogEntry> entries = [];

        public IReadOnlyList<TestLogEntry> Entries => entries;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, entries);

        public void Dispose()
        {
        }
    }

    sealed class ListLogger(string categoryName, List<TestLogEntry> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Add(new(categoryName, logLevel, formatter(state, exception), exception));
        }
    }

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

[GenerateProcessDefinition(nameof(Build))]
sealed partial class ConfigurableSuffixProcess(string suffix) : IProcessDefinition<string, string>
{
    async ProcessTask<string> Build(ProcessAuthoringContext<string, string> process, string input)
    {
        return process.Return(input + suffix);
    }
}

[GenerateProcessDefinition(nameof(Build))]
sealed partial class EarlyReturnProcess : IProcessDefinition<string, string>
{
    async ProcessTask<string> Build(ProcessAuthoringContext<string, string> process, string input)
    {
        if (input == "skip")
        {
            return process.Return("skipped");
        }

        var continued = await process.Compute(input + ":continued");
        return process.Return(continued);
    }
}

[GenerateProcessDefinition(nameof(Build))]
sealed partial class IfElseContinuationProcess : IProcessDefinition<string, string>
{
    async ProcessTask<string> Build(ProcessAuthoringContext<string, string> process, string input)
    {
        if (input == "left")
        {
            var leftBranch = await process.Compute("left-branch");
        }
        else
        {
            var rightBranch = await process.Compute("right-branch");
        }

        var finalValue = await process.Compute(input + ":done");
        return process.Return(finalValue);
    }
}
