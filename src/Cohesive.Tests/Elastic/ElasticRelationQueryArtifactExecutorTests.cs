using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Observability;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticRelationQueryArtifactExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RowHits_DecodesCanonicalRowsAndSearchAfterContinuation()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.KeysetRow();
        var artifact = Compile(fixture);
        var (runtime, calls) = Runtime(
            SearchResponse(
                hits:
                """
                [{"_index":"loads-generation-a","_id":"load-1","_source":{"id":"load-1","status":"ready"},"sort":["load-1"]}]
                """));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready"),
                [new("cursor")] = ObservationValue.FromString("load-0")
            }));

        if (!result.IsSuccessful)
        {
            throw new Xunit.Sdk.XunitException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        var row = Assert.Single(result.Rows);
        Assert.Equal("load-1", Field(row, artifact, ElasticRelationQueryResultSourceKind.SourceField, "id").String);
        Assert.Equal("ready", Field(row, artifact, ElasticRelationQueryResultSourceKind.SourceField, "status").String);
        var continuation = Assert.IsType<ElasticRelationQueryArtifactContinuation>(result.Continuation);
        Assert.Equal(artifact.Fingerprint, continuation.ArtifactFingerprint);
        Assert.Equal(ElasticRelationQueryPagingKind.SearchAfter, continuation.Kind);
        var continuationValue = Assert.Single(continuation.Values);
        Assert.Equal("id.keyword", continuationValue.PhysicalField);
        Assert.Equal("load-1", continuationValue.Value.String);
        Assert.True(continuation.TryCreateParameterOverrides(artifact, out var continuationParameters));
        Assert.Equal("load-1", Assert.Single(continuationParameters).Value.String);
        Assert.Single(calls);
    }

    [Fact]
    public async Task ExecuteAsync_Success_EmitsSafeNativeExecutionActivityAndDurationMetric()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(
            SearchResponse(
                hits:
                """
                [{"_index":"loads-generation-a","_id":"load-1","_source":{"id":"load-1","status":"ready"},"sort":["load-1"]}]
                """));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);
        var request = Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            });
        ConcurrentQueue<Activity> stopped = new();
        ConcurrentQueue<(double Duration, KeyValuePair<string, object?>[] Tags)> durations = new();
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = static source => source.Name == ElasticRelationQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(activityListener);
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == ElasticRelationQueryTelemetry.InstrumentationName
                && instrument.Name == RelationQueryTelemetry.OperationDurationInstrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, duration, tags, _) =>
            durations.Enqueue((duration, tags.ToArray())));
        meterListener.Start();
        using Activity root = new("tests.elastic.native.success");
        root.Start();

        var result = await executor.ExecuteAsync(request);

        var activity = Assert.Single(stopped, item =>
            item.OperationName == RelationQueryTelemetry.NativeExecutionActivityName
            && item.ParentSpanId == root.SpanId);
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal(RelationQueryTelemetry.SucceededStatus, activity.GetTagItem(RelationQueryTelemetry.StatusTagName));
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.Plan).Value,
            activity.GetTagItem(RelationQueryTelemetry.PlanFingerprintTagName));
        Assert.Equal(request.Realization.Value, activity.GetTagItem(RelationQueryTelemetry.RealizationFingerprintTagName));
        Assert.Equal(request.Placement.Value, activity.GetTagItem(RelationQueryTelemetry.PlacementFingerprintTagName));
        Assert.Equal(
            request.StorageBindingFingerprint.Value,
            activity.GetTagItem(RelationQueryTelemetry.BindingFingerprintTagName));
        Assert.Equal(artifact.Fingerprint.Value, activity.GetTagItem(RelationQueryTelemetry.ArtifactFingerprintTagName));
        Assert.Equal(1, activity.GetTagItem(RelationQueryTelemetry.RowCountTagName));
        Assert.Equal(0, activity.GetTagItem(RelationQueryTelemetry.DiagnosticCountTagName));
        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Value is string text
            && (text.Contains("ready", StringComparison.Ordinal)
                || text.Contains("load-1", StringComparison.Ordinal)
                || text.Contains(artifact.StorageBinding.IndexName, StringComparison.Ordinal)));

        var measurement = Assert.Single(durations, static item =>
            item.Tags.Any(static tag =>
                tag.Key == RelationQueryTelemetry.OperationTagName
                && Equals(tag.Value, RelationQueryTelemetry.NativeExecutionActivityName)));
        Assert.True(measurement.Duration >= 0d);
        Assert.Contains(measurement.Tags, static tag =>
            tag.Key == RelationQueryTelemetry.StatusTagName
            && Equals(tag.Value, RelationQueryTelemetry.SucceededStatus));
    }

    [Fact]
    public async Task ExecuteAsync_PreAffinityFailure_EmitsFailedActivityWithoutUnverifiedFingerprints()
    {
        const string UnverifiedFingerprint = "private-unverified-placement-fingerprint";
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, calls) = Runtime(SearchResponse());
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);
        var request = new ElasticRelationQueryArtifactExecutionRequest(
            plan: fixture.PlanReference,
            realization: fixture.Realization.Fingerprint,
            placement: new("caller-defined", "caller-defined", UnverifiedFingerprint),
            storageBindingFingerprint: fixture.StorageBinding.Fingerprint,
            runtimeFingerprint: runtime.Fingerprint,
            artifact: artifact,
            maximumRows: 1,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            });
        ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == ElasticRelationQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using Activity root = new("tests.elastic.native.affinity-failure");
        root.Start();

        var result = await executor.ExecuteAsync(request);

        var activity = Assert.Single(stopped, item =>
            item.OperationName == RelationQueryTelemetry.NativeExecutionActivityName
            && item.ParentSpanId == root.SpanId);
        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Equal(RelationQueryTelemetry.FailedStatus, activity.GetTagItem(RelationQueryTelemetry.StatusTagName));
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.PlanFingerprintTagName));
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.RealizationFingerprintTagName));
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.PlacementFingerprintTagName));
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.BindingFingerprintTagName));
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.ArtifactFingerprintTagName));
        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Value is string text && text.Contains(UnverifiedFingerprint, StringComparison.Ordinal));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_GlobalCount_RequiresAndDecodesExactTotalHits()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.GlobalCount();
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(SearchResponse(totalValue: 42));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var row = Assert.Single(result.Rows);
        var countField = Assert.Single(artifact.ResultFields);
        Assert.True(row.Value.TryGetField(countField.Field.Path, out var count));
        Assert.Equal(42, count.Int64);
        Assert.Null(result.Continuation);
    }

    [Fact]
    public async Task ExecuteAsync_CompositeCount_DecodesBucketsAndExactAfterKey()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.GroupedCount();
        var artifact = Compile(fixture);
        var response = SearchResponse(
            aggregations:
            """
            {"composite#groups":{"after_key":{"g0":"ready"},"buckets":[{"key":{"g0":"ready"},"doc_count":7}]}}
            """);
        var (runtime, _) = Runtime(response);
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("cursor")] = ObservationValue.FromString("pending")
            }));

        if (!result.IsSuccessful)
        {
            throw new Xunit.Sdk.XunitException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        var row = Assert.Single(result.Rows);
        var keyField = artifact.ResultFields.Single(static field =>
            field.SourceKind == ElasticRelationQueryResultSourceKind.CompositeKey);
        var countField = artifact.ResultFields.Single(static field =>
            field.SourceKind == ElasticRelationQueryResultSourceKind.CompositeDocumentCount);
        Assert.True(row.Value.TryGetField(keyField.Field.Path, out var key));
        Assert.Equal("ready", key.String);
        Assert.True(row.Value.TryGetField(countField.Field.Path, out var count));
        Assert.Equal(7, count.Int64);
        var continuation = Assert.IsType<ElasticRelationQueryArtifactContinuation>(result.Continuation);
        Assert.Equal(ElasticRelationQueryPagingKind.CompositeAfter, continuation.Kind);
        var after = Assert.Single(continuation.Values);
        Assert.Equal("status.keyword", after.PhysicalField);
        Assert.Equal("ready", after.Value.String);
        Assert.True(continuation.TryCreateParameterOverrides(artifact, out var continuationParameters));
        Assert.Equal("ready", Assert.Single(continuationParameters).Value.String);
    }

    [Fact]
    public async Task ExecuteAsync_InexactTotalHits_FailsClosed()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.GlobalCount();
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(SearchResponse(totalValue: 10, totalRelation: "gte"));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_MissingRequiredSourceField_FailsWithoutPartialRows()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(
            SearchResponse(
                hits:
                """
                [{"_index":"loads-generation-a","_id":"load-1","_source":{"id":"load-1"},"sort":["load-1"]}]
                """));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid, diagnostic.Code);
        Assert.Equal(0, diagnostic.RowOrdinal);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeAttestationMismatch_FailsBeforeProviderIo()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, calls) = Runtime(SearchResponse());
        var (foreignRuntime, _) = Runtime(SearchResponse(), cluster: new("foreign-cluster"));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            runtimeFingerprint: foreignRuntime.Fingerprint,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("runtime attestation", StringComparison.Ordinal));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_StaleArtifactFingerprint_FailsBeforeProviderIo()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var tamperedPolicy = new ElasticQueryLoweringFingerprint(
            algorithm: artifact.LoweringPolicyFingerprint.Algorithm,
            canonicalization: artifact.LoweringPolicyFingerprint.Canonicalization,
            value: artifact.LoweringPolicyFingerprint.Value + "0");
        var tamperedArtifact = new ElasticRelationQueryCompiledArtifact(
            branch: artifact.Branch,
            requestTemplate: artifact.RequestTemplate,
            storageBinding: artifact.StorageBinding,
            selectedFields: artifact.SelectedFields,
            resultFields: artifact.ResultFields,
            parameters: artifact.Parameters,
            paging: artifact.Paging,
            loweringPolicyFingerprint: tamperedPolicy,
            loweringDecisions: artifact.LoweringDecisions,
            provenance: artifact.Provenance,
            fingerprint: artifact.Fingerprint);
        var (runtime, calls) = Runtime(SearchResponse());
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            tamperedArtifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid
            && diagnostic.Message.Contains("artifact fingerprint", StringComparison.Ordinal));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_DeclaredPageAboveExecutionLimit_FailsBeforeProviderIo()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 2);
        var artifact = Compile(fixture);
        var (runtime, calls) = Runtime(SearchResponse());
        var executor = new ElasticRelationQueryArtifactExecutor(
            runtime,
            new ElasticRelationQueryArtifactExecutionOptions(maximumBufferedRows: 1));

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            maximumRows: 2,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderRowsAboveCompiledLimit_FailsWithoutPartialRows()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(
            SearchResponse(
                hits:
                """
                [
                  {"_index":"loads-generation-a","_id":"load-1","_source":{"id":"load-1","status":"ready"},"sort":["load-1"]},
                  {"_index":"loads-generation-a","_id":"load-2","_source":{"id":"load-2","status":"ready"},"sort":["load-2"]}
                ]
                """));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded);
    }

    [Fact]
    public async Task ExecuteAsync_TimedOutResponse_IsStructuredProviderFailure()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(SearchResponse(timedOut: true));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure);
    }

    [Fact]
    public async Task ExecuteAsync_IncompleteShardEvidence_IsStructuredProviderFailure()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, _) = Runtime(SearchResponse(successfulShards: 0));
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);

        var result = await executor.ExecuteAsync(Request(
            fixture,
            artifact,
            runtime,
            parameters: new Dictionary<QueryParameterId, ObservationValue>
            {
                [new("status")] = ObservationValue.FromString("ready")
            }));

        Assert.Equal(RelationQueryExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure
            && diagnostic.Message.Contains("shard-success", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_PreCanceledToken_PropagatesBeforeProviderIo()
    {
        var fixture = ElasticRelationQueryCompilerTests.Fixture.Row(limit: 1);
        var artifact = Compile(fixture);
        var (runtime, calls) = Runtime(SearchResponse());
        var executor = new ElasticRelationQueryArtifactExecutor(runtime);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                Request(
                    fixture,
                    artifact,
                    runtime,
                    parameters: new Dictionary<QueryParameterId, ObservationValue>
                    {
                        [new("status")] = ObservationValue.FromString("ready")
                    }),
                cancellation.Token));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("1.000", 1L)]
    [InlineData("1e2", 100L)]
    [InlineData("9223372036854775808", null)]
    [InlineData("1.5", null)]
    public void CanonicalValueCodec_Int64Result_EnforcesExactMathematicalIntegerBoundary(
        string json,
        long? expected)
    {
        using var document = JsonDocument.Parse(json);
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64));

        var decoded = ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
            document.RootElement,
            contract,
            ElasticRelationQueryResultValueEncoding.JsonInt64,
            out var value);

        Assert.Equal(expected is not null, decoded);
        if (expected is not null)
            Assert.Equal(expected.Value, value.Int64);
    }

    [Theory]
    [InlineData(ScalarTypeKind.Guid, "d2719eb2-1f21-4d72-87a0-0802f39bc16a", true)]
    [InlineData(ScalarTypeKind.Guid, "not-a-guid", false)]
    [InlineData(ScalarTypeKind.Date, "2026-08-01", true)]
    [InlineData(ScalarTypeKind.Date, "not-a-date", false)]
    [InlineData(ScalarTypeKind.DateTime, "2026-08-01T12:34:56.0000000", true)]
    [InlineData(ScalarTypeKind.Instant, "2026-08-01T12:34:56.0000000+00:00", true)]
    [InlineData(ScalarTypeKind.Instant, "2026-08-01T12:34:56.0000000", false)]
    public void CanonicalValueCodec_StringDomains_AreValidatedByRetainedSemanticContract(
        ScalarTypeKind kind,
        string physicalValue,
        bool expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(physicalValue));
        var contract = new ValueContract(new ScalarTypeRef(kind));
        var encoding = kind is ScalarTypeKind.Guid
            ? ElasticRelationQueryResultValueEncoding.JsonString
            : ElasticRelationQueryResultValueEncoding.CanonicalTemporalString;

        Assert.True(ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
            document.RootElement,
            contract,
            encoding,
            out var value));
        Assert.Equal(expected, contract.IsSatisfiedByConstant(value));
        Assert.Equal(physicalValue, value.String);
    }

    static ElasticRelationQueryCompiledArtifact Compile(
        ElasticRelationQueryCompilerTests.Fixture fixture)
    {
        var compilation = fixture.Compile();
        Assert.True(
            compilation.IsSuccessful,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.Single(compilation.Artifacts);
    }

    static ElasticRelationQueryArtifactExecutionRequest Request(
        ElasticRelationQueryCompilerTests.Fixture fixture,
        ElasticRelationQueryCompiledArtifact artifact,
        ElasticElasticsearchRuntimeBinding runtime,
        long maximumRows = 100,
        ElasticElasticsearchRuntimeFingerprint? runtimeFingerprint = null,
        IReadOnlyDictionary<QueryParameterId, ObservationValue>? parameters = null) =>
        new(
            plan: fixture.PlanReference,
            realization: fixture.Realization.Fingerprint,
            placement: fixture.Placement.Fingerprint,
            storageBindingFingerprint: fixture.StorageBinding.Fingerprint,
            runtimeFingerprint: runtimeFingerprint ?? runtime.Fingerprint,
            artifact: artifact,
            maximumRows: maximumRows,
            parameters: parameters ?? new Dictionary<QueryParameterId, ObservationValue>());

    static ObservationValue Field(
        RelationQueryOutputRow row,
        ElasticRelationQueryCompiledArtifact artifact,
        ElasticRelationQueryResultSourceKind sourceKind,
        string physicalName)
    {
        var binding = artifact.ResultFields.Single(field =>
            field.SourceKind == sourceKind
            && string.Equals(field.PhysicalName, physicalName, StringComparison.Ordinal));
        Assert.True(row.Value.TryGetField(binding.Field.Path, out var value));
        return value;
    }

    static (ElasticElasticsearchRuntimeBinding Runtime, List<ApiCallDetails> Calls) Runtime(
        string response,
        ElasticClusterId? cluster = null,
        int statusCode = 200)
    {
        List<ApiCallDetails> calls = [];
        InMemoryRequestInvoker invoker = new(
            responseBody: Encoding.UTF8.GetBytes(response),
            statusCode: statusCode,
            exception: null,
            contentType: "application/json",
            headers: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Elastic-Product"] = ["Elasticsearch"]
            });
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(invoker)
            .DisableDirectStreaming()
            .OnRequestCompleted(calls.Add));
        return (
            new ElasticElasticsearchRuntimeBinding(
                cluster: cluster ?? new("cluster-tests"),
                client: client,
                authority: "tests/elastic-query-runtime/v1"),
            calls);
    }

    static string SearchResponse(
        string hits = "[]",
        long totalValue = 0,
        string totalRelation = "eq",
        string? aggregations = null,
        bool timedOut = false,
        int totalShards = 1,
        int successfulShards = 1,
        int failedShards = 0) =>
        $$"""
        {
          "took": 1,
          "timed_out": {{timedOut.ToString().ToLowerInvariant()}},
          "_shards": {"total": {{totalShards}}, "successful": {{successfulShards}}, "skipped": 0, "failed": {{failedShards}}},
          "hits": {
            "total": {"value": {{totalValue}}, "relation": "{{totalRelation}}"},
            "max_score": null,
            "hits": {{hits}}
          }{{(aggregations is null ? string.Empty : $",\n  \"aggregations\": {aggregations}")}}
        }
        """;
}
