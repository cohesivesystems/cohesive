using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.MaterializationHarness.Control;

namespace Cohesive.MaterializationHarness.Materialize;

sealed record MaterializationHarnessElasticFaultPlan(
    string RunIdentity,
    string Provider,
    MaterializationHarnessElasticFaultKind Kind,
    string MarkerPath,
    string ReadAlias)
{
    const string KindVariable = "COHESIVE_MATERIALIZATION_ELASTIC_FAULT_KIND";
    const string MarkerVariable = "COHESIVE_MATERIALIZATION_ELASTIC_FAULT_MARKER_PATH";
    const string ProviderVariable = "COHESIVE_MATERIALIZATION_ELASTIC_FAULT_PROVIDER";
    const string RunVariable = "COHESIVE_MATERIALIZATION_ELASTIC_FAULT_RUN_ID";

    internal static MaterializationHarnessElasticFaultPlan? FromEnvironment(
        string provider,
        string readAlias)
    {
        var rawKind = Environment.GetEnvironmentVariable(KindVariable);
        if (string.IsNullOrWhiteSpace(rawKind))
            return null;
        if (!Enum.TryParse<MaterializationHarnessElasticFaultKind>(rawKind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            throw new InvalidOperationException($"Set {KindVariable} to a supported Elastic harness fault kind.");
        }
        var selectedProvider = RequiredEnvironment(ProviderVariable);
        if (!string.Equals(provider, selectedProvider, StringComparison.Ordinal))
            return null;
        var markerPath = RequiredEnvironment(MarkerVariable);
        if (!Path.IsPathFullyQualified(markerPath))
            throw new InvalidOperationException($"Set {MarkerVariable} to an absolute path.");
        return new(
            RunIdentity: RequiredEnvironment(RunVariable),
            Provider: selectedProvider,
            Kind: kind,
            MarkerPath: Path.GetFullPath(markerPath),
            ReadAlias: readAlias);
    }

    static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} when an Elastic harness fault is armed.");
}

sealed class MaterializationHarnessElasticFaultHandler(
    HttpMessageHandler innerHandler,
    MaterializationHarnessElasticFaultPlan plan) : DelegatingHandler(innerHandler)
{
    static readonly JsonSerializerOptions MarkerJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    readonly SemaphoreSlim observationGate = new(initialCount: 1, maxCount: 1);
    int injectionClaimed;
    MaterializationHarnessElasticFaultObservation? observation;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var match = await MatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (match is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (Volatile.Read(ref observation) is not null)
        {
            await RecordFollowupAsync(match, cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (Interlocked.CompareExchange(ref injectionClaimed, value: 1, comparand: 0) != 0)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        return plan.Kind switch
        {
            MaterializationHarnessElasticFaultKind.RetryableBulkRejection =>
                await ApplyPartialBulkFaultAsync(
                    request,
                    match,
                    appliedItemCount: 1,
                    statusCode: 429,
                    errorType: "es_rejected_execution_exception",
                    errorReason: "The attributable harness fault rejected unresolved bulk items.",
                    cancellationToken).ConfigureAwait(false),
            MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure =>
                await ApplyPartialBulkFaultAsync(
                    request,
                    match,
                    appliedItemCount: match.Items.Length - 1,
                    statusCode: 400,
                    errorType: "mapper_parsing_exception",
                    errorReason: "The attributable harness fault produced one permanent item failure.",
                    cancellationToken).ConfigureAwait(false),
            MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss =>
                await LoseAppliedPromotionResponseAsync(request, match, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported Elastic harness fault '{plan.Kind}'.")
        };
    }

    async Task<HttpResponseMessage> ApplyPartialBulkFaultAsync(
        HttpRequestMessage request,
        ElasticFaultMatch match,
        int appliedItemCount,
        int statusCode,
        string errorType,
        string errorReason,
        CancellationToken cancellationToken)
    {
        if (appliedItemCount <= 0 || appliedItemCount >= match.Items.Length)
            throw new InvalidOperationException("A partial bulk fault requires both applied and rejected items.");

        var appliedWireBytes = match.Items.Take(appliedItemCount).Sum(static item => item.WireBytes.Length);
        using var appliedRequest = CloneRequest(request, match.Body.AsMemory(0, appliedWireBytes));
        using var appliedResponse = await base.SendAsync(appliedRequest, cancellationToken).ConfigureAwait(false);
        if (!appliedResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The real Elasticsearch subset returned HTTP {(int)appliedResponse.StatusCode} before fault injection.",
                inner: null,
                appliedResponse.StatusCode);
        }
        var appliedResponseBytes = await appliedResponse.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        using var appliedDocument = JsonDocument.Parse(appliedResponseBytes);
        var appliedItems = appliedDocument.RootElement.GetProperty("items");
        if (appliedItems.ValueKind != JsonValueKind.Array || appliedItems.GetArrayLength() != appliedItemCount)
            throw new InvalidOperationException("The real Elasticsearch subset returned incompatible bulk evidence.");

        var responseBytes = BuildPartialBulkResponse(
            appliedItems,
            match.Items.AsSpan()[appliedItemCount..],
            statusCode,
            errorType,
            errorReason);
        await RecordInjectionAsync(
            match,
            appliedItems: match.Items[..appliedItemCount],
            rejectedItems: match.Items[appliedItemCount..],
            responseLostAfterApply: false,
            cancellationToken).ConfigureAwait(false);
        return JsonResponse(request, responseBytes);
    }

    async Task<HttpResponseMessage> LoseAppliedPromotionResponseAsync(
        HttpRequestMessage request,
        ElasticFaultMatch match,
        CancellationToken cancellationToken)
    {
        using var appliedResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!appliedResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The real Elasticsearch alias transaction returned HTTP {(int)appliedResponse.StatusCode} before response loss.",
                inner: null,
                appliedResponse.StatusCode);
        }
        await RecordInjectionAsync(
            match,
            appliedItems: [],
            rejectedItems: [],
            responseLostAfterApply: true,
            cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException("The attributable harness fault lost one applied alias-promotion response.");
    }

    async Task<ElasticFaultMatch?> MatchAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path is null)
            return null;
        if (plan.Kind == MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss
            && observation is not null
            && request.Method == HttpMethod.Get
            && InspectsAlias(path, plan.ReadAlias))
        {
            var requestIdentity = Encoding.UTF8.GetBytes(request.RequestUri!.PathAndQuery);
            return new(path, [], Fingerprint(requestIdentity), []);
        }
        if (request.Method != HttpMethod.Post || request.Content is null)
            return null;
        if (plan.Kind is MaterializationHarnessElasticFaultKind.RetryableBulkRejection
            or MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure)
        {
            if (!string.Equals(path, "/_bulk", StringComparison.Ordinal))
                return null;
            var wireBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var body = DecodeRequestBody(request.Content.Headers.ContentEncoding, wireBody);
            var items = ParseBulkItems(body);
            return items.Length < 2 && observation is null
                ? null
                : new(path, body, Fingerprint(body), items);
        }
        if (!string.Equals(path, "/_aliases", StringComparison.Ordinal))
            return null;
        var aliasWireBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var aliasBody = DecodeRequestBody(request.Content.Headers.ContentEncoding, aliasWireBody);
        return ContainsAlias(aliasBody, plan.ReadAlias)
            ? new(path, aliasBody, Fingerprint(aliasBody), [])
            : null;
    }

    async Task RecordInjectionAsync(
        ElasticFaultMatch match,
        ImmutableArray<BulkWireItem> appliedItems,
        ImmutableArray<BulkWireItem> rejectedItems,
        bool responseLostAfterApply,
        CancellationToken cancellationToken)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var injected = match.Items.Select(static item => item.Evidence).ToImmutableArray();
        observation = new(
            SchemaVersion: 1,
            RunIdentity: plan.RunIdentity,
            Provider: plan.Provider,
            Kind: plan.Kind,
            HostProcessId: Environment.ProcessId,
            RequestPath: match.Path,
            InjectedRequestFingerprint: match.Fingerprint,
            InjectedItems: injected,
            AppliedItems: appliedItems.Select(static item => item.Evidence).ToImmutableArray(),
            RejectedItems: rejectedItems.Select(static item => item.Evidence).ToImmutableArray(),
            ResponseLostAfterApply: responseLostAfterApply,
            MatchingRequestCount: 1,
            ExactRetryRequestFingerprint: null,
            ExactRetryItems: [],
            ReconciliationRequestPath: null,
            OccurredAtUtc: observedAtUtc,
            LastObservedAtUtc: observedAtUtc);
        await WriteObservationAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task RecordFollowupAsync(
        ElasticFaultMatch match,
        CancellationToken cancellationToken)
    {
        await observationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = observation
                ?? throw new InvalidOperationException("Elastic fault follow-up arrived before injection evidence.");
            var candidateItems = match.Items.Select(static item => item.Evidence).ToImmutableArray();
            var isExactRetry = plan.Kind == MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss
                ? match.Path == "/_aliases" && match.Fingerprint == current.InjectedRequestFingerprint
                : candidateItems.SequenceEqual(current.RejectedItems);
            var isReconciliation = plan.Kind == MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss
                && match.Path.StartsWith("/_alias/", StringComparison.Ordinal);
            observation = current with
            {
                MatchingRequestCount = current.MatchingRequestCount + 1,
                ExactRetryRequestFingerprint = current.ExactRetryRequestFingerprint ?? (isExactRetry
                    ? match.Fingerprint
                    : null),
                ExactRetryItems = current.ExactRetryRequestFingerprint is null && isExactRetry
                    ? candidateItems
                    : current.ExactRetryItems,
                ReconciliationRequestPath = current.ReconciliationRequestPath ?? (isReconciliation
                    ? match.Path
                    : null),
                LastObservedAtUtc = DateTimeOffset.UtcNow
            };
            await WriteObservationCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            observationGate.Release();
        }
    }

    async Task WriteObservationAsync(CancellationToken cancellationToken)
    {
        await observationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteObservationCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            observationGate.Release();
        }
    }

    async Task WriteObservationCoreAsync(CancellationToken cancellationToken)
    {
        var current = observation
            ?? throw new InvalidOperationException("No Elastic fault observation is available to persist.");
        var directory = Path.GetDirectoryName(plan.MarkerPath)
            ?? throw new InvalidOperationException("The Elastic fault marker path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{plan.MarkerPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(current, MarkerJson),
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, plan.MarkerPath, overwrite: true);
    }

    static HttpRequestMessage CloneRequest(HttpRequestMessage source, ReadOnlyMemory<byte> body)
    {
        HttpRequestMessage clone = new(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
            Content = new ByteArrayContent(body.ToArray())
        };
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in source.Content!.Headers)
        {
            if (string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    static byte[] DecodeRequestBody(ICollection<string> contentEncodings, byte[] wireBody)
    {
        if (contentEncodings.Count == 0)
            return wireBody;
        if (contentEncodings.Count != 1
            || !string.Equals(contentEncodings.Single(), "gzip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The Elastic harness fault cannot inspect content encoding '{string.Join(", ", contentEncodings)}'.");
        }
        using MemoryStream source = new(wireBody, writable: false);
        using GZipStream gzip = new(source, CompressionMode.Decompress);
        using MemoryStream decoded = new();
        gzip.CopyTo(decoded);
        return decoded.ToArray();
    }

    static HttpResponseMessage JsonResponse(HttpRequestMessage request, byte[] body)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(body)
        };
        response.Headers.TryAddWithoutValidation("X-Elastic-Product", "Elasticsearch");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    static byte[] BuildPartialBulkResponse(
        JsonElement appliedItems,
        ReadOnlySpan<BulkWireItem> rejectedItems,
        int statusCode,
        string errorType,
        string errorReason)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("took", 0);
            writer.WriteBoolean("errors", true);
            writer.WriteStartArray("items");
            foreach (var applied in appliedItems.EnumerateArray())
                applied.WriteTo(writer);
            foreach (var rejected in rejectedItems)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(rejected.Evidence.Operation);
                writer.WriteStartObject();
                writer.WriteString("_index", rejected.Evidence.Index);
                writer.WriteString("_id", rejected.Evidence.Item);
                writer.WriteNumber("status", statusCode);
                writer.WriteStartObject("error");
                writer.WriteString("type", errorType);
                writer.WriteString("reason", errorReason);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static ImmutableArray<BulkWireItem> ParseBulkItems(byte[] body)
    {
        var builder = ImmutableArray.CreateBuilder<BulkWireItem>();
        var cursor = 0;
        while (cursor < body.Length)
        {
            var actionEnd = Array.IndexOf(body, (byte)'\n', cursor);
            if (actionEnd < 0 || actionEnd == cursor)
                throw new InvalidOperationException("The Elastic harness fault received invalid bulk NDJSON.");
            using var actionDocument = JsonDocument.Parse(body.AsMemory(cursor, actionEnd - cursor));
            var actionRoot = actionDocument.RootElement;
            string operation;
            JsonElement action;
            if (actionRoot.TryGetProperty("index", out action))
                operation = "index";
            else if (actionRoot.TryGetProperty("delete", out action))
                operation = "delete";
            else
                throw new InvalidOperationException("The Elastic harness fault received an unsupported bulk action.");
            var wireEnd = actionEnd + 1;
            if (operation == "index")
            {
                var sourceEnd = Array.IndexOf(body, (byte)'\n', wireEnd);
                if (sourceEnd < 0 || sourceEnd == wireEnd)
                    throw new InvalidOperationException("An index bulk action omitted its source line.");
                wireEnd = sourceEnd + 1;
            }
            builder.Add(new(
                Evidence: new(
                    Operation: operation,
                    Index: action.GetProperty("_index").GetString()
                        ?? throw new InvalidOperationException("A bulk action omitted its index."),
                    Item: action.GetProperty("_id").GetString()
                        ?? throw new InvalidOperationException("A bulk action omitted its item identity.")),
                WireBytes: body.AsMemory(cursor, wireEnd - cursor)));
            cursor = wireEnd;
        }
        return builder.ToImmutable();
    }

    static bool ContainsAlias(byte[] body, string alias)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var action in actions.EnumerateArray())
        {
            foreach (var property in action.EnumerateObject())
            {
                if (property.Value.TryGetProperty("alias", out var candidate)
                    && string.Equals(candidate.GetString(), alias, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    static bool InspectsAlias(string path, string alias)
    {
        const string prefix = "/_alias/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return Uri.UnescapeDataString(path[prefix.Length..])
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(alias, StringComparer.Ordinal);
    }

    static string Fingerprint(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    sealed record ElasticFaultMatch(
        string Path,
        byte[] Body,
        string Fingerprint,
        ImmutableArray<BulkWireItem> Items);

    readonly record struct BulkWireItem(
        MaterializationHarnessElasticFaultItemEvidence Evidence,
        ReadOnlyMemory<byte> WireBytes);
}
