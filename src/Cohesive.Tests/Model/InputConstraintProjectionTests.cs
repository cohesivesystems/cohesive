using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class InputConstraintProjectionTests
{
    public sealed record TextInput([property: MaxLength(2)] string Value);
    public sealed record NestedInput(TextInput Input);
    public sealed record ConvertedInput([property: MaxLength(2)] ConvertedList Values);
    [JsonConverter(typeof(ConvertedListConverter))]
    public sealed class ConvertedList : List<string>;
    public sealed class ConvertedListConverter : JsonConverter<ConvertedList>
    {
        public override ConvertedList Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ConvertedList value, JsonSerializerOptions options) => writer.WriteStringValue("converted");
    }
    sealed class StringObjectConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) { writer.WriteStartObject(); writer.WriteString("value", value); writer.WriteEndObject(); }
    }
    sealed class NumberOverride : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) => context.Target == ClrShapeMetadataTarget.Field
            && context.ClrType == typeof(string) ? new() { TypeRef = new ScalarTypeRef(ScalarTypeKind.Int64) } : ClrShapeMetadata.Empty;
    }

    public sealed record RequestId(string Text);
    public sealed record ValueObjectInput([property: MaxLength(2)] RequestId Id);
    public sealed record NestedValueObjectInput(ValueObjectInput Input);
    sealed class ValueObjectProjection : IClrShapeMetadataProvider, IClrFieldProjectionValidator
    {
        public int Validated { get; private set; }
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) =>
            context.Target == ClrShapeMetadataTarget.Field && context.ClrType == typeof(RequestId)
                ? new() { TypeRef = new ScalarTypeRef(ScalarTypeKind.String) } : ClrShapeMetadata.Empty;
        public void ValidateFieldProjection(ClrFieldProjectionContext context)
        {
            if (context.Property.PropertyType != typeof(RequestId)) return;
            Assert.Equal(new ScalarTypeRef(ScalarTypeKind.String), context.Type);
            Assert.Contains(context.Metadata.Constraints, rule => rule is MaxLengthConstraint);
            Validated++;
        }
    }

    [Fact]
    public void FinalProjectionAdmitsValueObjectOverridesAndCallsCustomValidators()
    {
        var projection = new ValueObjectProjection();
        var builder = new ClrShapeGraphBuilder().AddMetadataProvider(projection).UseDataAnnotations()
            .AddShape<ValueObjectInput>().AddShape<NestedValueObjectInput>();
        var built = builder.BuildResult();
        var shape = built.GetShape<ValueObjectInput>();
        Assert.True(ObservationValidator.ValidateInput(ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["Id"] = ObservationValue.FromString("ok") }), shape).IsValid);
        Assert.False(ObservationValidator.ValidateInput(ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["Id"] = ObservationValue.FromString("long") }), shape).IsValid);
        Assert.True(projection.Validated >= 2);
    }

    [Fact]
    public void FinalMetadataOverridesAreCheckedForRootAndNestedFields()
    {
        Assert.Throws<NotSupportedException>(() => new ClrShapeGraphBuilder().UseDataAnnotations()
            .AddMetadataProvider(new NumberOverride()).AddShape<TextInput>().Build());
        Assert.Throws<NotSupportedException>(() => new ClrShapeGraphBuilder().UseDataAnnotations()
            .AddMetadataProvider(new NumberOverride()).AddShape<NestedInput>().Build());
    }

    [Fact]
    public void TypeAndConfiguredConvertersCannotSlipThroughDefaultJsonSettings()
    {
        Assert.Throws<NotSupportedException>(() => new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<ConvertedInput>().Build());
        var options = new JsonSerializerOptions(); options.Converters.Add(new StringObjectConverter());
        Assert.Throws<NotSupportedException>(() => new ClrShapeGraphBuilder().UsePublicJsonContracts(options)
            .UseDataAnnotations().AddShape<TextInput>().Build());
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => { if (info.Type == typeof(TextInput)) info.Properties[0].CustomConverter = new StringObjectConverter(); });
        Assert.Throws<NotSupportedException>(() => new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(new JsonSerializerOptions { TypeInfoResolver = resolver })
            .UseDataAnnotations().AddShape<TextInput>().Build());
    }

    [Fact]
    public void ConfiguredGeneratedMetadataIsUsedWithoutDefaultSerializerInference()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = LengthProjectionContext.Default };
        var built = new ClrShapeGraphBuilder().UsePublicJsonContracts(options).UseDataAnnotations().AddShape<TextInput>().BuildResult();
        Assert.True(ObservationValidator.ValidateInput(ObservationValue.FromObject(new TextInput("ok")), built.GetShape<TextInput>()).IsValid);
        Assert.False(ObservationValidator.ValidateInput(ObservationValue.FromObject(new TextInput("long")), built.GetShape<TextInput>()).IsValid);
    }

    [Theory]
    [InlineData("id", true)] [InlineData("ID", false)] [InlineData("missing", false)]
    public void DeclaredTargetNamesHaveTheSameOrdinalPolicyInEverySchema(string target, bool expected)
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        Shape shape = new(new("input"), [new(new("id"), scalar)]);
        TypeDefinition.Structural structural = new(new("record"), [new(new("id"), scalar)]);
        ObjectTypeRef obj = new([new("id", scalar)]);
        var path = FieldPath.FromField(target);
        Assert.Equal(expected, ConstraintSemantics.TryResolveTarget(path, out _, shape: shape));
        Assert.Equal(expected, ConstraintSemantics.TryResolveTarget(path, out _, structural: structural));
        Assert.Equal(expected, ConstraintSemantics.TryResolveTarget(path, out _, objectType: obj));
        if (expected) _ = new ValueContract(obj, constraints: [new MaxLengthConstraint(2, path)]);
        else Assert.Throws<NotSupportedException>(() => new ValueContract(obj, constraints: [new MaxLengthConstraint(2, path)]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOptionalConstraintTargetDoesNotAllocateOrCacheAbsentValues(bool owned)
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        var obj = new ObjectTypeRef([new("id", scalar, presence: FieldPresence.Optional)]);
        var contract = new ValueContract(obj, constraints: [new MaxLengthConstraint(2, FieldPath.FromField("id"))]);
        // Deliberately force a nonempty fallback; no cached negative lookup may hide newly present data.
        var fields = Enumerable.Range(0, 32).ToDictionary(i => "other" + i, _ => ObservationValue.FromString("value"));
        var value = ObservationValue.FromObject(fields);
        var source = owned ? value.Fields! : fields;
        for (var i = 0; i < 1000; i++) { ConstraintSemantics.TryGetField(source, "id", out _); contract.IsSatisfiedByConstant(value); }
        var minimum = long.MaxValue;
        for (var sample = 0; sample < 3; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var missing = true;
            for (var i = 0; i < 1000; i++)
            {
                missing &= !ConstraintSemantics.TryGetField(source, "id", out _);
                missing &= contract.IsSatisfiedByConstant(value);
            }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(missing);
        }
        Assert.Equal(0, minimum);
        Assert.True(contract.IsSatisfiedByConstant(value));
        fields["ID"] = ObservationValue.FromString("long");
        Assert.True(ConstraintSemantics.TryGetField(fields, "id", out var current));
        Assert.Equal("long", current.GetString());
        Assert.False(contract.IsSatisfiedByConstant(ObservationValue.FromObject(fields)));
    }
}

[JsonSerializable(typeof(InputConstraintProjectionTests.TextInput))]
internal partial class LengthProjectionContext : JsonSerializerContext;
