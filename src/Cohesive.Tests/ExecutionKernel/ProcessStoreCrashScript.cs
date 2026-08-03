using System.Collections.Immutable;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

internal readonly record struct ProcessStoreCrashPoint(
    ProcessStoreMutationKind MutationKind,
    ProcessStoreCrashPhase Phase,
    int Occurrence = 1)
{
    internal void Validate()
    {
        if (Occurrence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Occurrence), "A crash-point occurrence must be positive.");
        }
    }
}

internal sealed class ProcessStoreCrashScript(params ProcessStoreCrashPoint[] points)
{
    readonly ImmutableArray<ProcessStoreCrashPoint> points = Validate(points);
    readonly List<ProcessStoreCrashContext> observations = [];
    readonly List<ProcessStoreCrashContext> crashes = [];
    int matchingOccurrences;
    int nextPoint;

    internal ImmutableArray<ProcessStoreCrashContext> Observations => [.. observations];

    internal ImmutableArray<ProcessStoreCrashContext> Crashes => [.. crashes];

    internal bool IsComplete => nextPoint == points.Length;

    internal static ProcessStoreCrashScript Once(
        ProcessStoreMutationKind mutationKind,
        ProcessStoreCrashPhase phase,
        int occurrence = 1) =>
        new([new(mutationKind, phase, occurrence)]);

    internal bool ShouldCrash(ProcessStoreCrashContext context)
    {
        observations.Add(context);
        if (IsComplete)
        {
            return false;
        }

        var point = points[nextPoint];
        if (context.MutationKind != point.MutationKind || context.Phase != point.Phase)
        {
            return false;
        }

        matchingOccurrences++;
        if (matchingOccurrences != point.Occurrence)
        {
            return false;
        }

        crashes.Add(context);
        matchingOccurrences = 0;
        nextPoint++;
        return true;
    }

    static ImmutableArray<ProcessStoreCrashPoint> Validate(ProcessStoreCrashPoint[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("A crash script requires at least one point.", nameof(values));
        }

        foreach (var value in values)
        {
            value.Validate();
        }
        return [.. values];
    }
}
