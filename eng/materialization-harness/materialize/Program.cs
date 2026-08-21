using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Azure.Cosmos;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Runs the local freight materialization scenario across PostgreSQL and Cosmos sources.</summary>
public static class Program
{
    const int RootPageItems = 2;
    const int MaximumBatchItems = 64;
    const int MaximumRows = 128;
    const long MaximumBytes = 1 * 1024 * 1024;
    const string PostgresSchema = "freight_harness";
    static readonly byte[] ContinuationKey =
        "cohesive-materialization-harness-local-key-v1"u8.ToArray();
    static readonly PostgresLogicalReplicationSourcePolicy LocalPostgresChangePolicy = new(
        readInactivityTimeout: TimeSpan.FromSeconds(3));

    /// <summary>Validates or runs the standalone materialization harness.</summary>
    /// <param name="args">Optional single <c>--validate-only</c> argument.</param>
    /// <returns>Zero after successful validation or materialization.</returns>
    /// <exception cref="ArgumentException"><paramref name="args"/> contains an unsupported argument.</exception>
    public static async Task<int> Main(string[] args)
    {
        var validateOnly = args switch
        {
            [] => false,
            ["--validate-only"] => true,
            _ => throw new ArgumentException(
                "The materialization harness accepts only --validate-only.",
                nameof(args))
        };
        var semantics = FreightOrderMaterializationModel.Create();
        if (validateOnly)
        {
            var postgresPlan = CreateProviderPlan(ProviderKind.Postgres, semantics);
            var cosmosPlan = CreateProviderPlan(ProviderKind.Cosmos, semantics);
            Console.WriteLine($"Validated canonical definition: {semantics.DefinitionFingerprint.Value}.");
            Console.WriteLine(
                $"Validated provider plans: postgres={postgresPlan.HydrationPhysicalPlan.Fingerprint.Value}, "
                + $"cosmos={cosmosPlan.HydrationPhysicalPlan.Fingerprint.Value}.");
            return 0;
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var runId = startedAtUtc.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        await RunAsync(new(
            runId: runId,
            startedAtUtc: startedAtUtc,
            control: UncontrolledMaterializationHarnessRun.Instance));
        return 0;
    }

    /// <summary>Runs or exactly retries one bounded dual-provider materialization attempt.</summary>
    /// <param name="run">Stable attempt identity, cancellation, and safe-point control.</param>
    /// <returns>A task completing after both provider generations are atomically promoted and compared.</returns>
    public static async Task RunAsync(MaterializationHarnessRunOptions run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var semantics = FreightOrderMaterializationModel.Create();
        var options = HarnessOptions.FromEnvironment();
        var journal = await FreightScenarioJournal.LoadAsync(
            path: options.ScenarioPath,
            cancellationToken: run.CancellationToken);
        using HttpClient elasticHttp = new() { BaseAddress = options.ElasticsearchEndpoint };
        var clusterId = await ReadClusterIdAsync(elasticHttp);
        var context = OperationContext.Create(cancellationToken: run.CancellationToken);

        var postgres = await MaterializeProviderAsync(
            ProviderKind.Postgres,
            semantics,
            options,
            clusterId,
            elasticHttp,
            context,
            run,
            journal);
        var cosmos = await MaterializeProviderAsync(
            ProviderKind.Cosmos,
            semantics,
            options,
            clusterId,
            elasticHttp,
            context,
            run,
            journal);

        Require(
            postgres.Documents.SequenceEqual(cosmos.Documents, StringComparer.Ordinal),
            "Postgres and Cosmos produced different canonical Elasticsearch documents.");
        Require(
            postgres.DefinitionFingerprint == cosmos.DefinitionFingerprint,
            "Provider realizations did not retain one canonical materialization definition fingerprint.");

        Console.WriteLine($"Canonical definition: {semantics.DefinitionFingerprint.Value}");
        PrintResult(postgres);
        PrintResult(cosmos);
        Console.WriteLine($"Verified {postgres.Documents.Length} canonically equivalent freight documents.");
    }

    /// <summary>
    /// Idempotently abandons every non-active provider generation owned by one superseded harness run.
    /// </summary>
    /// <param name="runId">Stable run identity whose provider generations must never be promoted.</param>
    /// <param name="abandonedAtUtc">Stable UTC control-command time reused by exact retries.</param>
    /// <param name="cancellationToken">Cancellation for Elasticsearch operations.</param>
    /// <returns>A task completing after both provider targets retain abandonment evidence.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="runId"/> is empty or <paramref name="abandonedAtUtc"/> is not UTC.
    /// </exception>
    public static async Task AbandonRunAsync(
        string runId,
        DateTimeOffset abandonedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (abandonedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A materialization abandonment time must be UTC.", nameof(abandonedAtUtc));
        }

        var semantics = FreightOrderMaterializationModel.Create();
        var options = HarnessOptions.FromEnvironment();
        using HttpClient elasticHttp = new() { BaseAddress = options.ElasticsearchEndpoint };
        var clusterId = await ReadClusterIdAsync(elasticHttp);
        var context = OperationContext.Create(cancellationToken: cancellationToken);
        foreach (var provider in new[] { ProviderKind.Postgres, ProviderKind.Cosmos })
        {
            var targetBinding = CreateTargetBinding(provider, semantics, clusterId);
            await EnsureLocalElasticTemplatesAsync(elasticHttp, targetBinding, ProviderName(provider));
            var target = CreateTarget(targetBinding, options.ElasticsearchEndpoint);
            var generationId = new MaterializationGenerationId($"{ProviderName(provider)}/{runId}");
            var generation = await target.InspectGenerationAsync(context, generationId);
            if (generation?.State == MaterializationGenerationState.Active)
                continue;

            var abandoned = await target.AbandonGenerationAsync(
                context,
                new(
                    abandonmentId: new($"abandon/{ProviderName(provider)}/{runId}"),
                    generationId: generationId,
                    abandonedAtUtc: abandonedAtUtc));
            Require(
                abandoned.Disposition is MaterializationTargetOperationDisposition.Applied
                    or MaterializationTargetOperationDisposition.Replayed
                    or MaterializationTargetOperationDisposition.ActiveGenerationConflict,
                $"{provider} generation abandonment failed: {abandoned.Disposition}.");
        }
    }

    static async Task<ProviderResult> MaterializeProviderAsync(
        ProviderKind provider,
        FreightOrderMaterializationSemantics semantics,
        HarnessOptions options,
        ElasticClusterId clusterId,
        HttpClient elasticHttp,
        OperationContext context,
        MaterializationHarnessRunOptions run,
        FreightScenarioJournal journal)
    {
        var plan = CreateProviderPlan(provider, semantics);
        var targetBinding = CreateTargetBinding(provider, semantics, clusterId);
        await EnsureLocalElasticTemplatesAsync(elasticHttp, targetBinding, ProviderName(provider));
        var target = CreateTarget(targetBinding, options.ElasticsearchEndpoint);
        var before = await target.InspectAsync(context);
        var generationId = new MaterializationGenerationId($"{ProviderName(provider)}/{run.RunId}");
        var generationIndex = targetBinding.GetGenerationIndexName(generationId);
        if (before.ActiveGenerationId == generationId)
        {
            var replayedDocuments = await ReadCanonicalDocumentsAsync(elasticHttp, targetBinding.ReadAlias);
            return new(
                ProviderName(provider),
                targetBinding.ReadAlias,
                generationIndex,
                semantics.DefinitionFingerprint.Value,
                replayedDocuments);
        }
        var workerFence = MaterializationWorkerFence.Initial;
        var begun = await target.BeginGenerationAsync(
            context: context,
            request: new(
                materializationId: semantics.Definition.Id,
                generationId: generationId,
                definitionFingerprint: semantics.DefinitionFingerprint,
                workerFence: workerFence,
                createdAtUtc: run.StartedAtUtc));
        Require(
            begun.Disposition is MaterializationTargetOperationDisposition.Applied
                or MaterializationTargetOperationDisposition.Replayed,
            $"{provider} generation begin failed: {begun.Disposition}.");
        var generation = RequireValue(begun.Generation, $"{provider} generation begin returned no snapshot.");
        if (generation.State == MaterializationGenerationState.Loading)
        {
            var pageOrdinal = 0;
            if (provider == ProviderKind.Postgres)
            {
                await using var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
                var canonicalPlan = await CompilePostgresRebuildPlanAsync(
                    semantics: semantics,
                    plan: plan,
                    target: target,
                    dataSource: dataSource,
                    connectionString: options.PostgresConnectionString,
                    tenants: options.Tenants,
                    journal: journal,
                    cancellationToken: context.CancellationToken);
                Console.WriteLine(
                    $"Compiled canonical postgres rebuild plan {canonicalPlan.Plan.Fingerprint.Value} "
                    + $"with {canonicalPlan.Plan.Shards.Length} tenant shards.");
                var hydrationStorage = CreatePostgresStorageBinding(
                    plan.HydrationPlacement,
                    semantics.Plan,
                    "hydration");
                var scanStorage = CreatePostgresStorageBinding(plan.ScanPlacement, semantics.Plan, "scan");
                foreach (var tenant in options.Tenants)
                {
                    var policy = PostgresPolicy(tenant);
                    var scanRuntime = new PostgresNpgsqlRuntimeBinding(
                        scanStorage.Database,
                        dataSource,
                        "materialization-harness/postgres/scan");
                    var rootReader = new PostgresRelationQuerySourceReader(
                        semantics.Plan,
                        plan.ScanPhysicalPlan,
                        plan.OrderSource,
                        scanStorage,
                        dataSource,
                        scanRuntime,
                        policy);
                    var source = new PostgresMaterializationSource(
                        rootReader,
                        plan.ScanRoot,
                        ContinuationKey);
                    var readers = plan.HydrationSources
                        .Select(sourceId =>
                        {
                            var runtime = new PostgresNpgsqlRuntimeBinding(
                                hydrationStorage.Database,
                                dataSource,
                                "materialization-harness/postgres/hydration");
                            return (IRelationQuerySourceReader)new PostgresRelationQuerySourceReader(
                                semantics.Plan,
                                plan.HydrationPhysicalPlan,
                                sourceId,
                                hydrationStorage,
                                dataSource,
                                runtime,
                                policy);
                        })
                        .ToImmutableArray();
                    await MaterializeTenantAsync(
                        provider,
                        tenant,
                        semantics,
                        plan,
                        source,
                        source.Scope,
                        readers,
                        target,
                        generationId,
                        workerFence,
                        generation.Revision,
                        pageOrdinal,
                        context,
                        run);
                    pageOrdinal += 1_000;
                    generation = RequireValue(
                        await target.InspectGenerationAsync(context, generationId),
                        $"{provider} generation disappeared while loading.");
                }
            }
            else
            {
                using var cosmosClient = CreateCosmosClient(options.CosmosConnectionString);
                var database = cosmosClient.GetDatabase(options.CosmosDatabase);
                var canonicalPlan = CompileCosmosRebuildPlan(
                    semantics: semantics,
                    plan: plan,
                    target: target,
                    database: database,
                    databaseId: options.CosmosDatabase,
                    tenants: options.Tenants,
                    journal: journal);
                Console.WriteLine(
                    $"Compiled canonical cosmos rebuild plan {canonicalPlan.Plan.Fingerprint.Value} "
                    + $"with {canonicalPlan.Plan.Shards.Length} tenant shards.");
                foreach (var tenant in options.Tenants)
                {
                    var policy = CosmosPolicy(tenant);
                    var rootReader = CreateCosmosReader(
                        semantics.Root.Shape,
                        plan.OrderSource,
                        database.GetContainer("orders"),
                        options.CosmosDatabase,
                        "orders",
                        policy);
                    var source = CreateCosmosReconciliationSource(rootReader);
                    var sourceScope = new MaterializationSourceScope(
                        plan.ScanPhysicalPlan.Fingerprint,
                        plan.ScanRoot,
                        LogicalPartition(tenant),
                        new($"cosmos/reconciliation/{tenant}"),
                        new($"cosmos/reconciliation/{tenant}/canonical-order"));
                    var readers = plan.HydrationSources
                        .Select(sourceId => CreateCosmosHydrationReader(
                            semantics,
                            plan,
                            sourceId,
                            database,
                            options.CosmosDatabase,
                            policy))
                        .Cast<IRelationQuerySourceReader>()
                        .ToImmutableArray();
                    await MaterializeTenantAsync(
                        provider,
                        tenant,
                        semantics,
                        plan,
                        source,
                        sourceScope,
                        readers,
                        target,
                        generationId,
                        workerFence,
                        generation.Revision,
                        pageOrdinal,
                        context,
                        run);
                    pageOrdinal += 1_000;
                    generation = RequireValue(
                        await target.InspectGenerationAsync(context, generationId),
                        $"{provider} generation disappeared while loading.");
                }
            }
        }

        Require(
            generation.State is MaterializationGenerationState.Loading
                or MaterializationGenerationState.Sealed
                or MaterializationGenerationState.Validated,
            $"{provider} generation cannot resume from '{generation.State}'.");

        Require(
            generation.VisibleItemCount > RootPageItems,
            $"{provider} did not cross the configured root page boundary.");
        var aliasBeforePromotion = await ReadAliasIndicesAsync(elasticHttp, targetBinding.ReadAlias);
        Require(
            !aliasBeforePromotion.Contains(generationIndex, StringComparer.Ordinal),
            $"{provider} candidate generation was exposed through the read alias before promotion.");

        MaterializationSealReceipt sealReceipt;
        if (generation.State == MaterializationGenerationState.Loading)
        {
            var sealedResult = await target.SealGenerationAsync(
                context,
                new(
                    sealId: new($"seal/{ProviderName(provider)}/{run.RunId}"),
                    generationId: generationId,
                    expectedRevision: generation.Revision,
                    workerFence: workerFence,
                    sealedAtUtc: context.UtcNow));
            Require(
                sealedResult.Disposition is MaterializationTargetOperationDisposition.Applied
                    or MaterializationTargetOperationDisposition.Replayed,
                $"{provider} generation seal failed: {sealedResult.Disposition}.");
            generation = RequireValue(sealedResult.Generation, $"{provider} seal returned no generation.");
            sealReceipt = RequireValue(sealedResult.Receipt, $"{provider} seal returned no receipt.");
        }
        else
        {
            sealReceipt = RequireValue(generation.SealReceipt, $"{provider} retained no seal receipt.");
        }

        MaterializationValidationReceipt validationReceipt;
        if (generation.State == MaterializationGenerationState.Sealed)
        {
            var validated = await target.ValidateGenerationAsync(
                context,
                new(
                    validationId: new($"validate/{ProviderName(provider)}/{run.RunId}"),
                    generationId: generationId,
                    expectedRevision: generation.Revision,
                    expectedSealFingerprint: sealReceipt.Fingerprint,
                    expectedVisibleItemCount: generation.VisibleItemCount,
                    validator: "materialization-harness/freight-readback/v1",
                    workerFence: workerFence,
                    validatedAtUtc: context.UtcNow));
            Require(
                validated.Disposition is MaterializationTargetOperationDisposition.Applied
                    or MaterializationTargetOperationDisposition.Replayed,
                $"{provider} generation validation failed: {validated.Disposition}.");
            generation = RequireValue(validated.Generation, $"{provider} validation returned no generation.");
            validationReceipt = RequireValue(validated.Receipt, $"{provider} validation returned no receipt.");
        }
        else
        {
            validationReceipt = RequireValue(
                generation.ValidationReceipt,
                $"{provider} retained no validation receipt.");
        }
        Require(validationReceipt.Validation.IsValid, $"{provider} generation validation was inconclusive.");
        var promotionFence = new MaterializationPromotionFence(
            (before.LatestPromotionFence?.Ordinal + 1 ?? 1).ToString(CultureInfo.InvariantCulture));
        var promoted = await target.PromoteGenerationAsync(
            context,
            new(
                promotionId: new($"promote/{ProviderName(provider)}/{run.RunId}"),
                generationId: generationId,
                expectedGenerationRevision: generation.Revision,
                validationFingerprint: validationReceipt.Fingerprint,
                expectedActiveGenerationId: before.ActiveGenerationId,
                expectedTargetRevision: before.Revision,
                generationWorkerFence: workerFence,
                promotionFence: promotionFence,
                promotedAtUtc: context.UtcNow));
        Require(
            promoted.Disposition is MaterializationTargetOperationDisposition.Applied
                or MaterializationTargetOperationDisposition.Replayed,
            $"{provider} generation promotion failed: {promoted.Disposition}.");

        var aliasAfterPromotion = await ReadAliasIndicesAsync(elasticHttp, targetBinding.ReadAlias);
        Require(
            aliasAfterPromotion.SequenceEqual([generationIndex], StringComparer.Ordinal),
            $"{provider} read alias did not atomically resolve to exactly the promoted generation.");
        var documents = await ReadCanonicalDocumentsAsync(elasticHttp, targetBinding.ReadAlias);
        Require(
            documents.Length == generation.VisibleItemCount,
            $"{provider} alias readback count differs from materialized output.");
        return new(
            ProviderName(provider),
            targetBinding.ReadAlias,
            generationIndex,
            semantics.DefinitionFingerprint.Value,
            documents);
    }

    static async Task MaterializeTenantAsync(
        ProviderKind provider,
        string tenant,
        FreightOrderMaterializationSemantics semantics,
        ProviderPlan plan,
        IMaterializationSource source,
        MaterializationSourceScope sourceScope,
        ImmutableArray<IRelationQuerySourceReader> readers,
        ElasticMaterializationTarget target,
        MaterializationGenerationId generationId,
        MaterializationWorkerFence workerFence,
        MaterializationGenerationRevision initialRevision,
        int pageOrdinalBase,
        OperationContext context,
        MaterializationHarnessRunOptions run)
    {
        var read = CreateRootRead(plan, semantics.Root);
        var progressKey = new MaterializationProgressKey(
            materialization: semantics.Definition.Id,
            definitionFingerprint: semantics.DefinitionFingerprint,
            generation: generationId,
            scope: sourceScope);
        var progress = await AcquireProgressAsync(context, progressKey, run);
        if (progress?.LatestBatchCheckpoint?.Kind == MaterializationCheckpointKind.BatchCompleted)
            return;

        var continuation = progress?.LatestBatchCheckpoint?.Continuation;
        var generationRevision = initialRevision;
        var pageOrdinal = checked((int)(progress?.LatestBatchCheckpoint?.BatchPageOrdinal ?? 0));
        do
        {
            await run.Control.BeforePageAsync(
                context,
                ProviderName(provider),
                tenant,
                pageOrdinal);
            MaterializationSourcePage page;
            try
            {
                page = await source.ReadPageAsync(
                    context: context,
                    request: new(
                        read: read,
                        scope: sourceScope,
                        continuation: continuation,
                        maximumItems: RootPageItems,
                        maximumBytes: MaximumBytes));
            }
            catch (CosmosMaterializationSourceException exception)
            {
                throw new InvalidOperationException(
                    $"{provider}/{tenant} Cosmos page failed: kind={exception.FailureKind}, "
                    + $"disposition={exception.Observation.Disposition}, "
                    + $"status={exception.Observation.StatusCode}, "
                    + $"substatus={exception.Observation.SubStatusCode}, "
                    + $"evidence={exception.Observation.EvidenceReference}.",
                    exception);
            }
            Require(
                page.State == MaterializationSourcePageState.MoreAvailable
                    ? page.Read.State == RelationQuerySourceReadState.Partial
                        && page.Continuation is not null
                    : page.Read.State == RelationQuerySourceReadState.Complete
                        && page.Continuation is null,
                $"{provider}/{tenant} root page completeness did not match its continuation state: "
                + $"{page.Read.State}/{page.State}.");
            var supplied = new RelationQuerySuppliedSourceInput(
                input: semantics.Root.Input.Id,
                logicalPartition: sourceScope.LogicalPartition,
                completeness: RelationQueryEvidenceCompleteness.Complete,
                observations: page.Read.Observations,
                evidenceReference: page.Read.EvidenceReference);
            var execution = await new RelationQueryPhysicalExecutor(readers).ExecuteAsync(
                new(
                    plan: semantics.Plan,
                    physicalPlan: plan.HydrationPhysicalPlan,
                    realization: semantics.Realization,
                    evaluation: new($"materialization-harness/{ProviderName(provider)}/{tenant}/{pageOrdinal}"),
                    suppliedSources: [supplied],
                    capabilities: RelationQueryRealizationRuntimeEvidence.ProjectCapabilities(
                        semantics.Plan,
                        semantics.Realization)));
            Require(execution.IsSuccessful, FormatExecutionFailure(provider, tenant, execution));
            var interpretation = RequireValue(
                execution.Interpretation,
                $"{provider}/{tenant} hydration returned no interpretation.");
            var relation = RequireValue(
                interpretation.Relation,
                $"{provider}/{tenant} hydration returned no relation output.");
            Require(
                relation.State == RelationQueryExecutionOutputState.Complete,
                $"{provider}/{tenant} hydration relation was incomplete.");
            Require(
                relation.Rows.Length == page.Read.Observations.Length,
                $"{provider}/{tenant} did not produce one output for every root order.");
            if (!relation.Rows.IsDefaultOrEmpty)
            {
                var mutations = relation.Rows.Select((row, ordinal) =>
                {
                    var itemId = row.Identity?.String
                        ?? throw new InvalidOperationException("The freight relation emitted no string identity.");
                    Require(
                        row.Value.GetProperty("tenantId").String == tenant,
                        $"{provider}/{tenant} produced a cross-tenant joined document '{itemId}'.");
                    return (MaterializationItemMutation)new MaterializationUpsert(
                        itemId: new(itemId),
                        mutationId: new($"mutation/{generationId.Value}/{tenant}/{pageOrdinalBase + pageOrdinal}/{ordinal}"),
                        version: new("1"),
                        value: row.Value);
                }).ToImmutableArray();
                var applied = await target.ApplyBatchAsync(
                    context: context,
                    request: new(
                        batchId: new($"batch/{generationId.Value}/{tenant}/{pageOrdinalBase + pageOrdinal}"),
                        generationId: generationId,
                        workerFence: workerFence,
                        mutations: mutations));
                Require(
                    applied.Disposition is MaterializationBatchDisposition.Applied
                        or MaterializationBatchDisposition.Replayed,
                    $"{provider}/{tenant} target batch failed: {applied.Disposition}.");
                Require(
                    applied.Outcomes.All(static outcome =>
                        outcome.Disposition is MaterializationItemOutcomeDisposition.Applied
                            or MaterializationItemOutcomeDisposition.Replayed),
                    $"{provider}/{tenant} target batch contained a rejected item.");
                generationRevision = applied.GenerationRevision
                    ?? throw new InvalidOperationException("The target batch returned no generation revision.");
            }
            progress = await SaveProgressAsync(
                context,
                progressKey,
                progress,
                page,
                pageOrdinal,
                run);
            continuation = page.Continuation;
            pageOrdinal++;
        } while (continuation is not null);

        var observed = await target.InspectGenerationAsync(context, generationId);
        Require(
            observed?.Revision == generationRevision,
            $"{provider}/{tenant} target generation revision drifted after paging.");
    }

    static async Task<MaterializationProgressSnapshot?> AcquireProgressAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationHarnessRunOptions run)
    {
        if (run.ProgressStore is null)
            return null;

        var owner = run.ProgressOwner!;
        var current = await run.ProgressStore.LoadAsync(context, key);
        if (current is not null && string.Equals(current.FenceOwner, owner, StringComparison.Ordinal))
            return current;

        var priorRevision = current?.Revision.Value ?? "none";
        var scopeIdentity = Uri.EscapeDataString(
            $"{key.Scope.Source.Value}/{key.Scope.Partition.Value}/{key.Scope.OrderingScope.Value}");
        var acquired = await run.ProgressStore.AcquireFenceAsync(
            context: context,
            key: key,
            mutationId: new($"claim/{key.Generation.Value}/{scopeIdentity}/{priorRevision}"),
            expectedRevision: current?.Revision,
            owner: owner);
        Require(
            acquired.Disposition is MaterializationProgressMutationDisposition.Applied
                or MaterializationProgressMutationDisposition.Replayed,
            $"Could not acquire durable page progress: {acquired.Disposition}.");
        return RequireValue(acquired.Snapshot, "Progress acquisition returned no snapshot.");
    }

    static async Task<MaterializationProgressSnapshot?> SaveProgressAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressSnapshot? progress,
        MaterializationSourcePage page,
        int pageOrdinal,
        MaterializationHarnessRunOptions run)
    {
        if (run.ProgressStore is null)
            return null;
        progress = RequireValue(progress, "A durable run has no acquired progress snapshot.");
        var ordinal = checked((long)pageOrdinal + 1);
        var scopeIdentity = Uri.EscapeDataString(
            $"{key.Scope.Source.Value}/{key.Scope.Partition.Value}/{key.Scope.OrderingScope.Value}");
        var checkpointId = new MaterializationCheckpointId(
            $"checkpoint/{key.Generation.Value}/{scopeIdentity}/{ordinal}");
        var checkpoint = new MaterializationApplicationCheckpoint(
            id: checkpointId,
            kind: page.State == MaterializationSourcePageState.MoreAvailable
                ? MaterializationCheckpointKind.BatchContinuation
                : MaterializationCheckpointKind.BatchCompleted,
            continuation: page.Continuation,
            completion: page.State == MaterializationSourcePageState.Exhausted
                ? MaterializationSourceReadCompletion.FromPage(page)
                : null,
            position: null,
            appliedDeliveries: [],
            committedAtUtc: context.UtcNow,
            evidenceReference: page.Read.EvidenceReference,
            batchPageOrdinal: ordinal);
        var saved = await run.ProgressStore.SaveCheckpointAsync(
            context: context,
            key: key,
            mutationId: new($"save/{checkpointId.Value}"),
            expectedRevision: progress.Revision,
            owner: run.ProgressOwner!,
            fence: progress.Fence,
            checkpoint: checkpoint);
        Require(
            saved.Disposition is MaterializationProgressMutationDisposition.Applied
                or MaterializationProgressMutationDisposition.Replayed,
            $"Could not persist durable page progress: {saved.Disposition}.");
        return RequireValue(saved.Snapshot, "Progress persistence returned no snapshot.");
    }

    internal static ProviderPlan CreateProviderPlan(
        ProviderKind provider,
        FreightOrderMaterializationSemantics semantics)
    {
        var prefix = ProviderName(provider);
        var profile = provider == ProviderKind.Postgres
            ? PostgresRelationQuerySourceTargetProfile.Default
            : CosmosRelationQuerySourceReader.TargetProfile;
        var partitionSelector = provider == ProviderKind.Postgres
            ? "tenantId"
            : CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector;
        var limits = new RelationQuerySourcePlacementLimits(
            MaximumBatchItems,
            MaximumRows,
            MaximumBatchItems,
            maximumConcurrency: 2);
        var domain = new RelationQueryExecutionDomainId($"materialization-harness/{prefix}/freight");
        var orderSource = new RelationQuerySourceInstanceId($"{prefix}/freight/orders");
        var customerSource = new RelationQuerySourceInstanceId($"{prefix}/freight/customers");
        var stopSource = new RelationQuerySourceInstanceId($"{prefix}/freight/order-stops");
        var locationSource = new RelationQuerySourceInstanceId($"{prefix}/freight/locations");
        ImmutableArray<RelationQuerySourceInstance> sources =
        [
            new(orderSource, domain, profile, limits),
            new(customerSource, domain, profile, limits),
            new(stopSource, domain, profile, limits),
            new(locationSource, domain, profile, limits)
        ];
        var bindings = ImmutableArray.CreateBuilder<RelationQuerySourcePlacementBinding>();
        foreach (var source in semantics.Plan.InputContract.Sources)
        {
            bindings.Add(new(
                new($"{prefix}/placement/{Uri.EscapeDataString(source.Input.Id.Value)}"),
                source.Input.Id,
                source.Node,
                source.Binding,
                source.Shape,
                orderSource,
                RelationQuerySourcePlacementBindingKind.SourceSet,
                RelationQuerySourceAcquisitionKind.Supplied,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(source.Shape, IdentitySelector(provider), FieldPath.FromField("id")),
                Fields(provider, source.Shape, source.Fields),
                partition: new(partitionSelector)));
        }
        foreach (var traversal in semantics.Plan.InputContract.Traversals)
        {
            bindings.Add(new(
                new($"{prefix}/placement/{Uri.EscapeDataString(traversal.Input.Id.Value)}"),
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                SourceForShape(
                    traversal.ResultShape,
                    customerSource,
                    stopSource,
                    locationSource),
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, IdentitySelector(provider), FieldPath.FromField("id")),
                Fields(provider, traversal.ResultShape, traversal.Fields),
                relationshipKeys: traversal.Input.Direction == RelationshipTraversalDirection.Inverse
                    ? [new(
                        traversal.Input.Id,
                        traversal.Definition.SourceReference,
                        provider == ProviderKind.Postgres
                            ? Column(traversal.ResultShape, traversal.Definition.SourceReference)
                            : CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(
                                traversal.Definition.SourceReference))]
                    : [],
                partition: new(partitionSelector)));
        }
        var hydrationPlacement = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(semantics.Plan),
            $"materialization-harness/{prefix}/hydration-placement/v1",
            sources,
            bindings.ToImmutable());
        var planningPolicy = new RelationQueryPhysicalPlanningPolicy(
            new($"materialization-harness/{prefix}/physical-policy/v1"),
            $"materialization-harness/{prefix}/physical-conventions/v1",
            MaximumBatchItems,
            MaximumRows,
            MaximumRows,
            MaximumBatchItems,
            MaximumBatchItems,
            maximumConcurrency: 2);
        var hydrationPhysical = RequirePhysicalPlan(
            RelationQueryPhysicalPlanner.Compile(
                semantics.Plan,
                semantics.Realization,
                hydrationPlacement,
                planningPolicy));
        var impactPlan = FreightOrderRebuildPlanCompiler.CompileImpactPlan(semantics, prefix);
        var impactPlacement = CreateImpactPlacement(
            provider: provider,
            semantics: semantics,
            hydrationPlacement: hydrationPlacement,
            impactPlan: impactPlan);
        var impactPhysical = CreateImpactPhysicalPlan(
            semantics: semantics,
            hydrationPhysical: hydrationPhysical,
            impactPlacement: impactPlacement);
        var scanBindings = hydrationPlacement.Bindings.Select(binding =>
            binding.Input == semantics.Root.Input.Id
                ? new RelationQuerySourcePlacementBinding(
                    binding.Id,
                    binding.Input,
                    binding.Node,
                    binding.Binding,
                    binding.Shape,
                    binding.Source,
                    binding.Kind,
                    RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                    binding.Origin,
                    binding.Identity,
                    binding.Fields,
                    binding.RelationshipKeys,
                    binding.Partition)
                : binding).ToImmutableArray();
        var scanPlacement = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(semantics.Plan),
            $"materialization-harness/{prefix}/scan-placement/v1",
            sources,
            scanBindings);
        var scanRoot = scanPlacement.Bindings.Single(binding => binding.Input == semantics.Root.Input.Id);
        var suppliedRoot = hydrationPhysical.Stages.Single(stage =>
            stage.Kind == RelationQueryPhysicalStageKind.SuppliedInput
            && stage.PlacementBinding == scanRoot.Id);
        var scanStages = hydrationPhysical.Stages.Select(stage =>
        {
            var provenance = new RelationQueryPhysicalStageProvenance(
                stage.Provenance.Nodes,
                stage.Provenance.Inputs,
                stage.Provenance.Requirements,
                capabilityEvidence: [],
                stage.Provenance.CompositionRules,
                stage.Provenance.OperatingBoundaries,
                stage.Provenance.PlacementBindings,
                stage.Provenance.LoweringRule,
                stage.Provenance.PolicyDecisions);
            return new RelationQueryPhysicalStage(
                stage.Id,
                stage.Id == suppliedRoot.Id
                    ? RelationQueryPhysicalStageKind.SourceRead
                    : stage.Kind,
                stage.Dependencies,
                stage.PlacementBinding,
                stage.SemanticInputs,
                stage.Id == suppliedRoot.Id
                    ? [.. semantics.Root.Fields.Select(static field => field.Input.Id)]
                    : stage.RequestedFields,
                stage.BatchSize,
                provenance);
        }).ToImmutableArray();
        var scanPhysical = new CompiledRelationQueryPhysicalPlan(
            CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(semantics.Plan),
            semantics.Realization.Fingerprint,
            scanPlacement,
            hydrationPhysical.Policy,
            scanStages,
            hydrationPhysical.Terminal,
            diagnostics: hydrationPhysical.Diagnostics);
        return new(
            hydrationPlacement,
            hydrationPhysical,
            impactPlan,
            impactPlacement,
            impactPhysical,
            scanPlacement,
            scanPhysical,
            scanRoot,
            orderSource,
            [customerSource, stopSource, locationSource]);
    }

    static RelationQuerySourcePlacement CreateImpactPlacement(
        ProviderKind provider,
        FreightOrderMaterializationSemantics semantics,
        RelationQuerySourcePlacement hydrationPlacement,
        MaterializationImpactPlan impactPlan)
    {
        var stepRelationships = impactPlan.Routes
            .SelectMany(static route => route.Strategy is MaterializationInverseTraversalImpactStrategy inverse
                ? inverse.Steps
                : [])
            .GroupBy(static step => step.ReferenceSourceInput)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static step => step.RelationshipInput)
                    .Distinct()
                    .OrderBy(static input => input.Value, StringComparer.Ordinal)
                    .ToImmutableArray());
        var rootInput = semantics.Root.Input.Id;
        var bindings = hydrationPlacement.Bindings.Select(binding =>
        {
            var relationshipKeys = binding.RelationshipKeys.ToBuilder();
            foreach (var relationshipInput in stepRelationships.GetValueOrDefault(binding.Input, []))
            {
                if (relationshipKeys.Any(key => key.Input == relationshipInput))
                    continue;
                var traversal = semantics.Plan.InputContract.Traversals.Single(candidate =>
                    candidate.Input.Id == relationshipInput);
                relationshipKeys.Add(new(
                    input: relationshipInput,
                    semanticPath: traversal.Definition.SourceReference,
                    sourceSelector: provider == ProviderKind.Postgres
                        ? Column(binding.Shape, traversal.Definition.SourceReference)
                        : CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(
                            traversal.Definition.SourceReference)));
            }
            return new RelationQuerySourcePlacementBinding(
                id: binding.Id,
                input: binding.Input,
                node: binding.Node,
                binding: binding.Binding,
                shape: binding.Shape,
                source: binding.Source,
                kind: binding.Input == rootInput
                    ? RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                    : binding.Kind,
                acquisition: binding.Input == rootInput
                    ? RelationQuerySourceAcquisitionKind.BoundedLookup
                    : binding.Acquisition,
                origin: binding.Origin,
                identity: binding.Identity,
                fields: binding.Fields,
                relationshipKeys: relationshipKeys.ToImmutable(),
                partition: binding.Partition);
        }).ToImmutableArray();
        return new(
            schemaVersion: RelationQuerySourcePlacement.CurrentSchemaVersion,
            plan: RelationQueryCompiledPlanReference.From(semantics.Plan),
            conventionSetVersion: $"materialization-harness/{ProviderName(provider)}/impact-placement/v1",
            sourceInstances: hydrationPlacement.SourceInstances,
            bindings: bindings);
    }

    static CompiledRelationQueryPhysicalPlan CreateImpactPhysicalPlan(
        FreightOrderMaterializationSemantics semantics,
        CompiledRelationQueryPhysicalPlan hydrationPhysical,
        RelationQuerySourcePlacement impactPlacement)
    {
        var root = impactPlacement.Bindings.Single(binding => binding.Input == semantics.Root.Input.Id);
        var rootStage = hydrationPhysical.Stages.Single(stage =>
            stage.PlacementBinding == root.Id
            && stage.Kind == RelationQueryPhysicalStageKind.SuppliedInput);
        var stages = hydrationPhysical.Stages.ToBuilder();
        var requestedFields = semantics.Root.Fields.Select(static field => field.Input.Id).ToImmutableArray();
        var provenanceInputs = requestedFields
            .Add(semantics.Root.Input.Id)
            .Distinct()
            .ToImmutableArray();
        var provenance = new RelationQueryPhysicalStageProvenance(
            nodes: [semantics.Root.Node],
            inputs: provenanceInputs,
            placementBindings: [root.Id]);
        stages.Add(new(
            id: new($"{rootStage.Id.Value}/impact-identity"),
            kind: RelationQueryPhysicalStageKind.BatchedIdentityLookup,
            dependencies: [rootStage.Id],
            placementBinding: root.Id,
            semanticInputs: [semantics.Root.Input.Id],
            requestedFields: requestedFields,
            batchSize: MaximumBatchItems,
            provenance: provenance));
        stages.Add(new(
            id: new($"{rootStage.Id.Value}/impact-predicate"),
            kind: RelationQueryPhysicalStageKind.BatchedPredicateLookup,
            dependencies: [rootStage.Id],
            placementBinding: root.Id,
            semanticInputs: [semantics.Root.Input.Id],
            requestedFields: requestedFields,
            batchSize: MaximumBatchItems,
            provenance: provenance));
        return new(
            schemaVersion: CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
            plan: RelationQueryCompiledPlanReference.From(semantics.Plan),
            realization: semantics.Realization.Fingerprint,
            placement: impactPlacement,
            policy: hydrationPhysical.Policy,
            stages: stages.ToImmutable(),
            terminal: hydrationPhysical.Terminal,
            diagnostics: hydrationPhysical.Diagnostics);
    }

    internal static async Task<FreightOrderRebuildPlanCompilation> CompilePostgresRebuildPlanAsync(
        FreightOrderMaterializationSemantics semantics,
        ProviderPlan plan,
        ElasticMaterializationTarget target,
        NpgsqlDataSource dataSource,
        string connectionString,
        ImmutableArray<string> tenants,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(journal);
        var hydrationStorage = CreatePostgresStorageBinding(
            placement: plan.HydrationPlacement,
            plan: semantics.Plan,
            purpose: "canonical-rebuild-hydration");
        var scanStorage = CreatePostgresStorageBinding(
            placement: plan.ScanPlacement,
            plan: semantics.Plan,
            purpose: "canonical-rebuild-scan");
        var impactStorage = CreatePostgresStorageBinding(
            placement: plan.ImpactPlacement,
            plan: semantics.Plan,
            purpose: "canonical-impact-reads");
        var rootRead = CreateRootRead(plan, semantics.Root);
        var bindings = ImmutableArray.CreateBuilder<FreightOrderRebuildTenantBinding>(tenants.Length);
        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = PostgresPolicy(tenant);
            var scanRuntime = new PostgresNpgsqlRuntimeBinding(
                database: scanStorage.Database,
                dataSource: dataSource,
                authority: "materialization-harness/postgres/canonical-rebuild-scan",
                logicalReplicationConnectionFactory: () =>
                    new LogicalReplicationConnection(connectionString));
            var rootReader = new PostgresRelationQuerySourceReader(
                plan: semantics.Plan,
                physicalPlan: plan.ScanPhysicalPlan,
                source: plan.OrderSource,
                storage: scanStorage,
                dataSource: dataSource,
                runtimeBinding: scanRuntime,
                policy: policy);
            var rootSource = new PostgresMaterializationSource(
                reader: rootReader,
                placement: plan.ScanRoot,
                continuationAuthenticationKey: ContinuationKey);
            var hydrationBindings = plan.HydrationSources.Select(sourceId =>
            {
                var runtime = new PostgresNpgsqlRuntimeBinding(
                    database: hydrationStorage.Database,
                    dataSource: dataSource,
                    authority: "materialization-harness/postgres/canonical-rebuild-hydration",
                    logicalReplicationConnectionFactory: () =>
                        new LogicalReplicationConnection(connectionString));
                var reader = new PostgresRelationQuerySourceReader(
                    plan: semantics.Plan,
                    physicalPlan: plan.HydrationPhysicalPlan,
                    source: sourceId,
                    storage: hydrationStorage,
                    dataSource: dataSource,
                    runtimeBinding: runtime,
                    policy: policy);
                return (Reader: reader, Runtime: runtime);
            }).ToImmutableArray();
            var hydrationReaders = hydrationBindings
                .Select(static binding => binding.Reader)
                .ToImmutableArray();
            var bindingBySource = hydrationBindings.ToImmutableDictionary(
                static binding => binding.Reader.Descriptor.Source);
            var sources = ImmutableArray.CreateBuilder<FreightOrderRebuildSourceBinding>(
                semantics.Definition.Sources.Length);
            foreach (var requirement in semantics.Definition.Sources)
            {
                var isRoot = requirement.Input == semantics.Root.Input.Id;
                var placement = isRoot
                    ? plan.ScanRoot
                    : plan.HydrationPlacement.Bindings.Single(candidate =>
                        candidate.Input == requirement.Input);
                var reader = isRoot ? rootReader : bindingBySource[placement.Source].Reader;
                var runtime = isRoot ? scanRuntime : bindingBySource[placement.Source].Runtime;
                var logicalSource = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(
                    reader: reader,
                    placement: placement,
                    runtimeBinding: runtime,
                    binding: new(
                        publicationName: FreightMaterializationChangeFeedConventions.PostgresPublicationName,
                        slotName: FreightMaterializationChangeFeedConventions.PostgresSlotName(
                            tenant: tenant,
                            input: requirement.Input),
                        slotGeneration: FreightMaterializationChangeFeedConventions.PostgresSlotGeneration(
                            journal: journal,
                            tenant: tenant,
                            input: requirement.Input),
                        expectedReplicaIdentity: new(
                            kind: PostgresLogicalReplicationReplicaIdentityKind.Full),
                        beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required),
                    positionAuthenticationKey: ContinuationKey,
                    policy: LocalPostgresChangePolicy,
                    cancellationToken: cancellationToken);
                var source = new PostgresFreightMaterializationChangeSource(
                    source: logicalSource,
                    requirement: requirement,
                    impactEvidenceReference: $"relations-physical-plan/{plan.ImpactPhysicalPlan.Fingerprint.Value}");
                var scope = new PostgresMaterializationSource(
                    reader: reader,
                    placement: placement,
                    continuationAuthenticationKey: ContinuationKey).Scope;
                sources.Add(new(
                    input: requirement.Input,
                    scope: scope,
                    source: source));
            }
            var hydrator = new RelationQueryMaterializationRebuildHydrator(
                plan: semantics.Plan,
                physicalPlan: plan.HydrationPhysicalPlan,
                realization: semantics.Realization,
                suppliedRoot: semantics.Root.Input.Id,
                output: semantics.Output,
                sourceReaders: hydrationReaders);
            var impactReaders = plan.ImpactPlacement.SourceInstances.Select(source =>
            {
                var runtime = new PostgresNpgsqlRuntimeBinding(
                    database: impactStorage.Database,
                    dataSource: dataSource,
                    authority: "materialization-harness/postgres/canonical-impact-reads");
                return (IRelationQuerySourceReader)new PostgresRelationQuerySourceReader(
                    plan: semantics.Plan,
                    physicalPlan: plan.ImpactPhysicalPlan,
                    source: source.Id,
                    storage: impactStorage,
                    dataSource: dataSource,
                    runtimeBinding: runtime,
                    policy: policy);
            }).ToImmutableArray();
            var impactReader = new FreightOrderMaterializationImpactReader(
                plan: semantics.Plan,
                physicalPlan: plan.ImpactPhysicalPlan,
                sourceReaders: impactReaders);
            bindings.Add(new(
                tenant: tenant,
                rootRead: rootRead,
                hydrator: hydrator,
                sourceBindings: sources.MoveToImmutable(),
                impactRuntimeFactory: impactPlan =>
                {
                    var inverse = new MaterializationInverseTraversalExecutor(
                        plan: impactPlan,
                        definition: semantics.Definition,
                        reader: impactReader.ReadAsync);
                    return new RelationQueryMaterializationImpactRuntime(
                        impactPlan: impactPlan,
                        definition: semantics.Definition,
                        physicalPlan: plan.HydrationPhysicalPlan,
                        realization: semantics.Realization,
                        sourceReaders: hydrationReaders,
                        rootResolver: inverse.ResolveRootsAsync);
                }));
        }
        return FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: target.Descriptor,
            tenantBindings: bindings.MoveToImmutable(),
            impactPlan: plan.ImpactPlan);
    }

    internal static FreightOrderRebuildPlanCompilation CompileCosmosRebuildPlan(
        FreightOrderMaterializationSemantics semantics,
        ProviderPlan plan,
        ElasticMaterializationTarget target,
        Database database,
        string databaseId,
        ImmutableArray<string> tenants,
        FreightScenarioJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var rootRead = CreateRootRead(plan, semantics.Root);
        var bindings = tenants.Select(tenant =>
        {
            var policy = CosmosPolicy(tenant);
            var rootReader = CreateCosmosReader(
                shape: semantics.Root.Shape,
                sourceId: plan.OrderSource,
                container: database.GetContainer("orders"),
                databaseId: databaseId,
                containerId: "orders",
                policy: policy);
            var hydrationReaders = plan.HydrationSources.Select(sourceId =>
                CreateCosmosHydrationReader(
                    semantics: semantics,
                    plan: plan,
                    sourceId: sourceId,
                    database: database,
                    databaseId: databaseId,
                    policy: policy)).ToImmutableArray();
            var readerBySource = hydrationReaders.ToImmutableDictionary(
                static reader => reader.Descriptor.Source);
            var sources = semantics.Definition.Sources.Select(requirement =>
            {
                var isRoot = requirement.Input == semantics.Root.Input.Id;
                var placement = isRoot
                    ? plan.ScanRoot
                    : plan.HydrationPlacement.Bindings.Single(candidate =>
                        candidate.Input == requirement.Input);
                var reader = isRoot ? rootReader : readerBySource[placement.Source];
                var source = CreateCosmosReconciliationSource(reader);
                var physicalPlan = isRoot
                    ? plan.ScanPhysicalPlan.Fingerprint
                    : plan.HydrationPhysicalPlan.Fingerprint;
                var scope = new MaterializationSourceScope(
                    physicalPlan: physicalPlan,
                    placement: placement,
                    logicalPartition: LogicalPartition(tenant),
                    partition: new($"cosmos/scenario-envelope/{Uri.EscapeDataString(databaseId)}/{tenant}/{Uri.EscapeDataString(requirement.Input.Value)}"),
                    orderingScope: new($"cosmos/scenario-envelope/{tenant}/{Uri.EscapeDataString(requirement.Input.Value)}/journal-order"));
                return new FreightOrderRebuildSourceBinding(
                    input: requirement.Input,
                    scope: scope,
                    source: new CosmosScenarioEnvelopeMaterializationChangeSource(
                        baseline: source,
                        container: CosmosContainer(
                            database: database,
                            shape: placement.Shape),
                        scope: scope,
                        placement: placement,
                        requirement: requirement,
                        journal: journal));
            }).ToImmutableArray();
            var hydrator = new RelationQueryMaterializationRebuildHydrator(
                plan: semantics.Plan,
                physicalPlan: plan.HydrationPhysicalPlan,
                realization: semantics.Realization,
                suppliedRoot: semantics.Root.Input.Id,
                output: semantics.Output,
                sourceReaders: hydrationReaders);
            var impactReaders = plan.ImpactPlacement.SourceInstances.Select(source =>
            {
                var placement = plan.ImpactPlacement.Bindings.First(binding =>
                    binding.Source == source.Id);
                return (IRelationQuerySourceReader)CreateCosmosReader(
                    shape: placement.Shape,
                    sourceId: source.Id,
                    container: CosmosContainer(database, placement.Shape),
                    databaseId: databaseId,
                    containerId: CosmosContainerId(placement.Shape),
                    policy: policy);
            }).ToImmutableArray();
            var impactReader = new FreightOrderMaterializationImpactReader(
                plan: semantics.Plan,
                physicalPlan: plan.ImpactPhysicalPlan,
                sourceReaders: impactReaders);
            return new FreightOrderRebuildTenantBinding(
                tenant: tenant,
                rootRead: rootRead,
                hydrator: hydrator,
                sourceBindings: sources,
                impactRuntimeFactory: impactPlan =>
                {
                    var inverse = new MaterializationInverseTraversalExecutor(
                        plan: impactPlan,
                        definition: semantics.Definition,
                        reader: impactReader.ReadAsync);
                    return new RelationQueryMaterializationImpactRuntime(
                        impactPlan: impactPlan,
                        definition: semantics.Definition,
                        physicalPlan: plan.HydrationPhysicalPlan,
                        realization: semantics.Realization,
                        sourceReaders: hydrationReaders,
                        rootResolver: inverse.ResolveRootsAsync);
                });
        }).ToImmutableArray();
        return FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "cosmos",
            target: target.Descriptor,
            tenantBindings: bindings,
            impactPlan: plan.ImpactPlan);
    }

    static PostgresRelationQueryStorageBinding CreatePostgresStorageBinding(
        RelationQuerySourcePlacement placement,
        CompiledRelationQueryPlan plan,
        string purpose)
    {
        var tables = placement.Bindings
            .Where(static binding => binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            .Select(binding =>
        {
            var table = Table(binding.Shape);
            var identitySemantics = IdentityTextSemantics(table.Constraint);
            var fields = binding.Fields.Select(field =>
            {
                var column = Column(binding.Shape, field.SemanticPath);
                var isIdentity = field.SemanticPath.Matches("id");
                return new PostgresRelationQueryFieldBinding(
                    field.Input,
                    field.SemanticPath,
                    column,
                    field.SemanticPath.Matches("sequence")
                        ? PostgresRelationQueryScalarType.Int32
                        : PostgresRelationQueryScalarType.Text,
                    PostgresRelationQueryMissingValueEncoding.Prohibited,
                    PostgresRelationQueryNullValueEncoding.Prohibited,
                    textSemantics: field.SemanticPath.Matches("sequence")
                        ? null
                        : isIdentity ? identitySemantics : EqualityTextSemantics(),
                    ordering: isIdentity
                        ? PostgresRelationQueryOrderingCapability.Exact
                            | PostgresRelationQueryOrderingCapability.StableUnique
                        : PostgresRelationQueryOrderingCapability.None);
            }).ToImmutableArray();
            var relationshipReferences = binding.RelationshipKeys.Select(key =>
            {
                var traversal = plan.InputContract.Traversals.Single(candidate => candidate.Input.Id == key.Input);
                return new PostgresRelationQueryRelationshipReferenceBinding(
                    input: key.Input,
                    semanticPath: key.SemanticPath,
                    columnName: key.SourceSelector,
                    scalarType: PostgresRelationQueryScalarType.Text,
                    uniqueness: traversal.Definition.SourceReferenceUniqueness,
                    missingValueEncoding: PostgresRelationQueryMissingValueEncoding.Prohibited,
                    nullValueEncoding: PostgresRelationQueryNullValueEncoding.Prohibited,
                    textSemantics: EqualityTextSemantics());
            }).ToImmutableArray();
            return new PostgresRelationQueryTableBinding(
                binding.Source,
                binding.Id,
                binding.Input,
                binding.Shape,
                PostgresSchema,
                table.Name,
                new(FieldPath.FromField("id"), table.IdentityColumn, PostgresRelationQueryScalarType.Text, identitySemantics),
                fields,
                relationshipReferences: relationshipReferences,
                partition: new(
                    "tenantId",
                    FieldPath.FromField("tenantId"),
                    "tenant_id",
                    PostgresRelationQueryScalarType.Text,
                    EqualityTextSemantics()));
        }).ToImmutableArray();
        return new(
            new($"materialization-harness/postgres/{purpose}/v1"),
            new("cohesive-materialization-harness"),
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            tables,
            compiledPlanFingerprint: RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(plan)),
            placementFingerprint: placement.Fingerprint);
    }

    internal static ElasticMaterializationTargetBinding CreateTargetBinding(
        ProviderKind provider,
        FreightOrderMaterializationSemantics semantics,
        ElasticClusterId clusterId)
    {
        var name = ProviderName(provider);
        var alias = $"freight-order-search-{name}";
        return new(
            new($"materialization-harness/elastic/{name}/v1"),
            clusterId,
            new($"freight-order-search/{name}"),
            semantics.Definition.Id,
            alias,
            $"cohesive-freight-{name}-",
            $".cohesive-freight-{name}-control",
            new(
                $"cohesive-freight-{name}-template",
                new("sha256", "materialization-harness/elastic-template/v1", new string('a', 64)),
                "materialization-harness/elastic-template/v1"),
            new("materialization-harness/process-runtime/v1", $"freight-order-search/{name}"),
            new(
                new($"materialization-harness/elastic-search/{name}/v1"),
                new($"elastic/freight/{name}"),
                new($"elastic/freight/{name}/placement"),
                ElasticRelationQueryTargetProfile.Target,
                ElasticRelationQueryTargetProfile.ProfileId,
                alias,
                []));
    }

    internal static ElasticMaterializationTarget CreateTarget(
        ElasticMaterializationTargetBinding binding,
        Uri endpoint)
    {
        ElasticsearchClientSettings settings = new(endpoint);
        settings = settings.ServerCertificateValidationCallback(static (_, _, _, _) => true);
        var client = new ElasticsearchClient(settings);
        var runtime = new ElasticElasticsearchRuntimeBinding(
            binding.Cluster,
            client,
            "materialization-harness/local-compose/v1");
        return new(binding, ElasticMaterializationTargetPolicy.Default, runtime);
    }

    internal static RelationQuerySourceReadRequest CreateRootRead(
        ProviderPlan plan,
        RelationQuerySourceInputContract root)
    {
        var stage = plan.ScanPhysicalPlan.Stages.Single(candidate =>
            candidate.PlacementBinding == plan.ScanRoot.Id
            && candidate.Kind == RelationQueryPhysicalStageKind.SourceRead);
        return new(
            plan.ScanPhysicalPlan.Fingerprint,
            stage.Id,
            plan.ScanRoot.Id,
            plan.OrderSource,
            root.Shape,
            plan.ScanRoot.Identity!.SourceSelector,
            [
                .. plan.ScanRoot.Fields
                    .Where(field => stage.RequestedFields.Contains(field.Input))
                    .Select(static field => new RelationQuerySourceReadField(
                        field.Input,
                        field.SemanticPath,
                        field.SourceSelector,
                        RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            new RelationQueryBoundedEnumeration(MaximumRows),
            MaximumRows);
    }

    static CosmosRelationQuerySourceReader CreateCosmosHydrationReader(
        FreightOrderMaterializationSemantics semantics,
        ProviderPlan plan,
        RelationQuerySourceInstanceId sourceId,
        Database database,
        string databaseId,
        CosmosRelationQuerySourcePolicy policy)
    {
        if (sourceId == plan.HydrationSources[0])
        {
            return CreateCosmosReader(
                FreightOrderMaterializationModel.CustomerAccountShapeId,
                sourceId,
                CosmosContainer(database, FreightOrderMaterializationModel.CustomerAccountShapeId),
                databaseId,
                CosmosContainerId(FreightOrderMaterializationModel.CustomerAccountShapeId),
                policy);
        }
        if (sourceId == plan.HydrationSources[1])
        {
            return CreateCosmosReader(
                FreightOrderMaterializationModel.OrderStopShapeId,
                sourceId,
                CosmosContainer(database, FreightOrderMaterializationModel.OrderStopShapeId),
                databaseId,
                CosmosContainerId(FreightOrderMaterializationModel.OrderStopShapeId),
                policy);
        }
        return CreateCosmosReader(
            FreightOrderMaterializationModel.LocationShapeId,
            sourceId,
            CosmosContainer(database, FreightOrderMaterializationModel.LocationShapeId),
            databaseId,
            CosmosContainerId(FreightOrderMaterializationModel.LocationShapeId),
            policy);
    }

    static Container CosmosContainer(Database database, QualifiedShapeId shape) =>
        database.GetContainer(CosmosContainerId(shape));

    static string CosmosContainerId(QualifiedShapeId shape) =>
        shape == FreightOrderMaterializationModel.OrderShapeId
            ? "orders"
            : shape == FreightOrderMaterializationModel.CustomerAccountShapeId
                ? "customerAccounts"
                : shape == FreightOrderMaterializationModel.OrderStopShapeId
                    ? "orderStops"
                    : shape == FreightOrderMaterializationModel.LocationShapeId
                        ? "locations"
                        : throw new ArgumentException($"Unsupported freight shape '{shape}'.", nameof(shape));

    static CosmosRelationQuerySourceReader CreateCosmosReader(
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId sourceId,
        Container container,
        string databaseId,
        string containerId,
        CosmosRelationQuerySourcePolicy policy)
    {
        var source = new RelationQuerySourceInstance(
            sourceId,
            new("materialization-harness/cosmos/freight"),
            CosmosRelationQuerySourceReader.TargetProfile,
            policy.GetEffectivePlacementLimits(new(
                MaximumBatchItems,
                MaximumRows,
                MaximumBatchItems,
                maximumConcurrency: 2)));
        return new(shape, source, container, databaseId, containerId, policy);
    }

    static CosmosRelationQuerySourcePolicy CosmosPolicy(string tenant) => new(
        CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector,
        LogicalPartition(tenant),
        CosmosRelationQueryCrossPartitionPolicy.Prohibit,
        new PartitionKey(tenant),
        MaximumRows,
        MaximumBatchItems,
        maximumQueryChunks: 4,
        maximumSdkPageSize: RootPageItems);

    static PostgresRelationQuerySourcePolicy PostgresPolicy(string tenant) => new(
        MaximumBatchItems,
        MaximumRows,
        MaximumRows,
        MaximumBytes,
        partitionScope: new(
            LogicalPartition(tenant),
            "tenantId",
            tenant));

    static RelationQueryLogicalPartitionIdentity LogicalPartition(string tenant) =>
        new($"materialization-harness/freight/tenant/{tenant}");

    static InMemoryMaterializationSource CreateCosmosReconciliationSource(
        CosmosRelationQuerySourceReader reader)
    {
        ImmutableArray<string> references =
        [
            "cohesive.materialization-harness/cosmos-vnext/reconciliation/v1",
            "cohesive.storage/in-memory-materialization-source/v1",
            "cohesive.adapters.cosmos/relation-query-source/v1"
        ];
        ImmutableArray<MaterializationGuaranteeKind> readGuarantees =
        [
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.RequestLocalCompleteness,
            MaterializationGuaranteeKind.Reconciliation
        ];
        var profile = new MaterializationCapabilityProfile(
            new($"materialization-harness/cosmos/reconciliation/{reader.Descriptor.Source.Value}"),
            MaterializationEndpointRole.Source,
            reader.Descriptor.Source.Value,
            [
                new(
                    new("bounded-enumeration"),
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    CapabilityRealizationKind.Composed,
                    readGuarantees,
                    [
                        new(MaterializationLimitKind.ReadItems, MaximumRows),
                        new(MaterializationLimitKind.ReadBytes, MaximumBytes)
                    ],
                    references,
                    "The real Cosmos relation reader supplies deterministic bounded reads; the reference source pages the immutable result for reconciliation rebuilds."),
                new(
                    new("continuation"),
                    MaterializationCapabilityKind.SourceContinuation,
                    CapabilityRealizationKind.Composed,
                    [
                        MaterializationGuaranteeKind.StableOrdering,
                        MaterializationGuaranteeKind.Reconciliation
                    ],
                    [],
                    references,
                    "Authenticated provider-neutral in-memory offsets resume the deterministic Cosmos relation result.")
            ],
            "Local Cosmos vNext reconciliation source; it does not claim a coordinated snapshot or change-feed catch-up.");
        return new(new(reader, profile));
    }

    internal static CosmosClient CreateCosmosClient(string connectionString)
    {
        CosmosClientOptions options = new()
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = CreateCosmosHttpClient,
            LimitToEndpoint = true,
            Serializer = new CosmosSystemTextJsonSerializer()
        };
        return new(connectionString, options);
    }

    static HttpClient CreateCosmosHttpClient()
    {
        HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
            request.RequestUri?.IsLoopback == true || errors == SslPolicyErrors.None;
        return new(handler, disposeHandler: true);
    }

    static ImmutableArray<RelationQuerySourceFieldBinding> Fields(
        ProviderKind provider,
        QualifiedShapeId shape,
        ImmutableArray<RelationQueryFieldInputContract> fields) =>
    [
        .. fields.Select(field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            provider == ProviderKind.Postgres
                ? Column(shape, field.Input.Field.Path)
                : CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(field.Input.Field.Path)))
    ];

    static string IdentitySelector(ProviderKind provider) => provider == ProviderKind.Postgres
        ? "id"
        : CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector;

    static RelationQuerySourceInstanceId SourceForShape(
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId customer,
        RelationQuerySourceInstanceId stop,
        RelationQuerySourceInstanceId location) => shape == FreightOrderMaterializationModel.CustomerAccountShapeId
        ? customer
        : shape == FreightOrderMaterializationModel.OrderStopShapeId
            ? stop
        : shape == FreightOrderMaterializationModel.LocationShapeId
            ? location
            : throw new InvalidOperationException($"No provider source is registered for shape '{shape}'.");

    static (string Name, string IdentityColumn, string Constraint) Table(QualifiedShapeId shape) =>
        shape == FreightOrderMaterializationModel.OrderShapeId
            ? ("orders", "order_id", "ck_freight_harness_order_id_ascii")
            : shape == FreightOrderMaterializationModel.CustomerAccountShapeId
                ? ("customer_accounts", "customer_account_id", "ck_freight_harness_customer_id_ascii")
                : shape == FreightOrderMaterializationModel.OrderStopShapeId
                    ? ("order_stops", "order_stop_id", "ck_freight_harness_stop_id_ascii")
                : shape == FreightOrderMaterializationModel.LocationShapeId
                    ? ("locations", "location_id", "ck_freight_harness_location_id_ascii")
                    : throw new InvalidOperationException($"No PostgreSQL table is registered for shape '{shape}'.");

    static string Column(QualifiedShapeId shape, FieldPath path)
    {
        var field = path.ToString();
        if (field == "tenantId") return "tenant_id";
        if (shape == FreightOrderMaterializationModel.OrderShapeId)
        {
            return field switch
            {
                "id" => "order_id",
                "orderNumber" => "order_number",
                "customerAccountId" => "customer_account_id",
                "equipmentClass" => "equipment_class",
                _ => throw UnknownColumn(shape, path)
            };
        }
        if (shape == FreightOrderMaterializationModel.CustomerAccountShapeId)
        {
            return field switch
            {
                "id" => "customer_account_id",
                "displayName" => "display_name",
                _ => throw UnknownColumn(shape, path)
            };
        }
        if (shape == FreightOrderMaterializationModel.LocationShapeId)
        {
            return field switch
            {
                "id" => "location_id",
                "displayName" => "display_name",
                "city" => "city",
                "region" => "region",
                _ => throw UnknownColumn(shape, path)
            };
        }
        if (shape == FreightOrderMaterializationModel.OrderStopShapeId)
        {
            return field switch
            {
                "id" => "order_stop_id",
                "orderId" => "order_id",
                "sequence" => "sequence",
                "stopType" => "stop_type",
                "locationId" => "location_id",
                _ => throw UnknownColumn(shape, path)
            };
        }
        throw UnknownColumn(shape, path);
    }

    static Exception UnknownColumn(QualifiedShapeId shape, FieldPath path) =>
        new InvalidOperationException($"No PostgreSQL column is registered for '{shape}/{path}'.");

    static PostgresRelationQueryTextSemantics EqualityTextSemantics() => new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal);

    static PostgresRelationQueryTextSemantics IdentityTextSemantics(string constraint) => new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal,
        PostgresRelationQueryTextOrderingSemantics.Ordinal,
        new(constraint, "materialization-harness/postgres-schema/v1"));

    static CompiledRelationQueryPhysicalPlan RequirePhysicalPlan(
        RelationQueryPhysicalPlanningResult result) => result.Plan
        ?? throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    internal static async Task<ElasticClusterId> ReadClusterIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return new(document.RootElement.GetProperty("cluster_uuid").GetString()
            ?? throw new InvalidOperationException("Elasticsearch returned no cluster UUID."));
    }

    internal static async Task EnsureLocalElasticTemplatesAsync(
        HttpClient client,
        ElasticMaterializationTargetBinding binding,
        string provider)
    {
        await PutJsonAsync(
            client,
            $"/_index_template/cohesive-freight-{provider}-generations",
            $$"""
            {
              "index_patterns": ["{{binding.GenerationIndexPrefix}}*"],
              "priority": 500,
              "template": { "settings": { "index.number_of_replicas": 0 } }
            }
            """);
        await PutJsonAsync(
            client,
            $"/_index_template/cohesive-freight-{provider}-control",
            $$"""
            {
              "index_patterns": ["{{binding.ControlIndexName}}"],
              "priority": 500,
              "template": { "settings": { "index.number_of_replicas": 0 } }
            }
            """);
        using var controlSettings = new HttpRequestMessage(
            System.Net.Http.HttpMethod.Put,
            $"/{Uri.EscapeDataString(binding.ControlIndexName)}/_settings")
        {
            Content = new StringContent(
                "{\"index\":{\"number_of_replicas\":0}}",
                Encoding.UTF8,
                "application/json")
        };
        using var response = await client.SendAsync(controlSettings);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    static async Task PutJsonAsync(HttpClient client, string path, string json)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Put, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    static async Task<ImmutableArray<string>> ReadAliasIndicesAsync(HttpClient client, string alias)
    {
        using var response = await client.GetAsync($"/_alias/{Uri.EscapeDataString(alias)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return [.. document.RootElement.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static value => value, StringComparer.Ordinal)];
    }

    internal static async Task<ImmutableArray<string>> ReadCanonicalDocumentsAsync(HttpClient client, string alias)
    {
        using var response = await client.GetAsync(
            $"/{Uri.EscapeDataString(alias)}/_search?size=100&filter_path=hits.hits._source.value");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return
        [
            .. document.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
                .Select(static hit => hit.GetProperty("_source").GetProperty("value").GetRawText())
                .OrderBy(static value => value, StringComparer.Ordinal)
        ];
    }

    static string FormatExecutionFailure(
        ProviderKind provider,
        string tenant,
        RelationQueryPhysicalExecutionResult result) =>
        $"{provider}/{tenant} hydration failed ({result.Status}): "
        + string.Join(" ", result.Diagnostics.Select(static diagnostic => diagnostic.Message));

    internal static string ProviderName(ProviderKind provider) => provider switch
    {
        ProviderKind.Postgres => "postgres",
        ProviderKind.Cosmos => "cosmos",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported source provider.")
    };

    static void PrintResult(ProviderResult result) => Console.WriteLine(
        $"{result.Provider}: alias={result.ReadAlias}, index={result.GenerationIndex}, documents={result.Documents.Length}");

    static T RequireValue<T>(T? value, string message)
        where T : class => value ?? throw new InvalidOperationException(message);

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal enum ProviderKind
    {
        Postgres,
        Cosmos
    }

    internal sealed record ProviderPlan(
        RelationQuerySourcePlacement HydrationPlacement,
        CompiledRelationQueryPhysicalPlan HydrationPhysicalPlan,
        MaterializationImpactPlan ImpactPlan,
        RelationQuerySourcePlacement ImpactPlacement,
        CompiledRelationQueryPhysicalPlan ImpactPhysicalPlan,
        RelationQuerySourcePlacement ScanPlacement,
        CompiledRelationQueryPhysicalPlan ScanPhysicalPlan,
        RelationQuerySourcePlacementBinding ScanRoot,
        RelationQuerySourceInstanceId OrderSource,
        ImmutableArray<RelationQuerySourceInstanceId> HydrationSources);

    sealed record ProviderResult(
        string Provider,
        string ReadAlias,
        string GenerationIndex,
        string DefinitionFingerprint,
        ImmutableArray<string> Documents);

    internal sealed record HarnessOptions(
        string PostgresConnectionString,
        string CosmosConnectionString,
        string CosmosDatabase,
        Uri ElasticsearchEndpoint,
        ImmutableArray<string> Tenants,
        string ScenarioPath)
    {
        public static HarnessOptions FromEnvironment() => new(
            Required("COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING"),
            Required("COHESIVE_MATERIALIZATION_COSMOS_CONNECTION_STRING"),
            Required("COHESIVE_MATERIALIZATION_COSMOS_DATABASE"),
            new(Required("COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT"), UriKind.Absolute),
            [
                .. (Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_TENANTS")
                        ?? "acme,northwind")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static tenant => tenant, StringComparer.Ordinal)
            ],
            Required("COHESIVE_MATERIALIZATION_SCENARIO_PATH"));

        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Set {name} before running materialization.");
    }

}
