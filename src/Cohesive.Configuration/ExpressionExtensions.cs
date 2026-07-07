using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model;

namespace Cohesive.Configuration;

/// <summary>
/// Extension methods for <see cref="Expression"/>
/// </summary>
static class ExpressionExtensions
{
    public static IReadOnlyList<PropertyInfo> CapturePropertyChain<T, TParameter>(Expression<Func<T, TParameter>> selector)
    {
        List<PropertyInfo> reversedProperties = [];
        var current = selector.Body;
        while (true)
        {
            current = StripConvert(current);
            switch (current)
            {
                case MemberExpression { Member: PropertyInfo property, Expression: not null } member:
                    reversedProperties.Add(property);
                    current = member.Expression;
                    continue;
                case ParameterExpression parameter when ReferenceEquals(parameter, selector.Parameters[0]):
                    if (reversedProperties.Count == 0)
                        throw new ArgumentException("Selector must reference a property path.", nameof(selector));
                    reversedProperties.Reverse();
                    return reversedProperties;
                default:
                    throw new ArgumentException("Selector must be a property path rooted at the lambda parameter.", nameof(selector));
            }
        }
    }

    public static FieldPath CreateFieldPath(IEnumerable<PropertyInfo> propertyChain) =>
        new([..propertyChain.Select(property => FieldPathSegment.ForField(property.Name))]);

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
            current = unary.Operand;

        return current;
    }
}