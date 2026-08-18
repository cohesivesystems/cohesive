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
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.MaterializationHarness.Materialize;

static class Program
{
    const int RootPageItems = 2;
    const int MaximumBatchItems = 64;
    const int MaximumRows = 128;
    const long MaximumBytes = 1 * 1024 * 1024;
    const string PostgresSchema = "freight_harness";
    static readonly byte[] ContinuationKey =
        "cohesive-materialization-harness-local-key-v1"u8.ToArray();

    public static async Task<int> Main()
    {
        var options = HarnessOptions.FromEnvironment();
        var semantics = FreightOrderMaterializationModel.Create();
        using HttpClient elasticHttp = new() { BaseAddress = options.ElasticsearchEndpoint };
        var clusterId = await ReadClusterIdAsync(elasticHttp);

        var postgres = await MaterializeProviderAsync(
            ProviderKind.Postgres,
            semantics,
            options,
            clusterId,
            elasticHttp);
        var cosmos = await MaterializeProviderAsync(
            ProviderKind.Cosmos,
            semantics,
            options,
            clusterId,
            elasticHttp);

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
        return 0;
    }

    static async Task<ProviderResult> MaterializeProviderAsync(
        ProviderKind provider,
        FreightOrderMaterializationSemantics semantics,
        HarnessOptions options,
        ElasticClusterId clusterId,
        HttpClient elasticHttp)
    {
        var plan = CreateProviderPlan(provider, semantics);
        var targetBinding = CreateTargetBinding(provider, semantics, clusterId);
        await EnsureLocalElasticTemplatesAsync(elasticHttp, targetBinding, ProviderName(provider));
        var target = CreateTarget(targetBinding, options.ElasticsearchEndpoint);
        var context = OperationContext.Create();
        var before = await target.InspectAsync(context);
        var run = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        var generationId = new MaterializationGenerationId($"{ProviderName(provider)}/{run}");
        var generationIndex = targetBinding.GetGenerationIndexName(generationId);
        var workerFence = MaterializationWorkerFence.Initial;
        var begun = await target.BeginGenerationAsync(
            context,
            new(
                semantics.Definition.Id,
                generationId,
                semantics.DefinitionFingerprint,
                workerFence,
                DateTimeOffset.UtcNow));
        Require(
            begun.Disposition == MaterializationTargetOperationDisposition.Applied,
            $"{provider} generation begin failed: {begun.Disposition}.");
        var generation = RequireValue(begun.Generation, $"{provider} generation begin returned no snapshot.");
        var pageOrdinal = 0;
        var itemCount = 0;

        if (provider == ProviderKind.Postgres)
        {
            await using var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
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
                itemCount += await MaterializeTenantAsync(
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
                    pageOrdinal);
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
                itemCount += await MaterializeTenantAsync(
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
                    pageOrdinal);
                pageOrdinal += 1_000;
                generation = RequireValue(
                    await target.InspectGenerationAsync(context, generationId),
                    $"{provider} generation disappeared while loading.");
            }
        }

        Require(itemCount > RootPageItems, $"{provider} did not cross the configured root page boundary.");
        var aliasBeforePromotion = await ReadAliasIndicesAsync(elasticHttp, targetBinding.ReadAlias);
        Require(
            !aliasBeforePromotion.Contains(generationIndex, StringComparer.Ordinal),
            $"{provider} candidate generation was exposed through the read alias before promotion.");

        var sealedResult = await target.SealGenerationAsync(
            context,
            new(
                new($"seal/{ProviderName(provider)}/{run}"),
                generationId,
                generation.Revision,
                workerFence,
                DateTimeOffset.UtcNow));
        Require(
            sealedResult.Disposition == MaterializationTargetOperationDisposition.Applied,
            $"{provider} generation seal failed: {sealedResult.Disposition}.");
        var sealedGeneration = RequireValue(sealedResult.Generation, $"{provider} seal returned no generation.");
        var sealReceipt = RequireValue(sealedResult.Receipt, $"{provider} seal returned no receipt.");
        var validated = await target.ValidateGenerationAsync(
            context,
            new(
                new($"validate/{ProviderName(provider)}/{run}"),
                generationId,
                sealedGeneration.Revision,
                sealReceipt.Fingerprint,
                itemCount,
                "materialization-harness/freight-readback/v1",
                workerFence,
                DateTimeOffset.UtcNow));
        Require(
            validated.Disposition == MaterializationTargetOperationDisposition.Applied,
            $"{provider} generation validation failed: {validated.Disposition}.");
        var validatedGeneration = RequireValue(validated.Generation, $"{provider} validation returned no generation.");
        var validationReceipt = RequireValue(validated.Receipt, $"{provider} validation returned no receipt.");
        Require(validationReceipt.Validation.IsValid, $"{provider} generation validation was inconclusive.");
        var promotionFence = new MaterializationPromotionFence(
            (before.LatestPromotionFence?.Ordinal + 1 ?? 1).ToString(CultureInfo.InvariantCulture));
        var promoted = await target.PromoteGenerationAsync(
            context,
            new(
                new($"promote/{ProviderName(provider)}/{run}"),
                generationId,
                validatedGeneration.Revision,
                validationReceipt.Fingerprint,
                before.ActiveGenerationId,
                before.Revision,
                workerFence,
                promotionFence,
                DateTimeOffset.UtcNow));
        Require(
            promoted.Disposition == MaterializationTargetOperationDisposition.Applied,
            $"{provider} generation promotion failed: {promoted.Disposition}.");

        var aliasAfterPromotion = await ReadAliasIndicesAsync(elasticHttp, targetBinding.ReadAlias);
        Require(
            aliasAfterPromotion.SequenceEqual([generationIndex], StringComparer.Ordinal),
            $"{provider} read alias did not atomically resolve to exactly the promoted generation.");
        var documents = await ReadCanonicalDocumentsAsync(elasticHttp, targetBinding.ReadAlias);
        Require(documents.Length == itemCount, $"{provider} alias readback count differs from materialized output.");
        return new(
            ProviderName(provider),
            targetBinding.ReadAlias,
            generationIndex,
            semantics.DefinitionFingerprint.Value,
            documents);
    }

    static async Task<int> MaterializeTenantAsync(
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
        int pageOrdinalBase)
    {
        var context = OperationContext.Create();
        var read = CreateRootRead(plan, semantics.Root);
        MaterializationSourceContinuation? continuation = null;
        var generationRevision = initialRevision;
        var pageOrdinal = 0;
        var outputCount = 0;
        do
        {
            MaterializationSourcePage page;
            try
            {
                page = await source.ReadPageAsync(
                    context,
                    new(read, sourceScope, continuation, RootPageItems, MaximumBytes));
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
                semantics.Root.Input.Id,
                RelationQueryEvidenceCompleteness.Complete,
                page.Read.Observations,
                page.Read.EvidenceReference);
            var execution = await new RelationQueryPhysicalExecutor(readers).ExecuteAsync(
                new(
                    semantics.Plan,
                    plan.HydrationPhysicalPlan,
                    semantics.Realization,
                    new($"materialization-harness/{ProviderName(provider)}/{tenant}/{pageOrdinal}"),
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
                        new(itemId),
                        new($"mutation/{generationId.Value}/{tenant}/{pageOrdinalBase + pageOrdinal}/{ordinal}"),
                        new("1"),
                        row.Value);
                }).ToImmutableArray();
                var applied = await target.ApplyBatchAsync(
                    context,
                    new(
                        new($"batch/{generationId.Value}/{tenant}/{pageOrdinalBase + pageOrdinal}"),
                        generationId,
                        workerFence,
                        mutations));
                Require(
                    applied.Disposition == MaterializationBatchDisposition.Applied,
                    $"{provider}/{tenant} target batch failed: {applied.Disposition}.");
                Require(
                    applied.Outcomes.All(static outcome =>
                        outcome.Disposition == MaterializationItemOutcomeDisposition.Applied),
                    $"{provider}/{tenant} target batch contained a rejected item.");
                generationRevision = applied.GenerationRevision
                    ?? throw new InvalidOperationException("The target batch returned no generation revision.");
                outputCount += mutations.Length;
            }
            continuation = page.Continuation;
            pageOrdinal++;
        } while (continuation is not null);

        var observed = await target.InspectGenerationAsync(context, generationId);
        Require(
            observed?.Revision == generationRevision,
            $"{provider}/{tenant} target generation revision drifted after paging.");
        return outputCount;
    }

    static ProviderPlan CreateProviderPlan(
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
        var locationSource = new RelationQuerySourceInstanceId($"{prefix}/freight/locations");
        ImmutableArray<RelationQuerySourceInstance> sources =
        [
            new(orderSource, domain, profile, limits),
            new(customerSource, domain, profile, limits),
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
                    locationSource),
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, IdentitySelector(provider), FieldPath.FromField("id")),
                Fields(provider, traversal.ResultShape, traversal.Fields),
                relationshipKeys: [],
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
            hydrationPhysical.Terminal);
        return new(
            hydrationPlacement,
            hydrationPhysical,
            scanPlacement,
            scanPhysical,
            scanRoot,
            orderSource,
            [customerSource, locationSource]);
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
            return new PostgresRelationQueryTableBinding(
                binding.Source,
                binding.Id,
                binding.Input,
                binding.Shape,
                PostgresSchema,
                table.Name,
                new(FieldPath.FromField("id"), table.IdentityColumn, PostgresRelationQueryScalarType.Text, identitySemantics),
                fields,
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

    static ElasticMaterializationTargetBinding CreateTargetBinding(
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

    static ElasticMaterializationTarget CreateTarget(
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

    static RelationQuerySourceReadRequest CreateRootRead(
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
                database.GetContainer("customerAccounts"),
                databaseId,
                "customerAccounts",
                policy);
        }
        return CreateCosmosReader(
            FreightOrderMaterializationModel.LocationShapeId,
            sourceId,
            database.GetContainer("locations"),
            databaseId,
            "locations",
            policy);
    }

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
        partitionScope: new("tenantId", tenant));

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

    static CosmosClient CreateCosmosClient(string connectionString)
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
        RelationQuerySourceInstanceId location) => shape == FreightOrderMaterializationModel.CustomerAccountShapeId
        ? customer
        : shape == FreightOrderMaterializationModel.LocationShapeId
            ? location
            : throw new InvalidOperationException($"No provider source is registered for shape '{shape}'.");

    static (string Name, string IdentityColumn, string Constraint) Table(QualifiedShapeId shape) =>
        shape == FreightOrderMaterializationModel.OrderShapeId
            ? ("orders", "order_id", "ck_freight_harness_order_id_ascii")
            : shape == FreightOrderMaterializationModel.CustomerAccountShapeId
                ? ("customer_accounts", "customer_account_id", "ck_freight_harness_customer_id_ascii")
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
                "pickupStopId" => "pickup_stop_id",
                "deliveryStopId" => "delivery_stop_id",
                "originLocationId" => "origin_location_id",
                "destinationLocationId" => "destination_location_id",
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

    static async Task<ElasticClusterId> ReadClusterIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return new(document.RootElement.GetProperty("cluster_uuid").GetString()
            ?? throw new InvalidOperationException("Elasticsearch returned no cluster UUID."));
    }

    static async Task EnsureLocalElasticTemplatesAsync(
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

    static async Task<ImmutableArray<string>> ReadCanonicalDocumentsAsync(HttpClient client, string alias)
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

    static string ProviderName(ProviderKind provider) => provider switch
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

    enum ProviderKind
    {
        Postgres,
        Cosmos
    }

    sealed record ProviderPlan(
        RelationQuerySourcePlacement HydrationPlacement,
        CompiledRelationQueryPhysicalPlan HydrationPhysicalPlan,
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

    sealed record HarnessOptions(
        string PostgresConnectionString,
        string CosmosConnectionString,
        string CosmosDatabase,
        Uri ElasticsearchEndpoint,
        ImmutableArray<string> Tenants)
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
            ]);

        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Set {name} before running materialization.");
    }

}
