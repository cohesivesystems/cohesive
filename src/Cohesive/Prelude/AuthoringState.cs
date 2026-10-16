namespace Cohesive.Prelude;

/// <summary>Serializes declaration edits and publishes one immutable result or cached preparation failure.</summary>
/// <typeparam name="T">Prepared declaration type.</typeparam>
/// <remarks>Freezing starts before preparation, including when preparation fails. No edits can race preparation.
/// Successful reads use Lazy's published value without taking the authoring lock.</remarks>
public sealed class AuthoringState<T>
{
    readonly object gate = new();
    readonly Lazy<T> value;
    bool frozen;

    /// <summary>Creates a declaration lifetime without invoking preparation.</summary>
    /// <param name="prepare">One-time immutable materialization; must not mutate this authoring state.</param>
    /// <exception cref="ArgumentNullException">Preparation is null.</exception>
    public AuthoringState(Func<T> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        value = new(() => { lock (gate) { frozen = true; return prepare(); } });
    }

    /// <summary>Freezes edits and returns the cached result, or rethrows the cached preparation failure.</summary>
    public T Value => value.Value;

    /// <summary>Executes one construction-time contribution before freezing starts.</summary>
    /// <typeparam name="TResult">Contribution result.</typeparam>
    /// <param name="contribute">Synchronous edit, never retained.</param>
    /// <returns>The contribution's result.</returns>
    /// <exception cref="ArgumentNullException">Contribution is null.</exception>
    /// <exception cref="InvalidOperationException">Preparation has already started.</exception>
    public TResult Edit<TResult>(Func<TResult> contribute)
    {
        ArgumentNullException.ThrowIfNull(contribute);
        lock (gate)
        {
            if (frozen) throw new InvalidOperationException("The declaration is frozen.");
            return contribute();
        }
    }
}
