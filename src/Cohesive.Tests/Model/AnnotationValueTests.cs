using System.Text.Json.Nodes;

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

        var expected = JsonNode.Parse("""
            {
              "source": "dsl",
              "retryCount": 2,
              "tags": ["typed", "object"],
              "nested": {
                "enabled": true
              }
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, annotation.Value));
    }

    [Fact]
    public void AnnotationMapCreate_ProjectsClrScalarWithoutManualJson()
    {
        var annotations = AnnotationMap.Create("sem.concept", "load-id");

        Assert.Equal("load-id", annotations[new AnnotationKey("sem.concept")].Value?.GetValue<string>());
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
}
