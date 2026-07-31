using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cohesive.Adapters.Elastic;

/// <summary>Stable tracing and metrics contract for Elasticsearch materialization target operations.</summary>
public static class ElasticMaterializationTelemetry
{
    /// <summary>OpenTelemetry instrumentation scope shared by target activities and meters.</summary>
    public const string InstrumentationName = "Cohesive.Adapters.Elastic.Materialization";

    /// <summary>Activity name used for one target lifecycle or bulk operation.</summary>
    public const string OperationActivityName = "elastic.materialization.operation";

    /// <summary>Counter name for completed target operations.</summary>
    public const string OperationCountName = "cohesive.elastic.materialization.operations";

    /// <summary>Histogram name for target operation duration in milliseconds.</summary>
    public const string OperationDurationName = "cohesive.elastic.materialization.operation.duration";

    /// <summary>Histogram name for canonical input items in one bulk operation.</summary>
    public const string BatchItemsName = "cohesive.elastic.materialization.batch.items";

    /// <summary>Histogram name for canonical input bytes in one bulk operation.</summary>
    public const string BatchBytesName = "cohesive.elastic.materialization.batch.bytes";

    /// <summary>Counter name for terminal per-item outcome dispositions.</summary>
    public const string ItemOutcomeCountName = "cohesive.elastic.materialization.item.outcomes";

    /// <summary>Tag carrying the canonical materialization target identity.</summary>
    public const string TargetIdTagName = "cohesive.materialization.target.id";

    /// <summary>Tag carrying the logical materialization identity.</summary>
    public const string MaterializationIdTagName = "cohesive.materialization.id";

    /// <summary>
    /// Activity tag carrying the canonical generation identity when an operation addresses one. Generation identities
    /// are intentionally excluded from metric dimensions because their cardinality grows with every rebuild.
    /// </summary>
    public const string GenerationIdTagName = "cohesive.materialization.generation.id";

    /// <summary>Tag carrying the stable operation family such as <c>apply-batch</c> or <c>promote</c>.</summary>
    public const string OperationTagName = "cohesive.materialization.operation";

    /// <summary>Tag carrying the canonical Storage operation or batch disposition.</summary>
    public const string DispositionTagName = "cohesive.materialization.disposition";

    /// <summary>Tag carrying one canonical per-item outcome disposition.</summary>
    public const string ItemOutcomeTagName = "cohesive.materialization.item.outcome";

    /// <summary>Tag indicating whether provider or adapter pressure made an outcome retryable.</summary>
    public const string RetryableTagName = "cohesive.materialization.retryable";

    /// <summary>Tag carrying a sanitized stable adapter/provider failure code.</summary>
    public const string FailureCodeTagName = "error.type";

    /// <summary>Tag carrying a trustworthy Elasticsearch HTTP response status when available.</summary>
    public const string HttpStatusCodeTagName = "http.response.status_code";

    /// <summary>Tag carrying the persisted target-binding fingerprint.</summary>
    public const string BindingFingerprintTagName = "cohesive.materialization.binding.fingerprint";

    /// <summary>Tag carrying the exact target capability-profile identity.</summary>
    public const string CapabilityProfileTagName = "cohesive.materialization.capability.profile";

    internal static readonly string? InstrumentationVersion =
        typeof(ElasticMaterializationTelemetry).Assembly.GetName().Version?.ToString();

    internal static readonly ActivitySource Activities = new(InstrumentationName, InstrumentationVersion);
    internal static readonly Meter Meter = new(InstrumentationName, InstrumentationVersion);
    internal static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        OperationCountName,
        unit: "{operation}",
        description: "Completed Elasticsearch materialization target operations.");
    internal static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        OperationDurationName,
        unit: "ms",
        description: "Elapsed Elasticsearch materialization target operation time.");
    internal static readonly Histogram<long> BatchItems = Meter.CreateHistogram<long>(
        BatchItemsName,
        unit: "{item}",
        description: "Canonical item count admitted to one target bulk operation.");
    internal static readonly Histogram<long> BatchBytes = Meter.CreateHistogram<long>(
        BatchBytesName,
        unit: "By",
        description: "Canonical bytes admitted to one target bulk operation.");
    internal static readonly Counter<long> ItemOutcomes = Meter.CreateCounter<long>(
        ItemOutcomeCountName,
        unit: "{item}",
        description: "Terminal Elasticsearch materialization item outcomes, including retryable rejection pressure.");
}
