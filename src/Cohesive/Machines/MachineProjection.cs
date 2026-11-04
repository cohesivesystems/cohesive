using System.Collections.Immutable;

namespace Cohesive.Machines;

/// <summary>Authors a finite graph projection. This establishes structural correspondence, not liveness or runtime coordination.</summary>
public sealed class MachineProjectionBuilder
{
    readonly MachineDefinition source;
    readonly MachineDefinition target;
    readonly Dictionary<string, string> states = new(StringComparer.Ordinal);
    readonly Dictionary<string, ImmutableArray<string>> paths = new(StringComparer.Ordinal);
    /// <summary>Creates a projection between immutable exact definitions.</summary>
    /// <param name="source">Detailed machine.</param>
    /// <param name="target">Abstract machine.</param>
    /// <exception cref="ArgumentNullException">An endpoint is null.</exception>
    public MachineProjectionBuilder(MachineDefinition source, MachineDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        this.source = source; this.target = target;
    }
    /// <summary>Maps one source state.</summary>
    /// <param name="from">Source state identity.</param>
    /// <param name="to">Target state identity.</param>
    /// <returns>This authoring session.</returns>
    /// <exception cref="ArgumentException">A state is unknown or already mapped.</exception>
    public MachineProjectionBuilder State(string from, string to)
    {
        if (!source.States.Contains(from) || !target.States.Contains(to)) throw new ArgumentException("Unknown projection state.");
        states.Add(from, to); return this;
    }
    /// <summary>Maps an edge to a composable target path. An empty path explicitly declares stuttering.</summary>
    /// <param name="edge">Source edge identity.</param>
    /// <param name="path">Ordered target edge identities.</param>
    /// <returns>This authoring session.</returns>
    /// <exception cref="ArgumentException">An edge is unknown or already mapped.</exception>
    public MachineProjectionBuilder Edge(string edge, params string[] path)
    {
        if (!source.Edges.Any(e => e.Id == edge) || path.Any(id => !target.Edges.Any(e => e.Id == id)))
            throw new ArgumentException("Unknown projection edge.");
        paths.Add(edge, [.. path]); return this;
    }
    /// <summary>Validates and persists a structural projection against exact participant references.</summary>
    /// <param name="id">Stable relationship identity.</param>
    /// <param name="revision">Exact relationship revision.</param>
    /// <returns>Fingerprint-bound relationship document.</returns>
    /// <exception cref="InvalidOperationException">The mapping is malformed.</exception>
    public Cohesive.Execution.ExecutionDefinitionDocument Build(string id, string revision)
    {
        var diagnostics = Validate();
        if (!diagnostics.IsEmpty) throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.Message)));
        return Cohesive.Execution.ExecutionDefinitionDocument.Create(new("machine.projection/v1"), new(id), new(revision),
            new MachineProjectionPayload(source.Document.Reference, target.Document.Reference,
                states.ToImmutableSortedDictionary(StringComparer.Ordinal),
                [.. paths.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new MachinePathMapping(p.Key, p.Value))]),
            new(new("cohesive.machines.csharp", "1"), new(id)));
    }

    /// <summary>Checks totality, path endpoints, initialization, and terminal preservation.</summary>
    /// <returns>Structural diagnostics. An empty result makes no temporal or action-payload preservation claim.</returns>
    public ImmutableArray<MachineDiagnostic> Validate()
    {
        List<MachineDiagnostic> diagnostics = [];
        void Add(string code, string subject, string message) => diagnostics.Add(new("machines.projection." + code, subject, message));
        foreach (var state in source.States)
            if (!states.ContainsKey(state)) Add("state.missing", state, "Map every source state.");
        if (states.TryGetValue(source.Initial, out var initial) && initial != target.Initial)
            Add("initial.mismatch", source.Initial, "Initial state must map to target initialization.");
        foreach (var final in source.Finals)
            if (states.TryGetValue(final, out var mapped) && !target.Finals.Contains(mapped))
                Add("terminal.mismatch", final, "A terminal state must map to a terminal state.");
        foreach (var edge in source.Edges)
        {
            if (!paths.TryGetValue(edge.Id, out var path)) { Add("edge.missing", edge.Id, "Map every source edge, including stuttering edges."); continue; }
            if (!states.TryGetValue(edge.From, out var current) || !states.TryGetValue(edge.To, out var destination)) continue;
            foreach (var id in path)
            {
                var step = target.Edges.Single(e => e.Id == id);
                if (step.From != current) Add("path.disconnected", edge.Id, "Target path endpoints do not compose.");
                current = step.To;
            }
            if (current != destination) Add("path.target", edge.Id, "Target path does not establish the mapped destination.");
        }
        return [.. diagnostics];
    }
}

/// <summary>One explicit generating-edge image.</summary>
/// <param name="Edge">Source edge identity.</param>
/// <param name="Path">Ordered target edges; empty denotes identity.</param>
public sealed record MachinePathMapping(string Edge, ImmutableArray<string> Path);

/// <summary>Persisted exact finite projection semantics.</summary>
/// <param name="Source">Exact detailed machine.</param>
/// <param name="Target">Exact abstract machine.</param>
/// <param name="States">Total state map.</param>
/// <param name="Paths">Total generating-edge map.</param>
public sealed record MachineProjectionPayload(Cohesive.Execution.ExecutionDefinitionReference Source,
    Cohesive.Execution.ExecutionDefinitionReference Target, ImmutableSortedDictionary<string, string> States,
    ImmutableArray<MachinePathMapping> Paths);
