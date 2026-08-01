using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

static class MaterializationRebuildTestPlan
{
    public static MaterializationImpactPlan CompileImpactPlan(
        MaterializationDocument materialization,
        string policyId,
        long maximumAffectedRoots,
        long maximumReadBytes)
    {
        var compilation = MaterializationImpactPlanCompiler.Compile(
            materialization,
            new MaterializationImpactPlanningPolicy(
                id: new(policyId),
                strategyPreference: [MaterializationImpactStrategyKind.InverseTraversal],
                maximumAffectedRoots: maximumAffectedRoots,
                maximumReadBytes: maximumReadBytes));
        return compilation.Plan ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    public static (
        ImmutableArray<MaterializationChangeFeedCatalogEvidence> Evidence,
        ImmutableArray<MaterializationChangeFeedPlan> Feeds) CreateChangeFeedCatalog(
        CompiledRelationQueryPlan compiled,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        MaterializationImpactPlan impactPlan,
        ImmutableArray<MaterializationRebuildSourcePlan> sourcePlans,
        ImmutableArray<MaterializationRebuildShardPlan> shards,
        Func<MaterializationImpactRoute, RelationQuerySourcePlacementBinding> contributorPlacement,
        string channelCanonicalization)
    {
        var rootInput = compiled.InputContract.Sources
            .Single(static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            .Input.Id;
        var feeds = ImmutableArray.CreateBuilder<MaterializationChangeFeedPlan>(
            shards.Length + impactPlan.Routes.Count(route => route.ChangeInput != rootInput));
        foreach (var shard in shards)
        {
            feeds.Add(new(
                id: new($"feed/{shard.Id.Value}"),
                scope: shard.Scope,
                channel: Channel(channelCanonicalization, $"root/{shard.Id.Value}")));
        }

        foreach (var route in impactPlan.Routes.Where(route => route.ChangeInput != rootInput))
        {
            var source = sourcePlans.Single(candidate => candidate.Input == route.ChangeInput);
            var placement = contributorPlacement(route);
            if (placement.Input != route.ChangeInput
                || placement.Shape != route.ChangeShape
                || placement.Source != source.Source)
            {
                throw new InvalidOperationException(
                    $"The change-feed placement for '{route.ChangeInput.Value}' differs from the compiled route or source plan.");
            }

            feeds.Add(new(
                id: new($"feed/{route.ChangeInput.Value}"),
                scope: new(
                    physicalPlan,
                    placement,
                    partition: new($"partition/{route.ChangeInput.Value}"),
                    orderingScope: new($"ordering/{route.ChangeInput.Value}")),
                channel: Channel(channelCanonicalization, route.ChangeInput.Value)));
        }

        var exactFeeds = feeds.MoveToImmutable();
        var evidence = exactFeeds
            .GroupBy(static feed => feed.Scope.Input)
            .Select(group =>
            {
                var source = sourcePlans.Single(candidate => candidate.Input == group.Key);
                return new MaterializationChangeFeedCatalogEvidence(
                    input: group.Key,
                    source: source.Source,
                    scopes: [.. group.Select(static feed => feed.Scope)],
                    evidenceReference: $"catalog/{channelCanonicalization}/{group.Key.Value}");
            })
            .ToImmutableArray();

        return (evidence, exactFeeds);
    }

    static ChannelRealizationPlanFingerprint Channel(string canonicalization, string suffix) => new(
        algorithm: "sha256",
        canonicalization: canonicalization,
        value: $"channel/{suffix}");
}
