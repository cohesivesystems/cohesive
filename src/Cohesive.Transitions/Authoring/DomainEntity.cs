using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Model.Serialization;
using System.Linq.Expressions;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>Typed reference to one canonical entity declared in a domain.</summary>
/// <typeparam name="T">CLR authoring type for the canonical state.</typeparam>
/// <remarks>The handle owns no persistence or execution. Query shapes import the entity's exact state graph.</remarks>
public sealed class DomainEntity<T> where T : notnull
{
    readonly Lazy<ShapeGraphDocument> queryDocument;

    internal DomainEntity(EntityDefinition definition)
    {
        Definition = definition;
        queryDocument = new(() => ShapeGraphDocument.FromGraph(definition.StateShape.Graph));
    }

    /// <summary>Canonical entity definition shared by transition and storage bindings.</summary>
    public EntityDefinition Definition { get; }

    /// <summary>Authors a transition with a typed invocation payload.</summary>
    /// <typeparam name="TInput">Invocation payload.</typeparam>
    /// <typeparam name="TOutcome">Decision outcome.</typeparam>
    /// <param name="id">Stable transition identity.</param>
    /// <param name="revision">Exact semantic revision.</param>
    /// <param name="configure">Finite callback lowered to canonical IR; not retained.</param>
    /// <param name="sourceFile">Compiler-supplied attribution file.</param>
    /// <param name="sourceLine">Compiler-supplied attribution line.</param>
    /// <param name="sourceMember">Compiler-supplied attribution member.</param>
    /// <returns>A transition using the entity's exact declared shape.</returns>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="ArgumentException">Metadata or shape is invalid.</exception>
    /// <exception cref="TransitionExpressionTranslationException">Expressions are not portable.</exception>
    public Transition<T, TInput, TOutcome> Transition<TInput, TOutcome>(
        ExecutionDefinitionId id, ExecutionRevisionId revision,
        Action<TransitionBuilder<T, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        return TransitionAuthoring.Create(Definition.Shape, id, revision, configure,
            sourceFile, sourceLine, sourceMember);
    }

    /// <summary>Authors a transition with the shared no-payload Unit contract.</summary>
    /// <typeparam name="TOutcome">Decision outcome.</typeparam>
    /// <param name="id">Stable transition identity.</param>
    /// <param name="revision">Exact semantic revision.</param>
    /// <param name="configure">Finite callback lowered to canonical IR; not retained.</param>
    /// <param name="sourceFile">Compiler-supplied attribution file.</param>
    /// <param name="sourceLine">Compiler-supplied attribution line.</param>
    /// <param name="sourceMember">Compiler-supplied attribution member.</param>
    /// <returns>A transition using the entity's exact declared shape.</returns>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="ArgumentException">Metadata or shape is invalid.</exception>
    /// <exception cref="TransitionExpressionTranslationException">Expressions are not portable.</exception>
    public Transition<T, Unit, TOutcome> Transition<TOutcome>(
        ExecutionDefinitionId id, ExecutionRevisionId revision,
        Action<TransitionBuilder<T, Unit, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        TransitionAuthoring.Create<T, TOutcome>(Definition.Shape,
            id, revision, configure, sourceFile, sourceLine, sourceMember);

    /// <summary>Declares a reference to another entity's observation identity.</summary>
    /// <param name="reference">Direct string-valued member containing the target identity.</param>
    /// <param name="target">Canonical target entity.</param>
    /// <returns>A reusable relationship handle anchored to both exact entity state graphs.</returns>
    /// <remarks>This declares semantic correlation; it does not create a database foreign key or enforce tenant authorization.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The selector is not a direct reference field.</exception>
    public RelationQueryExpressionRelationship<T, TTarget> References<TTarget>(
        Expression<Func<T, string>> reference, DomainEntity<TTarget> target) where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(Relationship.From<T>(Definition.StateShape.QualifiedId).Reference(reference)
            .To(target.Definition.StateShape.QualifiedId), queryDocument.Value, target.queryDocument.Value);
    }

    /// <summary>Imports this entity's authoritative graph into a relation authoring session.</summary>
    /// <param name="author">Caller-owned registration-scoped session.</param>
    /// <returns>A typed query shape backed by the entity graph rather than a separately inferred schema.</returns>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ArgumentException">The session already registers an incompatible shape.</exception>
    public RelationQueryClrShape<T> QueryShape(RelationQueryExpressionAuthoring author)
    {
        ArgumentNullException.ThrowIfNull(author);
        return author.Clr.Shape<T>(queryDocument.Value, Definition.StateShape.QualifiedId);
    }
}
