using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationValueTests
{
    [Fact]
    public void FromObject_DictionaryWithNonStringKeys_Throws()
    {
        IDictionary values = new Dictionary<int, string> { [1] = "x" };

        var ex = Assert.Throws<InvalidOperationException>(() => ObservationValue.FromObject(values));

        Assert.Contains("keys must be strings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObject_CyclicClrObjectGraph_Throws()
    {
        var node = new RecursiveNode();
        node.Next = node;

        var ex = Assert.Throws<InvalidOperationException>(() => ObservationValue.FromObject(node));

        Assert.Contains("Cyclic CLR object graphs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObject_ClrObject_HonorsJsonPropertyNameAndJsonIgnore()
    {
        var observed = ObservationValue.FromObject(new AttributedInput
        {
            Value = 7,
            Hidden = 9
        });

        Assert.Equal(ObservationValueKind.Object, observed.Kind);
        Assert.Equal(7L, observed.GetProperty("renamed").GetInt64());
        Assert.False(observed.TryGetProperty("Hidden", out _));
    }

    [Fact]
    public void FromObject_Enumerable_ProducesArray()
    {
        var observed = ObservationValue.FromObject(new[] { 2, 4, 6 });

        Assert.Equal(ObservationValueKind.Array, observed.Kind);
        Assert.Equal(3, observed.GetArrayLength());
        Assert.Equal(4, observed.EnumerateArray()[1].GetInt32());
    }

    [Fact]
    public void FromObject_Single_UsesItsCanonicalSinglePrecisionDecimal()
    {
        var direct = ObservationValue.FromObject(0.98f);
        var nested = ObservationValue.FromObject(new NumericInput
        {
            Score = 0.98f
        }).GetProperty(nameof(NumericInput.Score));
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Decimal));

        Assert.Equal(ObservationValueKind.Decimal, direct.Kind);
        Assert.Equal(0.98m, direct.GetDecimal());
        Assert.Equal(direct, nested);
        Assert.True(contract.IsSatisfiedByConstant(direct));
    }

    [Fact]
    public void FromObject_DoubleAndDecimal_RetainTheirExplicitNumericKinds()
    {
        var floatingPoint = ObservationValue.FromObject(0.98d);
        var exactDecimal = ObservationValue.FromObject(0.98m);
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Decimal));

        Assert.Equal(ObservationValueKind.Double, floatingPoint.Kind);
        Assert.Equal(ObservationValueKind.Decimal, exactDecimal.Kind);
        Assert.True(contract.IsSatisfiedByConstant(floatingPoint));
        Assert.True(contract.IsSatisfiedByConstant(exactDecimal));
    }

    [Fact]
    public void FromObject_JsonDocument_ProjectsRootElement()
    {
        using var document = JsonDocument.Parse("{\"x\":12,\"nested\":{\"ok\":true}}");

        var observed = ObservationValue.FromObject(document);

        Assert.Equal(12L, observed.GetProperty("x").GetInt64());
        Assert.True(observed.GetProperty("nested").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void FromClrPropertyBag_Null_ReturnsEmptyObject()
    {
        var observed = ObservationValue.FromClrPropertyBag(null);

        Assert.Equal(ObservationValueKind.Object, observed.Kind);
        Assert.NotNull(observed.Fields);
        Assert.Empty(observed.Fields!);
    }

    [Fact]
    public void ToFieldDictionary_UsesClrPropertyNames()
    {
        var values = ObservationValue.ToFieldDictionary(new SimpleInput
        {
            Value = 5,
            Name = "carrier"
        });

        Assert.Equal(2, values.Count);
        Assert.Equal(5L, values["Value"].GetInt64());
        Assert.Equal("carrier", values["Name"].GetString());
    }

    [Fact]
    public void TryGetField_ResolvesNestedObjectPath()
    {
        var observed = ObservationValue.FromObject(new
        {
            Customer = new { Name = "Contoso" }
        });

        Assert.True(observed.TryGetField(FieldPath.Parse("Customer.Name"), out var name));
        Assert.Equal(ObservationValue.FromString("Contoso"), name);
        Assert.False(observed.TryGetField(FieldPath.Parse("Customer.Type"), out _));
        Assert.False(observed.TryGetField(default, out _));
    }

    [Fact]
    public void TryGetField_UsesOrdinalNamesIndependentOfDictionaryComparer()
    {
        var customer = new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = ObservationValue.FromString("Contoso")
        };
        var root = new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer"] = ObservationValue.FromObject(customer)
        };
        var observed = ObservationValue.FromObject(root);

        Assert.True(observed.TryGetField(FieldPath.Parse("Customer.Name"), out var name));
        Assert.Equal(ObservationValue.FromString("Contoso"), name);
        Assert.False(observed.TryGetField(FieldPath.Parse("customer.Name"), out _));
        Assert.False(observed.TryGetField(FieldPath.Parse("Customer.name"), out _));
        Assert.True(observed.TryGetProperty("Customer", out _));
        Assert.False(observed.TryGetProperty("customer", out _));
    }

    [Fact]
    public void TryGetProperty_OrdinalLookupDoesNotAllocate()
    {
        const int iterations = 10_000;
        var observed = ObservationValue.FromObject(
            new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Value"] = ObservationValue.FromInt64(42)
            });
        long checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            if (observed.TryGetProperty("Value", out var warmup))
                checksum += warmup.GetInt64();
        }

        _ = GC.GetAllocatedBytesForCurrentThread();
        var allocated = MeasureOrdinalLookupAllocations(observed, iterations, out var measuredChecksum);
        checksum += measuredChecksum;

        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long MeasureOrdinalLookupAllocations(
        ObservationValue observed,
        int iterations,
        out long checksum)
    {
        checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            if (observed.TryGetProperty("Value", out var value))
                checksum += value.GetInt64();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void PublicConstructor_SnapshotsCallerOwnedObjectArrayAndByteStorage()
    {
        var fields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Name"] = ObservationValue.FromString("Contoso")
        };
        ObservationValue[] items = [ObservationValue.FromString("original")];
        byte[] bytes = [1, 2, 3];
        var objectValue = new ObservationValue(ObservationValueKind.Object, fields: fields);
        var arrayValue = new ObservationValue(ObservationValueKind.Array, array: items);
        var bytesValue = new ObservationValue(ObservationValueKind.Bytes, bytes: bytes);

        fields["Name"] = ObservationValue.FromString("Fabrikam");
        fields["Added"] = ObservationValue.FromBool(true);
        items[0] = ObservationValue.FromString("changed");
        bytes[0] = 9;

        Assert.Equal("Contoso", objectValue.GetProperty("Name").GetString());
        Assert.False(objectValue.TryGetProperty("Added", out _));
        Assert.Equal("original", arrayValue.EnumerateArray()[0].GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, bytesValue.Bytes.ToArray());

        var returnedFields = Assert.IsAssignableFrom<IDictionary<string, ObservationValue>>(objectValue.Fields);
        Assert.Throws<NotSupportedException>(() =>
            returnedFields["Name"] = ObservationValue.FromString("mutated through Fields"));
        IList<ObservationValue> returnedArray = arrayValue.Array;
        Assert.Throws<NotSupportedException>(() =>
            returnedArray[0] = ObservationValue.FromString("mutated through Array"));

        Assert.Equal("Contoso", objectValue.GetProperty("Name").GetString());
        Assert.Equal("original", arrayValue.Array[0].GetString());
        Assert.Equal("original", arrayValue.EnumerateArray()[0].GetString());
    }

    [Fact]
    public void CollectionFactories_SnapshotCallerOwnedObjectArrayAndByteStorage()
    {
        var fields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Name"] = ObservationValue.FromString("Contoso")
        };
        ObservationValue[] items = [ObservationValue.FromInt64(1)];
        byte[] bytes = [4, 5, 6];
        var objectValue = ObservationValue.FromObject(fields);
        var arrayValue = ObservationValue.FromArray(items);
        var bytesValue = ObservationValue.FromBytes(bytes);

        fields.Clear();
        items[0] = ObservationValue.FromInt64(2);
        bytes[2] = 9;

        Assert.Equal("Contoso", objectValue.GetProperty("Name").GetString());
        Assert.Equal(1L, arrayValue.EnumerateArray()[0].GetInt64());
        Assert.Equal(new byte[] { 4, 5, 6 }, bytesValue.Bytes.ToArray());
    }

    [Fact]
    public void ImmutableCollectionInputs_RetainTheirOwnedStorage()
    {
        var arrayStorage = new[] { ObservationValue.FromString("retained") };
        var immutableItems = ImmutableCollectionsMarshal.AsImmutableArray(arrayStorage);
        var immutableFields = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Name"] = ObservationValue.FromString("Contoso")
            });

        var arrayValue = ObservationValue.FromImmutableArray(immutableItems);
        var objectValue = ObservationValue.FromObject(immutableFields);

        Assert.Same(
            arrayStorage,
            ImmutableCollectionsMarshal.AsArray(arrayValue.Array));
        Assert.Same(immutableFields, objectValue.Fields);
        Assert.Equal("retained", arrayValue.Array[0].GetString());
        Assert.Equal("Contoso", objectValue.GetProperty("Name").GetString());
    }

    [Fact]
    public void FromImmutableArray_DefaultStorage_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ObservationValue.FromImmutableArray(default));

        Assert.Equal("values", exception.ParamName);
    }

    [Fact]
    public void ImmutableObjectInput_WithNonOrdinalKeys_IsNormalizedBySnapshot()
    {
        var fields = ImmutableDictionary.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            new Dictionary<string, ObservationValue>
            {
                ["Name"] = ObservationValue.FromString("Contoso")
            });

        var value = ObservationValue.FromObject(fields);

        Assert.NotSame(fields, value.Fields);
        Assert.True(value.TryGetProperty("Name", out _));
        Assert.False(value.TryGetProperty("name", out _));
    }

    [Fact]
    public void JsonObjectProjection_UsesDeterministicOrdinalPropertyOrder()
    {
        using var document = JsonDocument.Parse("""{"z":1,"a":2,"m":3}""");

        var value = ObservationValue.FromJsonElement(document.RootElement);

        Assert.IsType<ImmutableSortedDictionary<string, ObservationValue>>(value.Fields);
        Assert.Equal("""{"a":2,"m":3,"z":1}""", value.GetRawText());
    }

    [Fact]
    public void NestedRequestValue_SnapshotsEveryCallerOwnedCollectionBoundary()
    {
        var predicateFields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Status"] = ObservationValue.FromString("active")
        };
        var predicate = new ObservationValue(ObservationValueKind.Object, fields: predicateFields);
        ObservationValue[] predicates = [predicate];
        byte[] continuation = [7, 8, 9];
        var requestFields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Predicates"] = new(ObservationValueKind.Array, array: predicates),
            ["Continuation"] = new(ObservationValueKind.Bytes, bytes: continuation)
        };
        var requestValue = new ObservationValue(ObservationValueKind.Object, fields: requestFields);

        predicateFields["Status"] = ObservationValue.FromString("inactive");
        predicates[0] = ObservationValue.Null;
        continuation[0] = 0;
        requestFields.Clear();

        var retainedPredicate = requestValue
            .GetProperty("Predicates")
            .EnumerateArray()[0];
        Assert.Equal("active", retainedPredicate.GetProperty("Status").GetString());
        Assert.Equal(
            new byte[] { 7, 8, 9 },
            requestValue.GetProperty("Continuation").Bytes.ToArray());
    }

    [Fact]
    public void CallerMutation_DoesNotChangeHashOrHashSetMembership()
    {
        ObservationValue[] items = [ObservationValue.FromString("load-1")];
        byte[] bytes = [1, 2, 3];
        var fields = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Items"] = ObservationValue.FromArray(items),
            ["Payload"] = ObservationValue.FromBytes(bytes)
        };
        var value = ObservationValue.FromObject(fields);
        var originalHash = value.GetHashCode();
        HashSet<ObservationValue> values = [value];

        items[0] = ObservationValue.FromString("load-2");
        bytes[0] = 9;
        fields.Clear();

        Assert.Equal(originalHash, value.GetHashCode());
        Assert.Contains(value, values);
        Assert.Equal("load-1", value.GetProperty("Items").Array[0].GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, value.GetProperty("Payload").Bytes.ToArray());
    }

    [Fact]
    public void WithField_AndWithoutField_ImmutablyShapeNestedObjects()
    {
        var original = ObservationValue.EmptyObject
            .WithField(FieldPath.Parse("Customer.Type"), ObservationValue.FromString("Preferred"))
            .WithField(FieldPath.Parse("Customer.Name"), ObservationValue.FromString("Contoso"));

        var updated = original
            .WithField(FieldPath.Parse("Customer.Name"), ObservationValue.FromString("Fabrikam"))
            .WithoutField(FieldPath.Parse("Customer.Type"));

        Assert.Equal("Contoso", original.GetProperty("Customer").GetProperty("Name").GetString());
        Assert.Equal("Preferred", original.GetProperty("Customer").GetProperty("Type").GetString());
        Assert.Equal("Fabrikam", updated.GetProperty("Customer").GetProperty("Name").GetString());
        Assert.False(updated.GetProperty("Customer").TryGetProperty("Type", out _));
        Assert.Equal(["Name"], updated.GetProperty("Customer").Fields!.Keys);
    }

    [Fact]
    public void WithField_InvalidTraversal_FailsPrecisely()
    {
        var scalarParent = ObservationValue.EmptyObject.WithField(
            FieldPath.FromField("Customer"),
            ObservationValue.FromString("Contoso"));

        Assert.Throws<InvalidOperationException>(() => scalarParent.WithField(
            FieldPath.Parse("Customer.Name"),
            ObservationValue.FromString("Fabrikam")));
        Assert.Throws<NotSupportedException>(() => ObservationValue.EmptyObject.WithField(
            new([FieldPathSegment.Element(), FieldPathSegment.ForField("Name")]),
            ObservationValue.FromString("Fabrikam")));
    }

    [Fact]
    public void TryGetInt64_DoubleIntegralSucceeds_AndFractionalFails()
    {
        var integral = ObservationValue.FromDouble(42d);
        var fractional = ObservationValue.FromDouble(42.5d);

        Assert.True(integral.TryGetInt64(out var parsed));
        Assert.Equal(42L, parsed);
        Assert.False(fractional.TryGetInt64(out _));
    }

    [Fact]
    public void TryGetDecimal_FromString_UsesInvariantParsing()
    {
        var observed = ObservationValue.FromString("12.75");

        Assert.True(observed.TryGetDecimal(out var value));
        Assert.Equal(12.75m, value);
        Assert.Equal(12.75m, observed.GetDecimal());
    }

    [Fact]
    public void FromDecimal_HighPrecisionValue_RoundTripsWithoutDoubleConversion()
    {
        const decimal expected = 12345678901234567890.123456789m;

        var observed = ObservationValue.FromDecimal(expected);
        var json = JsonSerializer.Serialize(observed);
        var roundTripped = JsonSerializer.Deserialize<ObservationValue>(json);

        Assert.Equal(ObservationValueKind.Decimal, observed.Kind);
        Assert.Equal(expected, observed.Decimal);
        Assert.Equal(expected, observed.GetDecimal());
        Assert.Equal("12345678901234567890.123456789", json);
        Assert.Equal(observed, roundTripped);
        Assert.Equal(expected, roundTripped.Decimal);
    }

    [Fact]
    public void FromDecimal_IntegralInt64Value_PreservesIntegerNormalization()
    {
        var observed = ObservationValue.FromDecimal(42.000m);

        Assert.Equal(ObservationValueKind.Int64, observed.Kind);
        Assert.Equal(42L, observed.Int64);
        Assert.Equal("42", observed.GetRawText());
    }

    [Fact]
    public void FromJsonElement_HighPrecisionNumber_UsesDecimalCarrier()
    {
        const decimal expected = 12345678901234567890.123456789m;
        using var document = JsonDocument.Parse("12345678901234567890.123456789");

        var observed = ObservationValue.FromJsonElement(document.RootElement);

        Assert.Equal(ObservationValueKind.Decimal, observed.Kind);
        Assert.Equal(expected, observed.Decimal);
        Assert.Equal(document.RootElement.GetRawText(), observed.GetRawText());
    }

    [Fact]
    public void FromJsonElement_NumberOutsideDecimalScale_FallsBackToDouble()
    {
        using var document = JsonDocument.Parse("1e-29");

        var observed = ObservationValue.FromJsonElement(document.RootElement);

        Assert.Equal(ObservationValueKind.Double, observed.Kind);
        Assert.Equal(1e-29d, observed.Double);
    }

    [Fact]
    public void FromJsonNode_PreservesTypedDoubleAndDecimalCarriers()
    {
        var floatingPoint = ObservationValue.FromJsonNode(JsonValue.Create(0.1d));
        var exact = ObservationValue.FromJsonNode(JsonValue.Create(0.1m));

        Assert.Equal(ObservationValueKind.Double, floatingPoint.Kind);
        Assert.Equal(0.1d, floatingPoint.Double);
        Assert.Equal(ObservationValueKind.Decimal, exact.Kind);
        Assert.Equal(0.1m, exact.Decimal);
    }

    [Fact]
    public void JsonRoundTrip_UntaggedDoubleNumber_PreservesSemanticEquality()
    {
        var original = ObservationValue.FromDouble(0.10000000000000002d);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ObservationValue>(json);

        Assert.Equal(ObservationValueKind.Decimal, roundTripped.Kind);
        Assert.Equal(original, roundTripped);
        Assert.Equal(original.GetHashCode(), roundTripped.GetHashCode());
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
    }

    [Fact]
    public void JsonRoundTrip_LargeIntegralDouble_UsesOneCanonicalNumericValue()
    {
        var original = ObservationValue.FromDouble(Math.BitIncrement(1e18));

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ObservationValue>(json);

        Assert.Equal("1E+18", 1e18.ToString("R", CultureInfo.InvariantCulture));
        Assert.Equal("1.0000000000000001E+18", json);
        Assert.Equal(original, roundTripped);
        Assert.Equal(original.GetHashCode(), roundTripped.GetHashCode());
        Assert.True(original.TryGetInt64(out var originalInteger));
        Assert.True(roundTripped.TryGetInt64(out var roundTrippedInteger));
        Assert.Equal(roundTrippedInteger, originalInteger);
    }

    [Fact]
    public void DecimalEquality_IsExactAndProducesCompatibleHashes()
    {
        var left = ObservationValue.FromDecimal(12345678901234567890.123456789m);
        var equal = ObservationValue.FromDecimal(12345678901234567890.123456789m);
        var different = ObservationValue.FromDecimal(12345678901234567890.123456788m);
        var exactDouble = ObservationValue.FromDouble(0.125d);
        var exactDecimal = ObservationValue.FromDecimal(0.125m);

        Assert.Equal(left, equal);
        Assert.Equal(left.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(left, different);
        Assert.Equal(exactDecimal, exactDouble);
        Assert.Equal(exactDecimal.GetHashCode(), exactDouble.GetHashCode());
        var canonicalDecimal = ObservationValue.FromDecimal(0.1m);
        var canonicalDouble = ObservationValue.FromDouble(0.1d);
        Assert.Equal(canonicalDecimal, canonicalDouble);
        Assert.Equal(canonicalDecimal.GetHashCode(), canonicalDouble.GetHashCode());
    }

    [Fact]
    public void ExprDecimalConstant_PreservesExactCarrier()
    {
        const decimal expected = 12345678901234567890.123456789m;

        var constant = Assert.IsType<ConstantExpr>(Expr.Const(expected));

        Assert.Equal(ObservationValueKind.Decimal, constant.Value.Kind);
        Assert.Equal(expected, constant.Value.Decimal);
    }

    [Fact]
    public void TryGetBoolean_FromString_ParsesTrueFalse()
    {
        var observed = ObservationValue.FromString("true");

        Assert.True(observed.TryGetBoolean(out var value));
        Assert.True(value);
    }

    [Fact]
    public void FromObject_ObjectExpression()
    {
        var observedObj = ObservationValue.FromObject(new { Field1 = "hello", Field2 = 42, Field3 = new[] { 1, 2, 3 }, Field4 = new[] { new { Nested = 1 }, new { Nested = 2 } } });
        
        Assert.Equal("hello", observedObj.GetProperty("Field1").GetString());
        Assert.Equal(42, observedObj.GetProperty("Field2").GetInt32());
        Assert.Equal(3, observedObj.GetProperty("Field3").GetArrayLength());
        Assert.Equal(1, observedObj.GetProperty("Field4").EnumerateArray()[0].GetProperty("Nested").GetInt32());
        Assert.Equal(2, observedObj.GetProperty("Field4").EnumerateArray()[1].GetProperty("Nested").GetInt32());
    }
    
    [Fact]
    public void FromObject_TemporalClrValues_UseTemporalKinds()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromMinutes(95);

        var observedDto = ObservationValue.FromObject(dto);
        var observedDateOnly = ObservationValue.FromObject(dateOnly);
        var observedTimeOnly = ObservationValue.FromObject(timeOnly);
        var observedTimeSpan = ObservationValue.FromObject(timeSpan);

        Assert.Equal(ObservationValueKind.DateTimeOffset, observedDto.Kind);
        Assert.Equal(ObservationValueKind.DateOnly, observedDateOnly.Kind);
        Assert.Equal(ObservationValueKind.TimeOnly, observedTimeOnly.Kind);
        Assert.Equal(ObservationValueKind.TimeSpan, observedTimeSpan.Kind);
    }

    [Fact]
    public void TemporalKinds_TryGetAndGet_RoundTrip()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(7);

        var observedDto = ObservationValue.FromDateTimeOffset(dto);
        var observedDateOnly = ObservationValue.FromDateOnly(dateOnly);
        var observedTimeOnly = ObservationValue.FromTimeOnly(timeOnly);
        var observedTimeSpan = ObservationValue.FromTimeSpan(timeSpan);

        Assert.True(observedDto.TryGetDateTimeOffset(out var parsedDto));
        Assert.Equal(dto, parsedDto);
        Assert.Equal(dto, observedDto.GetDateTimeOffset());

        Assert.True(observedDateOnly.TryGetDateOnly(out var parsedDateOnly));
        Assert.Equal(dateOnly, parsedDateOnly);
        Assert.Equal(dateOnly, observedDateOnly.GetDateOnly());

        Assert.True(observedTimeOnly.TryGetTimeOnly(out var parsedTimeOnly));
        Assert.Equal(timeOnly, parsedTimeOnly);
        Assert.Equal(timeOnly, observedTimeOnly.GetTimeOnly());

        Assert.True(observedTimeSpan.TryGetTimeSpan(out var parsedTimeSpan));
        Assert.Equal(timeSpan, parsedTimeSpan);
        Assert.Equal(timeSpan, observedTimeSpan.GetTimeSpan());
    }

    [Fact]
    public void TemporalReaders_ParseFromStringKind()
    {
        var dtoText = "2026-02-20T14:30:15.0000000-05:00";
        var dateOnlyText = "2026-02-20";
        var timeOnlyText = "14:30:15.1230000";
        var timeSpanText = "02:05:07";

        Assert.True(ObservationValue.FromString(dtoText).TryGetDateTimeOffset(out _));
        Assert.True(ObservationValue.FromString(dateOnlyText).TryGetDateOnly(out _));
        Assert.True(ObservationValue.FromString(timeOnlyText).TryGetTimeOnly(out _));
        Assert.True(ObservationValue.FromString(timeSpanText).TryGetTimeSpan(out _));
    }

    [Theory]
    [InlineData("2026-07-17T12:34:56Z", true)]
    [InlineData("2026-07-17T12:34:56z", true)]
    [InlineData("2026-07-17T12:34:56+02:30", true)]
    [InlineData("2026-07-17T12:34:56-07:00", true)]
    [InlineData("2026-07-17T12:34:56", false)]
    [InlineData("2026-07-17 12:34:56", false)]
    public void TryGetInstant_RequiresAnExplicitOffsetForStringValues(string text, bool expected)
    {
        var observed = ObservationValue.FromString(text);

        Assert.Equal(expected, observed.TryGetInstant(out _));
    }

    [Fact]
    public void TryGetInstant_AcceptsValidDedicatedDateTimeOffsetAndRejectsMalformedPayload()
    {
        var instant = new DateTimeOffset(2026, 7, 17, 12, 34, 56, TimeSpan.FromHours(-7));

        Assert.True(ObservationValue.FromDateTimeOffset(instant).TryGetInstant(out var parsed));
        Assert.Equal(instant, parsed);
        Assert.False(new ObservationValue(
            ObservationValueKind.DateTimeOffset,
            s: "not-an-instant").TryGetInstant(out _));
    }

    [Fact]
    public void DateTimeOffsetReader_RetainsCivilOffsetlessStringBehavior()
    {
        var observed = ObservationValue.FromString("2026-07-17T12:34:56");

        Assert.True(observed.TryGetDateTimeOffset(out _));
        Assert.False(observed.TryGetInstant(out _));
    }

    [Fact]
    public void GetString_TemporalKinds_ReturnsCanonicalText()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromMinutes(95);

        Assert.Equal(dto.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromDateTimeOffset(dto).GetString());
        Assert.Equal(dateOnly.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromDateOnly(dateOnly).GetString());
        Assert.Equal(timeOnly.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromTimeOnly(timeOnly).GetString());
        Assert.Equal(timeSpan.ToString("c", CultureInfo.InvariantCulture), ObservationValue.FromTimeSpan(timeSpan).GetString());
    }

    [Fact]
    public void WriteTo_TemporalKinds_WritesJsonStrings()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["dto"] = ObservationValue.FromDateTimeOffset(dto),
            ["date"] = ObservationValue.FromDateOnly(new DateOnly(2026, 2, 20)),
            ["time"] = ObservationValue.FromTimeOnly(new TimeOnly(14, 30, 15, 123)),
            ["span"] = ObservationValue.FromTimeSpan(TimeSpan.FromMinutes(95))
        });

        var json = observed.GetRawText();

        Assert.Contains("\"dto\":\"2026-02-20T14:30:15.0000000-05:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"date\":\"2026-02-20\"", json, StringComparison.Ordinal);
        Assert.Contains("\"time\":\"14:30:15.1230000\"", json, StringComparison.Ordinal);
        Assert.Contains("\"span\":\"01:35:00\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepEquals_TemporalKind_NotEqualToPlainString()
    {
        var text = "2026-02-20";
        var temporal = ObservationValue.FromDateOnly(new DateOnly(2026, 2, 20));
        var plain = ObservationValue.FromString(text);

        Assert.False(ObservationValue.DeepEquals(temporal, plain));
        Assert.NotEqual(temporal.GetHashCode(), plain.GetHashCode());
    }

    [Fact]
    public void FromJsonElement_String_DoesNotInferTemporalKinds()
    {
        using var document = JsonDocument.Parse("\"2026-02-20\"");

        var observed = ObservationValue.FromJsonElement(document.RootElement);

        Assert.Equal(ObservationValueKind.String, observed.Kind);
        Assert.Equal("2026-02-20", observed.GetString());
    }

    [Fact]
    public void GetString_NonString_Throws()
    {
        var observed = ObservationValue.FromInt64(3);

        Assert.Throws<InvalidOperationException>(() => observed.GetString());
    }

    [Fact]
    public void TryGetProperty_WhitespaceName_Throws()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["a"] = ObservationValue.FromInt64(1)
        });

        Assert.Throws<ArgumentException>(() => observed.TryGetProperty(" ", out _));
    }

    [Fact]
    public void WriteTo_DefaultBytesPolicy_ThrowsForBytes()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        Assert.Throws<InvalidOperationException>(() => observed.WriteTo(writer));
    }

    [Fact]
    public void WriteTo_Base64BytesPolicy_WritesExpectedJson()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["id"] = ObservationValue.FromInt64(7),
            ["payload"] = ObservationValue.FromBytes(new byte[] { 0, 255 }),
            ["active"] = ObservationValue.FromBool(true)
        });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            observed.WriteTo(writer, ObservationBytesJsonEncoding.Base64String);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.Equal("{\"id\":7,\"payload\":\"AP8=\",\"active\":true}", json);
    }

    [Fact]
    public void Deserialize_Object_MapsToClrType()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Value"] = ObservationValue.FromInt64(11)
        });

        var model = observed.Deserialize<SimplePayload>();

        Assert.NotNull(model);
        Assert.Equal(11, model!.Value);
    }

    sealed class RecursiveNode
    {
        public RecursiveNode? Next { get; set; }
    }

    sealed class AttributedInput
    {
        [JsonPropertyName("renamed")]
        public int Value { get; init; }

        [JsonIgnore]
        public int Hidden { get; init; }
    }

    sealed class SimpleInput
    {
        public int Value { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    sealed class NumericInput
    {
        public float? Score { get; init; }
    }

    sealed class SimplePayload
    {
        public int Value { get; set; }
    }
}
