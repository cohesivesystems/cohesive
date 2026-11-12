using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics.Tests;

public sealed class WorkspaceInspectionReaderTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"0\":null,\"1\":[{},[],{\"a/b~c\":42}],\"\":true}")]
    public async Task ReconstructsNestedPagedValuesWithoutConfusingNullOrNumericProperties(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = await WorkspaceInspectionReader.ReadAsync("", (pointer, offset, _) => Task.FromResult(Page(doc.RootElement, pointer, offset)));
        Assert.True(JsonElement.DeepEquals(doc.RootElement, result));
    }

    [Fact]
    public async Task RejectsNonAdvancingAndIncompletePages()
    {
        using var doc = JsonDocument.Parse("[1,2]");
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInspectionReader.ReadAsync("", (pointer, offset, _) =>
        {
            var page = Page(doc.RootElement, pointer, offset);
            return Task.FromResult(pointer == "" ? page with { Children = page.Children with { NextOffset = 0 } } : page);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInspectionReader.ReadAsync("", (pointer, offset, _) =>
        {
            var page = Page(doc.RootElement, pointer, offset);
            return Task.FromResult(pointer == "" ? page with { Children = page.Children with { NextOffset = null } } : page);
        }));
    }

    [Fact]
    public async Task EnforcesRequestByteAndCancellationBounds()
    {
        using var doc = JsonDocument.Parse("[\"abcdefgh\",null]");
        Task<WorkspaceInspection> Inspect(string pointer, int offset, CancellationToken _) => Task.FromResult(Page(doc.RootElement, pointer, offset));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInspectionReader.ReadAsync("", Inspect, maximumRequests: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInspectionReader.ReadAsync("", Inspect, maximumBytes: 5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceInspectionReader.ReadAsync("", Inspect, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void WireContractDistinguishesJsonNullFromTruncation()
    {
        using var doc = JsonDocument.Parse("[null]");
        var options = StrictDocumentJson.CreateOptions();
        var array = JsonSerializer.Deserialize<WorkspaceInspection>(JsonSerializer.Serialize(Page(doc.RootElement, "", 0), options), options)!;
        var scalar = JsonSerializer.Deserialize<WorkspaceInspection>(JsonSerializer.Serialize(Page(doc.RootElement, "/0", 0), options), options)!;
        Assert.True(array.IsTruncated);
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.False(scalar.IsTruncated);
        Assert.Equal(JsonValueKind.Null, scalar.ValueKind);
    }

    static WorkspaceInspection Page(JsonElement root, string pointer, int offset)
    {
        var value = root;
        if (pointer != "")
            foreach (var segment in pointer[1..].Split('/'))
                value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(segment)] : value.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
        var children = value.ValueKind switch
        {
            JsonValueKind.Array => Enumerable.Range(0, value.GetArrayLength()).Select(i => pointer + "/" + i).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().Select(p => pointer + "/" + p.Name.Replace("~", "~0").Replace("/", "~1")).ToArray(),
            _ => []
        };
        return new(new("document", "test", null, "fingerprint", "source"), pointer,
            children.Length > 0 ? null : value.Clone(),
            new([.. children.Skip(offset).Take(1)], children.Length, offset + 1 < children.Length ? offset + 1 : null),
            new([], 0, null), new([], 0, null), value.ValueKind);
    }
}
