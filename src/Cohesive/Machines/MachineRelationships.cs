using Cohesive.Execution;

namespace Cohesive.Machines;

/// <summary>Strict persistence and exact-participant validation for finite machine relationships.</summary>
public static class MachineRelationships
{
    /// <summary>Loads and validates a projection against exact participant definitions.</summary>
    /// <param name="json">Shared serialized relationship document.</param>
    /// <param name="source">Exact detailed definition.</param>
    /// <param name="target">Exact abstract definition.</param>
    /// <returns>The original verified document, retaining provenance.</returns>
    /// <exception cref="InvalidOperationException">Participants, mapping semantics, or canonical wire differ.</exception>
    public static ExecutionDefinitionDocument ReadProjection(string json, MachineDefinition source, MachineDefinition target)
    {
        var document = Read(json, "machine.projection/v1");
        var payload = document.GetDefinition<MachineProjectionPayload>();
        RequireParticipants(payload.Source, payload.Target, source, target);
        var builder = new MachineProjectionBuilder(source, target);
        foreach (var state in payload.States) builder.State(state.Key, state.Value);
        foreach (var path in payload.Paths) builder.Edge(path.Edge, [.. path.Path]);
        RequireCanonical(document, builder.Build(document.Metadata.DefinitionId.Value, document.Metadata.RevisionId.Value));
        return document;
    }
    /// <summary>Loads an immediate-coherence constraint without claiming it is realized.</summary>
    /// <param name="json">Shared serialized relationship document.</param>
    /// <param name="left">Exact left definition.</param>
    /// <param name="right">Exact right definition.</param>
    /// <returns>The original verified document.</returns>
    /// <exception cref="InvalidOperationException">Participants or canonical wire differ.</exception>
    public static ExecutionDefinitionDocument ReadCoherence(string json, MachineDefinition left, MachineDefinition right)
    {
        var document = Read(json, "machine.coherence/v1");
        var payload = document.GetDefinition<MachineCoherencePayload>();
        RequireParticipants(payload.Left, payload.Right, left, right);
        var builder = new MachineCoherenceBuilder(left, right);
        foreach (var pair in payload.Allowed) builder.Allow(pair.Left, pair.Right);
        RequireCanonical(document, builder.Build(document.Metadata.DefinitionId.Value, document.Metadata.RevisionId.Value));
        return document;
    }
    static ExecutionDefinitionDocument Read(string json, string kind)
    {
        var document = ExecutionDefinitionJsonSerializer.Deserialize(json);
        if (document.Kind.Value != kind || !document.Extensions.IsEmpty)
            throw new InvalidOperationException("Unsupported machine relationship kind or extension.");
        return document;
    }
    static void RequireParticipants(ExecutionDefinitionReference first, ExecutionDefinitionReference second,
        MachineDefinition left, MachineDefinition right)
    {
        if (first != left.Document.Reference || second != right.Document.Reference)
            throw new InvalidOperationException("Relationship participants must match exact revisions and fingerprints.");
    }
    static void RequireCanonical(ExecutionDefinitionDocument original, ExecutionDefinitionDocument rebuilt)
    {
        if (original.Metadata.Fingerprint != rebuilt.Metadata.Fingerprint)
            throw new InvalidOperationException("Relationship content is not canonical.");
    }
}
