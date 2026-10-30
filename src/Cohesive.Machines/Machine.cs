using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Cohesive.Machines;

/// <summary>A structured machine validation finding.</summary>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="Subject">State, action, or definition identity.</param>
/// <param name="Message">Actionable explanation.</param>
/// <param name="Source">Authoring location when known.</param>
public sealed record MachineDiagnostic(string Code, string Subject, string Message, string? Source = null);

/// <summary>A canonical legal movement. Actions are Unit-input protocol identities in this initial fragment.</summary>
/// <param name="Id">Stable edge identity.</param>
/// <param name="Action">Accepted action identity.</param>
/// <param name="From">Source state identity.</param>
/// <param name="To">Target state identity.</param>
/// <param name="Guard">Optional portable admission predicate and input contracts.</param>
public sealed record MachineEdge(string Id, string Action, string From, string To, MachineGuard? Guard = null);

/// <summary>Immutable flat deterministic machine IR, derived from fluent source.</summary>
public sealed partial class MachineDefinition
{
    internal MachineDefinition(string id, string revision, ImmutableArray<string> states, string initial,
        ImmutableArray<string> finals, ImmutableArray<MachineEdge> edges, ExecutionSourceMap? sourceMap = null)
    {
        Id = id; Revision = revision; States = states; Initial = initial; Finals = finals; Edges = edges;
        Document = ExecutionDefinitionDocument.Create(new("machine/v1"), new(id), new(revision),
            new MachinePayload(states, initial, finals, edges),
            new(new("cohesive.machines.csharp", "1"), new(id), DocumentOrigin.User), sourceMap: sourceMap);

    }
    /// <summary>Shared persisted authority with exact identity and producer attribution.</summary>
    public ExecutionDefinitionDocument Document { get; private set; }
    /// <summary>Stable machine identity.</summary>
    public string Id { get; }
    /// <summary>Exact authored revision.</summary>
    public string Revision { get; }
    /// <summary>Canonical state identities.</summary>
    public ImmutableArray<string> States { get; }
    /// <summary>Initial configuration.</summary>
    public string Initial { get; }
    /// <summary>Explicit terminal configurations.</summary>
    public ImmutableArray<string> Finals { get; }
    /// <summary>Canonical legal movements.</summary>
    public ImmutableArray<MachineEdge> Edges { get; }
    /// <summary>Versioned deterministic persisted representation.</summary>
    public string CanonicalJson => ExecutionDefinitionJsonSerializer.Serialize(Document);
    /// <summary>SHA-256 fingerprint of the canonical UTF-8 representation.</summary>
    public string Fingerprint => Document.Metadata.Fingerprint.Value;

    /// <summary>Returns offered actions without changing state.</summary>
    /// <param name="state">Current configuration.</param>
    /// <returns>Distinct actions in ordinal order.</returns>
    /// <exception cref="ArgumentException">The configuration is unknown.</exception>
    public ImmutableArray<string> Enabled(string state)
    {
        RequireState(state);
        if (Edges.Any(e => e.From == state && e.Guard is not null)) throw new InvalidOperationException("Guarded actions require explicit facts and input; use Evaluate.");
        return [.. Edges.Where(e => e.From == state).Select(e => e.Action).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>Selects one movement without committing or performing effects.</summary>
    /// <param name="state">Current configuration.</param>
    /// <param name="action">Requested protocol action.</param>
    /// <returns>The selected edge, or null when the known action is illegal in this configuration.</returns>
    /// <exception cref="ArgumentException">The state or action is unknown.</exception>
    public MachineEdge? Decide(string state, string action)
    {
        RequireState(state);
        if (!Edges.Any(e => e.Action == action)) throw new ArgumentException("Unknown machine action.", nameof(action));
        if (Edges.Any(e => e.Action == action && e.Guard is not null))
            throw new InvalidOperationException("Guarded actions require explicit facts and input; use Evaluate.");
        return Edges.SingleOrDefault(e => e.From == state && e.Action == action);
    }

    void RequireState(string state)
    {
        if (!States.Contains(state)) throw new ArgumentException("Unknown machine configuration.", nameof(state));
    }
}

/// <summary>Mutable authoring source for the flat, unguarded deterministic fragment. Build returns immutable IR.</summary>
public sealed partial class MachineBuilder
{
    readonly string id;
    readonly string revision;
    readonly List<string> states = [];
    readonly List<string> finals = [];
    readonly List<MachineEdge> edges = [];
    readonly List<string> initials = [];
    readonly Dictionary<string, string> sourceReferences = new(StringComparer.Ordinal);

    /// <summary>Creates a machine authoring session.</summary>
    /// <param name="id">Stable definition identity.</param>
    /// <param name="revision">Exact source revision.</param>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    public MachineBuilder(string id, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        this.id = id; this.revision = revision;
    }
    /// <summary>Declares a state.</summary>
    /// <param name="sourceFile">Caller source location.</param>
    /// <param name="sourceLine">Caller source line.</param>
    /// <param name="id">Stable state identity.</param>
    /// <returns>This authoring session.</returns>
    /// <exception cref="ArgumentException">Identity is blank.</exception>
    public MachineBuilder State(string id, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
    { ArgumentException.ThrowIfNullOrWhiteSpace(id); states.Add(id); sourceReferences["state/" + id] = $"{sourceFile}:{sourceLine}"; return this; }
    /// <summary>Selects the initial state.</summary>
    /// <param name="state">Declared state identity.</param>
    /// <returns>This authoring session.</returns>
    public MachineBuilder Initial(string state) { ArgumentException.ThrowIfNullOrWhiteSpace(state); initials.Add(state); return this; }
    /// <summary>Marks a declared state terminal.</summary>
    /// <param name="state">Declared state identity.</param>
    /// <returns>This authoring session.</returns>
    public MachineBuilder Final(string state) { ArgumentException.ThrowIfNullOrWhiteSpace(state); finals.Add(state); return this; }
    /// <summary>Declares a movement, defaulting its action identity to its edge identity.</summary>
    /// <param name="id">Stable edge identity.</param>
    /// <param name="from">Source state.</param>
    /// <param name="to">Target state.</param>
    /// <param name="action">Shared trigger identity, or null to use the edge identity.</param>
    /// <param name="guard">Portable predicate, or null for unconditional admission.</param>
    /// <param name="sourceFile">Caller source location.</param>
    /// <param name="sourceLine">Caller source line.</param>
    /// <returns>This authoring session.</returns>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    public MachineBuilder Edge(string id, string from, string to, string? action = null, MachineGuard? guard = null, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to); ArgumentException.ThrowIfNullOrWhiteSpace(action ?? id);
        edges.Add(new(id, action ?? id, from, to, guard)); sourceReferences["edge/" + id] = $"{sourceFile}:{sourceLine}"; return this;
    }
    /// <summary>Inspects incomplete source without producing executable IR.</summary>
    /// <returns>Deterministically ordered diagnostics; cycles are legal.</returns>
    public ImmutableArray<MachineDiagnostic> Validate()
    {
        List<MachineDiagnostic> result = [];
        void Add(string code, string subject, string message) => result.Add(new("machines." + code, subject, message,
            sourceReferences.GetValueOrDefault("edge/" + subject) ?? sourceReferences.GetValueOrDefault("state/" + subject)));
        foreach (var group in states.GroupBy(x => x).Where(g => g.Count() > 1)) Add("state.duplicate", group.Key, "Declare each state once.");
        foreach (var group in edges.GroupBy(x => x.Id).Where(g => g.Count() > 1)) Add("edge.duplicate", group.Key, "Declare each edge once.");
        if (initials.Count != 1) Add("initial.count", id, "Declare exactly one initial state.");
        foreach (var state in initials.Concat(finals).Concat(edges.SelectMany(e => new[] { e.From, e.To })).Distinct())
            if (!states.Contains(state)) Add("state.unknown", state, "Declare the referenced state.");
        foreach (var edge in edges.Where(e => finals.Contains(e.From))) Add("terminal.outgoing", edge.Id, "Terminal states cannot have outgoing movements.");
        foreach (var group in edges.GroupBy(e => (e.From, e.Action)).Where(g => g.Count(e => e.Guard is null) > 1)) Add("selection.ambiguous", group.Key.From, "An action selects multiple edges.");
        foreach (var edge in edges.Where(e => e.Guard is not null))
        {
            try
            {
                foreach (var diagnostic in edge.Guard!.Analyze().Validation.Diagnostics)
                    Add("guard.invalid", edge.Id, diagnostic.Message);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { Add("guard.invalid", edge.Id, error.Message); }
        }
        foreach (var action in edges.GroupBy(e => e.Action))
        {
            var contracts = action.Select(e => e.Guard?.InputContract).Distinct().ToArray();
            if (contracts.Length > 1) Add("action.contractMismatch", action.Key, "Every edge accepting an action must declare the same input contract.");
        }
        HashSet<string> reached = [.. initials];
        bool changed;
        do { changed = false; foreach (var edge in edges) if (reached.Contains(edge.From)) changed |= reached.Add(edge.To); } while (changed);
        foreach (var state in states.Distinct())
        {
            if (!reached.Contains(state)) Add("state.unreachable", state, "State is unreachable from initialization.");
            if (!finals.Contains(state) && !edges.Any(e => e.From == state)) Add("state.deadEnd", state, "Declare terminality or an outgoing movement.");
        }
        return [.. result.OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Subject, StringComparer.Ordinal)];
    }
    /// <summary>Lowers valid source into immutable canonical IR.</summary>
    /// <returns>A detached immutable definition; subsequent source edits cannot alter it.</returns>
    /// <exception cref="InvalidOperationException">Source has validation diagnostics.</exception>
    public MachineDefinition Build()
    {
        var diagnostics = Validate();
        if (!diagnostics.IsEmpty) throw new InvalidOperationException(string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Subject}: {d.Message}")));
        var orderedStates = states.Order(StringComparer.Ordinal).ToImmutableArray();
        var orderedEdges = edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
        List<ExecutionSourceProvenance> sources = [];
        for (var index = 0; index < orderedStates.Length; index++)
            sources.Add(new(sourceReferences["state/" + orderedStates[index]], new ExecutionSemanticPath(["states", index.ToString(System.Globalization.CultureInfo.InvariantCulture)])));
        for (var index = 0; index < orderedEdges.Length; index++)
            sources.Add(new(sourceReferences["edge/" + orderedEdges[index].Id], new ExecutionSemanticPath(["edges", index.ToString(System.Globalization.CultureInfo.InvariantCulture)])));
        return new(id, revision, orderedStates, initials.Single(),
            [.. finals.Distinct().Order(StringComparer.Ordinal)], orderedEdges, new([.. sources]));
    }
}
