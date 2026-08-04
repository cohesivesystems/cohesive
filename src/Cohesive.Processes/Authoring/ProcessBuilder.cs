using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Processes.Authoring;

/// <summary>
/// Authors a finite canonical Process graph using typed values and the closed persisted Process-node union.
/// </summary>
/// <typeparam name="TInput">CLR type projected into the Process invocation-input contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the Process terminal-result contract.</typeparam>
public sealed partial class ProcessBuilder<TInput, TResult>
{
    readonly ProcessAuthoringContext context;
    readonly List<ProcessNode> nodes = [];
    readonly HashSet<ExecutionNodeId> nodeIds = [];
    ExecutionNodeId? derivedEntry;

    internal ProcessBuilder(ProcessAuthoringContext context, AuthoredProcessSource rootSource)
    {
        this.context = context;
        Input = new(
            context,
            ProcessBindingIds.Input,
            context.InputContract,
            output: null);
        context.RegisterIfAbsent(Input.Expression, rootSource);
    }

    /// <summary>Typed binding for the complete Process invocation input.</summary>
    public ProcessBinding<TInput> Input { get; }

    internal void UseDerivedEntry(ExecutionNodeId entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Value))
            throw new ArgumentException("A derived Process entry requires a stable node identity.", nameof(entry));
        if (derivedEntry is not null)
            throw new InvalidOperationException("A Process authoring frontend supplied its entry more than once.");
        derivedEntry = entry;
    }

    /// <summary>Wraps an existing canonical expression and its explicitly attested contract.</summary>
    /// <typeparam name="TValue">CLR type projected into the expression contract.</typeparam>
    /// <param name="expression">Portable canonical expression.</param>
    /// <param name="contract">
    /// Exact portable contract asserted for <paramref name="expression"/> and verified by canonical validation.
    /// </param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed value scoped to this authoring session.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/> or <paramref name="contract"/> is <see langword="null"/>.
    /// </exception>
    public ProcessValue<TValue> CanonicalValue<TValue>(
        Expr expression,
        ValueContract contract,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        context.Value<TValue>(
            expression,
            Guard.RequireNotNull(contract),
            context.Source(sourceFile, sourceLine, sourceMember, "Canonical Process expression"));

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
        context.Constant(
            value,
            context.Source(sourceFile, sourceLine, sourceMember, "Canonical Process constant"));

    /// <summary>Creates a typed equality expression.</summary>
    /// <typeparam name="TValue">Shared operand value type.</typeparam>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Boolean canonical expression.</returns>
    /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An operand belongs to another authoring session.</exception>
    public ProcessValue<bool> Equal<TValue>(
        ProcessValue<TValue> left,
        ProcessValue<TValue> right,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Binary(left, right, Expr.Eq, sourceFile, sourceLine, sourceMember, "Process equality expression");

    /// <summary>Creates a typed inequality expression.</summary>
    /// <typeparam name="TValue">Shared operand value type.</typeparam>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Boolean canonical expression.</returns>
    /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An operand belongs to another authoring session.</exception>
    public ProcessValue<bool> NotEqual<TValue>(
        ProcessValue<TValue> left,
        ProcessValue<TValue> right,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Binary(left, right, Expr.Ne, sourceFile, sourceLine, sourceMember, "Process inequality expression");

    /// <summary>Creates a typed Boolean conjunction.</summary>
    /// <param name="left">Left Boolean operand.</param>
    /// <param name="right">Right Boolean operand.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Boolean canonical expression.</returns>
    /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An operand belongs to another authoring session.</exception>
    public ProcessValue<bool> And(
        ProcessValue<bool> left,
        ProcessValue<bool> right,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Binary(left, right, Expr.And, sourceFile, sourceLine, sourceMember, "Process Boolean conjunction");

    /// <summary>Creates a typed Boolean disjunction.</summary>
    /// <param name="left">Left Boolean operand.</param>
    /// <param name="right">Right Boolean operand.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Boolean canonical expression.</returns>
    /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An operand belongs to another authoring session.</exception>
    public ProcessValue<bool> Or(
        ProcessValue<bool> left,
        ProcessValue<bool> right,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Binary(left, right, Expr.Or, sourceFile, sourceLine, sourceMember, "Process Boolean disjunction");

    /// <summary>Creates a typed Boolean negation.</summary>
    /// <param name="operand">Boolean operand.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Boolean canonical expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="operand"/> belongs to another authoring session.</exception>
    public ProcessValue<bool> Not(
        ProcessValue<bool> operand,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(operand);
        return context.Value<bool>(
            Expr.Not(operand.Expression),
            context.Contract<bool>(),
            context.Source(sourceFile, sourceLine, sourceMember, "Process Boolean negation"));
    }

    /// <summary>Creates a typed conditional value expression.</summary>
    /// <typeparam name="TValue">Shared result type of the two alternatives.</typeparam>
    /// <param name="condition">Boolean selection condition.</param>
    /// <param name="whenTrue">Value selected when <paramref name="condition"/> is true.</param>
    /// <param name="whenFalse">Value selected when <paramref name="condition"/> is false.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed conditional canonical expression.</returns>
    /// <exception cref="ArgumentNullException">A supplied value is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A supplied value belongs to another authoring session.</exception>
    public ProcessValue<TValue> Conditional<TValue>(
        ProcessValue<bool> condition,
        ProcessValue<TValue> whenTrue,
        ProcessValue<TValue> whenFalse,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(condition);
        context.RequireValue(whenTrue);
        context.RequireValue(whenFalse);
        if (whenTrue.Contract != whenFalse.Contract)
        {
            throw new InvalidOperationException(
                "A typed Process conditional requires both alternatives to have the same exact value contract.");
        }
        return context.Value<TValue>(
            Expr.If(condition.Expression, whenTrue.Expression, whenFalse.Expression),
            whenTrue.Contract,
            context.Source(sourceFile, sourceLine, sourceMember, "Process conditional expression"));
    }

    /// <summary>Declares a typed output binding that a continuation or clause may populate.</summary>
    /// <remarks>
    /// CLR nullable-reference annotations are not reified in generic <see cref="Type"/> values. Use the overload
    /// accepting an explicit <see cref="ValueContract"/> when output reference nullability is semantic.
    /// </remarks>
    /// <typeparam name="TValue">CLR type projected into the output contract.</typeparam>
    /// <param name="binding">Stable value-binding identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this authoring session.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    public ProcessBinding<TValue> Output<TValue>(
        ValueBindingId binding,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A Process output requires a stable binding identity.", nameof(binding));
        return Output<TValue>(binding, context.Contract<TValue>(), sourceFile, sourceLine, sourceMember);
    }

    /// <summary>Declares a typed output binding with an explicit portable occurrence contract.</summary>
    /// <typeparam name="TValue">CLR type represented by the output handle.</typeparam>
    /// <param name="binding">Stable value-binding identity.</param>
    /// <param name="contract">
    /// Exact portable contract, including occurrence presence and nullability, of the produced output.
    /// </param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this authoring session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    public ProcessBinding<TValue> Output<TValue>(
        ValueBindingId binding,
        ValueContract contract,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A Process output requires a stable binding identity.", nameof(binding));
        ArgumentNullException.ThrowIfNull(contract);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Process output '{binding.Value}'");
        var output = new ProcessOutputBinding(binding, contract);
        context.Register(output, source);
        var authored = new ProcessBinding<TValue>(context, binding, output.Contract, output);
        context.RegisterIfAbsent(authored.Expression, source);
        return authored;
    }

    /// <summary>Declares a typed output binding using a deterministic owner-relative identity.</summary>
    /// <typeparam name="TValue">CLR type projected into the output contract.</typeparam>
    /// <param name="owner">Stable identity of the construct that owns the output.</param>
    /// <param name="role">Stable semantic role of the output within <paramref name="owner"/>.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed output binding scoped to this authoring session.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public ProcessBinding<TValue> Output<TValue>(
        ExecutionNodeId owner,
        string role,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Output<TValue>(ProcessAuthoringIdentities.BindingFor(owner, role), sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a binding that retains one admitted inbound Request obligation.</summary>
    /// <param name="binding">Stable Request-obligation binding identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Request-obligation handle scoped to this authoring session.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    public ProcessRequestObligation RequestObligation(
        RequestObligationBindingId binding,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A Process Request obligation requires a stable binding identity.", nameof(binding));
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Request obligation '{binding.Value}'");
        var canonical = new ProcessRequestObligationBinding(binding);
        context.Register(canonical, source);
        return new(context, canonical);
    }

    /// <summary>Declares a Request-obligation binding using a deterministic owner-relative identity.</summary>
    /// <param name="owner">Stable identity of the construct that owns the obligation.</param>
    /// <param name="role">Stable semantic role of the obligation within <paramref name="owner"/>.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed Request-obligation handle scoped to this authoring session.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public ProcessRequestObligation RequestObligation(
        ExecutionNodeId owner,
        string role,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        RequestObligation(
            ProcessAuthoringIdentities.RequestObligationFor(owner, role),
            sourceFile,
            sourceLine,
            sourceMember);

    /// <summary>Creates a stable directed Process edge.</summary>
    /// <param name="id">Stable edge identity.</param>
    /// <param name="target">Stable target node identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical directed edge.</returns>
    public ProcessEdge Edge(
        ProcessEdgeId id,
        ExecutionNodeId target,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Process edge '{id.Value}'");
        var edge = new ProcessEdge(id, target);
        context.Register(edge, source);
        return edge;
    }

    /// <summary>Creates a directed edge using a deterministic owner-relative identity.</summary>
    /// <param name="owner">Stable identity of the construct that owns the edge.</param>
    /// <param name="role">Stable semantic role of the edge within <paramref name="owner"/>.</param>
    /// <param name="target">Stable target node identity.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical directed edge.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is default, or <paramref name="role"/> is blank or contains a path separator.
    /// </exception>
    public ProcessEdge Edge(
        ExecutionNodeId owner,
        string role,
        ExecutionNodeId target,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        Edge(ProcessAuthoringIdentities.EdgeFor(owner, role), target, sourceFile, sourceLine, sourceMember);

    /// <summary>Creates a continuation without an output binding.</summary>
    /// <param name="edge">Stable edge selected by the continuation.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical continuation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edge"/> is <see langword="null"/>.</exception>
    public ProcessContinuation Continuation(
        ProcessEdge edge,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(edge);
        var source = context.Source(sourceFile, sourceLine, sourceMember, "Process continuation");
        var continuation = new ProcessContinuation(edge);
        context.Register(continuation, source);
        return continuation;
    }

    /// <summary>Creates a continuation that populates one typed output binding.</summary>
    /// <typeparam name="TValue">CLR type of the continuation output.</typeparam>
    /// <param name="edge">Stable edge selected by the continuation.</param>
    /// <param name="output">Typed binding populated by the selected operation or interaction result.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical typed continuation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edge"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="output"/> is an input or foreign binding.</exception>
    public ProcessContinuation Continuation<TValue>(
        ProcessEdge edge,
        ProcessBinding<TValue> output,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(edge);
        context.RequireBinding(output);
        var source = context.Source(sourceFile, sourceLine, sourceMember, "Typed Process continuation");
        var continuation = new ProcessContinuation(edge, output.RequireOutput());
        context.Register(continuation, source);
        return continuation;
    }

    /// <summary>Creates one terminal-outcome branch for an ordinary or child Process Request.</summary>
    /// <param name="id">Stable branch identity.</param>
    /// <param name="outcome">Stable Request terminal-outcome identity.</param>
    /// <param name="continuation">Continuation selected when the outcome is accepted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical Request outcome branch.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    public ProcessRequestOutcomeBranch RequestOutcome(
        ExecutionNodeId id,
        RequestTerminalOutcomeId outcome,
        ProcessContinuation continuation,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Request outcome '{id.Value}'");
        var branch = new ProcessRequestOutcomeBranch(id, outcome, continuation);
        context.Register(branch, source);
        return branch;
    }

    /// <summary>Creates one ordered Boolean Choice case.</summary>
    /// <param name="id">Stable case identity.</param>
    /// <param name="predicate">Typed Boolean branch predicate.</param>
    /// <param name="next">Edge selected when the predicate matches.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical ordered Choice case.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="predicate"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="predicate"/> belongs to another session.</exception>
    public ProcessChoiceCase ChoiceCase(
        ExecutionNodeId id,
        ProcessValue<bool> predicate,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(predicate);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Choice case '{id.Value}'");
        var choiceCase = new ProcessChoiceCase(id, predicate.Expression, next);
        context.Register(choiceCase, source);
        return choiceCase;
    }

    /// <summary>Creates an explicit Choice or Match fallback.</summary>
    /// <param name="id">Stable fallback identity.</param>
    /// <param name="next">Stable fallback edge.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical fallback.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public ProcessFallback Fallback(
        ExecutionNodeId id,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Process fallback '{id.Value}'");
        var fallback = new ProcessFallback(id, next);
        context.Register(fallback, source);
        return fallback;
    }

    /// <summary>Creates one ordered exact-value Match case.</summary>
    /// <typeparam name="TValue">CLR type projected into the Match contract.</typeparam>
    /// <param name="id">Stable case identity.</param>
    /// <param name="pattern">Exact portable pattern value.</param>
    /// <param name="next">Edge selected when the pattern matches.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical ordered Match case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="pattern"/> cannot be represented portably.</exception>
    public ProcessMatchCase MatchCase<TValue>(
        ExecutionNodeId id,
        TValue pattern,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Match case '{id.Value}'");
        var canonicalPattern = context.Pattern(pattern, context.Contract<TValue>(), source);
        var matchCase = new ProcessMatchCase(id, canonicalPattern, next);
        context.Register(matchCase, source);
        return matchCase;
    }

    /// <summary>Creates one ordered exact-value Match case using the matched value's exact occurrence contract.</summary>
    /// <typeparam name="TValue">CLR type represented by the matched value and pattern.</typeparam>
    /// <param name="id">Stable case identity.</param>
    /// <param name="matchedValue">
    /// Typed matched-value basis whose exact contract, including optionality and nullability, applies to the pattern.
    /// </param>
    /// <param name="pattern">Exact portable pattern value.</param>
    /// <param name="next">Edge selected when the pattern matches.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical ordered Match case.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="matchedValue"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="matchedValue"/> belongs to another session.</exception>
    /// <exception cref="NotSupportedException"><paramref name="pattern"/> cannot be represented portably.</exception>
    public ProcessMatchCase MatchCase<TValue>(
        ExecutionNodeId id,
        ProcessValue<TValue> matchedValue,
        TValue pattern,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(matchedValue);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Match case '{id.Value}'");
        var canonicalPattern = context.Pattern(pattern, matchedValue.Contract, source);
        var matchCase = new ProcessMatchCase(id, canonicalPattern, next);
        context.Register(matchCase, source);
        return matchCase;
    }

    /// <summary>Creates one stable token branch for a reciprocal Fork and Join.</summary>
    /// <param name="id">Stable branch identity.</param>
    /// <param name="start">Edge that starts the branch token.</param>
    /// <param name="capacityDomain">Optional canonical capacity domain consumed while the branch is active.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical Fork branch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="start"/> is <see langword="null"/>.</exception>
    public ProcessForkBranch ForkBranch(
        ExecutionNodeId id,
        ProcessEdge start,
        string? capacityDomain = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(start);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Fork branch '{id.Value}'");
        var branch = new ProcessForkBranch(id, start, capacityDomain);
        context.Register(branch, source);
        return branch;
    }

    /// <summary>Creates one typed interaction clause for a durable AwaitMatch.</summary>
    /// <typeparam name="TValue">CLR type projected into the admitted interaction-input contract.</typeparam>
    /// <param name="id">Stable clause identity.</param>
    /// <param name="contract">Exact typed interaction contract admitted by the clause.</param>
    /// <param name="input">Typed binding made visible to the guard and selected continuation.</param>
    /// <param name="requestObligation">Optional binding retaining an admitted Request obligation.</param>
    /// <param name="guard">Optional Boolean eligibility guard.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="continuation">Typed continuation selected when this clause wins.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical interaction AwaitMatch clause.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="input"/>, or <paramref name="continuation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A supplied handle belongs to another authoring session, or <paramref name="input"/> is the invocation-input
    /// binding and cannot receive an interaction value.
    /// </exception>
    public ProcessAwaitInteractionClause AwaitInteractionClause<TValue>(
        ExecutionNodeId id,
        InteractionContractReference contract,
        ProcessBinding<TValue> input,
        ProcessRequestObligation? requestObligation,
        ProcessValue<bool>? guard,
        int priority,
        ProcessContinuation continuation,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        context.RequireBinding(input);
        if (requestObligation is not null)
            context.RequireObligation(requestObligation);
        if (guard is not null)
            context.RequireValue(guard);
        ArgumentNullException.ThrowIfNull(continuation);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Await interaction clause '{id.Value}'");
        var clause = new ProcessAwaitInteractionClause(
            id,
            contract,
            input.RequireOutput(),
            requestObligation?.CanonicalBinding,
            guard?.Expression,
            priority,
            continuation);
        context.Register(clause, source);
        return clause;
    }

    /// <summary>Creates one absolute-time timer clause for a durable AwaitMatch.</summary>
    /// <param name="id">Stable clause identity.</param>
    /// <param name="dueAt">Typed absolute due instant.</param>
    /// <param name="priority">Explicit arbitration priority.</param>
    /// <param name="continuation">Continuation selected when the timer clause wins.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>The canonical timer AwaitMatch clause.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dueAt"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="dueAt"/> belongs to another authoring session.</exception>
    public ProcessAwaitTimerClause AwaitTimerClause(
        ExecutionNodeId id,
        ProcessValue<DateTimeOffset> dueAt,
        int priority,
        ProcessContinuation continuation,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(dueAt);
        ArgumentNullException.ThrowIfNull(continuation);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Await timer clause '{id.Value}'");
        var clause = new ProcessAwaitTimerClause(id, dueAt.Expression, priority, continuation);
        context.Register(clause, source);
        return clause;
    }

    internal CanonicalProcessDefinition Build()
    {
        if (nodes.Count == 0)
            throw new InvalidOperationException("Canonical Process authoring requires at least one graph node.");
        var entry = derivedEntry ?? context.Metadata.EntryId;
        if (entry is null)
        {
            throw new InvalidOperationException(
                "Low-level Process authoring requires an explicit entry identity; use a higher-level frontend to derive it.");
        }
        return new(
            context.InputContract,
            context.ResultContract,
            entry.Value,
            [.. nodes],
            context.Metadata.RecoveryPolicy);
    }

    ProcessValue<bool> Binary<TValue>(
        ProcessValue<TValue> left,
        ProcessValue<TValue> right,
        Func<Expr, Expr, Expr> operation,
        string sourceFile,
        int sourceLine,
        string sourceMember,
        string description)
    {
        context.RequireValue(left);
        context.RequireValue(right);
        return context.Value<bool>(
            operation(left.Expression, right.Expression),
            context.Contract<bool>(),
            context.Source(sourceFile, sourceLine, sourceMember, description));
    }

    ProcessBuilder<TInput, TResult> Add(ProcessNode node, AuthoredProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!nodeIds.Add(node.Id))
            throw new InvalidOperationException($"Process node identity '{node.Id.Value}' was authored more than once.");
        nodes.Add(node);
        context.Register(node, source);
        return this;
    }
}
