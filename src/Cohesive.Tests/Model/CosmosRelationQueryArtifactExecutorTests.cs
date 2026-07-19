using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryArtifactExecutorTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("Id");
    static readonly FieldPath CustomerNamePath = FieldPath.Parse("Customer.Name");
    static readonly FieldPath CustomerTypePath = FieldPath.Parse("Customer.Type");
    static readonly FieldPath CountPath = FieldPath.FromField("Count");
    static readonly FieldPath StatusPath = FieldPath.FromField("Status");
    static readonly FieldPath ValuePath = FieldPath.FromField("Value");

    [Fact]
    public void ExecutionDiagnosticCodes_UseDedicatedStableRange()
    {
        string[] codes =
        [
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.InvocationInvalid,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.BatchPreflightFailed,
            CosmosRelationQueryArtifactExecutionDiagnosticCodes.RequestSizePreflightFailed
        ];

        Assert.Equal(
            ["REL2270", "REL2271", "REL2272", "REL2273", "REL2274", "REL2275", "REL2276"],
            codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExecutionResult_CanonicalDiagnostics_RetainsImmutableStorage()
    {
        var fixture = ArtifactFixture.Row();
        var request = fixture.Request(maximumRows: 5);
        ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> diagnostics =
        [
            Diagnostic("REL2270", "first", request, rowOrdinal: null),
            Diagnostic("REL2271", "second", request, rowOrdinal: 1)
        ];

        CosmosRelationQueryArtifactExecutionResult result = new(
            request,
            RelationQueryExecutionStatus.Failed,
            [],
            diagnostics,
            providerEvidenceReference: null);

        Assert.True(diagnostics == result.Diagnostics);
    }

    [Fact]
    public void ExecutionResult_UnorderedDiagnostics_RestoresCanonicalOrdering()
    {
        var fixture = ArtifactFixture.Row();
        var request = fixture.Request(maximumRows: 5);
        var first = Diagnostic("REL2270", "first", request, rowOrdinal: null);
        var second = Diagnostic("REL2271", "second", request, rowOrdinal: null);
        var third = Diagnostic("REL2270", "third", request, rowOrdinal: 1);

        CosmosRelationQueryArtifactExecutionResult result = new(
            request,
            RelationQueryExecutionStatus.Failed,
            [],
            [third, second, first],
            providerEvidenceReference: null);

        Assert.Collection(
            result.Diagnostics,
            diagnostic => Assert.Same(first, diagnostic),
            diagnostic => Assert.Same(second, diagnostic),
            diagnostic => Assert.Same(third, diagnostic));
    }

    [Fact]
    public async Task ExecuteAsync_CompilerArtifact_BindsSdkQueryAndDecodesCanonicalRow()
    {
        var compilerFixture = CosmosRelationQueryCompilerTests.Fixture.Row();
        var compilation = compilerFixture.Compile(compilerFixture.StorageBindingWithAffinity());
        Assert.True(compilation.IsSuccessful);
        var artifact = Assert.Single(compilation.Artifacts);
        var idAlias = Assert.Single(artifact.ResultFields, static field => field.Field.Path == IdPath).Alias;
        var statusAlias = Assert.Single(artifact.ResultFields, static field => field.Field.Path == StatusPath).Alias;
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((idAlias, "load-1"), (statusAlias, "active")))
        ]);
        Microsoft.Azure.Cosmos.QueryDefinition? observedQuery = null;
        QueryRequestOptions? observedOptions = null;
        CosmosJsonQueryFeedReader feedReader = new(
            artifact.StorageBinding.AccountEndpoint,
            artifact.StorageBinding.DatabaseName,
            artifact.StorageBinding.ContainerName,
            (query, requestOptions) =>
        {
            observedQuery = query;
            observedOptions = requestOptions;
            return iterator;
        });
        CosmosRelationQueryArtifactExecutor executor = new(feedReader);
        CosmosRelationQueryArtifactExecutionRequest request = new(
            compilerFixture.PlanReference,
            compilerFixture.Realization.Fingerprint,
            compilerFixture.Placement.Fingerprint,
            artifact.StorageBinding.Fingerprint,
            artifact,
            maximumRows: 25,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("active")
            });

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var row = Assert.Single(result.Rows);
        Assert.Equal("load-1", FieldString(row.Value, IdPath));
        Assert.Equal("active", FieldString(row.Value, StatusPath));
        Assert.Equal(artifact.Statement.Text, observedQuery?.QueryText);
        var parameter = Assert.Single(observedQuery!.GetQueryParameters());
        Assert.Equal("@p0", parameter.Name);
        Assert.Equal("active", parameter.Value);
        Assert.Equal(25, observedOptions?.MaxItemCount);
        Assert.Equal(25, observedOptions?.MaxBufferedItemCount);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CompilerDistinctArtifact_AcceptsAndIgnoresRetainedAuxiliaryColumns()
    {
        var compilerFixture = CosmosRelationQueryCompilerTests.Fixture.DistinctSelectedId();
        var compilation = compilerFixture.Compile(compilerFixture.StorageBindingWithAffinity());
        Assert.True(compilation.IsSuccessful);
        var artifact = Assert.Single(compilation.Artifacts);
        var idAlias = Assert.Single(artifact.ResultFields).Alias;
        var auxiliaryAlias = Assert.Single(artifact.AuxiliaryResultAliases);
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((idAlias, "load-1"), (auxiliaryAlias, "active")))
        ]);
        CosmosRelationQueryArtifactExecutor executor = new(
            new CosmosJsonQueryFeedReader(
                artifact.StorageBinding.AccountEndpoint,
                artifact.StorageBinding.DatabaseName,
                artifact.StorageBinding.ContainerName,
                (_, _) => iterator));
        CosmosRelationQueryArtifactExecutionRequest request = new(
            compilerFixture.PlanReference,
            compilerFixture.Realization.Fingerprint,
            compilerFixture.Placement.Fingerprint,
            artifact.StorageBinding.Fingerprint,
            artifact,
            maximumRows: 25,
            parameters: ImmutableDictionary<QueryParameterId, ObservationValue>.Empty);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal("load-1", FieldString(Assert.Single(result.Rows).Value, IdPath));
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CompilerDistinctArtifact_RequiresRetainedAuxiliaryColumns()
    {
        var compilerFixture = CosmosRelationQueryCompilerTests.Fixture.DistinctSelectedId();
        var compilation = compilerFixture.Compile(compilerFixture.StorageBindingWithAffinity());
        Assert.True(compilation.IsSuccessful);
        var artifact = Assert.Single(compilation.Artifacts);
        Assert.Single(artifact.AuxiliaryResultAliases);
        var idAlias = Assert.Single(artifact.ResultFields).Alias;
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((idAlias, "load-1")))
        ]);
        CosmosRelationQueryArtifactExecutor executor = new(
            new CosmosJsonQueryFeedReader(
                artifact.StorageBinding.AccountEndpoint,
                artifact.StorageBinding.DatabaseName,
                artifact.StorageBinding.ContainerName,
                (_, _) => iterator));
        CosmosRelationQueryArtifactExecutionRequest request = new(
            compilerFixture.PlanReference,
            compilerFixture.Realization.Fingerprint,
            compilerFixture.Placement.Fingerprint,
            artifact.StorageBinding.Fingerprint,
            artifact,
            maximumRows: 25,
            parameters: ImmutableDictionary<QueryParameterId, ObservationValue>.Empty);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid
            && diagnostic.Message.Contains("auxiliary alias", StringComparison.Ordinal));
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CompilerArtifact_MatchesReferenceInterpreterForSameEvidence()
    {
        var compilerFixture = CosmosRelationQueryCompilerTests.Fixture.Row(offset: 1);
        var compilation = compilerFixture.Compile(compilerFixture.StorageBindingWithAffinity());
        Assert.True(compilation.IsSuccessful);
        var artifact = Assert.Single(compilation.Artifacts);
        var evidence = CreateThreeActiveLoadEvidence(compilerFixture.Plan);
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(compilerFixture.Plan, evidence));
        Assert.True(reference.IsSuccessful);
        var referenceRows = Assert.Single(reference.QueryResults).Rows;
        var idAlias = Assert.Single(artifact.ResultFields, static field =>
            field.Field.Path == CosmosRelationQueryCompilerTests.Fixture.IdPath).Alias;
        var statusAlias = Assert.Single(artifact.ResultFields, static field =>
            field.Field.Path == CosmosRelationQueryCompilerTests.Fixture.StatusPath).Alias;
        TrackingFeedIterator iterator = new(
        [
            Page(
                JsonObject((idAlias, "load-2"), (statusAlias, "active")),
                JsonObject((idAlias, "load-3"), (statusAlias, "active")))
        ]);
        CosmosRelationQueryArtifactExecutor executor = new(
            new CosmosJsonQueryFeedReader(
                artifact.StorageBinding.AccountEndpoint,
                artifact.StorageBinding.DatabaseName,
                artifact.StorageBinding.ContainerName,
                (_, _) => iterator));
        CosmosRelationQueryArtifactExecutionRequest request = new(
            compilerFixture.PlanReference,
            compilerFixture.Realization.Fingerprint,
            compilerFixture.Placement.Fingerprint,
            artifact.StorageBinding.Fingerprint,
            artifact,
            maximumRows: 25,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [Assert.Single(artifact.Parameters).Parameter] = ObservationValue.FromString("active")
            });

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(
            referenceRows.Select(static row => row.Value.GetRawText()),
            result.Rows.Select(static row => row.Value.GetRawText()));
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public void ExecutionOptions_RejectsUnbufferableMaximumRows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CosmosRelationQueryArtifactExecutionOptions(maximumBufferedRows: (long)Array.MaxLength + 1));
    }

    [Fact]
    public async Task ExecuteAsync_MaximumInputRows_DoesNotCapExpandedOutputBoundary()
    {
        var fixture = ArtifactFixture.Row();
        QueryRequestOptions? observedOptions = null;
        TrackingFeedIterator iterator = new([]);
        var executor = Executor(fixture, (_, options) =>
        {
            observedOptions = options;
            return iterator;
        });

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 150));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(150, observedOptions?.MaxItemCount);
        Assert.Equal(150, observedOptions?.MaxBufferedItemCount);
    }

    [Fact]
    public async Task ExecuteAsync_RowBranch_PreservesProviderOrderNestedMissingAndNull()
    {
        var fixture = ArtifactFixture.Row();
        var idAlias = fixture.Alias(IdPath);
        var nameAlias = fixture.Alias(CustomerNamePath);
        var typeAlias = fixture.Alias(CustomerTypePath);
        TrackingFeedIterator iterator = new(
        [
            Page(
                JsonObject((idAlias, "load-1"), (typeAlias, null)),
                JsonObject((idAlias, "load-2"), (nameAlias, "Acme"), (typeAlias, "shipper")))
        ]);
        Microsoft.Azure.Cosmos.QueryDefinition? observedQuery = null;
        QueryRequestOptions? observedOptions = null;
        var executor = Executor(fixture, (query, requestOptions) =>
        {
            observedQuery = query;
            observedOptions = requestOptions;
            return iterator;
        });

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 10));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(RelationQueryNativeResultKind.QueryRows, result.Branch.Kind);
        Assert.Equal(["load-1", "load-2"], result.Rows.Select(row => FieldString(row.Value, IdPath)));
        Assert.False(result.Rows[0].Value.TryGetField(CustomerNamePath, out _));
        Assert.True(result.Rows[0].Value.TryGetField(CustomerTypePath, out var explicitNull));
        Assert.Equal(ObservationValueKind.Null, explicitNull.Kind);
        Assert.Equal("Acme", FieldString(result.Rows[1].Value, CustomerNamePath));
        Assert.Equal("shipper", FieldString(result.Rows[1].Value, CustomerTypePath));
        Assert.False(result.Diagnostics.IsDefault);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(fixture.Artifact.Statement.Text, observedQuery?.QueryText);
        Assert.Equal(10, observedOptions?.MaxItemCount);
        Assert.True(iterator.Disposed);
        Assert.NotNull(result.ProviderEvidenceReference);
    }

    [Fact]
    public async Task ExecuteAsync_RowBranch_ReconstructsSharedNestedObjectOnce()
    {
        var fixture = ArtifactFixture.Row();
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject(
                (fixture.Alias(CustomerTypePath), "shipper"),
                (fixture.Alias(IdPath), "load-1"),
                (fixture.Alias(CustomerNamePath), "Acme")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(FieldPath.FromField("Customer"), out var customer));
        Assert.Equal(ObservationValueKind.Object, customer.Kind);
        Assert.Equal(["Name", "Type"], customer.Fields!.Keys);
        Assert.Equal("Acme", FieldString(row.Value, CustomerNamePath));
        Assert.Equal("shipper", FieldString(row.Value, CustomerTypePath));
    }

    [Fact]
    public async Task ExecuteAsync_AggregationBranch_DecodesExactCount()
    {
        var fixture = ArtifactFixture.Aggregation();
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(CountPath), 7), (fixture.Alias(StatusPath), "active")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(RelationQueryNativeResultKind.QueryAggregation, result.Branch.Kind);
        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(CountPath, out var count));
        Assert.True(count.TryGetInt64(out var countValue));
        Assert.Equal(7, countValue);
        Assert.Equal("active", FieldString(row.Value, StatusPath));
        Assert.True(iterator.Disposed);
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("1e0", 1)]
    [InlineData("-2147483648.0", int.MinValue)]
    [InlineData("2.147483647e9", int.MaxValue)]
    public async Task ExecuteAsync_Int32Result_DecodesExactIntegralJsonForms(string jsonNumber, int expected)
    {
        var fixture = ArtifactFixture.Int32Row();
        var alias = fixture.Alias(ValuePath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json($$"""{"{{alias}}":{{jsonNumber}}}"""))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(ValuePath, out var value));
        Assert.True(value.TryGetInt32(out var actual));
        Assert.Equal(expected, actual);
        Assert.True(iterator.Disposed);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("7.0", 7L)]
    [InlineData("7e0", 7L)]
    [InlineData("9007199254740991", 9_007_199_254_740_991L)]
    public async Task ExecuteAsync_CountResult_DecodesExactIntegralJsonForms(string jsonNumber, long expected)
    {
        var fixture = ArtifactFixture.Aggregation();
        var countAlias = fixture.Alias(CountPath);
        var statusAlias = fixture.Alias(StatusPath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json($$"""{"{{countAlias}}":{{jsonNumber}},"{{statusAlias}}":"active"}"""))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(CountPath, out var count));
        Assert.True(count.TryGetInt64(out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0.99999999999999999999999999999")]
    [InlineData("1e-100")]
    [InlineData("7.00000000000000000000000000001")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task ExecuteAsync_Int32Result_RejectsInexactOrOutOfRangeJsonNumbers(string jsonNumber)
    {
        var fixture = ArtifactFixture.Int32Row();
        var alias = fixture.Alias(ValuePath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json($$"""{"{{alias}}":{{jsonNumber}}}"""))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid);
        Assert.True(iterator.Disposed);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9007199254740992")]
    [InlineData("1e-100")]
    public async Task ExecuteAsync_CountResult_RejectsNumbersOutsideExactCountDomain(string jsonNumber)
    {
        var fixture = ArtifactFixture.Aggregation();
        var countAlias = fixture.Alias(CountPath);
        var statusAlias = fixture.Alias(StatusPath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json($$"""{"{{countAlias}}":{{jsonNumber}},"{{statusAlias}}":"active"}"""))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_DateResult_PreservesContractValidJsonString()
    {
        var fixture = ArtifactFixture.TemporalRow(ScalarTypeKind.Date);
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(ValuePath), "2026-07-18")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(ValuePath, out var value));
        Assert.Equal(ObservationValueKind.String, value.Kind);
        Assert.Equal("2026-07-18", value.String);
    }

    [Theory]
    [InlineData("2026-07-18T12:34:56.0000000")]
    [InlineData("2026-07-18T12:34:56.0000000+05:00")]
    public async Task ExecuteAsync_DateTimeResult_PreservesContractValidJsonString(string physicalValue)
    {
        var fixture = ArtifactFixture.TemporalRow(ScalarTypeKind.DateTime);
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(ValuePath), physicalValue)))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(ValuePath, out var value));
        Assert.Equal(ObservationValueKind.String, value.Kind);
        Assert.Equal(physicalValue, value.String);
    }

    [Theory]
    [InlineData("2026-07-18T14:34:56.0000000+02:00")]
    [InlineData("2026-07-18T12:34:56.0000000+00:00")]
    [InlineData("2026-07-18T12:34:56.0000000Z")]
    public async Task ExecuteAsync_InstantResult_PreservesContractValidJsonString(string physicalValue)
    {
        var fixture = ArtifactFixture.TemporalRow(ScalarTypeKind.Instant);
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(ValuePath), physicalValue)))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        var row = Assert.Single(result.Rows);
        Assert.True(row.Value.TryGetField(ValuePath, out var value));
        Assert.Equal(ObservationValueKind.String, value.Kind);
        Assert.Equal(physicalValue, value.String);
    }

    [Fact]
    public async Task ExecuteAsync_RelationBranch_PreservesConcreteResultIdentity()
    {
        var fixture = ArtifactFixture.Relation();
        var identityAlias = Assert.IsType<CosmosRelationQueryResultIdentityBinding>(fixture.Artifact.ResultIdentity).Alias;
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(IdPath), "load-1"), (identityAlias, "canonical-load-1")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        var row = Assert.Single(result.Rows);
        Assert.Equal(RelationQueryNativeResultKind.RelationRows, result.Branch.Kind);
        Assert.Equal(ObservationValueKind.String, row.Identity?.Kind);
        Assert.Equal("canonical-load-1", row.Identity?.String);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_RelationBranch_RejectsDuplicateCanonicalResultIdentity()
    {
        var fixture = ArtifactFixture.Relation();
        var identityAlias = Assert.IsType<CosmosRelationQueryResultIdentityBinding>(fixture.Artifact.ResultIdentity).Alias;
        TrackingFeedIterator iterator = new(
        [
            Page(
                JsonObject((fixture.Alias(IdPath), "load-1"), (identityAlias, "canonical-load")),
                JsonObject((fixture.Alias(IdPath), "load-2"), (identityAlias, "canonical-load")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid, diagnostic.Code);
        Assert.Equal(1, diagnostic.RowOrdinal);
        Assert.Contains("duplicate canonical output identity", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(iterator.Disposed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("1e100")]
    [InlineData("1e10000")]
    public async Task ExecuteAsync_RelationBranch_RejectsIdentityOutsideRetainedStringContract(
        string physicalIdentity)
    {
        var fixture = ArtifactFixture.Relation();
        var identityAlias = Assert.IsType<CosmosRelationQueryResultIdentityBinding>(fixture.Artifact.ResultIdentity).Alias;
        var idAlias = fixture.Alias(IdPath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json(
                "{\"" + idAlias + "\":\"load-1\",\"" + identityAlias + "\":" + physicalIdentity + "}"))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid, diagnostic.Code);
        Assert.Contains("does not match", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_AffinityMismatch_FailsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Row();
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });
        var request = fixture.Request(
            maximumRows: 10,
            placement: new("sha256", "tests/placement-v1", new string('f', 64)));

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid);
        Assert.Equal(0, iteratorCreations);
    }

    [Theory]
    [InlineData("https://different.tests.invalid", "operations")]
    [InlineData("https://tests.invalid", "analytics")]
    public async Task ExecuteAsync_PhysicalAffinityMismatchWithSameContainer_FailsBeforeIteratorCreation(
        string accountEndpoint,
        string databaseName)
    {
        var fixture = ArtifactFixture.Row();
        var iteratorCreations = 0;
        var executor = Executor(
            fixture,
            (_, _) =>
            {
                iteratorCreations++;
                return new TrackingFeedIterator([]);
            },
            new Uri(accountEndpoint),
            databaseName);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("executor", StringComparison.Ordinal));
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_ResultEncodingContractMismatch_FailsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Row().WithResultEncoding(
            IdPath,
            CosmosRelationQueryResultValueEncoding.JsonBoolean);
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("encoding metadata", StringComparison.Ordinal));
        Assert.Equal(0, iteratorCreations);
    }

    [Theory]
    [InlineData(CosmosRelationQueryResultValueEncoding.JsonBoolean)]
    [InlineData(CosmosRelationQueryResultValueEncoding.JsonInt32)]
    public async Task ExecuteAsync_ResultIdentityEncodingContractMismatch_FailsBeforeIteratorCreation(
        CosmosRelationQueryResultValueEncoding encoding)
    {
        var fixture = ArtifactFixture.Relation().WithIdentityEncoding(encoding);
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("result identity", StringComparison.Ordinal));
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_SelectedInputProvenanceMismatch_FailsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Row().WithUnattributedSelectedInput();
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("attributed compiled inputs", StringComparison.Ordinal));
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownInvocationParameter_FailsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Row();
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });
        var request = fixture.Request(
            maximumRows: 10,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("unknown")] = ObservationValue.FromString("value")
            });

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.InvocationInvalid);
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_RequestSizeBoundary_FailsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Parameterized(new ScalarTypeRef(ScalarTypeKind.String));
        var iteratorCreations = 0;
        var executor = Executor(
            fixture,
            (_, _) =>
            {
                iteratorCreations++;
                return new TrackingFeedIterator([]);
            },
            options: new CosmosRelationQueryArtifactExecutionOptions(
                requestSizeLimits: new CosmosQueryRequestSizeLimits(
                    maximumSqlQueryUtf8Bytes: 256,
                    maximumRequestUtf8Bytes: 5_000)));

        var result = await executor.ExecuteAsync(fixture.Request(
            maximumRows: 5,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [ArtifactFixture.Parameter] = ObservationValue.FromString(new string('x', 5_000))
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.RequestSizePreflightFailed
            && diagnostic.EvidenceReference is not null
            && diagnostic.EvidenceReference.Contains("query-request-boundary-exceeded", StringComparison.Ordinal));
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_DeclaredBoundary_ReturnsIncompleteAttributablePrefix()
    {
        var fixture = ArtifactFixture.Row(pagingLimit: 1);
        var idAlias = fixture.Alias(IdPath);
        var typeAlias = fixture.Alias(CustomerTypePath);
        TrackingFeedIterator iterator = new(
        [
            Page(
                JsonObject((idAlias, "load-1"), (typeAlias, "shipper")),
                JsonObject((idAlias, "load-2"), (typeAlias, "broker")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 20));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        Assert.Equal("load-1", FieldString(Assert.Single(result.Rows).Value, IdPath));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(result.ProviderEvidenceReference, diagnostic.EvidenceReference);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateExpectedAlias_FailsAsInvalidPhysicalResult()
    {
        var fixture = ArtifactFixture.Aggregation();
        var countAlias = fixture.Alias(CountPath);
        var statusAlias = fixture.Alias(StatusPath);
        TrackingFeedIterator iterator = new(
        [
            Page(Json($$"""{"{{countAlias}}":1,"{{countAlias}}":2,"{{statusAlias}}":"active"}"""))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid, diagnostic.Code);
        Assert.Contains("more than once", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(0, diagnostic.RowOrdinal);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedAlias_FailsAsInvalidPhysicalResult()
    {
        var fixture = ArtifactFixture.Row();
        TrackingFeedIterator iterator = new(
        [
            Page(JsonObject((fixture.Alias(IdPath), "load-1"), ("unexpected", "value")))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid, diagnostic.Code);
        Assert.Contains("not declared", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(0, diagnostic.RowOrdinal);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderFailure_IsStructuredAndDisposesIterator()
    {
        var fixture = ArtifactFixture.Row();
        TrackingFeedIterator iterator = new(
        [
            _ => Task.FromException<FeedResponse<JsonElement>>(new InvalidOperationException("provider failed"))
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        var result = await executor.ExecuteAsync(fixture.Request(maximumRows: 5));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure);
        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringRead_PropagatesAndDisposesIterator()
    {
        var fixture = ArtifactFixture.Row();
        using CancellationTokenSource cancellation = new();
        TrackingFeedIterator iterator = new(
        [
            token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<FeedResponse<JsonElement>>(token);
            }
        ]);
        var executor = Executor(fixture, (_, _) => iterator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(fixture.Request(maximumRows: 5), cancellation.Token));

        Assert.True(iterator.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_BatchPreflightFailure_PreventsEveryIteratorCreation()
    {
        var rows = ArtifactFixture.Row();
        var aggregation = ArtifactFixture.Aggregation();
        var iteratorCreations = 0;
        var executor = Executor(rows, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });
        var staleAggregation = aggregation.Request(
            maximumRows: 5,
            realization: new("sha256", "tests/realization-v1", new string('e', 64)));

        var results = await executor.ExecuteAsync(
            [rows.Request(maximumRows: 5), staleAggregation]);

        Assert.Equal(2, results.Length);
        Assert.All(results, static result => Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status));
        Assert.Contains(results[0].Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.BatchPreflightFailed);
        Assert.Contains(results[1].Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid);
        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateBatchBranch_RejectsBeforeIteratorCreation()
    {
        var fixture = ArtifactFixture.Row();
        var iteratorCreations = 0;
        var executor = Executor(fixture, (_, _) =>
        {
            iteratorCreations++;
            return new TrackingFeedIterator([]);
        });
        var request = fixture.Request(maximumRows: 5);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await executor.ExecuteAsync([request, request]));

        Assert.Equal(0, iteratorCreations);
    }

    [Fact]
    public async Task ExecuteAsync_RowAndAggregationBatch_PreservesRequestAndBranchOrder()
    {
        var rows = ArtifactFixture.Row();
        var aggregation = ArtifactFixture.Aggregation();
        Queue<TrackingFeedIterator> iterators = new(
        [
            new([Page(JsonObject((rows.Alias(IdPath), "load-1"), (rows.Alias(CustomerTypePath), "shipper")))]),
            new([Page(JsonObject((aggregation.Alias(CountPath), 1), (aggregation.Alias(StatusPath), "active")))])
        ]);
        var executor = Executor(rows, (_, _) => iterators.Dequeue());

        var results = await executor.ExecuteAsync(
            [rows.Request(maximumRows: 5), aggregation.Request(maximumRows: 5)]);

        Assert.Equal(
            [RelationQueryNativeResultKind.QueryRows, RelationQueryNativeResultKind.QueryAggregation],
            results.Select(static result => result.Branch.Kind));
        Assert.All(results, static result => Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status));
        Assert.Empty(iterators);
    }

    [Fact]
    public void Bind_CanonicalValueCodec_PreservesContractValidScalarAndArrayParameters()
    {
        AssertBoundParameter(
            new ScalarTypeRef(ScalarTypeKind.Int32),
            ObservationValue.FromInt64(42),
            42L);
        AssertBoundParameter(
            new ScalarTypeRef(ScalarTypeKind.Guid),
            ObservationValue.FromString("{9F13D289-6AFA-41D5-89D5-60E3D0C76663}"),
            "{9F13D289-6AFA-41D5-89D5-60E3D0C76663}");
        AssertBoundParameter(
            new ScalarTypeRef(ScalarTypeKind.Date),
            ObservationValue.FromString("2026-7-18"),
            "2026-7-18");
        AssertBoundParameter(
            new ScalarTypeRef(ScalarTypeKind.DateTime),
            ObservationValue.FromString("2026-07-18T12:34:56+05:00"),
            "2026-07-18T12:34:56+05:00");
        AssertBoundParameter(
            new ScalarTypeRef(ScalarTypeKind.Instant),
            ObservationValue.FromString("2026-07-18T14:34:56+02:00"),
            "2026-07-18T14:34:56+02:00");

        var arrayFixture = ArtifactFixture.Parameterized(
            new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.Guid)));
        var statement = arrayFixture.Artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [ArtifactFixture.Parameter] = ObservationValue.FromArray(
            [
                ObservationValue.FromString("9F13D289-6AFA-41D5-89D5-60E3D0C76663"),
                ObservationValue.FromString("{E9B927A9-69CD-430D-B47A-FDB72E20DABD}")
            ])
        });
        var values = Assert.IsType<ImmutableArray<object?>>(Assert.Single(statement.Parameters).Value);
        Assert.Equal(
            ["9F13D289-6AFA-41D5-89D5-60E3D0C76663", "{E9B927A9-69CD-430D-B47A-FDB72E20DABD}"],
            values.Cast<string>());
    }

    [Fact]
    public void Bind_CanonicalValueCodec_RejectsRepresentationChangingParameters()
    {
        AssertRejectedParameter(
            new ScalarTypeRef(ScalarTypeKind.Int32),
            ObservationValue.FromDouble(42d));
        AssertRejectedParameter(
            new ScalarTypeRef(ScalarTypeKind.Date),
            ObservationValue.FromDateOnly(new DateOnly(2026, 7, 18)));
        AssertRejectedParameter(
            new ScalarTypeRef(ScalarTypeKind.Instant),
            ObservationValue.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 18, 12, 34, 56, TimeSpan.Zero)));
    }

    [Fact]
    public void ArtifactFingerprint_ChangesWithFullBranchAndInputProvenance()
    {
        var fixture = ArtifactFixture.Row();
        var artifact = fixture.Artifact;
        RelationQueryNativeResultBranch changedBranch = new(
            artifact.Branch.Id,
            artifact.Branch.Kind,
            artifact.Branch.Node,
            new("changed-result-binding"),
            artifact.Branch.Shape,
            artifact.Branch.Outputs,
            artifact.Branch.Fields,
            artifact.Branch.Relation,
            artifact.Branch.QueryResult);
        var changedBranchFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            changedBranch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            artifact.ResultIdentity,
            artifact.AuxiliaryResultAliases,
            artifact.Parameters,
            artifact.Paging,
            artifact.Provenance);
        RelationQueryNativeCompilationProvenance changedProvenance = new(
            artifact.Provenance.Plan,
            artifact.Provenance.Branch,
            artifact.Provenance.Target,
            artifact.Provenance.TargetProfile,
            artifact.Provenance.Realization,
            artifact.Provenance.Placement,
            artifact.Provenance.CompilerProfile,
            artifact.Provenance.ConventionSetVersion,
            artifact.Provenance.CoveredNodes,
            artifact.Provenance.CoveredAssignments,
            [artifact.Provenance.Plan.Inputs[0]],
            artifact.Provenance.RealizationDecisions);
        var changedProvenanceFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            artifact.ResultIdentity,
            artifact.AuxiliaryResultAliases,
            artifact.Parameters,
            artifact.Paging,
            changedProvenance);
        var changedAuxiliaryFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            artifact.ResultIdentity,
            ["__auxiliary"],
            artifact.Parameters,
            artifact.Paging,
            artifact.Provenance);
        var originalPlan = artifact.Provenance.Plan;
        RelationQueryCompiledPlanReference changedPlan = new(
            originalPlan.CompilerProfile,
            originalPlan.DefinitionSchemaVersion,
            originalPlan.DefinitionFingerprint,
            originalPlan.ShapeSnapshotsFingerprint,
            originalPlan.RelationshipCatalogFingerprint,
            new(
                originalPlan.DemandFingerprint.Algorithm,
                originalPlan.DemandFingerprint.Canonicalization,
                new string('f', 64)),
            originalPlan.Inputs);
        RelationQueryNativeCompilationProvenance changedPlanProvenance = new(
            changedPlan,
            artifact.Provenance.Branch,
            artifact.Provenance.Target,
            artifact.Provenance.TargetProfile,
            artifact.Provenance.Realization,
            artifact.Provenance.Placement,
            artifact.Provenance.CompilerProfile,
            artifact.Provenance.ConventionSetVersion,
            artifact.Provenance.CoveredNodes,
            artifact.Provenance.CoveredAssignments,
            artifact.Provenance.InputFields,
            artifact.Provenance.RealizationDecisions);
        var changedPlanFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            artifact.ResultIdentity,
            artifact.AuxiliaryResultAliases,
            artifact.Parameters,
            artifact.Paging,
            changedPlanProvenance);

        Assert.NotEqual(artifact.Fingerprint, changedBranchFingerprint);
        Assert.NotEqual(artifact.Fingerprint, changedProvenanceFingerprint);
        Assert.NotEqual(artifact.Fingerprint, changedAuxiliaryFingerprint);
        Assert.NotEqual(artifact.Fingerprint, changedPlanFingerprint);
        Assert.EndsWith("/v3", artifact.Fingerprint.Canonicalization, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactFingerprint_ChangesWithParameterDefaultDefinition()
    {
        var fixture = ArtifactFixture.Parameterized(new ScalarTypeRef(ScalarTypeKind.String));
        var artifact = fixture.Artifact;
        QueryParameterDefinition changedDefinition = new(
            ArtifactFixture.Parameter,
            new ScalarTypeRef(ScalarTypeKind.String),
            FieldPresence.Optional,
            ObservationValue.FromString("fallback"));
        ImmutableArray<CosmosRelationQueryParameterBinding> changedParameters =
        [
            new("@p0", changedDefinition, changedDefinition.EffectiveValueContract)
        ];

        var changedFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            artifact.ResultIdentity,
            artifact.AuxiliaryResultAliases,
            changedParameters,
            artifact.Paging,
            artifact.Provenance);

        Assert.NotEqual(artifact.Fingerprint, changedFingerprint);
    }

    [Fact]
    public void ArtifactFingerprint_ChangesWithResultIdentityContractAndEncoding()
    {
        var artifact = ArtifactFixture.Relation().Artifact;
        var identity = Assert.IsType<CosmosRelationQueryResultIdentityBinding>(artifact.ResultIdentity);
        CosmosRelationQueryResultIdentityBinding changedContract = new(
            identity.Alias,
            new ExprValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)),
            identity.Encoding);
        CosmosRelationQueryResultIdentityBinding changedEncoding = new(
            identity.Alias,
            identity.ValueContract,
            CosmosRelationQueryResultValueEncoding.JsonBoolean);

        var contractFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            changedContract,
            artifact.AuxiliaryResultAliases,
            artifact.Parameters,
            artifact.Paging,
            artifact.Provenance);
        var encodingFingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
            artifact.Branch,
            artifact.Statement,
            artifact.StorageBinding,
            artifact.SelectedFields,
            artifact.ResultFields,
            changedEncoding,
            artifact.AuxiliaryResultAliases,
            artifact.Parameters,
            artifact.Paging,
            artifact.Provenance);

        Assert.NotEqual(artifact.Fingerprint, contractFingerprint);
        Assert.NotEqual(artifact.Fingerprint, encodingFingerprint);
    }

    static CosmosRelationQueryArtifactExecutor Executor(
        ArtifactFixture fixture,
        Func<Microsoft.Azure.Cosmos.QueryDefinition, QueryRequestOptions, FeedIterator<JsonElement>> factory,
        Uri? accountEndpoint = null,
        string? databaseName = null,
        string? containerName = null,
        CosmosRelationQueryArtifactExecutionOptions? options = null) =>
        new(new CosmosJsonQueryFeedReader(
            accountEndpoint ?? fixture.Artifact.StorageBinding.AccountEndpoint,
            databaseName ?? fixture.Artifact.StorageBinding.DatabaseName,
            containerName ?? fixture.Artifact.StorageBinding.ContainerName,
            factory), options);

    static CosmosRelationQueryArtifactExecutionDiagnostic Diagnostic(
        string code,
        string message,
        CosmosRelationQueryArtifactExecutionRequest request,
        long? rowOrdinal) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            request.Artifact.Branch.Id,
            rowOrdinal);

    static Func<CancellationToken, Task<FeedResponse<JsonElement>>> Page(params JsonElement[] rows) =>
        _ => Task.FromResult<FeedResponse<JsonElement>>(new TrackingFeedResponse(rows, requestCharge: 1.25));

    static JsonElement JsonObject(params (string Name, object? Value)[] properties) =>
        Json(JsonSerializer.Serialize(properties.ToDictionary(static item => item.Name, static item => item.Value)));

    static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static string FieldString(ObservationValue value, FieldPath path)
    {
        Assert.True(value.TryGetField(path, out var field));
        return field.GetRequiredString();
    }

    static void AssertBoundParameter(TypeRef type, ObservationValue value, object expected)
    {
        var fixture = ArtifactFixture.Parameterized(type);
        var statement = fixture.Artifact.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [ArtifactFixture.Parameter] = value
        });

        Assert.Equal(expected, Assert.Single(statement.Parameters).Value);
    }

    static void AssertRejectedParameter(TypeRef type, ObservationValue value)
    {
        var fixture = ArtifactFixture.Parameterized(type);

        var exception = Assert.Throws<ArgumentException>(() => fixture.Artifact.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [ArtifactFixture.Parameter] = value
            }));

        Assert.Contains("exact Cosmos representation", exception.Message, StringComparison.Ordinal);
    }

    static RelationQueryRuntimeEvidence CreateThreeActiveLoadEvidence(CompiledRelationQueryPlan plan)
    {
        var source = Assert.Single(plan.InputContract.Sources);
        ImmutableArray<RelationQueryObservationOccurrence> occurrences =
        [
            new(new("load/1"), source.Binding, source.Shape, "load-1"),
            new(new("load/2"), source.Binding, source.Shape, "load-2"),
            new(new("load/3"), source.Binding, source.Shape, "load-3")
        ];
        ImmutableArray<RelationQueryFieldEvidence>.Builder fields =
            ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>(source.Fields.Length * occurrences.Length);
        for (var index = 0; index < occurrences.Length; index++)
        {
            foreach (var field in source.Fields)
            {
                var value = field.Input.Field.Path == CosmosRelationQueryCompilerTests.Fixture.IdPath
                    ? ObservationValue.FromString($"load-{index + 1}")
                    : field.Input.Field.Path == CosmosRelationQueryCompilerTests.Fixture.StatusPath
                        ? ObservationValue.FromString("active")
                        : throw new InvalidOperationException(
                            $"Unexpected differential-test field '{field.Input.Field.Path}'.");
                fields.Add(new(
                    field.Input.Id,
                    occurrences[index].Id,
                    RelationQueryFieldEvidenceState.Value,
                    value));
            }
        }

        return new(
            new("tests/cosmos-artifact-differential"),
            plan,
            sources: [new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, occurrences)],
            fields: fields.MoveToImmutable(),
            parameters:
            [
                .. plan.InputContract.Parameters.Select(static parameter => new RelationQueryParameterEvidence(
                    parameter.Input.Id,
                    RelationQueryParameterEvidenceState.Provided,
                    ObservationValue.FromString("active")))
            ],
            capabilities:
            [
                .. plan.RequirementGraph.Inputs
                    .OfType<RelationQueryCapabilityInput>()
                    .Select(static input => new RelationQueryCapabilityEvidence(
                        input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ]);
    }

    sealed class ArtifactFixture
    {
        public static readonly QueryParameterId Parameter = new("value");

        ArtifactFixture(
            RelationQueryCompiledPlanReference plan,
            RelationQueryRealizationFingerprint realization,
            RelationQuerySourcePlacementFingerprint placement,
            CosmosRelationQueryCompiledArtifact artifact)
        {
            Plan = plan;
            Realization = realization;
            Placement = placement;
            Artifact = artifact;
        }

        public RelationQueryCompiledPlanReference Plan { get; }

        public RelationQueryRealizationFingerprint Realization { get; }

        public RelationQuerySourcePlacementFingerprint Placement { get; }

        public CosmosRelationQueryCompiledArtifact Artifact { get; }

        public string Alias(FieldPath path) => Assert.Single(
            Artifact.ResultFields,
            field => field.Field.Path == path).Alias;

        public CosmosRelationQueryArtifactExecutionRequest Request(
            long maximumRows,
            IReadOnlyDictionary<QueryParameterId, ObservationValue>? parameters = null,
            RelationQueryRealizationFingerprint? realization = null,
            RelationQuerySourcePlacementFingerprint? placement = null) =>
            new(
                Plan,
                realization ?? Realization,
                placement ?? Placement,
                Artifact.StorageBinding.Fingerprint,
                Artifact,
                maximumRows,
                parameters ?? ImmutableDictionary<QueryParameterId, ObservationValue>.Empty);

        public ArtifactFixture WithResultEncoding(
            FieldPath path,
            CosmosRelationQueryResultValueEncoding encoding)
        {
            ImmutableArray<CosmosRelationQueryResultFieldBinding> resultFields =
            [
                .. Artifact.ResultFields.Select(field => field.Field.Path == path
                    ? new CosmosRelationQueryResultFieldBinding(
                        field.Alias,
                        field.Field,
                        field.ValueContract,
                        encoding,
                        field.Assignment)
                    : field)
            ];
            return WithArtifact(resultFields: resultFields);
        }

        public ArtifactFixture WithIdentityEncoding(CosmosRelationQueryResultValueEncoding encoding)
        {
            var current = Assert.IsType<CosmosRelationQueryResultIdentityBinding>(Artifact.ResultIdentity);
            CosmosRelationQueryResultIdentityBinding identity = new(
                current.Alias,
                current.ValueContract,
                encoding);
            var fingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
                Artifact.Branch,
                Artifact.Statement,
                Artifact.StorageBinding,
                Artifact.SelectedFields,
                Artifact.ResultFields,
                identity,
                Artifact.AuxiliaryResultAliases,
                Artifact.Parameters,
                Artifact.Paging,
                Artifact.Provenance);
            CosmosRelationQueryCompiledArtifact artifact = new(
                Artifact.Branch,
                Artifact.Statement,
                Artifact.StorageBinding,
                Artifact.SelectedFields,
                Artifact.ResultFields,
                identity,
                Artifact.AuxiliaryResultAliases,
                Artifact.Parameters,
                Artifact.Paging,
                Artifact.Provenance,
                fingerprint);
            return new(Plan, Realization, Placement, artifact);
        }

        public ArtifactFixture WithUnattributedSelectedInput()
        {
            ImmutableArray<CosmosRelationQuerySelectedField> selectedFields =
            [
                new(
                    Plan.Inputs[0],
                    Artifact.Branch.Fields[0],
                    FieldPath.FromField("SourceValue"))
            ];
            return WithArtifact(selectedFields: selectedFields);
        }

        public static ArtifactFixture Row(int? pagingLimit = null) => Create(
            "rows",
            RelationQueryNativeResultKind.QueryRows,
            Shape("LoadSearchRow"),
            [
                new(IdPath, Required(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString),
                new(CustomerNamePath, Optional(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString),
                new(CustomerTypePath, Nullable(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString)
            ],
            pagingLimit: pagingLimit);

        public static ArtifactFixture Aggregation() => Create(
            "aggregations",
            RelationQueryNativeResultKind.QueryAggregation,
            Shape("LoadAggregation"),
            [
                new(CountPath, Required(ScalarTypeKind.Int64), CosmosRelationQueryResultValueEncoding.ExactCountInteger),
                new(StatusPath, Required(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString)
            ]);

        public static ArtifactFixture Int32Row() => Create(
            "int32-row",
            RelationQueryNativeResultKind.QueryRows,
            Shape("Int32Row"),
            [new(ValuePath, Required(ScalarTypeKind.Int32), CosmosRelationQueryResultValueEncoding.JsonInt32)]);

        public static ArtifactFixture TemporalRow(ScalarTypeKind kind)
        {
            Assert.True(kind is ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant);
            return Create(
                $"{kind}-row",
                RelationQueryNativeResultKind.QueryRows,
                Shape($"{kind}Row"),
                [
                    new(
                        ValuePath,
                        Required(kind),
                        CosmosRelationQueryResultValueEncoding.JsonString)
                ]);
        }

        public static ArtifactFixture Parameterized(TypeRef parameterType) => Create(
            "parameterized",
            RelationQueryNativeResultKind.QueryRows,
            Shape("ParameterizedRow"),
            [new(ValuePath, Required(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString)],
            parameterType: parameterType);

        public static ArtifactFixture Relation() => Create(
            "relation",
            RelationQueryNativeResultKind.RelationRows,
            Shape("LoadRelationRow"),
            [new(IdPath, Required(ScalarTypeKind.String), CosmosRelationQueryResultValueEncoding.JsonString)],
            includeIdentity: true);

        static ArtifactFixture Create(
            string branchName,
            RelationQueryNativeResultKind kind,
            QualifiedShapeId shape,
            ImmutableArray<ResultFieldSpec> fields,
            bool includeIdentity = false,
            int? pagingLimit = null,
            TypeRef? parameterType = null)
        {
            RelationQueryInputId input = new($"input/{branchName}");
            var plan = new RelationQueryCompiledPlanReference(
                "tests/static-compiler/v1",
                "tests/definition/v1",
                new("sha256", "tests/definition-v1", Hash('a')),
                new("sha256", "tests/shapes-v1", Hash('b')),
                relationshipCatalogFingerprint: null,
                new("sha256", "tests/demand-v1", Hash('c')),
                [input]);
            var planFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(plan);
            RelationQueryRealizationFingerprint realization = new("sha256", "tests/realization-v1", Hash('d'));
            RelationQuerySourcePlacementFingerprint placement = new("sha256", "tests/placement-v1", Hash('e'));
            QueryNodeId node = new($"node/{branchName}");
            QueryResultId? queryResult = kind == RelationQueryNativeResultKind.RelationRows
                ? null
                : new($"result/{branchName}");
            RelationId? relation = kind == RelationQueryNativeResultKind.RelationRows
                ? new($"relation/{branchName}")
                : null;
            RelationQueryOutputReference output = kind == RelationQueryNativeResultKind.RelationRows
                ? new(
                    new($"output/{branchName}"),
                    RelationQueryOutputReferenceKind.Relation,
                    node,
                    shape,
                    relation: relation)
                : new(
                    new($"output/{branchName}"),
                    RelationQueryOutputReferenceKind.QueryResult,
                    node,
                    shape,
                    queryResult: queryResult);
            RelationQueryNativeResultBranch branch = new(
                new($"branch/{branchName}"),
                kind,
                node,
                new($"binding/{branchName}"),
                shape,
                [output],
                [.. fields.Select(spec => new RelationQueryFieldReference(shape, spec.Path))],
                relation,
                queryResult);
            CosmosRelationQueryStorageBinding storageBinding = new(
                new($"binding/{branchName}/v1"),
                new($"source/{branchName}"),
                new($"placement/{branchName}"),
                CosmosRelationQueryTargetProfile.Target,
                CosmosRelationQueryTargetProfile.ProfileId,
                new Uri("https://tests.invalid"),
                "operations",
                "loads",
                "c",
                IdPath,
                [new(input, FieldPath.FromField("SourceValue"))],
                maximumInputRows: 100,
                compiledPlanFingerprint: planFingerprint,
                placementFingerprint: placement);
            var byPath = fields.ToDictionary(static spec => spec.Path);
            ImmutableArray<CosmosRelationQueryResultFieldBinding> resultFields =
            [
                .. branch.Fields.Select((field, index) =>
                {
                    var spec = byPath[field.Path];
                    return new CosmosRelationQueryResultFieldBinding(
                        $"f{index}",
                        field,
                        spec.Contract,
                        spec.Encoding);
                })
            ];
            QueryParameterDefinition? parameterDefinition = parameterType is null
                ? null
                : new(Parameter, parameterType);
            ImmutableArray<CosmosSqlParameterSlot> parameterSlots = parameterDefinition is null
                ? []
                : [new("@p0", CosmosSqlParameterBindingKind.Runtime, Parameter.Value, constantValue: null)];
            ImmutableArray<CosmosRelationQueryParameterBinding> parameterBindings = parameterDefinition is null
                ? []
                : [new("@p0", parameterDefinition, parameterDefinition.EffectiveValueContract)];
            CosmosSqlCommandTemplate statement = new(
                parameterDefinition is null
                    ? $"SELECT {string.Join(", ", resultFields.Select(field => $"c[\"SourceValue\"] AS {field.Alias}"))} FROM c"
                    : "SELECT @p0 AS f0 FROM c",
                parameterSlots);
            CosmosRelationQueryPagingContract? paging = pagingLimit is { } limit
                ? new(0, limit, IdPath)
                : null;
            RelationQueryNativeCompilationProvenance provenance = new(
                plan,
                branch.Id,
                CosmosRelationQueryTargetProfile.Target,
                CosmosRelationQueryTargetProfile.ProfileId,
                realization,
                placement,
                CosmosRelationQueryCompilerOptions.CurrentCompilerProfile,
                CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
                [node],
                [],
                [],
                [
                    new(
                        new($"requirement/{branchName}"),
                        RelationQueryRealizationDecisionKind.Native,
                        [new($"evidence/{branchName}")])
                ]);
            CosmosRelationQueryResultIdentityBinding? identity = includeIdentity
                ? new(
                    "__identity",
                    Required(ScalarTypeKind.String),
                    CosmosRelationQueryResultValueEncoding.JsonString)
                : null;
            var fingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
                branch,
                statement,
                storageBinding,
                [],
                resultFields,
                identity,
                [],
                parameterBindings,
                paging,
                provenance);
            CosmosRelationQueryCompiledArtifact artifact = new(
                branch,
                statement,
                storageBinding,
                [],
                resultFields,
                identity,
                [],
                parameterBindings,
                paging,
                provenance,
                fingerprint);
            return new(plan, realization, placement, artifact);
        }

        ArtifactFixture WithArtifact(
            ImmutableArray<CosmosRelationQuerySelectedField>? selectedFields = null,
            ImmutableArray<CosmosRelationQueryResultFieldBinding>? resultFields = null)
        {
            var effectiveSelectedFields = selectedFields ?? Artifact.SelectedFields;
            var effectiveResultFields = resultFields ?? Artifact.ResultFields;
            var fingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
                Artifact.Branch,
                Artifact.Statement,
                Artifact.StorageBinding,
                effectiveSelectedFields,
                effectiveResultFields,
                Artifact.ResultIdentity,
                Artifact.AuxiliaryResultAliases,
                Artifact.Parameters,
                Artifact.Paging,
                Artifact.Provenance);
            CosmosRelationQueryCompiledArtifact artifact = new(
                Artifact.Branch,
                Artifact.Statement,
                Artifact.StorageBinding,
                effectiveSelectedFields,
                effectiveResultFields,
                Artifact.ResultIdentity,
                Artifact.AuxiliaryResultAliases,
                Artifact.Parameters,
                Artifact.Paging,
                Artifact.Provenance,
                fingerprint);
            return new(Plan, Realization, Placement, artifact);
        }

        static string Hash(char value) => new(value, 64);

        static QualifiedShapeId Shape(string shape) => new(
            new GraphId("artifact-executor-tests/v1"),
            new ShapeId(shape));

        static ExprValueContract Required(ScalarTypeKind kind) => new(new ScalarTypeRef(kind));

        static ExprValueContract Optional(ScalarTypeKind kind) => new(
            new ScalarTypeRef(kind),
            presence: FieldPresence.Optional);

        static ExprValueContract Nullable(ScalarTypeKind kind) => new(
            new ScalarTypeRef(kind),
            nullability: FieldNullability.Nullable);

        sealed record ResultFieldSpec(
            FieldPath Path,
            ExprValueContract Contract,
            CosmosRelationQueryResultValueEncoding Encoding);
    }

    sealed class TrackingFeedIterator(
        IEnumerable<Func<CancellationToken, Task<FeedResponse<JsonElement>>>> reads)
        : FeedIterator<JsonElement>
    {
        readonly Queue<Func<CancellationToken, Task<FeedResponse<JsonElement>>>> reads = new(reads);

        public bool Disposed { get; private set; }

        public override bool HasMoreResults => reads.Count != 0;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(CancellationToken cancellationToken = default) =>
            reads.Dequeue()(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    sealed class TrackingFeedResponse(
        IReadOnlyList<JsonElement> rows,
        double requestCharge)
        : FeedResponse<JsonElement>
    {
        public override string ActivityId => "tests/activity";

        public override string ContinuationToken => string.Empty;

        public override int Count => rows.Count;

        public override CosmosDiagnostics Diagnostics => null!;

        public override string ETag => string.Empty;

        public override Headers Headers => null!;

        public override string IndexMetrics => string.Empty;

        public override double RequestCharge => requestCharge;

        public override IEnumerable<JsonElement> Resource => rows;

        public override HttpStatusCode StatusCode => HttpStatusCode.OK;

        public override IEnumerator<JsonElement> GetEnumerator() => rows.GetEnumerator();
    }
}
