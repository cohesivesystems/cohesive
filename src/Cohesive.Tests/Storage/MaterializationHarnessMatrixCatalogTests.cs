using Cohesive.MaterializationHarness.Control;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationHarnessMatrixCatalogTests
{
    [Fact]
    public void AggregateCellsAreCanonicalCompleteAndStrictlyOrdered()
    {
        var cells = MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds;

        Assert.Equal(15, cells.Length);
        Assert.Equal(cells.Order(StringComparer.Ordinal), cells);
        Assert.Equal(cells.Length, cells.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("source/cosmos", cells);
        Assert.Contains("source/postgres", cells);
        Assert.Contains("elastic/applied-promotion-response-loss", cells);
        Assert.Contains("drift/cosmos/storage-binding", cells);
        Assert.Contains("drift/postgres/cursor", cells);
    }

    [Fact]
    public void CompatibilityCellsDeclareExactRejectedAuthorityOutcomes()
    {
        var cells = MaterializationHarnessMatrixCatalog.CompatibilityDrifts;

        Assert.Equal(
            ["cursor", "generation", "plan", "schema", "storage-binding"],
            cells.Select(static cell => cell.WireName));
        Assert.All(cells, static cell => Assert.False(string.IsNullOrWhiteSpace(cell.ExpectedDiagnosticCode)));
        Assert.All(cells, static cell => Assert.Equal(
            ProcessRecoveryPolicy.RestartAttempt,
            cell.RequiredControlAction));
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.IdentityConflict.ToString(),
            MaterializationHarnessMatrixCatalog.GetCompatibilityDrift("cursor").ExpectedDisposition);
        Assert.Equal(
            MaterializationSynchronizationWorkMutationDisposition.NotFound.ToString(),
            MaterializationHarnessMatrixCatalog.GetCompatibilityDrift("generation").ExpectedDisposition);
        Assert.Equal(
            MaterializationProgressMutationDisposition.NotFound.ToString(),
            MaterializationHarnessMatrixCatalog.GetCompatibilityDrift("storage-binding").ExpectedDisposition);
    }

    [Fact]
    public void AggregateExpectationsDistinguishSuccessRecoveryAndExpectedFailure()
    {
        Assert.Equal(
            MaterializationHarnessExpectedOutcome.Success,
            MaterializationHarnessMatrixCatalog.GetAggregateCell("source/postgres").ExpectedOutcome);
        Assert.Equal(
            MaterializationHarnessExpectedOutcome.SuccessWithRecovery,
            MaterializationHarnessMatrixCatalog.GetAggregateCell(
                "elastic/applied-promotion-response-loss").ExpectedOutcome);
        var drift = MaterializationHarnessMatrixCatalog.GetAggregateCell("drift/cosmos/schema");
        Assert.Equal(MaterializationHarnessExpectedOutcome.ExpectedFailure, drift.ExpectedOutcome);
        Assert.Equal(ProcessRecoveryPolicy.RestartAttempt, drift.RequiredControlAction);
    }

    [Fact]
    public void ElasticCellsExposeOneCanonicalWireIdentityAndExpectedOutcome()
    {
        Assert.Equal(
            [
                "applied-promotion-response-loss",
                "permanent-bulk-item-failure",
                "retryable-bulk-rejection"
            ],
            MaterializationHarnessMatrixCatalog.ElasticFaults.Select(
                MaterializationHarnessMatrixCatalog.ElasticWireName));
        Assert.Equal(
            MaterializationHarnessExpectedOutcome.ExpectedFailure,
            MaterializationHarnessMatrixCatalog.ElasticExpectedOutcome(
                MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure));
        Assert.Equal(
            MaterializationHarnessExpectedOutcome.SuccessWithRecovery,
            MaterializationHarnessMatrixCatalog.ElasticExpectedOutcome(
                MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss));
    }
}
