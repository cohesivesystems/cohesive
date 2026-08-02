using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Search;

namespace Cohesive.Adapters.Elastic;

/// <summary>Physical buffering options for canonical Elasticsearch compiled-artifact execution.</summary>
public sealed record ElasticRelationQueryArtifactExecutionOptions
{
    /// <summary>Conventional maximum number of canonical rows retained by one invocation.</summary>
    public const long DefaultMaximumBufferedRows = 10_000;

    /// <summary>Creates physical execution options.</summary>
    /// <param name="maximumBufferedRows">
    /// Positive maximum number of canonical hit or aggregation rows retained by one invocation.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumBufferedRows"/> is not positive or exceeds the maximum immutable-array length.
    /// </exception>
    public ElasticRelationQueryArtifactExecutionOptions(
        long maximumBufferedRows = DefaultMaximumBufferedRows)
    {
        if (maximumBufferedRows <= 0 || maximumBufferedRows > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBufferedRows),
                maximumBufferedRows,
                $"An Elasticsearch buffered-row boundary must be between 1 and {Array.MaxLength.ToString(CultureInfo.InvariantCulture)}.");
        }

        MaximumBufferedRows = maximumBufferedRows;
    }

    /// <summary>Maximum number of canonical hit or aggregation rows retained by one invocation.</summary>
    public long MaximumBufferedRows { get; }
}

/// <summary>
/// Exact physical invocation of one canonical Elasticsearch compiled artifact and every affinity fact required to
/// reject stale, misplaced, or incorrectly attested execution.
/// </summary>
public sealed class ElasticRelationQueryArtifactExecutionRequest
{
    /// <summary>Creates one exact compiled-artifact invocation.</summary>
    /// <param name="plan">Exact demand-scoped compiled-plan reference expected by the artifact.</param>
    /// <param name="realization">Exact realization fingerprint expected by the artifact.</param>
    /// <param name="placement">Exact source-placement fingerprint expected by the artifact.</param>
    /// <param name="storageBindingFingerprint">
    /// Exact Elasticsearch storage-binding fingerprint expected by the artifact.
    /// </param>
    /// <param name="runtimeFingerprint">
    /// Exact sanitized Elasticsearch runtime attestation expected by the executor.
    /// </param>
    /// <param name="artifact">Canonical Elasticsearch compiled artifact to execute.</param>
    /// <param name="maximumRows">
    /// Positive invocation-specific result boundary, further constrained by executor and compiled paging limits.
    /// </param>
    /// <param name="parameters">Invocation values keyed by canonical parameter identity.</param>
    /// <exception cref="ArgumentNullException">A reference parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumRows"/> is not positive.</exception>
    public ElasticRelationQueryArtifactExecutionRequest(
        RelationQueryCompiledPlanReference plan,
        RelationQueryRealizationFingerprint realization,
        RelationQuerySourcePlacementFingerprint placement,
        ElasticRelationQueryBindingFingerprint storageBindingFingerprint,
        ElasticElasticsearchRuntimeFingerprint runtimeFingerprint,
        ElasticRelationQueryCompiledArtifact artifact,
        long maximumRows,
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        Plan = Guard.RequireNotNull(plan);
        Realization = Guard.RequireNotNull(realization);
        Placement = Guard.RequireNotNull(placement);
        StorageBindingFingerprint = Guard.RequireNotNull(storageBindingFingerprint);
        RuntimeFingerprint = Guard.RequireNotNull(runtimeFingerprint);
        Artifact = Guard.RequireNotNull(artifact);
        if (maximumRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                "A canonical Elasticsearch artifact invocation requires a positive result-row boundary.");
        }

        ArgumentNullException.ThrowIfNull(parameters);
        MaximumRows = maximumRows;
        Parameters = parameters.ToImmutableDictionary();
    }

    /// <summary>Exact demand-scoped compiled-plan reference expected by the artifact.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact realization fingerprint expected by the artifact.</summary>
    public RelationQueryRealizationFingerprint Realization { get; }

    /// <summary>Exact source-placement fingerprint expected by the artifact.</summary>
    public RelationQuerySourcePlacementFingerprint Placement { get; }

    /// <summary>Exact Elasticsearch storage-binding fingerprint expected by the artifact.</summary>
    public ElasticRelationQueryBindingFingerprint StorageBindingFingerprint { get; }

    /// <summary>Exact sanitized Elasticsearch runtime attestation expected by the executor.</summary>
    public ElasticElasticsearchRuntimeFingerprint RuntimeFingerprint { get; }

    /// <summary>Canonical Elasticsearch compiled artifact to execute.</summary>
    public ElasticRelationQueryCompiledArtifact Artifact { get; }

    /// <summary>Positive invocation-specific result-row boundary.</summary>
    public long MaximumRows { get; }

    /// <summary>Immutable invocation values keyed by canonical parameter identity.</summary>
    public ImmutableDictionary<QueryParameterId, ObservationValue> Parameters { get; }
}

/// <summary>One ordered Elasticsearch continuation value without exposing an SDK response type.</summary>
public sealed record ElasticRelationQueryContinuationValue
{
    internal ElasticRelationQueryContinuationValue(string physicalField, ObservationValue value)
    {
        PhysicalField = Guard.RequireNotNullOrWhiteSpace(physicalField);
        if (!ElasticQueryValueTemplate.IsSupportedScalar(value)
            || value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
        {
            throw new ArgumentException(
                "An Elasticsearch continuation requires one supported non-null scalar value.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Physical stable sort or composite-grouping field represented by the value.</summary>
    public string PhysicalField { get; }

    /// <summary>Portable scalar evidence used by the next canonical paging decision.</summary>
    public ObservationValue Value { get; }
}

/// <summary>Exact provider continuation returned by one successful Elasticsearch artifact invocation.</summary>
/// <remarks>
/// This is portable physical evidence, not an opaque SDK token and not a second paging authority. Values are ordered
/// by the originating artifact's retained physical sort or composite-source contract. A caller may project them
/// back onto parameterized continuation slots with <see cref="TryCreateParameterOverrides"/>; otherwise the
/// canonical planning layer must author and compile the next page from these values.
/// </remarks>
public sealed class ElasticRelationQueryArtifactContinuation
{
    internal ElasticRelationQueryArtifactContinuation(
        ElasticRelationQueryArtifactFingerprint artifactFingerprint,
        ElasticRelationQueryPagingKind kind,
        ImmutableArray<ElasticRelationQueryContinuationValue> values)
    {
        ArtifactFingerprint = Guard.RequireNotNull(artifactFingerprint);
        if (kind is not (ElasticRelationQueryPagingKind.SearchAfter or ElasticRelationQueryPagingKind.CompositeAfter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Only search-after and composite-after execution can return a continuation.");
        }

        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static value => value is null))
            throw new ArgumentException("An Elasticsearch continuation requires non-null ordered values.", nameof(values));
        if (normalized.GroupBy(static value => value.PhysicalField, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("An Elasticsearch continuation cannot repeat a physical field.", nameof(values));
        }

        Kind = kind;
        Values = normalized;
    }

    /// <summary>Exact compiled artifact whose paging contract produced this evidence.</summary>
    public ElasticRelationQueryArtifactFingerprint ArtifactFingerprint { get; }

    /// <summary>Physical continuation mechanism represented by this value set.</summary>
    public ElasticRelationQueryPagingKind Kind { get; }

    /// <summary>Continuation values in compiled stable-sort or grouping order.</summary>
    public ImmutableArray<ElasticRelationQueryContinuationValue> Values { get; }

    /// <summary>
    /// Tries to project this physical evidence onto the originating artifact's canonical continuation parameters.
    /// </summary>
    /// <param name="artifact">Artifact expected to have produced this continuation.</param>
    /// <param name="parameterOverrides">
    /// Canonical continuation parameter values to merge with the other invocation parameters when projection is
    /// possible; otherwise an empty immutable dictionary.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the exact artifact has one distinct, untransformed parameter slot for every
    /// continuation value; otherwise <see langword="false"/>. A first-page artifact without continuation slots
    /// therefore returns <see langword="false"/> and must be replanned for its next canonical page.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    public bool TryCreateParameterOverrides(
        ElasticRelationQueryCompiledArtifact artifact,
        out ImmutableDictionary<QueryParameterId, ObservationValue> parameterOverrides)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var templates = Kind switch
        {
            ElasticRelationQueryPagingKind.SearchAfter => artifact.RequestTemplate.Page.After,
            ElasticRelationQueryPagingKind.CompositeAfter => artifact.RequestTemplate.Aggregation.After,
            _ => []
        };
        if (ArtifactFingerprint != artifact.Fingerprint
            || artifact.Paging is null
            || artifact.Paging.Kind != Kind
            || templates.Length != Values.Length
            || !artifact.Paging.SortFields.SequenceEqual(
                Values.Select(static value => value.PhysicalField),
                StringComparer.Ordinal))
        {
            parameterOverrides = ImmutableDictionary<QueryParameterId, ObservationValue>.Empty;
            return false;
        }

        var builder = ImmutableDictionary.CreateBuilder<QueryParameterId, ObservationValue>();
        for (var index = 0; index < templates.Length; index++)
        {
            var template = templates[index];
            if (template is not
                {
                    SourceKind: ElasticQueryValueSourceKind.Parameter,
                    Parameter: { } parameter,
                    Transform: ElasticQueryValueTransform.None
                }
                || !builder.TryAdd(parameter, Values[index].Value))
            {
                parameterOverrides = ImmutableDictionary<QueryParameterId, ObservationValue>.Empty;
                return false;
            }
        }

        parameterOverrides = builder.ToImmutable();
        return true;
    }
}

/// <summary>One structured failure or warning emitted while executing a canonical Elasticsearch artifact.</summary>
public sealed record ElasticRelationQueryArtifactExecutionDiagnostic
{
    /// <summary>Creates an attributable artifact-execution diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable non-sensitive explanation.</param>
    /// <param name="branch">Affected native result branch.</param>
    /// <param name="rowOrdinal">Zero-based provider hit or bucket ordinal, or <see langword="null"/>.</param>
    /// <param name="evidenceReference">Opaque provider evidence reference, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A required string or branch identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="severity"/> is unsupported or <paramref name="rowOrdinal"/> is negative.
    /// </exception>
    public ElasticRelationQueryArtifactExecutionDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryNativeResultBranchId branch,
        long? rowOrdinal = null,
        string? evidenceReference = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("An execution diagnostic requires a branch identity.", nameof(branch));
        if (rowOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(rowOrdinal), rowOrdinal, "A result-row ordinal cannot be negative.");
        if (evidenceReference is not null && string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException("An evidence reference cannot be empty.", nameof(evidenceReference));

        Severity = severity;
        Branch = branch;
        RowOrdinal = rowOrdinal;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable non-sensitive explanation.</summary>
    public string Message { get; }

    /// <summary>Affected native result branch.</summary>
    public RelationQueryNativeResultBranchId Branch { get; }

    /// <summary>Zero-based provider hit or bucket ordinal, or <see langword="null"/>.</summary>
    public long? RowOrdinal { get; }

    /// <summary>Opaque provider evidence reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Immutable result of executing one canonical Elasticsearch compiled artifact.</summary>
public sealed class ElasticRelationQueryArtifactExecutionResult
{
    static readonly IComparer<ElasticRelationQueryArtifactExecutionDiagnostic> DiagnosticOrdering =
        Comparer<ElasticRelationQueryArtifactExecutionDiagnostic>.Create(
            static (left, right) => CompareDiagnostics(left, right));

    internal ElasticRelationQueryArtifactExecutionResult(
        ElasticRelationQueryArtifactExecutionRequest request,
        RelationQueryExecutionStatus status,
        ImmutableArray<RelationQueryOutputRow> rows,
        ImmutableArray<ElasticRelationQueryArtifactExecutionDiagnostic> diagnostics,
        ElasticRelationQueryArtifactContinuation? continuation,
        string? providerEvidenceReference)
    {
        Request = Guard.RequireNotNull(request);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported Elasticsearch execution status.");

        var normalizedRows = rows.IsDefault ? [] : rows;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedRows.Any(static row => row is null))
            throw new ArgumentException("Execution rows cannot contain null entries.", nameof(rows));
        if (normalizedRows.Any(row => row.Shape != request.Artifact.Branch.Shape))
            throw new ArgumentException("Execution rows must match the artifact branch shape.", nameof(rows));
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Execution diagnostics cannot contain null entries.", nameof(diagnostics));
        if (normalizedDiagnostics.Any(diagnostic => diagnostic.Branch != request.Artifact.Branch.Id))
            throw new ArgumentException("Execution diagnostics must identify the artifact branch.", nameof(diagnostics));

        var containsError = normalizedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var containsWarning = normalizedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        if (status == RelationQueryExecutionStatus.Succeeded && containsError)
            throw new ArgumentException("Successful execution cannot contain error diagnostics.", nameof(diagnostics));
        if (status == RelationQueryExecutionStatus.Failed && !normalizedRows.IsDefaultOrEmpty)
            throw new ArgumentException("Failed execution cannot expose untrustworthy partial rows.", nameof(rows));
        if (status == RelationQueryExecutionStatus.Failed && !containsError)
            throw new ArgumentException("Failed execution requires an error diagnostic.", nameof(diagnostics));
        if (status == RelationQueryExecutionStatus.Incomplete
            && (normalizedRows.IsDefaultOrEmpty || containsError || !containsWarning))
        {
            throw new ArgumentException(
                "Incomplete execution requires attributable prefix rows and a warning diagnostic.",
                nameof(status));
        }
        if (status == RelationQueryExecutionStatus.Failed && continuation is not null)
            throw new ArgumentException("Failed execution cannot expose an untrustworthy continuation.", nameof(continuation));
        if (continuation is not null
            && (request.Artifact.Paging is null
                || continuation.Kind != request.Artifact.Paging.Kind
                || continuation.ArtifactFingerprint != request.Artifact.Fingerprint))
        {
            throw new ArgumentException(
                "An execution continuation must match the exact artifact and its paging contract.",
                nameof(continuation));
        }
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
        {
            throw new ArgumentException(
                "A provider evidence reference cannot be empty.",
                nameof(providerEvidenceReference));
        }

        Status = status;
        Rows = normalizedRows;
        Diagnostics = normalizedDiagnostics.IsDefaultOrEmpty
            ? []
            : [.. normalizedDiagnostics.Order(DiagnosticOrdering)];
        Continuation = continuation;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    /// <summary>Exact invocation represented by this result.</summary>
    public ElasticRelationQueryArtifactExecutionRequest Request { get; }

    /// <summary>Whether execution succeeded, returned an attributable prefix, or failed.</summary>
    public RelationQueryExecutionStatus Status { get; }

    /// <summary>Canonical shaped rows in Elasticsearch hit or composite-bucket order.</summary>
    public ImmutableArray<RelationQueryOutputRow> Rows { get; }

    /// <summary>Structured deterministic execution diagnostics.</summary>
    public ImmutableArray<ElasticRelationQueryArtifactExecutionDiagnostic> Diagnostics { get; }

    /// <summary>Exact adapter continuation for another physical page, or <see langword="null"/>.</summary>
    public ElasticRelationQueryArtifactContinuation? Continuation { get; }

    /// <summary>Opaque non-sensitive provider response evidence reference, when available.</summary>
    public string? ProviderEvidenceReference { get; }

    /// <summary>Canonical terminal branch represented by <see cref="Rows"/>.</summary>
    public RelationQueryNativeResultBranch Branch => Request.Artifact.Branch;

    /// <summary>Whether execution completed conclusively without an error diagnostic.</summary>
    public bool IsSuccessful => Status == RelationQueryExecutionStatus.Succeeded;

    static int CompareDiagnostics(
        ElasticRelationQueryArtifactExecutionDiagnostic left,
        ElasticRelationQueryArtifactExecutionDiagnostic right)
    {
        var comparison = (left.RowOrdinal ?? -1L).CompareTo(right.RowOrdinal ?? -1L);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Code, right.Code);
        if (comparison != 0)
            return comparison;
        comparison = ((int)left.Severity).CompareTo((int)right.Severity);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(
            left.EvidenceReference ?? string.Empty,
            right.EvidenceReference ?? string.Empty);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Message, right.Message);
    }
}

/// <summary>Executes affinity-validated canonical Elasticsearch compiled artifacts through the Elasticsearch SDK.</summary>
/// <remarks>
/// The executor borrows its runtime binding, owns no SDK resource, and is safe for concurrent invocations to the
/// extent that the supplied <see cref="ElasticsearchClient"/> is safe. Each invocation performs at most one search;
/// durable retry, recurrence, and page orchestration remain above this adapter boundary.
/// </remarks>
public sealed class ElasticRelationQueryArtifactExecutor
{
    readonly ElasticElasticsearchRuntimeBinding runtimeBinding;
    readonly ElasticRelationQueryArtifactExecutionOptions options;

    /// <summary>Creates an artifact executor for one exactly attested Elasticsearch runtime.</summary>
    /// <param name="runtimeBinding">
    /// Borrowed-client runtime attestation whose fingerprint must match every invocation.
    /// </param>
    /// <param name="options">Physical buffering options, or <see langword="null"/> for conventions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeBinding"/> is <see langword="null"/>.</exception>
    public ElasticRelationQueryArtifactExecutor(
        ElasticElasticsearchRuntimeBinding runtimeBinding,
        ElasticRelationQueryArtifactExecutionOptions? options = null)
    {
        this.runtimeBinding = Guard.RequireNotNull(runtimeBinding);
        this.options = options ?? new();
    }

    /// <summary>Executes one exact canonical Elasticsearch artifact invocation.</summary>
    /// <param name="request">Artifact and exact affinity, attestation, and invocation facts to validate.</param>
    /// <param name="cancellationToken">Token observed before validation and throughout SDK execution and decoding.</param>
    /// <returns>
    /// A conclusive shaped result or a structured failed result. Affinity, attestation, invocation, provider,
    /// boundary, and physical-result outcomes are returned as diagnostics rather than thrown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public ValueTask<ElasticRelationQueryArtifactExecutionResult> ExecuteAsync(
        ElasticRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ElasticRelationQueryTelemetry.Emitter.IsEnabled
            ? ExecuteObservedAsync(request, cancellationToken)
            : ExecuteCoreAsync(request, cancellationToken);
    }

    async ValueTask<ElasticRelationQueryArtifactExecutionResult> ExecuteCoreAsync(
        ElasticRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken) =>
        TryPrepare(request, out var prepared, out var failure)
            ? await ExecutePreparedAsync(prepared!, cancellationToken).ConfigureAwait(false)
            : failure!;

    async ValueTask<ElasticRelationQueryArtifactExecutionResult> ExecuteObservedAsync(
        ElasticRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken)
    {
        Activity? activity = ElasticRelationQueryTelemetry.Emitter.StartActivity(
            RelationQueryTelemetry.NativeExecutionActivityName,
            ActivityKind.Client);
        var started = ElasticRelationQueryTelemetry.Emitter.StartTimer();
        try
        {
            ElasticRelationQueryArtifactExecutionResult result;
            if (TryPrepare(request, out var prepared, out var failure))
            {
                SetExecutionRequestTags(activity, request);
                result = await ExecutePreparedAsync(prepared!, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = failure!;
            }

            SetExecutionResultTags(activity, result);
            ElasticRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.NativeExecutionActivityName,
                RelationQueryTelemetry.GetStatusTagValue(result.Status));
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            ElasticRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.NativeExecutionActivityName,
                RelationQueryTelemetry.CanceledStatus,
                exception: exception);
            throw;
        }
        catch (Exception exception)
        {
            ElasticRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.NativeExecutionActivityName,
                RelationQueryTelemetry.ExceptionStatus,
                exception: exception);
            throw;
        }
    }

    static void SetExecutionRequestTags(
        Activity? activity,
        ElasticRelationQueryArtifactExecutionRequest request)
    {
        if (activity?.IsAllDataRequested != true)
            return;
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlanFingerprintTagName,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.Plan).Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.RealizationFingerprintTagName,
            request.Realization.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlacementFingerprintTagName,
            request.Placement.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.BindingFingerprintTagName,
            request.StorageBindingFingerprint.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.ArtifactFingerprintTagName,
            request.Artifact.Fingerprint.Value);
    }

    static void SetExecutionResultTags(
        Activity? activity,
        ElasticRelationQueryArtifactExecutionResult result)
    {
        if (activity?.IsAllDataRequested != true)
            return;
        activity.SetTag(RelationQueryTelemetry.RowCountTagName, result.Rows.Length);
        activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, result.Diagnostics.Length);
        foreach (var diagnostic in result.Diagnostics)
        {
            RelationQueryTelemetry.AddDiagnosticEvent(
                activity,
                diagnostic.Code,
                diagnostic.Severity);
        }
    }

    bool TryPrepare(
        ElasticRelationQueryArtifactExecutionRequest request,
        out PreparedArtifactInvocation? prepared,
        out ElasticRelationQueryArtifactExecutionResult? failure)
    {
        var affinityDiagnostics = ValidateAffinity(request);
        if (!affinityDiagnostics.IsDefaultOrEmpty)
        {
            prepared = null;
            failure = Failed(request, affinityDiagnostics);
            return false;
        }

        var maximumRows = Math.Min(options.MaximumBufferedRows, request.MaximumRows);
        var declaredRows = request.Artifact.Paging?.Limit ?? 1;
        if (declaredRows > maximumRows)
        {
            prepared = null;
            failure = Failed(
                request,
                [Error(
                    request: request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded,
                    message: $"The compiled Elasticsearch artifact may return {declaredRows.ToString(CultureInfo.InvariantCulture)} rows, which exceeds the exact execution boundary of {maximumRows.ToString(CultureInfo.InvariantCulture)}.")]);
            return false;
        }

        try
        {
            prepared = new(
                Request: request,
                SearchRequest: request.Artifact.Bind(request.Parameters),
                MaximumRows: maximumRows,
                ResultSourcePaths: PrepareResultSourcePaths(request.Artifact));
            failure = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            prepared = null;
            failure = Failed(
                request,
                [Error(
                    request: request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.InvocationInvalid,
                    message: $"The canonical Elasticsearch invocation could not be bound ({exception.GetType().Name}).")]);
            return false;
        }
    }

    async ValueTask<ElasticRelationQueryArtifactExecutionResult> ExecutePreparedAsync(
        PreparedArtifactInvocation invocation,
        CancellationToken cancellationToken)
    {
        SearchResponse<JsonElement> response;
        try
        {
            response = await runtimeBinding.Client.SearchAsync<JsonElement>(
                    invocation.SearchRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    message: $"The Elasticsearch SDK reported cancellation without invocation cancellation ({exception.GetType().Name}).")]);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    message: $"Elasticsearch query execution failed before a complete response was available ({ExceptionTypeChain(exception)}).")]);
        }

        var evidenceReference = ProviderEvidenceReference(response);
        if (!response.IsValidResponse)
        {
            var statusCode = response.ApiCallDetails?.HttpStatusCode;
            return Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    message: statusCode is null
                        ? "Elasticsearch rejected the search request without a trustworthy HTTP status."
                        : $"Elasticsearch rejected the search request with HTTP status {statusCode.Value.ToString(CultureInfo.InvariantCulture)}.",
                    evidenceReference: evidenceReference)],
                evidenceReference);
        }

        if (response.TimedOut)
        {
            return Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    message: "Elasticsearch timed out before producing a complete exact search response.",
                    evidenceReference: evidenceReference)],
                evidenceReference);
        }

        if (response.Shards is null
            || response.Shards.Failed != 0
            || response.Shards.Successful != response.Shards.Total)
        {
            return Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    message: "Elasticsearch did not attest a complete shard-success response.",
                    evidenceReference: evidenceReference)],
                evidenceReference);
        }

        return invocation.Request.Artifact.RequestTemplate.Aggregation.Kind switch
        {
            ElasticAggregationTemplateKind.None => DecodeHitRows(
                invocation,
                response,
                evidenceReference,
                cancellationToken),
            ElasticAggregationTemplateKind.GlobalCount => DecodeGlobalCount(
                invocation,
                response,
                evidenceReference,
                cancellationToken),
            ElasticAggregationTemplateKind.CompositeCount => DecodeCompositeCount(
                invocation,
                response,
                evidenceReference,
                cancellationToken),
            _ => Failed(
                invocation.Request,
                [Error(
                    request: invocation.Request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
                    message: "The compiled artifact uses an unsupported Elasticsearch aggregation kind.")])
        };
    }

    ImmutableArray<ElasticRelationQueryArtifactExecutionDiagnostic> ValidateAffinity(
        ElasticRelationQueryArtifactExecutionRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<ElasticRelationQueryArtifactExecutionDiagnostic>();
        var artifact = request.Artifact;
        var binding = artifact.StorageBinding;
        var provenance = artifact.Provenance;
        var adapterBinding = provenance.AdapterBinding;

        try
        {
            var requestPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.Plan);
            var artifactPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(provenance.Plan);
            Require(requestPlan == artifactPlan, "The invocation compiled-plan reference does not match artifact provenance.");
            Require(
                binding.CompiledPlanFingerprint is not null && binding.CompiledPlanFingerprint == requestPlan,
                "The Elasticsearch storage binding lacks or conflicts with exact compiled-plan affinity.");
            Require(
                binding.PlacementFingerprint is not null && binding.PlacementFingerprint == request.Placement,
                "The Elasticsearch storage binding lacks or conflicts with exact source-placement affinity.");
            Require(
                ElasticRelationQueryBindingFingerprinter.Compute(binding) == binding.Fingerprint,
                "The Elasticsearch storage-binding fingerprint is stale or malformed.");
            Require(
                ElasticRelationQueryArtifactFingerprinter.Compute(
                    artifact.Branch,
                    artifact.RequestTemplate,
                    binding,
                    artifact.SelectedFields,
                    artifact.ResultFields,
                    artifact.Parameters,
                    artifact.Paging,
                    artifact.LoweringDecisions,
                    artifact.LoweringPolicyFingerprint,
                    provenance) == artifact.Fingerprint,
                "The Elasticsearch compiled-artifact fingerprint is stale or malformed.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            diagnostics.Add(Error(
                request: request,
                code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
                message: $"The Elasticsearch compiled artifact could not be affinity-verified ({exception.GetType().Name})."));
        }

        Require(
            request.StorageBindingFingerprint == binding.Fingerprint,
            "The invocation storage-binding fingerprint does not match the compiled artifact.");
        Require(
            request.RuntimeFingerprint == runtimeBinding.Fingerprint,
            "The invocation runtime attestation does not match the executor's exact Elasticsearch client binding.");
        Require(
            request.Realization == provenance.Realization,
            "The invocation realization fingerprint does not match artifact provenance.");
        Require(
            request.Placement == provenance.Placement,
            "The invocation source-placement fingerprint does not match artifact provenance.");
        Require(
            artifact.Branch.Id == provenance.Branch,
            "The artifact branch identity does not match its provenance.");
        Require(
            binding.Target == provenance.Target,
            "The Elasticsearch binding target does not match artifact provenance.");
        Require(
            binding.TargetProfile == provenance.TargetProfile,
            "The Elasticsearch binding target profile does not match artifact provenance.");
        Require(
            provenance.Target == ElasticRelationQueryTargetProfile.Target
            && provenance.TargetProfile == ElasticRelationQueryTargetProfile.ProfileId,
            "The artifact does not target the canonical Elasticsearch capability profile.");
        Require(
            string.Equals(
                provenance.CompilerProfile,
                ElasticRelationQueryCompilerOptions.CurrentCompilerProfile,
                StringComparison.Ordinal),
            "The artifact compiler profile is not supported by this Elasticsearch executor.");
        Require(
            string.Equals(adapterBinding.SchemaVersion, binding.SchemaVersion, StringComparison.Ordinal)
            && string.Equals(adapterBinding.BindingId, binding.Id.Value, StringComparison.Ordinal)
            && adapterBinding.Target == binding.Target
            && adapterBinding.TargetProfile == binding.TargetProfile
            && string.Equals(adapterBinding.Fingerprint.Algorithm, binding.Fingerprint.Algorithm, StringComparison.Ordinal)
            && string.Equals(adapterBinding.Fingerprint.Canonicalization, binding.Fingerprint.Canonicalization, StringComparison.Ordinal)
            && string.Equals(adapterBinding.Fingerprint.Value, binding.Fingerprint.Value, StringComparison.Ordinal)
            && adapterBinding.CompiledPlanFingerprint == binding.CompiledPlanFingerprint
            && adapterBinding.PlacementFingerprint == binding.PlacementFingerprint
            && adapterBinding.Sources.SequenceEqual([binding.Source])
            && adapterBinding.PlacementBindings.SequenceEqual([binding.PlacementBinding]),
            "The artifact provenance does not retain the exact Elasticsearch storage-binding reference.");
        Require(
            string.Equals(artifact.RequestTemplate.Index, binding.IndexName, StringComparison.Ordinal),
            "The compiled request template does not address the exact Elasticsearch storage binding.");
        Require(
            artifact.ResultFields.Select(static field => field.Field).SequenceEqual(artifact.Branch.Fields),
            "The artifact result-field bindings do not exactly cover the native branch fields in canonical order.");
        Require(
            artifact.SelectedFields.Select(static field => field.Input).SequenceEqual(provenance.InputFields),
            "The artifact selected fields do not exactly cover its attributed compiled inputs in canonical order.");
        Require(
            artifact.ResultFields.All(field =>
                field.Field.Shape == artifact.Branch.Shape
                && field.ValueContract.Cardinality == FieldCardinality.Single
                && IsFieldOnlyPath(field.Field.Path)
                && ElasticRelationQueryResultValueEncodingSemantics.IsCompatible(
                    field.ValueContract,
                    field.Encoding)),
            "The artifact result fields contain an incompatible shape, path, cardinality, or value encoding.");
        Require(
            !HasPathPrefixConflict(artifact.ResultFields),
            "The artifact result-field paths cannot be reconstructed without a scalar/object collision.");
        ValidateExecutionShape(request, Require);

        return diagnostics.ToImmutable();

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                diagnostics.Add(Error(
                    request: request,
                    code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
                    message: message));
            }
        }
    }

    static void ValidateExecutionShape(
        ElasticRelationQueryArtifactExecutionRequest request,
        Action<bool, string> require)
    {
        var artifact = request.Artifact;
        var template = artifact.RequestTemplate;
        var resultSources = artifact.ResultFields.Select(static field => field.SourceKind).ToImmutableArray();
        require(
            artifact.Paging is null
            || (artifact.Paging.SortFields.Length == artifact.Paging.SortValueContracts.Length
                && artifact.Paging.SortValueContracts.All(static contract =>
                    ElasticRelationQueryResultValueEncodingSemantics.TryResolve(contract, out _))),
            "The Elasticsearch paging contract lacks an exact supported semantic value contract for every physical key.");
        switch (template.Aggregation.Kind)
        {
            case ElasticAggregationTemplateKind.None:
                require(
                    artifact.Branch.Kind == RelationQueryNativeResultKind.QueryRows,
                    "A non-aggregation Elasticsearch artifact must represent a canonical query-row branch.");
                require(
                    artifact.Paging is not null
                    && template.Page.Kind == (artifact.Paging.Kind switch
                    {
                        ElasticRelationQueryPagingKind.Offset => ElasticSearchPageKind.Offset,
                        ElasticRelationQueryPagingKind.SearchAfter => ElasticSearchPageKind.SearchAfter,
                        _ => (ElasticSearchPageKind)(-1)
                    }),
                    "The Elasticsearch hit page template conflicts with its retained paging contract.");
                require(
                    artifact.Paging is not null
                    && artifact.Paging.Limit == template.Page.Limit
                    && artifact.Paging.Offset == template.Page.Offset
                    && artifact.Paging.SortFields.SequenceEqual(
                        template.Sorts.Select(static sort => sort.Field),
                        StringComparer.Ordinal),
                    "The Elasticsearch hit paging and stable-sort evidence is inconsistent.");
                require(
                    resultSources.All(static source => source is
                        ElasticRelationQueryResultSourceKind.SourceField
                        or ElasticRelationQueryResultSourceKind.Constant),
                    "An Elasticsearch hit artifact contains an aggregation-only result source.");
                var expectedSources = artifact.ResultFields
                    .Where(static field => field.SourceKind == ElasticRelationQueryResultSourceKind.SourceField)
                    .Select(static field => field.PhysicalName!)
                    .ToHashSet(StringComparer.Ordinal);
                require(
                    expectedSources.SetEquals(template.SourceIncludes),
                    "The Elasticsearch hit source filter does not exactly cover its physical result fields.");
                break;
            case ElasticAggregationTemplateKind.GlobalCount:
                require(
                    artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation,
                    "A global-count Elasticsearch artifact must represent a canonical aggregation branch.");
                require(
                    artifact.Paging is null
                    && template.Page.Kind == ElasticSearchPageKind.None
                    && template.Sorts.IsDefaultOrEmpty
                    && template.SourceIncludes.IsDefaultOrEmpty,
                    "A global-count Elasticsearch artifact cannot retain hit paging, sorting, or source retrieval.");
                require(
                    resultSources.Count(static source => source == ElasticRelationQueryResultSourceKind.ExactTotalHits) == 1
                    && resultSources.All(static source => source is
                        ElasticRelationQueryResultSourceKind.ExactTotalHits
                        or ElasticRelationQueryResultSourceKind.Constant),
                    "A global-count Elasticsearch artifact requires exactly one exact-total-hits result.");
                break;
            case ElasticAggregationTemplateKind.CompositeCount:
                require(
                    artifact.Branch.Kind == RelationQueryNativeResultKind.QueryAggregation,
                    "A composite-count Elasticsearch artifact must represent a canonical aggregation branch.");
                require(
                    artifact.Paging is { Kind: ElasticRelationQueryPagingKind.CompositeAfter }
                    && artifact.Paging.Limit == template.Aggregation.Size
                    && template.Page.Kind == ElasticSearchPageKind.None
                    && template.Sorts.IsDefaultOrEmpty
                    && template.SourceIncludes.IsDefaultOrEmpty,
                    "A composite-count Elasticsearch artifact has inconsistent paging or hit retrieval metadata.");
                require(
                    artifact.Paging is not null
                    && artifact.Paging.SortFields.SequenceEqual(
                        template.Aggregation.Sources.Select(static source => source.Field),
                        StringComparer.Ordinal),
                    "The composite paging fields do not match the aggregation sources.");
                require(
                    CompositePagingContractsMatch(artifact, template.Aggregation),
                    "The composite paging value contracts do not match their canonical grouping results.");
                var expectedKeys = template.Aggregation.Sources
                    .Select(static source => source.Name)
                    .ToHashSet(StringComparer.Ordinal);
                require(
                    resultSources.Count(static source => source == ElasticRelationQueryResultSourceKind.CompositeDocumentCount) == 1
                    && resultSources.Count(static source => source == ElasticRelationQueryResultSourceKind.CompositeKey)
                    == expectedKeys.Count
                    && artifact.ResultFields
                        .Where(static field => field.SourceKind == ElasticRelationQueryResultSourceKind.CompositeKey)
                        .Select(static field => field.PhysicalName!)
                        .ToHashSet(StringComparer.Ordinal)
                        .SetEquals(expectedKeys)
                    && resultSources.All(static source => source is
                        ElasticRelationQueryResultSourceKind.CompositeKey
                        or ElasticRelationQueryResultSourceKind.CompositeDocumentCount
                        or ElasticRelationQueryResultSourceKind.Constant),
                    "A composite-count artifact does not exactly cover every grouping key and one document count.");
                break;
            default:
                require(false, "The Elasticsearch artifact uses an unsupported aggregation template.");
                break;
        }
    }

    static bool CompositePagingContractsMatch(
        ElasticRelationQueryCompiledArtifact artifact,
        ElasticAggregationTemplate aggregation)
    {
        if (artifact.Paging is not { } paging
            || aggregation.Sources.Length != paging.SortValueContracts.Length)
        {
            return false;
        }

        for (var index = 0; index < aggregation.Sources.Length; index++)
        {
            var source = aggregation.Sources[index];
            ElasticRelationQueryResultFieldBinding? match = null;
            foreach (var field in artifact.ResultFields)
            {
                if (field.SourceKind != ElasticRelationQueryResultSourceKind.CompositeKey
                    || !string.Equals(field.PhysicalName, source.Name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (match is not null)
                    return false;
                match = field;
            }
            if (match is null || match.ValueContract != paging.SortValueContracts[index])
                return false;
        }
        return true;
    }

    static ElasticRelationQueryArtifactExecutionResult DecodeHitRows(
        PreparedArtifactInvocation invocation,
        SearchResponse<JsonElement> response,
        string? evidenceReference,
        CancellationToken cancellationToken)
    {
        var request = invocation.Request;
        var artifact = request.Artifact;
        var paging = artifact.Paging!;
        var hits = response.Hits;
        if (response.Aggregations is { Count: > 0 })
        {
            return InvalidResult(
                request,
                "An Elasticsearch hit response unexpectedly contained aggregation results.",
                evidenceReference);
        }
        if (hits.Count > invocation.MaximumRows || hits.Count > paging.Limit)
        {
            return BoundaryFailure(
                request,
                hits.Count,
                paging.Limit,
                evidenceReference);
        }

        var rows = ImmutableArray.CreateBuilder<RelationQueryOutputRow>(hits.Count);
        ImmutableArray<ElasticRelationQueryContinuationValue> lastSort = default;
        var ordinal = 0L;
        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<ElasticRelationQueryContinuationValue> decodedSort = default;
            string? sortError = null;
            if (hit.Sort is null
                || !TryDecodeHitSortTuple(paging, hit.Sort, out decodedSort, out sortError))
            {
                return InvalidResult(
                    request,
                    sortError ?? "An Elasticsearch hit did not carry the exact compiled stable-sort tuple.",
                    evidenceReference,
                    ordinal);
            }
            if (hit.Source.ValueKind != JsonValueKind.Object)
            {
                return InvalidResult(
                    request,
                    "An Elasticsearch hit did not contain the exact filtered _source object required by the artifact.",
                    evidenceReference,
                    ordinal);
            }

            MutableObservationObject? valueBuilder = null;
            for (var fieldIndex = 0; fieldIndex < artifact.ResultFields.Length; fieldIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = artifact.ResultFields[fieldIndex];
                ObservationValue value;
                string? error;
                switch (field.SourceKind)
                {
                    case ElasticRelationQueryResultSourceKind.Constant:
                        value = field.Constant!.Value;
                        error = null;
                        break;
                    case ElasticRelationQueryResultSourceKind.SourceField:
                        if (!TryReadSourceValue(
                                hit.Source,
                                field,
                                invocation.ResultSourcePaths[fieldIndex],
                                out value,
                                out error))
                        {
                            return InvalidResult(request, error!, evidenceReference, ordinal);
                        }
                        break;
                    default:
                        return InvalidResult(
                            request,
                            "An Elasticsearch hit artifact used an aggregation-only result source.",
                            evidenceReference,
                            ordinal);
                }

                if (!TryAssignResultValue(ref valueBuilder, field, value, out error))
                    return InvalidResult(request, error!, evidenceReference, ordinal);
            }

            rows.Add(CreateRow(artifact, valueBuilder));
            lastSort = decodedSort;
            ordinal++;
        }

        ElasticRelationQueryArtifactContinuation? continuation = null;
        if (paging.Kind == ElasticRelationQueryPagingKind.SearchAfter
            && !lastSort.IsDefaultOrEmpty)
        {
            continuation = new(
                artifactFingerprint: artifact.Fingerprint,
                kind: ElasticRelationQueryPagingKind.SearchAfter,
                values: lastSort);
        }

        return new(
            request: request,
            status: RelationQueryExecutionStatus.Succeeded,
            rows: rows.MoveToImmutable(),
            diagnostics: [],
            continuation: continuation,
            providerEvidenceReference: evidenceReference);
    }

    static ElasticRelationQueryArtifactExecutionResult DecodeGlobalCount(
        PreparedArtifactInvocation invocation,
        SearchResponse<JsonElement> response,
        string? evidenceReference,
        CancellationToken cancellationToken)
    {
        var request = invocation.Request;
        var artifact = request.Artifact;
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Hits.Count != 0 || response.Aggregations is { Count: > 0 })
        {
            return InvalidResult(
                request,
                "An Elasticsearch exact-total-hits response unexpectedly contained hit or aggregation rows.",
                evidenceReference);
        }
        if (!TryReadExactTotalHits(response.HitsMetadata.Total, out var total))
        {
            return InvalidResult(
                request,
                "Elasticsearch did not return a non-negative exact total-hit count.",
                evidenceReference);
        }

        MutableObservationObject? valueBuilder = null;
        foreach (var field in artifact.ResultFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = field.SourceKind switch
            {
                ElasticRelationQueryResultSourceKind.Constant => field.Constant!.Value,
                ElasticRelationQueryResultSourceKind.ExactTotalHits => ObservationValue.FromInt64(total),
                _ => default
            };
            if (field.SourceKind is not (
                    ElasticRelationQueryResultSourceKind.Constant
                        or ElasticRelationQueryResultSourceKind.ExactTotalHits))
            {
                return InvalidResult(
                    request,
                    "An Elasticsearch global-count artifact used an incompatible result source.",
                    evidenceReference);
            }
            if (!TryAssignResultValue(ref valueBuilder, field, value, out var error))
                return InvalidResult(request, error!, evidenceReference);
        }

        return new(
            request: request,
            status: RelationQueryExecutionStatus.Succeeded,
            rows: [CreateRow(artifact, valueBuilder)],
            diagnostics: [],
            continuation: null,
            providerEvidenceReference: evidenceReference);
    }

    static ElasticRelationQueryArtifactExecutionResult DecodeCompositeCount(
        PreparedArtifactInvocation invocation,
        SearchResponse<JsonElement> response,
        string? evidenceReference,
        CancellationToken cancellationToken)
    {
        var request = invocation.Request;
        var artifact = request.Artifact;
        var paging = artifact.Paging!;
        var aggregationTemplate = artifact.RequestTemplate.Aggregation;
        if (response.Hits.Count != 0
            || response.Aggregations is null
            || response.Aggregations.Count != 1
            || !response.Aggregations.TryGetAggregate<CompositeAggregate>(
                aggregationTemplate.Name!,
                out var composite)
            || composite is null)
        {
            return InvalidResult(
                request,
                "Elasticsearch did not return the exact compiled composite aggregation.",
                evidenceReference);
        }

        var buckets = composite.Buckets;
        if (buckets.Count == 0 && composite.AfterKey is { Count: > 0 })
        {
            return InvalidResult(
                request,
                "An Elasticsearch composite response cannot return continuation evidence without a bucket row.",
                evidenceReference);
        }
        if (buckets.Count > invocation.MaximumRows || buckets.Count > paging.Limit)
        {
            return BoundaryFailure(
                request,
                buckets.Count,
                paging.Limit,
                evidenceReference);
        }

        var expectedKeys = aggregationTemplate.Sources
            .Select(static source => source.Name)
            .ToHashSet(StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<RelationQueryOutputRow>(buckets.Count);
        var ordinal = 0L;
        foreach (var bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bucket.DocCount < 0
                || bucket.Key.Count != expectedKeys.Count
                || bucket.Key.Keys.Any(key => !expectedKeys.Contains(key)))
            {
                return InvalidResult(
                    request,
                    "An Elasticsearch composite bucket has an invalid count or does not exactly cover every grouping key.",
                    evidenceReference,
                    ordinal);
            }

            MutableObservationObject? valueBuilder = null;
            foreach (var field in artifact.ResultFields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObservationValue value;
                switch (field.SourceKind)
                {
                    case ElasticRelationQueryResultSourceKind.Constant:
                        value = field.Constant!.Value;
                        break;
                    case ElasticRelationQueryResultSourceKind.CompositeDocumentCount:
                        value = ObservationValue.FromInt64(bucket.DocCount);
                        break;
                    case ElasticRelationQueryResultSourceKind.CompositeKey:
                        if (!bucket.Key.TryGetValue(field.PhysicalName!, out var physicalValue)
                            || !ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                                physicalValue,
                                field.ValueContract,
                                field.Encoding,
                                out value))
                        {
                            return InvalidResult(
                                request,
                                $"Elasticsearch composite key '{field.PhysicalName}' violates its retained value contract.",
                                evidenceReference,
                                ordinal);
                        }
                        break;
                    default:
                        return InvalidResult(
                            request,
                            "An Elasticsearch composite artifact used an incompatible result source.",
                            evidenceReference,
                            ordinal);
                }

                if (!TryAssignResultValue(ref valueBuilder, field, value, out var error))
                    return InvalidResult(request, error!, evidenceReference, ordinal);
            }

            rows.Add(CreateRow(artifact, valueBuilder));
            ordinal++;
        }

        ElasticRelationQueryArtifactContinuation? continuation = null;
        if (composite.AfterKey is { Count: > 0 })
        {
            if (!TryCreateCompositeContinuation(
                    artifact,
                    aggregationTemplate,
                    composite.AfterKey,
                    out continuation,
                    out var error))
            {
                return InvalidResult(request, error!, evidenceReference);
            }
        }

        return new(
            request: request,
            status: RelationQueryExecutionStatus.Succeeded,
            rows: rows.MoveToImmutable(),
            diagnostics: [],
            continuation: continuation,
            providerEvidenceReference: evidenceReference);
    }

    static bool TryReadSourceValue(
        JsonElement source,
        ElasticRelationQueryResultFieldBinding field,
        ImmutableArray<string> segments,
        out ObservationValue value,
        out string? error)
    {
        var current = source;
        for (var index = 0; index < segments.Length; index++)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                value = default;
                error = $"Elasticsearch source field '{field.PhysicalName}' traverses a non-object JSON value.";
                return false;
            }

            JsonElement next = default;
            var occurrences = 0;
            foreach (var property in current.EnumerateObject())
            {
                if (property.NameEquals(segments[index]))
                {
                    next = property.Value;
                    occurrences++;
                }
            }
            if (occurrences > 1)
            {
                value = default;
                error = $"Elasticsearch source field '{field.PhysicalName}' contains a repeated JSON property.";
                return false;
            }
            if (occurrences == 0 || index < segments.Length - 1 && next.ValueKind == JsonValueKind.Null)
            {
                value = ObservationValue.Undefined;
                error = null;
                return true;
            }

            current = next;
        }

        if (current.ValueKind == JsonValueKind.Null)
        {
            value = ObservationValue.Null;
            error = null;
            return true;
        }
        if (!ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                current,
                field.ValueContract,
                field.Encoding,
                out value))
        {
            error = $"Elasticsearch source field '{field.PhysicalName}' violates its retained physical encoding.";
            return false;
        }

        error = null;
        return true;
    }

    static bool TryAssignResultValue(
        ref MutableObservationObject? builder,
        ElasticRelationQueryResultFieldBinding field,
        ObservationValue value,
        out string? error)
    {
        if (!field.ValueContract.IsSatisfiedByConstant(value))
        {
            error = $"Elasticsearch result field '{field.Field.Path}' violates its retained semantic value contract.";
            return false;
        }
        if (value.Kind == ObservationValueKind.Undefined)
        {
            error = null;
            return true;
        }

        try
        {
            (builder ??= new(field.Field.Path.Segments.Length)).Set(field.Field.Path, value);
            error = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            error = $"Elasticsearch result field '{field.Field.Path}' could not be reconstructed ({exception.GetType().Name}).";
            return false;
        }
    }

    static RelationQueryOutputRow CreateRow(
        ElasticRelationQueryCompiledArtifact artifact,
        MutableObservationObject? valueBuilder) =>
        new(
            artifact.Branch.Shape,
            valueBuilder?.Freeze() ?? ObservationValue.EmptyObject,
            identity: null,
            root: null,
            inputOccurrences: [],
            unresolvedGaps: []);

    static bool TryDecodeHitSortTuple(
        ElasticRelationQueryPagingContract paging,
        IReadOnlyCollection<FieldValue> physicalValues,
        out ImmutableArray<ElasticRelationQueryContinuationValue> values,
        out string? error)
    {
        if (physicalValues.Count != paging.SortFields.Length)
        {
            values = [];
            error = "An Elasticsearch hit sort tuple does not match its compiled stable-sort arity.";
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<ElasticRelationQueryContinuationValue>(physicalValues.Count);
        using var enumerator = physicalValues.GetEnumerator();
        for (var index = 0; index < paging.SortFields.Length; index++)
        {
            if (!enumerator.MoveNext()
                || !ElasticRelationQueryResultValueEncodingSemantics.TryResolve(
                    paging.SortValueContracts[index],
                    out var encoding)
                || !ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                    enumerator.Current,
                    paging.SortValueContracts[index],
                    encoding,
                    out var value)
                || !paging.SortValueContracts[index].IsSatisfiedByConstant(value))
            {
                values = [];
                error = "An Elasticsearch hit sort tuple violates its retained semantic sort-key contract.";
                return false;
            }
            builder.Add(new(physicalField: paging.SortFields[index], value: value));
        }

        values = builder.MoveToImmutable();
        error = null;
        return true;
    }

    static bool TryCreateCompositeContinuation(
        ElasticRelationQueryCompiledArtifact artifact,
        ElasticAggregationTemplate aggregation,
        IReadOnlyDictionary<string, FieldValue> afterKey,
        out ElasticRelationQueryArtifactContinuation? continuation,
        out string? error)
    {
        var expectedKeys = aggregation.Sources
            .Select(static source => source.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!afterKey.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys))
        {
            continuation = null;
            error = "The Elasticsearch composite after-key does not exactly cover every compiled grouping source.";
            return false;
        }

        var keyBindings = artifact.ResultFields
            .Where(static field => field.SourceKind == ElasticRelationQueryResultSourceKind.CompositeKey)
            .ToDictionary(static field => field.PhysicalName!, StringComparer.Ordinal);
        var values = ImmutableArray.CreateBuilder<ElasticRelationQueryContinuationValue>(aggregation.Sources.Length);
        foreach (var source in aggregation.Sources)
        {
            var binding = keyBindings[source.Name];
            if (!afterKey.TryGetValue(source.Name, out var physicalValue)
                || !ElasticRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                    physicalValue,
                    binding.ValueContract,
                    binding.Encoding,
                    out var value)
                || !binding.ValueContract.IsSatisfiedByConstant(value)
                || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                continuation = null;
                error = $"Elasticsearch composite after-key '{source.Name}' violates its retained grouping contract.";
                return false;
            }

            values.Add(new(physicalField: source.Field, value: value));
        }

        continuation = new(
            artifactFingerprint: artifact.Fingerprint,
            kind: ElasticRelationQueryPagingKind.CompositeAfter,
            values: values.MoveToImmutable());
        error = null;
        return true;
    }

    static bool TryReadExactTotalHits(Union<TotalHits, long>? total, out long value)
    {
        if (total is null)
        {
            value = default;
            return false;
        }

        var exact = true;
        value = total.Match(
            hits =>
            {
                if (hits is null)
                {
                    exact = false;
                    return 0;
                }
                exact = hits.Relation == TotalHitsRelation.Eq;
                return hits.Value;
            },
            static count => count);
        return exact && value >= 0;
    }

    static ElasticRelationQueryArtifactExecutionResult BoundaryFailure(
        ElasticRelationQueryArtifactExecutionRequest request,
        long actualRows,
        long rowBoundary,
        string? evidenceReference) =>
        Failed(
            request,
            [Error(
                request: request,
                code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded,
                message: $"Elasticsearch returned {actualRows.ToString(CultureInfo.InvariantCulture)} rows across the exact execution boundary of {rowBoundary.ToString(CultureInfo.InvariantCulture)}.",
                evidenceReference: evidenceReference)],
            evidenceReference);

    static ElasticRelationQueryArtifactExecutionResult InvalidResult(
        ElasticRelationQueryArtifactExecutionRequest request,
        string message,
        string? evidenceReference,
        long? rowOrdinal = null) =>
        Failed(
            request,
            [Error(
                request: request,
                code: ElasticRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid,
                message: message,
                rowOrdinal: rowOrdinal,
                evidenceReference: evidenceReference)],
            evidenceReference);

    static string? ProviderEvidenceReference(SearchResponse<JsonElement> response) =>
        response.ApiCallDetails?.HttpStatusCode is { } statusCode
            ? $"elasticsearch-search/status/{statusCode.ToString(CultureInfo.InvariantCulture)}"
            : null;

    static bool IsFieldOnlyPath(FieldPath path) =>
        !path.Segments.IsDefaultOrEmpty
        && path.Segments.All(static segment =>
            segment.Kind == SegmentKind.Field && !string.IsNullOrEmpty(segment.Segment));

    static ImmutableArray<ImmutableArray<string>> PrepareResultSourcePaths(
        ElasticRelationQueryCompiledArtifact artifact)
    {
        var paths = ImmutableArray.CreateBuilder<ImmutableArray<string>>(artifact.ResultFields.Length);
        foreach (var field in artifact.ResultFields)
        {
            paths.Add(field.SourceKind == ElasticRelationQueryResultSourceKind.SourceField
                ? [.. field.PhysicalName!.Split('.', StringSplitOptions.None)]
                : []);
        }
        return paths.MoveToImmutable();
    }

    static bool HasPathPrefixConflict(ImmutableArray<ElasticRelationQueryResultFieldBinding> fields)
    {
        for (var leftIndex = 0; leftIndex < fields.Length; leftIndex++)
        {
            var left = fields[leftIndex].Field.Path.Segments;
            for (var rightIndex = leftIndex + 1; rightIndex < fields.Length; rightIndex++)
            {
                var right = fields[rightIndex].Field.Path.Segments;
                var prefixLength = Math.Min(left.Length, right.Length);
                if (left.AsSpan(0, prefixLength).SequenceEqual(right.AsSpan(0, prefixLength)))
                    return true;
            }
        }
        return false;
    }

    static bool IsRecoverable(Exception exception) => exception is not (
        OperationCanceledException
        or OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);

    static string ExceptionTypeChain(Exception exception)
    {
        StringBuilder builder = new(exception.GetType().Name);
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            builder.Append(" -> ");
            builder.Append(inner.GetType().Name);
        }
        return builder.ToString();
    }

    static ElasticRelationQueryArtifactExecutionDiagnostic Error(
        ElasticRelationQueryArtifactExecutionRequest request,
        string code,
        string message,
        long? rowOrdinal = null,
        string? evidenceReference = null) =>
        new(
            code: code,
            severity: DiagnosticSeverity.Error,
            message: message,
            branch: request.Artifact.Branch.Id,
            rowOrdinal: rowOrdinal,
            evidenceReference: evidenceReference);

    static ElasticRelationQueryArtifactExecutionResult Failed(
        ElasticRelationQueryArtifactExecutionRequest request,
        ImmutableArray<ElasticRelationQueryArtifactExecutionDiagnostic> diagnostics,
        string? providerEvidenceReference = null) =>
        new(
            request: request,
            status: RelationQueryExecutionStatus.Failed,
            rows: [],
            diagnostics: diagnostics,
            continuation: null,
            providerEvidenceReference: providerEvidenceReference);

    sealed record PreparedArtifactInvocation(
        ElasticRelationQueryArtifactExecutionRequest Request,
        SearchRequest SearchRequest,
        long MaximumRows,
        ImmutableArray<ImmutableArray<string>> ResultSourcePaths);

    sealed class MutableObservationObject
    {
        readonly Dictionary<string, Member> members;

        public MutableObservationObject(int capacity = 0) =>
            members = new(capacity, StringComparer.Ordinal);

        public void Set(FieldPath path, ObservationValue value)
        {
            if (path.Segments.IsDefaultOrEmpty)
                throw new ArgumentException("An object assignment requires a non-empty field path.", nameof(path));
            Set(path, segmentIndex: 0, value);
        }

        public ObservationValue Freeze()
        {
            if (members.Count == 0)
                return ObservationValue.EmptyObject;
            var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (var (name, member) in members)
            {
                fields.Add(name, member.Object is null ? member.Value : member.Object.Freeze());
            }
            return ObservationValue.FromObject(fields.ToImmutable());
        }

        void Set(FieldPath path, int segmentIndex, ObservationValue value)
        {
            var segment = path.Segments[segmentIndex];
            if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
                throw new NotSupportedException($"Observation assignment does not support path '{path}'.");

            var name = segment.Segment;
            if (segmentIndex == path.Segments.Length - 1)
            {
                if (members.TryGetValue(name, out var existing) && existing.Object is not null)
                    throw new InvalidOperationException($"Field path '{path}' cannot replace a reconstructed object.");
                members[name] = new(value, Object: null);
                return;
            }

            MutableObservationObject child;
            if (members.TryGetValue(name, out var member))
            {
                child = member.Object
                    ?? throw new InvalidOperationException($"Field path '{path}' cannot traverse a scalar value.");
            }
            else
            {
                child = new();
                members.Add(name, new(default, child));
            }
            child.Set(path, segmentIndex + 1, value);
        }

        readonly record struct Member(ObservationValue Value, MutableObservationObject? Object);
    }
}

/// <summary>Stable Elasticsearch compiled-artifact execution diagnostic codes.</summary>
public static class ElasticRelationQueryArtifactExecutionDiagnosticCodes
{
    /// <summary>An invocation affinity fact or runtime attestation conflicts with the compiled artifact.</summary>
    public const string ArtifactAffinityInvalid = "REL2290";

    /// <summary>Canonical invocation parameters cannot be bound to the compiled request template.</summary>
    public const string InvocationInvalid = "REL2291";

    /// <summary>The Elasticsearch SDK or service failed before a complete response was available.</summary>
    public const string ProviderFailure = "REL2292";

    /// <summary>A compiled or returned row count exceeds the explicit execution boundary.</summary>
    public const string ResultBoundaryExceeded = "REL2293";

    /// <summary>An Elasticsearch response conflicts with retained physical or semantic result metadata.</summary>
    public const string ResultInvalid = "REL2294";
}

/// <summary>
/// Exact canonical scalar closure shared by Elasticsearch artifact affinity validation and response decoding.
/// </summary>
internal static class ElasticRelationQueryCanonicalValueCodec
{
    internal static bool TryDecodeResultValue(
        JsonElement element,
        ValueContract contract,
        ElasticRelationQueryResultValueEncoding encoding,
        out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(contract);
        switch (encoding)
        {
            case ElasticRelationQueryResultValueEncoding.JsonBoolean
                when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = ObservationValue.FromBool(element.GetBoolean());
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonInt64
                when TryDecodeExactInteger(element, long.MinValue, long.MaxValue, out var integer):
                value = ObservationValue.FromInt64(integer);
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonDouble
                when element.ValueKind == JsonValueKind.Number
                     && element.TryGetDouble(out var floating)
                     && double.IsFinite(floating):
                value = ObservationValue.FromDouble(floating);
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonString
                or ElasticRelationQueryResultValueEncoding.CanonicalTemporalString
                when element.ValueKind == JsonValueKind.String:
                value = ObservationValue.FromString(element.GetString());
                return true;
            case ElasticRelationQueryResultValueEncoding.ExactCountInt64
                when TryDecodeExactInteger(element, minimum: 0, long.MaxValue, out var count):
                value = ObservationValue.FromInt64(count);
                return true;
            default:
                value = default;
                return false;
        }
    }

    internal static bool TryDecodeResultValue(
        FieldValue physicalValue,
        ValueContract contract,
        ElasticRelationQueryResultValueEncoding encoding,
        out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(contract);
        switch (encoding)
        {
            case ElasticRelationQueryResultValueEncoding.JsonBoolean
                when physicalValue.TryGetBool(out var boolean) && boolean is { } exactBoolean:
                value = ObservationValue.FromBool(exactBoolean);
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonInt64
                when physicalValue.TryGetLong(out var integer) && integer is { } exactInteger:
                value = ObservationValue.FromInt64(exactInteger);
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonDouble
                when physicalValue.TryGetDouble(out var floating)
                     && floating is { } exactFloating
                     && double.IsFinite(exactFloating):
                value = ObservationValue.FromDouble(exactFloating);
                return true;
            case ElasticRelationQueryResultValueEncoding.JsonString
                or ElasticRelationQueryResultValueEncoding.CanonicalTemporalString
                when physicalValue.TryGetString(out var text) && text is not null:
                value = ObservationValue.FromString(text);
                return true;
            case ElasticRelationQueryResultValueEncoding.ExactCountInt64
                when physicalValue.TryGetLong(out var count) && count is >= 0:
                value = ObservationValue.FromInt64(count.Value);
                return true;
            default:
                value = default;
                return false;
        }
    }

    static bool TryDecodeExactInteger(
        JsonElement element,
        long minimum,
        long maximum,
        out long value)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            value = default;
            return false;
        }
        if (element.TryGetInt64(out var integer))
        {
            value = integer;
            return integer >= minimum && integer <= maximum;
        }
        if (TryParseExactJsonInteger(element.GetRawText().AsSpan(), out var parsed)
            && parsed >= minimum
            && parsed <= maximum)
        {
            value = parsed;
            return true;
        }

        value = default;
        return false;
    }

    static bool TryParseExactJsonInteger(ReadOnlySpan<char> token, out long value)
    {
        var index = 0;
        var negative = token.Length != 0 && token[0] == '-';
        if (negative)
            index++;

        var integerStart = index;
        while (index < token.Length && token[index] is >= '0' and <= '9')
            index++;
        var integerLength = index - integerStart;
        if (integerLength == 0)
            return Fail(out value);

        var fractionStart = index;
        var fractionLength = 0;
        if (index < token.Length && token[index] == '.')
        {
            index++;
            fractionStart = index;
            while (index < token.Length && token[index] is >= '0' and <= '9')
                index++;
            fractionLength = index - fractionStart;
            if (fractionLength == 0)
                return Fail(out value);
        }

        long exponent = 0;
        if (index < token.Length && token[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = index < token.Length && token[index] == '-';
            if (index < token.Length && token[index] is '+' or '-')
                index++;
            var exponentStart = index;
            var exponentLimit = (long)token.Length + 20;
            while (index < token.Length && token[index] is >= '0' and <= '9')
            {
                exponent = Math.Min(exponentLimit, (exponent * 10) + (token[index] - '0'));
                index++;
            }
            if (index == exponentStart)
                return Fail(out value);
            if (exponentNegative)
                exponent = -exponent;
        }
        if (index != token.Length)
            return Fail(out value);

        var totalDigits = integerLength + fractionLength;
        var firstNonZero = -1;
        for (var digitIndex = 0; digitIndex < totalDigits; digitIndex++)
        {
            if (GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) != '0')
            {
                firstNonZero = digitIndex;
                break;
            }
        }
        if (firstNonZero < 0)
        {
            value = 0;
            return true;
        }

        var scale = exponent - fractionLength;
        var removedDigits = scale < 0 ? -scale : 0;
        if (removedDigits > totalDigits)
            return Fail(out value);
        for (long removed = 0; removed < removedDigits; removed++)
        {
            var digitIndex = totalDigits - 1 - (int)removed;
            if (GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) != '0')
                return Fail(out value);
        }

        var retainedDigits = totalDigits - (int)removedDigits;
        if (firstNonZero >= retainedDigits)
            return Fail(out value);
        var appendedZeros = scale > 0 ? scale : 0;
        if ((long)retainedDigits - firstNonZero + appendedZeros > 19)
            return Fail(out value);

        var magnitudeLimit = negative ? 9_223_372_036_854_775_808UL : long.MaxValue;
        ulong magnitude = 0;
        for (var digitIndex = firstNonZero; digitIndex < retainedDigits; digitIndex++)
        {
            var digit = (uint)(GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) - '0');
            if (magnitude > (magnitudeLimit - digit) / 10)
                return Fail(out value);
            magnitude = (magnitude * 10) + digit;
        }
        for (long appended = 0; appended < appendedZeros; appended++)
        {
            if (magnitude > magnitudeLimit / 10)
                return Fail(out value);
            magnitude *= 10;
        }

        value = negative
            ? magnitude == 9_223_372_036_854_775_808UL
                ? long.MinValue
                : -(long)magnitude
            : (long)magnitude;
        return true;
    }

    static char GetDigit(
        ReadOnlySpan<char> token,
        int integerStart,
        int integerLength,
        int fractionStart,
        int digitIndex) => digitIndex < integerLength
        ? token[integerStart + digitIndex]
        : token[fractionStart + digitIndex - integerLength];

    static bool Fail<T>(out T value)
    {
        value = default!;
        return false;
    }
}
