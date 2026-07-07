using System.Globalization;
using System.Text;
using Cohesive.Model;

namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Emits target-side TypeScript expressions from the canonical Cohesive expression IR.
/// </summary>
/// <remarks>
/// This emitter intentionally supports a portable expression profile. Infrastructure-specific
/// relation functions and transition runtime nodes should be interpreted by their own adapters.
/// </remarks>
public sealed class TypeScriptExprEmitter(TypeScriptExprEmitterOptions? options = null)
{
    readonly TypeScriptExprEmitterOptions options = options ?? new();

    /// <summary>
    /// Emits a TypeScript expression for the supplied Cohesive expression.
    /// </summary>
    public string Emit(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return EmitCore(expression);
    }

    /// <summary>
    /// Emits a TypeScript expression using default options.
    /// </summary>
    public static string EmitExpression(Expr expression) => new TypeScriptExprEmitter().Emit(expression);

    string EmitCore(Expr expression) => expression switch
    {
        FieldExpr field => EmitPath(options.FieldRootIdentifier, field.Path),
        FieldRefExpr field => EmitPath(options.FieldRootIdentifier, field.Path),
        CurrentItemExpr => options.CurrentItemIdentifier,
        ParameterExpr parameter => EmitParameter(parameter),
        ConstantExpr constant => EmitObservationValue(constant.Value),
        LiteralExpr literal => EmitObservationValue(literal.Value),
        UnaryExpr unary => EmitUnary(unary),
        BinaryExpr binary => EmitBinary(binary),
        ConditionalExpr conditional => EmitConditional(conditional),
        CallExpr call => EmitCall(call),
        AggregateExpr aggregate => EmitAggregate(aggregate),
        _ => throw new NotSupportedException($"Expression node '{expression.GetType().Name}' is not supported by the TypeScript expression emitter.")
    };

    string EmitParameter(ParameterExpr parameter)
    {
        if (options.ParameterBindings.TryGetValue(parameter.Parameter, out var binding))
            return binding;

        return EmitDottedPath(options.ParameterRootIdentifier, parameter.Parameter);
    }

    string EmitUnary(UnaryExpr unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => $"!({EmitCore(unary.Operand)})",
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported by the TypeScript expression emitter.")
        };
    }

    string EmitBinary(BinaryExpr binary)
    {
        var op = binary.Operator switch
        {
            BinaryOperator.Eq => "===",
            BinaryOperator.Ne => "!==",
            BinaryOperator.Gt => ">",
            BinaryOperator.Ge => ">=",
            BinaryOperator.Lt => "<",
            BinaryOperator.Le => "<=",
            BinaryOperator.And => "&&",
            BinaryOperator.Or => "||",
            BinaryOperator.Add => "+",
            BinaryOperator.Sub => "-",
            BinaryOperator.Mul => "*",
            BinaryOperator.Div => "/",
            _ => throw new NotSupportedException($"Binary operator '{binary.Operator}' is not supported by the TypeScript expression emitter.")
        };

        return $"({EmitCore(binary.Left)} {op} {EmitCore(binary.Right)})";
    }

    string EmitConditional(ConditionalExpr conditional) =>
        $"({EmitCore(conditional.Test)} ? {EmitCore(conditional.IfTrue)} : {EmitCore(conditional.IfFalse)})";

    string EmitCall(CallExpr call)
    {
        return call.Function switch
        {
            ExprFunctionNames.Contains => EmitContains(call),
            ExprFunctionNames.Count => EmitCount(call),
            _ => throw new NotSupportedException($"Function '{call.Function}' is not supported by the TypeScript expression emitter.")
        };
    }

    string EmitAggregate(AggregateExpr aggregate)
    {
        return aggregate.Operator switch
        {
            AggregateOperator.Count => EmitCount(new CallExpr(ExprFunctionNames.Count, [aggregate.Source])),
            _ => throw new NotSupportedException($"Aggregate operator '{aggregate.Operator}' is not supported by the TypeScript expression emitter.")
        };
    }

    string EmitContains(CallExpr call)
    {
        RequireArgumentCount(call, 2);
        return $"({EmitCore(call.Arguments[0])}).includes({EmitCore(call.Arguments[1])})";
    }

    string EmitCount(CallExpr call)
    {
        RequireArgumentCount(call, 1);
        return $"({EmitCore(call.Arguments[0])}).length";
    }

    static void RequireArgumentCount(CallExpr call, int count)
    {
        if (call.Arguments.Length != count)
            throw new NotSupportedException($"Function '{call.Function}' expects {count.ToString(CultureInfo.InvariantCulture)} argument(s), but received {call.Arguments.Length.ToString(CultureInfo.InvariantCulture)}.");
    }

    static string EmitPath(string root, FieldPath path)
    {
        var builder = new StringBuilder(root);
        foreach (var segment in path.Segments)
        {
            if (segment.Kind is not SegmentKind.Field || segment.Segment is null)
                throw new NotSupportedException($"Field path segment kind '{segment.Kind}' is not supported by the TypeScript expression emitter.");

            AppendPropertyAccess(builder, segment.Segment);
        }

        return builder.ToString();
    }

    static string EmitDottedPath(string root, string path)
    {
        var builder = new StringBuilder(root);
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AppendPropertyAccess(builder, segment);

        return builder.ToString();
    }

    static void AppendPropertyAccess(StringBuilder builder, string property)
    {
        if (IsIdentifier(property))
        {
            builder.Append('.');
            builder.Append(property);
            return;
        }

        builder.Append('[');
        builder.Append(QuoteString(property));
        builder.Append(']');
    }

    static string EmitObservationValue(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Undefined => "undefined",
            ObservationValueKind.Null => "null",
            ObservationValueKind.Bool => value.Bool ? "true" : "false",
            ObservationValueKind.Int64 => value.Int64.ToString(CultureInfo.InvariantCulture),
            ObservationValueKind.Double => value.Double.ToString("R", CultureInfo.InvariantCulture),
            ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan => QuoteString(value.String ?? string.Empty),
            ObservationValueKind.Array => EmitArray(value.Array ?? []),
            ObservationValueKind.Object => EmitObject(value.Fields ?? new Dictionary<string, ObservationValue>(StringComparer.Ordinal)),
            ObservationValueKind.Bytes => throw new NotSupportedException("Bytes values are not supported by the TypeScript expression emitter."),
            _ => throw new NotSupportedException($"Observation value kind '{value.Kind}' is not supported by the TypeScript expression emitter.")
        };
    }

    static string EmitArray(IReadOnlyList<ObservationValue> values) =>
        $"[{string.Join(", ", values.Select(EmitObservationValue))}]";

    static string EmitObject(IReadOnlyDictionary<string, ObservationValue> values)
    {
        var properties = values.Select(kvp =>
            IsIdentifier(kvp.Key)
                ? $"{kvp.Key}: {EmitObservationValue(kvp.Value)}"
                : $"{QuoteString(kvp.Key)}: {EmitObservationValue(kvp.Value)}");
        return $"{{ {string.Join(", ", properties)} }}";
    }

    static string QuoteString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var current in value)
        {
            switch (current)
            {
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(current);
                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!(char.IsLetter(value[0]) || value[0] == '_' || value[0] == '$'))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsLetterOrDigit(current) || current == '_' || current == '$')
                continue;

            return false;
        }

        return true;
    }
}

/// <summary>
/// Options for TypeScript expression emission.
/// </summary>
public sealed record TypeScriptExprEmitterOptions
{
    /// <summary>
    /// Root identifier used for <see cref="ParameterExpr"/> nodes when no explicit binding is provided.
    /// </summary>
    public string ParameterRootIdentifier { get; init; } = "context";

    /// <summary>
    /// Root identifier used for <see cref="FieldExpr"/> and <see cref="FieldRefExpr"/> nodes.
    /// </summary>
    public string FieldRootIdentifier { get; init; } = "context";

    /// <summary>
    /// Identifier used for <see cref="CurrentItemExpr"/> nodes.
    /// </summary>
    public string CurrentItemIdentifier { get; init; } = "item";

    /// <summary>
    /// Explicit TypeScript expressions for named parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string> ParameterBindings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
