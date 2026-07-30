using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Transactions;
using Cohesive.Adapters.Postgres;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQuerySourceReaderTests
{
    [Fact]
    public async Task IdentityBatch_UsesOneTypedAnyCommandAndProjectsSqlNullAsMissing()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", Optional: null, "parent-1"),
            new("item-b", "Beta", "present", "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var request = fixture.Read(
            fixture.PointPlacement,
            [Field(fixture.PointOptionalInput, "optional")],
            new RelationQueryIdentityBatchLookup(["item-a", "item-b"]));

        var result = await fixture.Reader.ReadAsync(request);

        Assert.True(
            result.State == RelationQuerySourceReadState.Complete,
            result.EvidenceReference);
        Assert.Equal(["item-a", "item-b"], result.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQuerySourceReadFieldState.Missing, result.Observations[0].Fields[0].State);
        Assert.Equal("present", result.Observations[1].Fields[0].Value?.String);
        var command = Assert.Single(executor.Commands);
        Assert.Contains("= ANY($1)", command.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("item-a", command.Text, StringComparison.Ordinal);
        var parameter = Assert.Single(command.Parameters);
        Assert.True(parameter.IsArray);
        Assert.Equal(PostgresRelationQueryScalarType.Text, parameter.ScalarType);
        Assert.Equal(["item-a", "item-b"], Assert.IsType<string[]>(parameter.Value));
        Assert.Equal(Policy.MaximumPageBytes, command.MaximumResultBytes);
    }

    [Fact]
    public async Task RelationshipBatch_UsesOneBoundedSetProbeAndReturnsCorrelationField()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var relationshipField = new RelationQuerySourceReadField(
            input: null,
            fixture.ParentPath,
            ParentSelector,
            RelationQuerySourceReadFieldPurpose.Correlation);
        var request = fixture.Read(
            fixture.TraversalPlacement,
            [relationshipField],
            new RelationQueryRelationshipKeyBatchLookup(
                fixture.ParentPath,
                ParentSelector,
                ["parent-1"]));

        var result = await fixture.Reader.ReadAsync(request);

        Assert.True(
            result.State == RelationQuerySourceReadState.Complete,
            result.EvidenceReference);
        Assert.Equal(["item-a", "item-b"], result.Observations.Select(static row => row.Identity));
        Assert.All(result.Observations, row => Assert.Equal("parent-1", row.Fields[0].Value?.String));
        var command = Assert.Single(executor.Commands);
        Assert.Contains(
            "FROM unnest($1) AS \"requested\"(\"key\") CROSS JOIN LATERAL",
            command.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "(\"source\".\"parent_id\" COLLATE \"C\") = (\"requested\".\"key\" COLLATE \"C\")",
            command.Text,
            StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(command.Text, "LIMIT 11", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("COUNT(*) OVER", command.Text, StringComparison.Ordinal);
        Assert.Single(command.Parameters);
    }

    [Fact]
    public async Task RelationshipBatch_RejectsTypedAliasesBeforeTheSetProbe()
    {
        List<PostgresNpgsqlCommand> commands = [];
        var fixture = CreateFixture(
            (command, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                commands.Add(command);
                return ValueTask.FromResult(new PostgresNpgsqlCommandResult([["item-a", true]]));
            },
            relationshipScalarType: PostgresRelationQueryScalarType.Boolean);
        var request = fixture.Read(
            fixture.TraversalPlacement,
            [],
            new RelationQueryRelationshipKeyBatchLookup(
                fixture.ParentPath,
                ParentSelector,
                ["TRUE", "true"]));

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("key-encoding-noncanonical", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(result.Observations);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task IdentityBatch_RejectsAnOversizedCanonicalUtf8KeyBeforeIo()
    {
        List<PostgresNpgsqlCommand> commands = [];
        var policy = new PostgresRelationQuerySourcePolicy(
            maximumBatchKeys: 10,
            maximumRowsPerRead: 10,
            maximumPageItems: 3,
            maximumPageBytes: 1_000,
            maximumKeyBytes: 5);
        var fixture = CreateFixture(
            (command, _) =>
            {
                commands.Add(command);
                return ValueTask.FromResult(new PostgresNpgsqlCommandResult([]));
            },
            policy);
        var request = fixture.Read(
            fixture.PointPlacement,
            [],
            new RelationQueryIdentityBatchLookup(["item-a"]));

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("key-boundary-exceeded", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Enumeration_UsesProbeRowAndReturnsPartialAtTheDeclaredBoundary()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var request = fixture.Read(
            fixture.SourcePlacement,
            [Field(fixture.NameInput, "name")],
            new RelationQueryBoundedEnumeration(maximumRows: 2),
            maximumBufferedRows: 2);

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Partial, result.State);
        Assert.Equal(["item-a", "item-b"], result.Observations.Select(static row => row.Identity));
        Assert.EndsWith("LIMIT 3", Assert.Single(executor.Commands).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPropagatesAndAffinityFailuresAvoidIo()
    {
        var executor = new TableExecutor([new("item-a", "Alpha", null, "parent-1")]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var request = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Reader.ReadAsync(request, cancellation.Token));
        Assert.Empty(executor.Commands);

        var foreign = new RelationQuerySourceReadRequest(
            new("sha256", "tests/physical-plan/v1", "foreign"),
            request.Stage,
            request.PlacementBinding,
            request.Source,
            request.Shape,
            request.IdentitySelector,
            request.Fields,
            request.Constraint,
            request.MaximumBufferedRows);
        var result = await fixture.Reader.ReadAsync(foreign);
        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("physical-plan-mismatch", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);

        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            var ambient = await fixture.Reader.ReadAsync(request);
            Assert.Equal(RelationQuerySourceReadState.Failed, ambient.State);
            Assert.Contains("ambient-transaction-not-supported", ambient.EvidenceReference, StringComparison.Ordinal);
        }
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task CancellationDuringAndImmediatelyAfterExecutorPropagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightFixture = CreateFixture(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new([]);
        });
        var inFlightRequest = inFlightFixture.Read(
            inFlightFixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        using var inFlightCancellation = new CancellationTokenSource();

        var inFlightRead = inFlightFixture.Reader
            .ReadAsync(inFlightRequest, inFlightCancellation.Token)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inFlightCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inFlightRead);

        using var postExecutorCancellation = new CancellationTokenSource();
        CancellationToken observedToken = default;
        var postExecutorFixture = CreateFixture((_, cancellationToken) =>
        {
            observedToken = cancellationToken;
            postExecutorCancellation.Cancel();
            return ValueTask.FromResult(new PostgresNpgsqlCommandResult([]));
        });
        var postExecutorRequest = postExecutorFixture.Read(
            postExecutorFixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await postExecutorFixture.Reader.ReadAsync(
                postExecutorRequest,
                postExecutorCancellation.Token));
        Assert.Equal(postExecutorCancellation.Token, observedToken);
    }

    [Fact]
    public async Task ProviderFailureIsSanitizedIntoStableEvidence()
    {
        const string secret = "password=do-not-leak";
        var fixture = CreateFixture((_, _) => throw new InvalidOperationException(secret));
        var request = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("provider-read-failed/InvalidOperationException", result.EvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializationPagesUseExclusiveDurableKeysetsWithoutOffset()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [Field(fixture.NameInput, "name")],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var context = OperationContext.Create();
        MaterializationSourceContinuation? continuation = null;
        List<string> identities = [];
        MaterializationSourcePage? page = null;

        do
        {
            page = await source.ReadPageAsync(
                context,
                new(read, source.Scope, continuation, maximumItems: 1, maximumBytes: 1_000_000));
            identities.AddRange(page.Read.Observations.Select(static row => row.Identity));
            continuation = page.Continuation;
            if (continuation is not null)
            {
                var json = JsonSerializer.Serialize(
                    continuation,
                    MaterializationJsonSerializer.CreateOptions());
                continuation = JsonSerializer.Deserialize<MaterializationSourceContinuation>(
                    json,
                    MaterializationJsonSerializer.CreateOptions());
            }
        }
        while (page.State == MaterializationSourcePageState.MoreAvailable);

        Assert.Equal(["item-a", "item-b", "item-c"], identities);
        Assert.Equal(RelationQuerySourceReadState.Complete, page.Read.State);
        Assert.Equal(3, executor.Commands.Count);
        Assert.All(executor.Commands, command => Assert.DoesNotContain("OFFSET", command.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(executor.Commands[0].Parameters);
        Assert.Single(executor.Commands[1].Parameters, static parameter => !parameter.IsArray);
        Assert.Single(executor.Commands[2].Parameters, static parameter => !parameter.IsArray);
    }

    [Fact]
    public async Task MaterializationReadBoundaryExhaustsAsPartialWithoutContinuation()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 2),
            maximumBufferedRows: 2);

        var page = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 2, maximumBytes: 1_000_000));

        Assert.Equal(MaterializationSourcePageState.Exhausted, page.State);
        Assert.Equal(RelationQuerySourceReadState.Partial, page.Read.State);
        Assert.Null(page.Continuation);
        Assert.Contains(page.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresMaterializationSource.ReadBoundaryReachedDiagnosticCode);
    }

    [Fact]
    public async Task MaterializationRejectsTamperedContinuationBeforeIo()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1_000_000));
        var valid = Assert.IsType<MaterializationSourceContinuation>(first.Continuation);
        var tampered = new MaterializationSourceContinuation(
            valid.FormatVersion,
            valid.ReadFingerprint,
            valid.Scope,
            string.Concat(valid.Value, "x"));
        var callsBeforeResume = executor.Commands.Count;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, source.Scope, tampered, maximumItems: 1, maximumBytes: 1_000_000)));
        Assert.Equal(callsBeforeResume, executor.Commands.Count);
    }

    [Fact]
    public async Task MaterializationRejectsContinuationRewrappedForAnotherReadBeforeIo()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var originalRead = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(originalRead, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1_000_000));
        var valid = Assert.IsType<MaterializationSourceContinuation>(first.Continuation);
        var otherRead = fixture.Read(
            fixture.SourcePlacement,
            [Field(fixture.NameInput, "name")],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var rewrapped = new MaterializationSourceContinuation(
            valid.FormatVersion,
            MaterializationSourceReadFingerprinter.Compute(otherRead),
            valid.Scope,
            valid.Value);
        var callsBeforeResume = executor.Commands.Count;

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(otherRead, source.Scope, rewrapped, maximumItems: 1, maximumBytes: 1_000_000)));

        Assert.Contains("conflicts with the read", exception.Message, StringComparison.Ordinal);
        Assert.Equal(callsBeforeResume, executor.Commands.Count);
    }

    [Fact]
    public async Task MaterializationRejectsForgedCanonicalContinuationBeforeIo()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1_000_000));
        var valid = Assert.IsType<MaterializationSourceContinuation>(first.Continuation);
        var invalid = RewriteContinuationIdentity(valid, "item-z");
        var callsBeforeResume = executor.Commands.Count;

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, source.Scope, invalid, maximumItems: 1, maximumBytes: 1_000_000)));

        Assert.Contains("failed authentication", exception.Message, StringComparison.Ordinal);
        Assert.Equal(callsBeforeResume, executor.Commands.Count);
    }

    [Fact]
    public async Task MaterializationRejectsOversizedContinuationBeforeIo()
    {
        var executor = new TableExecutor([new("item-a", "Alpha", null, "parent-1")]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var oversized = new MaterializationSourceContinuation(
            2,
            MaterializationSourceReadFingerprinter.Compute(read),
            source.Scope,
            string.Concat("postgres-keyset/v2/", new string('A', 4 * 1024 * 1024)));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, source.Scope, oversized, maximumItems: 1, maximumBytes: 1_000_000)));

        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task MaterializationEnforcesCanonicalByteAndCapabilityBounds()
    {
        var executor = new TableExecutor([new("item-a", "Alpha", null, "parent-1")]);
        var policy = new PostgresRelationQuerySourcePolicy(10, 10, 2, maximumPageBytes: 1_000);
        var fixture = CreateFixture(executor.ExecuteAsync, policy);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [Field(fixture.NameInput, "name")],
            new RelationQueryBoundedEnumeration(maximumRows: 10));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, source.Scope, continuation: null, maximumItems: 3, maximumBytes: 1_000)));
        Assert.Empty(executor.Commands);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1)));
        Assert.Single(executor.Commands);
    }

    [Fact]
    public void MaterializationProfileClaimsReconciliationButNotSnapshotOrChanges()
    {
        var fixture = CreateFixture(new TableExecutor([]).ExecuteAsync);
        Assert.Throws<ArgumentException>(() => new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            new byte[31]));
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);

        Assert.Contains(source.Descriptor.CapabilityProfile.Evidence, evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceBoundedEnumeration
            && evidence.Guarantees.Contains(MaterializationGuaranteeKind.Reconciliation)
            && !evidence.Guarantees.Contains(MaterializationGuaranteeKind.CoordinatedSnapshot));
        Assert.Contains(source.Descriptor.CapabilityProfile.Evidence, static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceContinuation);
        Assert.DoesNotContain(source.Descriptor.CapabilityProfile.Evidence, static evidence =>
            evidence.Capability is MaterializationCapabilityKind.SourceChangeDelivery
                or MaterializationCapabilityKind.SourceSettlement);
    }

    [Fact]
    public void MaterializationRejectsAnUnboundedContinuationProfileAtConstruction()
    {
        var fixture = CreateFixture(
            new TableExecutor([]).ExecuteAsync,
            sourcePlacementId: new string('p', 2_100_000));

        var exception = Assert.Throws<ArgumentException>(() => new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey));

        Assert.Equal("reader", exception.ParamName);
        Assert.Contains("bounded portable continuation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializationCapabilitiesMatchAndExecuteForTheExactPlacementScope()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var sourceSet = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var pointSource = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.PointPlacement,
            ContinuationAuthenticationKey);
        var traversal = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.TraversalPlacement,
            ContinuationAuthenticationKey);

        Assert.Equal(
            [MaterializationCapabilityKind.SourceBoundedEnumeration, MaterializationCapabilityKind.SourceContinuation],
            sourceSet.Descriptor.CapabilityProfile.Evidence.Select(static evidence => evidence.Capability).Order());
        Assert.Equal(
            [
                MaterializationCapabilityKind.SourceBatchedPointRead,
                MaterializationCapabilityKind.SourceContinuation
            ],
            pointSource.Descriptor.CapabilityProfile.Evidence.Select(static evidence => evidence.Capability).Order());
        Assert.Equal(
            [
                MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                MaterializationCapabilityKind.SourceContinuation
            ],
            traversal.Descriptor.CapabilityProfile.Evidence.Select(static evidence => evidence.Capability).Order());

        var enumeration = await sourceSet.ReadPageAsync(
            OperationContext.Create(),
            new(
                fixture.Read(
                    fixture.SourcePlacement,
                    [Field(fixture.NameInput, "name")],
                    new RelationQueryBoundedEnumeration(maximumRows: 10)),
                sourceSet.Scope,
                continuation: null,
                maximumItems: 3,
                maximumBytes: 1_000_000));
        var point = await pointSource.ReadPageAsync(
            OperationContext.Create(),
            new(
                fixture.Read(
                    fixture.PointPlacement,
                    [],
                    new RelationQueryIdentityBatchLookup(["item-a"])),
                pointSource.Scope,
                continuation: null,
                maximumItems: 3,
                maximumBytes: 1_000_000));
        var predicate = await traversal.ReadPageAsync(
            OperationContext.Create(),
            new(
                fixture.Read(
                    fixture.TraversalPlacement,
                    [],
                    new RelationQueryRelationshipKeyBatchLookup(
                        fixture.ParentPath,
                        ParentSelector,
                        ["parent-1"])),
                traversal.Scope,
                continuation: null,
                maximumItems: 3,
                maximumBytes: 1_000_000));

        Assert.Equal(RelationQuerySourceReadState.Complete, enumeration.Read.State);
        Assert.Equal(["item-a"], point.Read.Observations.Select(static row => row.Identity));
        Assert.Equal(["item-a", "item-b"], predicate.Read.Observations.Select(static row => row.Identity));
    }

    [Fact]
    public async Task MaterializationContinuationRoundTripsPlacementIdsContainingNewlines()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(
            executor.ExecuteAsync,
            sourcePlacementId: "placement:items\nsegment");
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1_000_000));
        var second = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, first.Continuation, maximumItems: 1, maximumBytes: 1_000_000));

        Assert.Equal("item-a", Assert.Single(first.Read.Observations).Identity);
        Assert.Equal("item-b", Assert.Single(second.Read.Observations).Identity);
        Assert.Equal(MaterializationSourcePageState.Exhausted, second.State);
    }

    [Fact]
    public async Task RelationshipFanOutRemainsBoundedAcrossPagesAndStatementDrift()
    {
        var initial = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var drifted = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-1")
        ]);
        var call = 0;
        var fixture = CreateFixture(
            (command, cancellationToken) => Interlocked.Increment(ref call) == 1
                ? initial.ExecuteAsync(command, cancellationToken)
                : drifted.ExecuteAsync(command, cancellationToken),
            maximumFanOut: 2);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.TraversalPlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.TraversalPlacement,
            [],
            new RelationQueryRelationshipKeyBatchLookup(
                fixture.ParentPath,
                ParentSelector,
                ["parent-1"]));

        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 1, maximumBytes: 1_000_000));
        var second = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, first.Continuation, maximumItems: 1, maximumBytes: 1_000_000));

        Assert.Equal(MaterializationSourcePageState.MoreAvailable, first.State);
        Assert.Equal(RelationQuerySourceReadState.Inconclusive, second.Read.State);
        Assert.Contains(second.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresMaterializationSource.SourceReadInconclusiveDiagnosticCode);
    }

    [Fact]
    public async Task ByteBoundPagingAdvancesFanOutOnlyForEmittedObservations()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync, maximumFanOut: 2);
        var source = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.TraversalPlacement,
            ContinuationAuthenticationKey);
        var read = fixture.Read(
            fixture.TraversalPlacement,
            [Field(fixture.TraversalNameInput, "name")],
            new RelationQueryRelationshipKeyBatchLookup(
                fixture.ParentPath,
                ParentSelector,
                ["parent-1"]));
        var complete = await fixture.Reader.ReadAsync(read);
        var firstByteCount = StrictDocumentJson.GetCanonicalBytes(
            complete.Observations[0],
            MaterializationJsonSerializer.CreateOptions()).LongLength;
        executor.Commands.Clear();

        var first = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, continuation: null, maximumItems: 2, maximumBytes: firstByteCount));
        var second = await source.ReadPageAsync(
            OperationContext.Create(),
            new(read, source.Scope, first.Continuation, maximumItems: 2, maximumBytes: 1_000_000));

        Assert.Equal(["item-a"], first.Read.Observations.Select(static row => row.Identity));
        Assert.Equal(["item-b"], second.Read.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQuerySourceReadState.Complete, second.Read.State);
    }

    [Fact]
    public async Task KeysetPagingMatchesTheDeterministicReferenceSource()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1"),
            new("item-c", "Gamma", null, "parent-2")
        ]);
        var fixture = CreateFixture(executor.ExecuteAsync);
        var postgres = new PostgresMaterializationSource(
            fixture.Reader,
            fixture.SourcePlacement,
            ContinuationAuthenticationKey);
        var reference = new InMemoryMaterializationSource(postgres.Descriptor);
        var read = fixture.Read(
            fixture.SourcePlacement,
            [Field(fixture.NameInput, "name")],
            new RelationQueryBoundedEnumeration(maximumRows: 10));

        var postgresRows = await ReadAllAsync(postgres, postgres.Scope, read, maximumItems: 1);
        var referenceRows = await ReadAllAsync(reference, postgres.Scope, read, maximumItems: 1);

        Assert.Equal(referenceRows.ToArray(), postgresRows.ToArray());
    }

    [Fact]
    public void ScalarKeysRoundTripScientificDecimalsAndFiniteTemporalEndpoints()
    {
        decimal[] decimals = [0.0000000000000000000000000001m, decimal.MinValue, decimal.MaxValue, 1.2300m];
        foreach (var value in decimals)
        {
            var encoded = PostgresRelationQueryScalarCatalog.FormatKey(value, PostgresRelationQueryScalarType.Numeric);
            var parsed = Assert.IsType<decimal>(
                PostgresRelationQueryScalarCatalog.ParseKey(encoded, PostgresRelationQueryScalarType.Numeric));
            Assert.Equal(value, parsed);
            Assert.Equal(encoded, PostgresRelationQueryScalarCatalog.FormatKey(parsed, PostgresRelationQueryScalarType.Numeric));
        }

        object[] endpoints =
        [
            DateOnly.MinValue,
            DateOnly.MaxValue,
            DateTime.MinValue,
            DateTime.MaxValue.AddTicks(-9),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue.AddTicks(-9)
        ];
        PostgresRelationQueryScalarType[] types =
        [
            PostgresRelationQueryScalarType.Date,
            PostgresRelationQueryScalarType.Date,
            PostgresRelationQueryScalarType.Timestamp,
            PostgresRelationQueryScalarType.Timestamp,
            PostgresRelationQueryScalarType.TimestampWithTimeZone,
            PostgresRelationQueryScalarType.TimestampWithTimeZone
        ];
        for (var index = 0; index < endpoints.Length; index++)
        {
            var encoded = PostgresRelationQueryScalarCatalog.FormatKey(endpoints[index], types[index]);
            var parsed = PostgresRelationQueryScalarCatalog.ParseKey(encoded, types[index]);
            Assert.Equal(endpoints[index], parsed);
        }
    }

    [Fact]
    public async Task PublicRegistrationRequiresExactCompiledPlanAndPlacementAffinity()
    {
        var fixture = CreateCanonicalExecutionFixture(new TableExecutor([]).ExecuteAsync);
        var other = CreateCanonicalExecutionFixture(
            new TableExecutor([]).ExecuteAsync,
            queryId: "other-postgres-source-reader");
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=cohesive_registration;Username=cohesive;Password=not-used");
        var runtimeBinding = new PostgresNpgsqlRuntimeBinding(
            fixture.Storage.Database,
            dataSource,
            "cohesive.tests/postgres/runtime-binding/v1");
        var reader = new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            fixture.Storage,
            dataSource,
            runtimeBinding,
            Policy);
        Assert.Equal(fixture.PhysicalPlan.Fingerprint, reader.PhysicalPlan);
        var runtimeEvidence = Assert.IsType<string>(reader.RuntimeEvidenceReference);
        Assert.Contains(Uri.EscapeDataString(runtimeBinding.Authority), runtimeEvidence, StringComparison.Ordinal);
        Assert.Contains(runtimeBinding.DataSourceFingerprint.Value, runtimeEvidence, StringComparison.Ordinal);
        var materialization = new PostgresMaterializationSource(
            reader,
            Assert.Single(fixture.PhysicalPlan.Placement.Bindings),
            ContinuationAuthenticationKey);
        Assert.All(
            materialization.Descriptor.CapabilityProfile.Evidence,
            evidence => Assert.Contains(runtimeEvidence, evidence.SourceReferences));

        await using var equivalentButUnattestedDataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=cohesive_registration;Username=cohesive;Password=not-used");
        var wrongDataSource = Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            fixture.Storage,
            equivalentButUnattestedDataSource,
            runtimeBinding,
            Policy));
        Assert.Equal("dataSource", wrongDataSource.ParamName);

        var wrongDatabaseBinding = new PostgresNpgsqlRuntimeBinding(
            new("cohesive-registration-other"),
            dataSource,
            "cohesive.tests/postgres/runtime-binding/v1");
        var wrongDatabase = Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            fixture.Storage,
            dataSource,
            wrongDatabaseBinding,
            Policy));
        Assert.Equal("runtimeBinding", wrongDatabase.ParamName);

        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            other.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            fixture.Storage,
            dataSource,
            runtimeBinding,
            Policy));

        var stale = new PostgresRelationQueryStorageBinding(
            fixture.Storage.SchemaVersion,
            fingerprint: null,
            fixture.Storage.DatabaseSemanticsProfile,
            fixture.Storage.Id,
            fixture.Storage.Database,
            fixture.Storage.Target,
            fixture.Storage.TargetProfile,
            fixture.Storage.Tables,
            fixture.Storage.Origin,
            fixture.Storage.ConventionSetVersion,
            fixture.Storage.ConfigurationDecisions,
            new("sha256", "tests/stale-plan/v1", "stale"),
            fixture.Storage.PlacementFingerprint);
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            stale,
            dataSource,
            runtimeBinding,
            Policy));

        var table = Assert.Single(fixture.Storage.Tables);
        var identity = Assert.IsType<PostgresRelationQueryIdentityBinding>(table.Identity);
        var wrongIdentityTable = new PostgresRelationQueryTableBinding(
            table.Source,
            table.PlacementBinding,
            table.Input,
            table.Shape,
            table.SchemaName,
            table.TableName,
            new(
                FieldPath.FromField("name"),
                identity.ColumnName,
                identity.ScalarType,
                identity.TextSemantics,
                identity.NumericDomain,
                identity.TemporalDomain),
            table.Fields,
            table.RelationshipReferences,
            table.IntervalValidities);
        var wrongIdentity = new PostgresRelationQueryStorageBinding(
            fixture.Storage.Id,
            fixture.Storage.Database,
            fixture.Storage.Target,
            fixture.Storage.TargetProfile,
            [wrongIdentityTable],
            fixture.Storage.Origin,
            fixture.Storage.ConventionSetVersion,
            fixture.Storage.ConfigurationDecisions,
            fixture.Storage.CompiledPlanFingerprint,
            fixture.Storage.PlacementFingerprint);
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            wrongIdentity,
            dataSource,
            runtimeBinding,
            Policy));

        var missingFieldsTable = new PostgresRelationQueryTableBinding(
            table.Source,
            table.PlacementBinding,
            table.Input,
            table.Shape,
            table.SchemaName,
            table.TableName,
            identity,
            fields: [],
            table.RelationshipReferences,
            table.IntervalValidities);
        var missingFields = new PostgresRelationQueryStorageBinding(
            fixture.Storage.Id,
            fixture.Storage.Database,
            fixture.Storage.Target,
            fixture.Storage.TargetProfile,
            [missingFieldsTable],
            fixture.Storage.Origin,
            fixture.Storage.ConventionSetVersion,
            fixture.Storage.ConfigurationDecisions,
            fixture.Storage.CompiledPlanFingerprint,
            fixture.Storage.PlacementFingerprint);
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            missingFields,
            dataSource,
            runtimeBinding,
            Policy));
    }

    [Fact]
    public async Task PublicRegistrationRejectsFieldScalarAndNullEncodingDrift()
    {
        var fixture = CreateCanonicalExecutionFixture(new TableExecutor([]).ExecuteAsync);
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=cohesive_registration;Username=cohesive;Password=not-used");
        var runtimeBinding = new PostgresNpgsqlRuntimeBinding(
            fixture.Storage.Database,
            dataSource,
            "cohesive.tests/postgres/runtime-binding/v1");
        var table = Assert.Single(fixture.Storage.Tables);
        var name = Assert.Single(table.Fields, static field => field.SemanticPath.Matches("name"));

        var wrongScalar = ReplaceField(
            fixture.Storage,
            table,
            name,
            new(
                name.Input,
                name.SemanticPath,
                name.ColumnName,
                PostgresRelationQueryScalarType.Int64,
                name.MissingValueEncoding,
                name.NullValueEncoding));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            wrongScalar,
            dataSource,
            runtimeBinding,
            Policy));

        var wrongMissing = ReplaceField(
            fixture.Storage,
            table,
            name,
            new(
                name.Input,
                name.SemanticPath,
                name.ColumnName,
                name.ScalarType,
                PostgresRelationQueryMissingValueEncoding.SqlNull,
                name.NullValueEncoding,
                name.TextSemantics,
                name.Ordering,
                name.NumericDomain,
                name.DecimalAggregates,
                name.TemporalDomain));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            wrongMissing,
            dataSource,
            runtimeBinding,
            Policy));

        var wrongNull = ReplaceField(
            fixture.Storage,
            table,
            name,
            new(
                name.Input,
                name.SemanticPath,
                name.ColumnName,
                name.ScalarType,
                name.MissingValueEncoding,
                PostgresRelationQueryNullValueEncoding.SqlNull,
                name.TextSemantics,
                name.Ordering,
                name.NumericDomain,
                name.DecimalAggregates,
                name.TemporalDomain));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            wrongNull,
            dataSource,
            runtimeBinding,
            Policy));

        static PostgresRelationQueryStorageBinding ReplaceField(
            PostgresRelationQueryStorageBinding storage,
            PostgresRelationQueryTableBinding table,
            PostgresRelationQueryFieldBinding prior,
            PostgresRelationQueryFieldBinding replacement)
        {
            var updatedTable = new PostgresRelationQueryTableBinding(
                table.Source,
                table.PlacementBinding,
                table.Input,
                table.Shape,
                table.SchemaName,
                table.TableName,
                table.Identity,
                [.. table.Fields.Select(field => field == prior ? replacement : field)],
                table.RelationshipReferences,
                table.IntervalValidities);
            return new(
                storage.Id,
                storage.Database,
                storage.Target,
                storage.TargetProfile,
                [updatedTable],
                storage.Origin,
                storage.ConventionSetVersion,
                storage.ConfigurationDecisions,
                storage.CompiledPlanFingerprint,
                storage.PlacementFingerprint);
        }
    }

    [Fact]
    public async Task PublicRegistrationRejectsRelationshipUniquenessDrift()
    {
        var fixture = CreateRelationshipRegistrationFixture();
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=cohesive_registration;Username=cohesive;Password=not-used");
        var runtimeBinding = new PostgresNpgsqlRuntimeBinding(
            fixture.Storage.Database,
            dataSource,
            "cohesive.tests/postgres/runtime-binding/v1");
        var table = Assert.Single(
            fixture.Storage.Tables,
            static candidate => !candidate.RelationshipReferences.IsDefaultOrEmpty);
        var reference = Assert.Single(table.RelationshipReferences);
        var wrongReference = new PostgresRelationQueryRelationshipReferenceBinding(
            reference.Input,
            reference.SemanticPath,
            reference.ColumnName,
            reference.ScalarType,
            SourceReferenceUniqueness.NotGuaranteed,
            reference.MissingValueEncoding,
            reference.NullValueEncoding,
            reference.TextSemantics,
            reference.NumericDomain,
            reference.TemporalDomain);
        var wrongTable = new PostgresRelationQueryTableBinding(
            table.Source,
            table.PlacementBinding,
            table.Input,
            table.Shape,
            table.SchemaName,
            table.TableName,
            table.Identity,
            table.Fields,
            [wrongReference],
            table.IntervalValidities);
        var wrongStorage = new PostgresRelationQueryStorageBinding(
            fixture.Storage.Id,
            fixture.Storage.Database,
            fixture.Storage.Target,
            fixture.Storage.TargetProfile,
            [.. fixture.Storage.Tables.Select(candidate => candidate == table ? wrongTable : candidate)],
            fixture.Storage.Origin,
            fixture.Storage.ConventionSetVersion,
            fixture.Storage.ConfigurationDecisions,
            fixture.Storage.CompiledPlanFingerprint,
            fixture.Storage.PlacementFingerprint);

        Assert.Throws<ArgumentException>(() => new PostgresRelationQuerySourceReader(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Source,
            wrongStorage,
            dataSource,
            runtimeBinding,
            Policy));
    }

    [Fact]
    public void TemporalSemanticsRequireExplicitStartupEvidence()
    {
        Assert.Equal(
            PostgresNpgsqlTemporalSemantics.Unsupported,
            PostgresRelationQuerySourcePolicy.Default.TemporalSemantics);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresRelationQuerySourcePolicy(
            maximumBatchKeys: 10,
            maximumRowsPerRead: 10,
            maximumPageItems: 3,
            maximumPageBytes: 1_000_000,
            temporalSemantics: (PostgresNpgsqlTemporalSemantics)int.MaxValue));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresNpgsqlExecution.RequireExactTemporalSemantics(
                PostgresNpgsqlTemporalSemantics.Unsupported));
    }

    [Fact]
    public void CorePublicApisDoNotExposeNpgsqlTypes()
    {
        var coreAssemblies = new[]
        {
            typeof(IRelationQuerySourceReader).Assembly,
            typeof(IMaterializationSource).Assembly
        };
        foreach (var assembly in coreAssemblies)
        {
            var leaked = assembly.ExportedTypes
                .SelectMany(static type => type.GetMembers())
                .SelectMany(static member => PublicSignatureTypes(member))
                .FirstOrDefault(static type => type.Namespace?.StartsWith("Npgsql", StringComparison.Ordinal) == true);
            Assert.Null(leaked);
        }
    }

    [PostgresFact]
    public async Task LocalPostgres_ExecutesRelationsReadAndMaterialization_WhenConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The PostgreSQL integration-test connection string disappeared after test discovery.");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = $"ari172_{Guid.NewGuid():N}";
        var contracts = CreateContracts(schema, "items");
        try
        {
            await using (var setup = dataSource.CreateCommand($$"""
                CREATE SCHEMA "{{schema}}";
                CREATE TABLE "{{schema}}"."items" (
                    "id" text COLLATE "C" PRIMARY KEY,
                    "name" text NOT NULL,
                    "optional" text NULL,
                    "parent_id" text COLLATE "C" NOT NULL,
                    CONSTRAINT "ck_items_id_ascii" CHECK (octet_length("id") = length("id"))
                );
                INSERT INTO "{{schema}}"."items" ("id", "name", "optional", "parent_id") VALUES
                    ('item-a', 'Alpha', NULL, 'parent-1'),
                    ('item-b', 'Beta', 'present', 'parent-1');
                CREATE TABLE "{{schema}}"."compiled_items" (
                    "load_id" text COLLATE "C" PRIMARY KEY,
                    "load_name" text NOT NULL,
                    CONSTRAINT "ck_compiled_items_id_ascii" CHECK (octet_length("load_id") = length("load_id"))
                );
                INSERT INTO "{{schema}}"."compiled_items" ("load_id", "load_name") VALUES
                    ('item-a', 'Alpha'),
                    ('item-b', 'Beta');
                """))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var reader = new PostgresRelationQuerySourceReader(
                PhysicalPlan,
                contracts.Placement,
                contracts.Source,
                contracts.Storage,
                (command, cancellationToken) => PostgresNpgsqlExecution.ExecuteAsync(
                    dataSource,
                    command,
                    cancellationToken),
                Policy);
            var fixture = contracts.ToFixture(reader);
            var relationRead = await reader.ReadAsync(fixture.Read(
                fixture.PointPlacement,
                [Field(fixture.PointNameInput, "name")],
                new RelationQueryIdentityBatchLookup(["item-a", "item-b"])));
            Assert.Equal(RelationQuerySourceReadState.Complete, relationRead.State);
            var relationshipRead = await reader.ReadAsync(fixture.Read(
                fixture.TraversalPlacement,
                [new RelationQuerySourceReadField(
                    input: null,
                    fixture.ParentPath,
                    ParentSelector,
                    RelationQuerySourceReadFieldPurpose.Correlation)],
                new RelationQueryRelationshipKeyBatchLookup(
                    fixture.ParentPath,
                    ParentSelector,
                    ["parent-1"])));
            Assert.Equal(RelationQuerySourceReadState.Complete, relationshipRead.State);
            Assert.Equal(
                ["item-a", "item-b"],
                relationshipRead.Observations.Select(static row => row.Identity));

            var materialization = new PostgresMaterializationSource(
                reader,
                fixture.SourcePlacement,
                ContinuationAuthenticationKey);
            var page = await materialization.ReadPageAsync(
                OperationContext.Create(),
                new(
                    fixture.Read(
                        fixture.SourcePlacement,
                        [Field(fixture.NameInput, "name")],
                    new RelationQueryBoundedEnumeration(maximumRows: 10)),
                    materialization.Scope,
                    continuation: null,
                    maximumItems: 3,
                    maximumBytes: 1_000_000));
            Assert.Equal(["item-a", "item-b"], page.Read.Observations.Select(static row => row.Identity));

            var compiled = CreateCanonicalExecutionFixture(
                (command, cancellationToken) => PostgresNpgsqlExecution.ExecuteAsync(
                    dataSource,
                    command,
                    cancellationToken),
                schema,
                "compiled_items");
            var publicCompiledReader = new PostgresRelationQuerySourceReader(
                compiled.Plan,
                compiled.PhysicalPlan,
                compiled.Source,
                compiled.Storage,
                dataSource,
                new PostgresNpgsqlRuntimeBinding(
                    compiled.Storage.Database,
                    dataSource,
                    "cohesive.tests/postgres/runtime-binding/v1"),
                Policy);
            var compiledResult = await new RelationQueryPhysicalExecutor([publicCompiledReader]).ExecuteAsync(new(
                compiled.Plan,
                compiled.PhysicalPlan,
                compiled.Realization,
                new("tests/postgres/local-canonical-execution"),
                capabilities: AvailableCapabilities(compiled.Plan)));
            Assert.True(compiledResult.IsSuccessful);
            Assert.Equal(2, Assert.Single(compiledResult.Interpretation!.QueryResults).Rows.Length);
            var compiledPlacement = Assert.Single(compiled.PhysicalPlan.Placement.Bindings);
            var compiledMaterialization = new PostgresMaterializationSource(
                publicCompiledReader,
                compiledPlacement,
                ContinuationAuthenticationKey);
            var compiledPage = await compiledMaterialization.ReadPageAsync(
                OperationContext.Create(),
                new(
                    CanonicalSourceRead(compiled),
                    compiledMaterialization.Scope,
                    continuation: null,
                    maximumItems: 3,
                    maximumBytes: 1_000_000));
            Assert.Equal(
                ["item-a", "item-b"],
                compiledPage.Read.Observations.Select(static row => row.Identity));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompiledPhysicalPlan_ExecutesThroughCanonicalRelationsWithDistinctSelectorsAndColumns()
    {
        var executor = new TableExecutor(
        [
            new("item-a", "Alpha", null, "parent-1"),
            new("item-b", "Beta", null, "parent-1")
        ]);
        var fixture = CreateCanonicalExecutionFixture(
            executor.ExecuteAsync,
            identitySelector: "name");
        var result = await new RelationQueryPhysicalExecutor([fixture.Reader]).ExecuteAsync(new(
            fixture.Plan,
            fixture.PhysicalPlan,
            fixture.Realization,
            new("tests/postgres/canonical-execution"),
            capabilities: AvailableCapabilities(fixture.Plan)));

        Assert.True(
            result.IsSuccessful,
            string.Join(
                Environment.NewLine,
                [$"status={result.Status}",
                    .. result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"),
                    .. result.SourceReads.Select(static read => $"read={read.State}/{read.EvidenceReference}"),
                    .. result.Interpretation?.Diagnostics.Select(static diagnostic =>
                        $"runtime={diagnostic.Code}: {diagnostic.Message}") ?? []]));
        Assert.Equal(2, Assert.Single(result.Interpretation!.QueryResults).Rows.Length);
        var command = Assert.Single(executor.Commands);
        Assert.Contains("\"load_id\"", command.Text, StringComparison.Ordinal);
        Assert.Contains("\"load_name\"", command.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\".\"id\"", command.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\".\"name\"", command.Text, StringComparison.Ordinal);

        var sourcePlacement = Assert.Single(fixture.PhysicalPlan.Placement.Bindings);
        Assert.Equal("name", sourcePlacement.Identity?.SourceSelector);
        var read = CanonicalSourceRead(fixture);
        var materialization = new PostgresMaterializationSource(
            fixture.Reader,
            sourcePlacement,
            ContinuationAuthenticationKey);
        var page = await materialization.ReadPageAsync(
            OperationContext.Create(),
            new(
                read,
                materialization.Scope,
                continuation: null,
                maximumItems: 3,
                maximumBytes: 1_000_000));

        Assert.Equal(["item-a", "item-b"], page.Read.Observations.Select(static row => row.Identity));
        Assert.Contains("\"load_id\"", executor.Commands[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\".\"id\"", executor.Commands[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompiledPhysicalStage_RejectsAReadWithMissingSemanticInputsBeforeIo()
    {
        var executor = new TableExecutor([]);
        var fixture = CreateCanonicalExecutionFixture(executor.ExecuteAsync);
        var canonical = CanonicalSourceRead(fixture);
        var request = new RelationQuerySourceReadRequest(
            canonical.PhysicalPlan,
            canonical.Stage,
            canonical.PlacementBinding,
            canonical.Source,
            canonical.Shape,
            canonical.IdentitySelector,
            canonical.Fields.RemoveAt(0),
            canonical.Constraint,
            canonical.MaximumBufferedRows);

        var result = await fixture.Reader.ReadAsync(request);

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("physical-stage-fields-mismatch", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task CompiledLookupStage_RejectsABatchWiderThanItsCompiledBoundaryBeforeIo()
    {
        var author = RelationQuery.Expression();
        var parentShape = author.Clr.Shape<BatchParent>();
        var childShape = author.Clr.Shape<BatchChild>();
        var parents = author.Source(parentShape);
        var children = author.Traverse<BatchParent, BatchChild>(parents, parent => parent.ChildId);
        var projected = author.Project(
            children,
            (BatchParent parent, BatchChild child) => new BatchRow
            {
                Id = parent.Id,
                ChildName = child.Name
            });
        var query = author.BuildQuery(
            new("postgres-compiled-batch-boundary"),
            new("PostgresCompiledBatchBoundary"),
            author.Rows(projected));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var placementBuilder = RelationQueryPlacement.For(plan);
        var sourceHandle = placementBuilder.Source(
            "tests/postgres/compiled-batch",
            PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(maximumBatchSize: 10, maximumBufferedRows: 10, maximumFanOut: 10, maximumConcurrency: 2));
        var placedParent = placementBuilder.PlaceSource(sourceHandle, parentShape)
            .Identity(parent => parent.Id)
            .FieldsBySemanticPath();
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var placedChild = placementBuilder.Place(traversalContract, sourceHandle, childShape)
            .Identity(child => child.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var parentInput = authoredPlacement.GetInput(placedParent);
        var childInput = authoredPlacement.GetInput(placedChild);
        var ordering = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            new("ck_compiled_batch_id_ascii", "tests/postgres/compiled-batch-boundary/v1"));
        var identityOptions = new PostgresRelationQueryColumnOptions(
            PostgresRelationQueryScalarType.Text,
            textSemantics: ordering,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var storage = PostgresRelationQueryBinding.For(authoredPlacement)
            .Database(new("tests-database"))
            .Table(
                parentInput,
                "batch_parents",
                table => table
                    .ColumnsExplicitly()
                    .Column(parent => parent.Id, "parent_id", identityOptions)
                    .Column(parent => parent.ChildId, "child_id")
                    .Identity(parent => parent.Id, "parent_id", identityOptions))
            .Table(
                childInput,
                "batch_children",
                table => table
                    .ColumnsExplicitly()
                    .Column(child => child.Name, "child_name")
                    .Identity(child => child.Id, "child_id", identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            authoredPlacement.Placement,
            new(
                new("tests/postgres/compiled-batch-policy/v1"),
                authoredPlacement.Placement.ConventionSetVersion,
                maximumBatchSize: 1,
                maximumBufferedRows: 10,
                maximumLocalRows: 10,
                maximumFanOut: 10,
                maximumReferenceKeysPerObservation: 10,
                maximumConcurrency: 2));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        var executor = new TableExecutor([]);
        var reader = new PostgresRelationQuerySourceReader(
            plan,
            physicalPlan,
            sourceHandle.Id,
            storage,
            executor.ExecuteAsync,
            Policy);
        var stage = Assert.Single(
            physicalPlan.Stages,
            static candidate => candidate.Kind == RelationQueryPhysicalStageKind.BatchedIdentityLookup);
        var binding = authoredPlacement.Placement.Bindings.Single(candidate => candidate.Id == stage.PlacementBinding);
        var fields = stage.RequestedFields.Select(input =>
        {
            var field = binding.Fields.Single(candidate => candidate.Input == input);
            return new RelationQuerySourceReadField(
                field.Input,
                field.SemanticPath,
                field.SourceSelector,
                RelationQuerySourceReadFieldPurpose.SemanticInput);
        }).ToImmutableArray();
        var request = new RelationQuerySourceReadRequest(
            physicalPlan.Fingerprint,
            stage.Id,
            binding.Id,
            sourceHandle.Id,
            binding.Shape,
            binding.Identity!.SourceSelector,
            fields,
            new RelationQueryIdentityBatchLookup(["child-a", "child-b"]),
            maximumBufferedRows: 10);

        var result = await reader.ReadAsync(request);

        Assert.Equal(1, stage.BatchSize);
        Assert.Equal(RelationQuerySourceReadState.Inconclusive, result.State);
        Assert.Contains("batch-boundary-exceeded", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
    }

    static RelationQuerySourceReadRequest CanonicalSourceRead(CanonicalExecutionFixture fixture)
    {
        var sourcePlacement = Assert.Single(fixture.PhysicalPlan.Placement.Bindings);
        var sourceStage = Assert.Single(
            fixture.PhysicalPlan.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.SourceRead);
        return new(
            fixture.PhysicalPlan.Fingerprint,
            sourceStage.Id,
            sourcePlacement.Id,
            fixture.Source,
            sourcePlacement.Shape,
            sourcePlacement.Identity!.SourceSelector,
            [
                .. sourcePlacement.Fields.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            new RelationQueryBoundedEnumeration(maximumRows: 10),
            maximumBufferedRows: 10);
    }

    static readonly QualifiedShapeId Shape = new(new("tests/postgres/source"), new("Item"));
    static readonly RelationQuerySourceInstanceId SourceId = new("postgres/tests");
    const string ParentSelector = "parent";
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan = new(
        "sha256",
        "tests/physical-plan/v1",
        "physical-plan");
    static readonly byte[] ContinuationAuthenticationKey = Convert.FromHexString(
        "6A68A7530D77D4EC92FC40B9DA97BEA07E19BB7269C8A2B2E8B8FD640F1689F7");
    static readonly PostgresRelationQuerySourcePolicy Policy = new(10, 10, 3, 1_000_000);

    static CanonicalExecutionFixture CreateCanonicalExecutionFixture(
        PostgresNpgsqlCommandExecutor executor,
        string schema = "public",
        string tableName = "items",
        string queryId = "postgres-source-reader",
        string? identitySelector = null)
    {
        var author = RelationQuery.Expression();
        var itemShape = author.Clr.Shape<CanonicalItem>();
        var items = author.Source(itemShape);
        var projected = author.Project(
            items.Node,
            (CanonicalItem item) => new CanonicalRow { Id = item.Id, Name = item.Name },
            items.Binding);
        var rows = author.Rows(projected.Node, projected.Binding, id: "rows");
        var query = author.BuildQuery(
            new(queryId),
            new("PostgresSourceReader"),
            rows);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var placementBuilder = RelationQueryPlacement.For(plan);
        var sourceHandle = placementBuilder.Source(
            "tests/postgres/items",
            PostgresRelationQuerySourceTargetProfile.Default,
            limits: new(maximumBatchSize: 10, maximumBufferedRows: 10, maximumFanOut: 10, maximumConcurrency: 2));
        var placedSource = placementBuilder.PlaceSource(sourceHandle, itemShape);
        _ = identitySelector is null
            ? placedSource.Identity(item => item.Id)
            : placedSource.Identity(item => item.Id, identitySelector);
        var placed = placedSource.FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        var placedInput = authoredPlacement.GetInput(placed);
        var ordering = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            new($"ck_{tableName}_id_ascii", "tests/postgres/canonical-execution/v1"));
        var identityOptions = new PostgresRelationQueryColumnOptions(
            PostgresRelationQueryScalarType.Text,
            textSemantics: ordering,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var storage = PostgresRelationQueryBinding.For(authoredPlacement)
            .Database(new("tests-database"))
            .Table(
                placedInput,
                tableName,
                table => table
                    .Schema(schema)
                    .ColumnsExplicitly()
                    .Column(item => item.Id, "load_id", identityOptions)
                    .Column(item => item.Name, "load_name")
                    .Identity(item => item.Id, "load_id", identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            authoredPlacement.Placement,
            new(
                new("tests/postgres/source-execution-policy/v1"),
                authoredPlacement.Placement.ConventionSetVersion,
                maximumBatchSize: 10,
                maximumBufferedRows: 10,
                maximumLocalRows: 10,
                maximumFanOut: 10,
                maximumReferenceKeysPerObservation: 10,
                maximumConcurrency: 2));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        var reader = new PostgresRelationQuerySourceReader(
            plan,
            physicalPlan,
            sourceHandle.Id,
            storage,
            executor,
            Policy);
        return new(plan, realization, physicalPlan, sourceHandle.Id, storage, reader);
    }

    static RelationshipRegistrationFixture CreateRelationshipRegistrationFixture()
    {
        var author = RelationQuery.Expression();
        var parentShape = author.Clr.Shape<RelationshipParent>();
        var childShape = author.Clr.Shape<RelationshipChild>();
        var parentRelationship = author.Relationship<RelationshipChild, string, RelationshipParent>(
            child => child.ParentId,
            sourceReferenceUniqueness: SourceReferenceUniqueness.GloballyUnique);
        var parents = author.Source(parentShape);
        var children = author.TraverseInverse(
            parents,
            parentRelationship,
            JoinKind.Left,
            QueryInputRequirement.Optional);
        var projected = author.Project(
            children,
            (RelationshipParent parent, RelationshipChild child) => new RelationshipRow
            {
                ParentId = parent.Id,
                ChildId = child.Id
            });
        var query = author.BuildQuery(
            new("postgres-source-reader-relationship-registration"),
            new("PostgresSourceReaderRelationshipRegistration"),
            author.Rows(projected));
        var compilation = RelationQueryStaticCompiler.Compile(new(
            query.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));

        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var placementBuilder = RelationQueryPlacement.For(plan);
        var executionDomain = new RelationQueryExecutionDomainId("tests/postgres/relationship-registration");
        var sourceLimits = new RelationQuerySourcePlacementLimits(
            maximumBatchSize: 10,
            maximumBufferedRows: 10,
            maximumFanOut: 10,
            maximumConcurrency: 2);
        var parentSource = placementBuilder.Source(
            "tests/postgres/relationship-registration/parents",
            PostgresRelationQuerySourceTargetProfile.Default,
            executionDomain,
            sourceLimits);
        var childSource = placementBuilder.Source(
            "tests/postgres/relationship-registration/children",
            PostgresRelationQuerySourceTargetProfile.Default,
            executionDomain,
            sourceLimits);
        var placedParent = placementBuilder.Place(sourceContract, parentSource, parentShape)
            .Identity(parent => parent.Id)
            .FieldsBySemanticPath();
        var placedChild = placementBuilder.Place(traversalContract, childSource, childShape)
            .Identity(child => child.Id)
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var parentInput = placement.GetInput(placedParent);
        var childInput = placement.GetInput(placedChild);
        var textSemantics = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            new(
                "ck_relationship_registration_ascii",
                "tests/postgres/relationship-registration/v1"));
        var identityOptions = new PostgresRelationQueryColumnOptions(
            PostgresRelationQueryScalarType.Text,
            textSemantics: textSemantics,
            ordering: PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique);
        var storage = PostgresRelationQueryBinding.For(
                placement,
                explicitAuthority: "tests/postgres/relationship-registration/v1")
            .Database(new("tests-database"))
            .Table(
                parentInput,
                "relationship_parents",
                table => table
                    .ColumnsExplicitly()
                    .Column(parent => parent.Id, "parent_id", identityOptions)
                    .Identity(parent => parent.Id, "parent_id", identityOptions))
            .Table(
                childInput,
                "relationship_children",
                table => table
                    .ColumnsExplicitly()
                    .Column(child => child.Id, "child_id", identityOptions)
                    .Column(child => child.ParentId, "parent_id", identityOptions)
                    .Identity(child => child.Id, "child_id", identityOptions)
                    .RelationshipReference(
                        traversalContract.Input.Id,
                        child => child.ParentId,
                        "parent_id",
                        identityOptions))
            .Build()
            .RequireValue();
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            placement.Placement,
            new(
                new("tests/postgres/relationship-registration-policy/v1"),
                placement.Placement.ConventionSetVersion,
                maximumBatchSize: 10,
                maximumBufferedRows: 10,
                maximumLocalRows: 10,
                maximumFanOut: 10,
                maximumReferenceKeysPerObservation: 10,
                maximumConcurrency: 2));
        var physicalPlan = physical.Plan
            ?? throw new InvalidOperationException(string.Join(Environment.NewLine, physical.Diagnostics));
        return new(plan, physicalPlan, childSource.Id, storage);
    }

    static ImmutableArray<RelationQueryCapabilityEvidence> AvailableCapabilities(
        CompiledRelationQueryPlan plan) =>
    [
        .. plan.RequirementGraph.Inputs
            .OfType<RelationQueryCapabilityInput>()
            .Select(static input => new RelationQueryCapabilityEvidence(
                input.Id,
                RelationQueryCapabilityEvidenceState.Available,
                "tests/postgres/canonical-execution"))
    ];

    static async Task<ImmutableArray<string>> ReadAllAsync(
        IMaterializationSource source,
        MaterializationSourceScope scope,
        RelationQuerySourceReadRequest read,
        int maximumItems)
    {
        ImmutableArray<string>.Builder identities = ImmutableArray.CreateBuilder<string>();
        MaterializationSourceContinuation? continuation = null;
        MaterializationSourcePage page;
        do
        {
            page = await source.ReadPageAsync(
                OperationContext.Create(),
                new(read, scope, continuation, maximumItems, maximumBytes: 1_000_000));
            identities.AddRange(page.Read.Observations.Select(static observation => observation.Identity));
            continuation = page.Continuation;
        }
        while (page.State == MaterializationSourcePageState.MoreAvailable);
        return identities.ToImmutable();
    }

    static MaterializationSourceContinuation RewriteContinuationIdentity(
        MaterializationSourceContinuation continuation,
        string identity)
    {
        const string prefix = "postgres-keyset/v2/";
        if (!continuation.Value.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("The test continuation is not a PostgreSQL keyset continuation.", nameof(continuation));

        var separator = continuation.Value.IndexOf('.', prefix.Length);
        if (separator < 0)
            throw new ArgumentException("The test continuation has no authentication tag.", nameof(continuation));
        var encoded = continuation.Value[prefix.Length..separator]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = (encoded.Length % 4) switch
        {
            0 => encoded,
            2 => string.Concat(encoded, "=="),
            3 => string.Concat(encoded, "="),
            _ => throw new FormatException("The test continuation has invalid base64url length.")
        };
        var payload = JsonNode.Parse(Convert.FromBase64String(encoded))?.AsObject()
            ?? throw new JsonException("The test continuation payload is null.");
        payload["identity"] = identity;
        var rewritten = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(payload, MaterializationJsonSerializer.CreateOptions()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new(
            continuation.FormatVersion,
            continuation.ReadFingerprint,
            continuation.Scope,
            string.Concat(prefix, rewritten, continuation.Value[separator..]));
    }

    static RelationQuerySourceReadField Field(RelationQueryInputId input, string name) => new(
        input,
        FieldPath.FromField(name),
        name,
        RelationQuerySourceReadFieldPurpose.SemanticInput);

    static Fixture CreateFixture(
        PostgresNpgsqlCommandExecutor executor,
        PostgresRelationQuerySourcePolicy? policy = null,
        long maximumFanOut = 10,
        string sourcePlacementId = "placement:items",
        PostgresRelationQueryScalarType relationshipScalarType = PostgresRelationQueryScalarType.Text)
    {
        var contracts = CreateContracts(
            "public",
            "items",
            maximumFanOut,
            sourcePlacementId,
            relationshipScalarType);
        var effectivePolicy = policy ?? Policy;
        var reader = new PostgresRelationQuerySourceReader(
            PhysicalPlan,
            contracts.Placement,
            contracts.Source,
            contracts.Storage,
            executor,
            effectivePolicy);
        return contracts.ToFixture(reader);
    }

    static Contracts CreateContracts(
        string schema,
        string tableName,
        long maximumFanOut = 10,
        string sourcePlacementId = "placement:items",
        PostgresRelationQueryScalarType relationshipScalarType = PostgresRelationQueryScalarType.Text)
    {
        var sourceInput = new RelationQueryInputId("input:items");
        var pointInput = new RelationQueryInputId("input:point-items");
        var traversalInput = new RelationQueryInputId("input:related-items");
        var nameInput = new RelationQueryInputId("field:name");
        var pointNameInput = new RelationQueryInputId("field:point-name");
        var pointOptionalInput = new RelationQueryInputId("field:point-optional");
        var traversalNameInput = new RelationQueryInputId("field:related-name");
        var optionalInput = new RelationQueryInputId("field:optional");
        var parentPath = FieldPath.FromField("parentId");
        var orderingDomain = new PostgresRelationQueryTextOrderingDomainEvidence(
            "ck_items_id_ascii",
            "tests/postgres/source-reader/v1");
        var identityText = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            orderingDomain);
        var equalityText = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal);
        var sourcePlacement = Placement(
            sourcePlacementId,
            sourceInput,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            [new(nameInput, FieldPath.FromField("name"), "name")],
            []);
        var pointPlacement = Placement(
            "placement:point-items",
            pointInput,
            RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
            RelationQuerySourceAcquisitionKind.BoundedLookup,
            [
                new(pointNameInput, FieldPath.FromField("name"), "name"),
                new(pointOptionalInput, FieldPath.FromField("optional"), "optional")
            ],
            []);
        var traversalPlacement = Placement(
            "placement:related-items",
            traversalInput,
            RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
            RelationQuerySourceAcquisitionKind.BoundedLookup,
            [
                new(traversalNameInput, FieldPath.FromField("name"), "name"),
                new(optionalInput, FieldPath.FromField("optional"), "optional")
            ],
            [new(traversalInput, parentPath, ParentSelector)]);
        var sourceFields = ImmutableArray.Create(
            new PostgresRelationQueryFieldBinding(
                nameInput,
                FieldPath.FromField("name"),
                "name",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited));
        var pointFields = ImmutableArray.Create(
            new PostgresRelationQueryFieldBinding(
                pointNameInput,
                FieldPath.FromField("name"),
                "name",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited),
            new PostgresRelationQueryFieldBinding(
                pointOptionalInput,
                FieldPath.FromField("optional"),
                "optional",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.SqlNull,
                PostgresRelationQueryNullValueEncoding.Prohibited));
        var traversalFields = ImmutableArray.Create(
            new PostgresRelationQueryFieldBinding(
                traversalNameInput,
                FieldPath.FromField("name"),
                "name",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited),
            new PostgresRelationQueryFieldBinding(
                optionalInput,
                FieldPath.FromField("optional"),
                "optional",
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryMissingValueEncoding.SqlNull,
                PostgresRelationQueryNullValueEncoding.Prohibited));
        var relationship = new PostgresRelationQueryRelationshipReferenceBinding(
            traversalInput,
            parentPath,
            "parent_id",
            relationshipScalarType,
            SourceReferenceUniqueness.NotGuaranteed,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            relationshipScalarType == PostgresRelationQueryScalarType.Text ? equalityText : null);
        var tables = ImmutableArray.Create(
            Table(sourcePlacement, sourceInput, sourceFields, []),
            Table(pointPlacement, pointInput, pointFields, []),
            Table(traversalPlacement, traversalInput, traversalFields, [relationship]));
        var source = new RelationQuerySourceInstance(
            SourceId,
            new("postgres/tests-domain"),
            PostgresRelationQuerySourceTargetProfile.Default,
            new(maximumBatchSize: 10, maximumBufferedRows: 10, maximumFanOut, maximumConcurrency: 2));
        var plan = new RelationQueryCompiledPlanReference(
            "tests/static-compiler/v1",
            "tests/definition/v1",
            new("sha256", "tests/definition/v1", "definition"),
            new("sha256", "tests/shapes/v1", "shapes"),
            relationshipCatalogFingerprint: null,
            new("sha256", "tests/demand/v1", "demand"),
            [
                sourceInput,
                pointInput,
                traversalInput,
                nameInput,
                pointNameInput,
                pointOptionalInput,
                traversalNameInput,
                optionalInput
            ]);
        var placement = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            plan,
            "tests/postgres-placement-conventions/v1",
            [source],
            [sourcePlacement, pointPlacement, traversalPlacement]);
        var storage = new PostgresRelationQueryStorageBinding(
            PostgresRelationQueryStorageBinding.CurrentSchemaVersion,
            fingerprint: null,
            PostgresRelationQueryStorageBinding.CanonicalDatabaseSemanticsProfile,
            new("tests/postgres/source-binding/v1"),
            new("tests-database"),
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            tables,
            PostgresRelationQueryBindingOrigin.Explicit,
            conventionSetVersion: null,
            configurationDecisions: [],
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(plan),
            placement.Fingerprint);
        return new(
            source,
            placement,
            storage,
            sourcePlacement,
            pointPlacement,
            traversalPlacement,
            nameInput,
            pointNameInput,
            pointOptionalInput,
            traversalNameInput,
            optionalInput,
            parentPath);

        PostgresRelationQueryTableBinding Table(
            RelationQuerySourcePlacementBinding placement,
            RelationQueryInputId input,
            ImmutableArray<PostgresRelationQueryFieldBinding> tableFields,
            ImmutableArray<PostgresRelationQueryRelationshipReferenceBinding> relationships) => new(
            SourceId,
            placement.Id,
            input,
            Shape,
            schema,
            tableName,
            new(
                FieldPath.FromField("id"),
                "id",
                PostgresRelationQueryScalarType.Text,
                identityText),
            tableFields,
            relationships);
    }

    static RelationQuerySourcePlacementBinding Placement(
        string id,
        RelationQueryInputId input,
        RelationQuerySourcePlacementBindingKind kind,
        RelationQuerySourceAcquisitionKind acquisition,
        ImmutableArray<RelationQuerySourceFieldBinding> fields,
        ImmutableArray<RelationQueryRelationshipKeyBinding> relationshipKeys) => new(
        new(id),
        input,
        new QueryNodeId($"node:{input.Value}"),
        new ValueBindingId($"binding:{input.Value}"),
        Shape,
        SourceId,
        kind,
        acquisition,
        RelationQuerySourcePlacementOrigin.Explicit,
        new(Shape, "id"),
        fields,
        relationshipKeys);

    static IEnumerable<Type> PublicSignatureTypes(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.MethodInfo method =>
            method.GetParameters().Select(static parameter => parameter.ParameterType).Append(method.ReturnType),
        System.Reflection.ConstructorInfo constructor =>
            constructor.GetParameters().Select(static parameter => parameter.ParameterType),
        System.Reflection.PropertyInfo property => [property.PropertyType],
        System.Reflection.FieldInfo field => [field.FieldType],
        System.Reflection.EventInfo @event when @event.EventHandlerType is { } eventType => [eventType],
        _ => []
    };

    sealed record Contracts(
        RelationQuerySourceInstance Source,
        RelationQuerySourcePlacement Placement,
        PostgresRelationQueryStorageBinding Storage,
        RelationQuerySourcePlacementBinding SourcePlacement,
        RelationQuerySourcePlacementBinding PointPlacement,
        RelationQuerySourcePlacementBinding TraversalPlacement,
        RelationQueryInputId NameInput,
        RelationQueryInputId PointNameInput,
        RelationQueryInputId PointOptionalInput,
        RelationQueryInputId TraversalNameInput,
        RelationQueryInputId OptionalInput,
        FieldPath ParentPath)
    {
        public Fixture ToFixture(PostgresRelationQuerySourceReader reader) => new(
            reader,
            SourcePlacement,
            PointPlacement,
            TraversalPlacement,
            NameInput,
            PointNameInput,
            PointOptionalInput,
            TraversalNameInput,
            OptionalInput,
            ParentPath);
    }

    sealed record Fixture(
        PostgresRelationQuerySourceReader Reader,
        RelationQuerySourcePlacementBinding SourcePlacement,
        RelationQuerySourcePlacementBinding PointPlacement,
        RelationQuerySourcePlacementBinding TraversalPlacement,
        RelationQueryInputId NameInput,
        RelationQueryInputId PointNameInput,
        RelationQueryInputId PointOptionalInput,
        RelationQueryInputId TraversalNameInput,
        RelationQueryInputId OptionalInput,
        FieldPath ParentPath)
    {
        public RelationQuerySourceReadRequest Read(
            RelationQuerySourcePlacementBinding placement,
            ImmutableArray<RelationQuerySourceReadField> fields,
            RelationQuerySourceReadConstraint constraint,
            long maximumBufferedRows = 10) => new(
            PhysicalPlan,
            new("read/source"),
            placement.Id,
            SourceId,
            Shape,
            "id",
            fields,
            constraint,
            maximumBufferedRows);
    }

    sealed class CanonicalItem
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class CanonicalRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class RelationshipParent
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }
    }

    sealed class RelationshipChild
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("parentId")]
        public required string ParentId { get; init; }
    }

    sealed class RelationshipRow
    {
        [JsonPropertyName("parentId")]
        public required string ParentId { get; init; }

        [JsonPropertyName("childId")]
        public string? ChildId { get; init; }
    }

    sealed class BatchParent
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("childId")]
        public required string ChildId { get; init; }
    }

    sealed class BatchChild
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class BatchRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("childName")]
        public required string ChildName { get; init; }
    }

    sealed record CanonicalExecutionFixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        CompiledRelationQueryPhysicalPlan PhysicalPlan,
        RelationQuerySourceInstanceId Source,
        PostgresRelationQueryStorageBinding Storage,
        PostgresRelationQuerySourceReader Reader);

    sealed record RelationshipRegistrationFixture(
        CompiledRelationQueryPlan Plan,
        CompiledRelationQueryPhysicalPlan PhysicalPlan,
        RelationQuerySourceInstanceId Source,
        PostgresRelationQueryStorageBinding Storage);

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
            {
                Skip = "Set COHESIVE_POSTGRES_TEST_CONNECTION_STRING or run eng/test-postgres-integration.sh.";
            }
        }
    }

    sealed record TableRow(string Id, string Name, string? Optional, string ParentId)
    {
        public object? Get(string column) => column switch
        {
            "id" => Id,
            "load_id" => Id,
            "name" => Name,
            "load_name" => Name,
            "optional" => Optional,
            "parent_id" => ParentId,
            _ => throw new InvalidOperationException($"Unknown test column '{column}'.")
        };
    }

    sealed class TableExecutor
    {
        static readonly Regex Projection = new(
            "\\\"source\\\"\\.\\\"(?<column>[^\\\"]+)\\\" AS \\\"(?<alias>_[^\\\"]+)\\\"",
            RegexOptions.CultureInvariant);
        static readonly Regex Limit = new(" LIMIT (?<limit>[0-9]+)$", RegexOptions.CultureInvariant);
        readonly ImmutableArray<TableRow> rows;

        public TableExecutor(ImmutableArray<TableRow> rows) =>
            this.rows = [.. rows.OrderBy(static row => row.Id, StringComparer.Ordinal)];

        public List<PostgresNpgsqlCommand> Commands { get; } = [];

        public ValueTask<PostgresNpgsqlCommandResult> ExecuteAsync(
            PostgresNpgsqlCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            IEnumerable<TableRow> selected = rows;
            var arrayParameter = command.Parameters.FirstOrDefault(static parameter => parameter.IsArray);
            if (arrayParameter.Value is string[] keys)
            {
                selected = command.Text.Contains("CROSS JOIN LATERAL", StringComparison.Ordinal)
                    ? keys.SelectMany(key => rows.Where(row => string.Equals(row.ParentId, key, StringComparison.Ordinal)))
                    : selected.Where(row => keys.Contains(row.Id, StringComparer.Ordinal));
            }
            var after = command.Parameters.FirstOrDefault(static parameter => !parameter.IsArray).Value as string;
            if (after is not null)
                selected = selected.Where(row => StringComparer.Ordinal.Compare(row.Id, after) > 0);
            var limitMatch = Limit.Match(command.Text);
            var maximum = int.Parse(limitMatch.Groups["limit"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var columns = Projection.Matches(command.Text)
                .Select(match => match.Groups["column"].Value)
                .ToArray();
            var selectedRows = selected.ToArray();
            var result = ImmutableArray.CreateBuilder<ImmutableArray<object?>>();
            foreach (var row in selectedRows.Take(maximum))
            {
                var values = ImmutableArray.CreateBuilder<object?>(columns.Length);
                foreach (var column in columns)
                    values.Add(row.Get(column));
                result.Add(values.MoveToImmutable());
            }
            return ValueTask.FromResult(new PostgresNpgsqlCommandResult(result.ToImmutable()));
        }
    }
}
