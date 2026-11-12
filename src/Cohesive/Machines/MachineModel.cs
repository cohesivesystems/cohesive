namespace Cohesive.Machines;

/// <summary>Typed authoring owner for one machine; subclasses expose state and movement handles.</summary>
/// <remarks>Constructors author through Source and finish with Freeze. Definition is derived immutable IR,
/// not an actor or entity instance. Runtime state and effects belong to an interpreter.</remarks>
public abstract class MachineModel
{
    readonly MachineAuthor source;

    /// <summary>Starts a machine authoring scope.</summary>
    /// <param name="id">Stable machine identity.</param>
    /// <param name="revision">Exact authored revision.</param>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    protected MachineModel(string id, string revision) => source = Machine.Define(id, revision);

    /// <summary>The single authoring scope owned by this model.</summary>
    protected MachineAuthor Source => source;

    /// <summary>Frozen definition derived from this model; the first access freezes authoring.</summary>
    /// <exception cref="InvalidOperationException">Authoring is incomplete or malformed.</exception>
    public MachineDefinition Definition => source.Build();

    /// <summary>Completes authoring and prevents further mutation through exposed handles.</summary>
    /// <exception cref="InvalidOperationException">Authoring is incomplete or malformed.</exception>
    protected void Freeze() => source.Build();
}
