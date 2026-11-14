using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Processes.Machines;

/// <summary>Closed semantic identity of authored reaction work. Episode identity cannot omit its selector.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MachineReactionIdentity.Occurrence), "occurrence")]
[JsonDerivedType(typeof(MachineReactionIdentity.Entity), "entity")]
[JsonDerivedType(typeof(MachineReactionIdentity.Episode), "episode")]
public abstract record MachineReactionIdentity
{
    private MachineReactionIdentity() { }

    /// <summary>Each distinct committed source occurrence creates independent work.</summary>
    public sealed record Occurrence : MachineReactionIdentity;

    /// <summary>One immutable occurrence per source entity and exact policy revision.</summary>
    public sealed record Entity : MachineReactionIdentity;

    /// <summary>One immutable occurrence per source entity, typed episode key and exact policy revision.</summary>
    public sealed record Episode : MachineReactionIdentity
    {
        /// <summary>Creates an episode identity declaration for lowering or persistence.</summary>
        /// <param name="key">Required direct source field; its scalar contract is validated against the source shape.</param>
        /// <exception cref="ArgumentException">The path is not one named field.</exception>
        public Episode(FieldPath key)
        {
            if (key.Segments.IsDefaultOrEmpty || key.Segments.Length != 1 || !key.Segments[0].TryGetFieldIdentity(out _))
                throw new ArgumentException("Episode identity requires one direct source field.", nameof(key));
            Key = key;
        }

        /// <summary>Required field selecting the episode from committed source facts.</summary>
        public FieldPath Key { get; }
    }
}

// Preserve the v1 wire representation and fingerprints. This DTO is a persistence projection,
// not an independently authorable identity model; malformed combinations are rejected here.
internal enum MachineReactionWireScope { SourceOccurrence, SourceEntity, SourceEpisode }

internal sealed record MachineReactionPayloadWire(Shape Source, ExecutionNodeId Movement, ExecutionDefinitionDocument Target,
    ExecutionDefinitionDocument Process,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] MachineReactionWireScope IdentityScope = MachineReactionWireScope.SourceOccurrence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExecutionDefinitionDocument? Prerequisite = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FieldPath? EpisodeKey = null)
{
    internal MachineReactionPayload ToModel()
    {
        MachineReactionIdentity identity = (IdentityScope, EpisodeKey) switch
        {
            (MachineReactionWireScope.SourceOccurrence, null) => new MachineReactionIdentity.Occurrence(),
            (MachineReactionWireScope.SourceEntity, null) => new MachineReactionIdentity.Entity(),
            (MachineReactionWireScope.SourceEpisode, { } key) => new MachineReactionIdentity.Episode(key),
            _ => throw new JsonException("Reaction identity scope and episode selector are inconsistent.")
        };
        return new(Source, Movement, Target, Process, identity, Prerequisite);
    }

    internal static MachineReactionPayloadWire FromModel(MachineReactionPayload value)
    {
        var scope = value.Identity switch
        {
            MachineReactionIdentity.Occurrence => MachineReactionWireScope.SourceOccurrence,
            MachineReactionIdentity.Entity => MachineReactionWireScope.SourceEntity,
            MachineReactionIdentity.Episode => MachineReactionWireScope.SourceEpisode,
            _ => throw new JsonException("Reaction identity is required.")
        };
        return new(value.Source, value.Movement, value.Target, value.Process,
            scope, value.Prerequisite, (value.Identity as MachineReactionIdentity.Episode)?.Key);
    }
}
