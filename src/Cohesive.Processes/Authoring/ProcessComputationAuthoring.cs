using System.Runtime.CompilerServices;
using Cohesive.Execution;

namespace Cohesive.Processes.Authoring;

/// <summary>
/// Marks a partial type whose syntax-only C# computation method is lowered to canonical Process IR.
/// </summary>
/// <remarks>
/// The named method is never executed. <c>Cohesive.Analyzers</c> reads its syntax and emits a <c>Define</c>
/// factory that constructs the canonical Process document through <see cref="ProcessBuilder{TInput,TResult}"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateProcessDefinitionAttribute : Attribute
{
    /// <summary>Creates Process computation-expression generation metadata.</summary>
    /// <param name="methodName">Name of the syntax-only authoring method declared by the annotated type.</param>
    /// <exception cref="ArgumentException"><paramref name="methodName"/> is empty or white space.</exception>
    public GenerateProcessDefinitionAttribute(string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        MethodName = methodName;
    }

    /// <summary>Name of the syntax-only authoring method lowered by the source generator.</summary>
    public string MethodName { get; }
}

/// <summary>
/// Syntax-only Process computation context recognized by the Cohesive source generator.
/// </summary>
/// <remarks>
/// Members return authoring awaitables solely so ordinary C# <c>async</c>/<c>await</c> syntax type-checks.
/// Invoking this surface at runtime is unsupported; generated code constructs canonical IR directly.
/// </remarks>
public sealed class ProcessContext
{
    ProcessContext()
    {
    }

    /// <summary>Declares evaluation of an exact Relation or Query and binds its result.</summary>
    /// <typeparam name="TResult">CLR type projected into the query result binding.</typeparam>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Pure query input fused into the canonical evaluation node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only awaitable whose result represents the query output.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Query<TResult>(
        ExecutionDefinitionReference relation,
        object? input,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>
    /// Declares an entity read represented by an exact Relation or Query definition and binds its result.
    /// </summary>
    /// <remarks>
    /// This is an authoring alias for canonical Relation evaluation. It does not introduce a separate entity-read
    /// execution model.
    /// </remarks>
    /// <typeparam name="TResult">CLR type projected into the read result binding.</typeparam>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Pure read input fused into the canonical evaluation node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only awaitable whose result represents the read output.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Read<TResult>(
        ExecutionDefinitionReference relation,
        object? input,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares invocation of an exact aggregate Transition and binds its outcome.</summary>
    /// <typeparam name="TResult">CLR type projected into the Transition outcome binding.</typeparam>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Pure authoritative aggregate-subject expression.</param>
    /// <param name="input">Pure Transition input fused into the canonical invocation node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only awaitable whose result represents the Transition outcome.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Transition<TResult>(
        ExecutionDefinitionReference transition,
        object subject,
        object? input,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares a durable Request effect with one selected terminal outcome and binds its result.</summary>
    /// <typeparam name="TResult">CLR type projected into the selected Request outcome binding.</typeparam>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="outcome">Terminal outcome that continues this sequential computation.</param>
    /// <param name="input">Pure Request payload fused into the canonical Request node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only awaitable whose result represents the selected Request outcome.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Effect<TResult>(
        RequestContractReference contract,
        RequestTerminalOutcomeId outcome,
        object? input,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation-expression members are syntax-only and must be lowered by Cohesive.Analyzers.");
}

/// <summary>Task-like result used only to type-check a generated Process computation method.</summary>
/// <typeparam name="TResult">CLR type of the Process terminal result.</typeparam>
[AsyncMethodBuilder(typeof(ProcessTaskMethodBuilder<>))]
public readonly struct ProcessTask<TResult>;

/// <summary>Awaitable placeholder used only while parsing a Process computation method.</summary>
/// <typeparam name="TResult">CLR type of the semantic value bound by <c>await</c>.</typeparam>
public readonly struct ProcessAwaitable<TResult>
{
    /// <summary>Returns the syntax-only awaiter.</summary>
    /// <returns>An awaiter that cannot be executed.</returns>
    public ProcessAwaiter<TResult> GetAwaiter() => default;
}

/// <summary>Awaiter that exists only so Process computation methods use ordinary C# <c>await</c> syntax.</summary>
/// <typeparam name="TResult">CLR type of the semantic value bound by <c>await</c>.</typeparam>
public readonly struct ProcessAwaiter<TResult> : ICriticalNotifyCompletion
{
    /// <summary>Indicates that syntax-only Process operations never complete at runtime.</summary>
    public bool IsCompleted => false;

    /// <summary>Rejects runtime execution of a syntax-only Process computation.</summary>
    /// <returns>This member never returns.</returns>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public TResult GetResult() => throw SyntaxOnly();

    /// <summary>Rejects runtime scheduling of a syntax-only Process continuation.</summary>
    /// <param name="continuation">Runtime continuation that would have been scheduled.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void OnCompleted(Action continuation) => throw SyntaxOnly();

    /// <summary>Rejects unsafe runtime scheduling of a syntax-only Process continuation.</summary>
    /// <param name="continuation">Runtime continuation that would have been scheduled.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void UnsafeOnCompleted(Action continuation) => throw SyntaxOnly();

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation awaitables are syntax-only and must be lowered by Cohesive.Analyzers.");
}

/// <summary>Async method builder used only to type-check a Process computation method.</summary>
/// <typeparam name="TResult">CLR type of the Process terminal result.</typeparam>
public struct ProcessTaskMethodBuilder<TResult>
{
    /// <summary>Creates a syntax-only Process task builder.</summary>
    /// <returns>A default syntax-only builder.</returns>
    public static ProcessTaskMethodBuilder<TResult> Create() => default;

    /// <summary>Gets the syntax-only Process task.</summary>
    public ProcessTask<TResult> Task => default;

    /// <summary>Rejects runtime execution of the generated async state machine.</summary>
    /// <typeparam name="TStateMachine">Compiler-generated async state-machine type.</typeparam>
    /// <param name="stateMachine">State machine that must never run.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine =>
        throw SyntaxOnly();

    /// <summary>Rejects runtime failure propagation from a syntax-only state machine.</summary>
    /// <param name="exception">Failure that would have completed the runtime task.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void SetException(Exception exception) =>
        throw new InvalidOperationException(
            "Process computation methods are syntax-only and must be lowered by Cohesive.Analyzers.",
            exception);

    /// <summary>Accepts the compiler-required terminal result without retaining runtime state.</summary>
    /// <param name="result">Compiler-produced terminal result; ignored because the method is never executed.</param>
    public void SetResult(TResult result)
    {
    }

    /// <summary>Rejects runtime scheduling through a safe awaiter.</summary>
    /// <typeparam name="TAwaiter">Awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">Compiler-generated async state-machine type.</typeparam>
    /// <param name="awaiter">Awaiter that must never execute.</param>
    /// <param name="stateMachine">State machine that must never resume.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        throw SyntaxOnly();

    /// <summary>Rejects runtime scheduling through an unsafe awaiter.</summary>
    /// <typeparam name="TAwaiter">Awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">Compiler-generated async state-machine type.</typeparam>
    /// <param name="awaiter">Awaiter that must never execute.</param>
    /// <param name="stateMachine">State machine that must never resume.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        throw SyntaxOnly();

    /// <summary>Accepts the compiler-required state-machine hook without retaining runtime state.</summary>
    /// <param name="stateMachine">Compiler-generated state machine; ignored.</param>
    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation methods are syntax-only and must be lowered by Cohesive.Analyzers.");
}
