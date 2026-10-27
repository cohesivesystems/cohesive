using Aspire.Hosting;
using Cohesive.Application;
using Cohesive.Application.Cli;
using Cohesive.Cli;

namespace Cohesive.Adapters.Aspire.Application;

/// <summary>One offline admission path for native Aspire execution and application explanation.</summary>
public static class AspireApplication
{
    /// <summary>Explains an admitted model or starts the configured native application.</summary>
    /// <param name="args">CLI arguments; use explain -- [native arguments] for inspection.</param>
    /// <param name="configure">Configures native resources and returns their admitted application explanation.
    /// Called once per invocation, never for explain help or pre-cancelled execution. Must not start resources.
    /// Create the report from the same model and association: report creation rejects incomplete or foreign deployments.</param>
    /// <param name="cancellationToken">Cancels admission before activation or native host execution.</param>
    /// <returns>The CLI exit code, or zero after successful native execution.</returns>
    /// <exception cref="ArgumentNullException">Arguments or configuration callback are null.</exception>
    /// <exception cref="OperationCanceledException">The invocation is cancelled before dispatch or during native execution.</exception>
    /// <remarks>Explain failures follow CLI error handling with no partial JSON. Native admission failures propagate
    /// before Build or Run. The helper owns and disposes the built native application; it never starts resources for explain.</remarks>
    public static Task<int> RunAsync(IReadOnlyList<string> args,
        Func<IDistributedApplicationBuilder, ApplicationExplanationDocument> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();
        (IDistributedApplicationBuilder Builder, ApplicationExplanationDocument Report) Prepare(IReadOnlyList<string> nativeArgs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var builder = DistributedApplication.CreateBuilder([.. nativeArgs]);
            var report = configure(builder) ?? throw new InvalidOperationException("Configuration must return an admitted application report.");
            token.ThrowIfCancellationRequested();
            return (builder, report);
        }
        var cli = new CliApplication("Cohesive Aspire application").WithoutEnvironmentVariables()
            .AddApplicationExplanation((nativeArgs, token) => Prepare(nativeArgs, token).Report);
        return cli.RunOrFallbackAsync(args, async (nativeArgs, token) =>
        {
            var (builder, _) = Prepare(nativeArgs, token);
            await using var application = builder.Build();
            await application.RunAsync(token).ConfigureAwait(false);
            return 0;
        }, cancellationToken);
    }
}
