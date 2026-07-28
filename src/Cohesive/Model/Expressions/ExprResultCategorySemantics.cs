namespace Cohesive.Model.Expressions;

/// <summary>Expression-specific interpretations of shared semantic value contracts.</summary>
public static class ValueContractExpressionExtensions
{
    /// <summary>Gets the most specific coarse expression-result category known for a value contract.</summary>
    /// <param name="value">Shared semantic value contract to classify.</param>
    /// <returns>The portable result category, or <see cref="ExprResultCategory.Any"/> when unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static ExprResultCategory GetResultCategory(this ValueContract value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ExprResultCategorySemantics.Classify(value);
    }
}

/// <summary>Shared classification and compatibility rules for coarse expression-result categories.</summary>
internal static class ExprResultCategorySemantics
{
    /// <summary>Classifies a portable value contract into its most specific known coarse category.</summary>
    /// <param name="value">Value contract to classify.</param>
    /// <returns>The most specific known category, or <see cref="ExprResultCategory.Any"/>.</returns>
    public static ExprResultCategory Classify(ValueContract? value)
    {
        if (value is null)
            return ExprResultCategory.Any;
        if (value.Cardinality == FieldCardinality.Many)
            return ExprResultCategory.Collection;

        var isShaped = value.Shape is not null;
        var type = value.GetEffectiveType();
        return type switch
        {
            ArrayTypeRef => ExprResultCategory.Collection,
            ObjectTypeRef => ExprResultCategory.Object,
            // A named type may resolve to a structural, enum, or union definition. Without the
            // owning shape graph its category is intentionally unknown rather than guessed.
            NamedTypeRef => ExprResultCategory.Any,
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } => ExprResultCategory.Boolean,
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 } => ExprResultCategory.Integer,
            ScalarTypeRef { Kind: ScalarTypeKind.Decimal } => ExprResultCategory.Numeric,
            ScalarTypeRef { Kind: ScalarTypeKind.String } => ExprResultCategory.Text,
            ScalarTypeRef { Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant } =>
                ExprResultCategory.Temporal,
            ScalarTypeRef => ExprResultCategory.Scalar,
            QuantityTypeRef => ExprResultCategory.Numeric,
            EnumTypeRef or EntityReferenceTypeRef => ExprResultCategory.Scalar,
            JsonTypeRef { Kind: JsonTypeKind.Boolean } => ExprResultCategory.Boolean,
            JsonTypeRef { Kind: JsonTypeKind.Array } => ExprResultCategory.Collection,
            JsonTypeRef { Kind: JsonTypeKind.Object } => ExprResultCategory.Object,
            JsonTypeRef { Kind: JsonTypeKind.String } => ExprResultCategory.Text,
            JsonTypeRef { Kind: JsonTypeKind.Number } => ExprResultCategory.Numeric,
            _ when isShaped => ExprResultCategory.Object,
            _ => ExprResultCategory.Any
        };
    }

    /// <summary>Tests whether an actual result category satisfies an expected category.</summary>
    /// <param name="actual">Actual or inferred result category.</param>
    /// <param name="expected">Expected result category.</param>
    /// <returns><see langword="true"/> when the actual category satisfies the expectation.</returns>
    public static bool Satisfies(ExprResultCategory actual, ExprResultCategory expected) =>
        actual == ExprResultCategory.Any
        || expected == ExprResultCategory.Any
        || actual == expected
        || expected == ExprResultCategory.Scalar
            && actual is ExprResultCategory.Numeric
                or ExprResultCategory.Integer
                or ExprResultCategory.Text
                or ExprResultCategory.Temporal
        || expected == ExprResultCategory.Numeric && actual == ExprResultCategory.Integer
        || expected == ExprResultCategory.Countable
            && actual is ExprResultCategory.Collection or ExprResultCategory.Object
        || expected == ExprResultCategory.Comparable
            && actual is ExprResultCategory.Numeric
                or ExprResultCategory.Integer
                or ExprResultCategory.Text
                or ExprResultCategory.Temporal;
}
