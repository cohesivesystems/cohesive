using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalJsonWriterTests
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ArrayClassification_UsesStableStructuralPathsWithoutContentInference()
    {
        JsonObject content = new()
        {
            ["ordered"] = new JsonArray(
                new JsonObject { ["id"] = "b" },
                new JsonObject { ["id"] = "a" }),
            ["sets"] = new JsonArray(
                new JsonObject
                {
                    ["members"] = new JsonArray(
                        new JsonObject { ["id"] = "b" },
                        new JsonObject { ["id"] = "a" })
                })
        };
        List<string> visitedPaths = [];

        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            path =>
            {
                visitedPaths.Add(path.Value);
                return path.Value == "/sets/*/members"
                    ? CanonicalJsonArrayOrdering.ObjectSet("id")
                    : CanonicalJsonArrayOrdering.Sequence;
            });

        Assert.Equal(
            "{\"ordered\":[{\"id\":\"b\"},{\"id\":\"a\"}],\"sets\":[{\"members\":[{\"id\":\"a\"},{\"id\":\"b\"}]}]}",
            Encoding.UTF8.GetString(canonical));
        Assert.Equal(["/ordered", "/sets", "/sets/*/members"], visitedPaths);
    }

    [Fact]
    public void ArrayPath_EscapesPropertySegmentsAndHasValidDefaultRoot()
    {
        JsonObject content = new()
        {
            ["a/b*~"] = new JsonArray()
        };
        CanonicalJsonArrayPath visited = default;

        _ = CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            path =>
            {
                visited = path;
                return CanonicalJsonArrayOrdering.Sequence;
            });

        Assert.Equal(string.Empty, default(CanonicalJsonArrayPath).Value);
        Assert.Equal("/a~1b~2~0", visited.Value);
    }

    [Fact]
    public void ObjectSet_RejectsMissingAndDuplicateSortKeys()
    {
        JsonObject missing = new()
        {
            ["items"] = new JsonArray(new JsonObject { ["value"] = 1 })
        };
        JsonObject duplicate = new()
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "same", ["value"] = 1 },
                new JsonObject { ["id"] = "same", ["value"] = 2 })
        };

        Assert.Throws<InvalidOperationException>(() => WriteObjectSet(missing));
        Assert.Throws<InvalidOperationException>(() => WriteObjectSet(duplicate));
    }

    [Fact]
    public void StringSet_RejectsDuplicateItems()
    {
        JsonObject content = new()
        {
            ["items"] = new JsonArray("duplicate", "duplicate")
        };

        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            static path => path.Value == "/items"
                ? CanonicalJsonArrayOrdering.StringSet
                : CanonicalJsonArrayOrdering.Sequence));
    }

    static byte[] WriteObjectSet(JsonObject content) =>
        CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            static path => path.Value == "/items"
                ? CanonicalJsonArrayOrdering.ObjectSet("id")
                : CanonicalJsonArrayOrdering.Sequence);
}
