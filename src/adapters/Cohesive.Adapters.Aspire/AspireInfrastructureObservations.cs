using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Local;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cohesive.Adapters.Aspire;

/// <summary>Captures and normalizes current Aspire runtime evidence for canonical infrastructure assessment.</summary>
public static class AspireInfrastructureObservations
{
    const string Stage = "aspire-infrastructure-observation";

    /// <summary>Stable diagnostics emitted while normalizing Aspire runtime evidence.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>The observed Aspire resource does not retain the exact projected infrastructure identity.</summary>
        public const string IdentityMismatch = "infra.aspire.observation.identityMismatch";

        /// <summary>The resource is in a known Aspire state that cannot currently admit work.</summary>
        public const string ResourceNotReady = "infra.aspire.observation.notReady";

        /// <summary>The running resource's Aspire health evidence does not establish readiness.</summary>
        public const string HealthNotReady = "infra.aspire.observation.healthNotReady";

        /// <summary>The Aspire snapshot has no resource state.</summary>
        public const string StateMissing = "infra.aspire.observation.stateMissing";

        /// <summary>The Aspire snapshot uses a state whose readiness semantics are not known to this adapter.</summary>
        public const string StateUnsupported = "infra.aspire.observation.stateUnsupported";
    }

    /// <summary>Captures current observations for the exact services projected into one Aspire application.</summary>
    /// <param name="application">Applied Aspire application fenced to an exact local infrastructure projection.</param>
    /// <param name="notifications">Aspire's authoritative current resource-notification service.</param>
    /// <param name="observedAtUtc">Explicit UTC time assigned to this capture.</param>
    /// <returns>
    /// Current attributable observations in physical-resource order. A projected service with no published Aspire
    /// event is absent so the provider-neutral evaluator can report its canonical missing-observation diagnostic.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> or <paramref name="notifications"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public static ImmutableArray<InfrastructureResourceObservation> CaptureCurrent(
        AspireLocalApplication application,
        ResourceNotificationService notifications,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(notifications);
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Aspire infrastructure observations must use UTC.", nameof(observedAtUtc));

        var observations = ImmutableArray.CreateBuilder<InfrastructureResourceObservation>(application.Services.Count);
        foreach (var projectedService in application.Projection.Services)
        {
            var physicalResource = projectedService.Service.PhysicalResource;
            var service = application.Services[physicalResource];
            if (!notifications.TryGetCurrentState(service.Resource.Name, out var resourceEvent))
                continue;

            observations.Add(Project(
                application,
                physicalResource,
                service.Resource,
                resourceEvent,
                observedAtUtc));
        }
        return observations.Count == observations.Capacity
            ? observations.MoveToImmutable()
            : observations.ToImmutable();
    }

    /// <summary>Captures current Aspire observations and assesses them against the exact canonical realization.</summary>
    /// <param name="application">Applied Aspire application fenced to an exact local infrastructure projection.</param>
    /// <param name="notifications">Aspire's authoritative current resource-notification service.</param>
    /// <param name="realization">Exact physical realization that produced the Aspire projection.</param>
    /// <param name="observedAtUtc">Explicit UTC time assigned to this capture.</param>
    /// <returns>A canonical readiness assessment containing the captured Aspire observations and derived decisions.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="realization"/> does not match the projection fence, or <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    public static InfrastructureReadinessAssessment AssessCurrent(
        AspireLocalApplication application,
        ResourceNotificationService notifications,
        InfrastructureRealization realization,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(realization);
        if (application.Projection.SourceRealization != realization.ToReference())
            throw new ArgumentException("The Aspire projection and readiness realization fingerprints do not match.", nameof(realization));

        return InfrastructureReadinessEvaluator.Assess(
            realization,
            CaptureCurrent(application, notifications, observedAtUtc));
    }

    static InfrastructureResourceObservation Project(
        AspireLocalApplication application,
        InfrastructurePhysicalResourceId physicalResource,
        IResource projectedResource,
        ResourceEvent resourceEvent,
        DateTimeOffset observedAtUtc)
    {
        var sourceReferences = AspireSourceReferences.Observation(
            application.Projection,
            resourceEvent.ResourceId);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var identityIsValid = ValidateIdentity(
            application,
            physicalResource,
            projectedResource,
            resourceEvent,
            sourceReferences,
            diagnostics);
        var (health, readiness) = identityIsValid
            ? NormalizeState(physicalResource, resourceEvent.Snapshot, sourceReferences, diagnostics)
            : (ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown);

        return new(
            physicalResource,
            health,
            readiness,
            observedAtUtc,
            sourceReferences,
            [.. diagnostics]);
    }

    static bool ValidateIdentity(
        AspireLocalApplication application,
        InfrastructurePhysicalResourceId physicalResource,
        IResource projectedResource,
        ResourceEvent resourceEvent,
        ImmutableArray<SourceReference> sourceReferences,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var annotations = resourceEvent.Resource.Annotations.OfType<AspireInfraIdentityAnnotation>().ToArray();
        var identity = annotations.Length == 1 ? annotations[0] : null;
        var valid = ReferenceEquals(projectedResource, resourceEvent.Resource)
                    && identity?.PhysicalResource == physicalResource
                    && identity.LocalRealization == application.Projection.LocalRealization
                    && identity.Projection == application.Projection.Fingerprint;
        if (valid)
            return true;

        diagnostics.Add(Diagnostic(
            DiagnosticCodes.IdentityMismatch,
            $"Aspire resource '{resourceEvent.ResourceId}' does not retain the exact projected identity for '{physicalResource.Value}'.",
            physicalResource,
            sourceReferences,
            ["Rebuild the Aspire application from the exact local projection before collecting observations."],
            expected: $"resource={projectedResource.Name}; physical={physicalResource.Value}; local={application.Projection.LocalRealization.Value}; projection={application.Projection.Fingerprint.Value}",
            observed: identity is null
                ? $"resource={resourceEvent.Resource.Name}; identityAnnotations={annotations.Length}"
                : $"resource={resourceEvent.Resource.Name}; physical={identity.PhysicalResource?.Value ?? "none"}; local={identity.LocalRealization.Value}; projection={identity.Projection.Value}"));
        return false;
    }

    static (ExecutionHealthStatus Health, ExecutionReadinessStatus Readiness) NormalizeState(
        InfrastructurePhysicalResourceId physicalResource,
        CustomResourceSnapshot snapshot,
        ImmutableArray<SourceReference> sourceReferences,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var state = snapshot.State?.Text;
        if (string.Equals(state, KnownResourceStates.Running, StringComparison.Ordinal))
            return NormalizeRunningHealth(physicalResource, snapshot, sourceReferences, diagnostics);

        if (IsKnownNotReadyState(state))
        {
            var health = string.Equals(state, KnownResourceStates.FailedToStart, StringComparison.Ordinal)
                         || string.Equals(state, KnownResourceStates.RuntimeUnhealthy, StringComparison.Ordinal)
                ? ExecutionHealthStatus.Unhealthy
                : ExecutionHealthStatus.Unknown;
            diagnostics.Add(Diagnostic(
                DiagnosticCodes.ResourceNotReady,
                $"Aspire resource '{physicalResource.Value}' is in state '{state}' and cannot currently admit work.",
                physicalResource,
                sourceReferences,
                ["Inspect the Aspire resource and restore it to the Running state with Healthy health evidence."],
                expected: $"state={KnownResourceStates.Running}; health={HealthStatus.Healthy}",
                observed: $"state={state}; health={snapshot.HealthStatus?.ToString() ?? "none"}"));
            return (health, ExecutionReadinessStatus.NotReady);
        }

        var missing = string.IsNullOrWhiteSpace(state);
        diagnostics.Add(Diagnostic(
            missing ? DiagnosticCodes.StateMissing : DiagnosticCodes.StateUnsupported,
            missing
                ? $"Aspire resource '{physicalResource.Value}' has no current lifecycle state."
                : $"Aspire resource '{physicalResource.Value}' reported unsupported state '{state}'.",
            physicalResource,
            sourceReferences,
            ["Wait for Aspire to publish a recognized resource state or update the adapter for an intentional custom state."],
            expected: "a recognized Aspire lifecycle state with established readiness semantics",
            observed: missing ? "no state" : state!,
            severity: DiagnosticSeverity.Warning));
        return (ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown);
    }

    static (ExecutionHealthStatus Health, ExecutionReadinessStatus Readiness) NormalizeRunningHealth(
        InfrastructurePhysicalResourceId physicalResource,
        CustomResourceSnapshot snapshot,
        ImmutableArray<SourceReference> sourceReferences,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var health = snapshot.HealthStatus switch
        {
            HealthStatus.Healthy => ExecutionHealthStatus.Healthy,
            HealthStatus.Degraded => ExecutionHealthStatus.Degraded,
            HealthStatus.Unhealthy => ExecutionHealthStatus.Unhealthy,
            null => ExecutionHealthStatus.Unknown,
            _ => throw new InvalidOperationException($"Unsupported Aspire health status '{snapshot.HealthStatus}'.")
        };
        if (health == ExecutionHealthStatus.Healthy)
            return (health, ExecutionReadinessStatus.Ready);

        var knownHealth = health != ExecutionHealthStatus.Unknown;
        diagnostics.Add(Diagnostic(
            DiagnosticCodes.HealthNotReady,
            knownHealth
                ? $"Running Aspire resource '{physicalResource.Value}' is {snapshot.HealthStatus} and does not satisfy Aspire readiness."
                : $"Running Aspire resource '{physicalResource.Value}' has no authoritative health result.",
            physicalResource,
            sourceReferences,
            ["Inspect the cited Aspire health reports and restore every registered check to Healthy."],
            expected: $"state={KnownResourceStates.Running}; health={HealthStatus.Healthy}",
            observed: $"state={KnownResourceStates.Running}; health={snapshot.HealthStatus?.ToString() ?? "none"}; reports={HealthReports(snapshot)}"));
        return (health, knownHealth ? ExecutionReadinessStatus.NotReady : ExecutionReadinessStatus.Unknown);
    }

    static bool IsKnownNotReadyState(string? state) =>
        string.Equals(state, KnownResourceStates.Starting, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.FailedToStart, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.RuntimeUnhealthy, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.Stopping, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.Exited, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.Finished, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.Waiting, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.NotStarted, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.Building, StringComparison.Ordinal)
        || string.Equals(state, KnownResourceStates.ValueMissing, StringComparison.Ordinal);

    static string HealthReports(CustomResourceSnapshot snapshot) => snapshot.HealthReports.IsEmpty
        ? "none"
        : string.Join(
            ",",
            snapshot.HealthReports
                .OrderBy(static report => report.Name, StringComparer.Ordinal)
                .Select(static report => $"{report.Name}={report.Status?.ToString() ?? "Unknown"}"));

    static DocumentValidationDiagnostic Diagnostic(
        string code,
        string message,
        InfrastructurePhysicalResourceId physicalResource,
        ImmutableArray<SourceReference> sourceReferences,
        ImmutableArray<string> resolutionOptions,
        string expected,
        string observed,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) => new(
        Code: code,
        Severity: severity,
        Message: message,
        Location: $"/resources/{physicalResource.Value}",
        SchemaLocation: physicalResource.Value,
        Evidence: new(
            stage: Stage,
            subject: physicalResource.Value,
            sourceReferences: sourceReferences.Select(static reference => reference.Value).ToImmutableArray(),
            resolutionOptions: resolutionOptions,
            expected: expected,
            observed: observed));
}

static class AspireSourceReferences
{
    internal static readonly SourceReference Target = SourceReference.Create(
        "aspire",
        AspireLocalProjectionDocument.CurrentAspireVersion);

    internal static SourceReference LocalRealization(InfrastructureLocalRealizationFingerprint fingerprint) =>
        SourceReference.Create("local-realization", fingerprint.Value);

    internal static SourceReference Projection(AspireLocalProjectionFingerprint fingerprint) =>
        SourceReference.Create("aspire-local-projection", fingerprint.Value);

    internal static SourceReference Resource(string resourceId) =>
        SourceReference.Create("aspire-resource", resourceId);

    internal static ImmutableArray<SourceReference> Observation(
        AspireLocalProjectionDocument projection,
        string resourceId) => SourceReference.NormalizeSet(
        [
            Target,
            LocalRealization(projection.LocalRealization),
            Projection(projection.Fingerprint),
            Resource(resourceId)
        ],
        requireNonEmpty: true);
}
