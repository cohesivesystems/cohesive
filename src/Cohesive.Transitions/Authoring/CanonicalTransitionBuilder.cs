using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Authoring;

/// <summary>Typed authoring handle for one immutable lexical binding in canonical Transition IR.</summary>
/// <typeparam name="TValue">CLR type projected into the local value contract.</typeparam>
public sealed class TransitionLocal<TValue>
{
    readonly object owner;
    readonly TransitionAuthoringScope scope;

    internal TransitionLocal(
        object owner,
        TransitionAuthoringScope scope,
        ValueBindingId binding,
        ValueContract contract)
    {
        this.owner = owner;
        this.scope = scope;
        Binding = binding;
        Contract = contract;
    }

    /// <summary>Stable canonical binding identity.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Portable semantic contract of the complete bound value.</summary>
    public ValueContract Contract { get; }

    /// <summary>Canonical expression referencing the complete bound value.</summary>
    public Expr Expression => Expr.BoundValue(Binding);

    internal void RequireVisible(object expectedOwner, TransitionAuthoringScope currentScope)
    {
        if (!ReferenceEquals(owner, expectedOwner))
        {
            throw new InvalidOperationException(
                $"Transition local '{Binding.Value}' belongs to another authoring session.");
        }
        if (!scope.IsAncestorOf(currentScope))
        {
            throw new InvalidOperationException(
                $"Transition local '{Binding.Value}' is not visible from this lexical branch.");
        }
    }
}

/// <summary>
/// Authors an ordered finite canonical Transition sequence, including lexical locals, structured branching,
/// algebraic sparse patches, exact interaction emissions, Machine movements, and typed terminal outcomes.
/// </summary>
/// <typeparam name="TEntity">Entity authoring type used by observation-field selectors.</typeparam>
/// <typeparam name="TInput">Typed invocation input.</typeparam>
/// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
public class TransitionSequenceBuilder<TEntity, TInput, TOutcome>
    where TEntity : Entity
{
    readonly List<TransitionNode> steps = [];

    internal TransitionSequenceBuilder(
        TransitionAuthoringContext<TEntity, TInput, TOutcome> context,
        TransitionAuthoringScope scope)
    {
        Context = context;
        Scope = scope;
    }

    internal TransitionAuthoringContext<TEntity, TInput, TOutcome> Context { get; }

    internal TransitionAuthoringScope Scope { get; }

    /// <summary>Adds one immutable lexical local binding.</summary>
    /// <typeparam name="TValue">CLR type of the local value.</typeparam>
    /// <param name="id">Stable identity of the Let node.</param>
    /// <param name="binding">Stable identity referenced by later canonical expressions.</param>
    /// <param name="value">Restricted pure expression producing the local value.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed local handle visible in the current sequence and its descendant branches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="TransitionExpressionTranslationException">The expression is outside the portable subset.</exception>
    public TransitionLocal<TValue> Let<TValue>(
        ExecutionNodeId id,
        ValueBindingId binding,
        Expression<Func<TEntity, TInput, TValue>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        var expression = Context.Translate(value);
        var contract = Context.ResolveContract(expression, Context.Contract<TValue>());
        var node = new LetTransitionNode(
            id,
            binding,
            contract,
            expression);
        Add(node, Context.Source(sourceFile, sourceLine, sourceMember, $"Let '{id.Value}'"));
        return new(Context.Owner, Scope, binding, contract);
    }

    /// <summary>Adds a Set sparse patch produced by a restricted typed expression.</summary>
    /// <typeparam name="TValue">CLR value type of the target field.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct entity field selector.</param>
    /// <param name="value">Pure replacement-value expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException">A selector or value is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Set<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<TValue>>> field,
        Expression<Func<TEntity, TInput, TValue>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        return AddPatch(
            id,
            Context.FieldPath(field),
            new SetTransitionPatch(Context.Translate(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Set '{id.Value}'"));
    }

    /// <summary>Adds a Set sparse patch containing a portable constant.</summary>
    /// <typeparam name="TValue">CLR value type of the target field.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct entity field selector.</param>
    /// <param name="value">Constant replacement value.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="value"/> cannot be represented portably.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> cannot be projected as an observation value.</exception>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="value"/> contains invalid JSON data.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="field"/> is not a direct entity field selector.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Set<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<TValue>>> field,
        TValue value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        return AddPatch(
            id,
            Context.FieldPath(field),
            new SetTransitionPatch(Context.Constant(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Set '{id.Value}'"));
    }

    /// <summary>Adds a Set sparse patch from a visible typed local.</summary>
    /// <typeparam name="TValue">CLR value type of the target field and local.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct entity field selector.</param>
    /// <param name="value">Visible lexical local supplying the replacement value.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is foreign or not lexically visible.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="field"/> is not a direct entity field selector.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Set<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<TValue>>> field,
        TransitionLocal<TValue> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        RequireLocal(value);
        return AddPatch(
            id,
            Context.FieldPath(field),
            new SetTransitionPatch(value.Expression),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Set '{id.Value}'"));
    }

    /// <summary>Adds a Remove sparse patch distinct from setting the target to null.</summary>
    /// <typeparam name="TValue">CLR value type of the target field.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct entity field selector.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="field"/> is not a direct entity field selector.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Remove<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<TValue>>> field,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        return AddPatch(
            id,
            Context.FieldPath(field),
            new RemoveTransitionPatch(),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Remove '{id.Value}'"));
    }

    /// <summary>Adds a numeric Increment sparse patch.</summary>
    /// <typeparam name="TValue">CLR numeric value type checked by canonical compilation.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct numeric field selector.</param>
    /// <param name="amount">Pure increment expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="amount"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException">The field selector or <paramref name="amount"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Increment<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<TValue>>> field,
        Expression<Func<TEntity, TInput, TValue>> amount,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(amount);
        return AddPatch(
            id,
            Context.FieldPath(field),
            new IncrementTransitionPatch(Context.Translate(amount)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Increment '{id.Value}'"));
    }

    /// <summary>Adds one value to a semantic set.</summary>
    /// <typeparam name="TValue">CLR element type of the set-like collection.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct many-valued entity field selector.</param>
    /// <param name="value">Pure candidate-element expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException">A selector or value is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> AddToSet<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<IReadOnlyList<TValue>>>> field,
        Expression<Func<TEntity, TInput, TValue>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        return AddPatch(
            id,
            Context.CollectionFieldPath(field),
            new AddToSetTransitionPatch(Context.Translate(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Add-to-set '{id.Value}'"));
    }

    /// <summary>Appends one value to an ordered collection.</summary>
    /// <typeparam name="TValue">CLR element type of the ordered collection.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct many-valued entity field selector.</param>
    /// <param name="value">Pure appended-value expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException">A selector or value is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Append<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<IReadOnlyList<TValue>>>> field,
        Expression<Func<TEntity, TInput, TValue>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        return AddPatch(
            id,
            Context.CollectionFieldPath(field),
            new AppendTransitionPatch(Context.Translate(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Append '{id.Value}'"));
    }

    /// <summary>Upserts one owned child selected by semantic identity.</summary>
    /// <typeparam name="TChild">CLR child value type.</typeparam>
    /// <typeparam name="TIdentity">CLR child-identity type.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct many-valued owned-child field selector.</param>
    /// <param name="identityField">Child-relative identity selector.</param>
    /// <param name="identity">Pure child-identity expression.</param>
    /// <param name="value">Pure complete replacement-child expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException">A selector or value expression is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="identityField"/> is not a readable property chain.</exception>
    /// <exception cref="TransitionExpressionTranslationException">An expression is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> UpsertOwnedChild<TChild, TIdentity>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<IReadOnlyList<TChild>>>> field,
        Expression<Func<TChild, TIdentity>> identityField,
        Expression<Func<TEntity, TInput, TIdentity>> identity,
        Expression<Func<TEntity, TInput, TChild>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(identityField);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(value);
        return AddPatch(
            id,
            Context.CollectionFieldPath(field),
            new UpsertOwnedChildTransitionPatch(
                TransitionAuthoringMemberPath.From(identityField),
                Context.Translate(identity),
                Context.Translate(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Owned-child upsert '{id.Value}'"));
    }

    /// <summary>Removes one owned child selected by semantic identity.</summary>
    /// <typeparam name="TChild">CLR child value type.</typeparam>
    /// <typeparam name="TIdentity">CLR child-identity type.</typeparam>
    /// <param name="id">Stable update-node identity.</param>
    /// <param name="field">Direct many-valued owned-child field selector.</param>
    /// <param name="identityField">Child-relative identity selector.</param>
    /// <param name="identity">Pure child-identity expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException">A selector or identity expression is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="identityField"/> is not a readable property chain.</exception>
    /// <exception cref="TransitionExpressionTranslationException">The field selector or <paramref name="identity"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> RemoveOwnedChild<TChild, TIdentity>(
        ExecutionNodeId id,
        Expression<Func<TEntity, Field<IReadOnlyList<TChild>>>> field,
        Expression<Func<TChild, TIdentity>> identityField,
        Expression<Func<TEntity, TInput, TIdentity>> identity,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(identityField);
        ArgumentNullException.ThrowIfNull(identity);
        return AddPatch(
            id,
            Context.CollectionFieldPath(field),
            new RemoveOwnedChildTransitionPatch(
                TransitionAuthoringMemberPath.From(identityField),
                Context.Translate(identity)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Owned-child removal '{id.Value}'"));
    }

    /// <summary>Adds an explicitly ordered predicate Choice.</summary>
    /// <param name="id">Stable Choice-node identity.</param>
    /// <param name="configure">Builder callback declaring cases and completeness.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Choice completeness declarations contradict one another.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Choose(
        ExecutionNodeId id,
        Action<TransitionChoiceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(configure);
        var source = Context.Source(sourceFile, sourceLine, sourceMember, $"Choice '{id.Value}'");
        var choice = new TransitionChoiceBuilder<TEntity, TInput, TOutcome>(Context, Scope);
        configure(choice);
        Add(choice.Build(id), source);
        return this;
    }

    /// <summary>Adds an explicitly typed exact-pattern Match.</summary>
    /// <typeparam name="TValue">CLR value type being matched.</typeparam>
    /// <param name="id">Stable Match-node identity.</param>
    /// <param name="value">Pure expression yielding the matched value.</param>
    /// <param name="configure">Builder callback declaring cases and completeness.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="value"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Match<TValue>(
        ExecutionNodeId id,
        Expression<Func<TEntity, TInput, TValue>> value,
        Action<TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(configure);
        var source = Context.Source(sourceFile, sourceLine, sourceMember, $"Match '{id.Value}'");
        var expression = Context.Translate(value);
        var match = new TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue>(
            Context,
            Scope,
            Context.ResolveContract(expression, Context.Contract<TValue>()));
        configure(match);
        Add(match.Build(id, expression), source);
        return this;
    }

    /// <summary>Adds an explicitly typed Match over a visible lexical local.</summary>
    /// <typeparam name="TValue">CLR value type being matched.</typeparam>
    /// <param name="id">Stable Match-node identity.</param>
    /// <param name="value">Visible local yielding the matched value.</param>
    /// <param name="configure">Builder callback declaring cases and completeness.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is foreign or not visible.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Match<TValue>(
        ExecutionNodeId id,
        TransitionLocal<TValue> value,
        Action<TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(configure);
        RequireLocal(value);
        var source = Context.Source(sourceFile, sourceLine, sourceMember, $"Match '{id.Value}'");
        var match = new TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue>(
            Context,
            Scope,
            value.Contract);
        configure(match);
        Add(match.Build(id, value.Expression), source);
        return this;
    }

    /// <summary>Adds a pure exact-contract interaction emission intent.</summary>
    /// <typeparam name="TPayload">CLR payload type lowered into a portable expression.</typeparam>
    /// <param name="id">Stable emission-node identity.</param>
    /// <param name="contract">Exact interaction definition reference owning event/request semantics.</param>
    /// <param name="payload">Pure typed payload expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> or <paramref name="payload"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="payload"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Emit<TPayload>(
        ExecutionNodeId id,
        ExecutionDefinitionReference contract,
        Expression<Func<TEntity, TInput, TPayload>> payload,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(payload);
        Add(
            new EmitTransitionNode(id, contract, Context.Translate(payload)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Emission '{id.Value}'"));
        return this;
    }

    /// <summary>Adds an exact Machine edge movement.</summary>
    /// <param name="id">Stable movement-node identity.</param>
    /// <param name="machine">Exact referenced Machine definition.</param>
    /// <param name="edge">Stable edge identity owned by the Machine.</param>
    /// <param name="rejection">Typed rejection expression used when the source configuration is illegal.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="rejection"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="rejection"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> MoveMachine(
        ExecutionNodeId id,
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        Expression<Func<TEntity, TInput, TOutcome>> rejection,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(rejection);
        Add(
            new MoveMachineTransitionNode(id, machine, edge, Context.Translate(rejection)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Machine movement '{id.Value}'"));
        return this;
    }

    /// <summary>Adds one typed terminal outcome expression.</summary>
    /// <param name="id">Stable outcome-node identity.</param>
    /// <param name="disposition">Applied, no-change, or domain-rejected disposition.</param>
    /// <param name="value">Pure typed outcome expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="value"/> is outside the portable subset.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Return(
        ExecutionNodeId id,
        TransitionOutcomeDisposition disposition,
        Expression<Func<TEntity, TInput, TOutcome>> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        Add(
            new OutcomeTransitionNode(id, disposition, Context.Translate(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Outcome '{id.Value}'"));
        return this;
    }

    /// <summary>Adds one typed terminal constant outcome.</summary>
    /// <param name="id">Stable outcome-node identity.</param>
    /// <param name="disposition">Applied, no-change, or domain-rejected disposition.</param>
    /// <param name="value">Portable constant outcome value.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> cannot be represented portably.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> cannot be projected as an observation value.</exception>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="value"/> contains invalid JSON data.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Return(
        ExecutionNodeId id,
        TransitionOutcomeDisposition disposition,
        TOutcome value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        Add(
            new OutcomeTransitionNode(id, disposition, Context.Constant(value)),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Outcome '{id.Value}'"));
        return this;
    }

    /// <summary>Adds one typed terminal outcome from a visible lexical local.</summary>
    /// <param name="id">Stable outcome-node identity.</param>
    /// <param name="disposition">Applied, no-change, or domain-rejected disposition.</param>
    /// <param name="value">Visible outcome-typed local.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This sequence builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is foreign or not visible.</exception>
    public TransitionSequenceBuilder<TEntity, TInput, TOutcome> Return(
        ExecutionNodeId id,
        TransitionOutcomeDisposition disposition,
        TransitionLocal<TOutcome> value,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        RequireLocal(value);
        Add(
            new OutcomeTransitionNode(id, disposition, value.Expression),
            Context.Source(sourceFile, sourceLine, sourceMember, $"Outcome '{id.Value}'"));
        return this;
    }

    internal SequenceTransitionNode BuildSequence(
        ExecutionNodeId id,
        AuthoredTransitionSource source)
    {
        var sequence = new SequenceTransitionNode(id, [.. steps]);
        Context.Register(sequence, source);
        return sequence;
    }

    internal void Add(TransitionNode node, AuthoredTransitionSource source)
    {
        steps.Add(node);
        Context.Register(node, source);
    }

    TransitionSequenceBuilder<TEntity, TInput, TOutcome> AddPatch(
        ExecutionNodeId id,
        FieldPath path,
        TransitionPatchOperation operation,
        AuthoredTransitionSource source)
    {
        Add(new UpdateTransitionNode(id, path, operation), source);
        return this;
    }

    void RequireLocal<TValue>(TransitionLocal<TValue> local)
    {
        ArgumentNullException.ThrowIfNull(local);
        local.RequireVisible(Context.Owner, Scope);
    }
}

/// <summary>Root canonical Transition builder with ordered admission rules and candidate-state invariants.</summary>
/// <typeparam name="TEntity">Entity authoring type used by observation-field selectors.</typeparam>
/// <typeparam name="TInput">Typed invocation input.</typeparam>
/// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
public sealed class TransitionBuilder<TEntity, TInput, TOutcome>
    : TransitionSequenceBuilder<TEntity, TInput, TOutcome>
    where TEntity : Entity
{
    readonly List<TransitionAdmissionRule> preconditions = [];
    readonly List<TransitionInvariant> invariants = [];

    internal TransitionBuilder(TransitionAuthoringContext<TEntity, TInput, TOutcome> context)
        : base(context, new(parent: null))
    {
    }

    /// <summary>Adds one ordered admission rule with a typed rejection outcome.</summary>
    /// <param name="id">Stable admission-rule identity.</param>
    /// <param name="predicate">Pure predicate evaluated against original observation and input.</param>
    /// <param name="rejection">Pure typed outcome returned when the predicate is false.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Transition builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> or <paramref name="rejection"/> is null.</exception>
    /// <exception cref="TransitionExpressionTranslationException">An expression is outside the portable subset.</exception>
    public TransitionBuilder<TEntity, TInput, TOutcome> Requires(
        ExecutionNodeId id,
        Expression<Func<TEntity, TInput, bool>> predicate,
        Expression<Func<TEntity, TInput, TOutcome>> rejection,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(rejection);
        var rule = new TransitionAdmissionRule(id, Context.Translate(predicate), Context.Translate(rejection));
        preconditions.Add(rule);
        Context.Register(
            rule,
            Context.Source(sourceFile, sourceLine, sourceMember, $"Admission '{id.Value}'"));
        return this;
    }

    /// <summary>Adds one candidate-state invariant.</summary>
    /// <param name="id">Stable invariant identity.</param>
    /// <param name="predicate">Pure predicate evaluated against the candidate observation.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Transition builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="predicate"/> is outside the portable subset.</exception>
    public TransitionBuilder<TEntity, TInput, TOutcome> Invariant(
        ExecutionNodeId id,
        Expression<Func<TEntity, bool>> predicate,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var invariant = new TransitionInvariant(id, Context.Translate(predicate));
        invariants.Add(invariant);
        Context.Register(
            invariant,
            Context.Source(sourceFile, sourceLine, sourceMember, $"Invariant '{id.Value}'"));
        return this;
    }

    internal IR.TransitionDefinition Build(
        ExecutionNodeId bodyId,
        AuthoredTransitionSource rootSource) => new(
        Context.InputContract,
        Context.ObservationContract,
        Context.OutcomeContract,
        [.. preconditions],
        BuildSequence(bodyId, rootSource),
        [.. invariants]);
}

/// <summary>Authors ordered predicate cases and explicit completeness for one canonical Choice.</summary>
/// <typeparam name="TEntity">Entity authoring type.</typeparam>
/// <typeparam name="TInput">Typed invocation input.</typeparam>
/// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
public sealed class TransitionChoiceBuilder<TEntity, TInput, TOutcome>
    where TEntity : Entity
{
    readonly TransitionAuthoringContext<TEntity, TInput, TOutcome> context;
    readonly TransitionAuthoringScope parentScope;
    readonly List<TransitionChoiceCase> cases = [];
    TransitionFallback? fallback;
    BranchCompleteness completeness;

    internal TransitionChoiceBuilder(
        TransitionAuthoringContext<TEntity, TInput, TOutcome> context,
        TransitionAuthoringScope parentScope)
    {
        this.context = context;
        this.parentScope = parentScope;
    }

    /// <summary>Adds one ordered predicate case.</summary>
    /// <param name="id">Stable case identity.</param>
    /// <param name="predicate">Pure branch predicate.</param>
    /// <param name="configure">Finite callback authoring the branch body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Choice builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default identity.</exception>
    /// <exception cref="TransitionExpressionTranslationException"><paramref name="predicate"/> is outside the portable subset.</exception>
    public TransitionChoiceBuilder<TEntity, TInput, TOutcome> Case(
        ExecutionNodeId id,
        Expression<Func<TEntity, TInput, bool>> predicate,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(configure);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Choice case '{id.Value}'");
        var branch = new TransitionSequenceBuilder<TEntity, TInput, TOutcome>(
            context,
            new(parentScope));
        configure(branch);
        var body = branch.BuildSequence(TransitionAuthoringIdentities.BodyFor(id), source);
        var choiceCase = new TransitionChoiceCase(id, context.Translate(predicate), body);
        cases.Add(choiceCase);
        context.Register(choiceCase, source);
        return this;
    }

    /// <summary>Declares that the ordered cases are intended to be statically exhaustive.</summary>
    /// <returns>This Choice builder.</returns>
    /// <exception cref="InvalidOperationException">A fallback was already declared.</exception>
    public TransitionChoiceBuilder<TEntity, TInput, TOutcome> Exhaustive()
    {
        if (fallback is not null || completeness == BranchCompleteness.Fallback)
            throw new InvalidOperationException("An exhaustive Choice cannot also declare a fallback.");
        completeness = BranchCompleteness.Exhaustive;
        return this;
    }

    /// <summary>Adds the explicit branch selected when no predicate case matches.</summary>
    /// <param name="id">Stable fallback identity.</param>
    /// <param name="configure">Finite callback authoring the fallback body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Choice builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default identity.</exception>
    /// <exception cref="InvalidOperationException">Exhaustive completeness or another fallback was already declared.</exception>
    public TransitionChoiceBuilder<TEntity, TInput, TOutcome> Fallback(
        ExecutionNodeId id,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (completeness == BranchCompleteness.Exhaustive || fallback is not null)
            throw new InvalidOperationException("A Choice can declare either exhaustive cases or one fallback.");
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Choice fallback '{id.Value}'");
        var branch = new TransitionSequenceBuilder<TEntity, TInput, TOutcome>(context, new(parentScope));
        configure(branch);
        fallback = new(id, branch.BuildSequence(TransitionAuthoringIdentities.BodyFor(id), source));
        context.Register(fallback, source);
        completeness = BranchCompleteness.Fallback;
        return this;
    }

    internal ChoiceTransitionNode Build(ExecutionNodeId id) => new(
        id,
        CaseSelection.OrderedFirstMatch,
        completeness,
        [.. cases],
        fallback);
}

/// <summary>Authors exact portable patterns and explicit completeness for one canonical Match.</summary>
/// <typeparam name="TEntity">Entity authoring type.</typeparam>
/// <typeparam name="TInput">Typed invocation input.</typeparam>
/// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
/// <typeparam name="TValue">Typed value matched by every case.</typeparam>
public sealed class TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue>
    where TEntity : Entity
{
    readonly TransitionAuthoringContext<TEntity, TInput, TOutcome> context;
    readonly TransitionAuthoringScope parentScope;
    readonly ValueContract contract;
    readonly List<TransitionMatchCase> cases = [];
    TransitionFallback? fallback;
    BranchCompleteness completeness;

    internal TransitionMatchBuilder(
        TransitionAuthoringContext<TEntity, TInput, TOutcome> context,
        TransitionAuthoringScope parentScope,
        ValueContract contract)
    {
        this.context = context;
        this.parentScope = parentScope;
        this.contract = contract;
    }

    /// <summary>Adds one exact portable constant case, including explicit null when supplied.</summary>
    /// <remarks>
    /// A null pattern is retained as explicit <c>Null</c> and produces a canonical validation diagnostic when the
    /// resolved Match contract is non-nullable; it is never treated as absence.
    /// </remarks>
    /// <param name="id">Stable case identity.</param>
    /// <param name="pattern">Exact typed pattern.</param>
    /// <param name="configure">Finite callback authoring the case body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Match builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default identity.</exception>
    /// <exception cref="NotSupportedException"><paramref name="pattern"/> cannot be represented portably.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="pattern"/> cannot be projected as an observation value.</exception>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="pattern"/> contains invalid JSON data.</exception>
    public TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue> Case(
        ExecutionNodeId id,
        TValue pattern,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") => AddCase(
        id,
        context.Pattern(pattern, contract),
        configure,
        context.Source(sourceFile, sourceLine, sourceMember, $"Match case '{id.Value}'"));

    /// <summary>Adds a case for an authoritatively absent matched value.</summary>
    /// <remarks>
    /// The absent pattern produces a canonical validation diagnostic when the resolved Match contract requires
    /// presence. Authoritative absence is distinct from an explicit null pattern.
    /// </remarks>
    /// <param name="id">Stable case identity.</param>
    /// <param name="configure">Finite callback authoring the case body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Match builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default identity.</exception>
    public TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue> Absent(
        ExecutionNodeId id,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") => AddCase(
        id,
        PortableValue.Absent(contract),
        configure,
        context.Source(sourceFile, sourceLine, sourceMember, $"Absent Match case '{id.Value}'"));

    /// <summary>Declares that the exact cases are intended to be statically exhaustive.</summary>
    /// <returns>This Match builder.</returns>
    /// <exception cref="InvalidOperationException">A fallback was already declared.</exception>
    public TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue> Exhaustive()
    {
        if (fallback is not null || completeness == BranchCompleteness.Fallback)
            throw new InvalidOperationException("An exhaustive Match cannot also declare a fallback.");
        completeness = BranchCompleteness.Exhaustive;
        return this;
    }

    /// <summary>Adds the explicit branch selected when no exact case matches.</summary>
    /// <param name="id">Stable fallback identity.</param>
    /// <param name="configure">Finite callback authoring the fallback body.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Match builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default identity.</exception>
    /// <exception cref="InvalidOperationException">Exhaustive completeness or another fallback was already declared.</exception>
    public TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue> Fallback(
        ExecutionNodeId id,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (completeness == BranchCompleteness.Exhaustive || fallback is not null)
            throw new InvalidOperationException("A Match can declare either exhaustive cases or one fallback.");
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Match fallback '{id.Value}'");
        var branch = new TransitionSequenceBuilder<TEntity, TInput, TOutcome>(context, new(parentScope));
        configure(branch);
        fallback = new(id, branch.BuildSequence(TransitionAuthoringIdentities.BodyFor(id), source));
        context.Register(fallback, source);
        completeness = BranchCompleteness.Fallback;
        return this;
    }

    internal MatchTransitionNode Build(ExecutionNodeId id, Expr value) => new(
        id,
        CaseSelection.OrderedFirstMatch,
        completeness,
        value,
        contract,
        [.. cases],
        fallback);

    TransitionMatchBuilder<TEntity, TInput, TOutcome, TValue> AddCase(
        ExecutionNodeId id,
        PortableValue pattern,
        Action<TransitionSequenceBuilder<TEntity, TInput, TOutcome>> configure,
        AuthoredTransitionSource source)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var branch = new TransitionSequenceBuilder<TEntity, TInput, TOutcome>(context, new(parentScope));
        configure(branch);
        var matchCase = new TransitionMatchCase(
            id,
            pattern,
            branch.BuildSequence(TransitionAuthoringIdentities.BodyFor(id), source));
        cases.Add(matchCase);
        context.Register(matchCase, source);
        return this;
    }
}

internal sealed record AuthoredTransitionSource(string Reference, string Description);

internal sealed class TransitionAuthoringScope(TransitionAuthoringScope? parent)
{
    public bool IsAncestorOf(TransitionAuthoringScope candidate)
    {
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(this, current))
                return true;
        }

        return false;
    }

    TransitionAuthoringScope? Parent { get; } = parent;
}

internal sealed class TransitionAuthoringContext<TEntity, TInput, TOutcome>
    where TEntity : Entity
{
    readonly TransitionExpressionTranslator<TEntity, TInput> translator;
    readonly IClrTypeRefMapper typeRefMapper;
    readonly Shape entityShape;
    readonly TransitionAuthoringMetadata metadata;
    readonly Dictionary<Type, ValueContract> contracts = [];
    readonly Dictionary<object, AuthoredTransitionSource> sources = new(ReferenceEqualityComparer.Instance);

    public TransitionAuthoringContext(
        Shape entityShape,
        TransitionAuthoringMetadata metadata,
        IClrTypeRefMapper typeRefMapper)
    {
        this.entityShape = entityShape;
        this.metadata = metadata;
        this.typeRefMapper = typeRefMapper;
        InputContract = Contract<TInput>();
        ObservationContract = ValueContract.FromShape(entityShape);
        OutcomeContract = Contract<TOutcome>();
        var parameterNames = InputContract.Type is ObjectTypeRef objectType
            ? objectType.Fields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        translator = new(
            entityShape,
            parameterNames,
            allowCapturedValues: false,
            typeRefMapper: typeRefMapper);
    }

    public object Owner => this;

    public ValueContract InputContract { get; }

    public ValueContract ObservationContract { get; }

    public ValueContract OutcomeContract { get; }

    public ValueContract Contract<TValue>() => Contract(typeof(TValue));

    public Expr Translate<TValue>(Expression<Func<TEntity, TInput, TValue>> expression) =>
        translator.Translate(expression);

    public Expr Translate(Expression<Func<TEntity, bool>> expression) => translator.Translate(expression);

    public Expr Constant<TValue>(TValue value) => Expr.Const(ObservationValue.FromObject(value));

    public PortableValue Pattern<TValue>(TValue value, ValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var observed = ObservationValue.FromObject(value);
        return observed.Kind == ObservationValueKind.Null
            ? PortableValue.Null(contract)
            : PortableValue.Concrete(contract, observed);
    }

    public ValueContract ResolveContract(Expr expression, ValueContract fallback)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(fallback);
        switch (expression)
        {
            case BindingExpr binding when binding.Binding == TransitionBindingIds.Input:
                return InputContract;
            case FieldExpr { Path.Segments.Length: 1 } field:
                {
                    var name = field.Path.Segments[0].Segment;
                    var definition = entityShape.Fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name.Value, name, StringComparison.Ordinal));
                    return definition is null ? fallback : ValueContract.FromField(definition);
                }
            case ParameterExpr parameter when InputContract.Type is ObjectTypeRef input:
                {
                    var field = input.Fields.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, parameter.Parameter, StringComparison.Ordinal));
                    return field is null
                        ? fallback
                        : new(
                            field.Type,
                            cardinality: field.Cardinality,
                            presence: field.Presence,
                            nullability: field.Nullability);
                }
            default:
                return fallback;
        }
    }

    public FieldPath FieldPath<TValue>(Expression<Func<TEntity, Field<TValue>>> field) =>
        Cohesive.Model.FieldPath.FromField(translator.TranslateFieldTarget(field));

    public FieldPath CollectionFieldPath<TValue>(
        Expression<Func<TEntity, Field<IReadOnlyList<TValue>>>> field) =>
        Cohesive.Model.FieldPath.FromField(translator.TranslateCollectionFieldTarget(field));

    public AuthoredTransitionSource Source(
        string sourceFile,
        int sourceLine,
        string sourceMember,
        string description)
    {
        var root = metadata.Provenance.Source.Reference;
        var member = string.IsNullOrWhiteSpace(sourceMember) ? "unknown" : sourceMember;
        var reference = sourceLine > 0
            ? $"{root}#{member}:L{sourceLine}"
            : $"{root}#{member}";
        var file = string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFileName(sourceFile);
        var detail = file is null || sourceLine <= 0
            ? description
            : $"{description} ({file}:{sourceLine})";
        return new(reference, detail);
    }

    public void Register(object construct, AuthoredTransitionSource source)
    {
        if (!sources.TryAdd(construct, source))
            throw new InvalidOperationException("A canonical Transition construct was registered twice by one authoring session.");
    }

    public ExecutionSourceMap BuildSourceMap(IR.TransitionDefinition definition)
    {
        List<ExecutionSourceProvenance> entries = [];
        for (var index = 0; index < definition.Preconditions.Length; index++)
        {
            Add(entries, definition.Preconditions[index], ["preconditions", Index(index)]);
        }

        AddNode(entries, definition.Body, ["body"]);
        for (var index = 0; index < definition.Invariants.Length; index++)
        {
            Add(entries, definition.Invariants[index], ["invariants", Index(index)]);
        }

        return new([.. entries]);
    }

    ValueContract Contract(Type type)
    {
        if (contracts.TryGetValue(type, out var contract))
            return contract;

        var nullable = Nullable.GetUnderlyingType(type) is not null;
        contract = new(
            typeRefMapper.Map(type, nullability: null),
            nullability: nullable ? FieldNullability.Nullable : FieldNullability.NonNullable);
        contracts.Add(type, contract);
        return contract;
    }

    void AddNode(
        List<ExecutionSourceProvenance> entries,
        TransitionNode node,
        ImmutableArray<string> path)
    {
        Add(entries, node, path);
        switch (node)
        {
            case SequenceTransitionNode sequence:
                for (var index = 0; index < sequence.Steps.Length; index++)
                    AddNode(entries, sequence.Steps[index], [.. path, "steps", Index(index)]);
                break;
            case ChoiceTransitionNode choice:
                for (var index = 0; index < choice.Cases.Length; index++)
                {
                    var choiceCase = choice.Cases[index];
                    var casePath = path.Add("cases").Add(Index(index));
                    Add(entries, choiceCase, casePath);
                    AddNode(entries, choiceCase.Body, casePath.Add("body"));
                }
                if (choice.Fallback is not null)
                {
                    var fallbackPath = path.Add("fallback");
                    Add(entries, choice.Fallback, fallbackPath);
                    AddNode(entries, choice.Fallback.Body, fallbackPath.Add("body"));
                }
                break;
            case MatchTransitionNode match:
                for (var index = 0; index < match.Cases.Length; index++)
                {
                    var matchCase = match.Cases[index];
                    var casePath = path.Add("cases").Add(Index(index));
                    Add(entries, matchCase, casePath);
                    AddNode(entries, matchCase.Body, casePath.Add("body"));
                }
                if (match.Fallback is not null)
                {
                    var fallbackPath = path.Add("fallback");
                    Add(entries, match.Fallback, fallbackPath);
                    AddNode(entries, match.Fallback.Body, fallbackPath.Add("body"));
                }
                break;
        }
    }

    void Add(
        List<ExecutionSourceProvenance> entries,
        object construct,
        ImmutableArray<string> path)
    {
        var source = sources.TryGetValue(construct, out var registered)
            ? registered
            : new(
                metadata.Provenance.Source.Reference,
                "Canonical Transition construct produced by C# authoring");
        entries.Add(new(source.Reference, new(path), source.Description));
    }

    static string Index(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class TransitionAuthoringMemberPath
{
    public static FieldPath From<TSource, TValue>(Expression<Func<TSource, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var boxed = Expression.Lambda<Func<TSource, object?>>(
            Expression.Convert(selector.Body, typeof(object)),
            selector.Parameters);
        return FieldPath.Capture(boxed);
    }
}
