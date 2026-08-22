using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Observability;

/// <summary>
/// Stable operational telemetry names and value conventions for canonical relation/query operations.
/// </summary>
/// <remarks>
/// This catalog describes runtime tracing and metrics. It is distinct from semantic result observability such as
/// <see cref="RelationQueryResultObservability"/>. Telemetry must summarize existing canonical artifacts and must
/// not become another semantic result model.
/// </remarks>
public static class RelationQueryTelemetry
{
    const int Sha256HexLength = 64;

    /// <summary>Activity-source name emitted by <c>Cohesive.Relations</c>.</summary>
    public const string ActivitySourceName = "Cohesive.Relations";

    /// <summary>Meter name emitted by <c>Cohesive.Relations</c>.</summary>
    public const string MeterName = "Cohesive.Relations";

    /// <summary>Canonical evaluation activity name and metric operation value.</summary>
    public const string EvaluationActivityName = "cohesive.relations.evaluate";

    /// <summary>Target-independent static-compilation activity name and metric operation value.</summary>
    public const string StaticCompilationActivityName = "cohesive.relations.compile";

    /// <summary>Target-profile feasibility evaluation activity name and metric operation value.</summary>
    public const string ProfileFeasibilityActivityName = "cohesive.relations.profile.evaluate";

    /// <summary>Target realization activity name and metric operation value.</summary>
    public const string RealizationActivityName = "cohesive.relations.realize";

    /// <summary>Target-independent physical-planning activity name and metric operation value.</summary>
    public const string PhysicalPlanningActivityName = "cohesive.relations.physical.plan";

    /// <summary>Physical execution activity name and metric operation value.</summary>
    public const string PhysicalExecutionActivityName = "cohesive.relations.physical.execute";

    /// <summary>Bounded physical source-read activity name and metric operation value.</summary>
    public const string SourceReadActivityName = "cohesive.relations.source.read";

    /// <summary>Canonical interpretation activity name and metric operation value.</summary>
    public const string InterpretationActivityName = "cohesive.relations.interpret";

    /// <summary>DTO-kernel compilation activity name and metric operation value.</summary>
    public const string DtoCompilationActivityName = "cohesive.relations.dto.compile";

    /// <summary>DTO batch-materialization activity name and metric operation value.</summary>
    public const string DtoMappingActivityName = "cohesive.relations.dto.map";

    /// <summary>Target-native compilation activity name and metric operation value.</summary>
    public const string NativeCompilationActivityName = "cohesive.relations.native.compile";

    /// <summary>Target-native execution activity name and metric operation value.</summary>
    public const string NativeExecutionActivityName = "cohesive.relations.native.execute";

    /// <summary>Duration histogram for completed relation/query operations, measured in seconds.</summary>
    public const string OperationDurationInstrumentName = "cohesive.relations.operation.duration";

    /// <summary>Counter of rows returned by bounded physical source reads.</summary>
    public const string SourceRowsInstrumentName = "cohesive.relations.source.rows";

    /// <summary>Counter of input, emitted, and rejected DTO rows.</summary>
    public const string DtoRowsInstrumentName = "cohesive.relations.dto.rows";

    /// <summary>Counter of canonical runtime requirement gaps grouped by portable cause.</summary>
    public const string RequirementGapsInstrumentName = "cohesive.relations.requirement_gaps";

    /// <summary>Activity and metric tag identifying the bounded operation.</summary>
    public const string OperationTagName = "cohesive.relations.operation";

    /// <summary>Activity and metric tag identifying the bounded terminal status.</summary>
    public const string StatusTagName = "cohesive.relations.status";

    /// <summary>Trace attribute identifying the stable interpretation target; this is never a core metric dimension.</summary>
    public const string TargetTagName = "cohesive.relations.target";

    /// <summary>Activity and metric tag identifying the phase that terminated canonical evaluation.</summary>
    public const string TerminalPhaseTagName = "cohesive.relations.terminal_phase";

    /// <summary>Activity and metric tag distinguishing a bound request from an already-native request.</summary>
    public const string RequestKindTagName = "cohesive.relations.request_kind";

    /// <summary>Request-kind value identifying compilation from a context-bound realization.</summary>
    public const string BoundRequestKind = "bound";

    /// <summary>Request-kind value identifying compilation from an already-native request.</summary>
    public const string NativeRequestKind = "native";

    /// <summary>Activity and metric tag identifying a bounded source-read kind.</summary>
    public const string ReadKindTagName = "cohesive.relations.read.kind";

    /// <summary>Activity and metric tag identifying evidence completeness.</summary>
    public const string CompletenessTagName = "cohesive.relations.completeness";

    /// <summary>Activity and metric tag identifying the DTO row-failure policy.</summary>
    public const string FailurePolicyTagName = "cohesive.relations.dto.failure_policy";

    /// <summary>Metric tag identifying whether DTO rows were supplied, emitted, or rejected.</summary>
    public const string RowOutcomeTagName = "cohesive.relations.row.outcome";

    /// <summary>Metric tag identifying a portable requirement-gap cause.</summary>
    public const string GapCauseTagName = "cohesive.relations.requirement_gap.cause";

    /// <summary>Trace attribute containing a structured diagnostic count.</summary>
    public const string DiagnosticCountTagName = "cohesive.relations.diagnostic.count";

    /// <summary>Trace attribute containing a canonical runtime requirement-gap count.</summary>
    public const string GapCountTagName = "cohesive.relations.requirement_gap.count";

    /// <summary>Trace attribute containing a target-native artifact count.</summary>
    public const string ArtifactCountTagName = "cohesive.relations.artifact.count";

    /// <summary>Trace attribute containing a demanded result-branch count.</summary>
    public const string BranchCountTagName = "cohesive.relations.branch.count";

    /// <summary>Trace attribute containing a row count.</summary>
    public const string RowCountTagName = "cohesive.relations.row.count";

    /// <summary>Trace attribute containing a successfully emitted DTO-row count.</summary>
    public const string EmittedRowCountTagName = "cohesive.relations.dto.row.emitted_count";

    /// <summary>Trace attribute containing a rejected DTO-row count.</summary>
    public const string RejectedRowCountTagName = "cohesive.relations.dto.row.rejected_count";

    /// <summary>Trace attribute containing a lookup-key count.</summary>
    public const string KeyCountTagName = "cohesive.relations.key.count";

    /// <summary>Trace attribute containing a zero-based physical source-read batch ordinal.</summary>
    public const string BatchOrdinalTagName = "cohesive.relations.batch.ordinal";

    /// <summary>Trace attribute containing a canonical definition fingerprint.</summary>
    public const string DefinitionFingerprintTagName = "cohesive.relations.fingerprint.definition";

    /// <summary>Trace attribute containing the complete canonical evaluation fingerprint.</summary>
    public const string EvaluationFingerprintTagName = "cohesive.relations.fingerprint.evaluation";

    /// <summary>Trace attribute containing a compiled-plan fingerprint.</summary>
    public const string PlanFingerprintTagName = "cohesive.relations.fingerprint.plan";

    /// <summary>Trace attribute containing a compiled physical-plan fingerprint.</summary>
    public const string PhysicalPlanFingerprintTagName = "cohesive.relations.fingerprint.physical_plan";

    /// <summary>Trace attribute containing a target realization fingerprint.</summary>
    public const string RealizationFingerprintTagName = "cohesive.relations.fingerprint.realization";

    /// <summary>Trace attribute containing a context-bound realization fingerprint.</summary>
    public const string BoundRealizationFingerprintTagName = "cohesive.relations.fingerprint.bound_realization";

    /// <summary>Trace attribute containing a physical placement fingerprint.</summary>
    public const string PlacementFingerprintTagName = "cohesive.relations.fingerprint.placement";

    /// <summary>Trace attribute containing an adapter-binding fingerprint.</summary>
    public const string BindingFingerprintTagName = "cohesive.relations.fingerprint.binding";

    /// <summary>Trace attribute containing a derived target-artifact fingerprint.</summary>
    public const string ArtifactFingerprintTagName = "cohesive.relations.fingerprint.artifact";

    /// <summary>Trace attribute containing a compiled DTO-kernel fingerprint.</summary>
    public const string DtoCompilationFingerprintTagName = "cohesive.relations.fingerprint.dto_compilation";

    /// <summary>Trace attribute containing a portable schema version.</summary>
    public const string SchemaVersionTagName = "cohesive.relations.schema.version";

    /// <summary>Trace attribute containing a compiler profile version.</summary>
    public const string CompilerProfileTagName = "cohesive.relations.compiler.profile";

    /// <summary>Trace attribute containing a convention-set version.</summary>
    public const string ConventionVersionTagName = "cohesive.relations.convention.version";

    /// <summary>Trace attribute containing a failure exception type without its message or stack.</summary>
    public const string ErrorTypeTagName = "error.type";

    /// <summary>Activity-event name for one structured relation/query diagnostic.</summary>
    public const string DiagnosticEventName = "cohesive.relations.diagnostic";

    /// <summary>Diagnostic-event attribute containing a stable machine-readable diagnostic code.</summary>
    public const string DiagnosticCodeTagName = "cohesive.relations.diagnostic.code";

    /// <summary>Diagnostic-event attribute containing a bounded diagnostic severity.</summary>
    public const string DiagnosticSeverityTagName = "cohesive.relations.diagnostic.severity";

    /// <summary>Terminal status used when an operation observes cancellation.</summary>
    public const string CanceledStatus = "canceled";

    /// <summary>Terminal status used when an operation propagates an exception.</summary>
    public const string ExceptionStatus = "exception";

    /// <summary>Terminal status used when telemetry policy fails after the observed operation succeeds.</summary>
    public const string ObservabilityFailureStatus = "observability_failure";

    /// <summary>Terminal status used when an operation completes successfully.</summary>
    public const string SucceededStatus = "succeeded";

    /// <summary>Terminal status used when an operation returns a structured failure.</summary>
    public const string FailedStatus = "failed";

    /// <summary>Terminal status used when an operation rejects an invalid contract.</summary>
    public const string InvalidStatus = "invalid";

    /// <summary>DTO-row outcome identifying canonical input rows.</summary>
    public const string InputRowOutcome = "input";

    /// <summary>DTO-row outcome identifying successfully emitted rows.</summary>
    public const string EmittedRowOutcome = "emitted";

    /// <summary>DTO-row outcome identifying rejected rows.</summary>
    public const string RejectedRowOutcome = "rejected";

    /// <summary>Evaluation terminal phase identifying cancellation.</summary>
    public const string CancellationTerminalPhase = "cancellation";

    /// <summary>Evaluation terminal phase identifying an exceptional exit.</summary>
    public const string ExceptionTerminalPhase = "exception";

    /// <summary>Evaluation terminal phase identifying target-independent static compilation.</summary>
    public const string StaticCompilationTerminalPhase = "static_compilation";

    /// <summary>Evaluation terminal phase identifying compiled-plan affinity validation.</summary>
    public const string PlanAffinityTerminalPhase = "plan_affinity";

    /// <summary>Evaluation terminal phase identifying target realization.</summary>
    public const string RealizationTerminalPhase = "realization";

    /// <summary>Evaluation terminal phase identifying physical planning.</summary>
    public const string PhysicalPlanningTerminalPhase = "physical_planning";

    /// <summary>Evaluation terminal phase identifying physical execution.</summary>
    public const string PhysicalExecutionTerminalPhase = "physical_execution";

    /// <summary>Returns the canonical telemetry value for a relation/query execution status.</summary>
    /// <param name="status">Execution status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationQueryExecutionStatus status) => status switch
    {
        RelationQueryExecutionStatus.Succeeded => SucceededStatus,
        RelationQueryExecutionStatus.Incomplete => "incomplete",
        RelationQueryExecutionStatus.Failed => FailedStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported relation/query execution status.")
    };

    /// <summary>Returns the canonical telemetry value for a target-realization status.</summary>
    /// <param name="status">Realization status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationQueryRealizationStatus status) => status switch
    {
        RelationQueryRealizationStatus.Realizable => "realizable",
        RelationQueryRealizationStatus.NotRealizable => "not_realizable",
        RelationQueryRealizationStatus.Invalid => InvalidStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported relation/query realization status.")
    };

    /// <summary>Returns the canonical telemetry value for target-native compilation.</summary>
    /// <param name="status">Native-compilation status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationQueryNativeCompilationStatus status) => status switch
    {
        RelationQueryNativeCompilationStatus.Exact => "exact",
        RelationQueryNativeCompilationStatus.Unsupported => "unsupported",
        RelationQueryNativeCompilationStatus.Invalid => InvalidStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported native-compilation status.")
    };

    /// <summary>Returns the canonical telemetry value for physical planning.</summary>
    /// <param name="status">Physical-planning status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationQueryPhysicalPlanningStatus status) => status switch
    {
        RelationQueryPhysicalPlanningStatus.Planned => SucceededStatus,
        RelationQueryPhysicalPlanningStatus.Unavailable => "unavailable",
        RelationQueryPhysicalPlanningStatus.Invalid => InvalidStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported physical-planning status.")
    };

    /// <summary>Returns the canonical telemetry value for a bounded physical source read.</summary>
    /// <param name="status">Source-read status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationQuerySourceReadState status) => status switch
    {
        RelationQuerySourceReadState.Complete => "complete",
        RelationQuerySourceReadState.Partial => "partial",
        RelationQuerySourceReadState.NotFound => "not_found",
        RelationQuerySourceReadState.Failed => FailedStatus,
        RelationQuerySourceReadState.Inconclusive => "inconclusive",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported source-read status.")
    };

    /// <summary>Returns the canonical telemetry value for DTO materialization.</summary>
    /// <param name="status">DTO-mapping status to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    public static string GetStatusTagValue(RelationDtoMappingStatus status) => status switch
    {
        RelationDtoMappingStatus.Succeeded => SucceededStatus,
        RelationDtoMappingStatus.SucceededWithSkippedRows => "succeeded_with_skipped_rows",
        RelationDtoMappingStatus.Incomplete => "incomplete",
        RelationDtoMappingStatus.Failed => FailedStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported DTO-mapping status.")
    };

    /// <summary>Returns the canonical telemetry value for a physical source-read kind.</summary>
    /// <param name="kind">Source-read kind to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public static string GetTagValue(RelationQuerySourceReadKind kind) => kind switch
    {
        RelationQuerySourceReadKind.BoundedEnumeration => "bounded_enumeration",
        RelationQuerySourceReadKind.IdentityBatch => "identity_batch",
        RelationQuerySourceReadKind.RelationshipKeyBatch => "relationship_key_batch",
        RelationQuerySourceReadKind.CollectionElementKeyBatch => "collection_element_key_batch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported source-read kind.")
    };

    /// <summary>Returns the canonical telemetry value for runtime evidence completeness.</summary>
    /// <param name="completeness">Completeness to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    public static string GetTagValue(RelationQueryEvidenceCompleteness completeness) => completeness switch
    {
        RelationQueryEvidenceCompleteness.Complete => "complete",
        RelationQueryEvidenceCompleteness.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported evidence completeness.")
    };

    /// <summary>Returns the canonical telemetry value for a DTO row-failure policy.</summary>
    /// <param name="policy">Failure policy to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is unsupported.</exception>
    public static string GetTagValue(RelationDtoMappingFailurePolicy policy) => policy switch
    {
        RelationDtoMappingFailurePolicy.Strict => "strict",
        RelationDtoMappingFailurePolicy.CollectDiagnostics => "collect_diagnostics",
        RelationDtoMappingFailurePolicy.SkipInvalidRows => "skip_invalid_rows",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported DTO-mapping failure policy.")
    };

    /// <summary>Returns the canonical telemetry value for a portable runtime requirement-gap cause.</summary>
    /// <param name="cause">Requirement-gap cause to project.</param>
    /// <returns>An interned low-cardinality telemetry value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cause"/> is unsupported.</exception>
    public static string GetTagValue(RelationRequirementGapCause cause) => cause switch
    {
        RelationRequirementGapCause.InputNotProvided => "input_not_provided",
        RelationRequirementGapCause.InputAcquisitionFailed => "input_acquisition_failed",
        RelationRequirementGapCause.ObservationIdentityMissing => "observation_identity_missing",
        RelationRequirementGapCause.ReferenceFieldNotLoaded => "reference_field_not_loaded",
        RelationRequirementGapCause.ReferenceValueMissing => "reference_value_missing",
        RelationRequirementGapCause.ReferenceValueNull => "reference_value_null",
        RelationRequirementGapCause.ResolutionNotAttempted => "resolution_not_attempted",
        RelationRequirementGapCause.ResolutionFailed => "resolution_failed",
        RelationRequirementGapCause.RelatedObservationNotFound => "related_observation_not_found",
        RelationRequirementGapCause.RelatedObservationRejected => "related_observation_rejected",
        RelationRequirementGapCause.RequiredFieldNotLoaded => "required_field_not_loaded",
        RelationRequirementGapCause.RequiredValueMissing => "required_value_missing",
        RelationRequirementGapCause.RequiredValueNull => "required_value_null",
        RelationRequirementGapCause.CapabilityUnavailable => "capability_unavailable",
        RelationRequirementGapCause.CardinalityViolation => "cardinality_violation",
        RelationRequirementGapCause.ConversionFailure => "conversion_failure",
        RelationRequirementGapCause.InputAcquisitionInconclusive => "input_acquisition_inconclusive",
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported requirement-gap cause.")
    };

    /// <summary>
    /// Adds a fingerprint trace attribute only when its value is a canonical lowercase hexadecimal SHA-256 digest.
    /// </summary>
    /// <param name="activity">Activity that receives the validated fingerprint.</param>
    /// <param name="tagName">Stable fingerprint trace-attribute name.</param>
    /// <param name="fingerprint">Candidate fingerprint value.</param>
    /// <returns>
    /// <see langword="true"/> when the fingerprint was canonical and emitted; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="activity"/>, <paramref name="tagName"/>, or <paramref name="fingerprint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="tagName"/> is empty or white space.</exception>
    public static bool TrySetFingerprintTag(Activity activity, string tagName, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length != Sha256HexLength)
            return false;
        foreach (var character in fingerprint)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        activity.SetTag(tagName, fingerprint);
        return true;
    }

    /// <summary>Adds one sampled structured diagnostic event without message text or application payload values.</summary>
    /// <param name="activity">Activity that owns the diagnostic.</param>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Bounded diagnostic severity.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="activity"/> or <paramref name="code"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    public static void AddDiagnosticEvent(
        Activity activity,
        string code,
        DiagnosticSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        if (!activity.IsAllDataRequested)
            return;

        try
        {
            ActivityTagsCollection tags = new()
            {
                [DiagnosticCodeTagName] = code,
                [DiagnosticSeverityTagName] = severity switch
                {
                    DiagnosticSeverity.Info => "info",
                    DiagnosticSeverity.Warning => "warning",
                    DiagnosticSeverity.Error => "error",
                    _ => throw new UnreachableException()
                }
            };
            activity.AddEvent(new(DiagnosticEventName, tags: tags));
        }
        catch (Exception exception) when (IsRecoverableObservabilityFailure(exception))
        {
            // Activity recording is a best-effort interpretation and cannot alter the observed operation.
        }
    }

    internal static bool IsRecoverableObservabilityFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}

/// <summary>
/// Package-owned, dependency-free emitter for relation/query activities and operation-duration measurements.
/// </summary>
/// <remarks>
/// Create one process-lifetime instance per instrumenting package. Operation-specific metrics remain owned by that
/// package; this emitter deliberately centralizes only the common activity and duration contract. A synchronous
/// registration-observer failure disables the affected tracing or duration channel for this emitter rather than
/// preventing package initialization.
/// </remarks>
public sealed class RelationQueryTelemetryEmitter : IDisposable
{
    readonly ActivitySource? source;
    readonly Meter meter;
    readonly Histogram<double>? operationDuration;

    /// <summary>Creates an emitter with matching activity-source and meter names.</summary>
    /// <param name="instrumentationName">Stable package-owned activity-source and meter name.</param>
    /// <param name="version">Optional package or instrumentation version.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instrumentationName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="instrumentationName"/> is empty or white space, or <paramref name="version"/> is non-null
    /// and empty or white space.
    /// </exception>
    public RelationQueryTelemetryEmitter(string instrumentationName, string? version = null)
    {
        InstrumentationName = Guard.RequireNotNullOrWhiteSpace(instrumentationName);
        if (version is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        try
        {
            source = new(InstrumentationName, Version);
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Activity registration observers run synchronously during construction and are strictly best effort.
        }
        meter = new(InstrumentationName, Version);
        try
        {
            operationDuration = meter.CreateHistogram<double>(
                RelationQueryTelemetry.OperationDurationInstrumentName,
                "s",
                "Elapsed duration of one bounded canonical relation/query operation.");
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Instrument-publication observers also run synchronously and cannot prevent package initialization.
        }
    }

    /// <summary>Stable package-owned activity-source and meter name.</summary>
    public string InstrumentationName { get; }

    /// <summary>Optional package or instrumentation version.</summary>
    public string? Version { get; }

    /// <summary>Whether an activity listener or duration-measurement listener currently observes this emitter.</summary>
    public bool IsEnabled => (source?.HasListeners() ?? false) || (operationDuration?.Enabled ?? false);

    internal Counter<T>? CreateCounter<T>(string name, string? unit, string? description)
        where T : struct
    {
        try
        {
            return meter.CreateCounter<T>(name, unit, description);
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Starts one low-cardinality operation activity when a listener requests it.</summary>
    /// <param name="name">Stable canonical or adapter-owned operation name.</param>
    /// <param name="kind">Activity kind describing the operation boundary.</param>
    /// <returns>
    /// A started activity, or <see langword="null"/> when no listener requests propagation or recorded data or an
    /// observer fails while the activity starts. The caller transfers disposal to <see cref="CompleteOperation"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported activity kind.");

        try
        {
            return source?.StartActivity(name, kind);
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Observers are application extensions. A failing observer must not alter relation/query semantics.
            return null;
        }
    }

    /// <summary>Captures a duration start timestamp only when the duration histogram is enabled.</summary>
    /// <returns>
    /// A monotonic timestamp accepted by <see cref="CompleteOperation"/>, or zero when duration measurement is
    /// disabled. The returned value has no wall-clock interpretation.
    /// </returns>
    public long StartTimer() => operationDuration?.Enabled == true ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>
    /// Completes one activity and optional duration measurement without projecting semantic payload values.
    /// Observer failures are contained and cannot alter the observed relation/query operation.
    /// </summary>
    /// <param name="activity">Started activity returned by <see cref="StartActivity"/>, or <see langword="null"/>.</param>
    /// <param name="started">Timestamp returned by <see cref="StartTimer"/>.</param>
    /// <param name="operation">Stable canonical or adapter-owned operation name.</param>
    /// <param name="status">Low-cardinality canonical terminal status.</param>
    /// <param name="terminalPhase">Optional low-cardinality terminal phase.</param>
    /// <param name="exception">
    /// Propagated failure, or <see langword="null"/>. Only its runtime type is recorded; its message and stack are
    /// never emitted by this method.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operation"/> or <paramref name="status"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> or <paramref name="status"/> is empty or white space, or
    /// <paramref name="terminalPhase"/> is non-null and empty or white space.
    /// </exception>
    public void CompleteOperation(
        Activity? activity,
        long started,
        string operation,
        string status,
        string? terminalPhase = null,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (terminalPhase is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(terminalPhase);

        try
        {
            if (activity is not null)
            {
                activity.SetTag(RelationQueryTelemetry.OperationTagName, operation);
                activity.SetTag(RelationQueryTelemetry.StatusTagName, status);
                if (terminalPhase is not null)
                    activity.SetTag(RelationQueryTelemetry.TerminalPhaseTagName, terminalPhase);
                if (exception is not null)
                    activity.SetTag(RelationQueryTelemetry.ErrorTypeTagName, exception.GetType().FullName);
                if (exception is not null
                    || string.Equals(status, RelationQueryTelemetry.FailedStatus, StringComparison.Ordinal)
                    || string.Equals(status, RelationQueryTelemetry.InvalidStatus, StringComparison.Ordinal))
                {
                    activity.SetStatus(ActivityStatusCode.Error);
                }
            }

            if (started != 0L && operationDuration is not null)
            {
                TagList tags = default;
                tags.Add(RelationQueryTelemetry.OperationTagName, operation);
                tags.Add(RelationQueryTelemetry.StatusTagName, status);
                if (terminalPhase is not null)
                    tags.Add(RelationQueryTelemetry.TerminalPhaseTagName, terminalPhase);
                operationDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
        }
        catch (Exception telemetryException) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(telemetryException))
        {
            // Metrics listeners run synchronously. Contain observer failures at the observability boundary.
        }

        try
        {
            activity?.Dispose();
        }
        catch (Exception telemetryException) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(telemetryException))
        {
            // Activity stop listeners also run synchronously and are strictly best effort.
        }
    }

    /// <summary>Releases the package-owned activity source and meter.</summary>
    public void Dispose()
    {
        try
        {
            source?.Dispose();
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Listener cleanup remains best effort for the same reason as emission.
        }

        try
        {
            meter.Dispose();
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Measurement-listener cleanup cannot alter application disposal semantics.
        }
    }
}

/// <summary>
/// Executes compiler operations through the shared relation/query telemetry lifecycle and projects common bounded
/// compiler context onto activities.
/// </summary>
/// <remarks>
/// The operation state and callbacks are generic so callers can use value-type state and cached static lambdas. When
/// the emitter is disabled, <see cref="Observe{TState,TResult}"/> invokes the compiler directly without starting a
/// timer or allocating telemetry state. Activity and meter listener failures are contained by
/// <see cref="RelationQueryTelemetryEmitter"/> and cannot change compiler results.
/// </remarks>
public static class RelationQueryCompilerTelemetry
{
    /// <summary>Executes one compiler operation and observes its terminal status when telemetry is enabled.</summary>
    /// <typeparam name="TState">Compiler invocation state passed without capture to the callbacks.</typeparam>
    /// <typeparam name="TResult">Compiler result type.</typeparam>
    /// <param name="emitter">Package-owned telemetry emitter.</param>
    /// <param name="operation">Stable compiler activity and metric operation name.</param>
    /// <param name="state">Compiler invocation state.</param>
    /// <param name="compile">Static-lambda-friendly compiler callback.</param>
    /// <param name="getStatus">Callback projecting the compiler result to a bounded terminal status.</param>
    /// <param name="projectActivity">
    /// Optional callback projecting bounded compiler context onto a fully sampled activity after compilation.
    /// </param>
    /// <param name="requestKind">Optional bounded request-kind value recorded before compilation begins.</param>
    /// <returns>The exact result returned by <paramref name="compile"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="emitter"/>, <paramref name="operation"/>, <paramref name="compile"/>, or
    /// <paramref name="getStatus"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is empty or white space, or <paramref name="requestKind"/> is non-null and empty
    /// or white space.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="compile"/> observes cancellation.</exception>
    /// <exception cref="Exception"><paramref name="compile"/> propagates a non-cancellation failure.</exception>
    public static TResult Observe<TState, TResult>(
        RelationQueryTelemetryEmitter emitter,
        string operation,
        TState state,
        Func<TState, TResult> compile,
        Func<TResult, string> getStatus,
        Action<Activity, TState, TResult>? projectActivity = null,
        string? requestKind = null)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(getStatus);
        if (requestKind is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(requestKind);

        if (!emitter.IsEnabled)
            return compile(state);

        Activity? activity = emitter.StartActivity(operation);
        var started = emitter.StartTimer();
        if (requestKind is not null)
            activity?.SetTag(RelationQueryTelemetry.RequestKindTagName, requestKind);
        TResult result;
        try
        {
            result = compile(state);
        }
        catch (OperationCanceledException exception)
        {
            emitter.CompleteOperation(
                activity,
                started,
                operation,
                RelationQueryTelemetry.CanceledStatus,
                exception: exception);
            throw;
        }
        catch (Exception exception)
        {
            emitter.CompleteOperation(
                activity,
                started,
                operation,
                RelationQueryTelemetry.ExceptionStatus,
                exception: exception);
            throw;
        }

        try
        {
            if (activity?.IsAllDataRequested == true)
                projectActivity?.Invoke(activity, state, result);
            emitter.CompleteOperation(activity, started, operation, getStatus(result));
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            emitter.CompleteOperation(
                activity,
                started,
                operation,
                RelationQueryTelemetry.ObservabilityFailureStatus,
                exception: exception);
        }
        return result;
    }

    /// <summary>Projects common contextual-realization compiler evidence onto a sampled activity.</summary>
    /// <param name="activity">Fully sampled compiler activity.</param>
    /// <param name="request">Contextual realization request.</param>
    /// <param name="bindingFingerprint">Target storage-binding fingerprint.</param>
    /// <param name="report">Exact bound-realization report produced for <paramref name="request"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="activity"/>, <paramref name="request"/>, <paramref name="bindingFingerprint"/>, or
    /// <paramref name="report"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="bindingFingerprint"/> is empty or white space.</exception>
    public static void ProjectRealizationActivity(
        Activity activity,
        RelationQueryBoundRealizationRequest request,
        string bindingFingerprint,
        RelationQueryBoundRealizationReport report)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingFingerprint);
        ArgumentNullException.ThrowIfNull(report);

        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlanFingerprintTagName,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference).Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.RealizationFingerprintTagName,
            request.ProfileFeasibility.Fingerprint.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlacementFingerprintTagName,
            request.Placement.Fingerprint.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.BindingFingerprintTagName,
            bindingFingerprint);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.BoundRealizationFingerprintTagName,
            report.Fingerprint.Value);
        activity.SetTag(RelationQueryTelemetry.TargetTagName, request.ProfileFeasibility.TargetProfile.Target.Value);
        activity.SetTag(RelationQueryTelemetry.BranchCountTagName, request.Selection.Branches.Length);
        activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, report.Diagnostics.Length);
        foreach (var diagnostic in report.Diagnostics)
        {
            RelationQueryTelemetry.AddDiagnosticEvent(
                activity,
                diagnostic.Code,
                diagnostic.Severity);
        }
    }

    /// <summary>Projects common native-compilation evidence onto a sampled activity.</summary>
    /// <param name="activity">Fully sampled compiler activity.</param>
    /// <param name="plan">Exact compiled-plan reference.</param>
    /// <param name="placement">Exact source placement.</param>
    /// <param name="target">Stable semantic interpretation-target identity.</param>
    /// <param name="bindingFingerprint">Target storage-binding fingerprint.</param>
    /// <param name="boundRealizationFingerprint">
    /// Exact contextual realization fingerprint used by compilation, or <see langword="null"/> when unavailable.
    /// </param>
    /// <param name="artifactCount">Number of successfully constructed native artifacts.</param>
    /// <param name="diagnosticCount">Number of native-compilation diagnostics.</param>
    /// <param name="singleArtifactFingerprint">
    /// Fingerprint of the sole native artifact, or <see langword="null"/> when the result contains zero or multiple
    /// artifacts.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="activity"/>, <paramref name="plan"/>, <paramref name="placement"/>,
    /// <paramref name="target"/>, or <paramref name="bindingFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> or a supplied fingerprint is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="artifactCount"/> or <paramref name="diagnosticCount"/> is negative.
    /// </exception>
    public static void ProjectNativeCompilationActivity(
        Activity activity,
        RelationQueryCompiledPlanReference plan,
        RelationQuerySourcePlacement placement,
        string target,
        string bindingFingerprint,
        string? boundRealizationFingerprint,
        int artifactCount,
        int diagnosticCount,
        string? singleArtifactFingerprint)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingFingerprint);
        if (boundRealizationFingerprint is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(boundRealizationFingerprint);
        if (singleArtifactFingerprint is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(singleArtifactFingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactCount);
        ArgumentOutOfRangeException.ThrowIfNegative(diagnosticCount);

        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlanFingerprintTagName,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(plan).Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlacementFingerprintTagName,
            placement.Fingerprint.Value);
        RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.BindingFingerprintTagName,
            bindingFingerprint);
        activity.SetTag(RelationQueryTelemetry.TargetTagName, target);
        if (boundRealizationFingerprint is not null)
        {
            RelationQueryTelemetry.TrySetFingerprintTag(
                activity,
                RelationQueryTelemetry.BoundRealizationFingerprintTagName,
                boundRealizationFingerprint);
        }
        activity.SetTag(RelationQueryTelemetry.ArtifactCountTagName, artifactCount);
        activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, diagnosticCount);
        if (singleArtifactFingerprint is not null)
        {
            RelationQueryTelemetry.TrySetFingerprintTag(
                activity,
                RelationQueryTelemetry.ArtifactFingerprintTagName,
                singleArtifactFingerprint);
        }
    }
}

internal static class RelationQueryTelemetryRuntime
{
    const string RowUnit = "{row}";
    const string GapUnit = "{gap}";
    const int RequirementGapCauseCount = (int)RelationRequirementGapCause.InputAcquisitionInconclusive + 1;

    static readonly string? Version = typeof(RelationQueryTelemetry).Assembly.GetName().Version?.ToString();
    static readonly RelationQueryTelemetryEmitter Emitter = new(RelationQueryTelemetry.ActivitySourceName, Version);
    static readonly Counter<long>? SourceRows = Emitter.CreateCounter<long>(
        RelationQueryTelemetry.SourceRowsInstrumentName,
        RowUnit,
        "Rows returned by bounded physical source reads.");
    static readonly Counter<long>? DtoRows = Emitter.CreateCounter<long>(
        RelationQueryTelemetry.DtoRowsInstrumentName,
        RowUnit,
        "Canonical input, emitted, and rejected DTO rows.");
    static readonly Counter<long>? RequirementGaps = Emitter.CreateCounter<long>(
        RelationQueryTelemetry.RequirementGapsInstrumentName,
        GapUnit,
        "Canonical runtime requirement gaps grouped by portable cause.");

    internal static bool IsOperationEnabled => Emitter.IsEnabled;

    internal static bool IsSourceReadEnabled => IsOperationEnabled || SourceRows?.Enabled == true;

    internal static bool IsInterpretationEnabled => IsOperationEnabled || RequirementGaps?.Enabled == true;

    internal static bool IsDtoMappingEnabled => IsOperationEnabled || DtoRows?.Enabled == true;

    internal static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal) => Emitter.StartActivity(name, kind);

    internal static long StartTimer() => Emitter.StartTimer();

    internal static void CompleteOperation(
        Activity? activity,
        long started,
        string operation,
        string status,
        string? terminalPhase = null,
        Exception? exception = null)
        => Emitter.CompleteOperation(activity, started, operation, status, terminalPhase, exception);

    internal static void RecordSourceRows(
        int rows,
        RelationQuerySourceReadKind kind,
        RelationQuerySourceReadState status)
    {
        if (SourceRows?.Enabled != true || rows <= 0)
            return;
        TagList tags = default;
        tags.Add(RelationQueryTelemetry.ReadKindTagName, RelationQueryTelemetry.GetTagValue(kind));
        tags.Add(RelationQueryTelemetry.StatusTagName, RelationQueryTelemetry.GetStatusTagValue(status));
        try
        {
            SourceRows.Add(rows, tags);
        }
        catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
        {
            // Measurement observers are synchronous and cannot alter source-read semantics.
        }
    }

    internal static void RecordDtoRows(
        int inputRows,
        int emittedRows,
        int rejectedRows,
        RelationDtoMappingStatus status,
        RelationDtoMappingFailurePolicy failurePolicy)
    {
        var dtoRows = DtoRows;
        if (dtoRows?.Enabled != true)
            return;

        var statusValue = RelationQueryTelemetry.GetStatusTagValue(status);
        var policyValue = RelationQueryTelemetry.GetTagValue(failurePolicy);
        Add(inputRows, RelationQueryTelemetry.InputRowOutcome);
        Add(emittedRows, RelationQueryTelemetry.EmittedRowOutcome);
        Add(rejectedRows, RelationQueryTelemetry.RejectedRowOutcome);
        return;

        void Add(int rows, string outcome)
        {
            if (rows <= 0)
                return;
            TagList tags = default;
            tags.Add(RelationQueryTelemetry.RowOutcomeTagName, outcome);
            tags.Add(RelationQueryTelemetry.StatusTagName, statusValue);
            tags.Add(RelationQueryTelemetry.FailurePolicyTagName, policyValue);
            try
            {
                dtoRows.Add(rows, tags);
            }
            catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
            {
                // Measurement observers are synchronous and cannot alter DTO-mapping semantics.
            }
        }
    }

    internal static void RecordRequirementGaps(RelationRequirementGapAnalysisResult analysis)
    {
        if (RequirementGaps?.Enabled != true || analysis.Gaps.IsDefaultOrEmpty)
            return;

        Span<int> counts = stackalloc int[RequirementGapCauseCount];
        foreach (var gap in analysis.Gaps)
            counts[(int)gap.Cause]++;
        for (var index = 0; index < counts.Length; index++)
        {
            var count = counts[index];
            if (count == 0)
                continue;
            TagList tags = default;
            tags.Add(
                RelationQueryTelemetry.GapCauseTagName,
                RelationQueryTelemetry.GetTagValue((RelationRequirementGapCause)index));
            try
            {
                RequirementGaps.Add(count, tags);
            }
            catch (Exception exception) when (RelationQueryTelemetry.IsRecoverableObservabilityFailure(exception))
            {
                // Measurement observers are synchronous and cannot alter interpretation semantics.
            }
        }
    }
}
