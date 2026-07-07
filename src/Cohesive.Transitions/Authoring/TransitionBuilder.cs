using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for declarative entity transitions.
/// </summary>
public sealed class TransitionBuilder
{
    readonly List<TransitionParameterDefinition> parameters = [];
    readonly List<TransitionPreconditionDefinition> preconditions = [];
    readonly List<FieldUpdateDefinition> updates = [];
    readonly List<EffectDefinition> effects = [];
    readonly HashSet<string> explicitReadSet = new(StringComparer.Ordinal);
    readonly HashSet<string> explicitWriteSet = new(StringComparer.Ordinal);
    int? pendingContinuationEffectIndex;
    Type? pendingContinuationResultType;
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;
    string? description;

    /// <summary>
    /// Sets transition description.
    /// </summary>
    /// <param name="description">Transition description text</param>
    public TransitionBuilder Description(string description)
    {
        ClearPendingContinuation();
        this.description = Guard.RequireNotNullOrWhiteSpace(value: description);
        return this;
    }

    /// <summary>
    /// Declares a typed transition input parameter.
    /// </summary>
    public TransitionBuilder Parameter(string name, TypeRef type, bool isRequired = true, string? description = null)
    {
        ClearPendingContinuation();
        parameters.Add(item: new TransitionParameterDefinition(name: name, type: type, isRequired: isRequired, description: description));
        return this;
    }

    /// <summary>
    /// Adds a transition precondition.
    /// </summary>
    /// <param name="name">Precondition name</param>
    /// <param name="expression">Precondition expression</param>
    /// <param name="message">Optional precondition explanatory message</param>
    public TransitionBuilder Requires(string name, Expr expression, string? message = null)
    {
        ClearPendingContinuation();
        preconditions.Add(item: new(name: name, expression: expression, message: message));
        return this;
    }

    /// <summary>
    /// Adds a field assignment to the transition.
    /// </summary>
    /// <param name="field">Field name to assign</param>
    /// <param name="valueExpression">Expression to assign to the value</param>
    public TransitionBuilder Set(string field, Expr valueExpression)
    {
        ClearPendingContinuation();
        var fieldIdentity = Guard.RequireNotNullOrWhiteSpace(field);
        explicitWriteSet.Add(fieldIdentity);
        updates.Add(item: new(field: fieldIdentity, valueExpression: valueExpression));
        return this;
    }

    /// <summary>
    /// Declares an explicit read dependency for this transition.
    /// </summary>
    public TransitionBuilder Read(string field)
    {
        ClearPendingContinuation();
        explicitReadSet.Add(Guard.RequireNotNullOrWhiteSpace(field));
        return this;
    }

    /// <summary>
    /// Declares an explicit field write dependency for this transition.
    /// </summary>
    public TransitionBuilder Write(string field)
    {
        ClearPendingContinuation();
        explicitWriteSet.Add(Guard.RequireNotNullOrWhiteSpace(field));
        return this;
    }

    /// <summary>
    /// Adds a value to the end of a collection field.
    /// </summary>
    /// <param name="field">Collection field name to update</param>
    /// <param name="valueExpression">Expression that resolves to the value to append</param>
    public TransitionBuilder Add(string field, Expr valueExpression)
    {
        ArgumentNullException.ThrowIfNull(valueExpression);
        ClearPendingContinuation();
        var fieldIdentity = Guard.RequireNotNullOrWhiteSpace(field);
        explicitWriteSet.Add(fieldIdentity);
        updates.Add(new FieldUpdateDefinition(
            field: fieldIdentity,
            valueExpression: Expr.Call(
                function: ExprFunctionNames.Append,
                Expr.Field(fieldIdentity),
                valueExpression
                )
            )
        );
        return this;
    }

    /// <summary>
    /// Inserts a value at a requested index in a collection field.
    /// </summary>
    /// <param name="field">Collection field name to update</param>
    /// <param name="indexExpression">Expression that resolves to the insertion index</param>
    /// <param name="valueExpression">Expression that resolves to the value to insert</param>
    public TransitionBuilder Insert(string field, Expr indexExpression, Expr valueExpression)
    {
        ArgumentNullException.ThrowIfNull(indexExpression);
        ArgumentNullException.ThrowIfNull(valueExpression);
        ClearPendingContinuation();
        var fieldIdentity = Guard.RequireNotNullOrWhiteSpace(field);
        explicitWriteSet.Add(fieldIdentity);
        updates.Add(item: new FieldUpdateDefinition(
            field: fieldIdentity,
            valueExpression: Expr.Call(
                function: ExprFunctionNames.InsertAt,
                Expr.Field(fieldIdentity),
                indexExpression,
                valueExpression)));
        return this;
    }

    /// <summary>
    /// Adds values to the end of a collection field.
    /// </summary>
    /// <param name="field">Collection field name to update</param>
    /// <param name="valuesExpression">Expression that resolves to the values to append</param>
    public TransitionBuilder AddRange(string field, Expr valuesExpression)
    {
        ArgumentNullException.ThrowIfNull(valuesExpression);
        ClearPendingContinuation();
        var fieldIdentity = Guard.RequireNotNullOrWhiteSpace(field);
        explicitWriteSet.Add(fieldIdentity);
        updates.Add(item: new FieldUpdateDefinition(
            field: fieldIdentity,
            valueExpression: Expr.Call(
                function: ExprFunctionNames.AppendRange,
                Expr.Field(fieldIdentity),
                valuesExpression)));
        return this;
    }

    /// <summary>
    /// Inserts values at a requested index in a collection field.
    /// </summary>
    /// <param name="field">Collection field name to update</param>
    /// <param name="indexExpression">Expression that resolves to the insertion index</param>
    /// <param name="valuesExpression">Expression that resolves to the values to insert</param>
    public TransitionBuilder InsertRange(string field, Expr indexExpression, Expr valuesExpression)
    {
        ArgumentNullException.ThrowIfNull(indexExpression);
        ArgumentNullException.ThrowIfNull(valuesExpression);
        ClearPendingContinuation();
        var fieldIdentity = Guard.RequireNotNullOrWhiteSpace(field);
        explicitWriteSet.Add(fieldIdentity);
        updates.Add(item: new FieldUpdateDefinition(
            field: fieldIdentity,
            valueExpression: Expr.Call(
                function: ExprFunctionNames.InsertRangeAt,
                Expr.Field(fieldIdentity),
                indexExpression,
                valuesExpression)));
        return this;
    }

    /// <summary>
    /// Adds an emitted effect request.
    /// </summary>
    public TransitionBuilder Emit(string name, Expr? payload = null, string? continuationTransition = null)
    {
        AddEffect(new EffectDefinition(
            name: name,
            payload: payload,
            continuation: continuationTransition is null
                ? null
                : new EffectContinuationDefinition(continuationTransition)),
            continuationResultType: null,
            trackPendingContinuation: false);
        return this;
    }

    /// <summary>
    /// Adds an emitted effect request with a direct continuation transition definition reference.
    /// </summary>
    public TransitionBuilder Emit(string name, Expr? payload, TransitionDefinition continuationTransition)
    {
        AddEffect(new EffectDefinition(
            name: name,
            payload: payload,
            continuation: new EffectContinuationDefinition(continuationTransition)),
            continuationResultType: null,
            trackPendingContinuation: false);
        return this;
    }

    /// <summary>
    /// Adds an emitted effect request.
    /// </summary>
    public TransitionBuilder Request(string name, Expr? payload = null, string? continuationTransition = null)
    {
        AddEffect(new EffectDefinition(
            name: name,
            payload: payload,
            continuation: continuationTransition is null
                ? null
                : new EffectContinuationDefinition(continuationTransition)),
            continuationResultType: null,
            trackPendingContinuation: true);
        return this;
    }

    /// <summary>
    /// Adds an emitted effect request with a direct continuation transition definition reference.
    /// </summary>
    public TransitionBuilder Request(string name, Expr? payload, TransitionDefinition continuationTransition)
    {
        AddEffect(new EffectDefinition(
            name: name,
            payload: payload,
            continuation: new EffectContinuationDefinition(continuationTransition)),
            continuationResultType: null,
            trackPendingContinuation: true);
        return this;
    }

    /// <summary>
    /// Adds an emitted typed effect request.
    /// </summary>
    public TransitionBuilder Request<TRequest, TResult>(
        Expr? payload = null,
        TransitionDefinition? continuationTransition = null
        )
        where TRequest : IEffectRequest<TResult>
    {
        AddEffect(new EffectDefinition(
            name: TRequest.RequestName,
            payload: payload,
            continuation: continuationTransition is null
                ? null
                : new EffectContinuationDefinition(continuationTransition)),
            continuationResultType: typeof(TResult),
            trackPendingContinuation: true);
        return this;
    }

    /// <summary>
    /// Attaches continuation metadata to the most recently requested effect.
    /// </summary>
    public TransitionBuilder Then(string continuationTransition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationTransition);
        return ThenCore(
            continuation: new EffectContinuationDefinition(continuationTransition),
            continuationInputType: null);
    }

    /// <summary>
    /// Attaches continuation metadata to the most recently requested effect.
    /// </summary>
    public TransitionBuilder Then(TransitionDefinition continuationTransition)
    {
        ArgumentNullException.ThrowIfNull(continuationTransition);
        return ThenCore(
            continuation: new EffectContinuationDefinition(continuationTransition),
            continuationInputType: null);
    }

    /// <summary>
    /// Attaches typed continuation metadata to the most recently requested effect.
    /// </summary>
    public TransitionBuilder Then<TContinuationInput>(TransitionDefinition continuationTransition)
    {
        ArgumentNullException.ThrowIfNull(continuationTransition);
        return ThenCore(
            continuation: new EffectContinuationDefinition(continuationTransition),
            continuationInputType: typeof(TContinuationInput));
    }

    /// <summary>
    /// Adds a transition-level annotation entry.
    /// </summary>
    public TransitionBuilder Annotation(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ClearPendingContinuation();
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new AnnotationKey(key)] = value;
        return this;
    }

    /// <summary>
    /// Adds a transition-level annotation entry from a CLR value or object graph.
    /// </summary>
    public TransitionBuilder Annotation<TValue>(string key, TValue value) =>
        Annotation(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Materializes the immutable transition definition.
    /// </summary>
    public TransitionDefinition Build(string name)
    {
        return new TransitionDefinition(
            name: name,
            inputs: [.. parameters],
            preconditions: [.. preconditions],
            updates: [.. updates],
            effects: [.. effects],
            readSet: [.. explicitReadSet],
            writeSet: [.. explicitWriteSet],
            description: description,
            annotations: annotations?.ToImmutable());
    }

    void AddEffect(EffectDefinition effect, Type? continuationResultType, bool trackPendingContinuation)
    {
        effects.Add(effect);
        if (effect.Continuation is not null || !trackPendingContinuation)
        {
            ClearPendingContinuation();
            return;
        }

        pendingContinuationEffectIndex = effects.Count - 1;
        pendingContinuationResultType = continuationResultType;
    }

    TransitionBuilder ThenCore(EffectContinuationDefinition continuation, Type? continuationInputType)
    {
        if (pendingContinuationEffectIndex is null)
        {
            throw new InvalidOperationException(
                "Then(...) can only be used after Request(...) without an explicit continuation.");
        }

        if (pendingContinuationResultType is not null
            && continuationInputType is not null
            && !string.Equals(
                pendingContinuationResultType.AssemblyQualifiedName,
                continuationInputType.AssemblyQualifiedName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Then(...) continuation input type '{continuationInputType.FullName}' does not match pending request result type '{pendingContinuationResultType.FullName}'.");
        }

        var index = pendingContinuationEffectIndex.Value;
        effects[index] = effects[index] with { Continuation = continuation };
        ClearPendingContinuation();
        return this;
    }

    void ClearPendingContinuation()
    {
        pendingContinuationEffectIndex = null;
        pendingContinuationResultType = null;
    }
}
