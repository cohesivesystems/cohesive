using System.Linq.Expressions;
using System.Reflection;

namespace Cohesive.Relations.Queries;

// TODO: consolidate reflection member-selection code
static class MemberSelector
{
    public static string ResolveName<TRecord, TValue>(Expression<Func<TRecord, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var body = StripConvert(selector.Body);
        if (body is not MemberExpression member)
            throw new ArgumentException("Field selector must reference a readable property or field.", nameof(selector));

        var source = member.Expression is null ? null : StripConvert(member.Expression);
        if (source is not ParameterExpression parameter || parameter != selector.Parameters[0])
            throw new ArgumentException("Field selector must reference the lambda parameter.", nameof(selector));

        return member.Member switch
        {
            PropertyInfo property => property.Name,
            FieldInfo field => field.Name,
            _ => throw new ArgumentException("Field selector must reference a property or field.", nameof(selector))
        };
    }

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            current = unary.Operand;
        }

        return current;
    }
}