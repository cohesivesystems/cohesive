namespace Cohesive.Host.Composition;

/// <summary>Shared offline explain switch handling; native hosts receive only their remaining arguments.</summary>
public sealed class ApplicationExplainCommand
{
    readonly bool requested;
    readonly string[] arguments;
    /// <summary>Parses the exact --explain switch without consuming other host configuration.</summary>
    /// <param name="args">Caller-owned command arguments, copied during construction.</param>
    /// <exception cref="ArgumentNullException">Arguments are null.</exception>
    public ApplicationExplainCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        requested = args.Contains("--explain", StringComparer.Ordinal);
        arguments = args.Where(argument => argument != "--explain").ToArray();
    }
    /// <summary>Remaining native host arguments; each caller receives an independent copy.</summary>
    public string[] Arguments => [.. arguments];
    /// <summary>Projects and writes one complete report only when requested. Failures propagate without partial JSON output.</summary>
    /// <param name="output">Caller-owned destination, never disposed by this operation.</param>
    /// <param name="explain">Offline validated report factory; must not start native resources.</param>
    /// <returns>True when a report was written and the host should exit without starting.</returns>
    /// <exception cref="ArgumentNullException">Output or factory is null.</exception>
    /// <remarks>Factory, serialization and writer exceptions propagate to the host.</remarks>
    public bool TryWrite(TextWriter output, Func<ApplicationExplanationDocument> explain)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(explain);
        if (!requested) return false;
        var json = explain().ToJson();
        output.WriteLine(json);
        return true;
    }
}
