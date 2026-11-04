using System.Collections.Immutable;
using System.Text;
using Cohesive.Execution;

namespace Cohesive.Machines;

/// <summary>Canonical machine payload in the shared execution envelope. Construct only in lowering or persistence.</summary>
/// <param name="States">Ordinal state catalog.</param>
/// <param name="Initial">Initial state identity.</param>
/// <param name="Finals">Ordinal terminal-state catalog.</param>
/// <param name="Edges">Ordinal edge catalog.</param>
public sealed record MachinePayload(ImmutableArray<string> States, string Initial,
    ImmutableArray<string> Finals, ImmutableArray<MachineEdge> Edges);

public sealed partial class MachineDefinition
{
    /// <summary>Loads a fingerprint-verified shared document and checks canonical machine semantics.</summary>
    /// <param name="json">Serialized execution document.</param>
    /// <returns>A validated executable definition.</returns>
    /// <exception cref="ArgumentException">The document has another semantic kind.</exception>
    /// <exception cref="InvalidOperationException">The machine is invalid or its collections are noncanonical.</exception>
    public static MachineDefinition Deserialize(string json)
    {
        var document = ExecutionDefinitionJsonSerializer.Deserialize(json);
        if (document.Kind.Value != "machine/v1") throw new ArgumentException("Expected machine/v1.", nameof(json));
        if (!document.Extensions.IsEmpty) throw new InvalidOperationException("Machine extensions require an explicitly supporting interpreter.");
        var payload = document.GetDefinition<MachinePayload>();
        var builder = new MachineBuilder(document.Metadata.DefinitionId.Value, document.Metadata.RevisionId.Value);
        foreach (var state in payload.States) builder.State(state);
        builder.Initial(payload.Initial);
        foreach (var state in payload.Finals) builder.Final(state);
        foreach (var edge in payload.Edges) builder.Edge(edge.Id, edge.From, edge.To, edge.Action, edge.Guard);
        var result = builder.Build();
        if (result.Document.Metadata.Fingerprint != document.Metadata.Fingerprint)
            throw new InvalidOperationException("Machine payload is not canonical.");
        result.Document = document;
        return result;
    }

    /// <summary>Projects a diagram with generated safe node identifiers.</summary>
    /// <returns>Mermaid state diagram source.</returns>
    public string ToMermaid()
    {
        var output = new StringBuilder("stateDiagram-v2\n");
        var names = States.Select((state, index) => (state, name: "s" + index)).ToDictionary(x => x.state, x => x.name);
        static string Escape(string text) => text.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace("\n", " ").Replace("\r", " ");
        foreach (var state in States) output.Append("    state \"").Append(Escape(state)).Append("\" as ").AppendLine(names[state]);
        output.Append("    [*] --> ").AppendLine(names[Initial]);
        foreach (var edge in Edges) output.Append("    ").Append(names[edge.From]).Append(" --> ").Append(names[edge.To]).Append(" : ").AppendLine(Escape(edge.Action));
        foreach (var state in Finals) output.Append("    ").Append(names[state]).AppendLine(" --> [*]");
        return output.ToString();
    }
}

public sealed partial class MachineDefinition
{
    /// <summary>Reports deterministic selection evidence without treating unknown guard overlap as proven.</summary>
    /// <returns>Finite structural proof, or Unknown when symbolic guard disjointness is required.</returns>
    public MachineClaim AnalyzeDeterminism()
    {
        var competing = Edges.GroupBy(e => (e.From, e.Action)).FirstOrDefault(g => g.Count() > 1);
        return competing is null
            ? new(MachineProofState.Proven, "At most one edge exists per state and action.", [])
            : new(MachineProofState.Unknown, "Guard disjointness requires additional proof; runtime selection rejects overlapping enabled edges.", [.. competing.Select(e => e.Id)]);
    }

    /// <summary>Projects admission for every state/action pair under explicit facts and payload.</summary>
    /// <param name="facts">Explicit snapshot facts.</param>
    /// <param name="input">Payload to evaluate; incompatible action contracts produce Indeterminate.</param>
    /// <returns>A deterministic admission matrix derived from the same evaluator.</returns>
    public ImmutableArray<MachineAdmission> AdmissionMatrix(Cohesive.Model.ObservationValue facts, Cohesive.Model.ObservationValue input) =>
        [.. States.SelectMany(state => Edges.Select(e => e.Action).Distinct().Order(StringComparer.Ordinal)
            .Select(action => new MachineAdmission(state, action, Evaluate(state, action, facts, input))))];
}

/// <summary>One derived state/action admission cell.</summary>
/// <param name="State">Configuration identity.</param>
/// <param name="Action">Protocol action identity.</param>
/// <param name="Decision">Pure admission outcome.</param>
public sealed record MachineAdmission(string State, string Action, MachineDecision Decision);
