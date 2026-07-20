using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Physical buffering options for canonical Cosmos compiled-artifact execution.</summary>
public sealed record CosmosRelationQueryArtifactExecutionOptions
{
    /// <summary>Conventional maximum number of rows requested from one SDK page.</summary>
    public const int DefaultMaximumPageSize = 256;

    /// <summary>Conventional maximum number of result rows retained by one invocation.</summary>
    public const long DefaultMaximumBufferedRows = 10_000;

    /// <summary>Creates physical execution options.</summary>
    /// <param name="maximumPageSize">Positive maximum number of rows requested from one SDK page.</param>
    /// <param name="maximumBufferedRows">Positive maximum number of result rows retained by one invocation.</param>
    /// <param name="requestSizeLimits">
    /// Explicit pre-I/O SQL-text and complete-request size boundaries, or <see langword="null"/> for Cosmos
    /// conventions.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumPageSize"/> or <paramref name="maximumBufferedRows"/> is not positive, or
    /// <paramref name="maximumBufferedRows"/> exceeds the maximum immutable-array length.
    /// </exception>
    public CosmosRelationQueryArtifactExecutionOptions(
        int maximumPageSize = DefaultMaximumPageSize,
        long maximumBufferedRows = DefaultMaximumBufferedRows,
        CosmosQueryRequestSizeLimits? requestSizeLimits = null)
    {
        if (maximumPageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageSize),
                maximumPageSize,
                "A Cosmos query page-size boundary must be positive.");
        }
        if (maximumBufferedRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBufferedRows),
                maximumBufferedRows,
                "A Cosmos query buffered-row boundary must be positive.");
        }
        if (maximumBufferedRows > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBufferedRows),
                maximumBufferedRows,
                $"A Cosmos query buffered-row boundary cannot exceed {Array.MaxLength.ToString(CultureInfo.InvariantCulture)} rows.");
        }

        MaximumPageSize = maximumPageSize;
        MaximumBufferedRows = maximumBufferedRows;
        RequestSizeLimits = requestSizeLimits ?? new();
    }

    /// <summary>Maximum number of rows requested from one SDK page.</summary>
    public int MaximumPageSize { get; }

    /// <summary>Maximum number of result rows retained by one invocation.</summary>
    public long MaximumBufferedRows { get; }

    /// <summary>Pre-I/O SQL-text and conservative complete-request size boundaries.</summary>
    public CosmosQueryRequestSizeLimits RequestSizeLimits { get; }
}

/// <summary>
/// Exact physical invocation of one canonical Cosmos compiled artifact and all affinity facts needed to reject
/// stale or misplaced execution.
/// </summary>
public sealed class CosmosRelationQueryArtifactExecutionRequest
{
    /// <summary>Creates an exact compiled-artifact invocation.</summary>
    /// <param name="plan">Exact demand-scoped compiled-plan reference expected by the artifact.</param>
    /// <param name="realization">Exact realization fingerprint expected by the artifact.</param>
    /// <param name="placement">Exact source-placement fingerprint expected by the artifact.</param>
    /// <param name="storageBindingFingerprint">Exact Cosmos storage-binding fingerprint expected by the artifact.</param>
    /// <param name="artifact">Canonical Cosmos compiled artifact to execute.</param>
    /// <param name="maximumRows">
    /// Positive invocation-specific result boundary, further constrained by executor and paging limits.
    /// </param>
    /// <param name="parameters">Invocation values keyed by canonical parameter identity.</param>
    /// <exception cref="ArgumentNullException">A reference parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumRows"/> is not positive.</exception>
    public CosmosRelationQueryArtifactExecutionRequest(
        RelationQueryCompiledPlanReference plan,
        RelationQueryRealizationFingerprint realization,
        RelationQuerySourcePlacementFingerprint placement,
        CosmosRelationQueryBindingFingerprint storageBindingFingerprint,
        CosmosRelationQueryCompiledArtifact artifact,
        long maximumRows,
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        Plan = Guard.RequireNotNull(plan);
        Realization = Guard.RequireNotNull(realization);
        Placement = Guard.RequireNotNull(placement);
        StorageBindingFingerprint = Guard.RequireNotNull(storageBindingFingerprint);
        Artifact = Guard.RequireNotNull(artifact);
        if (maximumRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                "A canonical Cosmos artifact invocation requires a positive result-row boundary.");
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

    /// <summary>Exact Cosmos storage-binding fingerprint expected by the artifact.</summary>
    public CosmosRelationQueryBindingFingerprint StorageBindingFingerprint { get; }

    /// <summary>Canonical Cosmos compiled artifact to execute.</summary>
    public CosmosRelationQueryCompiledArtifact Artifact { get; }

    /// <summary>Positive invocation-specific result-row boundary.</summary>
    public long MaximumRows { get; }

    /// <summary>Immutable invocation values keyed by canonical parameter identity.</summary>
    public ImmutableDictionary<QueryParameterId, ObservationValue> Parameters { get; }
}

/// <summary>One structured failure or warning emitted while executing a canonical Cosmos artifact.</summary>
public sealed record CosmosRelationQueryArtifactExecutionDiagnostic
{
    /// <summary>Creates an attributable artifact-execution diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable non-sensitive explanation.</param>
    /// <param name="branch">Affected native result branch.</param>
    /// <param name="rowOrdinal">Zero-based provider row ordinal, or <see langword="null"/>.</param>
    /// <param name="evidenceReference">Opaque provider evidence reference, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required string or branch identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="severity"/> is unsupported or <paramref name="rowOrdinal"/> is negative.
    /// </exception>
    public CosmosRelationQueryArtifactExecutionDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryNativeResultBranchId branch,
        long? rowOrdinal = null,
        string? evidenceReference = null
        )
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

    /// <summary>Zero-based provider row ordinal, or <see langword="null"/>.</summary>
    public long? RowOrdinal { get; }

    /// <summary>Opaque provider evidence reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Immutable result of executing one canonical Cosmos compiled artifact.</summary>
public sealed class CosmosRelationQueryArtifactExecutionResult
{
    static readonly IComparer<CosmosRelationQueryArtifactExecutionDiagnostic> DiagnosticOrdering =
        Comparer<CosmosRelationQueryArtifactExecutionDiagnostic>.Create(
            static (left, right) => CompareDiagnostics(left, right));

    internal CosmosRelationQueryArtifactExecutionResult(
        CosmosRelationQueryArtifactExecutionRequest request,
        RelationQueryExecutionStatus status,
        ImmutableArray<RelationQueryOutputRow> rows,
        ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> diagnostics,
        string? providerEvidenceReference
        )
    {
        Request = Guard.RequireNotNull(request);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported Cosmos artifact execution status.");

        var normalizedRows = rows.IsDefault ? [] : rows;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        var containsNullRow = false;
        var containsMismatchedShape = false;
        for (var index = 0; index < normalizedRows.Length; index++)
        {
            var row = normalizedRows[index];
            if (row is null)
            {
                containsNullRow = true;
                continue;
            }

            if (row.Shape != request.Artifact.Branch.Shape)
                containsMismatchedShape = true;
        }

        var containsNullDiagnostic = false;
        var containsMismatchedBranch = false;
        var containsError = false;
        var containsWarning = false;
        var diagnosticsAreCanonical = true;
        CosmosRelationQueryArtifactExecutionDiagnostic? previousDiagnostic = null;
        for (var index = 0; index < normalizedDiagnostics.Length; index++)
        {
            var diagnostic = normalizedDiagnostics[index];
            if (diagnostic is null)
            {
                containsNullDiagnostic = true;
                continue;
            }

            if (diagnostic.Branch != request.Artifact.Branch.Id)
                containsMismatchedBranch = true;
            containsError |= diagnostic.Severity == DiagnosticSeverity.Error;
            containsWarning |= diagnostic.Severity == DiagnosticSeverity.Warning;
            if (previousDiagnostic is not null
                && DiagnosticOrdering.Compare(previousDiagnostic, diagnostic) > 0)
            {
                diagnosticsAreCanonical = false;
            }
            previousDiagnostic = diagnostic;
        }

        if (containsNullRow)
            throw new ArgumentException("Execution rows cannot contain null entries.", nameof(rows));
        if (containsMismatchedShape)
            throw new ArgumentException("Execution rows must match the artifact branch shape.", nameof(rows));
        if (containsNullDiagnostic)
            throw new ArgumentException("Execution diagnostics cannot contain null entries.", nameof(diagnostics));
        if (containsMismatchedBranch)
            throw new ArgumentException("Execution diagnostics must identify the artifact branch.", nameof(diagnostics));
        if (status == RelationQueryExecutionStatus.Succeeded && containsError)
        {
            throw new ArgumentException("Successful execution cannot contain error diagnostics.", nameof(diagnostics));
        }
        if (status == RelationQueryExecutionStatus.Failed && !normalizedRows.IsDefaultOrEmpty)
            throw new ArgumentException("Failed execution cannot expose untrustworthy partial rows.", nameof(rows));
        if (status == RelationQueryExecutionStatus.Failed && !containsError)
        {
            throw new ArgumentException("Failed execution requires an error diagnostic.", nameof(diagnostics));
        }
        if (status == RelationQueryExecutionStatus.Incomplete
            && (normalizedRows.IsDefaultOrEmpty
                || containsError
                || !containsWarning))
        {
            throw new ArgumentException(
                "Incomplete execution requires attributable prefix rows and a warning diagnostic.",
                nameof(status));
        }
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
            throw new ArgumentException("A provider evidence reference cannot be empty.", nameof(providerEvidenceReference));

        Status = status;
        Rows = normalizedRows;
        Diagnostics = normalizedDiagnostics.IsDefaultOrEmpty || diagnosticsAreCanonical
            ? normalizedDiagnostics
            : [.. normalizedDiagnostics.Order(DiagnosticOrdering)];
        ProviderEvidenceReference = providerEvidenceReference;
    }

    static int CompareDiagnostics(
        CosmosRelationQueryArtifactExecutionDiagnostic left,
        CosmosRelationQueryArtifactExecutionDiagnostic right)
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

    /// <summary>Exact invocation represented by this result.</summary>
    public CosmosRelationQueryArtifactExecutionRequest Request { get; }

    /// <summary>Whether execution succeeded, returned an attributable bounded prefix, or failed.</summary>
    public RelationQueryExecutionStatus Status { get; }

    /// <summary>Canonical shaped rows in Cosmos provider order.</summary>
    public ImmutableArray<RelationQueryOutputRow> Rows { get; }

    /// <summary>Structured deterministic execution diagnostics.</summary>
    public ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> Diagnostics { get; }

    /// <summary>Opaque non-sensitive provider feed evidence reference, when available.</summary>
    public string? ProviderEvidenceReference { get; }

    /// <summary>Canonical terminal branch represented by <see cref="Rows"/>.</summary>
    public RelationQueryNativeResultBranch Branch => Request.Artifact.Branch;

    /// <summary>Whether execution completed conclusively without an error diagnostic.</summary>
    public bool IsSuccessful => Status == RelationQueryExecutionStatus.Succeeded;
}

/// <summary>Executes affinity-validated canonical Cosmos compiled artifacts through the Cosmos SDK.</summary>
public sealed class CosmosRelationQueryArtifactExecutor
{
    readonly CosmosJsonQueryFeedReader feedReader;
    readonly CosmosRelationQueryArtifactExecutionOptions options;

    /// <summary>Creates an artifact executor for one Cosmos container.</summary>
    /// <param name="container">Container whose identity must match every artifact storage binding.</param>
    /// <param name="options">Physical buffering options, or <see langword="null"/> for conventions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is <see langword="null"/>.</exception>
    public CosmosRelationQueryArtifactExecutor(
        Container container,
        CosmosRelationQueryArtifactExecutionOptions? options = null)
        : this(new CosmosJsonQueryFeedReader(container), options)
    {
    }

    internal CosmosRelationQueryArtifactExecutor(
        CosmosJsonQueryFeedReader feedReader,
        CosmosRelationQueryArtifactExecutionOptions? options = null)
    {
        this.feedReader = Guard.RequireNotNull(feedReader);
        this.options = options ?? new();
    }

    /// <summary>Executes one exact canonical Cosmos artifact invocation.</summary>
    /// <param name="request">Artifact and exact affinity and invocation facts to validate before SDK I/O.</param>
    /// <param name="cancellationToken">Token observed before validation and throughout SDK enumeration.</param>
    /// <returns>
    /// A conclusive shaped result, an attributable incomplete prefix at a declared row boundary, or a structured
    /// failed result. Affinity, invocation, provider, boundary, and physical-result outcomes are returned as
    /// diagnostics rather than thrown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public ValueTask<CosmosRelationQueryArtifactExecutionResult> ExecuteAsync(
        CosmosRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return CosmosRelationQueryTelemetry.Emitter.IsEnabled
            ? ExecuteObservedAsync(request, cancellationToken)
            : ExecuteCoreAsync(request, cancellationToken);
    }

    async ValueTask<CosmosRelationQueryArtifactExecutionResult> ExecuteCoreAsync(
        CosmosRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return TryPrepare(request, out var prepared, out var failure)
            ? await ExecutePreparedAsync(prepared!, cancellationToken).ConfigureAwait(false)
            : failure!;
    }

    /// <summary>
    /// Executes multiple exact artifact invocations sequentially, preserving caller order and branch attribution.
    /// </summary>
    /// <param name="requests">
    /// Row, aggregation, or relation branch invocations to execute in deterministic caller order.
    /// </param>
    /// <param name="cancellationToken">Token observed before and throughout every invocation.</param>
    /// <returns>One independently attributed execution result per request in caller order.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="requests"/> is <see langword="null"/> or contains a <see langword="null"/> request.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="requests"/> repeats a native branch identity.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public ValueTask<ImmutableArray<CosmosRelationQueryArtifactExecutionResult>> ExecuteAsync(
        IReadOnlyList<CosmosRelationQueryArtifactExecutionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var normalizedRequests = new CosmosRelationQueryArtifactExecutionRequest[requests.Count];
        HashSet<RelationQueryNativeResultBranchId> branchIds = new(requests.Count);
        var containsDuplicateBranch = false;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index]
                ?? throw new ArgumentNullException(nameof(requests), "Artifact execution requests cannot contain null entries.");
            normalizedRequests[index] = request;
            containsDuplicateBranch |= !branchIds.Add(request.Artifact.Branch.Id);
        }
        if (containsDuplicateBranch)
        {
            throw new ArgumentException("A Cosmos artifact execution batch cannot repeat a native branch identity.", nameof(requests));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CosmosRelationQueryTelemetry.Emitter.IsEnabled
            ? ExecuteObservedAsync(normalizedRequests, cancellationToken)
            : ExecuteBatchCoreAsync(normalizedRequests, cancellationToken);
    }

    async ValueTask<ImmutableArray<CosmosRelationQueryArtifactExecutionResult>> ExecuteBatchCoreAsync(
        CosmosRelationQueryArtifactExecutionRequest[] normalizedRequests,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedArtifactInvocation?[normalizedRequests.Length];
        var failures = new CosmosRelationQueryArtifactExecutionResult?[normalizedRequests.Length];
        var preflightFailed = false;
        for (var index = 0; index < normalizedRequests.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPrepare(normalizedRequests[index], out prepared[index], out failures[index]))
                preflightFailed = true;
        }

        if (preflightFailed)
        {
            var rejected = ImmutableArray.CreateBuilder<CosmosRelationQueryArtifactExecutionResult>(normalizedRequests.Length);
            for (var index = 0; index < normalizedRequests.Length; index++)
            {
                rejected.Add(failures[index] ?? Failed(
                    normalizedRequests[index],
                    [Error(
                        normalizedRequests[index],
                        CosmosRelationQueryArtifactExecutionDiagnosticCodes.BatchPreflightFailed,
                        "The combined Cosmos invocation was rejected because another branch failed preflight validation.")]));
            }
            return rejected.MoveToImmutable();
        }

        var results = ImmutableArray.CreateBuilder<CosmosRelationQueryArtifactExecutionResult>(normalizedRequests.Length);
        foreach (var invocation in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecutePreparedAsync(invocation!, cancellationToken).ConfigureAwait(false));
        }
        return results.MoveToImmutable();
    }

    async ValueTask<CosmosRelationQueryArtifactExecutionResult> ExecuteObservedAsync(
        CosmosRelationQueryArtifactExecutionRequest request,
        CancellationToken cancellationToken)
    {
        Activity? activity = CosmosRelationQueryTelemetry.Emitter.StartActivity(
            RelationQueryTelemetry.NativeExecutionActivityName,
            ActivityKind.Client);
        var started = CosmosRelationQueryTelemetry.Emitter.StartTimer();
        try
        {
            CosmosRelationQueryArtifactExecutionResult result;
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
            CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.NativeExecutionActivityName,
                RelationQueryTelemetry.GetStatusTagValue(result.Status));
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            CompleteExecutionException(activity, started, exception, RelationQueryTelemetry.CanceledStatus);
            throw;
        }
        catch (Exception exception)
        {
            CompleteExecutionException(activity, started, exception, RelationQueryTelemetry.ExceptionStatus);
            throw;
        }
    }

    async ValueTask<ImmutableArray<CosmosRelationQueryArtifactExecutionResult>> ExecuteObservedAsync(
        CosmosRelationQueryArtifactExecutionRequest[] requests,
        CancellationToken cancellationToken)
    {
        Activity? activity = CosmosRelationQueryTelemetry.Emitter.StartActivity(
            RelationQueryTelemetry.NativeExecutionActivityName,
            ActivityKind.Client);
        var started = CosmosRelationQueryTelemetry.Emitter.StartTimer();
        if (activity?.IsAllDataRequested == true)
            activity.SetTag(RelationQueryTelemetry.ArtifactCountTagName, requests.Length);
        try
        {
            var results = await ExecuteBatchCoreAsync(requests, cancellationToken).ConfigureAwait(false);
            var status = RelationQueryExecutionStatus.Succeeded;
            var rowCount = 0;
            var diagnosticCount = 0;
            foreach (var result in results)
            {
                if (result.Status == RelationQueryExecutionStatus.Failed)
                    status = RelationQueryExecutionStatus.Failed;
                else if (result.Status == RelationQueryExecutionStatus.Incomplete
                         && status == RelationQueryExecutionStatus.Succeeded)
                    status = RelationQueryExecutionStatus.Incomplete;
                rowCount += result.Rows.Length;
                diagnosticCount += result.Diagnostics.Length;
                if (activity?.IsAllDataRequested == true)
                {
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        RelationQueryTelemetry.AddDiagnosticEvent(
                            activity,
                            diagnostic.Code,
                            diagnostic.Severity);
                    }
                }
            }
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag(RelationQueryTelemetry.RowCountTagName, rowCount);
                activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, diagnosticCount);
            }
            CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.NativeExecutionActivityName,
                RelationQueryTelemetry.GetStatusTagValue(status));
            return results;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            CompleteExecutionException(activity, started, exception, RelationQueryTelemetry.CanceledStatus);
            throw;
        }
        catch (Exception exception)
        {
            CompleteExecutionException(activity, started, exception, RelationQueryTelemetry.ExceptionStatus);
            throw;
        }
    }

    static void SetExecutionRequestTags(
        Activity? activity,
        CosmosRelationQueryArtifactExecutionRequest request)
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
        CosmosRelationQueryArtifactExecutionResult result)
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

    static void CompleteExecutionException(
        Activity? activity,
        long started,
        Exception exception,
        string status) => CosmosRelationQueryTelemetry.Emitter.CompleteOperation(
            activity,
            started,
            RelationQueryTelemetry.NativeExecutionActivityName,
            status,
            exception: exception);

    bool TryPrepare(CosmosRelationQueryArtifactExecutionRequest request, out PreparedArtifactInvocation? prepared, out CosmosRelationQueryArtifactExecutionResult? failure)
    {
        var affinityDiagnostics = ValidateAffinity(request);
        if (!affinityDiagnostics.IsDefaultOrEmpty)
        {
            prepared = null;
            failure = Failed(request, affinityDiagnostics);
            return false;
        }

        try
        {
            var query = request.Artifact.Bind(request.Parameters).ToQueryDefinition();
            var maximumRows = ResolveMaximumRows(request);
            QueryRequestOptions requestOptions = new()
            {
                MaxItemCount = (int)Math.Min(options.MaximumPageSize, maximumRows),
                MaxBufferedItemCount = (int)maximumRows
            };
            prepared = new(
                request,
                feedReader.Prepare(query, requestOptions, options.RequestSizeLimits),
                maximumRows);
            failure = null;
            return true;
        }
        catch (CosmosQueryRequestSizeLimitException exception)
        {
            prepared = null;
            var evidence = $"cosmos-request-boundary/{Uri.EscapeDataString(exception.Reason)}";
            failure = Failed(
                request,
                [Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.RequestSizePreflightFailed,
                    "The bound canonical Cosmos invocation failed explicit pre-I/O request-size validation.",
                    evidenceReference: evidence)],
                evidence);
            return false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            prepared = null;
            failure = Failed(
                request,
                [Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.InvocationInvalid,
                    $"The canonical Cosmos invocation could not be bound: {exception.Message}")]);
            return false;
        }
    }

    async ValueTask<CosmosRelationQueryArtifactExecutionResult> ExecutePreparedAsync(
        PreparedArtifactInvocation invocation,
        CancellationToken cancellationToken)
    {
        var request = invocation.Request;
        CosmosJsonQueryFeedReadResult feed;
        try
        {
            feed = await feedReader.ReadAllAsync(
                invocation.FeedRequest,
                invocation.MaximumRows,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Failed(
                request,
                [Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    $"The Cosmos SDK reported cancellation without invocation cancellation ({exception.GetType().Name}).")]);
        }
        catch (CosmosException exception)
        {
            var evidence = FormattableString.Invariant(
                $"cosmos-provider/status/{(int)exception.StatusCode}/substatus/{exception.SubStatusCode}");
            return Failed(
                request,
                [Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    FormattableString.Invariant(
                        $"Cosmos query execution failed with HTTP status {(int)exception.StatusCode} and substatus {exception.SubStatusCode}."),
                    evidenceReference: evidence)],
                evidence);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed(
                request,
                [Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.ProviderFailure,
                    $"Cosmos SDK query execution failed before a complete result was available ({exception.GetType().Name}).")]);
        }

        if (feed.BoundaryStopped)
        {
            var (prefixRows, prefixDiagnostics) = DecodeRows(request, feed.Rows, cancellationToken);
            if (!prefixDiagnostics.IsDefaultOrEmpty)
                return Failed(request, prefixDiagnostics, feed.ProviderEvidenceReference);

            return new(
                request,
                RelationQueryExecutionStatus.Incomplete,
                prefixRows,
                [Warning(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultBoundaryExceeded,
                    $"Cosmos result rows exceeded the configured execution boundary of {invocation.MaximumRows.ToString(CultureInfo.InvariantCulture)}; the returned rows are an attributable provider-order prefix.",
                    evidenceReference: feed.ProviderEvidenceReference)],
                feed.ProviderEvidenceReference);
        }

        var (rows, resultDiagnostics) = DecodeRows(request, feed.Rows, cancellationToken);
        if (!resultDiagnostics.IsDefaultOrEmpty)
            return Failed(request, resultDiagnostics, feed.ProviderEvidenceReference);

        return new(
            request,
            RelationQueryExecutionStatus.Succeeded,
            rows,
            [],
            feed.ProviderEvidenceReference);
    }

    ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> ValidateAffinity(CosmosRelationQueryArtifactExecutionRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<CosmosRelationQueryArtifactExecutionDiagnostic>();
        var artifact = request.Artifact;
        var binding = artifact.StorageBinding;
        var provenance = artifact.Provenance;

        try
        {
            var requestPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.Plan);
            var artifactPlan = RelationQueryCompiledPlanReferenceFingerprinter.Compute(provenance.Plan);
            Require(requestPlan == artifactPlan, "The invocation compiled-plan reference does not match artifact provenance.");
            Require(
                binding.CompiledPlanFingerprint is not null && binding.CompiledPlanFingerprint == requestPlan,
                "The Cosmos storage binding lacks or conflicts with exact compiled-plan affinity.");
            Require(
                binding.PlacementFingerprint is not null && binding.PlacementFingerprint == request.Placement,
                "The Cosmos storage binding lacks or conflicts with exact source-placement affinity.");
            Require(
                CosmosRelationQueryBindingFingerprinter.Compute(binding) == binding.Fingerprint,
                "The Cosmos storage binding fingerprint is stale or malformed.");
            Require(
                CosmosRelationQueryArtifactFingerprinter.Compute(
                    artifact.Branch,
                    artifact.Statement,
                    binding,
                    artifact.SelectedFields,
                    artifact.ResultFields,
                    artifact.ResultIdentity,
                    artifact.AuxiliaryResultAliases,
                    artifact.Parameters,
                    artifact.Paging,
                    provenance) == artifact.Fingerprint,
                "The Cosmos compiled-artifact fingerprint is stale or malformed.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            diagnostics.Add(Error(
                request,
                CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
                $"The Cosmos compiled artifact could not be fingerprint-verified ({exception.GetType().Name})."));
        }

        Require(
            request.StorageBindingFingerprint == binding.Fingerprint,
            "The invocation storage-binding fingerprint does not match the compiled artifact.");
        Require(request.Realization == provenance.Realization, "The invocation realization fingerprint does not match artifact provenance.");
        Require(request.Placement == provenance.Placement, "The invocation source-placement fingerprint does not match artifact provenance.");
        Require(artifact.Branch.Id == provenance.Branch, "The artifact branch identity does not match its provenance.");
        Require(binding.Target == provenance.Target, "The Cosmos binding target does not match artifact provenance.");
        Require(binding.TargetProfile == provenance.TargetProfile, "The Cosmos binding target profile does not match artifact provenance.");
        Require(
            provenance.Target == CosmosRelationQueryTargetProfile.Target
            && provenance.TargetProfile == CosmosRelationQueryTargetProfile.ProfileId,
            "The artifact does not target the canonical Cosmos SQL capability profile.");
        Require(
            string.Equals(
                provenance.CompilerProfile,
                CosmosRelationQueryCompilerOptions.CurrentCompilerProfile,
                StringComparison.Ordinal),
            "The artifact compiler profile is not supported by this Cosmos executor.");
        Require(
            feedReader.AccountEndpoint == binding.AccountEndpoint,
            "The executor account endpoint does not match the artifact storage binding.");
        Require(
            string.Equals(feedReader.DatabaseName, binding.DatabaseName, StringComparison.Ordinal),
            "The executor database does not match the artifact storage binding.");
        Require(
            string.Equals(feedReader.ContainerName, binding.ContainerName, StringComparison.Ordinal),
            "The executor container does not match the artifact storage binding.");
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
                && IsFieldOnlyPath(field.Field.Path)),
            "The artifact result-field bindings contain an incompatible shape, cardinality, or structural path.");
        Require(
            artifact.ResultFields.All(field =>
                CosmosRelationQueryCanonicalValueCodec.IsResultEncodingCompatible(
                    field.ValueContract,
                    field.Encoding)),
            "The artifact result-field encoding metadata conflicts with its retained semantic value contracts.");
        Require(
            !HasPathPrefixConflict(artifact.ResultFields),
            "The artifact result-field paths cannot be reconstructed without a scalar/object collision.");
        Require(
            artifact.Branch.Kind == RelationQueryNativeResultKind.RelationRows || artifact.ResultIdentity is null,
            "Only relation-result artifacts may retain a hidden canonical identity binding.");
        Require(
            artifact.ResultIdentity is null
            || (artifact.ResultIdentity.ValueContract.Presence == FieldPresence.Required
                && artifact.ResultIdentity.ValueContract.Nullability == FieldNullability.NonNullable
                && CosmosRelationQueryCanonicalValueCodec.IsResultEncodingCompatible(
                    artifact.ResultIdentity.ValueContract,
                    artifact.ResultIdentity.Encoding)),
            "The artifact result identity must retain a required non-null canonical-key contract and compatible physical encoding.");
        Require(
            !artifact.AuxiliaryResultAliases.Any(alias =>
                artifact.ResultFields.Any(field => string.Equals(field.Alias, alias, StringComparison.Ordinal))
                || string.Equals(artifact.ResultIdentity?.Alias, alias, StringComparison.Ordinal)),
            "Auxiliary physical-result aliases cannot collide with canonical output aliases.");

        return diagnostics.ToImmutable();

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                diagnostics.Add(Error(
                    request,
                    CosmosRelationQueryArtifactExecutionDiagnosticCodes.ArtifactAffinityInvalid,
                    message));
            }
        }
    }

    long ResolveMaximumRows(CosmosRelationQueryArtifactExecutionRequest request)
    {
        var artifact = request.Artifact;
        var maximumRows = Math.Min(options.MaximumBufferedRows, request.MaximumRows);
        if (artifact.Paging is { } paging)
            maximumRows = Math.Min(maximumRows, paging.Limit);
        if (maximumRows <= 0)
        {
            throw new InvalidOperationException(
                "The effective Cosmos result-row boundary must remain positive after applying every artifact limit.");
        }
        return maximumRows;
    }

    static (ImmutableArray<RelationQueryOutputRow> Rows,
        ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> Diagnostics) DecodeRows(
        CosmosRelationQueryArtifactExecutionRequest request,
        ImmutableArray<JsonElement> physicalRows,
        CancellationToken cancellationToken
        )
    {
        HashSet<string> expectedAliases = new(
            request.Artifact.ResultFields.Select(static field => field.Alias),
            StringComparer.Ordinal);
        if (request.Artifact.ResultIdentity is { } expectedIdentity)
            expectedAliases.Add(expectedIdentity.Alias);
        expectedAliases.UnionWith(request.Artifact.AuxiliaryResultAliases);
        var rows = ImmutableArray.CreateBuilder<RelationQueryOutputRow>(physicalRows.Length);
        HashSet<ObservationValue>? observedIdentities = request.Artifact.ResultIdentity is null
            ? null
            : [];
        var diagnostics = ImmutableArray.CreateBuilder<CosmosRelationQueryArtifactExecutionDiagnostic>();

        for (var index = 0; index < physicalRows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDecodeRow(
                    request.Artifact,
                    expectedAliases,
                    physicalRows[index],
                    cancellationToken,
                    out var row,
                    out var message))
            {
                if (observedIdentities is not null
                    && row!.Identity is { } identity
                    && !observedIdentities.Add(identity))
                {
                    diagnostics.Add(Error(
                        request,
                        CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid,
                        "Cosmos relation result rows contain a duplicate canonical output identity.",
                        rowOrdinal: index));
                    continue;
                }
                rows.Add(row!);
                continue;
            }

            diagnostics.Add(Error(
                request,
                CosmosRelationQueryArtifactExecutionDiagnosticCodes.ResultInvalid,
                message!,
                rowOrdinal: index));
        }

        return diagnostics.Count == 0
            ? (rows.MoveToImmutable(), [])
            : ([], diagnostics.ToImmutable());
    }

    static bool TryDecodeRow(
        CosmosRelationQueryCompiledArtifact artifact,
        IReadOnlySet<string> expectedAliases,
        JsonElement physicalRow,
        CancellationToken cancellationToken,
        out RelationQueryOutputRow? row,
        out string? error)
    {
        row = null;
        if (physicalRow.ValueKind != JsonValueKind.Object)
        {
            error = "A Cosmos result row was not a JSON object projected by the compiled artifact.";
            return false;
        }

        Dictionary<string, JsonElement> physicalValues = new(expectedAliases.Count, StringComparer.Ordinal);
        foreach (var property in physicalRow.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expectedAliases.Contains(property.Name))
            {
                error = $"Cosmos result alias '{property.Name}' is not declared by the compiled artifact.";
                return false;
            }
            if (!physicalValues.TryAdd(property.Name, property.Value))
            {
                error = $"Cosmos result alias '{property.Name}' occurs more than once in one physical row.";
                return false;
            }
        }
        foreach (var auxiliaryAlias in artifact.AuxiliaryResultAliases)
        {
            if (!physicalValues.ContainsKey(auxiliaryAlias))
            {
                error = $"Cosmos result row is missing retained auxiliary alias '{auxiliaryAlias}'.";
                return false;
            }
        }

        MutableObservationObject? valueBuilder = null;
        foreach (var field in artifact.ResultFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservationValue decoded;
            if (!physicalValues.TryGetValue(field.Alias, out var element))
            {
                decoded = ObservationValue.Undefined;
            }
            else if (element.ValueKind == JsonValueKind.Null)
            {
                decoded = ObservationValue.Null;
            }
            else if (!CosmosRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                         element,
                         field.ValueContract,
                         field.Encoding,
                         out decoded))
            {
                error = $"Cosmos result alias '{field.Alias}' does not match its retained physical encoding '{field.Encoding}'.";
                return false;
            }

            if (!field.ValueContract.IsSatisfiedByConstant(decoded))
            {
                error = $"Cosmos result alias '{field.Alias}' violates its retained semantic value contract.";
                return false;
            }

            if (decoded.Kind != ObservationValueKind.Undefined)
            {
                try
                {
                    (valueBuilder ??= new(artifact.ResultFields.Length)).Set(field.Field.Path, decoded);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    error = $"Cosmos result alias '{field.Alias}' could not be reconstructed at its canonical field path ({exception.GetType().Name}).";
                    return false;
                }
            }
        }
        var value = valueBuilder?.Freeze() ?? ObservationValue.EmptyObject;

        ObservationValue? identity = null;
        if (artifact.ResultIdentity is { } identityBinding)
        {
            if (!physicalValues.TryGetValue(identityBinding.Alias, out var identityElement))
            {
                error = $"Cosmos result row is missing retained identity alias '{identityBinding.Alias}'.";
                return false;
            }

            if (!CosmosRelationQueryCanonicalValueCodec.TryDecodeResultValue(
                    identityElement,
                    identityBinding.ValueContract,
                    identityBinding.Encoding,
                    out var decodedIdentity)
                || !identityBinding.ValueContract.IsSatisfiedByConstant(decodedIdentity))
            {
                error = $"Cosmos result identity alias '{identityBinding.Alias}' does not match its retained physical encoding '{identityBinding.Encoding}' and semantic value contract.";
                return false;
            }
            identity = decodedIdentity;
        }

        row = new(
            artifact.Branch.Shape,
            value,
            identity,
            root: null,
            inputOccurrences: [],
            unresolvedGaps: []);
        error = null;
        return true;
    }

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
                fields.Add(
                    name,
                    member.Object is null
                        ? member.Value
                        : member.Object.Freeze());
            }
            return ObservationValue.FromObject(fields.ToImmutable());
        }

        void Set(FieldPath path, int segmentIndex, ObservationValue value)
        {
            var segment = path.Segments[segmentIndex];
            if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
            {
                throw new NotSupportedException(
                    $"Observation value assignment does not support collection-element path '{path}'.");
            }

            var name = segment.Segment;
            if (segmentIndex == path.Segments.Length - 1)
            {
                if (members.TryGetValue(name, out var existing) && existing.Object is not null)
                {
                    throw new InvalidOperationException(
                        $"Field path '{path}' cannot replace a reconstructed object with value kind '{value.Kind}'.");
                }
                members[name] = new(value, Object: null);
                return;
            }

            MutableObservationObject child;
            if (members.TryGetValue(name, out var member))
            {
                child = member.Object
                    ?? throw new InvalidOperationException(
                        $"Field path '{path}' cannot be assigned through value kind '{member.Value.Kind}'.");
            }
            else
            {
                child = new();
                members.Add(name, new(default, child));
            }
            child.Set(path, segmentIndex + 1, value);
        }

        readonly record struct Member(
            ObservationValue Value,
            MutableObservationObject? Object);
    }

    static bool IsFieldOnlyPath(FieldPath path) =>
        !path.Segments.IsDefaultOrEmpty
        && path.Segments.All(static segment =>
            segment.Kind == SegmentKind.Field && !string.IsNullOrEmpty(segment.Segment));

    static bool HasPathPrefixConflict(ImmutableArray<CosmosRelationQueryResultFieldBinding> fields)
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

    static CosmosRelationQueryArtifactExecutionDiagnostic Error(
        CosmosRelationQueryArtifactExecutionRequest request,
        string code,
        string message,
        long? rowOrdinal = null,
        string? evidenceReference = null) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            request.Artifact.Branch.Id,
            rowOrdinal,
            evidenceReference);

    static CosmosRelationQueryArtifactExecutionDiagnostic Warning(
        CosmosRelationQueryArtifactExecutionRequest request,
        string code,
        string message,
        long? rowOrdinal = null,
        string? evidenceReference = null) =>
        new(
            code,
            DiagnosticSeverity.Warning,
            message,
            request.Artifact.Branch.Id,
            rowOrdinal,
            evidenceReference);

    static CosmosRelationQueryArtifactExecutionResult Failed(
        CosmosRelationQueryArtifactExecutionRequest request,
        ImmutableArray<CosmosRelationQueryArtifactExecutionDiagnostic> diagnostics,
        string? providerEvidenceReference = null) =>
        new(
            request,
            RelationQueryExecutionStatus.Failed,
            [],
            diagnostics,
            providerEvidenceReference);

    sealed record PreparedArtifactInvocation(
        CosmosRelationQueryArtifactExecutionRequest Request,
        CosmosJsonQueryFeedReader.PreparedRequest FeedRequest,
        long MaximumRows);
}

/// <summary>Stable Cosmos compiled-artifact execution diagnostic codes.</summary>
public static class CosmosRelationQueryArtifactExecutionDiagnosticCodes
{
    /// <summary>An invocation affinity fact conflicts with the compiled artifact or physical container.</summary>
    public const string ArtifactAffinityInvalid = "REL2270";

    /// <summary>Canonical invocation parameters cannot be bound to the compiled command template.</summary>
    public const string InvocationInvalid = "REL2271";

    /// <summary>The Cosmos SDK or service failed before complete provider exhaustion.</summary>
    public const string ProviderFailure = "REL2272";

    /// <summary>A declared row boundary produced an attributable but incomplete provider-order prefix.</summary>
    public const string ResultBoundaryExceeded = "REL2273";

    /// <summary>A provider result row conflicts with retained physical or semantic result metadata.</summary>
    public const string ResultInvalid = "REL2274";

    /// <summary>A combined invocation was rejected because at least one branch failed preflight validation.</summary>
    public const string BatchPreflightFailed = "REL2275";

    /// <summary>
    /// A fully bound command exceeds an explicit request-size boundary or cannot be measured deterministically.
    /// </summary>
    public const string RequestSizePreflightFailed = "REL2276";
}
