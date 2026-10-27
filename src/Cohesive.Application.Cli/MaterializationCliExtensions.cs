using System.Globalization;
using System.Text.Json;
using Cohesive.Cli;
using Cohesive.Storage.Materialization;

namespace Cohesive.Application.Cli;

/// <summary>Parsed, stable invocation inputs for a bounded shadow activation.</summary>
/// <param name="Operation">Requested bounded operation; promotion is not available.</param>
/// <param name="Attempt">Attempt identity and original start retained across recovery.</param>
/// <param name="Invocation">Catch-up activation identity retained across retries.</param>
public sealed record MaterializationShadowInvocation(MaterializationShadowOperation Operation,
    MaterializationRebuildAttempt Attempt, MaterializationSynchronizationInvocationId Invocation);

/// <summary>Reusable operator commands over caller-owned native materialization resources.</summary>
public static class MaterializationCliExtensions
{
    /// <summary>Registers explicit initialization and bounded shadow rebuild/catch-up commands.</summary>
    /// <param name="cli">Command application owning parsing, IO and cancellation.</param>
    /// <param name="attemptNamespace">Stable process-instance namespace; preserve it across restarts.</param>
    /// <param name="initialize">Explicit native initialization. Never called by help or execution commands.</param>
    /// <param name="execute">Opens and disposes native resources and executes the parsed activation.</param>
    /// <returns>The same CLI application.</returns>
    /// <exception cref="ArgumentException">A required argument or namespace is invalid.</exception>
    /// <remarks>Commands are shadow-only. No promotion, alias switch or live-read binding is performed.
    /// Malformed invocation arguments fail before the native factory runs. Cancellation follows CliApplication's exit contract.</remarks>
    public static CliApplication AddMaterializationShadowCommands(this CliApplication cli, string attemptNamespace,
        Func<CancellationToken, Task> initialize,
        Func<MaterializationShadowInvocation, CancellationToken, Task<MaterializationSynchronizationRunResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptNamespace);
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(execute);
        cli.Command("initialize", description: "Explicitly initialize native shadow resources.", options: new Dictionary<string, CliOption>()).OnExecute(async context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await initialize(context.CancellationToken).ConfigureAwait(false);
            context.Io.WriteLine("Shadow resources initialized; no generation promoted.");
            return 0;
        });
        foreach (var operation in Enum.GetValues<MaterializationShadowOperation>())
        {
            cli.Command(operation == MaterializationShadowOperation.Rebuild ? "rebuild" : "catch-up",
                options: new Dictionary<string, CliOption>
                {
                    ["attempt"] = CliOption.For<string>(description: "Stable attempt ID retained on resume") with { Required = true },
                    ["started"] = CliOption.For<string>(description: "Original attempt start with explicit UTC offset (ISO 8601)") with { Required = true },
                    ["invocation"] = CliOption.For<string>(description: "Activation ID retained on retry; change for a new cut") with { Required = true }
                }).OnExecute(async context =>
            {
                var id = context.Configuration.Get<string>("attempt")!;
                var start = context.Configuration.Get<string>("started")!;
                if (!(start.EndsWith("Z", StringComparison.Ordinal) || (start.Length >= 6 && start[^3] == ':' && (start[^6] == '+' || start[^6] == '-')))
                    || !DateTimeOffset.TryParseExact(start, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
                    throw new ArgumentException("--started must be an ISO 8601 timestamp with an explicit offset.");
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                var invocation = context.Configuration.Get<string>("invocation")!;
                ArgumentException.ThrowIfNullOrWhiteSpace(invocation);
                context.CancellationToken.ThrowIfCancellationRequested();
                var result = await execute(new(operation,
                    new(new(new(attemptNamespace + "/" + id), new(id)), started), new(invocation)), context.CancellationToken).ConfigureAwait(false);
                context.Io.WriteLine(JsonSerializer.Serialize(new { mode = "shadow", generation = result.Generation.Value, disposition = result.Disposition.ToString(), result.Diagnostics, promotion = "disabled" }));
                return result.Receipt is not null ? 0 : 2;
            });
        }
        return cli;
    }
}
