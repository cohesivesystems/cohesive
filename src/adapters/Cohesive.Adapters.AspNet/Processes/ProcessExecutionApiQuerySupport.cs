using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Small helpers for process execution query endpoints.
/// </summary>
public static class ProcessExecutionApiQuerySupport
{
    /// <summary>
    /// Parses process execution statuses, returning the supplied defaults when the request did not include statuses.
    /// </summary>
    public static IReadOnlyCollection<ProcessExecutionStatus> ResolveStatuses(
        IReadOnlyCollection<string>? statuses,
        IReadOnlyCollection<ProcessExecutionStatus> defaultStatuses)
    {
        ArgumentNullException.ThrowIfNull(defaultStatuses);
        if (statuses is null || statuses.Count == 0)
            return defaultStatuses;

        var resolved = new List<ProcessExecutionStatus>(statuses.Count);
        foreach (var status in statuses)
        {
            if (!Enum.TryParse<ProcessExecutionStatus>(status, ignoreCase: true, out var parsed))
                throw new BadHttpRequestException($"Unsupported process execution status '{status}'.");

            resolved.Add(parsed);
        }

        return resolved;
    }
}
