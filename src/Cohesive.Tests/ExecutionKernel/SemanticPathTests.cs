using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class SemanticPathTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SemanticPath_HasStructuralOrdinalEquality()
    {
        var first = new ExecutionSemanticPath(ImmutableArray.Create("process", "reserve"));
        var second = new ExecutionSemanticPath(ImmutableArray.Create("process", "reserve"));
        var differentCase = new ExecutionSemanticPath(ImmutableArray.Create("process", "Reserve"));
        var differentOrder = new ExecutionSemanticPath(ImmutableArray.Create("reserve", "process"));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, differentCase);
        Assert.NotEqual(first, differentOrder);
    }

    [Fact]
    public void DefaultSemanticPath_HasSafeValueEqualityAndHashing()
    {
        var uninitialized = default(ExecutionSemanticPath);

        Assert.Equal(default, uninitialized);
        Assert.Equal(0, uninitialized.GetHashCode());
        Assert.NotEqual(ExecutionSemanticPath.From("process"), uninitialized);
    }

    [Fact]
    public void Append_ReturnsNewPathAndPreservesOriginal()
    {
        var root = ExecutionSemanticPath.From("process");

        var child = root.Append("reserve");

        Assert.Collection(root.Segments, actual => Assert.Equal("process", actual));
        Assert.Collection(
            child.Segments,
            actual => Assert.Equal("process", actual),
            actual => Assert.Equal("reserve", actual));
        Assert.Throws<InvalidOperationException>(() => default(ExecutionSemanticPath).Append("node"));
    }

    [Fact]
    public void ToString_UsesCanonicalJsonPointerEscaping()
    {
        var path = new ExecutionSemanticPath(["processes", "retry/effect", "a~b"]);

        Assert.Equal("/processes/retry~1effect/a~0b", path.ToString());
    }

    [Fact]
    public void SemanticPath_RoundTripsWithoutCollectionIdentityAffectingEquality()
    {
        var path = new ExecutionSemanticPath(["processes", "branch", "approved"]);

        var json = JsonSerializer.Serialize(path, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExecutionSemanticPath>(json, JsonOptions);

        Assert.Equal(path, roundTrip);
        Assert.Equal(path.GetHashCode(), roundTrip.GetHashCode());
    }

    [Fact]
    public void SemanticPath_RejectsMissingOrInvalidSegments()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionSemanticPath(default));
        Assert.Throws<ArgumentException>(() => new ExecutionSemanticPath([]));
        Assert.Throws<ArgumentException>(() => new ExecutionSemanticPath(["process", null!]));
        Assert.Throws<ArgumentException>(() => new ExecutionSemanticPath(["process", " "]));
        Assert.Throws<ArgumentNullException>(() => ExecutionSemanticPath.From(null!));
        Assert.Throws<ArgumentException>(() => ExecutionSemanticPath.From(" "));
    }
}
