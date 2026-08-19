using Cohesive.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Safe-point control consulted before each bounded source page.</summary>
public interface IMaterializationHarnessRunControl
{
    /// <summary>Authorizes or blocks the next bounded page.</summary>
    /// <param name="context">Current execution context.</param>
    /// <param name="provider">Stable source-provider identity.</param>
    /// <param name="tenant">Tenant partition about to be read.</param>
    /// <param name="pageOrdinal">Zero-based page ordinal within the tenant.</param>
    /// <returns>A value task completing when the page may proceed.</returns>
    /// <exception cref="MaterializationHarnessRunSuspendedException">The current attempt is paused or superseded.</exception>
    /// <exception cref="OperationCanceledException">The current attempt is cancelled.</exception>
    ValueTask BeforePageAsync(
        OperationContext context,
        string provider,
        string tenant,
        int pageOrdinal);
}

/// <summary>Stable identity and safe-point policy for one restartable harness attempt.</summary>
public sealed record MaterializationHarnessRunOptions
{
    /// <summary>Creates one materialization run.</summary>
    /// <param name="runId">Stable attempt identity reused by exact retries and Continue.</param>
    /// <param name="startedAtUtc">Stable UTC attempt-start time reused in generation-allocation intent.</param>
    /// <param name="control">Safe-point policy checked before every bounded source page.</param>
    /// <param name="progressStore">Optional durable page-progress authority used by a restartable host.</param>
    /// <param name="progressOwner">Stable worker identity used when <paramref name="progressStore"/> is supplied.</param>
    /// <param name="cancellationToken">Host-shutdown or caller cancellation.</param>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is empty.</exception>
    /// <exception cref="ArgumentException"><paramref name="startedAtUtc"/> is not UTC.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="control"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Exactly one of <paramref name="progressStore"/> and <paramref name="progressOwner"/> is supplied.
    /// </exception>
    public MaterializationHarnessRunOptions(
        string runId,
        DateTimeOffset startedAtUtc,
        IMaterializationHarnessRunControl control,
        IMaterializationProgressStore? progressStore = null,
        string? progressOwner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("A materialization run start time must be UTC.", nameof(startedAtUtc));
        if ((progressStore is null) != (progressOwner is null))
        {
            throw new ArgumentException(
                "A durable progress store and its stable worker owner must be supplied together.",
                nameof(progressStore));
        }
        if (progressOwner is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(progressOwner);
        RunId = runId;
        StartedAtUtc = startedAtUtc;
        Control = control ?? throw new ArgumentNullException(nameof(control));
        ProgressStore = progressStore;
        ProgressOwner = progressOwner;
        CancellationToken = cancellationToken;
    }

    /// <summary>Stable attempt identity reused by exact retries and Continue.</summary>
    public string RunId { get; }

    /// <summary>Stable UTC attempt-start time used by exact generation-allocation retries.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Safe-point policy checked before every bounded source page.</summary>
    public IMaterializationHarnessRunControl Control { get; }

    /// <summary>Optional durable authority for exact per-source page progress.</summary>
    public IMaterializationProgressStore? ProgressStore { get; }

    /// <summary>Stable worker identity associated with <see cref="ProgressStore"/>, when configured.</summary>
    public string? ProgressOwner { get; }

    /// <summary>Host-shutdown or caller cancellation.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>Expected nonterminal interruption when a run is paused or its attempt is replaced.</summary>
public sealed class MaterializationHarnessRunSuspendedException : Exception
{
    /// <summary>Creates an expected safe-point suspension.</summary>
    /// <param name="message">Human-readable suspension reason.</param>
    public MaterializationHarnessRunSuspendedException(string message)
        : base(message)
    {
    }
}

/// <summary>Run control that always authorizes the next page.</summary>
public sealed class UncontrolledMaterializationHarnessRun : IMaterializationHarnessRunControl
{
    /// <summary>Shared stateless instance.</summary>
    public static UncontrolledMaterializationHarnessRun Instance { get; } = new();

    UncontrolledMaterializationHarnessRun()
    {
    }

    /// <inheritdoc />
    public ValueTask BeforePageAsync(
        OperationContext context,
        string provider,
        string tenant,
        int pageOrdinal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (pageOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(pageOrdinal), pageOrdinal, "A page ordinal cannot be negative.");
        context.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
