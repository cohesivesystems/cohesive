using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresMaterializationBackendRouterTests
{
    static readonly DateTimeOffset Epoch = new(
        year: 2026,
        month: 8,
        day: 19,
        hour: 12,
        minute: 0,
        second: 0,
        offset: TimeSpan.Zero);
    static readonly MaterializationId MaterializationId = new("materialization/postgres-backend-routing");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        algorithm: "sha256",
        canonicalization: "tests/postgres-materialization-backend-routing/v1",
        value: new string('a', 64));
    static readonly MaterializationBackendRoutingFence FenceOne = new("1");
    static readonly MaterializationBackendRoutingFence FenceTwo = new("2");

    [PostgresFact]
    public async Task LocalPostgres_ConcurrentRoutersLinearizeOneSameRevisionMutation()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"ari430_routing_concurrency_{Guid.NewGuid():N}";
        var options = new PostgresMaterializationStateStoreOptions(
            authorityId: $"authority/routing-concurrency/{Guid.NewGuid():N}",
            schema: schema);
        var context = OperationContext.Create();
        var rig = await RoutingRig.CreateAsync();

        try
        {
            var firstRouter = Router(dataSource, options, rig);
            var secondRouter = Router(dataSource, options, rig);
            await firstRouter.EnsureCreatedAsync(context);
            var initialized = await firstRouter.SwapAsync(
                context,
                SwapRequest(
                    rig,
                    commandId: "command/concurrent-initialize",
                    expectedRevision: MaterializationBackendRoutingRevision.Initial,
                    fence: FenceOne,
                    issuedAtUtc: At(4),
                    read: rig.First.Read,
                    write: rig.First.Generation));
            var candidate = await rig.CreateAndActivateCandidateAsync();
            var firstRequest = new MaterializationAdmitBackendCandidateRequest(
                header: Header(
                    rig,
                    commandId: "command/concurrent-first",
                    expectedRevision: initialized.Snapshot.Revision,
                    fence: FenceOne,
                    issuedAtUtc: At(20)),
                candidate: candidate.Generation);
            var secondRequest = new MaterializationAdmitBackendCandidateRequest(
                header: Header(
                    rig,
                    commandId: "command/concurrent-second",
                    expectedRevision: initialized.Snapshot.Revision,
                    fence: FenceOne,
                    issuedAtUtc: At(20)),
                candidate: candidate.Generation);

            var results = await Task.WhenAll(
                firstRouter.AdmitCandidateAsync(context, firstRequest).AsTask(),
                secondRouter.AdmitCandidateAsync(context, secondRequest).AsTask());
            var restored = await Router(dataSource, options, rig).InspectAsync(context, rig.Scope);

            Assert.Single(results, static result =>
                result.Disposition == MaterializationBackendRoutingDisposition.Applied);
            Assert.Single(results, static result =>
                result.Disposition == MaterializationBackendRoutingDisposition.RevisionConflict);
            Assert.Equal(new MaterializationBackendRoutingRevision("2"), restored.Revision);
            Assert.Equal(candidate.Generation, restored.Candidate);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task LocalPostgres_RestartPreservesCanonicalReplayFenceAndCleanupAuthority()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string disappeared after test discovery.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"ari430_routing_{Guid.NewGuid():N}";
        var options = new PostgresMaterializationStateStoreOptions(
            authorityId: $"authority/routing-restart/{Guid.NewGuid():N}",
            schema: schema);
        var context = OperationContext.Create();
        var rig = await RoutingRig.CreateAsync();

        try
        {
            var firstHost = Router(dataSource, options, rig);
            await firstHost.EnsureCreatedAsync(context);
            var initialRequest = SwapRequest(
                rig,
                commandId: "command/initialize",
                expectedRevision: MaterializationBackendRoutingRevision.Initial,
                fence: FenceOne,
                issuedAtUtc: At(4),
                read: rig.First.Read,
                write: rig.First.Generation);
            var initialized = await firstHost.SwapAsync(context, initialRequest);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, initialized.Disposition);
            var candidate = await rig.CreateAndActivateCandidateAsync();
            var swapRequest = SwapRequest(
                rig,
                commandId: "command/swap-candidate",
                expectedRevision: new("2"),
                fence: FenceOne,
                issuedAtUtc: At(21),
                read: candidate.Read,
                write: candidate.Generation);
            var admissionRequest = new MaterializationAdmitBackendCandidateRequest(
                header: Header(
                    rig,
                    commandId: "command/admit-candidate",
                    expectedRevision: initialized.Snapshot.Revision,
                    fence: FenceOne,
                    issuedAtUtc: At(20)),
                candidate: candidate.Generation,
                expectedFollowUp: swapRequest);
            var admitted = await firstHost.AdmitCandidateAsync(context, admissionRequest);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
            Assert.Equal(swapRequest.Header.ExpectedRevision, admitted.Snapshot.Revision);
            var promotionRestart = Router(dataSource, options, rig);
            var swapped = await promotionRestart.SwapAsync(context, swapRequest);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
            Assert.Single(swapped.Snapshot.Draining);
            var beforeRestart = await promotionRestart.CaptureAsync(context);

            var restarted = Router(dataSource, options, rig);
            var restored = await restarted.InspectAsync(context, rig.Scope);
            var afterRestart = await restarted.CaptureAsync(context);
            var replayedSwap = await restarted.SwapAsync(context, swapRequest);
            var identityConflict = await restarted.SwapAsync(
                context,
                SwapRequest(
                    rig,
                    commandId: swapRequest.Header.CommandId.Value,
                    expectedRevision: swapRequest.Header.ExpectedRevision,
                    fence: swapRequest.Header.Fence,
                    issuedAtUtc: At(22),
                    read: candidate.Read,
                    write: candidate.Generation));
            var takeover = await restarted.AdmitCandidateAsync(
                context,
                new(
                    header: Header(
                        rig,
                        commandId: "command/takeover",
                        expectedRevision: MaterializationBackendRoutingRevision.Initial,
                        fence: FenceTwo,
                        issuedAtUtc: At(22)),
                    candidate: rig.First.Generation));

            var afterTakeoverRestart = Router(dataSource, options, rig);
            var stale = await afterTakeoverRestart.AdmitCandidateAsync(
                context,
                new(
                    header: Header(
                        rig,
                        commandId: "command/stale-owner",
                        expectedRevision: swapped.Snapshot.Revision,
                        fence: FenceOne,
                        issuedAtUtc: At(23)),
                    candidate: rig.First.Generation));
            var drain = Assert.Single(stale.Snapshot.Draining);
            var completed = await afterTakeoverRestart.CompleteDrainAsync(
                context,
                new(
                    header: Header(
                        rig,
                        commandId: "command/complete-drain",
                        expectedRevision: stale.Snapshot.Revision,
                        fence: FenceTwo,
                        issuedAtUtc: At(25)),
                    proof: new(
                        placementSlice: rig.Scope,
                        generation: rig.First.Generation,
                        admissionsClosedAtRevision: drain.AdmissionsClosedAtRevision,
                        inFlightOperationCount: 0,
                        quiescenceToken: "quiescence/postgres-restart",
                        observedAtUtc: At(24))));
            var retired = await afterTakeoverRestart.RetireAsync(
                context,
                new(
                    header: Header(
                        rig,
                        commandId: "command/retire",
                        expectedRevision: completed.Snapshot.Revision,
                        fence: FenceTwo,
                        issuedAtUtc: At(26)),
                    generation: rig.First.Generation));
            var reservationRequest = new MaterializationReserveBackendCleanupRequest(
                header: Header(
                    rig,
                    commandId: "command/reserve-cleanup",
                    expectedRevision: retired.Snapshot.Revision,
                    fence: FenceTwo,
                    issuedAtUtc: At(27)),
                generation: rig.First.Generation);
            var reserved = await afterTakeoverRestart.ReserveCleanupAsync(context, reservationRequest);

            var afterReservationRestart = Router(dataSource, options, rig);
            var replayedReservation = await afterReservationRestart.ReserveCleanupAsync(context, reservationRequest);
            var cleanupRequest = new MaterializationCleanupBackendGenerationRequest(
                header: Header(
                    rig,
                    commandId: "command/cleanup",
                    expectedRevision: reserved.Routing.Snapshot.Revision,
                    fence: FenceTwo,
                    issuedAtUtc: At(102)),
                proof: new(
                    placementSlice: rig.Scope,
                    generation: rig.First.Generation,
                    retiredAtRevision: retired.Snapshot.Revision,
                    reservationToken: reserved.Reservation!.Token,
                    cleanupFingerprint: "cleanup/postgres-physical-receipt",
                    observedAtUtc: At(101)));
            var cleaned = await afterReservationRestart.CleanupAsync(context, cleanupRequest);

            var finalRestart = Router(dataSource, options, rig);
            var final = await finalRestart.InspectAsync(context, rig.Scope);
            var readmission = await finalRestart.AdmitCandidateAsync(
                context,
                new(
                    header: Header(
                        rig,
                        commandId: "command/readmit-cleaned",
                        expectedRevision: final.Revision,
                        fence: FenceTwo,
                        issuedAtUtc: At(103)),
                    candidate: rig.First.Generation));

            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, initialized.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, admitted.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, swapped.Disposition);
            Assert.Equal(swapped.Snapshot.Revision, restored.Revision);
            Assert.Equal(
                MaterializationBackendRoutingAuthorityJsonSerializer.GetCanonicalBytes(beforeRestart),
                MaterializationBackendRoutingAuthorityJsonSerializer.GetCanonicalBytes(afterRestart));
            Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedSwap.Disposition);
            Assert.Equal(swapped.Receipt, replayedSwap.Receipt);
            Assert.Equal(MaterializationBackendRoutingDisposition.IdentityConflict, identityConflict.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.RevisionConflict, takeover.Disposition);
            Assert.Equal(FenceTwo, takeover.Snapshot.LatestFence);
            Assert.Equal(MaterializationBackendRoutingDisposition.StaleFence, stale.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, completed.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, retired.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, reserved.Routing.Disposition);
            Assert.Equal(MaterializationBackendRoutingDisposition.Replayed, replayedReservation.Routing.Disposition);
            Assert.Equal(reserved.Reservation, replayedReservation.Reservation);
            Assert.Equal(MaterializationBackendRoutingDisposition.Applied, cleaned.Disposition);
            Assert.Contains(rig.First.Generation, final.Cleaned);
            Assert.Equal(MaterializationBackendRoutingDisposition.StateConflict, readmission.Disposition);

            await using var tamper = dataSource.CreateCommand($$"""
                UPDATE {{options.QualifiedTable}}
                SET document_fingerprint = 'sha256-v1:tampered'
                WHERE authority_id = @authority_id;
                """);
            tamper.Parameters.AddWithValue(
                parameterName: "authority_id",
                value: $"{options.AuthorityId}/backend-routing");
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync(context.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => finalRestart.CaptureAsync(context).AsTask());
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    static PostgresMaterializationBackendRouter Router(
        NpgsqlDataSource dataSource,
        PostgresMaterializationStateStoreOptions options,
        RoutingRig rig) =>
        new(
            dataSource: dataSource,
            options: options,
            document: rig.Document,
            targets: rig.Pool,
            timeProvider: new FixedTimeProvider(At(100)));

    static MaterializationSwapBackendRoutingRequest SwapRequest(
        RoutingRig rig,
        string commandId,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc,
        MaterializationReadableBackendReference read,
        MaterializationBackendGenerationReference write) =>
        new(
            header: Header(
                rig: rig,
                commandId: commandId,
                expectedRevision: expectedRevision,
                fence: fence,
                issuedAtUtc: issuedAtUtc),
            read: read,
            write: write,
            configuration: MaterializationBackendRoutingConfigurationResolver.Resolve(
                definition: rig.Definition,
                layers:
                [
                    new(
                        origin: EffectiveConfigurationOrigin.Explicit,
                        authority: "tests/postgres-materialization-routing/v1",
                        settings: new(
                            readTarget: read.Generation.TargetId,
                            writeTarget: write.TargetId))
                ]));

    static MaterializationBackendRoutingCommandHeader Header(
        RoutingRig rig,
        string commandId,
        MaterializationBackendRoutingRevision expectedRevision,
        MaterializationBackendRoutingFence fence,
        DateTimeOffset issuedAtUtc) =>
        new(
            commandId: new(commandId),
            placementSlice: rig.Scope,
            expectedRevision: expectedRevision,
            fence: fence,
            issuedAtUtc: issuedAtUtc);

    static DateTimeOffset At(int minute) => Epoch.AddMinutes(minute);

    sealed class RoutingRig
    {
        RoutingRig(
            MaterializationBackendPoolDefinition definition,
            MaterializationBackendPoolDocument document,
            InMemoryMaterializationTarget target,
            InMemoryMaterializationTargetPool pool,
            MaterializationPlacementSliceReference scope,
            BackendFixture first)
        {
            Definition = definition;
            Document = document;
            Target = target;
            Pool = pool;
            Scope = scope;
            First = first;
        }

        internal MaterializationBackendPoolDefinition Definition { get; }

        internal MaterializationBackendPoolDocument Document { get; }

        internal InMemoryMaterializationTarget Target { get; }

        internal InMemoryMaterializationTargetPool Pool { get; }

        internal MaterializationPlacementSliceReference Scope { get; }

        internal BackendFixture First { get; }

        internal static async Task<RoutingRig> CreateAsync()
        {
            var target = new InMemoryMaterializationTarget(Descriptor(new("target/postgres-routing")));
            MaterializationBackendPoolDefinition definition = new(
                id: new("pool/postgres-routing"),
                materializationId: MaterializationId,
                definitionFingerprint: DefinitionFingerprint,
                members: [target.Descriptor],
                defaultTarget: target.Descriptor.Id,
                provenance: new(
                    producer: new("tests", "1"),
                    source: new("tests/postgres-materialization-backend-routing"),
                    origin: DocumentOrigin.Generated));
            var document = MaterializationBackendPoolDocument.FromDefinition(definition);
            var pool = new InMemoryMaterializationTargetPool(
                definition: definition,
                targets: [target]);
            var scope = MaterializationPlacementSliceReference.Create(
                materialization: MaterializationBackendPoolReference.FromDocument(document).Materialization,
                membership: new(
                    algorithm: "sha256",
                    canonicalization: "tests/postgres-materialization-membership/v1",
                    value: new string('b', 64)),
                pool: MaterializationBackendPoolReference.FromDocument(document),
                target: target.Descriptor.Id,
                subjects: [new("placement-subject/tenant-a")]);
            var first = await ActivateAsync(
                target: target,
                scope: scope,
                suffix: "first",
                createdAtUtc: At(0));
            return new(
                definition: definition,
                document: document,
                target: target,
                pool: pool,
                scope: scope,
                first: first);
        }

        internal async Task<BackendFixture> CreateAndActivateCandidateAsync() =>
            await ActivateAsync(
                target: Target,
                scope: Scope,
                suffix: "candidate",
                createdAtUtc: At(10));
    }

    static async Task<BackendFixture> ActivateAsync(
        InMemoryMaterializationTarget target,
        MaterializationPlacementSliceReference scope,
        string suffix,
        DateTimeOffset createdAtUtc)
    {
        MaterializationGenerationId generationId = new($"generation/{suffix}");
        var begun = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(
                materializationId: MaterializationId,
                generationId: generationId,
                definitionFingerprint: DefinitionFingerprint,
                workerFence: MaterializationWorkerFence.Initial,
                createdAtUtc: createdAtUtc));
        var written = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                batchId: new($"batch/{suffix}"),
                generationId: generationId,
                workerFence: MaterializationWorkerFence.Initial,
                mutations:
                [
                    new MaterializationUpsert(
                        itemId: new($"item/{suffix}"),
                        mutationId: new($"mutation/{suffix}"),
                        version: new("1"),
                        value: ObservationValue.FromString(suffix))
                ]));
        var sealedResult = await target.SealGenerationAsync(
            OperationContext.Create(),
            new(
                sealId: new($"seal/{suffix}"),
                generationId: generationId,
                expectedRevision: written.GenerationRevision!.Value,
                workerFence: MaterializationWorkerFence.Initial,
                sealedAtUtc: createdAtUtc.AddMinutes(1)));
        var validated = await target.ValidateGenerationAsync(
            OperationContext.Create(),
            new(
                validationId: new($"validation/{suffix}"),
                generationId: generationId,
                expectedRevision: sealedResult.Generation!.Revision,
                expectedSealFingerprint: sealedResult.Receipt!.Fingerprint,
                expectedVisibleItemCount: 1,
                validator: "tests/postgres-materialization-routing-validator/v1",
                workerFence: MaterializationWorkerFence.Initial,
                validatedAtUtc: createdAtUtc.AddMinutes(2)));
        var beforePromotion = await target.InspectAsync(OperationContext.Create());
        var promoted = await target.PromoteGenerationAsync(
            OperationContext.Create(),
            new(
                promotionId: new($"promotion/{suffix}"),
                generationId: generationId,
                expectedGenerationRevision: validated.Generation!.Revision,
                validationFingerprint: validated.Receipt!.Fingerprint,
                expectedActiveGenerationId: beforePromotion.ActiveGenerationId,
                expectedTargetRevision: beforePromotion.Revision,
                generationWorkerFence: MaterializationWorkerFence.Initial,
                promotionFence: MaterializationPromotionFence.Initial,
                promotedAtUtc: createdAtUtc.AddMinutes(3)));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, begun.Disposition);
        Assert.Equal(MaterializationBatchDisposition.Applied, written.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, sealedResult.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, validated.Disposition);
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, promoted.Disposition);

        MaterializationBackendGenerationReference generation = new(
            targetId: target.Descriptor.Id,
            generationId: generationId,
            definitionFingerprint: DefinitionFingerprint);
        var receipt = promoted.Receipt!;
        MaterializationActiveGenerationReference activation = new(
            schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
            authority: Authority(scope),
            generation: generationId,
            targetRevision: receipt.TargetRevision,
            promotion: receipt.PromotionId,
            promotionFence: receipt.PromotionFence,
            validation: receipt.ValidationFingerprint,
            activatedAtUtc: receipt.PromotedAtUtc);
        return new(
            Generation: generation,
            Read: new(
                placementSlice: scope,
                generation: generation,
                activation: activation));
    }

    static MaterializationRebuildLeafExecutionAuthority Authority(
        MaterializationPlacementSliceReference placementSlice)
    {
        MaterializationRebuildRequestReference request = new(
            schemaVersion: MaterializationRebuildRequestReference.CurrentSchemaVersion,
            materialization: placementSlice.Materialization,
            request: new(
                algorithm: "sha256",
                canonicalization: "tests/postgres-materialization-routing-request/v1",
                value: new string('c', 64)));
        MaterializationRebuildPlanSetReference planSet = new(
            schemaVersion: MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request: request,
            planSet: new(
                algorithm: "sha256",
                canonicalization: "tests/postgres-materialization-routing-plan-set/v1",
                value: new string('d', 64)));
        return new(
            schemaVersion: MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet: planSet,
            binding: new(
                slice: placementSlice,
                leafPlan: new(
                    plan: new(
                        algorithm: "sha256",
                        canonicalization: "tests/postgres-materialization-routing-plan/v1",
                        value: new string('e', 64)),
                    placementSlice: placementSlice.Fingerprint)));
    }

    static MaterializationTargetDescriptor Descriptor(MaterializationTargetId targetId)
    {
        MaterializationCapabilityEvidence Evidence(
            string id,
            MaterializationCapabilityKind capability,
            ImmutableArray<MaterializationGuaranteeKind> guarantees,
            ImmutableArray<MaterializationOperatingLimit> limits = default) =>
            new(
                id: new($"{targetId.Value}/{id}"),
                capability: capability,
                realization: CapabilityRealizationKind.Native,
                guarantees: guarantees,
                operatingLimits: limits.IsDefault ? [] : limits,
                sourceReferences: ["cohesive.storage.in-memory/postgres-routing-tests/v1"]);

        ImmutableArray<MaterializationOperatingLimit> writeLimits =
        [
            new(kind: MaterializationLimitKind.WriteItems, maximum: 16),
            new(kind: MaterializationLimitKind.WriteBytes, maximum: 1_000_000)
        ];
        return new(
            id: targetId,
            materializationId: MaterializationId,
            capabilities: new(
                id: new($"profile/{targetId.Value}"),
                role: MaterializationEndpointRole.Target,
                subject: targetId.Value,
                evidence:
                [
                    Evidence(
                        "isolation",
                        MaterializationCapabilityKind.TargetGenerationIsolation,
                        [MaterializationGuaranteeKind.FencedMutation, MaterializationGuaranteeKind.GenerationIsolation]),
                    Evidence(
                        "outcomes",
                        MaterializationCapabilityKind.TargetPerItemOutcomes,
                        [MaterializationGuaranteeKind.ExactPerItemOutcome],
                        writeLimits),
                    Evidence(
                        "promotion",
                        MaterializationCapabilityKind.TargetFencedPromotion,
                        [MaterializationGuaranteeKind.AtomicPromotion, MaterializationGuaranteeKind.FencedPromotion]),
                    Evidence(
                        "seal",
                        MaterializationCapabilityKind.TargetSeal,
                        [MaterializationGuaranteeKind.FencedMutation]),
                    Evidence(
                        "upsert",
                        MaterializationCapabilityKind.TargetBulkUpsert,
                        [
                            MaterializationGuaranteeKind.FencedMutation,
                            MaterializationGuaranteeKind.IdempotentWrite,
                            MaterializationGuaranteeKind.VersionConditionalWrite
                        ],
                        writeLimits),
                    Evidence(
                        "validation",
                        MaterializationCapabilityKind.TargetValidation,
                        [MaterializationGuaranteeKind.FencedMutation])
                ]));
    }

    sealed record BackendFixture(
        MaterializationBackendGenerationReference Generation,
        MaterializationReadableBackendReference Read);

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
            {
                Skip = "Set COHESIVE_POSTGRES_TEST_CONNECTION_STRING or run the materialization harness.";
            }
        }
    }
}
