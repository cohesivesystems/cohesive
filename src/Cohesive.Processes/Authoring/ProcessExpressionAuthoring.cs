using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Processes.Authoring;

/// <summary>
/// One transient declarative Process operation produced by the C# expression frontend.
/// </summary>
/// <remarks>
/// Expressions contain authoring-time lowering data only. They are consumed synchronously and are never retained
/// by canonical Process IR, persisted documents, compiled plans, or runtime state.
/// </remarks>
/// <typeparam name="TInput">CLR type projected into the Process invocation-input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the Process terminal-result contract.</typeparam>
public sealed class ProcessExpression<TInput, TResult>
{
    internal ProcessExpression(
        ProcessExpressionAuthoring<TInput, TResult> owner,
        string role,
        ExecutionNodeId? explicitId,
        bool terminal,
        Action<ExecutionNodeId, ExecutionNodeId?> lower)
    {
        Owner = owner;
        Role = role;
        ExplicitId = explicitId;
        IsTerminal = terminal;
        Lower = lower;
    }

    internal ProcessExpressionAuthoring<TInput, TResult> Owner { get; }

    internal string Role { get; }

    internal ExecutionNodeId? ExplicitId { get; }

    internal bool IsTerminal { get; }

    internal Action<ExecutionNodeId, ExecutionNodeId?> Lower { get; }

}

/// <summary>
/// One transient terminal-outcome declaration used by an expression-authored Request.
/// </summary>
/// <remarks>The declaration is lowered immediately to a canonical <see cref="ProcessRequestOutcomeBranch"/>.</remarks>
/// <typeparam name="TInput">CLR type projected into the Process invocation-input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the Process terminal-result contract.</typeparam>
public sealed class ProcessExpressionRequestOutcome<TInput, TResult>
{
    internal ProcessExpressionRequestOutcome(
        ProcessExpressionAuthoring<TInput, TResult> owner,
        RequestTerminalOutcomeId outcome,
        ExecutionNodeId? explicitId,
        Func<ExecutionNodeId, ExecutionNodeId, ProcessRequestOutcomeBranch> lower)
    {
        Owner = owner;
        Outcome = outcome;
        ExplicitId = explicitId;
        Lower = lower;
    }

    internal ProcessExpressionAuthoring<TInput, TResult> Owner { get; }

    internal RequestTerminalOutcomeId Outcome { get; }

    internal ExecutionNodeId? ExplicitId { get; }

    internal Func<ExecutionNodeId, ExecutionNodeId, ProcessRequestOutcomeBranch> Lower { get; }
}

/// <summary>
/// Typed C# expression frontend for an ordinary sequential canonical Process.
/// </summary>
/// <remarks>
/// Operations create transient declarations rather than mutating the canonical graph. The enclosing collection
/// expression establishes semantic sequence; lowering derives identities and edges and delegates node construction
/// to <see cref="ProcessBuilder{TInput,TResult}"/>. Structured branching, parallelism, waits, child Processes,
/// partitioning, and recurrence belong to the structured-control frontend.
/// </remarks>
/// <typeparam name="TInput">CLR type projected into the Process invocation-input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the Process terminal-result contract.</typeparam>
public sealed class ProcessExpressionAuthoring<TInput, TResult>
{
    readonly ProcessBuilder<TInput, TResult> builder;
    readonly ProcessAuthoringMetadata metadata;

    internal ProcessExpressionAuthoring(
        ProcessBuilder<TInput, TResult> builder,
        ProcessAuthoringMetadata metadata)
    {
        this.builder = builder;
        this.metadata = metadata;
    }

    /// <summary>Typed binding for the complete Process invocation input.</summary>
    public ProcessBinding<TInput> Input => builder.Input;

    /// <summary>Wraps an existing canonical expression and its explicitly attested contract.</summary>
    /// <typeparam name="TValue">CLR type projected into the expression contract.</typeparam>
    /// <param name="expression">Portable canonical expression.</param>
    /// <param name="contract">Exact portable contract asserted for <paramref name="expression"/>.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed value scoped to this expression-authoring session.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/> or <paramref name="contract"/> is <see langword="null"/>.
    /// </exception>
    public ProcessValue<TValue> CanonicalValue<TValue>(
        Expr expression,
        ValueContract contract,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.CanonicalValue<TValue>(expression, contract, sourceFile, sourceLine, sourceMember);

    /// <summary>Creates a typed portable constant value.</summary>
    /// <typeparam name="TValue">CLR type projected into the constant contract.</typeparam>
    /// <param name="value">Value converted immediately to canonical observation data.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed canonical constant value.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> cannot be represented portably.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> cannot be projected as observation data.</exception>
    public ProcessValue<TValue> Constant<TValue>(
        TValue value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.Constant(value, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a named typed output binding using the expression-authoring identity convention.</summary>
    /// <typeparam name="TValue">CLR type projected into the output contract.</typeparam>
    /// <param name="name">Stable semantic binding name within the Process body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this expression-authoring session.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public ProcessBinding<TValue> Output<TValue>(
        string name,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.Output<TValue>(BindingOwner(name), "value", sourceFile, sourceLine, sourceMember);

    /// <summary>Declares an explicitly identified typed output binding.</summary>
    /// <typeparam name="TValue">CLR type projected into the output contract.</typeparam>
    /// <param name="binding">Stable explicit value-binding identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this expression-authoring session.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    public ProcessBinding<TValue> Output<TValue>(
        ValueBindingId binding,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.Output<TValue>(binding, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a named typed output binding with an explicit portable occurrence contract.</summary>
    /// <typeparam name="TValue">CLR type represented by the output handle.</typeparam>
    /// <param name="name">Stable semantic binding name within the Process body.</param>
    /// <param name="contract">Exact portable output contract.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this expression-authoring session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public ProcessBinding<TValue> Output<TValue>(
        string name,
        ValueContract contract,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.Output<TValue>(
            ProcessAuthoringIdentities.BindingFor(BindingOwner(name), "value"),
            contract,
            sourceFile,
            sourceLine,
            sourceMember);

    /// <summary>Declares a named admitted Request obligation using the expression identity convention.</summary>
    /// <param name="name">Stable semantic obligation name within the Process body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle usable by a later expression-authored Reply.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public ProcessRequestObligation RequestObligation(
        string name,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.RequestObligation(BindingOwner(name), "request", sourceFile, sourceLine, sourceMember);

    /// <summary>Declares an explicitly identified admitted Request obligation.</summary>
    /// <param name="binding">Stable explicit obligation-binding identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle usable by a later expression-authored Reply.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    public ProcessRequestObligation RequestObligation(
        RequestObligationBindingId binding,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        builder.RequestObligation(binding, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a sequential Transition invocation without an output binding.</summary>
    /// <typeparam name="TSubject">CLR type of the authoritative aggregate-subject expression.</typeparam>
    /// <typeparam name="TTransitionInput">CLR type of the Transition input expression.</typeparam>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Typed aggregate-subject expression.</param>
    /// <param name="input">Typed Transition input expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="transition"/>, <paramref name="subject"/>, or <paramref name="input"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException">A typed value belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> InvokeTransition<TSubject, TTransitionInput>(
        ExecutionDefinitionReference transition,
        ProcessValue<TSubject> subject,
        ProcessValue<TTransitionInput> input,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "invoke-transition",
            id,
            terminal: false,
            (node, next) => builder.InvokeTransition(
                node,
                transition,
                subject,
                input,
                Continue(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a sequential Transition invocation whose outcome populates a typed binding.</summary>
    /// <typeparam name="TSubject">CLR type of the authoritative aggregate-subject expression.</typeparam>
    /// <typeparam name="TTransitionInput">CLR type of the Transition input expression.</typeparam>
    /// <typeparam name="TOutcome">CLR type of the Transition outcome binding.</typeparam>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Typed aggregate-subject expression.</param>
    /// <param name="input">Typed Transition input expression.</param>
    /// <param name="output">Binding populated by the Transition outcome.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="transition"/>, <paramref name="subject"/>, <paramref name="input"/>, or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException">A typed value or binding belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> InvokeTransition<TSubject, TTransitionInput, TOutcome>(
        ExecutionDefinitionReference transition,
        ProcessValue<TSubject> subject,
        ProcessValue<TTransitionInput> input,
        ProcessBinding<TOutcome> output,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "invoke-transition",
            id,
            terminal: false,
            (node, next) => builder.InvokeTransition(
                node,
                transition,
                subject,
                input,
                Continue(node, RequireNext(next), output, sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares sequential evaluation of an exact Relation or Query without an output binding.</summary>
    /// <typeparam name="TQueryInput">CLR type of the query-input expression.</typeparam>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Typed query-input expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relation"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="input"/> belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> EvaluateRelation<TQueryInput>(
        ExecutionDefinitionReference relation,
        ProcessValue<TQueryInput> input,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "evaluate-relation",
            id,
            terminal: false,
            (node, next) => builder.EvaluateRelation(
                node,
                relation,
                input,
                Continue(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares sequential evaluation of an exact Relation or Query with a typed result binding.</summary>
    /// <typeparam name="TQueryInput">CLR type of the query-input expression.</typeparam>
    /// <typeparam name="TQueryOutput">CLR type of the query-result binding.</typeparam>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Typed query-input expression.</param>
    /// <param name="output">Binding populated by the query result.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relation"/>, <paramref name="input"/>, or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException">A typed value or binding belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> EvaluateRelation<TQueryInput, TQueryOutput>(
        ExecutionDefinitionReference relation,
        ProcessValue<TQueryInput> input,
        ProcessBinding<TQueryOutput> output,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "evaluate-relation",
            id,
            terminal: false,
            (node, next) => builder.EvaluateRelation(
                node,
                relation,
                input,
                Continue(node, RequireNext(next), output, sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a Request terminal outcome without a result binding.</summary>
    /// <param name="outcome">Stable terminal outcome declared by the exact Request contract.</param>
    /// <param name="id">Optional explicit branch identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient Request outcome declaration.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outcome"/> or <paramref name="id"/> contains a default identity.
    /// </exception>
    public ProcessExpressionRequestOutcome<TInput, TResult> RequestOutcome(
        RequestTerminalOutcomeId outcome,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Outcome(
            outcome,
            id,
            (branch, target) => builder.RequestOutcome(
                branch,
                outcome,
                Continue(branch, target, sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a Request terminal outcome that populates a typed result binding.</summary>
    /// <typeparam name="TOutcome">CLR type of the outcome result binding.</typeparam>
    /// <param name="outcome">Stable terminal outcome declared by the exact Request contract.</param>
    /// <param name="output">Binding populated by the accepted outcome.</param>
    /// <param name="id">Optional explicit branch identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient Request outcome declaration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outcome"/> or <paramref name="id"/> contains a default identity.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="output"/> belongs to another authoring session.</exception>
    public ProcessExpressionRequestOutcome<TInput, TResult> RequestOutcome<TOutcome>(
        RequestTerminalOutcomeId outcome,
        ProcessBinding<TOutcome> output,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Outcome(
            outcome,
            id,
            (branch, target) => builder.RequestOutcome(
                branch,
                outcome,
                Continue(branch, target, output, sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a sequential durable Request whose outcomes converge on the following expression.</summary>
    /// <typeparam name="TPayload">CLR type of the Request payload.</typeparam>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="payload">Typed Request payload expression.</param>
    /// <param name="outcomes">Terminal outcomes, each of which continues to the next expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="payload"/>, or <paramref name="outcomes"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> contains a default identity, or <paramref name="outcomes"/> is empty or contains a
    /// foreign declaration.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="payload"/> belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> Request<TPayload>(
        RequestContractReference contract,
        ProcessValue<TPayload> payload,
        IReadOnlyList<ProcessExpressionRequestOutcome<TInput, TResult>> outcomes,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
            throw new ArgumentException("An expression-authored Request requires at least one terminal outcome.", nameof(outcomes));
        var copied = new ProcessExpressionRequestOutcome<TInput, TResult>[outcomes.Count];
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index]
                ?? throw new ArgumentException("Request outcomes cannot contain null declarations.", nameof(outcomes));
            if (!ReferenceEquals(outcome.Owner, this))
                throw new ArgumentException("A Request outcome belongs to another expression-authoring session.", nameof(outcomes));
            copied[index] = outcome;
        }

        return Step(
            "request",
            id,
            terminal: false,
            (node, next) =>
            {
                var target = RequireNext(next);
                var branches = ImmutableArray.CreateBuilder<ProcessRequestOutcomeBranch>(copied.Length);
                for (var index = 0; index < copied.Length; index++)
                {
                    var outcome = copied[index];
                    var branch = outcome.ExplicitId ?? ProcessAuthoringIdentities.NodeFor(
                        new(["request", node.Value, "outcomes", outcome.Outcome.Value]));
                    branches.Add(outcome.Lower(branch, target));
                }
                builder.Request(node, contract, payload, branches.MoveToImmutable(), sourceFile, sourceLine, sourceMember);
            });
    }

    /// <summary>Declares a sequential typed domain-event emission.</summary>
    /// <typeparam name="TPayload">CLR type of the event payload.</typeparam>
    /// <param name="contract">Exact typed domain-event contract.</param>
    /// <param name="payload">Typed event payload expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/> or <paramref name="payload"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="payload"/> belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> EmitEvent<TPayload>(
        DomainEventContractReference contract,
        ProcessValue<TPayload> payload,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "emit-event",
            id,
            terminal: false,
            (node, next) => builder.EmitEvent(
                node,
                contract,
                payload,
                Next(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a sequential typed Signal send.</summary>
    /// <typeparam name="TTarget">CLR type of the semantic target expression.</typeparam>
    /// <typeparam name="TPayload">CLR type of the Signal payload.</typeparam>
    /// <param name="contract">Exact typed Signal contract.</param>
    /// <param name="target">Typed semantic target expression.</param>
    /// <param name="payload">Typed Signal payload expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="target"/>, or <paramref name="payload"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException">A typed value belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> SendSignal<TTarget, TPayload>(
        SignalContractReference contract,
        ProcessValue<TTarget> target,
        ProcessValue<TPayload> payload,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "send-signal",
            id,
            terminal: false,
            (node, next) => builder.SendSignal(
                node,
                contract,
                target,
                payload,
                Next(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares a sequential typed Reply that discharges an admitted Request obligation.</summary>
    /// <typeparam name="TPayload">CLR type of the Reply payload.</typeparam>
    /// <param name="contract">Exact typed Reply contract.</param>
    /// <param name="request">Visible Request obligation discharged by the Reply.</param>
    /// <param name="payload">Typed Reply payload expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="request"/>, or <paramref name="payload"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException">An authoring handle belongs to another session.</exception>
    public ProcessExpression<TInput, TResult> Reply<TPayload>(
        ReplyContractReference contract,
        ProcessRequestObligation request,
        ProcessValue<TPayload> payload,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "reply",
            id,
            terminal: false,
            (node, next) => builder.Reply(
                node,
                contract,
                request,
                payload,
                Next(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares an explicit durable activation boundary and sequential resume.</summary>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient sequential Process expression.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    public ProcessExpression<TInput, TResult> DurableCut(
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "durable-cut",
            id,
            terminal: false,
            (node, next) => builder.DurableCut(
                node,
                Next(node, RequireNext(next), sourceFile, sourceLine, sourceMember),
                sourceFile,
                sourceLine,
                sourceMember));

    /// <summary>Declares the successful terminal result of the sequential Process.</summary>
    /// <param name="result">Typed Process result expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient terminal Process expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> Return(
        ProcessValue<TResult> result,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "return",
            id,
            terminal: true,
            (node, _) => builder.Return(node, result, sourceFile, sourceLine, sourceMember));

    /// <summary>Declares the failed terminal result of the sequential Process.</summary>
    /// <param name="result">Typed Process failure-result expression.</param>
    /// <param name="id">Optional explicit node identity; convention identity is used when omitted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A transient terminal Process expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identity.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> belongs to another authoring session.</exception>
    public ProcessExpression<TInput, TResult> Fail(
        ProcessValue<TResult> result,
        ExecutionNodeId? id = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Step(
            "fail",
            id,
            terminal: true,
            (node, _) => builder.Fail(node, result, sourceFile, sourceLine, sourceMember));

    internal ImmutableArray<ProcessExpressionIdentityEvidence> Lower(
        IReadOnlyList<ProcessExpression<TInput, TResult>> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Count == 0)
            throw new ArgumentException("A Process expression requires at least one operation.", nameof(expressions));

        var ids = new ExecutionNodeId[expressions.Count];
        var paths = new ExecutionSemanticPath[expressions.Count];
        var conventional = new bool[expressions.Count];
        HashSet<ExecutionNodeId> observed = [];
        for (var index = 0; index < expressions.Count; index++)
        {
            var expression = expressions[index]
                ?? throw new ArgumentException("A Process expression cannot contain null operations.", nameof(expressions));
            if (!ReferenceEquals(expression.Owner, this))
                throw new ArgumentException("A Process operation belongs to another expression-authoring session.", nameof(expressions));
            if (expression.IsTerminal != (index == expressions.Count - 1))
            {
                throw new ArgumentException(
                    expression.IsTerminal
                        ? "A terminal Process expression must be the final operation."
                        : "A Process expression must end in Return or Fail.",
                    nameof(expressions));
            }

            var path = new ExecutionSemanticPath(
                ["body", "steps", index.ToString(System.Globalization.CultureInfo.InvariantCulture), expression.Role]);
            var explicitId = expression.ExplicitId;
            if (index == 0 && metadata.EntryId is { } metadataEntry)
            {
                if (explicitId is { } expressionEntry && expressionEntry != metadataEntry)
                {
                    throw new ArgumentException(
                        $"Expression entry '{expressionEntry.Value}' conflicts with metadata entry '{metadataEntry.Value}'.",
                        nameof(expressions));
                }
                explicitId = metadataEntry;
            }

            var id = explicitId ?? ProcessAuthoringIdentities.NodeFor(path);
            if (!observed.Add(id))
                throw new InvalidOperationException($"Process node identity '{id.Value}' was authored more than once.");
            ids[index] = id;
            paths[index] = path;
            conventional[index] = explicitId is null;
        }

        builder.UseDerivedEntry(ids[0]);
        var evidence = ImmutableArray.CreateBuilder<ProcessExpressionIdentityEvidence>(expressions.Count);
        for (var index = 0; index < expressions.Count; index++)
        {
            var expression = expressions[index];
            var id = ids[index];
            var path = paths[index];
            var provenance = conventional[index]
                ? ProcessAuthoringIdentities.ConventionSourceFor(path, path)
                : ExplicitIdentitySource(id, path);
            evidence.Add(new(id, provenance));
            expression.Lower(id, index + 1 < ids.Length ? ids[index + 1] : null);
        }
        return evidence.MoveToImmutable();
    }

    ProcessExpression<TInput, TResult> Step(
        string role,
        ExecutionNodeId? id,
        bool terminal,
        Action<ExecutionNodeId, ExecutionNodeId?> lower)
    {
        if (id is { } explicitId && string.IsNullOrWhiteSpace(explicitId.Value))
            throw new ArgumentException("An explicit Process expression identity cannot be default.", nameof(id));
        return new(
            this,
            role,
            id,
            terminal,
            lower);
    }

    ProcessExpressionRequestOutcome<TInput, TResult> Outcome(
        RequestTerminalOutcomeId outcome,
        ExecutionNodeId? id,
        Func<ExecutionNodeId, ExecutionNodeId, ProcessRequestOutcomeBranch> lower)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
            throw new ArgumentException("A Request outcome requires a stable identity.", nameof(outcome));
        if (id is { } explicitId && string.IsNullOrWhiteSpace(explicitId.Value))
            throw new ArgumentException("An explicit Request outcome identity cannot be default.", nameof(id));
        return new(this, outcome, id, lower);
    }

    ProcessContinuation Continue(
        ExecutionNodeId owner,
        ExecutionNodeId target,
        string sourceFile,
        int sourceLine,
        string sourceMember) =>
        builder.Continuation(
            Next(owner, target, sourceFile, sourceLine, sourceMember),
            sourceFile,
            sourceLine,
            sourceMember);

    ProcessContinuation Continue<TValue>(
        ExecutionNodeId owner,
        ExecutionNodeId target,
        ProcessBinding<TValue> output,
        string sourceFile,
        int sourceLine,
        string sourceMember) =>
        builder.Continuation(
            Next(owner, target, sourceFile, sourceLine, sourceMember),
            output,
            sourceFile,
            sourceLine,
            sourceMember);

    ProcessEdge Next(
        ExecutionNodeId owner,
        ExecutionNodeId target,
        string sourceFile,
        int sourceLine,
        string sourceMember) =>
        builder.Edge(owner, "next", target, sourceFile, sourceLine, sourceMember);

    static ExecutionNodeId RequireNext(ExecutionNodeId? next) =>
        next ?? throw new InvalidOperationException("A non-terminal Process expression requires a following operation.");

    static ExecutionNodeId BindingOwner(string name) =>
        ProcessAuthoringIdentities.NodeFor(
            new(["body", "bindings", Guard.RequireNotNullOrWhiteSpace(name)]));

    static ExecutionSourceProvenance ExplicitIdentitySource(
        ExecutionNodeId id,
        ExecutionSemanticPath structuralPath)
    {
        var idPath = new ExecutionSemanticPath(["identity", id.Value]);
        return new(
            $"{ProcessAuthoring.ExpressionProducer}#explicit{idPath}",
            structuralPath,
            "Identity supplied explicitly to the C# Process expression frontend.");
    }
}

public static partial class ProcessAuthoring
{
    /// <summary>Stable producer identity for the human-facing C# Process expression frontend.</summary>
    public const string ExpressionProducer = "cohesive.processes.csharp-expression/v1";

    /// <summary>Authors a sequential canonical Process from a declarative collection of typed expressions.</summary>
    /// <typeparam name="TInput">Typed invocation input.</typeparam>
    /// <typeparam name="TResult">Typed terminal result shared by successful and failed outcomes.</typeparam>
    /// <param name="metadata">
    /// Stable definition, revision, recovery, and provenance metadata; entry identity may be omitted.
    /// </param>
    /// <param name="author">
    /// Synchronous frontend callback returning the semantic operation sequence, normally as a collection expression.
    /// </param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> or <paramref name="author"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The returned sequence is empty, foreign, structurally invalid, or contains conflicting identities.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Authored handles are foreign, identities collide, or canonical graph construction is contradictory.
    /// </exception>
    public static Process<TInput, TResult> CreateExpression<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        Func<ProcessExpressionAuthoring<TInput, TResult>, IReadOnlyList<ProcessExpression<TInput, TResult>>> author,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        CreateExpressionCore(
            metadata,
            inputContract: null,
            resultContract: null,
            author,
            sourceFile,
            sourceLine,
            sourceMember);

    /// <summary>
    /// Authors a sequential canonical Process from typed expressions and explicit top-level occurrence contracts.
    /// </summary>
    /// <typeparam name="TInput">Typed invocation input represented by <paramref name="inputContract"/>.</typeparam>
    /// <typeparam name="TResult">Typed terminal result represented by <paramref name="resultContract"/>.</typeparam>
    /// <param name="metadata">
    /// Stable definition, revision, recovery, and provenance metadata; entry identity may be omitted.
    /// </param>
    /// <param name="inputContract">Exact portable input occurrence contract.</param>
    /// <param name="resultContract">Exact portable result occurrence contract.</param>
    /// <param name="author">
    /// Synchronous frontend callback returning the semantic operation sequence, normally as a collection expression.
    /// </param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/>, <paramref name="inputContract"/>, <paramref name="resultContract"/>, or
    /// <paramref name="author"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The returned sequence is empty, foreign, structurally invalid, or contains conflicting identities.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Authored handles are foreign, identities collide, or canonical graph construction is contradictory.
    /// </exception>
    public static Process<TInput, TResult> CreateExpression<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        ValueContract inputContract,
        ValueContract resultContract,
        Func<ProcessExpressionAuthoring<TInput, TResult>, IReadOnlyList<ProcessExpression<TInput, TResult>>> author,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        CreateExpressionCore(
            metadata,
            Guard.RequireNotNull(inputContract),
            Guard.RequireNotNull(resultContract),
            author,
            sourceFile,
            sourceLine,
            sourceMember);

    static Process<TInput, TResult> CreateExpressionCore<TInput, TResult>(
        ProcessAuthoringMetadata metadata,
        ValueContract? inputContract,
        ValueContract? resultContract,
        Func<ProcessExpressionAuthoring<TInput, TResult>, IReadOnlyList<ProcessExpression<TInput, TResult>>> author,
        string sourceFile,
        int sourceLine,
        string sourceMember)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(author);
        ImmutableArray<ProcessExpressionIdentityEvidence> identityEvidence = [];
        return CreateCore<TInput, TResult>(
            metadata,
            inputContract,
            resultContract,
            builder =>
            {
                var expression = new ProcessExpressionAuthoring<TInput, TResult>(builder, metadata);
                identityEvidence = expression.Lower(author(expression));
            },
            sourceFile,
            sourceLine,
            sourceMember,
            (definition, sourceMap) => AddIdentityEvidence(definition, sourceMap, identityEvidence));
    }

    static ExecutionSourceMap AddIdentityEvidence(
        CanonicalProcessDefinition definition,
        ExecutionSourceMap sourceMap,
        ImmutableArray<ProcessExpressionIdentityEvidence> evidence)
    {
        if (evidence.IsDefaultOrEmpty)
            return sourceMap;

        var indices = new Dictionary<ExecutionNodeId, int>(definition.Nodes.Length);
        for (var index = 0; index < definition.Nodes.Length; index++)
            indices.Add(definition.Nodes[index].Id, index);

        var entries = ImmutableArray.CreateBuilder<ExecutionSourceProvenance>();
        entries.AddRange(sourceMap.Entries);
        foreach (var item in evidence)
        {
            if (!indices.TryGetValue(item.Node, out var index))
            {
                throw new InvalidOperationException(
                    $"Expression identity evidence references absent Process node '{item.Node.Value}'.");
            }
            var prefix = new ExecutionSemanticPath(
                ["nodes", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            HashSet<ExecutionSemanticPath> mappedPaths = [];
            foreach (var source in sourceMap.Entries)
            {
                var path = source.SemanticPath!.Value;
                if (!HasPrefix(path, prefix) || !mappedPaths.Add(path))
                    continue;
                entries.Add(new(item.Provenance.Reference, path, item.Provenance.Description));
            }
        }
        return new(entries.ToImmutable());

        static bool HasPrefix(ExecutionSemanticPath path, ExecutionSemanticPath prefix)
        {
            if (path.Segments.Length < prefix.Segments.Length)
                return false;
            for (var index = 0; index < prefix.Segments.Length; index++)
            {
                if (!string.Equals(path.Segments[index], prefix.Segments[index], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}

internal readonly record struct ProcessExpressionIdentityEvidence(
    ExecutionNodeId Node,
    ExecutionSourceProvenance Provenance);
