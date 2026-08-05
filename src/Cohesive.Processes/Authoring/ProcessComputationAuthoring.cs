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
    /// <param name="nextRole">Stable semantic role of the successful continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the query result binding.</param>
    /// <returns>A syntax-only awaitable whose result represents the query output.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Query<TResult>(
        ExecutionDefinitionReference relation,
        object? input,
        ExecutionNodeId? id = null,
        string nextRole = "next",
        string outputRole = "result") =>
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
    /// <param name="nextRole">Stable semantic role of the successful continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the read result binding.</param>
    /// <returns>A syntax-only awaitable whose result represents the read output.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Read<TResult>(
        ExecutionDefinitionReference relation,
        object? input,
        ExecutionNodeId? id = null,
        string nextRole = "next",
        string outputRole = "result") =>
        throw SyntaxOnly();

    /// <summary>Declares invocation of an exact aggregate Transition and binds its outcome.</summary>
    /// <typeparam name="TResult">CLR type projected into the Transition outcome binding.</typeparam>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Pure authoritative aggregate-subject expression.</param>
    /// <param name="input">Pure Transition input fused into the canonical invocation node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <param name="nextRole">Stable semantic role of the completed continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the Transition outcome binding.</param>
    /// <returns>A syntax-only awaitable whose result represents the Transition outcome.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> Transition<TResult>(
        ExecutionDefinitionReference transition,
        object subject,
        object? input,
        ExecutionNodeId? id = null,
        string nextRole = "next",
        string outputRole = "result") =>
        throw SyntaxOnly();

    /// <summary>Declares an exact aggregate Transition whose outcome is not retained.</summary>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Pure authoritative aggregate-subject expression.</param>
    /// <param name="input">Pure Transition input fused into the canonical invocation node.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <param name="nextRole">Stable semantic role of the completed continuation edge.</param>
    /// <returns>A syntax-only task representing Transition completion.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Transition(
        ExecutionDefinitionReference transition,
        object subject,
        object? input,
        ExecutionNodeId? id = null,
        string nextRole = "next") =>
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

    /// <summary>Declares a durable Request whose terminal outcomes select typed source-only branches.</summary>
    /// <remarks>
    /// Every outcome branch must be a named local <c>async ProcessTask</c> function. The generator erases the CLR
    /// branch functions and lowers their bodies to canonical Request continuations.
    /// </remarks>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="input">Pure Request payload fused into the canonical Request node.</param>
    /// <param name="outcomes">Closed terminal-outcome branch set.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only task representing completion of the selected terminal-outcome branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Effect(
        RequestContractReference contract,
        object? input,
        ProcessRequestOutcomeCase[] outcomes,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one typed terminal-outcome branch of a durable Request.</summary>
    /// <typeparam name="TOutcome">CLR type projected from the exact terminal-outcome contract.</typeparam>
    /// <param name="outcome">Stable terminal-outcome identity declared by the Request contract.</param>
    /// <param name="branch">Named source-only local branch receiving the selected outcome value.</param>
    /// <param name="id">Optional explicit canonical outcome-branch identity.</param>
    /// <param name="role">Stable semantic role of the selected outcome continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the selected continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the selected outcome binding.</param>
    /// <param name="outputOwner">Optional durable owner of the selected outcome binding.</param>
    /// <returns>An opaque syntax-only Request outcome consumed by <see cref="Effect(RequestContractReference, object?, ProcessRequestOutcomeCase[], ExecutionNodeId?)"/>.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessRequestOutcomeCase Outcome<TOutcome>(
        RequestTerminalOutcomeId outcome,
        ProcessOutcomeBranch<TOutcome> branch,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null,
        string outputRole = "result",
        ExecutionNodeId? outputOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares a terminal-outcome branch that does not retain the Request outcome payload.</summary>
    /// <param name="outcome">Stable terminal-outcome identity declared by the Request contract.</param>
    /// <param name="branch">Named source-only local branch selected for this outcome.</param>
    /// <param name="id">Optional explicit canonical outcome-branch identity.</param>
    /// <param name="role">Stable semantic role of the selected outcome continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the selected continuation edge.</param>
    /// <returns>An opaque syntax-only Request outcome consumed by <see cref="Effect(RequestContractReference, object?, ProcessRequestOutcomeCase[], ExecutionNodeId?)"/>.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessRequestOutcomeCase Outcome(
        RequestTerminalOutcomeId outcome,
        ProcessBranch branch,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one exact child Process invocation through the canonical Request/Reply protocol.</summary>
    /// <remarks>
    /// Every outcome branch must be a named local <c>async ProcessTask</c> function. The generator lowers the
    /// invocation directly to <see cref="InvokeProcessProcessNode"/> and erases the CLR branch functions.
    /// </remarks>
    /// <param name="process">Exact child Process definition revision and fingerprint.</param>
    /// <param name="contract">Exact Request contract used to durably start and join the child.</param>
    /// <param name="outcomeMapping">Total mapping from child terminal status to Request outcome identity.</param>
    /// <param name="input">Pure child input and Request payload fused into the canonical invocation.</param>
    /// <param name="purpose">Explicit work, compensation, or reconciliation purpose.</param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="outcomes">Closed terminal-outcome branch set.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only task representing completion of the selected child terminal-outcome branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask InvokeProcess(
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        object? input,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ProcessRequestOutcomeCase[] outcomes,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares a durable absolute-time wait.</summary>
    /// <param name="dueAt">Pure expression yielding the absolute due instant.</param>
    /// <param name="id">Optional explicit canonical timer identity.</param>
    /// <returns>A syntax-only task representing durable timer completion.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Timer(DateTimeOffset dueAt, ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one domain-event clause of a durable AwaitMatch.</summary>
    /// <typeparam name="TInput">CLR type projected from the exact event payload contract.</typeparam>
    /// <param name="contract">Exact domain-event contract.</param>
    /// <param name="branch">Named source-only local branch receiving the admitted event payload.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="when">Optional inline portable eligibility predicate.</param>
    /// <param name="id">Optional explicit canonical clause identity.</param>
    /// <param name="role">Stable semantic role of the winning continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the winning continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the admitted input binding.</param>
    /// <param name="outputOwner">Optional durable owner of the admitted input binding.</param>
    /// <returns>An opaque syntax-only AwaitMatch clause.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitCase Event<TInput>(
        DomainEventContractReference contract,
        ProcessInteractionBranch<TInput> branch,
        int priority = 0,
        ProcessGuard<TInput>? when = null,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null,
        string outputRole = "input",
        ExecutionNodeId? outputOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one Signal clause of a durable AwaitMatch.</summary>
    /// <typeparam name="TInput">CLR type projected from the exact Signal payload contract.</typeparam>
    /// <param name="contract">Exact Signal contract.</param>
    /// <param name="branch">Named source-only local branch receiving the admitted Signal payload.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="when">Optional inline portable eligibility predicate.</param>
    /// <param name="id">Optional explicit canonical clause identity.</param>
    /// <param name="role">Stable semantic role of the winning continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the winning continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the admitted input binding.</param>
    /// <param name="outputOwner">Optional durable owner of the admitted input binding.</param>
    /// <returns>An opaque syntax-only AwaitMatch clause.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitCase Signal<TInput>(
        SignalContractReference contract,
        ProcessInteractionBranch<TInput> branch,
        int priority = 0,
        ProcessGuard<TInput>? when = null,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null,
        string outputRole = "input",
        ExecutionNodeId? outputOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one inbound Request clause of a durable AwaitMatch.</summary>
    /// <typeparam name="TInput">CLR type projected from the exact Request payload contract.</typeparam>
    /// <param name="contract">Exact inbound Request contract.</param>
    /// <param name="branch">Named source-only local branch receiving the payload and retained Request obligation.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="when">Optional inline portable eligibility predicate.</param>
    /// <param name="id">Optional explicit canonical clause identity.</param>
    /// <param name="role">Stable semantic role of the winning continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the winning continuation edge.</param>
    /// <param name="outputRole">Stable semantic role of the admitted input binding.</param>
    /// <param name="outputOwner">Optional durable owner of the admitted input binding.</param>
    /// <returns>An opaque syntax-only AwaitMatch clause.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitCase Request<TInput>(
        RequestContractReference contract,
        ProcessRequestBranch<TInput> branch,
        int priority = 0,
        ProcessGuard<TInput>? when = null,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null,
        string outputRole = "input",
        ExecutionNodeId? outputOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one absolute-time clause of a durable AwaitMatch.</summary>
    /// <param name="dueAt">Pure expression yielding the absolute due instant.</param>
    /// <param name="branch">Named parameterless source-only local branch selected when the timer wins.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="id">Optional explicit canonical clause identity.</param>
    /// <param name="role">Stable semantic role of the winning continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the winning continuation edge.</param>
    /// <returns>An opaque syntax-only AwaitMatch clause.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitCase Deadline(
        DateTimeOffset dueAt,
        ProcessBranch branch,
        int priority = 0,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares a durable exclusive race over a closed set of typed interaction and timer clauses.</summary>
    /// <param name="clauses">Closed set of source-only AwaitMatch clauses.</param>
    /// <param name="arbitration">Explicit canonical winner-selection semantics.</param>
    /// <param name="lateInput">Disposition for input arriving after a winner completed the wait.</param>
    /// <param name="staleInput">Disposition for input targeting incompatible continuation state.</param>
    /// <param name="duplicateInput">Disposition for repeated logical input.</param>
    /// <param name="missingTarget">Disposition when no compatible durable target can be resolved.</param>
    /// <param name="retentionHorizon">Minimum duration for which the wait remains addressable.</param>
    /// <param name="id">Optional explicit canonical AwaitMatch identity.</param>
    /// <returns>A syntax-only task representing completion of the selected clause branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask AwaitMatch(
        ProcessAwaitCase[] clauses,
        ProcessAwaitArbitration arbitration,
        ProcessAwaitInputDisposition lateInput,
        ProcessAwaitInputDisposition staleInput,
        ProcessAwaitInputDisposition duplicateInput,
        ProcessAwaitMissingTargetDisposition missingTarget,
        TimeSpan retentionHorizon,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares a typed Reply discharging an admitted inbound Request obligation.</summary>
    /// <param name="contract">Exact typed Reply contract.</param>
    /// <param name="request">Request obligation received by the selected inbound Request branch.</param>
    /// <param name="payload">Pure Reply payload.</param>
    /// <param name="id">Optional explicit canonical Reply identity.</param>
    /// <returns>A syntax-only task representing acceptance of the Reply intent.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Reply(
        ReplyContractReference contract,
        ProcessRequestObligation request,
        object? payload,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares an explicitly ordered portable predicate Choice.</summary>
    /// <remarks>
    /// Each arm and optional fallback must name a parameterless local <c>async ProcessTask</c> function. This form
    /// exists for policies that ordinary C# <c>if</c>/<c>else</c> cannot state explicitly; it lowers to the same
    /// canonical Choice node and does not retain delegates or callbacks.
    /// </remarks>
    /// <param name="selection">Explicit canonical case-selection policy.</param>
    /// <param name="completeness">Explicit canonical coverage declaration.</param>
    /// <param name="cases">One or more predicate arms in semantic selection order.</param>
    /// <param name="fallback">Optional named fallback branch; required exactly when completeness is fallback.</param>
    /// <param name="id">Optional explicit canonical Choice identity.</param>
    /// <param name="fallbackId">Optional explicit canonical fallback identity.</param>
    /// <param name="fallbackRole">Stable semantic role of the fallback continuation edge.</param>
    /// <param name="fallbackEdgeOwner">Optional durable owner of the fallback continuation edge.</param>
    /// <returns>A syntax-only task representing convergence of the selected branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Choice(
        CaseSelection selection,
        BranchCompleteness completeness,
        ProcessChoiceArm[] cases,
        ProcessBranch? fallback = null,
        ExecutionNodeId? id = null,
        ExecutionNodeId? fallbackId = null,
        string fallbackRole = "next",
        ExecutionNodeId? fallbackEdgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one ordered predicate arm of an explicit Choice.</summary>
    /// <param name="predicate">Portable Boolean expression deciding whether this arm is eligible.</param>
    /// <param name="branch">Named parameterless local Process branch.</param>
    /// <param name="id">Optional explicit canonical case identity.</param>
    /// <param name="role">Stable semantic role of the selected case continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the selected case continuation edge.</param>
    /// <returns>An opaque syntax-only Choice arm consumed by <see cref="Choice"/>.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessChoiceArm When(
        bool predicate,
        ProcessBranch branch,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares an explicitly typed and ordered exact-value Match.</summary>
    /// <remarks>
    /// Patterns must be exact portable values and every branch must name a parameterless local
    /// <c>async ProcessTask</c> function. Ordinary exact C# <c>switch</c> remains the concise convention form;
    /// this method exposes policies that cannot be inferred safely from that syntax.
    /// </remarks>
    /// <typeparam name="TValue">Portable matched-value and exact-pattern CLR type.</typeparam>
    /// <param name="value">Portable value expression to match.</param>
    /// <param name="selection">Explicit canonical case-selection policy.</param>
    /// <param name="completeness">Explicit canonical coverage declaration.</param>
    /// <param name="cases">One or more exact-value arms in semantic selection order.</param>
    /// <param name="fallback">Optional named fallback branch; required exactly when completeness is fallback.</param>
    /// <param name="id">Optional explicit canonical Match identity.</param>
    /// <param name="fallbackId">Optional explicit canonical fallback identity.</param>
    /// <param name="fallbackRole">Stable semantic role of the fallback continuation edge.</param>
    /// <param name="fallbackEdgeOwner">Optional durable owner of the fallback continuation edge.</param>
    /// <returns>A syntax-only task representing convergence of the selected branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Match<TValue>(
        TValue value,
        CaseSelection selection,
        BranchCompleteness completeness,
        ProcessMatchArm<TValue>[] cases,
        ProcessBranch? fallback = null,
        ExecutionNodeId? id = null,
        ExecutionNodeId? fallbackId = null,
        string fallbackRole = "next",
        ExecutionNodeId? fallbackEdgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one ordered exact-value arm of an explicit Match.</summary>
    /// <typeparam name="TValue">Portable matched-value and exact-pattern CLR type.</typeparam>
    /// <param name="pattern">Exact portable pattern value.</param>
    /// <param name="branch">Named parameterless local Process branch.</param>
    /// <param name="id">Optional explicit canonical case identity.</param>
    /// <param name="role">Stable semantic role of the selected case continuation edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the selected case continuation edge.</param>
    /// <returns>An opaque syntax-only Match arm consumed by <see cref="Match{TValue}"/>.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessMatchArm<TValue> Case<TValue>(
        TValue pattern,
        ProcessBranch branch,
        ExecutionNodeId? id = null,
        string role = "next",
        ExecutionNodeId? edgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Marks one returned value as a successful terminal with an optional durable identity.</summary>
    /// <typeparam name="TResult">Portable Process result type.</typeparam>
    /// <param name="result">Pure successful terminal result.</param>
    /// <param name="id">Optional explicit canonical terminal identity.</param>
    /// <returns>The syntax-only value consumed by a C# <c>return</c> statement.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public TResult Complete<TResult>(TResult result, ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Marks one returned value as a failed terminal with an optional durable identity.</summary>
    /// <typeparam name="TResult">Portable Process failure-result type.</typeparam>
    /// <param name="result">Pure failed terminal result.</param>
    /// <param name="id">Optional explicit canonical terminal identity.</param>
    /// <returns>The syntax-only value consumed by a C# <c>return</c> statement.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public TResult Fail<TResult>(TResult result, ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Marks the source position after an exhaustive terminal branch set as unreachable.</summary>
    /// <remarks>
    /// This source-only marker satisfies C# definite-return analysis after an explicit <see cref="Match{TValue}"/>
    /// or <see cref="Choice"/> whose every branch terminates the Process. The generator erases the marker; it does
    /// not create an IR node or a runtime failure path.
    /// </remarks>
    /// <typeparam name="TResult">Portable root Process result type required by the containing C# method.</typeparam>
    /// <returns>No value; the marker is erased by source generation.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public TResult Unreachable<TResult>() => throw SyntaxOnly();

    /// <summary>Terminates a result-less local branch with a successful root Process result.</summary>
    /// <typeparam name="TResult">Portable root Process result type.</typeparam>
    /// <param name="result">Pure successful terminal result.</param>
    /// <param name="id">Optional explicit canonical terminal identity.</param>
    /// <returns>A syntax-only terminal task.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Succeed<TResult>(TResult result, ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Terminates a result-less local branch with a failed root Process result.</summary>
    /// <typeparam name="TResult">Portable root Process failure-result type.</typeparam>
    /// <param name="result">Pure failed terminal result.</param>
    /// <param name="id">Optional explicit canonical terminal identity.</param>
    /// <returns>A syntax-only terminal task.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Terminate<TResult>(TResult result, ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Transfers a branch back to an existing durable Process node.</summary>
    /// <remarks>
    /// This source-only construct expresses durable re-entry such as a hold decision returning to the same
    /// <see cref="AwaitMatch"/>. The generator emits no node for the transfer; the selecting continuation targets
    /// <paramref name="target"/> directly.
    /// </remarks>
    /// <param name="target">Exact existing canonical node selected as the branch continuation.</param>
    /// <returns>A syntax-only terminal branch task.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask ContinueAt(ExecutionNodeId target) => throw SyntaxOnly();

    /// <summary>Declares finite, explicitly bounded child Process work over a portable partition collection.</summary>
    /// <remarks>
    /// Projection lambdas are inline pure source syntax. They are fused into the canonical partition node and are
    /// never retained as delegates or host enumeration state. Successful completion continues after this await;
    /// the named failure branch lowers to the canonical failed edge and then rejoins the following computation.
    /// </remarks>
    /// <typeparam name="TPartition">Portable type of one partition element.</typeparam>
    /// <typeparam name="TChildInput">Portable input type of each exact child Process.</typeparam>
    /// <param name="partitions">Pure expression producing one finite portable Array value.</param>
    /// <param name="progressIdentity">Inline pure projection producing a stable non-empty identity per partition.</param>
    /// <param name="process">Exact child Process definition used for every partition.</param>
    /// <param name="contract">Exact Request contract used to durably start and join each child.</param>
    /// <param name="outcomeMapping">Total mapping from child terminal status to Request outcome identity.</param>
    /// <param name="childInput">Inline pure projection producing each child Process input.</param>
    /// <param name="limits">Explicit total-item, per-activation start, and parallelism limits.</param>
    /// <param name="failure">Explicit sibling-admission behavior after a child failure.</param>
    /// <param name="capacityIdentity">Optional inline pure projection assigning each partition to a capacity domain.</param>
    /// <param name="capacityDomains">Canonical capacity-domain limits.</param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="failed">Named source-only branch selected when bounded work reaches its failed outcome.</param>
    /// <param name="id">Optional explicit canonical node identity.</param>
    /// <returns>A syntax-only task representing settlement of the bounded partition occurrence.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask ForEachPartition<TPartition, TChildInput>(
        IReadOnlyList<TPartition> partitions,
        ProcessProjection<TPartition, string> progressIdentity,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        ProcessProjection<TPartition, TChildInput> childInput,
        ProcessWorkLimits limits,
        ProcessPartitionFailurePolicy failure,
        ProcessProjection<TPartition, string>? capacityIdentity,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains,
        ProcessChildCancellationPolicy cancellation,
        ProcessBranch failed,
        ExecutionNodeId? id = null) =>
        throw SyntaxOnly();

    /// <summary>Declares one finite recurrence whose occurrences are separated by durable activation cuts.</summary>
    /// <remarks>
    /// <paramref name="occurrence"/> must invoke one parameterless local <c>async ProcessTask&lt;TResult&gt;</c>
    /// function. The inline termination and progress projections are fused into the canonical recurrence node;
    /// neither the local function nor the projection delegates survive in persisted IR. A deliberate delay may be
    /// authored with <see cref="Timer(DateTimeOffset, ExecutionNodeId?)"/> inside the occurrence body.
    /// </remarks>
    /// <typeparam name="TResult">Portable result produced by each occurrence and retained after completion.</typeparam>
    /// <typeparam name="TProgress">Portable progress-evidence type compared across occurrences.</typeparam>
    /// <param name="occurrence">Invocation of one typed local Process occurrence body.</param>
    /// <param name="continueWhen">Inline pure projection returning <see langword="true"/> when another occurrence is required.</param>
    /// <param name="progress">Inline pure projection producing deterministic progress evidence.</param>
    /// <param name="policy">Explicit total-occurrence and unchanged-progress limits.</param>
    /// <param name="exhausted">Named branch selected when the total occurrence limit is reached.</param>
    /// <param name="stalled">Named branch selected when progress remains unchanged beyond its limit.</param>
    /// <param name="id">Optional explicit canonical recurrence identity.</param>
    /// <returns>A syntax-only awaitable yielding the final completed occurrence result.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<TResult> RepeatAcrossActivation<TResult, TProgress>(
        ProcessTask<TResult> occurrence,
        ProcessProjection<TResult, bool> continueWhen,
        ProcessProjection<TResult, TProgress> progress,
        ProcessRecurrencePolicy policy,
        ProcessBranch exhausted,
        ProcessBranch stalled,
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

    /// <summary>Annotates a Fork branch with an explicit durable identity and optional capacity domain.</summary>
    /// <typeparam name="TResult">Portable typed branch-result type.</typeparam>
    /// <param name="branch">Syntax-only local branch computation.</param>
    /// <param name="id">Explicit canonical branch identity.</param>
    /// <param name="capacityDomain">Optional stable capacity-domain identity.</param>
    /// <param name="role">Optional stable semantic role of the branch start edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the branch start edge.</param>
    /// <returns>The syntax-only annotated branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask<TResult> Branch<TResult>(
        ProcessTask<TResult> branch,
        ExecutionNodeId id,
        string? capacityDomain = null,
        string? role = null,
        ExecutionNodeId? edgeOwner = null) =>
        throw SyntaxOnly();

    /// <summary>Annotates a result-less Fork branch with an explicit durable identity.</summary>
    /// <param name="branch">Syntax-only local branch computation.</param>
    /// <param name="id">Explicit canonical branch identity.</param>
    /// <param name="capacityDomain">Optional stable capacity-domain identity.</param>
    /// <param name="role">Optional stable semantic role of the branch start edge.</param>
    /// <param name="edgeOwner">Optional durable owner of the branch start edge.</param>
    /// <returns>The syntax-only annotated branch.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask Branch(
        ProcessTask branch,
        ExecutionNodeId id,
        string? capacityDomain = null,
        string? role = null,
        ExecutionNodeId? edgeOwner = null) =>
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

    /// <summary>Declares a fully identified all-branches Fork/Join compatibility boundary.</summary>
    /// <param name="id">Explicit canonical Fork identity.</param>
    /// <param name="joinId">Explicit canonical reciprocal Join identity.</param>
    /// <param name="nextRole">Stable semantic role of the completed Join continuation edge.</param>
    /// <param name="branches">Two or more syntax-only branch computations.</param>
    /// <returns>A syntax-only task representing convergence at the identified Join.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessTask ForkJoin(
        ExecutionNodeId id,
        ExecutionNodeId joinId,
        string nextRole,
        params ProcessTask[] branches) =>
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

    /// <summary>Declares a homogeneous typed Fork whose first eligible completed branch wins.</summary>
    /// <typeparam name="TResult">Common portable result type produced by every branch.</typeparam>
    /// <param name="branches">Two or more typed branch computations in authored result order.</param>
    /// <param name="policy">Explicit canonical Any-Join policy.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable containing the exact selected branch identity and result.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<ProcessJoinWinner<TResult>> ForkAny<TResult>(
        ProcessTask<TResult>[] branches,
        ProcessJoinPolicy policy,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null)
        where TResult : notnull =>
        throw SyntaxOnly();

    /// <summary>Declares a homogeneous typed Fork that selects an explicit required number of completed branches.</summary>
    /// <typeparam name="TResult">Common portable result type produced by every branch.</typeparam>
    /// <param name="branches">Typed branch computations whose stable identities govern deterministic selection.</param>
    /// <param name="policy">Explicit canonical RequiredCount-Join policy.</param>
    /// <param name="admission">Optional bounded admission policy; omission preserves eager finite-set admission.</param>
    /// <param name="id">Optional explicit canonical Fork identity.</param>
    /// <returns>A syntax-only awaitable containing selected branch identities and results in canonical selection order.</returns>
    /// <exception cref="InvalidOperationException">Always thrown if the syntax-only member is executed.</exception>
    public ProcessAwaitable<ImmutableArray<ProcessJoinWinner<TResult>>> ForkRequired<TResult>(
        ProcessTask<TResult>[] branches,
        ProcessJoinPolicy policy,
        ProcessAdmission? admission = null,
        ExecutionNodeId? id = null)
        where TResult : notnull =>
        throw SyntaxOnly();

    static InvalidOperationException SyntaxOnly() => new(
        "Process computation-expression members are syntax-only and must be lowered by Cohesive.Analyzers.");
}

/// <summary>Source-only typed branch selected by one outbound Request terminal outcome.</summary>
/// <typeparam name="TOutcome">Portable terminal-outcome result type.</typeparam>
/// <param name="outcome">Selected typed terminal-outcome value.</param>
/// <returns>The syntax-only Process branch.</returns>
public delegate ProcessTask ProcessOutcomeBranch<in TOutcome>(TOutcome outcome);

/// <summary>Source-only typed branch selected by an inbound event or Signal.</summary>
/// <typeparam name="TInput">Portable admitted interaction payload type.</typeparam>
/// <param name="input">Admitted typed interaction payload.</param>
/// <returns>The syntax-only Process branch.</returns>
public delegate ProcessTask ProcessInteractionBranch<in TInput>(TInput input);

/// <summary>Source-only typed branch selected by an inbound Request.</summary>
/// <typeparam name="TInput">Portable admitted Request payload type.</typeparam>
/// <param name="input">Admitted typed Request payload.</param>
/// <param name="request">Durably retained Request obligation available for a later Reply.</param>
/// <returns>The syntax-only Process branch.</returns>
public delegate ProcessTask ProcessRequestBranch<in TInput>(TInput input, ProcessRequestObligation request);

/// <summary>Source-only parameterless branch selected by one canonical Process edge.</summary>
/// <returns>The syntax-only Process branch.</returns>
public delegate ProcessTask ProcessBranch();

/// <summary>Inline syntax-only portable projection over one lexical Process value.</summary>
/// <typeparam name="TInput">Portable lexical input type.</typeparam>
/// <typeparam name="TResult">Portable projected result type.</typeparam>
/// <param name="input">Lexically visible portable input.</param>
/// <returns>The pure value fused into the nearest canonical Process consumer.</returns>
public delegate TResult ProcessProjection<in TInput, out TResult>(TInput input);

/// <summary>Opaque syntax-only predicate arm consumed by explicit Choice authoring.</summary>
public abstract class ProcessChoiceArm
{
    /// <summary>Restricts Choice arms to syntax recognized by the Process computation generator.</summary>
    private protected ProcessChoiceArm()
    {
    }
}

/// <summary>Opaque syntax-only exact-value arm consumed by explicit Match authoring.</summary>
/// <typeparam name="TValue">Portable matched-value and exact-pattern CLR type.</typeparam>
public abstract class ProcessMatchArm<TValue>
{
    /// <summary>Restricts Match arms to syntax recognized by the Process computation generator.</summary>
    private protected ProcessMatchArm()
    {
    }
}

/// <summary>Inline syntax-only portable guard for one admitted interaction payload.</summary>
/// <typeparam name="TInput">Portable admitted interaction payload type.</typeparam>
/// <param name="input">Candidate typed interaction payload.</param>
/// <returns><see langword="true"/> when the clause is eligible.</returns>
public delegate bool ProcessGuard<in TInput>(TInput input);

/// <summary>Opaque syntax-only terminal-outcome case consumed by multi-outcome Request authoring.</summary>
public abstract class ProcessRequestOutcomeCase
{
    /// <summary>Restricts outcome cases to syntax recognized by the Process computation generator.</summary>
    private protected ProcessRequestOutcomeCase()
    {
    }
}

/// <summary>Opaque syntax-only interaction or timer case consumed by AwaitMatch authoring.</summary>
public abstract class ProcessAwaitCase
{
    /// <summary>Restricts AwaitMatch cases to syntax recognized by the Process computation generator.</summary>
    private protected ProcessAwaitCase()
    {
    }
}

/// <summary>Typed materialized result of one branch selected by a partial Process Join.</summary>
/// <typeparam name="TResult">Portable branch-result type shared by every candidate branch.</typeparam>
/// <param name="Branch">Exact stable branch identity selected by canonical Join arbitration.</param>
/// <param name="Result">Portable result evaluated in the selected branch token scope.</param>
public sealed record ProcessJoinWinner<TResult>(string Branch, TResult Result)
    where TResult : notnull;

/// <summary>Readable factories that return the canonical <see cref="ProcessJoinPolicy"/> directly.</summary>
public static class ProcessJoin
{
    /// <summary>Creates a canonical first-eligible-branch Join policy.</summary>
    /// <param name="cancellation">Behavior for branches remaining after the winner is selected.</param>
    /// <param name="failure">Branch-failure behavior.</param>
    /// <param name="completionOrder">Whether logical completion order is observable.</param>
    /// <param name="tieBreak">Deterministic simultaneous-eligibility arbitration.</param>
    /// <returns>The canonical Any-Join policy consumed directly by Process IR authoring.</returns>
    public static ProcessJoinPolicy Any(
        ProcessJoinCancellationPolicy cancellation,
        ProcessJoinFailurePolicy failure = ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCompletionOrder completionOrder = ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak tieBreak = ProcessJoinTieBreak.BranchIdentity) =>
        new(
            mode: ProcessJoinMode.Any,
            requiredCount: 0,
            failure: failure,
            cancellation: cancellation,
            completionOrder: completionOrder,
            tieBreak: tieBreak);

    /// <summary>Creates a canonical required-eligible-branch-count Join policy.</summary>
    /// <param name="requiredCount">Positive number of eligible completed branches required to resolve the Join.</param>
    /// <param name="cancellation">Behavior for branches remaining after the selection threshold is reached.</param>
    /// <param name="failure">Branch-failure behavior.</param>
    /// <param name="completionOrder">Whether logical completion order is observable.</param>
    /// <param name="tieBreak">Deterministic simultaneous-eligibility arbitration.</param>
    /// <returns>The canonical RequiredCount-Join policy consumed directly by Process IR authoring.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requiredCount"/> is not positive.</exception>
    public static ProcessJoinPolicy Required(
        int requiredCount,
        ProcessJoinCancellationPolicy cancellation,
        ProcessJoinFailurePolicy failure = ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCompletionOrder completionOrder = ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak tieBreak = ProcessJoinTieBreak.BranchIdentity)
    {
        if (requiredCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredCount), "Required branch count must be positive.");
        }

        return new(
            mode: ProcessJoinMode.RequiredCount,
            requiredCount: requiredCount,
            failure: failure,
            cancellation: cancellation,
            completionOrder: completionOrder,
            tieBreak: tieBreak);
    }
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
        {
            throw new ArgumentOutOfRangeException(nameof(minimumParallelism), "Minimum parallelism must be positive.");
        }

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

/// <summary>Task-like result used only to type-check a generated Process method, typed branch, or recurrence occurrence.</summary>
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
