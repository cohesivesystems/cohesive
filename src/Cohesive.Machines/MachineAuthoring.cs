using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Cohesive.Machines;

/// <summary>Entry point for owner-scoped fluent machine authoring.</summary>
public static class Machine
{
    /// <summary>Starts an authoring session.</summary>
    /// <param name="id">Stable machine identity.</param>
    /// <param name="revision">Exact revision.</param>
    /// <returns>A mutable authoring session frozen by Build.</returns>
    public static MachineAuthor Define(string id, string revision) => new(id, revision);
}

/// <summary>Owner-scoped typed authoring projection over the structural builder.</summary>
public sealed class MachineAuthor
{
    internal readonly MachineBuilder Builder;
    readonly List<MachineMovement> movements = [];
    MachineDefinition? definition;
    internal MachineAuthor(string id, string revision) => Builder = new(id, revision);
    internal void Mutable()
    {
        if (definition is not null) throw new InvalidOperationException("Machine source is frozen.");
    }
    internal void Own(MachineState state)
    {
        if (!ReferenceEquals(state.Owner, this)) throw new ArgumentException("State belongs to another machine.");
    }
    /// <summary>Declares a named state.</summary>
    /// <param name="sourceFile">Caller source file.</param>
    /// <param name="sourceLine">Caller source line.</param>
    /// <param name="id">Stable state identity.</param>
    /// <returns>An owner-scoped state handle.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineState State(string id, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
    { Mutable(); Builder.State(id, sourceFile, sourceLine); return new(this, id); }
    /// <summary>Begins an edge declaration.</summary>
    /// <param name="id">Stable movement identity.</param>
    /// <returns>An owner-scoped fluent movement handle.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineMovement Edge(string id)
    {
        Mutable(); ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var movement = new MachineMovement(this, id); movements.Add(movement); return movement;
    }
    /// <summary>Lowers and freezes source. Repeated calls return the same definition.</summary>
    /// <returns>Immutable canonical machine.</returns>
    /// <exception cref="InvalidOperationException">Source is incomplete or malformed.</exception>
    public MachineDefinition Build()
    {
        if (definition is not null) return definition;
        foreach (var movement in movements) movement.Check();
        // Build a detached source so failed validation never appends edges to the session.
        var source = Builder.Copy();
        foreach (var movement in movements) source.Edge(movement.Id, movement.Source!.Id, movement.Target!.Id, movement.Action, movement.Guard);
        definition = source.Build(); return definition;
    }
}

/// <summary>A state handle tied to one authoring owner.</summary>
public sealed class MachineState
{
    internal MachineAuthor Owner { get; }
    internal MachineState(MachineAuthor owner, string id) { Owner = owner; Id = id; }
    /// <summary>Stable state identity.</summary>
    public string Id { get; }
    /// <summary>Resolves this state against its frozen owning definition.</summary>
    /// <returns>The exact machine and state identity.</returns>
    public (MachineDefinition Machine, string State) Resolve() => (Owner.Build(), Id);
    /// <summary>Marks this state initial.</summary>
    /// <returns>This handle.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineState Initial() { Owner.Mutable(); Owner.Builder.Initial(Id); return this; }
    /// <summary>Marks this state terminal.</summary>
    /// <returns>This handle.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineState Final() { Owner.Mutable(); Owner.Builder.Final(Id); return this; }
}

/// <summary>A movement handle with owner-checked endpoints.</summary>
public sealed class MachineMovement
{
    readonly MachineAuthor owner;
    internal MachineState? Source;
    internal MachineState? Target;
    internal MachineGuard? Guard;
    internal string? Action;
    internal MachineMovement(MachineAuthor owner, string id) { this.owner = owner; Id = id; }
    /// <summary>Stable edge identity.</summary>
    public string Id { get; }
    /// <summary>Sets the source endpoint.</summary>
    /// <param name="state">A state from this machine.</param>
    /// <returns>This movement.</returns>
    /// <exception cref="ArgumentException">State belongs to another machine.</exception>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineMovement From(MachineState state) { owner.Mutable(); owner.Own(state); Source = state; return this; }
    /// <summary>Sets the target endpoint.</summary>
    /// <param name="state">A state from this machine.</param>
    /// <returns>This movement.</returns>
    /// <exception cref="ArgumentException">State belongs to another machine.</exception>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineMovement To(MachineState state) { owner.Mutable(); owner.Own(state); Target = state; return this; }
    /// <summary>Declares a shared action identity.</summary>
    /// <param name="action">Stable protocol action.</param>
    /// <returns>This movement.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineMovement On(string action) { owner.Mutable(); ArgumentException.ThrowIfNullOrWhiteSpace(action); Action = action; return this; }
    /// <summary>Lowers a typed guard over explicit facts and input.</summary>
    /// <typeparam name="TFacts">Facts type.</typeparam>
    /// <typeparam name="TInput">Action payload type.</typeparam>
    /// <param name="predicate">Pure admission predicate.</param>
    /// <returns>This movement.</returns>
    /// <exception cref="InvalidOperationException">Source is frozen.</exception>
    public MachineMovement When<TFacts, TInput>(Expression<Func<TFacts, TInput, bool>> predicate)
        where TFacts : notnull where TInput : notnull
    { owner.Mutable(); Guard = MachineGuard.From(predicate); return this; }
    /// <summary>Resolves this handle to its frozen exact definition and edge.</summary>
    /// <returns>The owning definition and selected edge.</returns>
    public (MachineDefinition Machine, MachineEdge Edge) Resolve()
    { var machine = owner.Build(); return (machine, machine.Edges.Single(e => e.Id == Id)); }
    internal void Check()
    { if (Source is null || Target is null) throw new InvalidOperationException($"Movement '{Id}' requires both endpoints."); }
}

public sealed partial class MachineBuilder
{
    internal MachineBuilder Copy()
    {
        var copy = new MachineBuilder(id, revision);
        copy.states.AddRange(states); copy.finals.AddRange(finals); copy.initials.AddRange(initials); copy.edges.AddRange(edges);
        foreach (var entry in sourceReferences) copy.sourceReferences.Add(entry.Key, entry.Value);
        return copy;
    }
}
