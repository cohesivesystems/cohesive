using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>Scoped CLR authoring configuration shared by an entity, its guards, bindings and transitions.</summary>
/// <remarks>The context is source configuration, never persisted IR. Keep its mapper stable for its lifetime.
/// Existing standalone authoring entry points remain useful for independently owned portable machines.</remarks>
/// <typeparam name="TEntity">Entity observation type.</typeparam>
/// <typeparam name="TInput">Command type shared by the authored guards and transitions.</typeparam>
public sealed class EntityTransitionAuthoring<TEntity, TInput> where TEntity : notnull where TInput : notnull
{
    readonly IClrTypeRefMapper mapper;

    /// <summary>Creates a scope and lowers its entity definition using one mapper.</summary>
    /// <param name="name">Stable entity identity.</param>
    /// <param name="typeRefMapper">Explicit contract mapping; null selects framework defaults.</param>
    /// <exception cref="InvalidOperationException">The entity has no readable fields or ambiguous field names.</exception>
    public EntityTransitionAuthoring(EntityTypeName name, IClrTypeRefMapper? typeRefMapper = null)
    {
        mapper = typeRefMapper ?? new DefaultClrTypeRefMapper();
        Entity = ObjectEntityDefinition.For<TEntity>(name, mapper);
    }

    /// <summary>The entity definition shared by all transitions authored in this scope.</summary>
    public EntityDefinition Entity { get; }

    /// <summary>Lowers a guard using this scope's facts and command contracts.</summary>
    /// <param name="predicate">Portable typed predicate to pass to a movement's When method.</param>
    /// <returns>Derived guard IR; no delegate is retained.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The expression cannot be lowered.</exception>
    public MachineGuard Guard(Expression<Func<TEntity, TInput, bool>> predicate) =>
        MachineGuardCompiler.Compile(predicate, mapper);

    /// <summary>Starts a discriminator binding with this scope's contracts.</summary>
    /// <typeparam name="TValue">Discriminator value type.</typeparam>
    /// <param name="machine">Frozen machine definition.</param>
    /// <param name="selector">Entity discriminator field.</param>
    /// <returns>A binding ready for state mappings.</returns>
    /// <exception cref="ArgumentNullException">The machine is null.</exception>
    /// <exception cref="ArgumentException">The selector is not a field.</exception>
    public MachineDiscriminatorBinding<TEntity, TValue> Discriminator<TValue>(MachineDefinition machine,
        Expression<Func<TEntity, TValue>> selector) => new(machine, selector, mapper);

    /// <summary>Starts a predicate binding with this scope's contracts.</summary>
    /// <param name="machine">Frozen machine definition.</param>
    /// <returns>A binding ready for configuration predicates.</returns>
    /// <exception cref="ArgumentNullException">The machine is null.</exception>
    public MachinePredicateBinding<TEntity, TInput> Predicates(MachineDefinition machine) => new(machine, mapper);

    /// <summary>Authors a transition against this scope's entity and CLR contracts.</summary>
    /// <typeparam name="TOutcome">Result type.</typeparam>
    /// <param name="metadata">Stable identity, revision and provenance.</param>
    /// <param name="configure">Source builder callback, executed once and not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file.</param>
    /// <param name="sourceLine">Compiler-supplied source line.</param>
    /// <param name="sourceMember">Compiler-supplied source member.</param>
    /// <returns>The lowered canonical transition document and validation result.</returns>
    /// <exception cref="ArgumentNullException">Metadata or configure is null.</exception>
    /// <exception cref="ArgumentException">Metadata is invalid.</exception>
    /// <exception cref="TransitionExpressionTranslationException">An expression is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Builder structure is contradictory.</exception>
    /// <exception cref="NotSupportedException">A constant cannot be represented portably.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be encoded.</exception>
    public Transition<TEntity, TInput, TOutcome> Create<TOutcome>(TransitionAuthoringMetadata metadata,
        Action<TransitionBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        TransitionAuthoring.Create(Entity.Shape, metadata, configure, sourceFile, sourceLine, sourceMember, mapper);
}
