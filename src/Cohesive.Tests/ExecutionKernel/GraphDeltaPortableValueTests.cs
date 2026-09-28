using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class GraphDeltaPortableValueTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"version\"")]
    [InlineData("\"Unknown\"")]
    public void DeltaKindRejectsNoncanonicalWireValues(string json) =>
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<GraphDeltaKind>(json));

    [Fact]
    public void NativeDeltaRetainsPolymorphicOperationsAndAnnotationsThroughPortableQueryValue()
    {
        var query = HostedQuery<string, GraphDelta>.Create(new("tests/delta"), new("1"),
            new("tests.delta", "1"), new { Policy = "native-delta" },
            new(new("tests"), new("tests/delta"), DocumentOrigin.Generated),
            evaluationSemantics: HostedQueryEvaluationSemantics.DeterministicComputation);
        Assert.True(query.IsValid, string.Join("; ", query.Validation.Diagnostics));
        var delta = new GraphDelta("delta/1", [
            new AddShapeOperation(new(new("shape/new"), [new(new("Id"), new ScalarTypeRef(ScalarTypeKind.String))])),
            new RemoveShapeOperation(new("shape/old")),
            new SetGraphAnnotationOperation(new("owner"), AnnotationValue.FromString("team"))],
            GraphDeltaKind.Version, new("graph/before"), new("graph/after"), "1", "2",
            ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("reason"), AnnotationValue.FromString("test")));
        var encoded = HostedQueryValueAdapter.Encode(delta, query.ResultContract);
        Assert.Equal(ResultType.Success, encoded.Type);
        var decoded = HostedQueryValueAdapter.Decode<GraphDelta>(encoded.Success!, query.ResultContract);
        Assert.True(decoded.Type == ResultType.Success, decoded.Failure?.Message);
        Assert.Equal(delta.Id, decoded.Success!.Id);
        Assert.Equal(delta.Kind, decoded.Success.Kind);
        Assert.Equal(delta.SourceGraphId, decoded.Success.SourceGraphId);
        Assert.Equal(delta.TargetGraphId, decoded.Success.TargetGraphId);
        Assert.Equal(delta.Annotations, decoded.Success.Annotations);
        Assert.Collection(decoded.Success.Operations,
            value => Assert.Equal("Id", Assert.Single(Assert.IsType<AddShapeOperation>(value).Shape.Fields).Name),
            value => Assert.IsType<RemoveShapeOperation>(value),
            value => Assert.IsType<SetGraphAnnotationOperation>(value));
        Assert.Equal(encoded.Success, HostedQueryValueAdapter.Encode(decoded.Success, query.ResultContract).Success);
        var malformed = JsonNode.Parse(encoded.Success!.Value!.Value.GetRawText())!;
        var operations = malformed.AsObject().Single(pair => string.Equals(pair.Key, "Operations", StringComparison.OrdinalIgnoreCase)).Value!;
        operations[0]!["$operation"] = "unknown-operation";
        var rejected = HostedQueryValueAdapter.Decode<GraphDelta>(
            PortableValue.Concrete(query.ResultContract, ObservationValue.FromJsonNode(malformed)), query.ResultContract);
        Assert.Equal(ResultType.Failure, rejected.Type);
        Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, rejected.Failure!.Code);
    }
}
