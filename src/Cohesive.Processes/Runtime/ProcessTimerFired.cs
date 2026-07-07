namespace Cohesive.Processes.Runtime;

/// <summary>
/// Payload returned by timer waits.
/// </summary>
public sealed record ProcessTimerFired(string Key, DateTimeOffset FiredAtUtc);