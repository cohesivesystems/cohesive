using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class AnnotationValueTests
{
    [Fact]
    public void FromObject_ProjectsClrObjectToJsonCompatibleNode()
    {
        var annotation = AnnotationValue.FromObject(new
        {
            source = "dsl",
            retryCount = 2,
            tags = new[] { "typed", "object" },
            nested = new
            {
                enabled = true
            }
        });

        using var expected = JsonDocument.Parse("""
            {
              "source": "dsl",
              "retryCount": 2,
              "tags": ["typed", "object"],
              "nested": {
                "enabled": true
              }
            }
            """);

        Assert.True(JsonElement.DeepEquals(expected.RootElement, annotation.Value));
    }

    [Fact]
    public void AnnotationMapCreate_ProjectsClrScalarWithoutManualJson()
    {
        var annotations = AnnotationMap.Create("sem.concept", "load-id");

        Assert.Equal("load-id", annotations[new AnnotationKey("sem.concept")].Value.GetString());
    }

    [Fact]
    public void AnnotationValue_Equality_UsesStructuralJsonSemantics()
    {
        var left = AnnotationValue.FromObject(new
        {
            priority = 1,
            domain = "edi"
        });

        var right = AnnotationValue.FromObject(new
        {
            domain = "edi",
            priority = 1
        });

        var different = AnnotationValue.FromObject(new
        {
            priority = 2,
            domain = "edi"
        });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
    }
    [Fact]
    public void AnnotationOwnsSnapshotOfMutableSource()
    {
        var source = new Dictionary<string, object> { ["values"] = new[] { "original" } };
        var annotation = AnnotationValue.FromObject(source);
        ((string[])source["values"])[0] = "changed";
        source.Clear();
        Assert.Equal("original", annotation.Value.GetProperty("values")[0].GetString());
        var roundTrip = JsonSerializer.Deserialize<AnnotationValue>(JsonSerializer.Serialize(annotation))!;
        Assert.Equal(annotation, roundTrip);
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1e2", "100")]
    [InlineData("{\"b\":2,\"a\":1}", "{\"a\":1.0,\"b\":2}")]
    public void EquivalentJsonHasEqualHashes(string leftJson, string rightJson)
    {
        var left = JsonSerializer.Deserialize<AnnotationValue>(leftJson)!;
        var right = JsonSerializer.Deserialize<AnnotationValue>(rightJson)!;
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
    [Fact]
    public void AnnotationHashIsAllocationFreeAfterPreparation()
    {
        var annotation = AnnotationValue.FromObject(new { text = "original", values = new[] { 1, 2, 3 } });
        _ = annotation.GetHashCode();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; index++) _ = annotation.GetHashCode();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Fact]
    public void ScalarProjectionPreservesNestedPathsAndExplicitIdentityPolicy()
    {
        var annotations = AnnotationMap.Create("metadata", new { name = "sample", enabled = true,
            values = new object?[] { 1.5m, null, "", "value" } });
        var scalars = AnnotationMap.FlattenScalars(annotations, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("sample", scalars["METADATA.NAME"]);
        Assert.Equal("true", scalars["metadata.enabled"]);
        Assert.Equal("1.5", scalars["metadata.values[0]"]);
        Assert.Equal("value", scalars["metadata.values[3]"]);
        Assert.Equal(4, scalars.Count);
    }
}
