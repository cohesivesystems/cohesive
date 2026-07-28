using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionProvenanceTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Provenance_RoundTripsPortableProducerSourceAndSemanticPath()
    {
        var provenance = new ExecutionProvenance(
            producer: new("ari", version: " 2026.07 "),
            source: new(
                reference: "notion:execution-kernel/specification",
                semanticPath: ExecutionSemanticPath.From("processes").Append("retry/effect"),
                description: "  Effect retry rule  "),
            origin: DocumentOrigin.Generated);

        var json = JsonSerializer.Serialize(provenance, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExecutionProvenance>(json, JsonOptions);

        Assert.Equal("2026.07", provenance.Producer.Version);
        Assert.Equal("Effect retry rule", provenance.Source.Description);
        Assert.Equal(provenance, roundTrip);
        Assert.Equal(provenance.GetHashCode(), roundTrip?.GetHashCode());
        Assert.Contains("\"origin\":\"Generated\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerProvenance_RequiresStableProducerAndNormalizesOptionalVersion()
    {
        Assert.Throws<ArgumentNullException>(() => new ExecutionProducerProvenance(null!));
        Assert.Throws<ArgumentException>(() => new ExecutionProducerProvenance(" "));
        Assert.Null(new ExecutionProducerProvenance("ari", "  ").Version);
    }

    [Fact]
    public void SourceProvenance_RequiresReferenceAndRejectsDefaultSemanticPath()
    {
        Assert.Throws<ArgumentNullException>(() => new ExecutionSourceProvenance(null!));
        Assert.Throws<ArgumentException>(() => new ExecutionSourceProvenance(" "));
        Assert.Throws<ArgumentException>(() => new ExecutionSourceProvenance(
            reference: "source",
            semanticPath: default(ExecutionSemanticPath)));
    }

    [Fact]
    public void ExecutionProvenance_RequiresProducerSourceAndDefinedOrigin()
    {
        var producer = new ExecutionProducerProvenance("ari");
        var source = new ExecutionSourceProvenance("source");

        Assert.Throws<ArgumentNullException>(() => new ExecutionProvenance(null!, source));
        Assert.Throws<ArgumentNullException>(() => new ExecutionProvenance(producer, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionProvenance(
            producer,
            source,
            origin: (DocumentOrigin)int.MaxValue));
    }
}
