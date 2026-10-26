using Cohesive.Cli;

namespace Cohesive.Application;

/// <summary>Offline application inspection registered through the shared CLI command machinery.</summary>
public static class ApplicationExplanationCliExtensions
{
    /// <summary>Registers an explain command; normal host startup remains the caller's fallback.</summary>
    /// <param name="cli">Existing command application owning parsing, help, IO and cancellation.</param>
    /// <param name="explain">Constructs and admits an offline model from native host arguments after --.
    /// Must not start a host or native resources. Called only for command execution, never help; native argument validation belongs to the factory.</param>
    /// <returns>The same CLI application.</returns>
    /// <exception cref="ArgumentNullException">Application or factory is null.</exception>
    /// <exception cref="InvalidOperationException">The command identity is already registered.</exception>
    /// <remarks>Invoke as explain -- [native arguments]. The complete report is serialized before output;
    /// factory and serialization failures produce no partial report and follow CLI error handling.</remarks>
    public static CliApplication AddApplicationExplanation(this CliApplication cli,
        Func<IReadOnlyList<string>, CancellationToken, ApplicationExplanationDocument> explain)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(explain);
        var command = cli.Command("explain", description: "Explain offline application bindings and infrastructure; no resources are started.",
            options: new Dictionary<string, CliOption>
            {
                ["host-arguments"] = CliOption.For<string[]>(defaultValue: [],
                    description: "Native host configuration arguments, supplied after --.")
            });
        command.Argument("host-arguments");
        command.OnExecute(context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var report = explain(context.Configuration.Get<string[]>("host-arguments") ?? [], context.CancellationToken);
            var json = report.ToJson();
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Io.WriteLine(json);
            return 0;
        });
        return cli;
    }
}
