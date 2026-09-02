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
        await using MemoryStream validationInput = new(jsonLines, writable: false);
        var validation = await WorldJsonLinesVerifier.ValidateAsync(manifest, validationInput);

        Assert.Equal(manifest.ArtifactId, result.ArtifactId);
        Assert.Equal("verification/test", result.TargetId);
        Assert.NotNull(result.RunId);
        Assert.Equal(2, result.BatchSize);
        Assert.Equal(3, result.ItemCount);
        Assert.True(input.CanRead);
        Assert.True(validation.IsSuccessful);
        Assert.Equal(result, validation.Verification);
        Assert.Empty(validation.Validation.Diagnostics);
    }

    [Theory]
    [InlineData("artifact-id", "artifactId", "simulation.worldArtifact.jsonLines.artifactMismatch")]
    [InlineData(
        "manifest-fingerprint",
        "artifactManifestFingerprint",
        "simulation.worldArtifact.jsonLines.artifactMismatch")]
    [InlineData("run-id", "runId", "simulation.worldArtifact.jsonLines.provisioningIdentityMismatch")]
    [InlineData("batch-id", "batchId", "simulation.worldArtifact.jsonLines.provisioningIdentityMismatch")]
    [InlineData("observation", "observation", "simulation.worldArtifact.jsonLines.observationMismatch")]
    [InlineData("unknown-property", "unexpected", "simulation.worldArtifact.jsonLines.propertyUnknown")]
    [InlineData("duplicate-property", "format", "simulation.worldArtifact.jsonLines.propertyDuplicate")]
    [InlineData("missing-property", "runId", "simulation.worldArtifact.jsonLines.propertyMissing")]
    [InlineData("property-order", "runId", "simulation.worldArtifact.jsonLines.propertyOrderInvalid")]
    public async Task TamperedRecord_FailsClosed(
        string mutation,
        string expectedProperty,
        string expectedCode)
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
            "duplicate-property" => ReplaceOnce(
                content,
                "\"runId\":",
                $"\"format\":\"{WorldJsonLinesSink.Format}\",\"runId\":"),
            "missing-property" => RemoveProperty(content, "runId"),
            "property-order" => SwapFirstTwoProperties(content),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        await using MemoryStream input = new(Encoding.UTF8.GetBytes(tampered), writable: false);

        var exception = await Assert.ThrowsAsync<WorldJsonLinesVerificationException>(() =>
            WorldJsonLinesVerifier.VerifyAsync(manifest, input));

        Assert.Equal(1, exception.LineNumber);
        Assert.Equal(expectedProperty, exception.PropertyName);
        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal($"/lines/0/{expectedProperty}", diagnostic.Location);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsStructuredDiagnosticForNoncanonicalRecordWithoutThrowing()
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        var content = Encoding.UTF8.GetString(jsonLines);
        var noncanonical = ReplaceOnce(content, "{\"format\":", "{ \"format\":");
        await using MemoryStream input = new(Encoding.UTF8.GetBytes(noncanonical), writable: false);

        var result = await WorldJsonLinesVerifier.ValidateAsync(manifest, input);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Verification);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("simulation.worldArtifact.jsonLines.wireNonCanonical", diagnostic.Code);
        Assert.Equal("/lines/0", diagnostic.Location);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsStructuredDiagnosticForInvalidUtf8()
    {
        var manifest = WorldArtifactManifest.FromWorld(DemoWorld().Compile(), rootSeed: 42);
        await using MemoryStream input = new([0xff, (byte)'\n'], writable: false);

        var result = await WorldJsonLinesVerifier.ValidateAsync(manifest, input);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("simulation.worldArtifact.jsonLines.jsonInvalid", diagnostic.Code);
        Assert.Equal("/lines/0", diagnostic.Location);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUtf8ByteOrderMarkAsNoncanonicalWire()
    {
        var (manifest, jsonLines) = await CreateArtifactAsync();
        var preamble = Encoding.UTF8.GetPreamble();
        var withByteOrderMark = new byte[preamble.Length + jsonLines.Length];
        preamble.CopyTo(withByteOrderMark, 0);
        jsonLines.CopyTo(withByteOrderMark, preamble.Length);
        await using MemoryStream input = new(withByteOrderMark, writable: false);

        var result = await WorldJsonLinesVerifier.ValidateAsync(manifest, input);

        Assert.False(result.IsSuccessful);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("simulation.worldArtifact.jsonLines.jsonInvalid", diagnostic.Code);
        Assert.Equal("/lines/0", diagnostic.Location);
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
        Assert.Equal(
            "simulation.worldArtifact.jsonLines.itemMissing",
            Assert.Single(exception.Validation.Diagnostics).Code);
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
        Assert.Equal(
            "simulation.worldArtifact.jsonLines.itemUnexpected",
            Assert.Single(exception.Validation.Diagnostics).Code);
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
        {
            throw new InvalidOperationException($"Property '{propertyName}' was not found.");
        }

        var valueEnd = content.IndexOf('"', valueStart);
        return content[..valueStart] + replacement + content[valueEnd..];
    }

    static string ReplaceOnce(string content, string value, string replacement)
    {
        var index = content.IndexOf(value, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Value '{value}' was not found.");
        }

        return content[..index] + replacement + content[(index + value.Length)..];
    }

    static string RemoveProperty(string content, string propertyName)
    {
        var propertyStart = content.IndexOf($"\"{propertyName}\":", StringComparison.Ordinal);
        if (propertyStart < 0)
        {
            throw new InvalidOperationException($"Property '{propertyName}' was not found.");
        }

        var valueStart = content.IndexOf('"', propertyStart + propertyName.Length + 3) + 1;
        var valueEnd = content.IndexOf('"', valueStart) + 1;
        return content.Remove(propertyStart, valueEnd - propertyStart + 1);
    }

    static string SwapFirstTwoProperties(string content)
    {
        var formatProperty = $"\"format\":\"{WorldJsonLinesSink.Format}\"";
        const string RunIdPrefix = "\"runId\":\"";
        var runIdStart = content.IndexOf(RunIdPrefix, StringComparison.Ordinal);
        if (runIdStart < 0)
        {
            throw new InvalidOperationException("Property 'runId' was not found.");
        }

        var runIdEnd = content.IndexOf('"', runIdStart + RunIdPrefix.Length) + 1;
        var runIdProperty = content[runIdStart..runIdEnd];
        return ReplaceOnce(
            content,
            $"{{{formatProperty},{runIdProperty}",
            $"{{{runIdProperty},{formatProperty}");
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
