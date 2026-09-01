namespace Cohesive.Cli.Testing;

/// <summary>
/// Helpers for invoking CLI applications in tests with captured output channels.
/// </summary>
public static class CliApplicationTestHarness
{
    /// <summary>Invokes a CLI application and captures its outputs.</summary>
    public static async Task<CliInvocationCapture> InvokeAsync(CliApplication app, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        using var standardOutput = new StringWriter();
        using var errorOutput = new StringWriter();
        var exitCode = await app.InvokeAsync(
            args,
            new()
            {
                StandardOutput = standardOutput,
                ErrorOutput = errorOutput
            },
            ct);
        return new(ExitCode: exitCode, standardOutput.ToString(), errorOutput.ToString());
    }
}

/// <summary>
/// Result captured from a test invocation of a CLI application.
/// </summary>
public sealed record CliInvocationCapture(
    int ExitCode,
    string StandardOutput,
    string ErrorOutput
    );
