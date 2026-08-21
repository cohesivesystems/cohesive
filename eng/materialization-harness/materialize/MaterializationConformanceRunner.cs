using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Canonical logical evidence returned by one materialization conformance replica.</summary>
internal interface IMaterializationConformanceResult
{
    /// <summary>Stable replica identity.</summary>
    string Replica { get; }

    /// <summary>Canonical semantic-definition fingerprint interpreted by the replica.</summary>
    string DefinitionFingerprint { get; }

    /// <summary>Canonical materialized documents in deterministic order.</summary>
    ImmutableArray<string> Documents { get; }
}

/// <summary>One explicitly bound replica that can execute a common materialization conformance scenario.</summary>
/// <typeparam name="TResult">Replica result retaining canonical comparison evidence.</typeparam>
internal interface IMaterializationConformanceReplica<TResult>
    where TResult : IMaterializationConformanceResult
{
    /// <summary>Stable replica identity.</summary>
    string Replica { get; }

    /// <summary>Executes the scenario through this replica's explicit adapter binding.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <returns>Canonical result plus any scenario-specific physical evidence.</returns>
    ValueTask<TResult> ExecuteAsync(OperationContext context);
}

/// <summary>
/// Provider-neutral conformance orchestration over an open catalog of explicitly bound replicas.
/// </summary>
/// <typeparam name="TResult">Canonical comparison result.</typeparam>
/// <remarks>
/// This runner owns workflow ordering and semantic equality only. Provisioning, capability evidence,
/// adapter construction, and raw provider verification remain responsibilities of each replica fixture.
/// </remarks>
internal sealed class MaterializationConformanceRunner<TResult>
    where TResult : IMaterializationConformanceResult
{
    readonly string expectedDefinitionFingerprint;
    readonly ImmutableArray<IMaterializationConformanceReplica<TResult>> replicas;

    /// <summary>Creates one deterministic conformance runner.</summary>
    /// <param name="expectedDefinitionFingerprint">Canonical semantic authority all replicas must interpret.</param>
    /// <param name="replicas">Open replica catalog; input order is immaterial.</param>
    /// <exception cref="ArgumentException">The fingerprint is empty or replica identities are absent or repeated.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="replicas"/> is <see langword="null"/>.</exception>
    internal MaterializationConformanceRunner(
        string expectedDefinitionFingerprint,
        IEnumerable<IMaterializationConformanceReplica<TResult>> replicas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDefinitionFingerprint);
        ArgumentNullException.ThrowIfNull(replicas);
        this.expectedDefinitionFingerprint = expectedDefinitionFingerprint;
        var candidates = replicas.ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("A conformance run requires at least one replica.", nameof(replicas));
        if (candidates.Any(static replica => replica is null || string.IsNullOrWhiteSpace(replica.Replica)))
            throw new ArgumentException("Every conformance replica requires a stable identity.", nameof(replicas));
        var normalized = candidates
            .OrderBy(static replica => replica.Replica, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.GroupBy(static replica => replica.Replica, StringComparer.Ordinal)
            .Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException("A conformance run cannot repeat a replica identity.", nameof(replicas));
        }
        this.replicas = normalized;
    }

    /// <summary>Executes every replica in canonical identity order and requires exact semantic equality.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <returns>Replica-specific results in canonical identity order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A replica reports drift or differs from the canonical result.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    internal async Task<ImmutableArray<TResult>> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var results = ImmutableArray.CreateBuilder<TResult>(replicas.Length);
        ImmutableArray<string> expectedDocuments = [];
        foreach (var replica in replicas)
        {
            context.ThrowIfCancellationRequested();
            var result = await replica.ExecuteAsync(context).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Replica '{replica.Replica}' returned no conformance result.");
            if (!string.Equals(result.Replica, replica.Replica, StringComparison.Ordinal))
                throw new InvalidOperationException($"Replica '{replica.Replica}' returned evidence for '{result.Replica}'.");
            if (!string.Equals(
                    result.DefinitionFingerprint,
                    expectedDefinitionFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replica '{replica.Replica}' interpreted another materialization definition.");
            }
            if (result.Documents.IsDefault)
                throw new InvalidOperationException($"Replica '{replica.Replica}' returned no canonical document collection.");
            for (var index = 1; index < result.Documents.Length; index++)
            {
                if (StringComparer.Ordinal.Compare(result.Documents[index - 1], result.Documents[index]) >= 0)
                {
                    throw new InvalidOperationException(
                        $"Replica '{replica.Replica}' documents are not in strict canonical order.");
                }
            }
            if (results.Count == 0)
            {
                expectedDocuments = result.Documents;
            }
            else if (!result.Documents.SequenceEqual(expectedDocuments, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replica '{replica.Replica}' produced different canonical materialized documents.");
            }
            results.Add(result);
        }
        return results.MoveToImmutable();
    }
}
