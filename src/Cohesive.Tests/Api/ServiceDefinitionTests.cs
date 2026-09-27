using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Api;

public sealed class ServiceDefinitionTests
{
    static ExecutionDefinitionReference Reference(string id) => new(new(id), new("v1"),
        new("sha256", "test/v1", new string('a', 64)));

    [Fact]
    public void OneServiceRoundTripsExactTransitionQueryAndProcessAuthorities()
    {
        var transition = new ServiceTransitionOperation("revise", new(new("notes"), new("note")),
            Reference("note/revise"), [new("notes.write")]);
        var query = new ServiceQueryOperation("search", Reference("notes/by-owner"), [new("notes.read")]);
        var process = new ServiceProcessOperation("publish", Reference("notes/publish"), [new("notes.publish")]);
        var definition = new ServiceDefinition([transition, query, process]);
        var document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"), definition,
            new(new("tests"), new("tests/services"), DocumentOrigin.Generated));

        var read = ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(document), out var restored);
        Assert.True(read.IsValid);
        var projected = ServiceDefinitionDocuments.ValidateAndProject(restored!, out var actual);
        Assert.True(projected.IsValid, string.Join("; ", projected.Diagnostics.Select(d => d.Message)));
        Assert.Equal(definition, actual);
        Assert.Equal(new[] { "publish", "revise", "search" }, actual!.Operations.Select(o => o.Id));
        Assert.Equal(process.Process, Assert.IsType<ServiceProcessOperation>(actual.Operations[0]).Process);
        Assert.Equal(transition.Transition, Assert.IsType<ServiceTransitionOperation>(actual.Operations[1]).Transition);
        Assert.Equal(query.Query, Assert.IsType<ServiceQueryOperation>(actual.Operations[2]).Query);
        Assert.Equal(document.Metadata.Fingerprint, restored!.Metadata.Fingerprint);
    }

    [Fact]
    public void OperationIdentityIsUniqueAcrossSemanticFamilies()
    {
        Assert.Throws<ArgumentException>(() => new ServiceDefinition([
            new ServiceQueryOperation("run", Reference("read")),
            new ServiceProcessOperation("run", Reference("write"))]));
    }

    [Fact]
    public void ValueEqualityRetainsFamilyAndExactReferenceWhileNormalizingRequirements()
    {
        var first = new ServiceQueryOperation("run", Reference("query"), [new("b"), new("a")]);
        var reordered = new ServiceQueryOperation("run", Reference("query"), [new("a"), new("b")]);
        Assert.Equal(first, reordered);
        Assert.Equal(first.GetHashCode(), reordered.GetHashCode());
        Assert.NotEqual<ServiceOperation>(first, new ServiceProcessOperation("run", Reference("query"), [new("a"), new("b")]));
        Assert.NotEqual(first, new ServiceQueryOperation("run", Reference("other"), [new("a"), new("b")]));
    }
}
