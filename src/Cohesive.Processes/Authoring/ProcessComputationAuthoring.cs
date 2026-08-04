using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Processes.IR;

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
/// Typed Fork results and their tuple projection are also syntax-only: an all-branches Join exposes the existing
/// branch-local bindings, and the generator fuses each pure result expression into its post-Join consumers.
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

    /// <summary>Annotates one typed Fork branch with a canonical capacity-domain assignment.</summary>
    /// <typeparam name="TResult">CLR type projected from the branch result after an all-branches Join.</typeparam>
    /// <param name="branch">Syntax-only local branch computation.</param>
    /// <param name="capacityDomain">Stable capacity-domain identity declared by the Fork admission policy.</param>
    /// <returns>A syntax-only annotated branch consumed by a typed <c>ForkJoin</c> overload.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask<TResult> Branch<TResult>(
        ProcessTask<TResult> branch,
        string capacityDomain) =>
        throw SyntaxOnly();

    /// <summary>
    /// Declares parallel syntax-only branch computations converging through an all-branches Join.
    /// </summary>
    /// <remarks>
    /// Each argument must invoke a parameterless local <c>async</c> function returning <see cref="ProcessTask"/>.
    /// The generator lowers the functions to canonical Fork branches and an explicit deterministic Join policy;
    /// neither the branch functions nor their compiler state machines are retained by the generated definition.
    /// </remarks>
    /// <param name="branches">Two or more syntax-only branch computations.</param>
    /// <returns>A syntax-only task representing convergence at the reciprocal Join.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask ForkJoin(params ProcessTask[] branches) =>
        throw SyntaxOnly();

    /// <summary>
    /// Declares identified parallel syntax-only branch computations converging through an all-branches Join.
    /// </summary>
    /// <remarks>
    /// Each argument must invoke a parameterless local <c>async</c> function returning <see cref="ProcessTask"/>.
    /// The supplied identity belongs to the Fork; the reciprocal Join and branch identities are derived from it.
    /// </remarks>
    /// <param name="id">Explicit canonical Fork identity.</param>
    /// <param name="branches">Two or more syntax-only branch computations.</param>
    /// <returns>A syntax-only task representing convergence at the reciprocal Join.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask ForkJoin(ExecutionNodeId id, params ProcessTask[] branches) =>
        throw SyntaxOnly();

    /// <summary>Declares two typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting both results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2)> ForkJoin<T1, T2>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares three typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3)> ForkJoin<T1, T2, T3>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares four typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <typeparam name="T4">Fourth branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="branch4">Fourth typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3, T4)> ForkJoin<T1, T2, T3, T4>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessTask<T4> branch4,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares five typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <typeparam name="T4">Fourth branch result type.</typeparam>
    /// <typeparam name="T5">Fifth branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="branch4">Fourth typed branch.</param>
    /// <param name="branch5">Fifth typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3, T4, T5)> ForkJoin<T1, T2, T3, T4, T5>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessTask<T4> branch4,
        ProcessTask<T5> branch5,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares six typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <typeparam name="T4">Fourth branch result type.</typeparam>
    /// <typeparam name="T5">Fifth branch result type.</typeparam>
    /// <typeparam name="T6">Sixth branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="branch4">Fourth typed branch.</param>
    /// <param name="branch5">Fifth typed branch.</param>
    /// <param name="branch6">Sixth typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3, T4, T5, T6)> ForkJoin<T1, T2, T3, T4, T5, T6>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessTask<T4> branch4,
        ProcessTask<T5> branch5,
        ProcessTask<T6> branch6,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares seven typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <typeparam name="T4">Fourth branch result type.</typeparam>
    /// <typeparam name="T5">Fifth branch result type.</typeparam>
    /// <typeparam name="T6">Sixth branch result type.</typeparam>
    /// <typeparam name="T7">Seventh branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="branch4">Fourth typed branch.</param>
    /// <param name="branch5">Fifth typed branch.</param>
    /// <param name="branch6">Sixth typed branch.</param>
    /// <param name="branch7">Seventh typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3, T4, T5, T6, T7)> ForkJoin<T1, T2, T3, T4, T5, T6, T7>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessTask<T4> branch4,
        ProcessTask<T5> branch5,
        ProcessTask<T6> branch6,
        ProcessTask<T7> branch7,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares eight typed branches converging through an all-branches Join.</summary>
    /// <typeparam name="T1">First branch result type.</typeparam>
    /// <typeparam name="T2">Second branch result type.</typeparam>
    /// <typeparam name="T3">Third branch result type.</typeparam>
    /// <typeparam name="T4">Fourth branch result type.</typeparam>
    /// <typeparam name="T5">Fifth branch result type.</typeparam>
    /// <typeparam name="T6">Sixth branch result type.</typeparam>
    /// <typeparam name="T7">Seventh branch result type.</typeparam>
    /// <typeparam name="T8">Eighth branch result type.</typeparam>
    /// <param name="branch1">First typed branch.</param>
    /// <param name="branch2">Second typed branch.</param>
    /// <param name="branch3">Third typed branch.</param>
    /// <param name="branch4">Fourth typed branch.</param>
    /// <param name="branch5">Fifth typed branch.</param>
    /// <param name="branch6">Sixth typed branch.</param>
    /// <param name="branch7">Seventh typed branch.</param>
    /// <param name="branch8">Eighth typed branch.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable projecting all results in authored branch order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<(T1, T2, T3, T4, T5, T6, T7, T8)> ForkJoin<T1, T2, T3, T4, T5, T6, T7, T8>(
        ProcessTask<T1> branch1,
        ProcessTask<T2> branch2,
        ProcessTask<T3> branch3,
        ProcessTask<T4> branch4,
        ProcessTask<T5> branch5,
        ProcessTask<T6> branch6,
        ProcessTask<T7> branch7,
        ProcessTask<T8> branch8,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation-expression members are syntax-only and must be lowered by Cohesive.Analyzers.");
}

/// <summary>Authoring-only bounded Fork admission projected into canonical Process work limits.</summary>
/// <remarks>
/// This descriptor is consumed only while generating a definition. Generated code projects it into
/// <see cref="ProcessWorkLimits"/> and <see cref="ProcessCapacityDomainLimit"/> values; it is never persisted or
/// interpreted as a second admission authority.
/// </remarks>
public sealed record ProcessAdmission
{
    ProcessAdmission(
        int minimumParallelism,
        int maximumParallelism,
        int? maximumStartsPerActivation,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains)
    {
        if (minimumParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumParallelism), "Minimum parallelism must be positive.");
        if (maximumParallelism < minimumParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParallelism),
                "Maximum parallelism cannot be less than minimum parallelism.");
        }
        if (maximumStartsPerActivation is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStartsPerActivation),
                "Maximum starts per activation must be positive when supplied.");
        }

        MinimumParallelism = minimumParallelism;
        MaximumParallelism = maximumParallelism;
        MaximumStartsPerActivation = maximumStartsPerActivation;
        CapacityDomains = capacityDomains.IsDefault ? [] : capacityDomains;
    }

    /// <summary>Hard minimum permitted runtime admission operating point.</summary>
    public int MinimumParallelism { get; }

    /// <summary>Hard maximum permitted runtime admission operating point.</summary>
    public int MaximumParallelism { get; }

    /// <summary>Optional maximum branch starts per activation; omission uses the finite branch count.</summary>
    public int? MaximumStartsPerActivation { get; }

    /// <summary>Canonical capacity-domain limits assigned by annotated branches.</summary>
    public ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains { get; }

    /// <summary>Creates bounded authoring policy for a finite Fork.</summary>
    /// <param name="maximumParallelism">Hard maximum permitted runtime admission operating point.</param>
    /// <param name="minimumParallelism">Hard minimum permitted runtime admission operating point.</param>
    /// <param name="maximumStartsPerActivation">Optional positive per-activation branch-start limit.</param>
    /// <param name="capacityDomains">Optional canonical named capacity-domain limits.</param>
    /// <returns>An authoring descriptor projected into canonical Fork limits by the generator.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A supplied limit is not positive or the maximum is less than the minimum.
    /// </exception>
    public static ProcessAdmission Bounded(
        int maximumParallelism,
        int minimumParallelism = 1,
        int? maximumStartsPerActivation = null,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains = default) =>
        new(minimumParallelism, maximumParallelism, maximumStartsPerActivation, capacityDomains);
}

/// <summary>Concise authoring factories for canonical Process capacity values.</summary>
public static class ProcessCapacity
{
    /// <summary>Creates one canonical named capacity-domain limit.</summary>
    /// <param name="identity">Stable non-empty capacity-domain identity.</param>
    /// <param name="maximumParallelism">Positive maximum concurrent admitted work in the domain.</param>
    /// <returns>The canonical capacity-domain limit.</returns>
    public static ProcessCapacityDomainLimit Domain(string identity, int maximumParallelism) =>
        new(identity, maximumParallelism);
}

/// <summary>Task-like result used only to type-check a generated Process method or typed local Fork branch.</summary>
/// <typeparam name="TResult">CLR type of the Process terminal result or pure typed branch result.</typeparam>
[AsyncMethodBuilder(typeof(ProcessTaskMethodBuilder<>))]
public readonly struct ProcessTask<TResult>;

/// <summary>
/// Task-like branch computation used only to type-check local functions supplied to
/// <see cref="ProcessContext.ForkJoin(ProcessTask[])"/>.
/// </summary>
/// <remarks>
/// The source generator reads the local function bodies and emits canonical branch nodes. This value and its
/// compiler-generated state machine are never retained or executed by the generated Process definition.
/// </remarks>
[AsyncMethodBuilder(typeof(ProcessTaskMethodBuilder))]
public readonly struct ProcessTask : ICriticalNotifyCompletion
{
    /// <summary>Returns this syntax-only value as its awaiter.</summary>
    /// <returns>This syntax-only task.</returns>
    public ProcessTask GetAwaiter() => this;

    /// <summary>Indicates that syntax-only branch computations never complete at runtime.</summary>
    public bool IsCompleted => false;

    /// <summary>Rejects runtime completion of a syntax-only branch computation.</summary>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void GetResult() => throw SyntaxOnly();

    /// <summary>Rejects runtime scheduling of a syntax-only branch continuation.</summary>
    /// <param name="continuation">Runtime continuation that would have been scheduled.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void OnCompleted(Action continuation) => throw SyntaxOnly();

    /// <summary>Rejects unsafe runtime scheduling of a syntax-only branch continuation.</summary>
    /// <param name="continuation">Runtime continuation that would have been scheduled.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    public void UnsafeOnCompleted(Action continuation) => throw SyntaxOnly();

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation branches are syntax-only and must be lowered by Cohesive.Analyzers.");
}

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

/// <summary>Async method builder used only to type-check a syntax-only Process branch function.</summary>
public struct ProcessTaskMethodBuilder
{
    /// <summary>Creates a syntax-only Process branch task builder.</summary>
    /// <returns>A default syntax-only builder.</returns>
    public static ProcessTaskMethodBuilder Create() => default;

    /// <summary>Gets the syntax-only Process branch task.</summary>
    public ProcessTask Task => default;

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
            "Process computation branches are syntax-only and must be lowered by Cohesive.Analyzers.",
            exception);

    /// <summary>Accepts compiler-required branch completion without retaining runtime state.</summary>
    public void SetResult()
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
        "Process computation branches are syntax-only and must be lowered by Cohesive.Analyzers.");
}
