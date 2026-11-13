using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Machines;

/// <summary>One entity-state classification and optional discriminator assignments derived by binding authoring.</summary>
/// <param name="Predicate">Pure observation-only predicate recognizing this state.</param>
/// <param name="Assignments">Values established when a movement enters this state; empty for predicate bindings.</param>
public sealed record EntityMachineState(Expr Predicate, ImmutableArray<TransitionMachineConfigurationAssignment> Assignments);

/// <summary>Portable, single lifecycle authority carried by an entity shape and its derived transitions.</summary>
/// <remarks>Produced by binding authoring. Classification must be unique at runtime; this does not assert a symbolic partition proof.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EntityMachineBinding : IEquatable<EntityMachineBinding>
{
    static readonly AnnotationKey Key = new("cohesive.entity.machine/v1");
    static readonly ConditionalWeakTable<Shape, EntityMachineBinding> Cache = new();
    static readonly JsonSerializerOptions Json = ExecutionDefinitionJsonSerializer.CreateOptions();
    readonly MachineDefinition definition;

    /// <summary>Restores a materialized binding.</summary>
    /// <param name="machine">Exact canonical machine document.</param>
    /// <param name="states">Total state classification and discriminator assignment map.</param>
    /// <exception cref="ArgumentException">The document or state map is invalid.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [JsonConstructor]
    public EntityMachineBinding(ExecutionDefinitionDocument machine, ImmutableSortedDictionary<string, EntityMachineState> states)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(states);
        definition = MachineDefinition.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(machine));
        if (states.Count != definition.States.Length || definition.States.Any(state => !states.ContainsKey(state))
            || states.Values.Any(state => state is null || state.Predicate is null || state.Assignments.IsDefault))
            throw new ArgumentException("Bind every machine state exactly once with a predicate and initialized assignments.", nameof(states));
        Machine = machine;
        States = states.WithComparers(StringComparer.Ordinal);
        // Validate assignment contracts and overlap through the existing linker boundary.
        _ = Link();
    }

    /// <summary>Exact machine document; references are derived from this authority.</summary>
    public ExecutionDefinitionDocument Machine { get; }
    /// <summary>Deterministically ordered state classification.</summary>
    public ImmutableSortedDictionary<string, EntityMachineState> States { get; }

    internal TransitionMachineLinkCatalog Link() => MachineTransitionLinker.Link(definition,
        state => States[state].Predicate, edge => States[edge.To].Assignments);

    internal string Classify(PortableExpressionReferenceEvaluator evaluator, PortableExpressionEvaluationContext context)
    {
        string? selected = null;
        foreach (var (state, configuration) in States)
        {
            if (!PortableExpressionReferenceEvaluator.RequireBoolean(
                evaluator.Evaluate(configuration.Predicate, context), "Entity machine classification")) continue;
            if (selected is not null) throw new InvalidOperationException($"Entity matches both '{selected}' and '{state}'.");
            selected = state;
        }
        return selected ?? throw new InvalidOperationException("Entity does not match any state of its authoritative machine.");
    }

    internal EntityDefinition Attach(EntityDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Read(entity.Shape) is not null) throw new InvalidOperationException("An entity already has an authoritative machine binding.");
        if (entity.ShapeGraph is not null) throw new NotSupportedException("Bind the lifecycle before publishing a graph-backed entity shape.");
        var shape = entity.Shape with { Annotations = entity.Shape.Annotations.Add(Key,
            AnnotationValue.FromObject(JsonSerializer.SerializeToElement(this, Json))) };
        return new(entity.Name, shape, entity.Invariants);
    }

    /// <summary>Reads the lifecycle authority persisted on an entity shape.</summary>
    /// <param name="shape">Exact entity shape.</param>
    /// <returns>The immutable binding, or null for an unbound shape. Successful decoding is cached by shape lifetime.</returns>
    /// <exception cref="ArgumentNullException">Shape is null.</exception>
    /// <exception cref="JsonException">The annotation is malformed.</exception>
    /// <exception cref="InvalidOperationException">The retained machine document fails validation.</exception>
    /// <exception cref="ArgumentException">The persisted binding is invalid.</exception>
    public static EntityMachineBinding? Read(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!shape.Annotations.ContainsKey(Key)) return null;
        return Cache.GetValue(shape, static value => value.Annotations[Key].Value.Deserialize<EntityMachineBinding>(Json)
            ?? throw new JsonException("The entity machine binding is null."));
    }

    /// <summary>Compares the exact machine reference and all classification semantics.</summary>
    /// <param name="other">Binding to compare, or null.</param>
    /// <returns>True when both bindings have identical meaning.</returns>
    public bool Equals(EntityMachineBinding? other) => ReferenceEquals(this, other) || other is not null
        && Machine.Reference == other.Machine.Reference && States.Count == other.States.Count
        && States.All(pair => other.States.TryGetValue(pair.Key, out var state)
            && pair.Value.Predicate == state.Predicate && pair.Value.Assignments.SequenceEqual(state.Assignments));

    /// <summary>Compares an object to this binding.</summary>
    /// <param name="obj">Candidate binding.</param>
    /// <returns>True for an equivalent binding.</returns>
    public override bool Equals(object? obj) => obj is EntityMachineBinding other && Equals(other);

    /// <summary>Hashes the exact machine and classification semantics.</summary>
    /// <returns>A hash consistent with binding equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Machine.Reference);
        foreach (var pair in States)
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value.Predicate);
            foreach (var assignment in pair.Value.Assignments) hash.Add(assignment);
        }
        return hash.ToHashCode();
    }

    /// <summary>Checks that a transition retains exactly the entity's lifecycle authority.</summary>
    /// <param name="entity">Registered entity definition.</param>
    /// <param name="binding">Binding retained by the transition.</param>
    /// <exception cref="ArgumentNullException">Entity is null.</exception>
    /// <exception cref="InvalidOperationException">The binding was omitted, replaced, or introduced on an unbound entity.</exception>
    public static void RequireSame(EntityDefinition entity, EntityMachineBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var expected = Read(entity.Shape);
        if (ReferenceEquals(expected, binding)) return;
        if (expected is null || binding is null ||
            !expected.Equals(binding))
            throw new InvalidOperationException("Transition and entity must retain the same authoritative machine binding.");
    }
}
