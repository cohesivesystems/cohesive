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
