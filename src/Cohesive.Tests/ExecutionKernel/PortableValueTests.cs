using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PortableValueTests
{
    static readonly ValueContract OptionalNullableString = new(
        new ScalarTypeRef(ScalarTypeKind.String),
        presence: FieldPresence.Optional,
        nullability: FieldNullability.Nullable);

    [Fact]
    public void StateFactories_PreserveSixDistinctSemanticStates()
    {
        PortableValue[] values =
        [
            PortableValue.Missing(OptionalNullableString),
            PortableValue.Absent(OptionalNullableString),
            PortableValue.Null(OptionalNullableString),
            PortableValue.Unknown(OptionalNullableString),
            PortableValue.Failed(
                OptionalNullableString,
                new("source.timeout", DiagnosticSeverity.Error, "The source timed out.")),
            PortableValue.Concrete(OptionalNullableString, ObservationValue.FromString("ready"))
        ];

        Assert.Equal(6, values.Select(static value => value.State).Distinct().Count());
        Assert.Equal(values, values.Select(RoundTrip));
        Assert.Equal(values[0], PortableValue.Missing(OptionalNullableString));
        Assert.NotEqual(values[0], values[1]);
    }

    [Fact]
    public void ConcreteFactory_RejectsUndefinedAndNullRootObservations()
    {
        Assert.Throws<ArgumentException>(() =>
            PortableValue.Concrete(OptionalNullableString, ObservationValue.Undefined));
        Assert.Throws<ArgumentException>(() =>
            PortableValue.Concrete(OptionalNullableString, ObservationValue.Null));
    }

    [Fact]
    public void FailedFactory_RequiresACompleteErrorDiagnostic()
    {
        Assert.Throws<ArgumentNullException>(() => PortableValue.Failed(OptionalNullableString, null!));
        Assert.Throws<ArgumentException>(() => PortableValue.Failed(
            OptionalNullableString,
            new("source.timeout", DiagnosticSeverity.Warning, "The source may have timed out.")));
        Assert.Throws<ArgumentException>(() => PortableValue.Failed(
            OptionalNullableString,
            new(" ", DiagnosticSeverity.Error, "The source timed out.")));
    }

    [Fact]
    public void TaggedJson_RoundTripsEveryObservationKindRecursively()
    {
        var expectedFields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        expectedFields["array"] = ObservationValue.FromImmutableArray(
        [
            ObservationValue.Undefined,
            ObservationValue.Null,
            ObservationValue.FromString("nested")
        ]);
        expectedFields["bool"] = ObservationValue.FromBool(true);
        expectedFields["bytes"] = ObservationValue.FromBytes(new byte[] { 0, 1, 127, 255 });
        expectedFields["dateOnly"] = ObservationValue.FromDateOnly(new DateOnly(2026, 7, 27));
        expectedFields["dateTimeOffset"] = ObservationValue.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 27, 14, 15, 16, TimeSpan.FromHours(-7)));
        expectedFields["decimal"] = new(ObservationValueKind.Decimal, dec: 7922816251426433759354395033.5m);
        expectedFields["double"] = ObservationValue.FromDouble(Math.PI);
        expectedFields["int64"] = ObservationValue.FromInt64(long.MinValue);
        expectedFields["null"] = ObservationValue.Null;
        expectedFields["object"] = ObservationValue.FromObject(
            ImmutableSortedDictionary<string, ObservationValue>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("child", ObservationValue.FromInt64(17)));
        expectedFields["string"] = ObservationValue.FromString("portable");
        expectedFields["timeOnly"] = ObservationValue.FromTimeOnly(new TimeOnly(23, 59, 58, 123));
        expectedFields["timeSpan"] = ObservationValue.FromTimeSpan(TimeSpan.FromDays(12.5));
        expectedFields["undefined"] = ObservationValue.Undefined;

        var contract = new ValueContract(new JsonTypeRef(JsonTypeKind.Object));
        var original = PortableValue.Concrete(contract, ObservationValue.FromObject(expectedFields.ToImmutable()));

        var json = JsonSerializer.Serialize(original, WebJsonOptions);
        var rehydrated = JsonSerializer.Deserialize<PortableValue>(json, WebJsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(original, rehydrated);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, WebJsonOptions));

        var actualFields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ObservationValue>>(
            rehydrated.Value!.Value.Fields);
        foreach (var expected in expectedFields)
        {
            var actual = actualFields[expected.Key];
            Assert.Equal(expected.Value.Kind, actual.Kind);
        }
        Assert.Equal(
            [ObservationValueKind.Undefined, ObservationValueKind.Null, ObservationValueKind.String],
            actualFields["array"].Array.Select(static value => value.Kind));
        Assert.Equal(
            expectedFields["bytes"].Bytes.ToArray(),
            actualFields["bytes"].Bytes.ToArray());
        Assert.Equal(expectedFields["decimal"].Decimal, actualFields["decimal"].Decimal);
        Assert.Equal(expectedFields["dateTimeOffset"].String, actualFields["dateTimeOffset"].String);
    }

    [Fact]
    public void TaggedJson_RejectsUnknownAndDuplicatePortableValueProperties()
    {
        var json = JsonSerializer.Serialize(PortableValue.Missing(OptionalNullableString), WebJsonOptions);
        var unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unexpected"] = true;
        var duplicate = json.Insert(
            startIndex: json.LastIndexOf('}'),
            value: ",\"state\":\"missing\"");

        var unknownException = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PortableValue>(unknown.ToJsonString(), WebJsonOptions));
        var duplicateException = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PortableValue>(duplicate, WebJsonOptions));

        Assert.Contains(
            "Unknown portable value property 'unexpected'",
            unknownException.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "property 'state' is declared more than once",
            duplicateException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TaggedJson_RejectsUnknownAndDuplicatePropertiesAtEveryObservationDepth()
    {
        var json = SerializeNestedObservation(ObservationValue.FromString("ready"));
        var unknown = JsonNode.Parse(json)!.AsObject();
        GetNestedObservation(unknown)["unexpected"] = true;
        var duplicate = json.Replace(
            oldValue: "\"$kind\":\"string\"",
            newValue: "\"$kind\":\"string\",\"$kind\":\"string\"",
            comparisonType: StringComparison.Ordinal);

        var unknownException = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PortableValue>(unknown.ToJsonString(), WebJsonOptions));
        var duplicateException = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PortableValue>(duplicate, WebJsonOptions));

        Assert.Contains(
            "Unknown tagged observation property 'unexpected'",
            unknownException.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "property '$kind' is declared more than once",
            duplicateException.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ObservationValueKind.Undefined)]
    [InlineData(ObservationValueKind.Null)]
    public void TaggedJson_RejectsValuePayloadForValuelessObservationKinds(ObservationValueKind kind)
    {
        var observation = kind == ObservationValueKind.Undefined
            ? ObservationValue.Undefined
            : ObservationValue.Null;
        var json = JsonNode.Parse(SerializeNestedObservation(observation))!.AsObject();
        GetNestedObservation(json)["$value"] = 42;

        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PortableValue>(json.ToJsonString(), WebJsonOptions));

        Assert.Contains("cannot contain '$value'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("absent")]
    public void Validator_RejectsMissingAndAbsentForRequiredContracts(string state)
    {
        var required = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var value = state == "missing"
            ? PortableValue.Missing(required)
            : PortableValue.Absent(required);

        var result = PortableExecutionValidator.Validate(value);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.PresenceMismatch);
    }

    [Fact]
    public void Validator_RejectsNullForNonNullableContracts()
    {
        var value = PortableValue.Null(new ValueContract(new ScalarTypeRef(ScalarTypeKind.String)));

        var result = PortableExecutionValidator.Validate(value);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.NullabilityMismatch);
    }

    [Fact]
    public void Validator_RecursivelyRejectsUndefinedAndNonFiniteConcreteValues()
    {
        var nested = ObservationValue.FromObject(
            ImmutableSortedDictionary<string, ObservationValue>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(
                    "items",
                    ObservationValue.FromImmutableArray(
                    [
                        ObservationValue.Undefined,
                        ObservationValue.FromDouble(double.NaN),
                        ObservationValue.FromDouble(double.PositiveInfinity)
                    ])));
        var value = PortableValue.Concrete(
            new ValueContract(new JsonTypeRef(JsonTypeKind.Object)),
            nested);

        var result = PortableExecutionValidator.Validate(value);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.UndefinedObservation);
        Assert.Equal(
            2,
            result.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == PortableExecutionDiagnosticCodes.NonFiniteNumber));
    }

    [Fact]
    public void Validator_RejectsOpaqueRuntimeTypesAtAnyDepth()
    {
        TypeRef type = new ObjectTypeRef(
        [
            new ObjectFieldTypeDef(
                "items",
                new ArrayTypeRef(new OpaqueRuntimeTypeRef(typeof(string).AssemblyQualifiedName!)))
        ]);

        var result = PortableExecutionValidator.Validate(type);

        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.OpaqueRuntimeType);
        Assert.Equal("/fields/0/type/elementType", diagnostic.Location);
    }

    [Fact]
    public void Validator_FailsClosedForUnrecognizedTypeSubclasses()
    {
        var result = PortableExecutionValidator.Validate(new UnsupportedTypeRef());

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PortableExecutionDiagnosticCodes.UnsupportedType, diagnostic.Code);
    }

    [Fact]
    public void Validator_RejectsIncompatibleConcreteValues()
    {
        var value = PortableValue.Concrete(
            new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)),
            ObservationValue.FromString("true"));

        var result = PortableExecutionValidator.Validate(value);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
    }

    [Fact]
    public void Validator_RecursivelyChecksEachNamedStructuralValue()
    {
        TypeId nodeTypeId = new("execution/node");
        var nodeType = new TypeDefinition.Structural(
            nodeTypeId,
            [
                new StructuralField(
                    new("next"),
                    new NamedTypeRef(nodeTypeId),
                    presence: FieldPresence.Optional)
            ]);
        var graph = new ShapeGraph(new("execution/recursive-structure"), [], [nodeType]);
        var contract = new ValueContract(new NamedTypeRef(nodeTypeId));
        var invalid = PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["next"] = ObservationValue.FromString("not-a-node")
            }));
        var terminal = ObservationValue.FromObject(new Dictionary<string, ObservationValue>());
        var nested = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["next"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["next"] = terminal
            })
        });

        var invalidResult = PortableExecutionValidator.Validate(invalid, graph);
        var validResult = PortableExecutionValidator.Validate(
            PortableValue.Concrete(contract, nested),
            graph);

        Assert.Contains(
            invalidResult.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
        Assert.True(validResult.IsValid);
    }

    [Fact]
    public void Validator_FailsClosedWhenRecursiveNamedMatchingMakesNoValueProgress()
    {
        TypeId loopTypeId = new("execution/loop");
        var loopType = new TypeDefinition.Union(
            loopTypeId,
            new UnionDiscriminator("kind"),
            [new UnionCase("Loop", new NamedTypeRef(loopTypeId), "loop")]);
        var graph = new ShapeGraph(new("execution/recursive-union"), [], [loopType]);
        var value = PortableValue.Concrete(
            new ValueContract(new NamedTypeRef(loopTypeId)),
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["kind"] = ObservationValue.FromString("loop")
            }));

        var result = PortableExecutionValidator.Validate(value, graph);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
    }

    [Fact]
    public void ObjectFieldMetadata_RoundTripsAndControlsPortableCompatibility()
    {
        var contract = new ValueContract(new ObjectTypeRef(
        [
            new ObjectFieldTypeDef(
                name: "tags",
                type: new ScalarTypeRef(ScalarTypeKind.String),
                cardinality: FieldCardinality.Many,
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable)
        ]));
        var roundTrip = RoundTrip(PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["tags"] = ObservationValue.FromArray(
                [
                    ObservationValue.FromString("durable"),
                    ObservationValue.FromString("portable")
                ])
            })));

        Assert.True(PortableExecutionValidator.Validate(roundTrip).IsValid);
        var field = Assert.Single(Assert.IsType<ObjectTypeRef>(roundTrip.Contract.Type).Fields);
        Assert.Equal(FieldCardinality.Many, field.Cardinality);
        Assert.Equal(FieldPresence.Optional, field.Presence);
        Assert.Equal(FieldNullability.Nullable, field.Nullability);

        var nullable = PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["tags"] = ObservationValue.Null
            }));
        var missing = PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>()));
        var incompatible = PortableValue.Concrete(
            contract,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["tags"] = ObservationValue.FromArray([ObservationValue.FromInt64(1)])
            }));

        Assert.True(PortableExecutionValidator.Validate(nullable).IsValid);
        Assert.True(PortableExecutionValidator.Validate(missing).IsValid);
        Assert.Contains(
            PortableExecutionValidator.Validate(incompatible).Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.ConcreteTypeMismatch);
    }

    [Fact]
    public void Validator_RejectsUnrecognizedExpressionSubclasses()
    {
        var result = PortableExecutionValidator.Validate(new UnsupportedExpr());

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PortableExecutionDiagnosticCodes.UnsupportedExpression, diagnostic.Code);
    }

    [Fact]
    public void Validator_RecursivelyValidatesExpressionTypeReferences()
    {
        Expr expression = new CallExpr(
            "runtimeOnly",
            [Expr.Const("input")],
            new OpaqueRuntimeTypeRef("System.String, Runtime.Assembly"));

        var result = PortableExecutionValidator.Validate(expression);

        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.OpaqueRuntimeType);
    }

    static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    static string SerializeNestedObservation(ObservationValue observation) =>
        JsonSerializer.Serialize(
            PortableValue.Concrete(
                new ValueContract(new JsonTypeRef(JsonTypeKind.Object)),
                ObservationValue.FromObject(
                    ImmutableSortedDictionary<string, ObservationValue>.Empty
                        .WithComparers(StringComparer.Ordinal)
                        .Add("nested", observation))),
            WebJsonOptions);

    static JsonObject GetNestedObservation(JsonObject portableValue) =>
        portableValue["value"]!["$value"]!["nested"]!.AsObject();

    static PortableValue RoundTrip(PortableValue value)
    {
        var json = JsonSerializer.Serialize(value, WebJsonOptions);
        return JsonSerializer.Deserialize<PortableValue>(json, WebJsonOptions)!;
    }

    sealed record UnsupportedExpr : Expr;

    sealed record UnsupportedTypeRef : TypeRef;
}
