namespace Cohesive.Cli;

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

    /// <summary>Gets the success.</summary>
    public static CliValidationResult Success { get; } = new([]);

    /// <summary>Gets the errors.</summary>
    public IReadOnlyList<string> Errors => errors;

    /// <summary>Gets whether validation succeeded.</summary>
    public bool IsValid => errors.Count == 0;

    /// <summary>Creates a failed validation result from error messages.</summary>
    public static CliValidationResult Failure(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Failure((IEnumerable<string>)errors);
    }

    /// <summary>Creates a failed validation result from error messages.</summary>
    public static CliValidationResult Failure(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new([..errors.WhereNotNullOrWhiteSpace().Select(static error => error.Trim())]);
    }
}
