namespace Cohesive.Host.Cli;

/// <summary>
/// Delegate used by the CLI validation pipeline.
/// </summary>
/// <param name="context">Current invocation context.</param>
/// <returns>A validation result for the invocation.</returns>
public delegate Task<CliValidationResult> CliCommandValidationDelegate(CliCommandContext context);

/// <summary>
/// Validation result returned by CLI validators.
/// </summary>
public sealed class CliValidationResult
{
    readonly IReadOnlyList<string> errors;

    CliValidationResult(IReadOnlyList<string> errors) => this.errors = errors;

    public static CliValidationResult Success { get; } = new([]);

    public IReadOnlyList<string> Errors => errors;

    public bool IsValid => errors.Count == 0;

    public static CliValidationResult Failure(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Failure((IEnumerable<string>)errors);
    }

    public static CliValidationResult Failure(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new([..errors.WhereNotNullOrWhiteSpace().Select(static error => error.Trim())]);
    }
}
