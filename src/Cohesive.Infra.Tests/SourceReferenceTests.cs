using System.Text.Json;
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
        Assert.Throws<ArgumentException>(() => new SourceReference("Repo://src/file.cs"));
        Assert.Throws<ArgumentException>(() => new SourceReference("repo://"));
        Assert.Throws<ArgumentException>(() => new SourceReference("repo://src/file name.cs"));
        Assert.Throws<ArgumentException>(() => SourceReference.Create("Repo", "src/file.cs"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("../outside.csproj"));
        Assert.Throws<ArgumentException>(() => new RepositoryPath("src//app.csproj"));
    }
}
