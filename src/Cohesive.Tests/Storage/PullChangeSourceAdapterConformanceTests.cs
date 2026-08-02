using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class PullChangeSourceAdapterConformanceTests
{
    static readonly ImmutableArray<PullChangeSourceConformanceCase> Cases =
    [
        Postgres.PostgresRelationQuerySourceReaderTests.CreatePullChangeSourceConformanceCase(),
        Cosmos.CosmosMaterializationSourceTests.CreatePullChangeSourceConformanceCase()
    ];

    [Fact]
    public async Task BaselinePage_IsBoundedAndExactContinuationReplayIsStable()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveBaselineReplayAsync();

            Assert.InRange(observed.First.Read.Observations.Length, 1, observed.MaximumItems);
            Assert.InRange(CanonicalByteCount(observed.First.Read.Observations), 1, observed.MaximumBytes);
            Assert.Equal(MaterializationSourcePageState.MoreAvailable, observed.First.State);
            var continuation = Assert.IsType<MaterializationSourceContinuation>(observed.First.Continuation);
            Assert.Equal(observed.First.Scope, continuation.Scope);
            Assert.Equal(observed.First.ReadFingerprint, continuation.ReadFingerprint);

            Assert.InRange(observed.Resumed.Read.Observations.Length, 1, observed.MaximumItems);
            Assert.InRange(CanonicalByteCount(observed.Resumed.Read.Observations), 1, observed.MaximumBytes);
            Assert.Equal(observed.First.Scope, observed.Resumed.Scope);
            Assert.Equal(observed.First.ReadFingerprint, observed.Resumed.ReadFingerprint);
            AssertBaselinePagesEqual(observed.Resumed, observed.Replayed, item.Adapter);
            Assert.True(observed.ProviderReadAttempts > 0, item.Adapter);
        }
    }

    [Fact]
    public async Task PositionedChangeRead_IsBoundedAndRedeliveryRetainsIdentityWithoutImplicitSettlement()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObservePositionedRedeliveryAsync();

            Assert.Equal(observed.Scope, observed.CapturedPosition.Scope);
            Assert.NotEqual(observed.CapturedPosition, observed.Initial.ThroughPosition);
            Assert.InRange(observed.Initial.Deliveries.Length, 1, observed.MaximumDeliveries);
            Assert.InRange(observed.Redelivered.Deliveries.Length, 1, observed.MaximumDeliveries);
            Assert.InRange(CanonicalByteCount(observed.Initial.Deliveries), 1, observed.MaximumBytes);
            Assert.InRange(CanonicalByteCount(observed.Redelivered.Deliveries), 1, observed.MaximumBytes);
            var initial = Assert.Single(observed.Initial.Deliveries);
            var redelivered = Assert.Single(observed.Redelivered.Deliveries);
            Assert.Equal(observed.Scope, initial.Change.Scope);
            Assert.Equal(observed.Scope, redelivered.Change.Scope);
            Assert.NotNull(initial.Change.Position);
            Assert.Equal(initial.Id, redelivered.Id);
            Assert.Equal(initial.Change.Id, redelivered.Change.Id);
            Assert.Equal(initial.Change.Position, redelivered.Change.Position);
            Assert.Equal(initial.Change.SubjectIdentity, redelivered.Change.SubjectIdentity);
            Assert.Equal(initial.Change.Kind, redelivered.Change.Kind);
            Assert.Equal(CanonicalBytes(initial.Change.Before), CanonicalBytes(redelivered.Change.Before));
            Assert.Equal(CanonicalBytes(initial.Change.After), CanonicalBytes(redelivered.Change.After));
            Assert.Equal(observed.Initial.ThroughPosition, observed.Redelivered.ThroughPosition);
            Assert.Equal(
                observed.ProviderSettlementAttemptsBeforeReads,
                observed.ProviderSettlementAttemptsAfterReads);
        }
    }

    [Fact]
    public async Task ExplicitSettlement_IsIdempotentOrExplicitlyUnsupported()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveExplicitSettlementAsync();
            Assert.True(
                item.SettlementCapability is null or MaterializationCapabilityKind.SourceSettlement,
                item.Adapter);
            var settlementExpected = item.SettlementCapability == MaterializationCapabilityKind.SourceSettlement;

            Assert.Equal(
                settlementExpected,
                observed.SettlementPortAvailable);
            Assert.Equal(settlementExpected, observed.SettlementCapabilityAdvertised);
            if (!settlementExpected)
            {
                Assert.Null(observed.Acknowledged);
                Assert.Null(observed.Replayed);
                Assert.Equal(0, observed.ProviderSettlementAttempts);
                Assert.Equal(observed.ProviderSettlementStateBefore, observed.ProviderSettlementStateAfter);
                continue;
            }

            Assert.Equal(
                MaterializationSourceSettlementDisposition.Acknowledged,
                Assert.IsType<MaterializationSourceSettlementResult>(observed.Acknowledged).Disposition);
            Assert.Equal(
                MaterializationSourceSettlementDisposition.Replayed,
                Assert.IsType<MaterializationSourceSettlementResult>(observed.Replayed).Disposition);
            Assert.Equal(observed.Acknowledged.Receipt, observed.Replayed.Receipt);
            Assert.Equal(1, observed.ProviderSettlementAttempts);
            Assert.NotEqual(observed.ProviderSettlementStateBefore, observed.ProviderSettlementStateAfter);
        }
    }

    [Fact]
    public async Task Cancellation_LeavesProviderReadsAndSettlementStateUnchanged()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveCancellationAsync();

            Assert.True(observed.ChangeReadCancellationObserved, item.Adapter);
            Assert.Equal(observed.ProviderReadAttemptsBefore, observed.ProviderReadAttemptsAfter);
            Assert.Equal(
                observed.ProviderSettlementAttemptsBefore,
                observed.ProviderSettlementAttemptsAfter);
            Assert.Equal(observed.ProviderSettlementStateBefore, observed.ProviderSettlementStateAfter);
            Assert.Equal(
                item.SettlementCapability == MaterializationCapabilityKind.SourceSettlement,
                observed.SettlementCancellationObserved);
        }
    }

    [Fact]
    public async Task ScopeReadAndPositionAffinity_MismatchesFailBeforeProviderIo()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveAffinityRejectionsAsync();

            Assert.True(observed.ScopeMismatchRejected, item.Adapter);
            Assert.True(observed.ReadMismatchRejected, item.Adapter);
            Assert.True(observed.PositionMismatchRejected, item.Adapter);
            Assert.Equal(observed.ProviderReadAttemptsBefore, observed.ProviderReadAttemptsAfter);
            Assert.Equal(
                observed.ProviderSettlementAttemptsBefore,
                observed.ProviderSettlementAttemptsAfter);
        }
    }

    static void AssertBaselinePagesEqual(
        MaterializationSourcePage expected,
        MaterializationSourcePage actual,
        string adapter)
    {
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.ReadFingerprint, actual.ReadFingerprint);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Continuation, actual.Continuation);
        Assert.Equal(expected.Read.State, actual.Read.State);
        Assert.Equal(expected.Read.Observations.Length, actual.Read.Observations.Length);
        for (var index = 0; index < expected.Read.Observations.Length; index++)
        {
            var expectedObservation = expected.Read.Observations[index];
            var actualObservation = actual.Read.Observations[index];
            Assert.Equal(expectedObservation.Identity, actualObservation.Identity);
            Assert.Equal(expectedObservation.Shape, actualObservation.Shape);
            Assert.Equal(expectedObservation.Fields.Length, actualObservation.Fields.Length);
            for (var fieldIndex = 0; fieldIndex < expectedObservation.Fields.Length; fieldIndex++)
            {
                Assert.Equal(expectedObservation.Fields[fieldIndex], actualObservation.Fields[fieldIndex]);
            }
        }

        Assert.True(expected.Read.Observations.Length > 0, adapter);
    }

    static long CanonicalByteCount<T>(ImmutableArray<T> items) where T : class
    {
        long total = 0;
        foreach (var item in items)
        {
            total = checked(total + StrictDocumentJson.GetCanonicalBytes(
                value: item,
                options: MaterializationJsonSerializer.CreateOptions()).LongLength);
        }

        return total;
    }

    static byte[]? CanonicalBytes<T>(T? item) where T : class => item is null
        ? null
        : StrictDocumentJson.GetCanonicalBytes(
            value: item,
            options: MaterializationJsonSerializer.CreateOptions());
}

internal static class PullChangeSourceConformanceInputs
{
    internal static MaterializationSourceContinuation RequireContinuation(MaterializationSourcePage page) =>
        page.Continuation
        ?? throw new InvalidOperationException("The conformance baseline did not return a continuation.");

    internal static MaterializationSourceScope ForeignScope(MaterializationSourceScope scope) => new(
        physicalPlan: scope.PhysicalPlan,
        placement: scope.Placement,
        partition: new MaterializationSourcePartitionId(string.Concat(scope.Partition.Value, "/foreign")),
        orderingScope: scope.OrderingScope);

    internal static RelationQuerySourceReadRequest AlternateRead(RelationQuerySourceReadRequest read) => new(
        physicalPlan: read.PhysicalPlan,
        stage: read.Stage,
        placementBinding: read.PlacementBinding,
        source: read.Source,
        shape: read.Shape,
        identitySelector: read.IdentitySelector,
        fields: read.Fields,
        constraint: read.Constraint,
        maximumBufferedRows: checked(read.MaximumBufferedRows + 1));
}

internal sealed record PullChangeSourceConformanceCase(
    string Adapter,
    MaterializationCapabilityKind? SettlementCapability,
    Func<Task<PullBaselineReplayObservation>> ObserveBaselineReplayAsync,
    Func<Task<PullPositionedRedeliveryObservation>> ObservePositionedRedeliveryAsync,
    Func<Task<PullExplicitSettlementObservation>> ObserveExplicitSettlementAsync,
    Func<Task<PullCancellationObservation>> ObserveCancellationAsync,
    Func<Task<PullAffinityRejectionObservation>> ObserveAffinityRejectionsAsync);

internal sealed record PullBaselineReplayObservation(
    int MaximumItems,
    long MaximumBytes,
    MaterializationSourcePage First,
    MaterializationSourcePage Resumed,
    MaterializationSourcePage Replayed,
    int ProviderReadAttempts);

internal sealed record PullPositionedRedeliveryObservation(
    MaterializationSourceScope Scope,
    MaterializationSourcePosition CapturedPosition,
    int MaximumDeliveries,
    long MaximumBytes,
    MaterializationChangePage Initial,
    MaterializationChangePage Redelivered,
    int ProviderSettlementAttemptsBeforeReads,
    int ProviderSettlementAttemptsAfterReads);

internal sealed record PullExplicitSettlementObservation(
    bool SettlementPortAvailable,
    bool SettlementCapabilityAdvertised,
    MaterializationSourceSettlementResult? Acknowledged,
    MaterializationSourceSettlementResult? Replayed,
    int ProviderSettlementAttempts,
    string ProviderSettlementStateBefore,
    string ProviderSettlementStateAfter);

internal sealed record PullCancellationObservation(
    bool ChangeReadCancellationObserved,
    bool SettlementCancellationObserved,
    int ProviderReadAttemptsBefore,
    int ProviderReadAttemptsAfter,
    int ProviderSettlementAttemptsBefore,
    int ProviderSettlementAttemptsAfter,
    string ProviderSettlementStateBefore,
    string ProviderSettlementStateAfter);

internal sealed record PullAffinityRejectionObservation(
    bool ScopeMismatchRejected,
    bool ReadMismatchRejected,
    bool PositionMismatchRejected,
    int ProviderReadAttemptsBefore,
    int ProviderReadAttemptsAfter,
    int ProviderSettlementAttemptsBefore,
    int ProviderSettlementAttemptsAfter);
