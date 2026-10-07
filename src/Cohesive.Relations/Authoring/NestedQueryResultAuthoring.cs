using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Starts a portable single-parent nested-result declaration; no CLR assembly callback is needed.</summary>
    /// <typeparam name="TResult">Nested result POCO.</typeparam>
    /// <returns>A registration-local builder; not safe for concurrent mutation.</returns>
    public NestedQueryResultBuilder<TResult> SingleOrDefault<TResult>() where TResult : class => new(this);

    internal RelationQueryClrShape<T> ResultShape<T>() where T : notnull
    {
        var shape = Clr.Shape<T>();
        TrackShape(shape);
        return shape;
    }

    internal Expr ResultValue<T, TValue>(RelationQueryNodeHandle<LogicalQueryNode> node,
        RelationQueryExpressionValueBinding<T> binding, Expression<Func<T, TValue>> value) where T : notnull
    {
        RequireBindingVisible(node, binding, nameof(binding));
        return lowerer.LowerValue(value, [binding], "nested-result/field").RequireValue().Value;
    }
}

/// <summary>Declares scalar parent fields and distinct ordered child collections, deriving raw projection slots.</summary>
/// <typeparam name="TResult">Nested result POCO; deserialized only after shared canonical assembly.</typeparam>
public sealed class NestedQueryResultBuilder<TResult> where TResult : class
{
    readonly RelationQueryExpressionAuthoring author;
    readonly RelationQueryClrShape<TResult> shape;
    readonly List<FieldDefinition> slots = [];
    readonly List<RelationQueryProjectionAssignment> assignments = [];
    readonly List<QueryResultFieldMapping> fields = [];
    readonly List<QueryResultCollectionAssembly> collections = [];
    RelationQueryNodeHandle<LogicalQueryNode>? input;
    FieldPath identity;
    bool built;

    internal NestedQueryResultBuilder(RelationQueryExpressionAuthoring author)
    {
        this.author = author;
        shape = author.ResultShape<TResult>();
    }

    /// <summary>Selects the complete joined branch and its parent identity.</summary>
    /// <typeparam name="TFocus">Focused row type.</typeparam>
    /// <typeparam name="TParent">Parent source type.</typeparam>
    /// <param name="rows">Joined logical branch.</param>
    /// <param name="parent">Visible parent binding.</param>
    /// <param name="key">Required ordinal string parent identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The source is already set or the builder is complete.</exception>
    public NestedQueryResultBuilder<TResult> From<TFocus, TParent>(RelationQueryExpressionBoundNode<TFocus> rows,
        RelationQueryExpressionValueBinding<TParent> parent, Expression<Func<TParent, string>> key)
        where TFocus : notnull where TParent : notnull
    {
        if (built || input is not null) throw new InvalidOperationException("Nested result source is already configured.");
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(key);
        input = rows.StructuralNode;
        identity = AddSlot(parent, key);
        return this;
    }

    /// <summary>Selects a joined branch and the focused identity of a visible parent node.</summary>
    /// <typeparam name="TFocus">Joined branch focus.</typeparam>
    /// <typeparam name="TParent">Parent source type.</typeparam>
    /// <param name="rows">Complete joined branch.</param>
    /// <param name="parent">Visible parent node; its focused binding is used.</param>
    /// <param name="key">Required ordinal string parent identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The parent is foreign or not visible.</exception>
    /// <exception cref="InvalidOperationException">The source is already set or the builder is complete.</exception>
    public NestedQueryResultBuilder<TResult> From<TFocus, TParent>(RelationQueryExpressionBoundNode<TFocus> rows,
        RelationQueryExpressionBoundNode<TParent> parent, Expression<Func<TParent, string>> key)
        where TFocus : notnull where TParent : notnull =>
        From(rows, (parent ?? throw new ArgumentNullException(nameof(parent))).Binding, key);

    /// <summary>Maps the already selected parent identity into its public result property without a second projection slot.</summary>
    /// <param name="target">Direct string property receiving the identity selected by From.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Target is null.</exception>
    /// <exception cref="ArgumentException">Target is not a direct result property.</exception>
    /// <exception cref="InvalidOperationException">The source is unset or this builder is complete.</exception>
    public NestedQueryResultBuilder<TResult> Identity(Expression<Func<TResult, string>> target)
    {
        RequireMutable();
        fields.Add(new(identity, Target(shape, target)));
        return this;
    }

    /// <summary>Maps a parent field from a bound node's focused value.</summary>
    /// <typeparam name="TSource">Visible source type.</typeparam>
    /// <typeparam name="TValue">Scalar value type.</typeparam>
    /// <param name="target">Direct result property.</param>
    /// <param name="source">Visible bound node.</param>
    /// <param name="value">Canonical value expression.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The source is foreign/not visible or a selector is invalid.</exception>
    /// <exception cref="InvalidOperationException">The source is unset or this builder is complete.</exception>
    public NestedQueryResultBuilder<TResult> Field<TSource, TValue>(Expression<Func<TResult, TValue>> target,
        RelationQueryExpressionBoundNode<TSource> source, Expression<Func<TSource, TValue>> value) where TSource : notnull =>
        Field(target, (source ?? throw new ArgumentNullException(nameof(source))).Binding, value);

    /// <summary>Maps one parent field once; native projection and assembly both derive from this declaration.</summary>
    /// <typeparam name="TSource">Visible source type.</typeparam>
    /// <typeparam name="TValue">Scalar field type.</typeparam>
    /// <param name="target">Direct result property.</param>
    /// <param name="source">Visible source binding.</param>
    /// <param name="value">Canonical source expression.</param>
    /// <returns>This builder.</returns>
    public NestedQueryResultBuilder<TResult> Field<TSource, TValue>(Expression<Func<TResult, TValue>> target,
        RelationQueryExpressionValueBinding<TSource> source, Expression<Func<TSource, TValue>> value) where TSource : notnull
    {
        var path = Target(shape, target);
        fields.Add(new(AddSlot(source, value), path));
        return this;
    }

    /// <summary>Declares distinct child objects ordered by ordinal identity; absent identities produce an empty collection.</summary>
    /// <typeparam name="TChild">Child result POCO.</typeparam>
    /// <typeparam name="TSource">Source owning the child identity.</typeparam>
    /// <param name="target">Direct collection result property.</param>
    /// <param name="source">Visible child source binding.</param>
    /// <param name="key">Child identity; missing/null denotes an absent outer-joined child.</param>
    /// <param name="configure">Scalar child mappings.</param>
    /// <returns>This builder.</returns>
    public NestedQueryResultBuilder<TResult> Collection<TChild, TSource>(Expression<Func<TResult, IReadOnlyList<TChild>>> target,
        RelationQueryExpressionValueBinding<TSource> source, Expression<Func<TSource, string>> key,
        Action<NestedQueryChildBuilder<TResult, TChild>> configure) where TChild : class where TSource : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        var path = Target(shape, target);
        var childKey = AddSlot(source, key);
        var child = new NestedQueryChildBuilder<TResult, TChild>(this, author.ResultShape<TChild>(), childKey);
        configure(child);
        collections.Add(new(path, childKey, child.Complete()));
        return this;
    }

    /// <summary>Declares a collection keyed by the focused value of a visible bound node.</summary>
    /// <typeparam name="TChild">Child result type.</typeparam>
    /// <typeparam name="TSource">Child source type.</typeparam>
    /// <param name="target">Direct collection property.</param>
    /// <param name="source">Visible child node.</param>
    /// <param name="key">Child identity; missing/null means absent.</param>
    /// <param name="configure">Child scalar mappings.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A selector is invalid or the source is foreign/not visible.</exception>
    /// <exception cref="InvalidOperationException">The source is unset or this builder is complete.</exception>
    public NestedQueryResultBuilder<TResult> Collection<TChild, TSource>(Expression<Func<TResult, IReadOnlyList<TChild>>> target,
        RelationQueryExpressionBoundNode<TSource> source, Expression<Func<TSource, string>> key,
        Action<NestedQueryChildBuilder<TResult, TChild>> configure) where TChild : class where TSource : notnull =>
        Collection(target, (source ?? throw new ArgumentNullException(nameof(source))).Binding, key, configure);

    /// <summary>Freezes the canonical raw query and its portable assembly metadata into one typed declaration.</summary>
    /// <typeparam name="TInput">Single invocation parameter type.</typeparam>
    /// <param name="id">Stable query identity.</param>
    /// <param name="name">Query name.</param>
    /// <param name="parameter">The branch's single invocation parameter.</param>
    /// <returns>A typed query returning null for no parent.</returns>
    /// <exception cref="ArgumentException">The parameter is foreign/additional, or assembly mappings conflict.</exception>
    /// <exception cref="InvalidOperationException">The source is unset or the builder is complete.</exception>
    public RelationQuery<TInput, TResult?> Build<TInput>(QueryId id, QueryName name, RelationQueryExpressionParameter<TInput> parameter)
    {
        RequireMutable();
        ArgumentNullException.ThrowIfNull(parameter);
        if (!ReferenceEquals(parameter.Owner, author)) throw new ArgumentException("Foreign query parameter.", nameof(parameter));
        var rowShape = new QualifiedShapeId(new GraphId(id.Value + "/result-slots/v1"), new ShapeId("Row"));
        var graph = new ShapeGraph(rowShape.GraphId, [new Shape(rowShape.ShapeId, [.. slots])]);
        var projected = author.Structural.Project(input!.Value, rowShape, [.. assignments]);
        var rows = author.Structural.Rows(projected.Node);
        var assembly = new NestedQueryResultAssembly(rows.Id, shape.Id, identity, [.. fields], [.. collections]);
        assembly.Validate(graph.Shapes[0], shape.Document.Graph);
        var definition = author.Structural.BuildQuery(id, name, [rows]).Definition with { Assembly = assembly };
        if (definition.Body.Parameters.Length != 1 || definition.Body.Parameters[0].Id != parameter.Id)
            throw new ArgumentException("Nested typed query requires exactly the supplied invocation parameter.", nameof(parameter));
        var request = new RelationQueryCompilationRequest(RelationQueryDocument.FromDefinition(definition),
            [.. author.ShapeDocuments, ShapeGraphDocument.FromGraph(graph)], author.CreateRelationshipCatalogDocument());
        built = true;
        return new(request, parameter.Id, values =>
        {
            var result = NestedQueryResultAssembler.Assemble(assembly, values);
            if (result.Kind == ObservationValueKind.Null) return null;
            if (!ObservationValidator.TryValidateAgainstShape(result, shape.Document.Graph.GetShape(shape.Id.ShapeId), out _, shape.Document.Graph))
                throw new InvalidOperationException("Nested result violates its declared output shape.");
            return result.Deserialize<TResult>();
        });
    }

    internal FieldPath AddSlot<TSource, TValue>(RelationQueryExpressionValueBinding<TSource> source,
        Expression<Func<TSource, TValue>> value) where TSource : notnull
    {
        RequireMutable();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(value);
        var expression = author.ResultValue(input!.Value, source, value);
        var name = "slot_" + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = FieldPath.FromField(name);
        slots.Add(new(new(name), author.Clr.GetTypeRef(typeof(TValue)), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable));
        assignments.Add(new(path, expression));
        return path;
    }

    internal static FieldPath Target<T, TValue>(RelationQueryClrShape<T> shape, Expression<Func<T, TValue>> target) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression parameter }
            || parameter != target.Parameters[0]) throw new ArgumentException("A direct result property is required.", nameof(target));
        return shape.ResolveMemberPath([property]);
    }

    void RequireMutable()
    {
        if (built || input is null) throw new InvalidOperationException("Select the nested result source before mapping, and build only once.");
    }
}

/// <summary>Child scalar mappings owned by the enclosing nested query declaration.</summary>
/// <typeparam name="TResult">Parent result type.</typeparam>
/// <typeparam name="TChild">Child result type.</typeparam>
public sealed class NestedQueryChildBuilder<TResult, TChild> where TResult : class where TChild : class
{
    readonly NestedQueryResultBuilder<TResult> owner;
    readonly RelationQueryClrShape<TChild> shape;
    readonly FieldPath identity;
    readonly List<QueryResultFieldMapping> fields = [];
    bool completed;
    internal ImmutableArray<QueryResultFieldMapping> Complete()
    {
        completed = true;
        return fields.ToImmutableArray();
    }
    internal NestedQueryChildBuilder(NestedQueryResultBuilder<TResult> owner, RelationQueryClrShape<TChild> shape, FieldPath identity)
    { this.owner = owner; this.shape = shape; this.identity = identity; }

    /// <summary>Maps the collection's selected identity without repeating its expression or projection slot.</summary>
    /// <param name="target">Direct string child property receiving the collection identity.</param>
    /// <returns>This child builder.</returns>
    /// <exception cref="ArgumentNullException">Target is null.</exception>
    /// <exception cref="ArgumentException">Target is not a direct child property.</exception>
    /// <exception cref="InvalidOperationException">Child configuration is complete.</exception>
    public NestedQueryChildBuilder<TResult, TChild> Identity(Expression<Func<TChild, string>> target)
    {
        if (completed) throw new InvalidOperationException("Child mappings are already complete.");
        fields.Add(new(identity, NestedQueryResultBuilder<TResult>.Target(shape, target)));
        return this;
    }

    /// <summary>Maps a child field from a bound node's focused value.</summary>
    /// <typeparam name="TSource">Visible source type.</typeparam>
    /// <typeparam name="TValue">Scalar field type.</typeparam>
    /// <param name="target">Direct child property.</param>
    /// <param name="source">Visible bound node.</param>
    /// <param name="value">Canonical source expression.</param>
    /// <returns>This child builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A selector is invalid or the source is foreign/not visible.</exception>
    /// <exception cref="InvalidOperationException">Child configuration is complete.</exception>
    public NestedQueryChildBuilder<TResult, TChild> Field<TSource, TValue>(Expression<Func<TChild, TValue>> target,
        RelationQueryExpressionBoundNode<TSource> source, Expression<Func<TSource, TValue>> value) where TSource : notnull =>
        Field(target, (source ?? throw new ArgumentNullException(nameof(source))).Binding, value);

    /// <summary>Maps a child field from a visible source expression.</summary>
    /// <typeparam name="TSource">Visible source type.</typeparam>
    /// <typeparam name="TValue">Scalar field type.</typeparam>
    /// <param name="target">Direct child property.</param>
    /// <param name="source">Visible source binding.</param>
    /// <param name="value">Canonical value expression.</param>
    /// <returns>This child builder.</returns>
    public NestedQueryChildBuilder<TResult, TChild> Field<TSource, TValue>(Expression<Func<TChild, TValue>> target,
        RelationQueryExpressionValueBinding<TSource> source, Expression<Func<TSource, TValue>> value) where TSource : notnull
    {
        if (completed) throw new InvalidOperationException("Child mappings are already complete.");
        var path = NestedQueryResultBuilder<TResult>.Target(shape, target);
        fields.Add(new(owner.AddSlot(source, value), path));
        return this;
    }
}
