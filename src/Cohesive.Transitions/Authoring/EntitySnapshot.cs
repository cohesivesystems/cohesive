using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Binds a typed entity definition to a specific state snapshot for ergonomic field reads.
/// </summary>
public sealed class EntitySnapshot<TEntity>(TEntity entity, EntityState state) where TEntity : Entity
{
    /// <summary>
    /// Bound entity definition.
    /// </summary>
    public TEntity Entity { get; } = Guard.RequireNotNull(entity);

    /// <summary>
    /// Bound runtime state.
    /// </summary>
    public EntityState State { get; } = Guard.RequireNotNull(state);

    /// <summary>
    /// Entity identity carried by the bound state.
    /// </summary>
    public EntityId EntityId => State.EntityId;

    /// <summary>
    /// Version carried by the bound state.
    /// </summary>
    public long Version => State.Version;

    /// <summary>
    /// Reads a typed field value from the bound state using an entity field selector.
    /// </summary>
    public TValue Get<TValue>(Expression<Func<TEntity, Field<TValue>>> field) =>
        ResolveField(field).Get(State);

    /// <summary>
    /// Attempts to read a typed field value from the bound state using an entity field selector.
    /// </summary>
    public bool TryGet<TValue>(Expression<Func<TEntity, Field<TValue>>> field, out TValue value) =>
        ResolveField(field).TryGet(State, out value);

    /// <summary>
    /// Returns true when the selected field has a materializable value in the bound state.
    /// </summary>
    public bool Has<TValue>(Expression<Func<TEntity, Field<TValue>>> field) =>
        ResolveField(field).HasValue(State);

    /// <summary>
    /// Reads a typed field value from the bound state or returns the supplied fallback.
    /// </summary>
    public TValue GetOrDefault<TValue>(Expression<Func<TEntity, Field<TValue>>> field, TValue defaultValue = default!) => 
        ResolveField(field).GetOrDefault(State, defaultValue);

    /// <summary>
    /// Reads a typed field value from the bound state and throws when no value is present.
    /// </summary>
    public TValue Require<TValue>(Expression<Func<TEntity, Field<TValue>>> field, string? message = null) => 
        ResolveField(field).Require(State, message);

    /// <summary>
    /// Materializes the bound state through the deterministic core plan.
    /// </summary>
    public T Populate<T>() => State.Populate<T>();

    /// <summary>
    /// Materializes the bound state through an explicitly configured core plan.
    /// </summary>
    public T Populate<T>(Action<ObservationMaterializerBuilder<T>> configure) => State.Populate(configure);

    Field<TValue> ResolveField<TValue>(Expression<Func<TEntity, Field<TValue>>> field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var body = StripConvert(field.Body);
        if (body is not MemberExpression member)
            throw new ArgumentException("Field selector must reference an entity field property.", nameof(field));

        var source = member.Expression is null ? null : StripConvert(member.Expression);
        if (source is not ParameterExpression parameter || parameter != field.Parameters[0])
            throw new ArgumentException("Field selector must reference the entity lambda parameter.", nameof(field));

        if (member.Member is not PropertyInfo property || !IsFieldProperty(property.PropertyType))
            throw new ArgumentException($"Member '{member.Member.Name}' is not a semantic field.", nameof(field));

        if (property.GetValue(Entity) is not Field<TValue> resolvedField)
            throw new InvalidOperationException($"Field selector '{property.Name}' could not be resolved on entity type '{typeof(TEntity).Name}'.");

        return resolvedField;
    }

    static bool IsFieldProperty(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Field<>);

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            current = unary.Operand;
        }

        return current;
    }
    
    /// <summary>Extracts the entity state from a snapshot.</summary>
    public static implicit operator EntityState(EntitySnapshot<TEntity> snapshot) => snapshot.State;
}
