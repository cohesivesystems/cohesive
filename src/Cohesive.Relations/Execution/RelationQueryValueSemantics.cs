using Cohesive.Model.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

internal static class RelationQueryValueSemantics
{
    public static IEqualityComparer<ObservationValue> EqualityComparer { get; } =
        new ObservationValueEqualityComparer();

    public static IComparer<ObservationValue> Comparer { get; } =
        new ObservationValueComparer();

    public static bool IsNullish(ObservationValue value) =>
        value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined;

    public static bool Equals(ObservationValue left, ObservationValue right) =>
        ObservationValueSemantics.Equals(left, right);

    public static int GetHashCode(ObservationValue value) =>
        ObservationValueSemantics.GetHashCode(value);

    public static int Compare(ObservationValue left, ObservationValue right)
    {
        try
        {
            return ObservationValueSemantics.Compare(left, right);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidOperand(exception.Message);
        }
    }

    public static int CompareForOrdering(
        ObservationValue left,
        ObservationValue right,
        QuerySortDirection direction,
        QueryNullPlacement nullPlacement)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported query sort direction.");
        if (!Enum.IsDefined(nullPlacement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported query null placement.");

        var leftNullish = IsNullish(left);
        var rightNullish = IsNullish(right);
        if (leftNullish || rightNullish)
        {
            if (leftNullish && rightNullish)
                return 0;

            return leftNullish == (nullPlacement == QueryNullPlacement.First) ? -1 : 1;
        }

        var result = Math.Sign(Compare(left, right));
        return direction == QuerySortDirection.Ascending ? result : -result;
    }

    public static ObservationValue Add(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Add, left, right, ObservationValueSemantics.Add);

    public static ObservationValue Subtract(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Sub, left, right, ObservationValueSemantics.Subtract);

    public static ObservationValue Multiply(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Mul, left, right, ObservationValueSemantics.Multiply);

    public static ObservationValue Divide(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Div, left, right, ObservationValueSemantics.Divide);

    internal static decimal RequireDecimal(ObservationValue value, string operation)
    {
        try
        {
            return ObservationValueSemantics.RequireDecimal(value, operation);
        }
        catch (OverflowException exception)
        {
            throw NumericFailure(
                $"Numeric value '{value}' has no exact representation in the supported decimal execution domain for '{operation}'.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidOperand(exception.Message);
        }
    }

    static ObservationValue Arithmetic(
        BinaryOperator operation,
        ObservationValue left,
        ObservationValue right,
        Func<ObservationValue, ObservationValue, ObservationValue> evaluate)
    {
        try
        {
            return evaluate(left, right);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidOperand(exception.Message);
        }
        catch (DivideByZeroException exception)
        {
            throw NumericFailure("Division by zero is not defined.", exception);
        }
        catch (OverflowException exception)
        {
            throw NumericFailure($"Arithmetic operation '{operation}' overflowed.", exception);
        }
    }

    static RelationQueryExpressionEvaluationException InvalidOperand(string message) =>
        new(RelationQueryExpressionEvaluationError.InvalidOperand, message);

    static RelationQueryExpressionEvaluationException NumericFailure(
        string message,
        Exception innerException) =>
        new(RelationQueryExpressionEvaluationError.NumericFailure, message, innerException);

    sealed class ObservationValueEqualityComparer : IEqualityComparer<ObservationValue>
    {
        public bool Equals(ObservationValue x, ObservationValue y) =>
            RelationQueryValueSemantics.Equals(x, y);

        public int GetHashCode(ObservationValue obj) =>
            RelationQueryValueSemantics.GetHashCode(obj);
    }

    sealed class ObservationValueComparer : IComparer<ObservationValue>
    {
        public int Compare(ObservationValue x, ObservationValue y) =>
            RelationQueryValueSemantics.Compare(x, y);
    }
}
