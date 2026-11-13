using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model.Serialization;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

/// <summary>Offline Tiingo-shaped acquisition, coverage evaluation and evidence attribution using the fluent model.</summary>
/// <remarks>This example interprets individual evidence boundaries with synthetic source and sink inputs.
/// It does not execute the compiled Process or qualify durable storage. Ito supplies the production adapter and stores.</remarks>
public sealed class TiingoCoverageEvidenceExamples
{
    static readonly ExecutionProvenance Origin = new(new("tiingo-evidence-example", "1"),
        new("examples/integrations/tiingo-coverage-evidence"), DocumentOrigin.Generated);
    const string Operation = "spy/2026-09-01-through-02";

    /// <summary>Only complete validated coverage and a confirmed matching publication can support progress.</summary>
    /// <param name="returnedSessions">Number of valid rows returned for two expected sessions.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CoverageAndPublicationEvidenceHaveSeparateBoundaries(int returnedSessions)
    {
        var selection = CreateSelection();
        var qualified = HistoricalQualificationExamples.Assess(selection);
        Assert.True(qualified.IsAdmissible);
        var compiled = qualified.Compilation!;
        var consumer = compiled.Binding!.GetDefinition<IngestionProviderBinding>().Consumer;
        Assert.All(qualified.Decisions, d => Assert.Equal(IntegrationQualificationStatus.RequiresRuntimeEvidence, d.Status));

        // Use the compiled selection as authority for the evaluator, just as a later worker would.
        var bound = compiled.Binding!.GetDefinition<IngestionProviderBinding>();
        var property = compiled.Documents.Single(d => d.Reference == bound.Contract.Properties[0].Property)
            .GetDefinition<Cohesive.Contracts.ContractPropertyDefinition>();
        var scopeDocument = compiled.Documents.Single(d => d.Reference == property.Parameters);
        var pinned = scopeDocument.GetDefinition<HistoricalCoverageScope>();
        var selectionBytes = Encoding.UTF8.GetBytes(scopeDocument.Definition.GetRawText());
        var transformation = Cohesive.Contracts.ContractPropertyDocuments.DeclareContext(
            new("example/tiingo/raw-policy"), new("v1"), pinned.Normalizer, Origin);
        var requested = IngestionWorkDocuments.Create(new IngestionAcquisitionRequest(
            consumer, bound.Contract.Ingestion, transformation.Reference, Operation, "attempt/1",
            IngestionContentReference.Describe("selection.json", selectionBytes, "application/json"),
            ExpectedLedgerRevision: 0, SelectedAtUtc: selection.RequestedAsOfUtc), Origin);

        // Simulated HTTP response. In a real host these exact bytes and receipt are durably retained first.
        var bytes = Response(returnedSessions);
        var content = IngestionContentReference.Describe("tiingo-response.json", bytes, "application/json");
        var acquired = IngestionWorkDocuments.Create(new IngestionAcquisitionReceipt(
            requested.Reference, content, selection.RequestedAsOfUtc.AddSeconds(1)), Origin);
        var evidence = Evaluate(pinned, content, bytes);
        Assert.Equal(2 - returnedSessions, evidence.MissingSessions.Length);
        Assert.Equal(selection.ExpectedSessions.Skip(returnedSessions).ToArray(), evidence.MissingSessions.ToArray());
        Assert.True(IngestionWorkDocuments.ValidateChain(requested, acquired).IsValid);
        // Evaluation is reproducible from the same retained bytes, including an incomplete response.
        Assert.Equal(JsonSerializer.Serialize(evidence), JsonSerializer.Serialize(Evaluate(pinned, content, bytes)));
        if (!evidence.IsComplete)
        {
            Assert.Throws<InvalidDataException>(() => Prepare(evidence));
            return; // No prepared publication and no ledger advancement can be produced by this example.
        }

        var publicationBytes = Prepare(evidence);
        var publication = IngestionContentReference.Describe("publication.json", publicationBytes, "application/json");
        var prepared = IngestionWorkDocuments.Create(new IngestionPreparedPublication(acquired.Reference,
            transformation.Reference, publication,
            new IngestionDateRangePosition(selection.StartDate, selection.EndDateInclusive.AddDays(1)),
            selection.RequestedAsOfUtc.AddSeconds(2)), Origin);
        Assert.True(IngestionWorkDocuments.ValidateChain(requested, acquired, prepared).IsValid);

        // The sink supplies this only after confirming a commit. Coverage alone is insufficient.
        Assert.Throws<ArgumentNullException>(() => IngestionWorkDocuments.CreateLedgerAdvance(
            requested, acquired, prepared, receipt: null!, Origin));
        var confirmed = new IngestionPublicationReceipt(Operation, publication.Sha256, "example-sink/receipt/1");
        Assert.Throws<ArgumentException>(() => IngestionWorkDocuments.CreateLedgerAdvance(requested, acquired,
            prepared, confirmed with { PublicationId = "different-operation" }, Origin));
        Assert.Throws<ArgumentException>(() => IngestionWorkDocuments.CreateLedgerAdvance(requested, acquired,
            prepared, confirmed with { ContentFingerprint = publication.Sha256 + "-different" }, Origin));
        var advance = IngestionWorkDocuments.CreateLedgerAdvance(requested, acquired, prepared, confirmed, Origin);
        var accepted = IngestionLedgerReduction.Evaluate(current: null, priorReceipt: null, advance);
        Assert.Equal(IngestionLedgerDisposition.Advanced, accepted.Disposition);
        Assert.Equal(advance.Reference, accepted.Receipt!.Advance);
        var replay = IngestionLedgerReduction.Evaluate(accepted.Receipt.Entry, accepted.Receipt, advance);
        Assert.Equal(IngestionLedgerDisposition.Replayed, replay.Disposition);
        Assert.Equal(accepted.Receipt, replay.Receipt);
    }

    /// <summary>Invalid observations cannot establish coverage even when their source bytes match the declared digest.</summary>
    /// <param name="variant">Invalid row or evidence variant.</param>
    [Theory]
    [InlineData("duplicate-session")]
    [InlineData("unexpected-session")]
    [InlineData("invalid-price")]
    [InlineData("tampered-bytes")]
    public void InvalidEvidenceCannotEstablishCoverage(string variant)
    {
        var bytes = Response(2);
        if (variant == "duplicate-session")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("2026-09-02", "2026-09-01", StringComparison.Ordinal));
        if (variant == "unexpected-session")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("2026-09-02", "2026-09-03", StringComparison.Ordinal));
        if (variant == "invalid-price")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("105", "-1", StringComparison.Ordinal));
        var content = IngestionContentReference.Describe("response.json", bytes, "application/json");
        if (variant == "tampered-bytes") bytes[1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Evaluate(CreateSelection(), content, bytes));
    }

    // A deliberately small domain interpreter for this synthetic raw-price fixture, not a production Tiingo adapter.
    /// <summary>Checks retained synthetic rows against the exact expected session scope.</summary>
    /// <param name="selection">Pinned historical scope.</param>
    /// <param name="retained">Digest and length of the retained response.</param>
    /// <param name="bytes">Response bytes to verify and evaluate.</param>
    /// <returns>Validated rows and unresolved expected sessions.</returns>
    /// <exception cref="InvalidDataException">Evidence, normalization or rows violate the fixture contract.</exception>
    internal static CoverageEvidence Evaluate(HistoricalCoverageScope selection, IngestionContentReference retained, ReadOnlyMemory<byte> bytes)
    {
        if (!retained.Matches(bytes.Span)) throw new InvalidDataException("Retained response digest or length differs.");
        if (selection.Normalizer != CreateSelection().Normalizer)
            throw new InvalidDataException("This evaluator requires the example's exact raw-normalization policy.");
        var expected = selection.ExpectedSessions.ToHashSet();
        var observed = new HashSet<DateOnly>();
        var bars = ImmutableArray.CreateBuilder<DailyBar>();
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected daily-price rows.");
        foreach (var row in json.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a daily-price object.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
                if (!fields.Add(field.Name)) throw new InvalidDataException("Duplicate response field.");
            var bar = row.Deserialize<DailyBar>() ?? throw new InvalidDataException("Missing daily price.");
            var date = DateOnly.FromDateTime(bar.Date.UtcDateTime);
            if (bar.Date.Offset != TimeSpan.Zero || bar.Date.TimeOfDay != TimeSpan.Zero
                || !expected.Contains(date) || !observed.Add(date))
                throw new InvalidDataException("Duplicate, unexpected or invalid session date.");
            if (bar.Low <= 0 || bar.Low > bar.High || bar.Open < bar.Low || bar.Open > bar.High
                || bar.Close < bar.Low || bar.Close > bar.High || bar.Volume < 0)
                throw new InvalidDataException("Invalid raw OHLCV values.");
            bars.Add(bar);
        }
        return new([.. bars.OrderBy(b => b.Date)], [.. expected.Except(observed).Order()]);
    }

    /// <summary>Produces local publication bytes only for complete source coverage.</summary>
    /// <param name="evidence">Validated source observations.</param>
    /// <returns>Serialized ordered bars.</returns>
    /// <exception cref="InvalidDataException">Expected sessions remain unresolved.</exception>
    internal static byte[] Prepare(CoverageEvidence evidence)
    {
        if (!evidence.IsComplete) throw new InvalidDataException("Missing sessions remain unresolved; publication is blocked.");
        return JsonSerializer.SerializeToUtf8Bytes(evidence.Bars);
    }

    /// <summary>Creates an offline daily-price response.</summary>
    /// <param name="sessions">Number of consecutive fixture sessions starting September 1.</param>
    /// <returns>Synthetic JSON response bytes.</returns>
    internal static byte[] Response(int sessions) => JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, sessions)
        .Select(day => new DailyBar(new(2026, 9, day + 1, 0, 0, 0, TimeSpan.Zero), 100, 110, 90, 105, 1000)).ToArray());

    /// <summary>Minimal raw daily-price wire payload used by the offline fixture.</summary>
    /// <param name="Date">UTC-midnight session date.</param>
    /// <param name="Open">Raw opening price.</param>
    /// <param name="High">Raw session high.</param>
    /// <param name="Low">Raw session low.</param>
    /// <param name="Close">Raw closing price.</param>
    /// <param name="Volume">Nonnegative session volume.</param>
    internal sealed record DailyBar([property: JsonPropertyName("date"), JsonRequired] DateTimeOffset Date,
        [property: JsonPropertyName("open"), JsonRequired] decimal Open, [property: JsonPropertyName("high"), JsonRequired] decimal High,
        [property: JsonPropertyName("low"), JsonRequired] decimal Low, [property: JsonPropertyName("close"), JsonRequired] decimal Close,
        [property: JsonPropertyName("volume"), JsonRequired] decimal Volume);

    /// <summary>Derived source-coverage observation; acceptance still needs sink evidence.</summary>
    /// <param name="Bars">Validated rows ordered by session.</param>
    /// <param name="MissingSessions">Expected dates without observations.</param>
    internal sealed record CoverageEvidence(ImmutableArray<DailyBar> Bars, ImmutableArray<DateOnly> MissingSessions)
    {
        /// <summary>Whether the source covers every expected session.</summary>
        public bool IsComplete => MissingSessions.IsEmpty;
    }
}
