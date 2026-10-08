using System.Diagnostics;

namespace Cohesive.Storage.Processes;

/// <summary>Host-scoped private operator evidence emitted before a storage failure is sanitized for a process.</summary>
/// <param name="TraceContext">Invocation trace for correlating a protected log or trace.</param>
/// <param name="Result">Original repository failure including identities, concurrency evidence and provider diagnostics.</param>
/// <remarks>Delivered only to subscribers holding the adapter or hosted service instance. No global diagnostic
/// listener, automatic exporter, or persistence receives this payload.</remarks>
public sealed record EntityTransitionFailureDiagnostic(ActivityContext? TraceContext, EntityTransitionOperationResult Result);
