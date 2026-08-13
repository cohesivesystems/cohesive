using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class HostedQueryHandlerCatalogTests
{
    static readonly DateTimeOffset ObservedAtUtc = new(2026, 8, 13, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TypedHandler_DecodesExactInputAndEncodesCanonicalResult()
    {
        var query = Query();
        ProcessRelationEvaluation? observed = null;
        OperationContext? observedContext = null;
        var catalog = new HostedQueryHandlerCatalog([
            HostedQueryHandlerRegistration.Create(
                query,
                async (context, evaluation, input) =>
                {
                    await Task.Yield();
                    observedContext = context;
                    observed = evaluation;
                    return new QueryResult(input.Id, "resolved");
                })
        ]);
        var context = OperationContext.Create();
        var evaluation = Evaluation(query, new QueryInput("source/42"));

        var result = await catalog.EvaluateAsync(context, evaluation);

        Assert.True(result.IsSuccessful);
        Assert.Same(context, observedContext);
        Assert.Equal(evaluation, observed);
        Assert.Equal(query.ResultContract, result.Value!.Contract);
        Assert.Equal(
            ObservationValue.FromObject(new QueryResult("source/42", "resolved")),
            result.Value.Value);
    }

    [Fact]
    public async Task ChangedDefinitionFingerprint_CannotReachRegisteredHandler()
    {
        var deployed = Query(policy: "exact");
        var changed = Query(policy: "latest");
        var invocations = 0;
        var catalog = new HostedQueryHandlerCatalog([
            HostedQueryHandlerRegistration.Create(
                deployed,
                (context, evaluation, input) =>
                {
                    invocations++;
                    return ValueTask.FromResult(new QueryResult(input.Id, "unexpected"));
                })
        ]);

        var result = await catalog.EvaluateAsync(
            OperationContext.Create(),
            Evaluation(changed, new QueryInput("source/42")));

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, invocations);
        Assert.Equal(
            HostedQueryHandlerDiagnosticCodes.DefinitionFingerprintMismatch,
            result.Failure!.Code);
    }

    [Fact]
    public async Task ContractMismatch_ReturnsStructuredFailureBeforeHandlerExecution()
    {
        var query = Query();
        var invocations = 0;
        var catalog = new HostedQueryHandlerCatalog([
            HostedQueryHandlerRegistration.Create(
                query,
                (context, evaluation, input) =>
                {
                    invocations++;
                    return ValueTask.FromResult(new QueryResult(input.Id, "unexpected"));
                })
        ]);
        var evaluation = Evaluation(query, new QueryInput("source/42")) with
        {
            Input = PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString("source/42"))
        };

        var result = await catalog.EvaluateAsync(OperationContext.Create(), evaluation);

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, invocations);
        Assert.Equal(HostedQueryHandlerDiagnosticCodes.InputContractMismatch, result.Failure!.Code);
    }

    [Fact]
    public async Task CallerCancellation_ReachesNaturallyAsynchronousHandler()
    {
        var query = Query();
        CancellationToken observed = default;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new HostedQueryHandlerCatalog([
            HostedQueryHandlerRegistration.Create(
                query,
                async (context, evaluation, input) =>
                {
                    observed = context.CancellationToken;
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                    return new QueryResult(input.Id, "unreachable");
                })
        ]);
        using var cancellation = new CancellationTokenSource();
        var context = OperationContext.Create(cancellationToken: cancellation.Token);

        var pending = catalog.EvaluateAsync(
            context,
            Evaluation(query, new QueryInput("source/42"))).AsTask();
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, observed);
    }

    [Fact]
    public void Constructor_RejectsDuplicateAndConflictingExactDeployments()
    {
        var first = Query(policy: "exact");
        var changed = Query(policy: "latest");
        var registration = Registration(first);

        Assert.Throws<ArgumentException>(() => new HostedQueryHandlerCatalog([registration, registration]));
        Assert.Throws<ArgumentException>(() => new HostedQueryHandlerCatalog([
            registration,
            Registration(changed)
        ]));
    }

    static HostedQueryHandlerRegistration Registration(HostedQuery<QueryInput, QueryResult> query) =>
        HostedQueryHandlerRegistration.Create(
            query,
            static (context, evaluation, input) =>
                ValueTask.FromResult(new QueryResult(input.Id, "resolved")));

    internal static HostedQuery<QueryInput, QueryResult> Query(string policy = "exact") =>
        HostedQuery<QueryInput, QueryResult>.Create(
            new("query/tests/async-hosted-query"),
            new("1"),
            new("tests.async-hosted-query", "1"),
            new QueryConfiguration("entity", policy),
            Provenance());

    internal static ProcessRelationEvaluation Evaluation(
        HostedQuery<QueryInput, QueryResult> query,
        QueryInput input) => new(
            query.Reference,
            PortableValue.Concrete(query.InputContract, ObservationValue.FromObject(input)),
            Continuation(),
            new("activation/tests/async-hosted-query"),
            new("token/tests/async-hosted-query"),
            new("node/tests/async-hosted-query"),
            0,
            ObservedAtUtc,
            ActivationContext());

    internal static ProcessContinuationIdentity Continuation() => new(
        new("process/tests/async-hosted-query"),
        new("attempt/tests/async-hosted-query"));

    internal static ProcessActivationContext ActivationContext() => new(
        new("authority/tests", "tenant/acme"),
        new("correlation/tests/async-hosted-query"),
        new(
            InteractionDurabilityDemand.Durable,
            InteractionVisibilityDemand.AfterOriginCommit),
        Provenance());

    internal static ExecutionProvenance Provenance() => new(
        new("tests.async-hosted-query", "1"),
        new("tests/ari-371/async-hosted-query"),
        DocumentOrigin.User);

    public sealed record QueryInput(string Id);

    public sealed record QueryResult(string Id, string Value);

    public sealed record QueryConfiguration(string SourceFamily, string Policy);
}

public sealed class ProcessAsyncReferenceInterpreterTests
{
    [Fact]
    public async Task SynchronousCompatibilityAdapter_ForwardsEveryClosedOperationAndPrechecksCancellation()
    {
        var query = HostedQueryHandlerCatalogTests.Query();
        var evaluation = HostedQueryHandlerCatalogTests.Evaluation(
            query,
            new HostedQueryHandlerCatalogTests.QueryInput("source/42"));
        var invocation = new ProcessTransitionInvocation(
            query.Reference,
            query.Reference,
            evaluation.Input,
            evaluation.Input,
            evaluation.Continuation,
            evaluation.Activation,
            evaluation.Token,
            evaluation.Node,
            evaluation.Occurrence,
            evaluation.ObservedAtUtc,
            evaluation.Context);
        var resolution = new ProcessSignalTargetResolution(
            evaluation.Input,
            evaluation.Continuation,
            evaluation.Activation,
            evaluation.Token,
            evaluation.Node,
            evaluation.Occurrence,
            evaluation.ObservedAtUtc,
            evaluation.Context);
        var result = ProcessOperationResult.Completed(evaluation.Input);
        var target = new ProcessTokenInteractionTarget(evaluation.Continuation, evaluation.Token);
        var synchronous = new CapturingSynchronousHost(result, ProcessSignalTargetResult.Resolved(target));
        var adapter = new SynchronousProcessReferenceHostAdapter(synchronous);
        var context = OperationContext.Create();

        Assert.Equal(result, await adapter.InvokeTransitionAsync(context, invocation));
        Assert.Equal(result, await adapter.EvaluateRelationAsync(context, evaluation));
        Assert.Equal(target, (await adapter.ResolveSignalTargetAsync(context, resolution)).Target);
        Assert.Equal(invocation, synchronous.Transition);
        Assert.Equal(evaluation, synchronous.Relation);
        Assert.Equal(resolution, synchronous.SignalTarget);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await adapter.EvaluateRelationAsync(
                OperationContext.Create(cancellationToken: cancellation.Token),
                evaluation));
        Assert.Equal(1, synchronous.RelationInvocationCount);
    }

    [Fact]
    public async Task AsyncActivation_ReentersPureReducerWithoutRepeatingMaterializedOccurrences()
    {
        var query = HostedQueryHandlerCatalogTests.Query();
        var process = AsyncHostedQueryProcess.Define(Metadata());
        var compilation = process.Compile(new ProcessDefinitionValidationContext([
            query.CreateProcessDefinitionLink()
        ]));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var input = PortableValue.Concrete(
            process.Definition.Input,
            ObservationValue.FromObject(new HostedQueryHandlerCatalogTests.QueryInput("source/42")));
        var state = ProcessReferenceInterpreter.Create(
            plan,
            HostedQueryHandlerCatalogTests.Continuation(),
            input);
        var activation = new ProcessActivation(
            new("activation/tests/async-reference"),
            ProcessActivationCause.Start,
            new(2026, 8, 13, 22, 30, 0, TimeSpan.Zero),
            HostedQueryHandlerCatalogTests.ActivationContext());
        var expectedValue = PortableValue.Concrete(
            query.ResultContract,
            ObservationValue.FromObject(
                new HostedQueryHandlerCatalogTests.QueryResult("source/42", "resolved")));
        var synchronous = ProcessReferenceInterpreter.Activate(
            plan,
            state,
            activation,
            new SynchronousQueryHost(expectedValue));
        var asyncHost = new CountingAsyncQueryHost(expectedValue);

        var actual = await ProcessReferenceInterpreter.ActivateAsync(
            OperationContext.Create(),
            plan,
            state,
            activation,
            asyncHost);

        Assert.Equal(2, asyncHost.Evaluations.Count);
        Assert.Equal(2, asyncHost.Evaluations.Select(static evaluation => evaluation.Node).Distinct().Count());
        Assert.Equivalent(synchronous, actual, strict: true);
    }

    [Fact]
    public async Task AsyncActivation_CancellationProducesNoSemanticDecision()
    {
        var query = HostedQueryHandlerCatalogTests.Query();
        var process = AsyncHostedQueryProcess.Define(Metadata());
        var compilation = process.Compile(new ProcessDefinitionValidationContext([
            query.CreateProcessDefinitionLink()
        ]));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var input = PortableValue.Concrete(
            process.Definition.Input,
            ObservationValue.FromObject(new HostedQueryHandlerCatalogTests.QueryInput("source/42")));
        var state = ProcessReferenceInterpreter.Create(
            plan,
            HostedQueryHandlerCatalogTests.Continuation(),
            input);
        var activation = new ProcessActivation(
            new("activation/tests/async-reference-cancel"),
            ProcessActivationCause.Start,
            new(2026, 8, 13, 22, 45, 0, TimeSpan.Zero),
            HostedQueryHandlerCatalogTests.ActivationContext());
        using var cancellation = new CancellationTokenSource();
        var context = OperationContext.Create(cancellationToken: cancellation.Token);
        var host = new CancellingAsyncHost(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProcessReferenceInterpreter.ActivateAsync(context, plan, state, activation, host));

        Assert.Equal(1, host.InvocationCount);
        Assert.Equivalent(ProcessReferenceInterpreter.Create(plan, state.Continuation, input), state, strict: true);
    }

    static ProcessAuthoringMetadata Metadata() => new(
        new("process/tests/async-hosted-query"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        HostedQueryHandlerCatalogTests.Provenance());

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));

    sealed class SynchronousQueryHost(PortableValue result) : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("The test Process contains no Transition.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(result);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("The test Process contains no Signal.");
    }

    sealed class CapturingSynchronousHost(
        ProcessOperationResult result,
        ProcessSignalTargetResult signalResult) : IProcessReferenceHost
    {
        public ProcessTransitionInvocation? Transition { get; private set; }

        public ProcessRelationEvaluation? Relation { get; private set; }

        public ProcessSignalTargetResolution? SignalTarget { get; private set; }

        public int RelationInvocationCount { get; private set; }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            Transition = invocation;
            return result;
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            RelationInvocationCount++;
            Relation = evaluation;
            return result;
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            SignalTarget = resolution;
            return signalResult;
        }
    }

    sealed class CountingAsyncQueryHost(PortableValue result) : IAsyncProcessReferenceHost
    {
        public List<ProcessRelationEvaluation> Evaluations { get; } = [];

        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
            OperationContext context,
            ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("The test Process contains no Transition.");

        public async ValueTask<ProcessOperationResult> EvaluateRelationAsync(
            OperationContext context,
            ProcessRelationEvaluation evaluation)
        {
            await Task.Yield();
            Evaluations.Add(evaluation);
            return ProcessOperationResult.Completed(result);
        }

        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
            OperationContext context,
            ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("The test Process contains no Signal.");
    }

    sealed class CancellingAsyncHost(CancellationTokenSource cancellation) : IAsyncProcessReferenceHost
    {
        public int InvocationCount { get; private set; }

        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
            OperationContext context,
            ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("The test Process contains no Transition.");

        public async ValueTask<ProcessOperationResult> EvaluateRelationAsync(
            OperationContext context,
            ProcessRelationEvaluation evaluation)
        {
            InvocationCount++;
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            throw new InvalidOperationException("Cancellation did not reach the asynchronous host.");
        }

        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
            OperationContext context,
            ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("The test Process contains no Signal.");
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class AsyncHostedQueryProcess
{
    static async ProcessTask<HostedQueryHandlerCatalogTests.QueryResult> Run(
        ProcessContext process,
        HostedQueryHandlerCatalogTests.QueryInput input)
    {
        var first = await process.Query(
            HostedQueryHandlerCatalogTests.Query(),
            input,
            id: new("query/first"));
        var second = await process.Read(
            HostedQueryHandlerCatalogTests.Query(),
            input,
            id: new("query/second"));
        return second;
    }
}
