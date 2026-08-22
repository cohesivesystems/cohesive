using System.Collections.Immutable;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Control;

enum MaterializationHarnessExpectedOutcome
{
    Success = 0,
    SuccessWithRecovery = 1,
    ExpectedFailure = 2
}

enum MaterializationHarnessCompatibilityDriftKind
{
    Cursor = 0,
    Generation = 1,
    Plan = 2,
    Schema = 3,
    StorageBinding = 4
}

sealed record MaterializationHarnessCompatibilityDriftCell(
    MaterializationHarnessCompatibilityDriftKind Kind,
    string WireName,
    string Authority,
    string ExpectedDisposition,
    string ExpectedDiagnosticCode,
    ProcessRecoveryPolicy RequiredControlAction);

sealed record MaterializationHarnessMatrixCellExpectation(
    string CellId,
    MaterializationHarnessExpectedOutcome ExpectedOutcome,
    ProcessRecoveryPolicy? RequiredControlAction);

static class MaterializationHarnessMatrixCatalog
{
    internal static ImmutableArray<string> SourceProviders { get; } = ["cosmos", "postgres"];

    internal static ImmutableArray<MaterializationHarnessElasticFaultKind> ElasticFaults { get; } =
    [
        MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss,
        MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure,
        MaterializationHarnessElasticFaultKind.RetryableBulkRejection
    ];

    internal static ImmutableArray<MaterializationHarnessCompatibilityDriftCell> CompatibilityDrifts { get; } =
    [
        new(
            Kind: MaterializationHarnessCompatibilityDriftKind.Cursor,
            WireName: "cursor",
            Authority: "synchronization-work",
            ExpectedDisposition: MaterializationSynchronizationWorkMutationDisposition.IdentityConflict.ToString(),
            ExpectedDiagnosticCode: MaterializationSynchronizationWorkDiagnosticCodes.IdentityConflict,
            RequiredControlAction: ProcessRecoveryPolicy.RestartAttempt),
        new(
            Kind: MaterializationHarnessCompatibilityDriftKind.Generation,
            WireName: "generation",
            Authority: "synchronization-work",
            ExpectedDisposition: MaterializationSynchronizationWorkMutationDisposition.NotFound.ToString(),
            ExpectedDiagnosticCode: MaterializationSynchronizationWorkDiagnosticCodes.NotFound,
            RequiredControlAction: ProcessRecoveryPolicy.RestartAttempt),
        new(
            Kind: MaterializationHarnessCompatibilityDriftKind.Plan,
            WireName: "plan",
            Authority: "synchronization-work",
            ExpectedDisposition: MaterializationSynchronizationWorkMutationDisposition.NotFound.ToString(),
            ExpectedDiagnosticCode: MaterializationSynchronizationWorkDiagnosticCodes.NotFound,
            RequiredControlAction: ProcessRecoveryPolicy.RestartAttempt),
        new(
            Kind: MaterializationHarnessCompatibilityDriftKind.Schema,
            WireName: "schema",
            Authority: "synchronization-work",
            ExpectedDisposition: MaterializationSynchronizationWorkMutationDisposition.IdentityConflict.ToString(),
            ExpectedDiagnosticCode: MaterializationSynchronizationWorkDiagnosticCodes.IdentityConflict,
            RequiredControlAction: ProcessRecoveryPolicy.RestartAttempt),
        new(
            Kind: MaterializationHarnessCompatibilityDriftKind.StorageBinding,
            WireName: "storage-binding",
            Authority: "progress",
            ExpectedDisposition: MaterializationProgressMutationDisposition.NotFound.ToString(),
            ExpectedDiagnosticCode: MaterializationProgressDiagnosticCodes.NotFound,
            RequiredControlAction: ProcessRecoveryPolicy.RestartAttempt)
    ];

    internal static MaterializationHarnessCompatibilityDriftCell GetCompatibilityDrift(string wireName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wireName);
        return CompatibilityDrifts.SingleOrDefault(cell => string.Equals(
                cell.WireName,
                wireName,
                StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unsupported compatibility drift '{wireName}'.", nameof(wireName));
    }

    internal static string ElasticWireName(MaterializationHarnessElasticFaultKind fault) => fault switch
    {
        MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss =>
            "applied-promotion-response-loss",
        MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure =>
            "permanent-bulk-item-failure",
        MaterializationHarnessElasticFaultKind.RetryableBulkRejection =>
            "retryable-bulk-rejection",
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unsupported Elastic failure cell.")
    };

    internal static MaterializationHarnessElasticFaultKind GetElasticFault(string wireName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wireName);
        return ElasticFaults.Single(fault => string.Equals(
            ElasticWireName(fault),
            wireName,
            StringComparison.Ordinal));
    }

    internal static MaterializationHarnessExpectedOutcome ElasticExpectedOutcome(
        MaterializationHarnessElasticFaultKind fault) => fault switch
    {
        MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure =>
            MaterializationHarnessExpectedOutcome.ExpectedFailure,
        MaterializationHarnessElasticFaultKind.RetryableBulkRejection
            or MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss =>
            MaterializationHarnessExpectedOutcome.SuccessWithRecovery,
        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unsupported Elastic failure cell.")
    };

    internal static ImmutableArray<MaterializationHarnessMatrixCellExpectation> ExpectedAggregateCells { get; } =
    [
        .. SourceProviders.Select(static provider => new MaterializationHarnessMatrixCellExpectation(
                CellId: $"source/{provider}",
                ExpectedOutcome: MaterializationHarnessExpectedOutcome.Success,
                RequiredControlAction: null))
            .Concat(ElasticFaults.Select(static fault => new MaterializationHarnessMatrixCellExpectation(
                CellId: $"elastic/{ElasticWireName(fault)}",
                ExpectedOutcome: ElasticExpectedOutcome(fault),
                RequiredControlAction: null)))
            .Concat(SourceProviders.SelectMany(provider => CompatibilityDrifts.Select(
                drift => new MaterializationHarnessMatrixCellExpectation(
                    CellId: $"drift/{provider}/{drift.WireName}",
                    ExpectedOutcome: MaterializationHarnessExpectedOutcome.ExpectedFailure,
                    RequiredControlAction: drift.RequiredControlAction))))
            .OrderBy(static cell => cell.CellId, StringComparer.Ordinal)
    ];

    internal static ImmutableArray<string> ExpectedAggregateCellIds { get; } =
    [
        .. ExpectedAggregateCells.Select(static cell => cell.CellId)
    ];

    internal static MaterializationHarnessMatrixCellExpectation GetAggregateCell(string cellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellId);
        return ExpectedAggregateCells.SingleOrDefault(cell => string.Equals(
                cell.CellId,
                cellId,
                StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unsupported aggregate matrix cell '{cellId}'.", nameof(cellId));
    }
}

sealed record MaterializationHarnessCompatibilityDriftProbeResult(
    int SchemaVersion,
    string Provider,
    MaterializationHarnessCompatibilityDriftKind Kind,
    string Authority,
    string CanonicalGeneration,
    string DriftedIdentity,
    string ExpectedDisposition,
    string ActualDisposition,
    ImmutableArray<string> DiagnosticCodes,
    Cohesive.Processes.IR.ProcessRecoveryPolicy RequiredControlAction,
    string BeforeAuthorityRevision,
    string AfterAuthorityRevision,
    string BeforeAuthorityFence,
    string AfterAuthorityFence,
    string BeforeTargetRevision,
    string AfterTargetRevision,
    string? BeforeActiveGeneration,
    string? AfterActiveGeneration,
    bool CanonicalAuthorityPreserved,
    bool DriftedAuthorityAbsent,
    bool TargetAuthorityPreserved);
