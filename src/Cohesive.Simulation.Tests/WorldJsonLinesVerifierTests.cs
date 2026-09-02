using System.Text;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class WorldJsonLinesVerifierTests
{
    [Fact]
    public async Task CompleteStream_VerifiesAgainstIndependentlyDeserializedManifest()
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        await using MemoryStream input = new(jsonLines, writable: false);

        var result = await WorldJsonLinesVerifier.VerifyAsync(manifest, input);

        Assert.Equal(manifest.ArtifactId, result.ArtifactId);
        Assert.Equal("verification/test", result.TargetId);
        Assert.NotNull(result.RunId);
        Assert.Equal(2, result.BatchSize);
        Assert.Equal(3, result.ItemCount);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData("artifact-id", "artifactId")]
    [InlineData("manifest-fingerprint", "artifactManifestFingerprint")]
    [InlineData("run-id", "runId")]
    [InlineData("batch-id", "batchId")]
    [InlineData("observation", "observation")]
    [InlineData("unknown-property", "unexpected")]
    public async Task TamperedRecord_FailsClosed(string mutation, string expectedProperty)
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        var content = Encoding.UTF8.GetString(jsonLines);
        var tampered = mutation switch
        {
            "artifact-id" => ReplaceOnce(content, manifest.ArtifactId.Value, "csimartifact1_tampered"),
            "manifest-fingerprint" => ReplaceOnce(
                content,
                $"\"artifactManifestFingerprint\":\"{manifest.Fingerprint.Value}\"",
                "\"artifactManifestFingerprint\":\"tampered\""),
            "run-id" => ReplacePropertyValue(content, "runId", "tampered"),
            "batch-id" => ReplacePropertyValue(content, "batchId", "tampered"),
            "observation" => ReplaceOnce(
                content,
                "\"observation\":{",
                "\"observation\":{\"tampered\":true,"),
            "unknown-property" => ReplaceOnce(
                content,
                "\"observation\":",
                "\"unexpected\":true,\"observation\":"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        await using MemoryStream input = new(Encoding.UTF8.GetBytes(tampered), writable: false);

        var exception = await Assert.ThrowsAsync<WorldJsonLinesVerificationException>(() =>
            WorldJsonLinesVerifier.VerifyAsync(manifest, input));

        Assert.Equal(1, exception.LineNumber);
        Assert.Equal(expectedProperty, exception.PropertyName);
    }

    [Fact]
    public async Task MissingTrailingItem_FailsClosedAfterLastRecord()
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        var lines = Encoding.UTF8.GetString(jsonLines)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await using MemoryStream input = new(
            Encoding.UTF8.GetBytes(string.Join('\n', lines[..^1]) + '\n'),
            writable: false);

        var exception = await Assert.ThrowsAsync<WorldJsonLinesVerificationException>(() =>
            WorldJsonLinesVerifier.VerifyAsync(manifest, input));

        Assert.Equal(3, exception.LineNumber);
        Assert.Null(exception.PropertyName);
        Assert.Contains("ended before", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtraItem_FailsClosedAtFirstRecordPastManifest()
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        var content = Encoding.UTF8.GetString(jsonLines);
        var firstLine = content[..content.IndexOf('\n')];
        await using MemoryStream input = new(
            Encoding.UTF8.GetBytes(content + firstLine + '\n'),
            writable: false);

        var exception = await Assert.ThrowsAsync<WorldJsonLinesVerificationException>(() =>
            WorldJsonLinesVerifier.VerifyAsync(manifest, input));

        Assert.Equal(4, exception.LineNumber);
        Assert.Null(exception.PropertyName);
        Assert.Contains("more items", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RequiresReadableCallerOwnedStream()
    {
        var manifest = WorldArtifactManifest.FromWorld(DemoWorld().Compile(), rootSeed: 42);
        await using Stream output = new WriteOnlyStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WorldJsonLinesVerifier.VerifyAsync(manifest, output));
    }

    static async Task<(WorldArtifactManifest Manifest, byte[] JsonLines)> CreateArtifactAsync()
    {
        var created = WorldArtifactManifest.FromWorld(DemoWorld().Compile(), rootSeed: 42);
        var retained = WorldArtifactManifestJsonSerializer.Deserialize(
            WorldArtifactManifestJsonSerializer.Serialize(created));
        await using MemoryStream output = new();
        await WorldProvisioner.ProvisionAsync(
            retained,
            new WorldJsonLinesSink("verification/test", output),
            new(batchSize: 2));
        return (retained, output.ToArray());
    }

    static string ReplacePropertyValue(string content, string propertyName, string replacement)
    {
        var prefix = $"\"{propertyName}\":\"";
        var valueStart = content.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        if (valueStart < prefix.Length)
            throw new InvalidOperationException($"Property '{propertyName}' was not found.");
        var valueEnd = content.IndexOf('"', valueStart);
        return content[..valueStart] + replacement + content[valueEnd..];
    }

    static string ReplaceOnce(string content, string value, string replacement)
    {
        var index = content.IndexOf(value, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Value '{value}' was not found.");
        return content[..index] + replacement + content[(index + value.Length)..];
    }

    static WorldDefinition DemoWorld()
    {
        var customers = Simulation.Define<VerifiedCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        return Simulation.DefineWorld("world/json-lines-verifier", "r1", builder => builder
            .Population("customers", count: 3, customers)
            .Exemplar("customer-for-ui", "customers", sequenceIndex: 2));
    }

    public sealed record VerifiedCustomer(string Name, int Age);

    sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
