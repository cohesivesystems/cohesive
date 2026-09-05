using System.Text.Json;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class SourceReferenceTests
{
    [Fact]
    public void Repository_references_are_typed_canonical_and_string_serialized()
    {
        RepositoryPath path = new("src\\Ari.Training.Api\\Ari.Training.Api.csproj");
        var reference = SourceReference.Repository(path);

        Assert.Equal("src/Ari.Training.Api/Ari.Training.Api.csproj", path.Value);
        Assert.Equal("repo://src/Ari.Training.Api/Ari.Training.Api.csproj", reference.Value);
        Assert.Equal(
            "\"repo://src/Ari.Training.Api/Ari.Training.Api.csproj\"",
            JsonSerializer.Serialize(reference));
        Assert.Equal(reference, JsonSerializer.Deserialize<SourceReference>(JsonSerializer.Serialize(reference)));
        Assert.Equal(path, JsonSerializer.Deserialize<RepositoryPath>(JsonSerializer.Serialize(path)));
    }

    [Fact]
    public void References_and_repository_paths_reject_noncanonical_values()
    {
        Assert.Throws<ArgumentException>(() => new SourceReference("missing-scheme"));
        Assert.Throws<ArgumentException>(() => new SourceReference("repo:src/file.cs"));
        Assert.Throws<ArgumentException>(() => new SourceReference("Repo://src/file.cs"));
        Assert.Throws<ArgumentException>(() => new SourceReference("repo://"));
        Assert.Throws<ArgumentException>(() => new SourceReference("repo://src/file name.cs"));
        Assert.Throws<ArgumentException>(() => SourceReference.Create("Repo", "src/file.cs"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("../outside.csproj"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("src//app.csproj"));
    }

    [Fact]
    public void Reference_sets_have_one_shared_canonicalization_authority()
    {
        SourceReference first = new("test://a");
        SourceReference second = new("test://b");

        Assert.Equal<SourceReference>([first, second], SourceReference.NormalizeSet([second, first]));
        Assert.Throws<ArgumentException>(() => SourceReference.NormalizeSet([first, first]));
        Assert.Throws<ArgumentException>(() => SourceReference.NormalizeSet([], requireNonEmpty: true));
    }

    [Fact]
    public void Infrastructure_identifiers_project_to_typed_source_references()
    {
        Assert.Equal(
            "infrastructure-target://pulumi-azure-native/3.16.0",
            InfrastructureSourceReferences.Target(new("pulumi-azure-native/3.16.0")).Value);
        Assert.Equal(
            "infrastructure-lifecycle-authority://pulumi/shipping/development",
            InfrastructureSourceReferences.LifecycleAuthority(new("pulumi/shipping/development")).Value);
        Assert.Equal(
            "infrastructure-node://workloads/api",
            InfrastructureSourceReferences.Node(new("workloads/api")).Value);
    }

    [Fact]
    public void Infrastructure_source_maps_normalize_and_resolve_exact_semantic_subjects()
    {
        var api = InfrastructureSourceReferences.Node(new("workloads/api"));
        var state = InfrastructureSourceReferences.Node(new("resources/state"));
        SourceReference first = new("csharp://manifest/Stack.cs#Define:L20");
        SourceReference second = new("spec://deployment/production");
        var map = new InfrastructureSourceMap(
        [
            new(api, second),
            new(state, first),
            new(api, first)
        ]);

        Assert.Equal<SourceReference>([first, second], map.Resolve(api));
        Assert.Equal(state, map.Entries[0].Subject);
        Assert.Throws<ArgumentException>(() => new InfrastructureSourceMap([new(api, first), new(api, first)]));
    }
}
