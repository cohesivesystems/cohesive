using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Acquisition;

namespace Cohesive.Storage.Materialization;

/// <summary>Bounded full-scope parity evidence. This is not a promotion or snapshot-isolation receipt.</summary>
/// <param name="Convergence">Existing exact-plan catch-up evidence, including every feed checkpoint.</param>
/// <param name="ObservedAtUtc">Comparison completion time.</param>
/// <param name="SourceStable">Every source position remained unchanged during the reads.</param>
/// <param name="Fresh">The convergence receipt still satisfies plan affinity and freshness.</param>
/// <param name="LiveCount">Number of visible canonical live outputs.</param>
/// <param name="ShadowCount">Number of visible candidate outputs.</param>
/// <param name="Missing">Live identities absent from the candidate.</param>
/// <param name="Extra">Candidate identities absent from live outputs.</param>
/// <param name="Changed">Shared identities with unequal canonical values.</param>
public sealed record MaterializationShadowComparison(
    MaterializationConvergenceReceipt Convergence, DateTimeOffset ObservedAtUtc,
    bool SourceStable, bool Fresh, int LiveCount, int ShadowCount,
    ImmutableArray<string> Missing, ImmutableArray<string> Extra, ImmutableArray<string> Changed)
{
    /// <summary>Full bounded comparison passed at observation time; does not grant promotion or future freshness.</summary>
    public bool Matches => SourceStable && Fresh && Missing.IsEmpty && Extra.IsEmpty && Changed.IsEmpty;
}

/// <summary>Compares the existing canonical hydration interpretation with a bounded candidate read.</summary>
public static class MaterializationShadowVerification
{
    /// <summary>Reads the whole declared scope within explicit item/page limits and compares visible values.</summary>
    /// <param name="runtime">Exact admitted source, target and plan bindings.</param>
    /// <param name="context">Cancellation and observation clock.</param>
    /// <param name="convergence">Exact candidate convergence evidence.</param>
    /// <param name="readCandidate">Reads visible values from that exact target generation, rejecting incomplete reads.</param>
    /// <param name="maximumItems">Maximum retained outputs and scanned roots.</param>
    /// <param name="maximumPages">Maximum root pages across all shards.</param>
    /// <returns>Parity and freshness evidence; values are never included in the report.</returns>
    /// <exception cref="ArgumentException">Limits or convergence affinity are invalid.</exception>
    /// <exception cref="InvalidOperationException">A read is incomplete, exceeds limits, or repeats an identity.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    /// <remarks>The caller must exclude competing candidate writers while reading. Sources are checked before and after;
    /// this detects movement conservatively and does not create a transaction spanning sources and target.</remarks>
    public static async Task<MaterializationShadowComparison> CompareAsync(
        BoundMaterialization runtime, OperationContext context, MaterializationConvergenceReceipt convergence,
        Func<MaterializationGenerationId, int, CancellationToken, Task<IReadOnlyDictionary<string, ObservationValue>>> readCandidate,
        int maximumItems = 1000, int maximumPages = 100)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(convergence);
        ArgumentNullException.ThrowIfNull(readCandidate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPages);
        var resolved = runtime.Resolved;
        if (!convergence.ValidateAgainst(resolved.Plan, context.TimeProvider.GetUtcNow()).IsValid)
            throw new ArgumentException("Comparison requires fresh convergence for the exact bound plan.", nameof(convergence));
        var before = await PositionsAsync();
        Dictionary<string, ObservationValue> live = new(StringComparer.Ordinal);
        var pages = 0;
        var roots = 0;
        foreach (var shard in resolved.Plan.Shards)
        {
            var binding = resolved.GetShard(shard.Id);
            MaterializationSourceContinuation? continuation = null;
            do
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (++pages > maximumPages) throw new InvalidOperationException("Shadow comparison exceeded its page limit.");
                var page = await binding.Source.ReadPageAsync(context, new(shard.Read, shard.Scope, continuation,
                    maximumItems: Math.Min(maximumItems, resolved.Plan.Limits.MaximumPageItems),
                    maximumBytes: resolved.Plan.Limits.MaximumPageBytes)).ConfigureAwait(false);
                if (page.State == MaterializationSourcePageState.Exhausted
                    ? page.Read.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound)
                    : page.Read.State != RelationQuerySourceReadState.Partial)
                    throw new InvalidOperationException("Shadow comparison requires complete source coverage.");
                roots = checked(roots + page.Read.Observations.Length);
                if (roots > maximumItems) throw new InvalidOperationException("Shadow comparison exceeded its root limit.");
                var hydrated = await binding.Hydrator.HydrateAsync(context, new(new("shadow-comparison/" + pages), shard, page)).ConfigureAwait(false);
                var projections = MaterializationRootProjectionSemantics.FromBaselinePage(shard.Scope.Input,
                    page.Read.Observations, resolved.Plan.Materialization.Definition.Relation.ValueShape, hydrated.Rows);
                foreach (var projection in projections)
                    if (projection.Row is { } row && !live.TryAdd(MaterializationItemIdentity.FromRootIdentity(projection.Root.Identity).Value, row.Value))
                        throw new InvalidOperationException("Shadow comparison encountered a repeated root identity.");
                continuation = page.Continuation;
                if (page.State == MaterializationSourcePageState.Exhausted) break;
                if (continuation is null) throw new InvalidOperationException("Incomplete source page has no continuation.");
            } while (true);
        }
        var candidate = await readCandidate(convergence.Generation, maximumItems, context.CancellationToken).ConfigureAwait(false);
        if (candidate.Count > maximumItems) throw new InvalidOperationException("Shadow comparison exceeded its candidate limit.");
        var after = await PositionsAsync();
        var observed = context.TimeProvider.GetUtcNow();
        return CompareValues(convergence, observed, before.SequenceEqual(after),
            convergence.ValidateAgainst(resolved.Plan, observed).IsValid, live, candidate);

        async Task<ImmutableArray<MaterializationSourcePosition>> PositionsAsync()
        {
            var result = ImmutableArray.CreateBuilder<MaterializationSourcePosition>(resolved.Plan.ChangeFeeds.Length);
            foreach (var feed in resolved.Plan.ChangeFeeds)
                result.Add(await resolved.GetChangeFeed(feed.Id).Source.CaptureCurrentPositionAsync(context, feed.Scope).ConfigureAwait(false));
            return result.MoveToImmutable();
        }
    }

    internal static MaterializationShadowComparison CompareValues(MaterializationConvergenceReceipt convergence,
        DateTimeOffset observed, bool stable, bool fresh, IReadOnlyDictionary<string, ObservationValue> live,
        IReadOnlyDictionary<string, ObservationValue> candidate)
    {
        var missing = ImmutableArray.CreateBuilder<string>();
        var extra = ImmutableArray.CreateBuilder<string>();
        var changed = ImmutableArray.CreateBuilder<string>();
        foreach (var (id, value) in live)
            if (!candidate.TryGetValue(id, out var actual)) missing.Add(id);
            else if (!value.Equals(actual)) changed.Add(id);
        foreach (var id in candidate.Keys)
            if (!live.ContainsKey(id)) extra.Add(id);
        missing.Sort(StringComparer.Ordinal); extra.Sort(StringComparer.Ordinal); changed.Sort(StringComparer.Ordinal);
        return new(convergence, observed, stable, fresh, live.Count, candidate.Count,
            missing.ToImmutable(), extra.ToImmutable(), changed.ToImmutable());
    }
}
