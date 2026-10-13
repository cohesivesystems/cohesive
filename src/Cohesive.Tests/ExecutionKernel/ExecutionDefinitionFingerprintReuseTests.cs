using System.Collections.Concurrent;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionFingerprintReuseTests
{
    [Fact]
    public void AuthoredDocument_ReusesIndependentlyComputedConstructionDigest()
    {
        var document = Create();
        Assert.Same(document.Metadata.Fingerprint, ExecutionDefinitionFingerprinter.Compute(document));
        Assert.Equal(Independent(document), ExecutionDefinitionFingerprinter.Compute(document));
        var retained = document.WithRetainedDiagnostics([]);
        Assert.Same(ExecutionDefinitionFingerprinter.Compute(document), ExecutionDefinitionFingerprinter.Compute(retained));
    }

    [Fact]
    public void ImportedDocument_CoordinatesConcurrentFirstUseWithoutTrustingDeclaredMetadata()
    {
        var original = Create();
        var document = new ExecutionDefinitionDocument(original.Kind, original.Metadata, original.Definition, original.Extensions);
        var serialized = ExecutionDefinitionJsonSerializer.Serialize(document);
        var hash = document.GetHashCode();
        ConcurrentBag<ExecutionDefinitionFingerprint> results = [];
        Parallel.For(0, 64, _ => results.Add(ExecutionDefinitionFingerprinter.Compute(document)));
        var first = results.First();
        Assert.NotSame(document.Metadata.Fingerprint, first);
        Assert.All(results, value => Assert.Same(first, value));
        Assert.Equal(Independent(document), first);
        Assert.Equal(serialized, ExecutionDefinitionJsonSerializer.Serialize(document));
        Assert.Equal(hash, document.GetHashCode());
        Assert.Equal(original, document);
    }

    [Fact]
    public void ForgedFingerprint_StillFailsEveryIntegrityAdmission()
    {
        var original = Create();
        var other = Create("different");
        var metadata = new ExecutionDefinitionMetadata(original.Metadata.DefinitionId, original.Metadata.RevisionId,
            original.Metadata.SchemaVersion, other.Metadata.Fingerprint, original.Metadata.Provenance);
        var forged = new ExecutionDefinitionDocument(original.Kind, metadata, original.Definition);
        Assert.Equal(Independent(original), ExecutionDefinitionFingerprinter.Compute(forged));
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var validation = ExecutionDefinitionDocumentValidator.Validate(forged);
            Assert.Contains(validation.Diagnostics, d => d.Code == ExecutionDefinitionDiagnosticCodes.FingerprintMismatch);
            Assert.False(ExecutionDefinitionDocumentCatalog.TryCreate([forged], out _).IsValid);
        }
    }

    [Fact]
    public void SameIdentityAndRevisionWithNewContent_HasIndependentComputation()
    {
        var first = Create();
        var second = Create("different");
        Assert.Equal(first.Metadata.DefinitionId, second.Metadata.DefinitionId);
        Assert.Equal(first.Metadata.RevisionId, second.Metadata.RevisionId);
        Assert.NotEqual(ExecutionDefinitionFingerprinter.Compute(first), ExecutionDefinitionFingerprinter.Compute(second));
        var imported = new ExecutionDefinitionDocument(first.Kind, first.Metadata, first.Definition);
        Assert.NotSame(ExecutionDefinitionFingerprinter.Compute(first), ExecutionDefinitionFingerprinter.Compute(imported));
    }

    [Fact]
    public void ContextualExtensionValidation_IsFreshAfterSuccessfulIntegrityComputation()
    {
        TypeId settings = new("settings");
        var graph = new ShapeGraph(new("fingerprint-reuse"), [],
            [new TypeDefinition.Structural(settings, [new(new("mode"), new ScalarTypeRef(ScalarTypeKind.String))])]);
        var extension = new ExecutionDefinitionExtension(new("settings"), new("1"), PortableValue.Concrete(
            new(new NamedTypeRef(settings)),
            ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["mode"] = ObservationValue.FromString("adaptive") })));
        var document = ExecutionDefinitionDocument.Create(new("test"), new("same"), new("1"),
            new { mode = "adaptive" }, Provenance(), [extension]);
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(document);
        Assert.True(ExecutionDefinitionDocumentValidator.Validate(document, graph).IsValid);
        Assert.False(ExecutionDefinitionDocumentValidator.Validate(document).IsValid);
        Assert.True(ExecutionDefinitionDocumentValidator.Validate(document, graph).IsValid);
        Assert.Same(fingerprint, ExecutionDefinitionFingerprinter.Compute(document));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(4096)]
    public void ImportedFirstFingerprintDoesNotAllocateAFullCanonicalEnvelope(int rows)
    {
        using var json = JsonDocument.Parse("{\"rows\":[" + string.Join(",", Enumerable.Repeat(
            "{\"z\":\"λ/\\\"<>&\",\"values\":[1.00,-0.0,1e21,null],\"a\":true}", rows)) + "]}");
        var expected = ExecutionDefinitionFingerprinter.Compute(ExecutionDefinitionDocument.CurrentSchemaVersion, new("test"), json.RootElement);
        var metadata = new ExecutionDefinitionMetadata(new("streaming"), new("1"),
            ExecutionDefinitionDocument.CurrentSchemaVersion, expected, Provenance());
        _ = ExecutionDefinitionFingerprinter.Compute(new(new("test"), metadata, json.RootElement));
        var imported = new ExecutionDefinitionDocument(new("test"), metadata, json.RootElement);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = ExecutionDefinitionFingerprinter.Compute(imported);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        // Normalization and owned document construction precede this boundary. First digest
        // computation must retain only its digest, without allocating a payload-sized envelope.
        Assert.InRange(allocated, 0, 16_384);
    }

    [Fact]
    public void WarmRepeatedCompute_DoesNotAllocatePayloadSizedWork()
    {
        using var json = JsonDocument.Parse("{\"rows\":[" + string.Join(",", Enumerable.Repeat(
            "{\"name\":\"example\",\"values\":[1,2,3,null],\"enabled\":true}", 4096)) + "]}");
        var seed = ExecutionDefinitionFingerprinter.Compute(ExecutionDefinitionDocument.CurrentSchemaVersion, new("test"), json.RootElement);
        var document = new ExecutionDefinitionDocument(new("test"), new(new("large"), new("1"),
            ExecutionDefinitionDocument.CurrentSchemaVersion, seed, Provenance()), json.RootElement);
        _ = ExecutionDefinitionFingerprinter.Compute(document);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1024; iteration++)
            _ = ExecutionDefinitionFingerprinter.Compute(document);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
        Assert.Equal(Independent(document), ExecutionDefinitionFingerprinter.Compute(document));
    }

    static ExecutionDefinitionDocument Create(string value = "original") => ExecutionDefinitionDocument.Create(
        new("test"), new("same"), new("1"), new { value, ordered = new[] { 1, 2, 3 } }, Provenance());
    static ExecutionProvenance Provenance() => new(new("fingerprint-tests", "1"), new("tests/fingerprint-reuse"), DocumentOrigin.Generated);
    static ExecutionDefinitionFingerprint Independent(ExecutionDefinitionDocument document) => ExecutionDefinitionFingerprinter.Compute(
        document.Metadata.SchemaVersion, document.Kind, document.Definition, document.Extensions);
}
