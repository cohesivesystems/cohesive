using System.Collections.Immutable;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Fluent builder for semantic field definitions.
/// </summary>
public sealed class FieldBuilder
{
    readonly FieldName name;
    readonly Type? runtimeType;
    readonly List<InvariantDefinition> constraints = [];
    TypeRef type;
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;
    FieldCardinality cardinality = FieldCardinality.Single;
    FieldPresence presence = FieldPresence.Required;
    FieldMutability mutability = FieldMutability.Mutable;
    ComputeDefinition? compute;

    /// <summary>
    /// Creates a field builder.
    /// </summary>
    public FieldBuilder(FieldName name, TypeRef type, Type? runtimeType = null)
    {
        this.name = name;
        ArgumentNullException.ThrowIfNull(argument: type);
        this.type = type;
        this.runtimeType = runtimeType;
    }

    /// <summary>
    /// Overrides the inferred field type.
    /// </summary>
    public FieldBuilder Type(TypeRef type)
    {
        this.type = Guard.RequireNotNull(type);
        return this;
    }

    /// <summary>
    /// Marks the inferred CLR field type as an opaque runtime payload.
    /// </summary>
    public FieldBuilder Opaque()
    {
        if (runtimeType is null)
            throw new InvalidOperationException("Opaque field inference requires a CLR runtime type. Use Opaque<T>() or Opaque(Type) when constructing a field builder directly.");

        return Opaque(runtimeType);
    }

    /// <summary>
    /// Marks the supplied CLR type as an opaque runtime payload.
    /// </summary>
    public FieldBuilder Opaque<T>() => Opaque(typeof(T));

    /// <summary>
    /// Marks the supplied CLR type as an opaque runtime payload.
    /// </summary>
    public FieldBuilder Opaque(Type runtimeType)
    {
        ArgumentNullException.ThrowIfNull(runtimeType);
        type = new OpaqueRuntimeTypeRef(runtimeType.FullName ?? runtimeType.Name);
        return this;
    }

    /// <summary>
    /// Marks the field optional.
    /// </summary>
    public FieldBuilder Optional()
    {
        presence = FieldPresence.Optional;
        return this;
    }

    /// <summary>
    /// Marks the field required.
    /// </summary>
    public FieldBuilder Required()
    {
        presence = FieldPresence.Required;
        return this;
    }

    /// <summary>
    /// Sets single cardinality.
    /// </summary>
    public FieldBuilder Single()
    {
        cardinality = FieldCardinality.Single;
        return this;
    }

    /// <summary>
    /// Sets many cardinality.
    /// </summary>
    public FieldBuilder Many()
    {
        cardinality = FieldCardinality.Many;
        return this;
    }

    /// <summary>
    /// Marks field mutable.
    /// </summary>
    public FieldBuilder Mutable()
    {
        mutability = FieldMutability.Mutable;
        compute = null;
        return this;
    }

    /// <summary>
    /// Marks field write-once.
    /// </summary>
    public FieldBuilder WriteOnce()
    {
        mutability = FieldMutability.WriteOnce;
        compute = null;
        return this;
    }

    /// <summary>
    /// Marks field computed from expression.
    /// </summary>
    public FieldBuilder Computed(Expr expression)
    {
        mutability = FieldMutability.Computed;
        compute = new(Expression: expression);
        return this;
    }

    /// <summary>
    /// Adds a declarative constraint.
    /// </summary>
    public FieldBuilder Constraint(string name, Expr expression, string? message = null)
    {
        constraints.Add(item: new(
            name: name,
            expression: expression,
            message: message
        ));
        return this;
    }

    /// <summary>
    /// Adds a field-level annotation entry.
    /// </summary>
    public FieldBuilder Annotation(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new(key)] = value;
        return this;
    }

    /// <summary>
    /// Adds a field-level annotation entry from a CLR value or object graph.
    /// </summary>
    public FieldBuilder Annotation<TValue>(string key, TValue value) =>
        Annotation(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Materializes the immutable field definition.
    /// </summary>
    public FieldDefinition Build()
    {
        return FieldDefinition.Create(
            name: name,
            type: type,
            cardinality: cardinality,
            presence: presence,
            mutability: mutability,
            constraints: [..constraints],
            compute: compute,
            annotations: annotations?.ToImmutable());
    }
}
