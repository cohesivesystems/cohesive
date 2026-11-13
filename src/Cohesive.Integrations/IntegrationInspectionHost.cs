using System.Text.Json;
using Cohesive.Execution;

namespace Cohesive.Integrations;

/// <summary>Internal experimental host seam for local inspection examples and conformance tests.</summary>
/// <remarks>The host supplies a prevalidated binding and an authenticated evidence reader. No compilation, provider
/// discovery or acquisition occurs here. Endpoint authorization and transport errors remain host-owned. Reader failures
/// propagate rather than being disguised as missing evidence; the clock is sampled after evidence retrieval.
/// The binding currently owns an InMemoryIntegrationLifecycle. This is not a durable-store hosting contract;
/// keep it internal until a real consumer establishes the storage, ownership and transport boundaries.</remarks>
internal sealed class IntegrationInspectionHost
{
    static readonly JsonSerializerOptions JsonOptions = InteractionEnvelopeJsonSerializer.CreateOptions();
    readonly IntegrationAdmissionBinding binding;
    readonly Func<CancellationToken, ValueTask<IntegrationHealthEvidence?>> readEvidence;
    readonly TimeProvider clock;

    /// <summary>Configures a reusable inspection reader without creating another lifecycle authority.</summary>
    /// <param name="binding">Validated binding that owns lifecycle inspection.</param>
    /// <param name="readEvidence">Authenticated store reader; null result means evidence is missing, not a read failure.</param>
    /// <param name="clock">Clock for inspection boundaries; defaults to the system clock.</param>
    /// <exception cref="ArgumentNullException">Binding or evidence reader is null.</exception>
    public IntegrationInspectionHost(IntegrationAdmissionBinding binding,
        Func<CancellationToken, ValueTask<IntegrationHealthEvidence?>> readEvidence, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(readEvidence);
        this.binding = binding; this.readEvidence = readEvidence; this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads current evidence once and inspects the bound controller at the current clock boundary.</summary>
    /// <param name="cancellationToken">Cancellation passed to the evidence reader.</param>
    /// <returns>A read-only projection; no admission or execution occurs.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    /// <exception cref="ArgumentException">The reader returns foreign-binding evidence or the clock is not UTC.</exception>
    public async ValueTask<IntegrationInspection> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = await readEvidence(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return binding.Inspect(clock.GetUtcNow(), evidence);
    }

    /// <summary>Reads once and exports inspection as JSON using the shared execution wire conventions.</summary>
    /// <param name="destination">Caller-owned writable stream, left open after export.</param>
    /// <param name="cancellationToken">Cancellation for evidence retrieval and output.</param>
    /// <returns>Completes after the inspection has been written. Partial output is possible on cancellation or I/O failure.</returns>
    /// <exception cref="ArgumentNullException">Destination is null.</exception>
    /// <exception cref="ArgumentException">Destination is not writable, evidence is foreign or the clock is not UTC.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    /// <exception cref="IOException">The output stream fails.</exception>
    public async Task WriteJsonAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Inspection destination must be writable.", nameof(destination));
        var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(destination, view, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
