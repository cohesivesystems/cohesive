using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Continuation transition metadata attached to an emitted effect request.
/// </summary>
public sealed class EffectContinuation
{
    readonly Func<object?, TransitionResult>? run;
    readonly Func<IReadOnlyList<string>, string>? projectSnapshotToken;

    /// <summary>
    /// Creates continuation metadata referenced by transition name.
    /// </summary>
    public EffectContinuation(string transitionName)
    {
        TransitionName = Guard.RequireNotNullOrWhiteSpace(value: transitionName);
    }

    internal EffectContinuation(
        string transitionName,
        Type inputType,
        Func<object?, TransitionResult> run,
        Func<IReadOnlyList<string>, string>? projectSnapshotToken = null
    ) : this(transitionName)
    {
        InputType = Guard.RequireNotNull(inputType);
        this.run = Guard.RequireNotNull(run);
        this.projectSnapshotToken = projectSnapshotToken;
    }

    /// <summary>
    /// Continuation transition name.
    /// </summary>
    public string TransitionName { get; }

    /// <summary>
    /// Continuation transition input type when bound to a direct transition reference.
    /// </summary>
    public Type? InputType { get; }

    /// <summary>
    /// True when this continuation is bound to a direct transition reference.
    /// </summary>
    public bool HasDirectReference => run is not null;

    /// <summary>
    /// Executes the bound continuation transition.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public Task<TransitionResult> RunAsync(object? input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (run is null)
        {
            throw new SemanticRuleViolationException(
                $"Continuation transition '{TransitionName}' is not bound to a direct transition reference.");
        }

        if (InputType is not null && input is not null && !InputType.IsInstanceOfType(input))
        {
            throw new SemanticRuleViolationException(
                $"Continuation transition '{TransitionName}' expects input type '{InputType.FullName}' but received '{input.GetType().FullName}'.");
        }

        if (InputType is { IsValueType: true } && Nullable.GetUnderlyingType(InputType) is null && input is null)
        {
            throw new SemanticRuleViolationException(
                $"Continuation transition '{TransitionName}' requires a non-null value for input type '{InputType.FullName}'.");
        }

        return Task.FromResult(run(input));
    }

    /// <summary>
    /// Ensures the current entity snapshot matches the expected snapshot token.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public void EnsureSnapshotMatches(EffectSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        if (projectSnapshotToken is null)
        {
            throw new SemanticRuleViolationException(
                $"Continuation transition '{TransitionName}' cannot validate snapshot token because no snapshot projector is bound.");
        }

        var currentToken = projectSnapshotToken(snapshot.FieldNames);
        if (!string.Equals(currentToken, snapshot.Token, StringComparison.Ordinal))
        {
            throw new SemanticRuleViolationException(
                $"Continuation transition '{TransitionName}' rejected stale effect result due to snapshot token mismatch.");
        }
    }

    internal EffectContinuation Bind(
        Type inputType,
        Func<object?, TransitionResult> run,
        Func<IReadOnlyList<string>, string>? snapshotTokenProjector = null
    ) => new(
        transitionName: TransitionName,
        inputType: inputType,
        run: run,
        projectSnapshotToken: snapshotTokenProjector);
}