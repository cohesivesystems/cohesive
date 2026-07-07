using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Convenience builder for convention and rename field mappings.
/// </summary>
public sealed class RelationMapFieldsBuilder<TSource, TTarget>
{
    readonly ShapeId from;
    readonly ShapeId to;
    readonly Dictionary<PropertyInfo, PropertyInfo> targetToSource = [];

    internal RelationMapFieldsBuilder(ShapeId from, ShapeId to)
    {
        this.from = from;
        this.to = to;

        var sourceByName = RelationDslCompiler
            .GetReadableProperties(typeof(TSource))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var targetProperty in RelationDslCompiler.GetReadableProperties(typeof(TTarget)))
        {
            if (sourceByName.TryGetValue(targetProperty.Name, out var sourceProperty))
                targetToSource[targetProperty] = sourceProperty;
        }
    }

    /// <summary>
    /// Renames source and target fields for convention mapping.
    /// </summary>
    public RelationMapFieldsBuilder<TSource, TTarget> Rename<TValueSource, TValueTarget>(
        Expression<Func<TSource, TValueSource>> source,
        Expression<Func<TTarget, TValueTarget>> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var sourceProperty = RelationDslCompiler.ResolveProperty(source);
        var targetProperty = RelationDslCompiler.ResolveProperty(target);
        targetToSource[targetProperty] = sourceProperty;
        return this;
    }

    /// <summary>
    /// Builds a relation definition.
    /// </summary>
    public RelationDefinition Build()
    {
        var assignments = targetToSource
            .OrderBy(x => x.Key.MetadataToken)
            .Select(mapping =>
            {
                var targetField = RelationDslCompiler.ResolveFieldIdentity(mapping.Key);
                var sourceField = RelationDslCompiler.ResolveFieldPath(mapping.Value);
                return new FieldAssignment(
                    targetField: targetField,
                    expr: Expr.Field(sourceField),
                    id: $"assign_{targetField}");
            })
            .ToArray();

        if (assignments.Length == 0)
            throw new InvalidOperationException("MapFields() did not resolve any source/target mappings.");

        return RelationDslCompiler.BuildRelation(
            from: from,
            to: to,
            assignments: assignments);
    }

    /// <summary>
    /// Allows assigning this builder directly where a relation definition is expected.
    /// </summary>
    public static implicit operator RelationDefinition(RelationMapFieldsBuilder<TSource, TTarget> builder)
        => builder.Build();
}
