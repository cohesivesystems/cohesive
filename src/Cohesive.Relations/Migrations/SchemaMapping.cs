using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Migrations;

/// <summary>Authors one identity-preserving extent mapping over exact CLR-derived schema snapshots.</summary>
public static class SchemaMapping
{
    /// <summary>Captures scalar field clauses and freezes the mapping after the recipe completes.</summary>
    /// <param name="id">Stable mapping identity, also used for its single pullback relation.</param>
    /// <param name="domain">Schema containing the summary extent.</param>
    /// <param name="codomain">Schema containing the source extent and every path endpoint.</param>
    /// <param name="relationships">Declared relationships in the codomain.</param>
    /// <param name="define">Synchronous authoring recipe over the destination and rooted source path.</param>
    /// <returns>The validated portable mapping. It retains no CLR metadata or callbacks.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">An extent is unregistered or a mapping obligation is invalid.</exception>
    /// <exception cref="ArgumentException">A selector, identity, or relationship handle is invalid.</exception>
    public static SchemaMappingDocument Define<TDomain, TCodomain>(
        RelationId id, ClrShapeGraphBuildResult domain, ClrShapeGraphBuildResult codomain,
        RelationshipCatalog relationships, Action<MappingExtent<TDomain>, MappingPath<TCodomain>> define) where TCodomain : notnull
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(codomain);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(define);
        var target = domain.GetShape<TDomain>();
        var root = codomain.GetShape<TCodomain>();
        var extent = new MappingExtent<TDomain>(domain);
        define(extent, new(extent, codomain, relationships, []));
        return new(SchemaMappingDocument.CurrentVersion, id, target, root, relationships, [.. extent.Fields]);
    }

    internal static FieldName Member<T, TValue>(ClrShapeGraphBuildResult schema, Expression<Func<T, TValue>> selector)
    {
        var path = FieldPath.Capture(selector, schema.ResolveMemberPath);
        if (!path.TryGetDirectFieldName(out var name))
            throw new ArgumentException("Select a direct scalar property.", nameof(selector));
        return new(name);
    }
}

/// <summary>A forward relationship path from the mapping root to a typed endpoint.</summary>
public sealed class MappingPath<T> where T : notnull
{
    internal MappingPath(object owner, ClrShapeGraphBuildResult schema, RelationshipCatalog catalog,
        ImmutableArray<RelationshipId> hops) => (Owner, Schema, Catalog, Hops) = (owner, schema, catalog, hops);

    internal object Owner { get; }
    internal ClrShapeGraphBuildResult Schema { get; }
    internal RelationshipCatalog Catalog { get; }
    internal ImmutableArray<RelationshipId> Hops { get; }

    /// <summary>Appends one pinned relationship to this path.</summary>
    /// <param name="relationship">Typed handle over the exact codomain relationship definition.</param>
    /// <returns>A path rooted in the same extent and focused on the related shape.</returns>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    /// <exception cref="ArgumentException">The definition or its endpoints disagree with the pinned schema.</exception>
    /// <exception cref="InvalidOperationException">An endpoint is unregistered in the schema.</exception>
    public MappingPath<TNext> Via<TNext>(RelationQueryExpressionRelationship<T, TNext> relationship) where TNext : notnull
    {
        ArgumentNullException.ThrowIfNull(relationship);
        if (!Catalog.TryGetRelationship(relationship.Id, out var pinned) || pinned != relationship.Definition
            || relationship.SourceShape != Schema.GetShape<T>().QualifiedId
            || relationship.TargetShape != Schema.GetShape<TNext>().QualifiedId)
            throw new ArgumentException("Relationship does not match the pinned path endpoints and catalog.", nameof(relationship));
        return new(Owner, Schema, Catalog, [.. Hops, relationship.Id]);
    }
}

/// <summary>Collects the non-identity scalar assignments for one mapped extent.</summary>
public sealed class MappingExtent<T>
{
    readonly ClrShapeGraphBuildResult schema;
    internal MappingExtent(ClrShapeGraphBuildResult schema) => this.schema = schema;
    internal List<SchemaMappingField> Fields { get; } = [];

    /// <summary>Assigns a direct target property from the selected endpoint of a rooted path.</summary>
    /// <param name="target">Property of the domain extent.</param>
    /// <param name="path">Rooted path supplied by this mapping.</param>
    /// <param name="source">Property of the path endpoint with the same value type.</param>
    /// <exception cref="ArgumentNullException">A selector or path is null.</exception>
    /// <exception cref="ArgumentException">The path is foreign or a selector is not a direct property.</exception>
    public void Field<TEndpoint, TValue>(Expression<Func<T, TValue>> target, MappingPath<TEndpoint> path,
        Expression<Func<TEndpoint, TValue>> source) where TEndpoint : notnull
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!ReferenceEquals(path.Owner, this))
            throw new ArgumentException("The source path belongs to another mapping.", nameof(path));
        Fields.Add(new(SchemaMapping.Member(schema, target), path.Hops, SchemaMapping.Member(path.Schema, source)));
    }
}
