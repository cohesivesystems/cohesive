using Cohesive.Model;
using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Publication/ledger recovery evidence required by the separate-ledger profile; declarations are not physical qualification.</summary>
/// <remarks>Validation covers these obligations only. Source completeness, cursor validity, settlement safety,
/// binding to exact contracts and physical adapter conformance require separate evidence. Successful Process lowering remains unqualified.</remarks>
/// <param name="RetainedPreparedInput">Exact input and publication identity survive interruption before sink dispatch.</param>
/// <param name="ExactPublicationReplay">Sink retries return original outcomes without reapplying or overwriting later state.</param>
/// <param name="AtomicLedgerAndReceipt">Progress CAS and independent advancement receipt commit atomically.</param>
/// <param name="DefinitiveReceiptBeforeRevision">Ledger either checks definitive original receipts before CAS or returns Unknown under weak visibility.</param>
/// <param name="PublicationReceiptRetention">Guaranteed sink receipt horizon; null means unqualified.</param>
/// <param name="LedgerReceiptRetention">Guaranteed ledger receipt horizon; null means unqualified.</param>
/// <param name="PreparedInputRetention">Guaranteed retained-input horizon; null means unqualified.</param>
public sealed record IngestionRecoveryCapabilities(bool RetainedPreparedInput, bool ExactPublicationReplay,
    bool AtomicLedgerAndReceipt, bool DefinitiveReceiptBeforeRevision, TimeSpan? PublicationReceiptRetention,
    TimeSpan? LedgerReceiptRetention, TimeSpan? PreparedInputRetention)
{
    /// <summary>Explains unsupported recovery guarantees without I/O or silently weakening the requested horizon.</summary>
    /// <param name="requiredRecoveryHorizon">Positive maximum supported interruption/retry duration.</param>
    /// <returns>Structured errors for missing guarantees and insufficient/unknown retention.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The required horizon is not positive.</exception>
    public DocumentValidationResult Validate(TimeSpan requiredRecoveryHorizon)
    {
        if (requiredRecoveryHorizon <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requiredRecoveryHorizon));
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        Require(RetainedPreparedInput, "prepared-input", "Retain exact prepared work and publication identity before sink dispatch.");
        Require(ExactPublicationReplay, "publication-replay", "The sink must reconcile original exact publications without overwriting later data.");
        Require(AtomicLedgerAndReceipt, "atomic-ledger-receipt", "Commit progress CAS and its independent receipt in one atomic boundary.");
        Require(DefinitiveReceiptBeforeRevision, "receipt-visibility", "Reconcile receipts before CAS; inconclusive visibility must remain Unknown.");
        Retention(PublicationReceiptRetention, "publication-retention");
        Retention(LedgerReceiptRetention, "ledger-retention");
        Retention(PreparedInputRetention, "input-retention");
        return new(diagnostics.ToImmutable());

        void Require(bool condition, string code, string message)
        {
            if (!condition) diagnostics.Add(new("integrations.ingestion." + code, DiagnosticSeverity.Error, message, "/realization/" + code));
        }
        void Retention(TimeSpan? actual, string code) => Require(actual >= requiredRecoveryHorizon,
            code, "Declare retention at least as long as the required recovery horizon; unknown retention is not sufficient.");
    }
}
