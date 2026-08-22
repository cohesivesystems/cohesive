using System.Text.Json.Nodes;
using Cohesive.MaterializationHarness.Model;

namespace Cohesive.Tests.Storage;

/// <summary>Deterministic resolution coverage for the shared real-container scenario authority.</summary>
public sealed class FreightScenarioJournalTests
{
    [Fact]
    public async Task JournalResolvesExactBaselineMutationTransactionsAndFinalState()
    {
        var journal = await FreightScenarioJournal.LoadAsync(ScenarioPath());
        var transitions = journal.MutationTransactions.SelectMany(static value => value.Transitions).ToArray();

        Assert.Equal("freight-incremental-v1", journal.ScenarioId);
        Assert.Equal(33, journal.BaselineThroughSequence);
        Assert.Equal(33, journal.Baseline.ThroughSequence);
        Assert.Equal(46, journal.Final.ThroughSequence);
        Assert.Equal(9, journal.MutationTransactions.Length);
        Assert.Equal(9, transitions.Length);
        Assert.Equal(7, journal.Baseline.Orders.Length);
        Assert.Equal(16, journal.Baseline.StopCount);
        Assert.Equal(6, journal.Final.Orders.Length);
        Assert.Equal(13, journal.Final.StopCount);

        var created = Assert.Single(transitions, static value => value.Sequence == 37);
        Assert.Equal(FreightScenarioEntityKind.Order, created.Entity);
        Assert.Equal(FreightScenarioOperationKind.Upsert, created.Operation);
        Assert.NotNull(created.BeforeState);
        Assert.NotNull(created.AfterState);
        Assert.Equal(4, created.Version);
        Assert.Equal(2, created.GetBefore<FreightOrder>()!.Stops.Length);
        Assert.Equal(3, created.GetAfter<FreightOrder>()!.Stops.Length);

        var deleted = Assert.Single(transitions, static value => value.Sequence == 38);
        Assert.Equal(FreightScenarioEntityKind.Order, deleted.Entity);
        Assert.Equal(FreightScenarioOperationKind.Upsert, deleted.Operation);
        Assert.Equal(3, deleted.GetBefore<FreightOrder>()!.Stops.Length);
        Assert.Equal(2, deleted.GetAfter<FreightOrder>()!.Stops.Length);
        Assert.Equal(5, deleted.Version);

        var atomicExchange = Assert.Single(
            journal.MutationTransactions,
            static value => value.Id == "stop-type-exchange");
        var exchange = Assert.Single(atomicExchange.Transitions);
        Assert.Equal(41, exchange.Sequence);
        Assert.Equal("acme", exchange.Key.TenantId);
        Assert.Equal("order-2002", exchange.Key.Id);
        Assert.Equal(FreightScenarioEntityKind.Order, exchange.Entity);
        Assert.Equal(4, exchange.Version);
        Assert.Equal("Pickup", exchange.GetBefore<FreightOrder>()!.Stops.Single(
            static stop => stop.Id == "stop-2002-pickup").StopType);
        Assert.Equal("Drop", exchange.GetAfter<FreightOrder>()!.Stops.Single(
            static stop => stop.Id == "stop-2002-pickup").StopType);

        var rootDelete = Assert.Single(transitions, static value => value.Sequence == 46);
        Assert.Equal(FreightScenarioEntityKind.Order, rootDelete.Entity);
        Assert.Equal(FreightScenarioOperationKind.Delete, rootDelete.Operation);
        Assert.Equal(3, rootDelete.GetBefore<FreightOrder>()!.Stops.Length);
        Assert.Null(rootDelete.GetAfter<FreightOrder>());

        var finalCustomer = Assert.Single(
            journal.Final.Customers,
            static value => value.TenantId == "northwind" && value.Id == "customer-grocery");
        Assert.Equal("Northwind Cold Chain Grocery", finalCustomer.DisplayName);
        Assert.Equal(
            2,
            journal.Final.GetVersion(
                FreightScenarioEntityKind.CustomerAccount,
                finalCustomer.TenantId,
                finalCustomer.Id));
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-01T10:00:46Z"),
            journal.Final.OccurredAtUtc);
    }

    [Fact]
    public async Task JournalResolutionProducesStableDeliveryIdentityAndFingerprint()
    {
        var first = await FreightScenarioJournal.LoadAsync(ScenarioPath());
        var second = await FreightScenarioJournal.LoadAsync(ScenarioPath());
        var firstTransitions = first.MutationTransactions.SelectMany(static value => value.Transitions).ToArray();
        var secondTransitions = second.MutationTransactions.SelectMany(static value => value.Transitions).ToArray();

        Assert.Equal(
            firstTransitions.Select(static value => value.DeliveryId),
            secondTransitions.Select(static value => value.DeliveryId));
        Assert.Equal(
            firstTransitions.Select(static value => value.Fingerprint),
            secondTransitions.Select(static value => value.Fingerprint));
        Assert.All(firstTransitions, static value => Assert.Equal(64, value.Fingerprint.Length));
        Assert.Equal(
            "scenario/freight-incremental-v1/operation/34",
            firstTransitions[0].DeliveryId);
    }

    [Fact]
    public async Task JournalRejectsAtomicTransactionThatCrossesCanonicalAggregates()
    {
        var source = JsonNode.Parse(await File.ReadAllTextAsync(ScenarioPath()))
            ?? throw new InvalidOperationException("The freight scenario JSON is empty.");
        var operations = source["operations"]?.AsArray()
            ?? throw new InvalidOperationException("The freight scenario has no operations.");
        operations.Single(operation => operation?["sequence"]?.GetValue<long>() == 39)!["transaction"] = "stop-delete";
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"cohesive-freight-scenario-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, source.ToJsonString());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FreightScenarioJournal.LoadAsync(temporaryPath));

            Assert.Contains("canonical aggregate", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    static string ScenarioPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "eng",
                "materialization-harness",
                "scenarios",
                "freight-baseline.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Cannot locate the freight materialization scenario journal.");
    }
}
