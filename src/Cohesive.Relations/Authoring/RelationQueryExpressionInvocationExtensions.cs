using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Typed conveniences for authoring invocations of expression-authored canonical queries.
/// </summary>
public static class RelationQueryExpressionInvocationExtensions
{
    /// <summary>Supplies a CLR value for a declared typed query parameter.</summary>
    /// <typeparam name="T">CLR parameter type.</typeparam>
    /// <param name="builder">Invocation builder receiving the parameter evidence.</param>
    /// <param name="parameter">Typed parameter declaration.</param>
    /// <param name="value">CLR value converted to canonical observation evidence.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="parameter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The parameter's expression session did not produce the exact invoked query, the parameter is not
    /// declared by that query, or the converted value is incompatible with its canonical contract.
    /// </exception>
    /// <exception cref="InvalidOperationException">The parameter was already configured.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="value"/> cannot be represented as an <see cref="ObservationValue"/>.
    /// </exception>
    public static RelationQueryInvocationBuilder Set<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionParameter<T> parameter,
        T value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Owner.RequireInvocationDefinition(builder, nameof(parameter));
        return builder.Set(parameter.Id, ObservationValue.FromObject(value));
    }

    /// <summary>Supplies explicit null evidence for a nullable typed query parameter.</summary>
    /// <typeparam name="T">CLR parameter type.</typeparam>
    /// <param name="builder">Invocation builder receiving the parameter evidence.</param>
    /// <param name="parameter">Typed parameter declaration.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="parameter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The parameter's expression session did not produce the exact invoked query, the parameter is not
    /// declared by that query, or its effective contract is non-nullable.
    /// </exception>
    /// <exception cref="InvalidOperationException">The parameter was already configured.</exception>
    public static RelationQueryInvocationBuilder SetNull<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionParameter<T> parameter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Owner.RequireInvocationDefinition(builder, nameof(parameter));
        return builder.SetNull(parameter.Id);
    }

    /// <summary>Supplies explicit semantic-missing evidence for a typed query parameter.</summary>
    /// <typeparam name="T">CLR parameter type.</typeparam>
    /// <param name="builder">Invocation builder receiving the parameter evidence.</param>
    /// <param name="parameter">Typed parameter declaration.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="parameter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The parameter's expression session did not produce the exact invoked query, or the parameter is
    /// not declared by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">The parameter was already configured.</exception>
    public static RelationQueryInvocationBuilder SetMissing<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionParameter<T> parameter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Owner.RequireInvocationDefinition(builder, nameof(parameter));
        return builder.SetMissing(parameter.Id);
    }

    /// <summary>Explicitly omits a declared typed query parameter.</summary>
    /// <typeparam name="T">CLR parameter type.</typeparam>
    /// <param name="builder">Invocation builder receiving the omission evidence.</param>
    /// <param name="parameter">Typed parameter declaration.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="parameter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The parameter's expression session did not produce the exact invoked query, or the parameter is
    /// not declared by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">The parameter was already configured.</exception>
    public static RelationQueryInvocationBuilder Omit<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionParameter<T> parameter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Owner.RequireInvocationDefinition(builder, nameof(parameter));
        return builder.Omit(parameter.Id);
    }

    /// <summary>Selects every field emitted by a typed row result.</summary>
    /// <typeparam name="T">CLR row type represented by the result.</typeparam>
    /// <param name="builder">Invocation builder receiving the result demand.</param>
    /// <param name="result">Typed named row result.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The result's expression session did not produce the exact invoked query, or the result is not
    /// declared by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">The result was already selected.</exception>
    public static RelationQueryInvocationBuilder Select<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionRowsResult<T> result)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(result);
        result.Owner.RequireInvocationDefinition(builder, nameof(result));
        return builder.Select(result.Id);
    }

    /// <summary>Selects explicit fields emitted by a typed row result.</summary>
    /// <typeparam name="T">CLR row type represented by the result.</typeparam>
    /// <param name="builder">Invocation builder receiving the result demand.</param>
    /// <param name="result">Typed named row result.</param>
    /// <param name="fields">
    /// Non-empty direct or nested property selectors resolved by the same CLR metadata profile that
    /// produced the result shape.
    /// </param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/>, <paramref name="result"/>, or <paramref name="fields"/> is
    /// <see langword="null"/>, or <paramref name="fields"/> contains a null selector.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The result's expression session did not produce the exact invoked query; <paramref name="fields"/>
    /// is empty; a selector is not a property chain rooted at its parameter; or the result is not declared
    /// by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A selected CLR property has no field mapping in the result's metadata profile, or the result was
    /// already selected.
    /// </exception>
    public static RelationQueryInvocationBuilder Select<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionRowsResult<T> result,
        params Expression<Func<T, object?>>[] fields)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
            throw new ArgumentException("Selected result fields cannot be empty.", nameof(fields));
        result.Owner.RequireInvocationDefinition(builder, nameof(result));

        return builder.Select(
            result.Id,
            fields.Select(field => new RelationQueryFieldReference(
                result.Shape,
                ResolveSelector(result.Owner, field, nameof(fields)))));
    }

    /// <summary>Selects every field emitted by a typed aggregation result.</summary>
    /// <typeparam name="T">CLR aggregation-row type represented by the result.</typeparam>
    /// <param name="builder">Invocation builder receiving the result demand.</param>
    /// <param name="result">Typed named aggregation result.</param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The result's expression session did not produce the exact invoked query, or the result is not
    /// declared by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">The result was already selected.</exception>
    public static RelationQueryInvocationBuilder Select<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionAggregationResult<T> result)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(result);
        result.Owner.RequireInvocationDefinition(builder, nameof(result));
        return builder.Select(result.Id);
    }

    /// <summary>Selects explicit fields emitted by a typed aggregation result.</summary>
    /// <typeparam name="T">CLR aggregation-row type represented by the result.</typeparam>
    /// <param name="builder">Invocation builder receiving the result demand.</param>
    /// <param name="result">Typed named aggregation result.</param>
    /// <param name="fields">
    /// Non-empty direct or nested property selectors resolved by the same CLR metadata profile that
    /// produced the result shape.
    /// </param>
    /// <returns><paramref name="builder"/> for continued invocation authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/>, <paramref name="result"/>, or <paramref name="fields"/> is
    /// <see langword="null"/>, or <paramref name="fields"/> contains a null selector.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The result's expression session did not produce the exact invoked query; <paramref name="fields"/>
    /// is empty; a selector is not a property chain rooted at its parameter; or the result is not declared
    /// by that query.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A selected CLR property has no field mapping in the result's metadata profile, or the result was
    /// already selected.
    /// </exception>
    public static RelationQueryInvocationBuilder Select<T>(
        this RelationQueryInvocationBuilder builder,
        RelationQueryExpressionAggregationResult<T> result,
        params Expression<Func<T, object?>>[] fields)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
            throw new ArgumentException("Selected result fields cannot be empty.", nameof(fields));
        result.Owner.RequireInvocationDefinition(builder, nameof(result));

        return builder.Select(
            result.Id,
            fields.Select(field => new RelationQueryFieldReference(
                result.Shape,
                ResolveSelector(result.Owner, field, nameof(fields)))));
    }

    static FieldPath ResolveSelector<T>(
        RelationQueryExpressionAuthoring owner,
        Expression<Func<T, object?>> selector,
        string parameterName)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression current = selector.Body;
        while (current is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
            } conversion
               && conversion.Type == typeof(object))
        {
            current = conversion.Operand;
        }

        List<PropertyInfo> reversed = [];
        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo property)
                throw new ArgumentException("A selected result field must use readable CLR properties.", parameterName);
            reversed.Add(property);
            current = member.Expression
                ?? throw new ArgumentException("A selected result field cannot use a static member.", parameterName);
        }

        if (!ReferenceEquals(current, selector.Parameters[0]) || reversed.Count == 0)
        {
            throw new ArgumentException(
                "A selected result field must be a direct or nested property chain rooted at the selector parameter.",
                parameterName);
        }

        reversed.Reverse();
        return owner.ResolveMemberPath(typeof(T), reversed);
    }
}
