using System.Runtime.CompilerServices;

namespace Cohesive.Processes.Model;

/// <summary>
/// A typed process definition provider returning a <see cref="TypedProcessDefinition{TInput, TOutput}"/>.
/// </summary>
/// <typeparam name="TInput">Process input type.</typeparam>
/// <typeparam name="TOutput">Process output type.</typeparam>
/// <remarks>
/// Implementations based on <see cref="ProcessTask{TOutput}"/> syntax can be annotated with <see cref="GenerateProcessDefinitionAttribute"/> to automatically generate the implementation of <see cref="Define"/>.
/// </remarks>
public interface IProcessDefinition<TInput, TOutput>
{
    /// <summary>
    /// Generated typed process-definition factory.
    /// </summary>
    /// <param name="processName">Optional process name override.</param>
    TypedProcessDefinition<TInput, TOutput> Define(string? processName = null);
}

/// <summary>
/// Marks a process-definition type for compile-time lowering into a <see cref="ProcessDefinition"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateProcessDefinitionAttribute : Attribute
{
    /// <summary>
    /// Creates process-definition generation metadata.
    /// </summary>
    /// <param name="methodName">Name of the authoring method to lower into a generated definition.</param>
    public GenerateProcessDefinitionAttribute(string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        MethodName = methodName;
    }

    /// <summary>
    /// Name of the authoring method to lower into a generated definition.
    /// </summary>
    public string MethodName { get; }
}

/// <summary>
/// Authoring-only process surface consumed by the process-definition source generator.
/// </summary>
/// <typeparam name="TInput">Primary process input type.</typeparam>
/// <typeparam name="TResult">Process result type.</typeparam>
public sealed class ProcessAuthoringContext<TInput, TResult>
{
    /// <summary>
    /// Declares the primary process input binding.
    /// </summary>
    public ProcessAwaitable<TInput> Input(string? name = null) => default;

    /// <summary>
    /// Declares an additional named process parameter binding.
    /// </summary>
    public ProcessAwaitable<TParameter> Parameter<TParameter>(string name) => default;

    /// <summary>
    /// Declares an effect request whose response becomes the next bound value in the authoring flow.
    /// </summary>
    public ProcessAwaitable<TResultStep> Request<TResultStep>(IEffectRequestPayload<TResultStep> request, string? nodeName = null, string? resultName = null, Func<ProcessExecutionContext, ProcessEntityRef>? continuationEntityExpression = null) => default;

    /// <summary>
    /// Declares a process-native entity read whose response becomes the next bound value.
    /// </summary>
    public ProcessAwaitable<TResultStep> Read<TResultStep>(ProcessEntityRead<TResultStep> read, string? nodeName = null, string? resultName = null) => default;

    /// <summary>
    /// Declares a process-native entity create operation whose response becomes the next bound value.
    /// </summary>
    public ProcessAwaitable<TResultStep> Create<TResultStep>(ProcessEntityCreate<TResultStep> create, string? nodeName = null, string? resultName = null) => default;

    /// <summary>
    /// Declares a process-native entity read by id and binds the loaded snapshot.
    /// </summary>
    public ProcessAwaitable<EntitySnapshot<TEntity>> Read<TEntity>(
        TEntity entity,
        string entityId,
        string? partitionKey = null,
        ProcessEntityReadOptions? read = null,
        string? nodeName = null,
        string? resultName = null)
        where TEntity : Entity
        => Read(
            read: ProcessEntityRead.ReadById(
                entity: entity,
                entityId: entityId,
                partitionKey: partitionKey,
                read: read),
            nodeName: nodeName,
            resultName: resultName);

    /// <summary>
    /// Declares an entity read by id and projects the loaded snapshot to a typed result.
    /// </summary>
    public ProcessAwaitable<TResultStep> Read<TEntity, TResultStep>(
        TEntity entity,
        string entityId,
        Func<EntitySnapshot<TEntity>, TResultStep> project,
        string? partitionKey = null,
        ProcessEntityReadOptions? read = null,
        string? nodeName = null,
        string? resultName = null)
        where TEntity : Entity
        => Read(
            read: entity.ReadById(
                entityId: entityId,
                project: project,
                partitionKey: partitionKey,
                read: read),
            nodeName: nodeName,
            resultName: resultName);

    /// <summary>
    /// Declares a process-native entity create by id and binds the created snapshot.
    /// </summary>
    public ProcessAwaitable<EntitySnapshot<TEntity>> Create<TEntity>(
        TEntity entity,
        string entityId,
        object? stateObject = null,
        string? partitionKey = null,
        long version = 0,
        string? nodeName = null,
        string? resultName = null)
        where TEntity : Entity
        => Create(
            create: ProcessEntityCreate.Create(
                entity: entity,
                entityId: entityId,
                stateObject: stateObject,
                partitionKey: partitionKey,
                version: version),
            nodeName: nodeName,
            resultName: resultName);

    /// <summary>
    /// Declares a process-native entity create by id and projects the created snapshot to a typed result.
    /// </summary>
    public ProcessAwaitable<TResultStep> Create<TEntity, TResultStep>(
        TEntity entity,
        string entityId,
        object? stateObject,
        Func<EntitySnapshot<TEntity>, TResultStep> project,
        string? partitionKey = null,
        long version = 0,
        string? nodeName = null,
        string? resultName = null)
        where TEntity : Entity
        => Create(
            create: entity.Create(
                entityId: entityId,
                stateObject: stateObject,
                project: project,
                partitionKey: partitionKey,
                version: version),
            nodeName: nodeName,
            resultName: resultName);

    /// <summary>
    /// Declares canonical relation/query evaluation and projects its in-process outcome before checkpoint capture.
    /// </summary>
    /// <typeparam name="TResultStep">Projected process value type.</typeparam>
    /// <param name="evaluation">Exact relation/query definition snapshots, inputs, demand, and evaluation identity.</param>
    /// <param name="projectResult">
    /// Required pure projection from the non-wire evaluation outcome to an application-owned checkpoint value.
    /// </param>
    /// <param name="nodeName">Optional stable process-node name.</param>
    /// <param name="resultName">Optional variable name receiving the projected result.</param>
    /// <returns>An authoring-only awaitable representing the projected checkpoint value.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evaluation"/> or <paramref name="projectResult"/> is <see langword="null"/> when the
    /// generated process definition is constructed.
    /// </exception>
    public ProcessAwaitable<TResultStep> Evaluate<TResultStep>(
        RelationQueryEvaluation evaluation,
        Func<RelationQueryEvaluationOutcome, TResultStep> projectResult,
        string? nodeName = null,
        string? resultName = null) => default;

    /// <summary>
    /// Declares a pure computation whose result becomes the next bound value.
    /// </summary>
    public ProcessAwaitable<TResultStep> Compute<TResultStep>(TResultStep value, string? nodeName = null, string? resultName = null) => default;

    /// <summary>
    /// Declares a typed entity transition execution.
    /// </summary>
    public ProcessAwaitable<TransitionResult> Transition(ProcessEntityTransitionInvocation transition, string? nodeName = null, string? resultName = null) => default;

    /// <summary>
    /// Declares a typed entity transition execution directly from transition parameters.
    /// </summary>
    public ProcessAwaitable<TransitionResult> Transition<TEntity, TTransitionInput>(
        Transition<TEntity, TTransitionInput> transition,
        string entityId,
        TTransitionInput input,
        string? partitionKey = null,
        ProcessEffectSchedulingMode effectScheduling = ProcessEffectSchedulingMode.AutoDispatch,
        string? nodeName = null,
        string? resultName = null)
        where TEntity : Entity
        => Transition(
            transition: ProcessEntityTransition.For(transition: transition,
                entityId: entityId,
                input: input,
                partitionKey: partitionKey, effectScheduling: effectScheduling),
            nodeName: nodeName,
            resultName: resultName);

    /// <summary>
    /// Declares a timer wait that resumes after the supplied delay.
    /// </summary>
    public ProcessAwaitable<ProcessTimerFired> Timer(
        TimeSpan delay,
        string? key = null,
        string? nodeName = null,
        string? resultName = null) => default;

    /// <summary>
    /// Declares a polling loop that repeatedly issues the same effect request until the result is terminal or the timeout elapses.
    /// </summary>
    public ProcessAwaitable<TResultStep> Poll<TResultStep>(
        IEffectRequestPayload<TResultStep> request,
        Func<TResultStep, bool> isCompleted,
        TimeSpan interval,
        TimeSpan timeout,
        TResultStep timeoutResult,
        string? nodeName = null,
        string? resultName = null) => default;

    /// <summary>
    /// Declares a batch of typed entity transition executions.
    /// </summary>
    public ProcessAwaitable<IReadOnlyList<TransitionResult>> TransitionMany(ProcessEntityTransitionBatch transitions, string? nodeName = null, string? resultName = null) => default;

    /// <summary>
    /// Completes the authoring flow with the final process result.
    /// </summary>
    public TResult Return(TResult result, string? nodeName = null) => result;
}

/// <summary>
/// Task-like authoring result used only so async process-definition methods compile.
/// </summary>
/// <typeparam name="TResult">Authoring result type.</typeparam>
[AsyncMethodBuilder(typeof(ProcessTaskMethodBuilder<>))]
public readonly struct ProcessTask<TResult>;

/// <summary>
/// Awaitable binding placeholder used only during process authoring.
/// </summary>
/// <typeparam name="T">Bound value type.</typeparam>
public readonly struct ProcessAwaitable<T>
{
    /// <summary>
    /// Returns the authoring awaiter.
    /// </summary>
    public ProcessAwaiter<T> GetAwaiter() => default;
}

/// <summary>
/// Awaiter used only so authoring methods can use <c>await</c> syntax.
/// </summary>
/// <typeparam name="T">The awaited value type.</typeparam>
public readonly struct ProcessAwaiter<T> : ICriticalNotifyCompletion
{
    /// <summary>
    /// Always returns <c>false</c>; authoring awaiters are never executed at runtime.
    /// </summary>
    public bool IsCompleted => false;

    /// <summary>
    /// Throws because authoring awaiters are syntax-only.
    /// </summary>
    public T GetResult() =>
        throw new InvalidOperationException("Process authoring awaitables are syntax-only and must be lowered by source generation.");

    /// <summary>
    /// Throws because authoring awaiters are syntax-only.
    /// </summary>
    public void OnCompleted(Action continuation) =>
        throw new InvalidOperationException("Process authoring awaitables are syntax-only and must be lowered by source generation.");

    /// <summary>
    /// Throws because authoring awaiters are syntax-only.
    /// </summary>
    public void UnsafeOnCompleted(Action continuation) =>
        throw new InvalidOperationException("Process authoring awaitables are syntax-only and must be lowered by source generation.");
}

/// <summary>
/// Async method builder used only so process authoring methods compile.
/// </summary>
/// <typeparam name="TResult">Authoring result type.</typeparam>
public struct ProcessTaskMethodBuilder<TResult>
{
    /// <summary>
    /// Creates the authoring method builder.
    /// </summary>
    public static ProcessTaskMethodBuilder<TResult> Create() => default;

    /// <summary>
    /// Returns the syntax-only process task.
    /// </summary>
    public ProcessTask<TResult> Task => default;

    /// <summary>
    /// Throws because authoring methods are not executable at runtime.
    /// </summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        throw new InvalidOperationException("Process authoring methods are syntax-only and must be lowered by source generation.");

    /// <summary>
    /// Throws because authoring methods are not executable at runtime.
    /// </summary>
    public void SetException(Exception exception) =>
        throw new InvalidOperationException("Process authoring methods are syntax-only and must be lowered by source generation.", exception);

    /// <summary>
    /// Completes the syntax-only authoring task.
    /// </summary>
    public void SetResult(TResult result)
    {
    }

    /// <summary>
    /// Throws because authoring methods are not executable at runtime.
    /// </summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        throw new InvalidOperationException("Process authoring methods are syntax-only and must be lowered by source generation.");

    /// <summary>
    /// Throws because authoring methods are not executable at runtime.
    /// </summary>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        throw new InvalidOperationException("Process authoring methods are syntax-only and must be lowered by source generation.");

    /// <summary>
    /// No-op for syntax-only authoring tasks.
    /// </summary>
    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }
}
