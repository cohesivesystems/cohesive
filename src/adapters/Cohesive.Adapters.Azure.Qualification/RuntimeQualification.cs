namespace Cohesive.Adapters.Azure.Qualification;

/// <summary>Evidence about one native operation, not canonical infrastructure readiness.</summary>
public enum QualificationOutcome
{
    /// <summary>No creation was attempted.</summary>
    NotStarted,
    /// <summary>A preexisting target prevented creation; it was not touched.</summary>
    Collision,
    /// <summary>The complete challenge round trip was observed.</summary>
    Verified,
    /// <summary>The operation failed or timed out; completion must not be assumed.</summary>
    Unverified
}

/// <summary>Disposition of only this attempt's synthetic data or scheduler history.</summary>
public enum QualificationCleanup
{
    /// <summary>No object was created by the attempt.</summary>
    NotRequired,
    /// <summary>Conditional deletion completed for the creation receipt.</summary>
    Deleted,
    /// <summary>Scheduler history is intentionally retained; no purge or termination is attempted.</summary>
    HistoryRetained,
    /// <summary>Creation/deletion outcome is ambiguous or cleanup failed; inspect the exact object.</summary>
    Unresolved
}

/// <summary>A bounded, explicitly invoked attempt. Never use one run ID for a retry.</summary>
public sealed record RuntimeQualificationOptions
{
    /// <summary>Creates policy with positive operation (at most five minutes) and cleanup (at most one minute) budgets.</summary>
    public RuntimeQualificationOptions(Guid runId, TimeSpan operationTimeout, TimeSpan cleanupTimeout)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A fresh run ID is required.", nameof(runId));
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        RunId = runId; OperationTimeout = operationTimeout; CleanupTimeout = cleanupTimeout;
    }
    /// <summary>Caller-retained correlation identifier; never a product record identity.</summary>
    public Guid RunId { get; }
    /// <summary>Budget shared by creation and verification.</summary>
    public TimeSpan OperationTimeout { get; }
    /// <summary>Independent cleanup budget, including after caller cancellation.</summary>
    public TimeSpan CleanupTimeout { get; }
    /// <summary>Exact synthetic object/instance name, retained for ambiguous-outcome investigation.</summary>
    public string ObjectName => "cohesive-qualification-" + RunId.ToString("N");
}

/// <summary>Redacted outcome. No provider exceptions, tokens, payloads or endpoint identities are retained.</summary>
/// <param name="RunId">Attempt identity.</param>
/// <param name="ObjectName">Only the generated object/instance identifier, not a native resource ID.</param>
/// <param name="Outcome">Observed round-trip outcome.</param>
/// <param name="Cleanup">Actual or intentional cleanup disposition.</param>
/// <param name="Diagnostic">Stable stage code, never an exception message.</param>
public sealed record RuntimeQualificationResult(Guid RunId, string ObjectName, QualificationOutcome Outcome,
    QualificationCleanup Cleanup, string? Diagnostic)
{
    /// <summary>True only for verified operations with completed data cleanup or explicitly retained scheduler history.</summary>
    public bool Succeeded => Outcome == QualificationOutcome.Verified && Cleanup is QualificationCleanup.Deleted or QualificationCleanup.HistoryRetained;
}

// Kept internal: this is the common storage lifecycle, not an extensible parallel infrastructure graph.
internal static class StorageQualification
{
    internal static async Task<RuntimeQualificationResult> RunAsync(RuntimeQualificationOptions options,
        Func<CancellationToken, Task<string?>> create,
        Func<string, CancellationToken, Task<bool>> verify,
        Func<string, CancellationToken, Task> delete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (cancellationToken.IsCancellationRequested)
            return new(options.RunId, options.ObjectName, QualificationOutcome.NotStarted, QualificationCleanup.NotRequired, "qualification.canceled");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work.CancelAfter(options.OperationTimeout);
        string? receipt = null;
        var outcome = QualificationOutcome.Unverified;
        var cleanup = QualificationCleanup.Unresolved;
        string? diagnostic = "qualification.createUnknown";
        try
        {
            receipt = await create(work.Token).ConfigureAwait(false);
            if (receipt is null)
                return new(options.RunId, options.ObjectName, QualificationOutcome.Collision, QualificationCleanup.NotRequired, "qualification.collision");
            if (string.IsNullOrWhiteSpace(receipt)) throw new InvalidOperationException();
            diagnostic = "qualification.verifyFailed";
            if (await verify(receipt, work.Token).ConfigureAwait(false))
            {
                outcome = QualificationOutcome.Verified;
                diagnostic = null;
            }
        }
        catch (Exception) { /* Ambiguous creation is not ownership. Never expose provider payloads. */ }
        finally
        {
            if (!string.IsNullOrWhiteSpace(receipt))
            {
                using var cleanupBudget = new CancellationTokenSource(options.CleanupTimeout);
                try
                {
                    await delete(receipt, cleanupBudget.Token).ConfigureAwait(false);
                    cleanup = QualificationCleanup.Deleted;
                }
                catch (Exception) { diagnostic = "qualification.cleanupUnresolved"; }
            }
        }
        return new(options.RunId, options.ObjectName, outcome, cleanup, diagnostic);
    }
}
