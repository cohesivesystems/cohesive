using System.Text;
using System.Text.Json;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class ObservationJsonMaterializationTests
{
    static readonly byte[] Valid = "{\"audit\":7,\"Name\":\"Ada\",\"Id\":42}"u8.ToArray();

    [Fact]
    public void StrictJsonUsesTheExactBoundLayoutAndExistingConstructorDefaults()
    {
        var shape = Shape();
        var layout = ObservationLayout.Create(shape, ["Note", "audit", "Name", "Id"]);
        var plan = ObservationMaterializer.For<Row>(shape).Compile(layout);
        Assert.Same(layout, plan.OrdinalLayout);
        Assert.Equal(new Row(42, "Ada"), plan.MaterializeJson(Valid));
        Assert.Equal(new Row(42, "Ada", "present"), plan.MaterializeJson("{\"Id\":42,\"Name\":\"Ada\",\"Note\":\"present\",\"audit\":7}"u8));
        Assert.Equal(new Row(42, "Ada"), plan.MaterializeJson("{\"Id\":42,\"Name\":\"Ada\",\"Note\":null,\"audit\":7} \r\n\t"u8));
        Assert.Equal(new Row(42, "Ada"), plan.MaterializeJson("{\"\\u0049d\":42,\"Name\":\"Ada\",\"audit\":7}"u8));
    }

    [Fact]
    public void NameOnlyPlanCannotSilentlyChooseAnIngestionLayout()
    {
        var plan = ObservationMaterializer.For<Row>(Shape()).Compile();
        Assert.Throws<InvalidOperationException>(() => plan.MaterializeJson(Valid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"Id\":42,\"Id\":43,\"Name\":\"Ada\",\"audit\":7}")]
    [InlineData("{\"Id\":42,\"\\u0049d\":43,\"Name\":\"Ada\",\"audit\":7}")]
    [InlineData("{\"id\":42,\"Name\":\"Ada\",\"audit\":7}")]
    [InlineData("{\"Id\":42,\"Name\":\"Ada\",\"audit\":7,\"extra\":1}")]
    [InlineData("{\"Id\":42,\"Name\":\"Ada\",\"audit\":7} {}")]
    [InlineData("{\"Id\":42,\"Name\":\"Ada\",\"audit\":7} /* comment */")]
    public void RootAdmissionRemainsStrictRegardlessOfProjectionSerializerOptions(string json)
    {
        var shape = Shape();
        var plan = ObservationMaterializer.For<Row>(shape)
            .WithSerializerOptions(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Compile(ObservationLayout.Create(shape));
        Assert.ThrowsAny<JsonException>(() => plan.MaterializeJson(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("{\"Id\":42,\"Name\":\"Ada\"}")]
    [InlineData("{\"Id\":null,\"Name\":\"Ada\",\"audit\":7}")]
    [InlineData("{\"Id\":true,\"Name\":\"Ada\",\"audit\":7}")]
    [InlineData("{\"Id\":42,\"Name\":\"Ada\",\"audit\":null}")]
    public void FullShapeAdmissionPrecedesAnyMappedConverter(string json)
    {
        var shape = Shape();
        var calls = 0;
        var plan = ObservationMaterializer.For<Row>(shape).Map("Name", row => row.Name, value =>
        {
            calls++;
            return value.GetRequiredString();
        }).Compile(ObservationLayout.Create(shape));
        var error = Assert.Throws<ArgumentException>(() => plan.MaterializeJson(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("utf8Json", error.ParamName);
        Assert.Equal(0, calls);
        Assert.Equal(new Row(42, "Ada"), plan.MaterializeJson(Valid));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TrailingContentCannotReachConvertersAndMissingPolicyStillAppliesAfterAdmission()
    {
        var shape = Shape();
        var calls = 0;
        var plan = ObservationMaterializer.For<Row>(shape).Map("Name", row => row.Name, value =>
        {
            calls++;
            return value.GetRequiredString();
        }).Compile(ObservationLayout.Create(shape));
        Assert.ThrowsAny<JsonException>(() => plan.MaterializeJson([.. Valid, (byte)'x']));
        Assert.Equal(0, calls);
        var required = ObservationMaterializer.For<RequiredNote>(shape).WithMissingFieldBehavior(ObservationMissingFieldBehavior.Throw).Compile(ObservationLayout.Create(shape));
        Assert.Throws<InvalidOperationException>(() => required.MaterializeJson(Valid));
    }

    [Fact]
    public void MutableOutputsAndInputBytesStayIndependent()
    {
        Shape definition = new(new("binary"), [new(new("Bytes"), new ScalarTypeRef(ScalarTypeKind.Bytes))]);
        ShapeGraph graph = new(new("json-binary"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var plan = ObservationMaterializer.For<BinaryRow>(shape).Compile(ObservationLayout.Create(shape));
        var json = "{\"Bytes\":\"AQID\"}"u8.ToArray();
        var first = plan.MaterializeJson(json);
        first.Bytes[0] = 99;
        var second = plan.MaterializeJson(json);
        Assert.Equal(new byte[] { 1, 2, 3 }, second.Bytes);
        Array.Fill(json, (byte)0);
        Assert.Equal(new byte[] { 1, 2, 3 }, second.Bytes);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(4097)]
    public void WidePresenceStorageRemainsValidAfterMalformedInput(int count)
    {
        Shape definition = new(new("wide"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)),
            .. Enumerable.Range(1, count - 1).Select(index => new FieldDefinition(new("field" + index), new ScalarTypeRef(ScalarTypeKind.Int64)))]);
        ShapeGraph graph = new(new("wide-json"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var plan = ObservationMaterializer.For<OnlyId>(shape).Compile(ObservationLayout.Create(shape));
        var json = Encoding.UTF8.GetBytes("{\"Id\":42," + string.Join(',', Enumerable.Range(1, count - 1).Select(index => $"\"field{index}\":{index}")) + "}");
        Assert.Equal(new OnlyId(42), plan.MaterializeJson(json));
        Assert.ThrowsAny<JsonException>(() => plan.MaterializeJson(json.AsSpan(0, json.Length - 1)));
        Assert.Equal(new OnlyId(42), plan.MaterializeJson(json));
    }

    [Fact]
    public void WarmAllocationContainsOwnedValuesTextReaderAndClrResultOnly()
    {
        var shape = Shape();
        var plan = ObservationMaterializer.For<Row>(shape).Compile(ObservationLayout.Create(shape));
        for (var index = 0; index < 100; index++) _ = plan.MaterializeJson(Valid);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Row? result = null;
        for (var index = 0; index < 100; index++) result = plan.MaterializeJson(Valid);
        // Four owned value slots (128 B), array header (24), ordinal reader (40), text (32), DTO (40).
        Assert.InRange((GC.GetAllocatedBytesForCurrentThread() - before) / 100, 0, 272);
        GC.KeepAlive(result);
    }

    static GraphShapeId Shape()
    {
        Shape definition = new(new("row"), [
            new(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)),
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Note"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable),
            new(new("audit"), new ScalarTypeRef(ScalarTypeKind.Int64))]);
        ShapeGraph graph = new(new("json-materialization"), [definition]);
        return new(graph, definition.Id);
    }

    public sealed record Row(long Id, string Name, string? Note = null);
    public sealed record RequiredNote(long Id, string Name, string Note);
    public sealed record BinaryRow(byte[] Bytes);
    public sealed record OnlyId(long Id);
}
