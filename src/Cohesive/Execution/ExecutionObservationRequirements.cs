namespace Cohesive.Execution;

/// <summary>Shared validation for explicit observations crossing execution-kernel boundaries.</summary>
static class ExecutionObservationRequirements
{
    /// <summary>Requires a UTC timestamp.</summary>
    /// <param name="value">Timestamp to validate.</param>
    /// <param name="parameterName">Public parameter associated with the timestamp.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not expressed in UTC.</exception>
    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Execution observations must be expressed in UTC.", parameterName);
        }
    }
}
