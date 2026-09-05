using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
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
                SqlParameterBindingKind.Constant,
                SqlParameterBindingKind.Runtime,
                SqlParameterBindingKind.Constant
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
    public void FunctionAndObject_ImmutableAndFixedArityPaths_RenderEquivalentSql()
    {
        var name = CosmosSqlExpression.Property("c", FieldPath.FromField("Name"));
        var prefix = CosmosSqlExpression.Parameter("co");
        var caseInsensitive = CosmosSqlExpression.Parameter(true);
        ImmutableArray<CosmosSqlExpression> immutableFunctionArguments = [name, prefix, caseInsensitive];
        ImmutableArray<CosmosSqlObjectProperty> immutableProperties =
        [
            new("normalized", CosmosSqlExpression.Function(CosmosSqlFunction.Lower, name)),
            new(
                "matches",
                CosmosSqlExpression.FunctionFromImmutable(
                    CosmosSqlFunction.StartsWith,
                    immutableFunctionArguments))
        ];

        var statement = new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.ObjectFromImmutable(immutableProperties))
            .Build();

        Assert.Equal(
            "SELECT VALUE { \"normalized\": LOWER(c[\"Name\"]), \"matches\": STARTSWITH(c[\"Name\"], @p0, @p1) } FROM c",
            statement.Text);
        Assert.Equal(["co", true], statement.Parameters.Select(static parameter => parameter.Value));
    }

    [Fact]
    public void FunctionAndObject_MutableArrayEntrypoints_DefensivelySnapshotInputs()
    {
        CosmosSqlExpression[] arguments =
        [
            CosmosSqlExpression.Property("c", FieldPath.FromField("Name"))
        ];
        var function = CosmosSqlExpression.FunctionFromMutable(CosmosSqlFunction.Lower, arguments);
        arguments[0] = CosmosSqlExpression.Property("c", FieldPath.FromField("Changed"));
        CosmosSqlObjectProperty[] properties = [new("original", function)];
        var expression = CosmosSqlExpression.ObjectFromMutable(properties);
        properties[0] = new("changed", CosmosSqlExpression.Alias("c"));

        var statement = new CosmosSqlBuilder()
            .SelectValue(expression)
            .Build();

        Assert.Equal("SELECT VALUE { \"original\": LOWER(c[\"Name\"]) } FROM c", statement.Text);
    }

    [Fact]
    public void FunctionAndObject_NullAndDefaultInputs_HaveUnambiguousContracts()
    {
        var functionNull = Assert.Throws<ArgumentNullException>(
            () => CosmosSqlExpression.Function(CosmosSqlFunction.Lower, null!));
        var objectNull = Assert.Throws<ArgumentNullException>(
            () => CosmosSqlExpression.Object(null!));
        var mutableFunctionNull = Assert.Throws<ArgumentNullException>(
            () => CosmosSqlExpression.FunctionFromMutable(CosmosSqlFunction.Lower, null!));
        var mutableObjectNull = Assert.Throws<ArgumentNullException>(
            () => CosmosSqlExpression.ObjectFromMutable(null!));

        Assert.Equal("argument", functionNull.ParamName);
        Assert.Equal("property", objectNull.ParamName);
        Assert.Equal("arguments", mutableFunctionNull.ParamName);
        Assert.Equal("properties", mutableObjectNull.ParamName);
        Assert.Throws<ArgumentNullException>(
            () => CosmosSqlExpression.Function(CosmosSqlFunction.Lower, default!));
        Assert.Throws<ArgumentNullException>(() => CosmosSqlExpression.Object(default!));
    }

    [Fact]
    public void CollectionExists_NestedScopesAndStatementAliases_AllocateDeterministically()
    {
        var first = CosmosSqlExpression.CollectionExists(
            CosmosSqlExpression.Property("e1", FieldPath.FromField("Children")),
            item => CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Equal,
                CosmosSqlExpression.Property(item, FieldPath.FromField("Code")),
                CosmosSqlExpression.Parameter("first")));
        var second = CosmosSqlExpression.CollectionExists(
            CosmosSqlExpression.Property("e1", FieldPath.FromField("Children")),
            item => CosmosSqlExpression.CollectionExists(
                CosmosSqlExpression.Property(item, FieldPath.FromField("Details")),
                detail => CosmosSqlExpression.Binary(
                    CosmosSqlBinaryOperator.Equal,
                    CosmosSqlExpression.Property(detail, FieldPath.FromField("Value")),
                    CosmosSqlExpression.Parameter("second"))));
        var builder = new CosmosSqlBuilder("e0")
            .JoinCollection("e1", CosmosSqlExpression.Property("e0", FieldPath.FromField("Items")))
            .SelectValue(CosmosSqlExpression.Alias("e0"))
            .Where(CosmosSqlExpression.Binary(CosmosSqlBinaryOperator.And, first, second));

        var firstBuild = builder.Build();
        var secondBuild = builder.Build();

        Assert.Equal(
            "SELECT VALUE e0 FROM e0 JOIN e1 IN e0[\"Items\"] WHERE "
            + "(EXISTS (SELECT VALUE e2 FROM e2 IN e1[\"Children\"] WHERE (e2[\"Code\"] = @p0)) AND "
            + "EXISTS (SELECT VALUE e3 FROM e3 IN e1[\"Children\"] WHERE EXISTS (SELECT VALUE e4 FROM e4 IN "
            + "e3[\"Details\"] WHERE (e4[\"Value\"] = @p1))))",
            firstBuild.Text);
        Assert.Equal(firstBuild.Text, secondBuild.Text);
        Assert.Equal(["first", "second"], firstBuild.Parameters.Select(static parameter => parameter.Value));
        Assert.True(firstBuild.Parameters.SequenceEqual(secondBuild.Parameters));
    }

    [Fact]
    public void CollectionExists_InvalidInputsAndEscapedItem_FailClosed()
    {
        var collection = CosmosSqlExpression.Property("c", FieldPath.FromField("Items"));
        var nullCollection = Assert.Throws<ArgumentNullException>(() =>
            CosmosSqlExpression.CollectionExists(
                null!,
                static item => item));
        var nullPredicate = Assert.Throws<ArgumentNullException>(() =>
            CosmosSqlExpression.CollectionExists(collection, null!));
        var nullPredicateResult = Assert.Throws<ArgumentException>(() =>
            CosmosSqlExpression.CollectionExists(
                collection,
                static _ => null!));

        CosmosSqlExpression? escapedItem = null;
        var valid = CosmosSqlExpression.CollectionExists(
            collection,
            item =>
            {
                escapedItem = item;
                return CosmosSqlExpression.Parameter(true);
            });
        Assert.NotNull(new CosmosSqlBuilder().SelectValue(valid).Build());
        var escaped = Assert.Throws<InvalidOperationException>(() =>
            new CosmosSqlBuilder().SelectValue(escapedItem!).Build());

        Assert.Equal("collection", nullCollection.ParamName);
        Assert.Equal("predicate", nullPredicate.ParamName);
        Assert.Equal("predicate", nullPredicateResult.ParamName);
        Assert.Contains("outside its existential predicate scope", escaped.Message, StringComparison.Ordinal);
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
        var invalidArity = Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Function(
            CosmosSqlFunction.IsDefined,
            CosmosSqlExpression.Alias("c"),
            CosmosSqlExpression.Alias("c")));
        var duplicateSecondProperty = Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Object(
            new("duplicate", CosmosSqlExpression.Alias("c")),
            new("duplicate", CosmosSqlExpression.Alias("c"))));
        var duplicateThirdProperty = Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Object(
            new("first", CosmosSqlExpression.Alias("c")),
            new("duplicate", CosmosSqlExpression.Alias("c")),
            new("duplicate", CosmosSqlExpression.Alias("c"))));
        var invalidImmutableArity = Assert.Throws<ArgumentException>(() =>
            CosmosSqlExpression.FunctionFromImmutable(
                CosmosSqlFunction.IsDefined,
                [CosmosSqlExpression.Alias("c"), CosmosSqlExpression.Alias("c")]));
        var invalidMutableArity = Assert.Throws<ArgumentException>(() =>
            CosmosSqlExpression.FunctionFromMutable(
                CosmosSqlFunction.IsDefined,
                [CosmosSqlExpression.Alias("c"), CosmosSqlExpression.Alias("c")]));
        var duplicateImmutableProperty = Assert.Throws<ArgumentException>(() =>
            CosmosSqlExpression.ObjectFromImmutable(
                [
                    new("duplicate", CosmosSqlExpression.Alias("c")),
                    new("duplicate", CosmosSqlExpression.Alias("c"))
                ]));
        var duplicateMutableProperty = Assert.Throws<ArgumentException>(() =>
            CosmosSqlExpression.ObjectFromMutable(
                [
                    new("duplicate", CosmosSqlExpression.Alias("c")),
                    new("duplicate", CosmosSqlExpression.Alias("c"))
                ]));
        Assert.Equal("function", invalidArity.ParamName);
        Assert.Equal("function", invalidImmutableArity.ParamName);
        Assert.Equal("function", invalidMutableArity.ParamName);
        Assert.Equal("secondProperty", duplicateSecondProperty.ParamName);
        Assert.Equal("thirdProperty", duplicateThirdProperty.ParamName);
        Assert.Equal("properties", duplicateImmutableProperty.ParamName);
        Assert.Equal("properties", duplicateMutableProperty.ParamName);
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.RuntimeParameter("value"))
            .Build());
        Assert.Throws<ArgumentException>(() => CosmosSqlExpression.FunctionFromImmutable(
            CosmosSqlFunction.Lower,
            default));
        Assert.Throws<ArgumentException>(() => CosmosSqlExpression.ObjectFromImmutable(default));
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
    public void Parameter_RejectsCyclicClrStructuredValues()
    {
        List<object?> cyclicSequence = [];
        cyclicSequence.Add(cyclicSequence);
        Dictionary<string, object?> cyclicObject = new(StringComparer.Ordinal);
        cyclicObject.Add("self", cyclicObject);

        var sequenceException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(cyclicSequence));
        var objectException = Assert.Throws<NotSupportedException>(
            () => CosmosSqlExpression.Parameter(cyclicObject));

        Assert.Contains("Cyclic structured values", sequenceException.Message, StringComparison.Ordinal);
        Assert.Contains("Cyclic structured values", objectException.Message, StringComparison.Ordinal);
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
