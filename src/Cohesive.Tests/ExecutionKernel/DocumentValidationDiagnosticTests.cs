using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DocumentValidationDiagnosticTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Evidence_NormalizesOrdinalSetsAndRetainsCanonicalStorage()
    {
        ImmutableArray<string> canonicalLocations = ["/definition/a", "/definition/b"];

        var evidence = new DocumentDiagnosticEvidence(
            stage: "transition-static/v1",
            subject: "node/update-inventory",
            relatedLocations: canonicalLocations,
            sourceReferences: ["source/z", "source/a"],
            resolutionOptions: ["use-fallback", "add-branch"],
            expected: "exhaustive",
            observed: "unknown");

        Assert.True(evidence.RelatedLocations == canonicalLocations);
        Assert.True(evidence.SourceReferences.SequenceEqual(["source/a", "source/z"]));
        Assert.True(evidence.ResolutionOptions.SequenceEqual(["add-branch", "use-fallback"]));
        Assert.False(evidence.RelatedLocations.IsDefault);
        Assert.Equal("transition-static/v1", evidence.Stage);
        Assert.Equal("node/update-inventory", evidence.Subject);
        Assert.Equal("exhaustive", evidence.Expected);
        Assert.Equal("unknown", evidence.Observed);
    }

    [Fact]
    public void Evidence_DefaultCollectionsBecomeInitializedEmptyCollections()
    {
        var evidence = new DocumentDiagnosticEvidence();

        Assert.Empty(evidence.RelatedLocations);
        Assert.Empty(evidence.SourceReferences);
        Assert.Empty(evidence.ResolutionOptions);
        Assert.False(evidence.RelatedLocations.IsDefault);
        Assert.False(evidence.SourceReferences.IsDefault);
        Assert.False(evidence.ResolutionOptions.IsDefault);
    }

    [Fact]
    public void Evidence_HasStructuralEqualityHashingAndJsonRoundTrip()
    {
        var first = new DocumentDiagnosticEvidence(
            stage: "flow",
            subject: "node/choice",
            relatedLocations: ["/b", "/a"],
            sourceReferences: ["source/b", "source/a"],
            resolutionOptions: ["option/b", "option/a"],
            expected: "terminating",
            observed: "fallthrough");
        var second = new DocumentDiagnosticEvidence(
            stage: "flow",
            subject: "node/choice",
            relatedLocations: ["/a", "/b"],
            sourceReferences: ["source/a", "source/b"],
            resolutionOptions: ["option/a", "option/b"],
            expected: "terminating",
            observed: "fallthrough");
        var differentCase = new DocumentDiagnosticEvidence(
            stage: "Flow",
            subject: "node/choice",
            relatedLocations: ["/a", "/b"],
            sourceReferences: ["source/a", "source/b"],
            resolutionOptions: ["option/a", "option/b"],
            expected: "terminating",
            observed: "fallthrough");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, differentCase);

        var json = JsonSerializer.Serialize(first, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DocumentDiagnosticEvidence>(json, JsonOptions);

        Assert.Equal(first, roundTrip);
        Assert.Equal(first.GetHashCode(), roundTrip?.GetHashCode());
    }

    [Fact]
    public void Evidence_RejectsEmptyScalarsAndInvalidSetEntries()
    {
        Assert.Equal(
            "stage",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(stage: " ")).ParamName);
        Assert.Equal(
            "subject",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(subject: "")).ParamName);
        Assert.Equal(
            "expected",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(expected: "\t")).ParamName);
        Assert.Equal(
            "observed",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(observed: "\r\n")).ParamName);
        Assert.Equal(
            "relatedLocations",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(
                relatedLocations: ["/valid", null!])).ParamName);
        Assert.Equal(
            "sourceReferences",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(
                sourceReferences: ["source", " "])).ParamName);
        Assert.Equal(
            "resolutionOptions",
            Assert.Throws<ArgumentException>(() => new DocumentDiagnosticEvidence(
                resolutionOptions: ["retry", "retry"])).ParamName);
    }

    [Fact]
    public void Diagnostic_ExistingConstructorShapeRemainsCompatibleAndEvidenceRoundTrips()
    {
        DocumentValidationDiagnostic legacy = new(
            "execution.test",
            DiagnosticSeverity.Warning,
            "Legacy constructor",
            "/definition",
            "/transition");
        var evidence = new DocumentDiagnosticEvidence(
            stage: "static",
            relatedLocations: ["/definition/body"],
            sourceReferences: ["test.cs:42"]);
        var enriched = legacy with { Evidence = evidence };

        Assert.Null(legacy.Evidence);
        Assert.Equal(evidence, enriched.Evidence);

        var json = JsonSerializer.Serialize(enriched, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DocumentValidationDiagnostic>(json, JsonOptions);

        Assert.Equal(enriched, roundTrip);
    }

    [Fact]
    public void DiagnosticComparer_OrdersEvidenceAfterMessageDeterministically()
    {
        var expected = new[]
        {
            Diagnostic(),
            Diagnostic(new()),
            Diagnostic(new(stage: "stage")),
            Diagnostic(new(stage: "stage", subject: "subject")),
            Diagnostic(new(stage: "stage", subject: "subject", relatedLocations: ["/a"])),
            Diagnostic(new(
                stage: "stage",
                subject: "subject",
                relatedLocations: ["/a"],
                sourceReferences: ["source"])),
            Diagnostic(new(
                stage: "stage",
                subject: "subject",
                relatedLocations: ["/a"],
                sourceReferences: ["source"],
                resolutionOptions: ["resolve"])),
            Diagnostic(new(
                stage: "stage",
                subject: "subject",
                relatedLocations: ["/a"],
                sourceReferences: ["source"],
                resolutionOptions: ["resolve"],
                expected: "expected")),
            Diagnostic(new(
                stage: "stage",
                subject: "subject",
                relatedLocations: ["/a"],
                sourceReferences: ["source"],
                resolutionOptions: ["resolve"],
                expected: "expected",
                observed: "observed"))
        };
        var shuffled = expected.Reverse().ToArray();

        Array.Sort(shuffled, DocumentValidationDiagnosticComparer.Ordinal);

        Assert.Equal(expected, shuffled);
        Assert.Equal(
            0,
            DocumentValidationDiagnosticComparer.Ordinal.Compare(
                Diagnostic(new(relatedLocations: ["/b", "/a"])),
                Diagnostic(new(relatedLocations: ["/a", "/b"]))));

        var earlierMessageWithLaterEvidence = new DocumentValidationDiagnostic(
            "test.code",
            DiagnosticSeverity.Error,
            "A",
            Evidence: new(stage: "z"));
        var laterMessageWithoutEvidence = new DocumentValidationDiagnostic(
            "test.code",
            DiagnosticSeverity.Error,
            "B");
        Assert.True(DocumentValidationDiagnosticComparer.Ordinal.Compare(
            earlierMessageWithLaterEvidence,
            laterMessageWithoutEvidence) < 0);
    }

    static DocumentValidationDiagnostic Diagnostic(DocumentDiagnosticEvidence? evidence = null) =>
        new(
            "test.code",
            DiagnosticSeverity.Error,
            "Message",
            "/definition",
            "/transition",
            evidence);
}
