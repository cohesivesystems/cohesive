namespace Cohesive.Host.Cli;

/// <summary>
/// Delegate used by CLI command middleware to continue command execution.
/// </summary>
/// <param name="context">Current invocation context.</param>
/// <returns>The command exit code.</returns>
public delegate Task<int> CliCommandExecutionDelegate(CliCommandContext context);
