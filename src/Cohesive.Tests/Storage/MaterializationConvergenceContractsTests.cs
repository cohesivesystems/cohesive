using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationConvergenceContractsTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    static readonly QualifiedShapeId Shape = new(new("tests"), new("Item"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan = new(
        algorithm: "sha256",
        canonicalization: "tests/convergence-physical/v1",
        value: "0123456789abcdef");
    static readonly RelationQuerySourceInstanceId Source = new("tests/convergence-source");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        id: new("tests/convergence-placement"),
        input: new("source/items"),
        node: new("node/source"),
        binding: new("binding/source"),
        shape: Shape,
        source: Source,
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new RelationQuerySourceIdentityBinding(Shape, "id"));

    [Fact]
    public void Receipt_NormalizesFeedsAndFingerprintsCanonicalContent()
    {
        var feedA = Feed("feed-a", "a");
        var feedB = Feed("feed-b", "b");

        var left = Receipt([feedB, feedA]);
        var right = Receipt([feedA, feedB]);

        Assert.True(left.IsValid);
        Assert.Equal(["feed-a", "feed-b"], left.Feeds.Select(static feed => feed.Feed.Value));
        Assert.Equal(left.Fingerprint, right.Fingerprint);
        Assert.Equal(
            MaterializationConvergenceReceiptJsonSerializer.GetCanonicalBytes(left),
            MaterializationConvergenceReceiptJsonSerializer.GetCanonicalBytes(right));

        var json = MaterializationConvergenceReceiptJsonSerializer.Serialize(
            left,
            PortableDocumentJsonFormatting.Indented);
        var roundTripped = MaterializationConvergenceReceiptJsonSerializer.Deserialize(json);

        Assert.Equal(left, roundTripped);
        Assert.Equal(left.Feeds.Select(static feed => feed.Feed), roundTripped.Feeds.Select(static feed => feed.Feed));
    }

    [Fact]
    public void FeedEvidence_RejectsSettlementThatDoesNotCoverExactCheckpoint()
    {
        var scope = Scope("a");
        var position = new MaterializationSourcePosition(
            formatVersion: 1,
            scope: scope,
            value: "position-a");
        var wrongSettlement = new MaterializationSourceSettlement(
            id: new("settlement-a"),
            checkpoint: new("checkpoint-other"),
            position: position,
            settledAtUtc: Epoch.AddSeconds(3));

        Assert.Throws<ArgumentException>(() => new MaterializationCatchUpFeedEvidence(
            feed: new("feed-a"),
            scope: scope,
            latestChangeCheckpoint: new("checkpoint-a"),
            throughPosition: position,
            caughtUpReadStartedAtUtc: Epoch,
            caughtUpReadCompletedAtUtc: Epoch.AddSeconds(1),
            checkpointCommittedAtUtc: Epoch.AddSeconds(2),
            settlementRequirement: MaterializationConvergenceSettlementRequirement.NotRequired,
            settlement: wrongSettlement));
    }

    [Fact]
    public void NoOpCaughtUpRead_AllowsAlreadyDurableCheckpointAndSettlement()
    {
        var scope = Scope("a");
        var checkpoint = new MaterializationCheckpointId("checkpoint-a");
        var position = new MaterializationSourcePosition(
            formatVersion: 1,
            scope: scope,
            value: "position-a");
        var settlement = new MaterializationSourceSettlement(
            id: new("settlement-a"),
            checkpoint: checkpoint,
            position: position,
            settledAtUtc: Epoch.AddSeconds(-1));
        var feed = new MaterializationCatchUpFeedEvidence(
            feed: new("feed-a"),
            scope: scope,
            latestChangeCheckpoint: checkpoint,
            throughPosition: position,
            caughtUpReadStartedAtUtc: Epoch,
            caughtUpReadCompletedAtUtc: Epoch.AddSeconds(1),
            checkpointCommittedAtUtc: Epoch.AddSeconds(-2),
            settlementRequirement: MaterializationConvergenceSettlementRequirement.Explicit,
            settlement: settlement);

        var receipt = Receipt(
            [feed],
            evaluatedAtUtc: Epoch.AddSeconds(2),
            freshness: new(maximumLagMilliseconds: 5_000, maximumUnsettledMilliseconds: 5_000));

        Assert.True(receipt.IsValid);
    }

    [Fact]
    public void Receipt_RejectsDuplicateFeedAndAliasedScopeEvidence()
    {
        var feed = Feed("feed-a", "a");
        var duplicateId = Feed("feed-a", "b");
        var aliasedScope = Feed("feed-b", "a");

        Assert.Throws<ArgumentException>(() => Receipt([feed, duplicateId]));
        Assert.Throws<ArgumentException>(() => Receipt([feed, aliasedScope]));
    }

    [Fact]
    public void Receipt_RequiresSettlementOnlyForFeedsWithSeparateSettlementObligation()
    {
        var feed = Feed("feed-a", "a", includeSettlement: false);

        var optional = Receipt(
            [feed],
            freshness: new(maximumLagMilliseconds: 10_000));
        var sourceRequired = Receipt(
            [Feed(
                "feed-a",
                "a",
                settlementRequirement: MaterializationConvergenceSettlementRequirement.Explicit)],
            freshness: new(maximumLagMilliseconds: 10_000));
        var noSeparateSettlement = Receipt(
            [feed],
            freshness: new(maximumLagMilliseconds: 10_000, maximumUnsettledMilliseconds: 5_000));

        Assert.True(optional.IsValid);
        Assert.False(sourceRequired.IsValid);
        Assert.True(noSeparateSettlement.IsValid);
        Assert.Contains(
            sourceRequired.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationConvergenceDiagnosticCodes.SettlementMissing);
        Assert.False(sourceRequired.ValidateFreshness(Epoch.AddSeconds(4)).IsValid);
    }

    [Fact]
    public void Receipt_FailsWhenSettlementExceedsMaximumUnsettledAge()
    {
        var feed = Feed(
            "feed-a",
            "a",
            includeSettlement: true,
            settlementAtUtc: Epoch.AddSeconds(10),
            settlementRequirement: MaterializationConvergenceSettlementRequirement.Explicit);
        var receipt = Receipt(
            [feed],
            evaluatedAtUtc: Epoch.AddSeconds(11),
            freshness: new(maximumLagMilliseconds: 20_000, maximumUnsettledMilliseconds: 5_000));

        Assert.False(receipt.IsValid);
        Assert.Contains(
            receipt.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationConvergenceDiagnosticCodes.UnsettledAgeExceeded);
    }

    [Fact]
    public void ValidateFreshness_FailsAfterReceiptAndSourceHeadProofAgePastDemand()
    {
        var receipt = Receipt(
            [Feed("feed-a", "a")],
            evaluatedAtUtc: Epoch.AddSeconds(4),
            freshness: new(maximumLagMilliseconds: 5_000));

        Assert.True(receipt.IsValid);

        var validation = receipt.ValidateFreshness(Epoch.AddSeconds(10));

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationConvergenceDiagnosticCodes.LagExceeded);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationConvergenceDiagnosticCodes.ProofStale);
    }

    [Fact]
    public void Receipt_DecisionFailsWhenAlreadyBeyondMaximumLag()
    {
        var receipt = Receipt(
            [Feed("feed-a", "a")],
            evaluatedAtUtc: Epoch.AddSeconds(6),
            freshness: new(maximumLagMilliseconds: 5_000));

        Assert.False(receipt.IsValid);
        Assert.Contains(
            receipt.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationConvergenceDiagnosticCodes.LagExceeded);
    }

    [Fact]
    public void JsonDeserializer_RejectsForgedFingerprint()
    {
        var receipt = Receipt([Feed("feed-a", "a")]);
        var json = MaterializationConvergenceReceiptJsonSerializer.Serialize(
            receipt,
            PortableDocumentJsonFormatting.Compact);
        var forgedValue = receipt.Fingerprint.Value[0] == '0'
            ? $"1{receipt.Fingerprint.Value[1..]}"
            : $"0{receipt.Fingerprint.Value[1..]}";
        var forged = json.Replace(receipt.Fingerprint.Value, forgedValue, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => MaterializationConvergenceReceiptJsonSerializer.Deserialize(forged));
    }

    static MaterializationConvergenceReceipt Receipt(
        ImmutableArray<MaterializationCatchUpFeedEvidence> feeds,
        DateTimeOffset? evaluatedAtUtc = null,
        MaterializationFreshnessPolicy? freshness = null) =>
        new(
            schemaVersion: MaterializationConvergenceReceipt.CurrentSchemaVersion,
            synchronization: new(
                materialization: new("tests/materialization"),
                definitionFingerprint: new(
                    algorithm: "sha256",
                    canonicalization: "tests/materialization-definition/v1",
                    value: "0123456789abcdef"),
                rebuildPlanFingerprint: new(
                    algorithm: "sha256",
                    canonicalization: "tests/materialization-rebuild/v1",
                    value: "abcdef0123456789"),
                impactPlanFingerprint: new(
                    algorithm: "sha256",
                    canonicalization: "tests/materialization-impact/v1",
                    value: "0123456789abcdef"),
                generation: new("generation-a")),
            feeds: feeds,
            evaluatedAtUtc: evaluatedAtUtc ?? Epoch.AddSeconds(4),
            freshnessDemand: freshness ?? new(maximumLagMilliseconds: 10_000),
            validation: DocumentValidationResult.Valid);

    static MaterializationCatchUpFeedEvidence Feed(
        string feed,
        string suffix,
        bool includeSettlement = false,
        DateTimeOffset? settlementAtUtc = null,
        MaterializationConvergenceSettlementRequirement settlementRequirement = MaterializationConvergenceSettlementRequirement.NotRequired)
    {
        var scope = Scope(suffix);
        var checkpoint = new MaterializationCheckpointId($"checkpoint-{suffix}");
        var position = new MaterializationSourcePosition(
            formatVersion: 1,
            scope: scope,
            value: $"position-{suffix}");
        var settlement = includeSettlement
            ? new MaterializationSourceSettlement(
                id: new($"settlement-{suffix}"),
                checkpoint: checkpoint,
                position: position,
                settledAtUtc: settlementAtUtc ?? Epoch.AddSeconds(3))
            : null;
        return new(
            feed: new(feed),
            scope: scope,
            latestChangeCheckpoint: checkpoint,
            throughPosition: position,
            caughtUpReadStartedAtUtc: Epoch,
            caughtUpReadCompletedAtUtc: Epoch.AddSeconds(1),
            checkpointCommittedAtUtc: Epoch.AddSeconds(2),
            settlementRequirement: settlementRequirement,
            settlement: settlement);
    }

    static MaterializationSourceScope Scope(string suffix) =>
        new(
            physicalPlan: PhysicalPlan,
            placement: Placement,
            partition: new($"partition-{suffix}"),
            orderingScope: new($"ordering-{suffix}"));
}
