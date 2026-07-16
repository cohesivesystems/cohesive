using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class CosmosSqlStandaloneContractTests
{
    [Fact]
    public void BuildTemplate_AllowListedFunctionsAndConditional_PreserveFirstUseParameterOrder()
    {
        var name = CosmosSqlExpression.Property("c", FieldPath.FromField("Name"));
        var status = CosmosSqlExpression.Property("c", FieldPath.FromField("Status"));
        var template = new CosmosSqlBuilder()
            .Select(
                CosmosSqlExpression.Conditional(
                    CosmosSqlExpression.Function(CosmosSqlFunction.IsDefined, name),
                    CosmosSqlExpression.Function(CosmosSqlFunction.Lower, name),
                    CosmosSqlExpression.Parameter("unknown")),
                "display")
            .Where(CosmosSqlExpression.Function(
                CosmosSqlFunction.StartsWith,
                status,
                CosmosSqlExpression.RuntimeParameter("prefix"),
                CosmosSqlExpression.Parameter(true)))
            .BuildTemplate();

        Assert.Equal(
            "SELECT IIF(IS_DEFINED(c[\"Name\"]), LOWER(c[\"Name\"]), @p0) AS display "
            + "FROM c WHERE STARTSWITH(c[\"Status\"], @p1, @p2)",
            template.Text);
        Assert.Equal(["@p0", "@p1", "@p2"], template.Parameters.Select(static slot => slot.Name));
        Assert.Equal(
            [
                CosmosSqlParameterBindingKind.Constant,
                CosmosSqlParameterBindingKind.Runtime,
                CosmosSqlParameterBindingKind.Constant
            ],
            template.Parameters.Select(static slot => slot.Kind));

        var statement = template.Bind(new Dictionary<string, object?>
        {
            ["prefix"] = "rea"
        });

        Assert.Equal("unknown", statement.Parameters[0].Value);
        Assert.Equal("rea", statement.Parameters[1].Value);
        Assert.Equal(true, statement.Parameters[2].Value);
    }

    [Fact]
    public void Bind_ObservationObject_NormalizesNestedValuesDeterministically()
    {
        var template = new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.RuntimeParameter("payload"))
            .BuildTemplate();
        var payload = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["z"] = ObservationValue.FromInt64(2),
            ["items"] = ObservationValue.FromArray(
            [
                ObservationValue.FromString("first"),
                ObservationValue.Null
            ]),
            ["a"] = ObservationValue.FromBool(true)
        });

        var statement = template.Bind(new Dictionary<string, object?>
        {
            ["payload"] = payload
        });

        var normalized = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(statement.Parameters).Value);
        Assert.Equal(["a", "items", "z"], normalized.Keys);
        Assert.Equal(true, normalized["a"]);
        Assert.Equal(2L, normalized["z"]);
        Assert.Equal(
            new object?[] { "first", null },
            Assert.IsAssignableFrom<IEnumerable<object?>>(normalized["items"]));
    }

    [Fact]
    public void Parameter_CapturedStructuredValue_IsDeeplySnapshottedAndFinite()
    {
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal)
        {
            ["items"] = ObservationValue.FromArray([ObservationValue.FromInt64(1)]),
            ["name"] = ObservationValue.FromString("before")
        };
        var payload = ObservationValue.FromObject(fields);
        var expression = CosmosSqlExpression.Parameter(payload);
        fields["name"] = ObservationValue.FromString("after");

        var statement = new CosmosSqlBuilder()
            .SelectValue(expression)
            .Build();

        var normalized = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(statement.Parameters).Value);
        Assert.Equal("before", normalized["name"]);
        Assert.Equal(
            new object?[] { 1L },
            Assert.IsAssignableFrom<IEnumerable<object?>>(normalized["items"]));
        Assert.NotNull(statement.ToQueryDefinition());
        Assert.Throws<NotSupportedException>(() => CosmosSqlExpression.Parameter(float.NaN));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.RuntimeParameter("value"))
            .BuildTemplate()
            .Bind(new Dictionary<string, object?> { ["value"] = double.PositiveInfinity }));
    }

    [Fact]
    public void Builder_RejectsAmbiguousSelectionAliasesAndFunctionArity()
    {
        var builder = new CosmosSqlBuilder()
            .Select(CosmosSqlExpression.Alias("c"), "projectedValue");

        Assert.Throws<ArgumentException>(() => builder.Select(CosmosSqlExpression.Alias("c"), "projectedValue"));
        Assert.Throws<InvalidOperationException>(() => builder.SelectValue(CosmosSqlExpression.Alias("c")));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder().JoinCollection(
            "c",
            CosmosSqlExpression.Property("c", FieldPath.FromField("items"))));
        Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Function(
            CosmosSqlFunction.IsDefined,
            CosmosSqlExpression.Alias("c"),
            CosmosSqlExpression.Alias("c")));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.RuntimeParameter("value"))
            .Build());
    }

    [Fact]
    public void Builder_RejectsReservedWordsAsAliases_CaseInsensitively()
    {
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder("SELECT"));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder()
            .Select(CosmosSqlExpression.Alias("c"), "value"));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder()
            .JoinCollection(
                "jOiN",
                CosmosSqlExpression.Property("c", FieldPath.FromField("items"))));
        Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Alias("where"));
    }

    [Fact]
    public void Parameter_RejectsCyclicStructuredValues()
    {
        List<object?> cyclicSequence = [];
        cyclicSequence.Add(cyclicSequence);
        Dictionary<string, object?> cyclicObject = new(StringComparer.Ordinal);
        cyclicObject.Add("self", cyclicObject);
        Dictionary<string, ObservationValue> cyclicObservationFields = new(StringComparer.Ordinal)
        {
            ["self"] = ObservationValue.Null
        };
        var cyclicObservation = ObservationValue.FromObject(cyclicObservationFields);
        cyclicObservationFields["self"] = cyclicObservation;

        var sequenceException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(cyclicSequence));
        var objectException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(cyclicObject));
        var observationException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(cyclicObservation));

        Assert.Contains("Cyclic structured values", sequenceException.Message, StringComparison.Ordinal);
        Assert.Contains("Cyclic structured values", objectException.Message, StringComparison.Ordinal);
        Assert.Contains("Cyclic structured values", observationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_RejectsMultidimensionalArraysAndExcessiveNesting()
    {
        object nested = "leaf";
        for (var depth = 0; depth < 65; depth++)
            nested = new object?[] { nested };

        var arrayException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(new int[2, 2]));
        var nestingException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(nested));

        Assert.Contains("Multidimensional CLR arrays", arrayException.Message, StringComparison.Ordinal);
        Assert.Contains("cannot exceed 64 levels", nestingException.Message, StringComparison.Ordinal);
    }
}
