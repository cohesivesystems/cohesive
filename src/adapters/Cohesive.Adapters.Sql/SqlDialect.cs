namespace Cohesive.Adapters.Sql;

/// <summary>SQL grammar facilities whose availability is decided by the concrete adapter.</summary>
public enum SqlFeature
{
    /// <summary>Comparison against elements of a native SQL array.</summary>
    ArrayAny,
    /// <summary>Native array expansion as a table source.</summary>
    ArrayUnnest,
    /// <summary>Correlated lateral table sources.</summary>
    Lateral,
    /// <summary>Rows returned by a mutation.</summary>
    Returning,
    /// <summary>Conflict-target insert behavior.</summary>
    OnConflict,
    /// <summary>Aggregate-local filtering.</summary>
    AggregateFilter,
    /// <summary>Offset pagination without a limit clause.</summary>
    OffsetWithoutLimit
}

/// <summary>Adapter-owned policy for rendering and binding shared SQL construction artifacts.</summary>
/// <remarks>Instances must be immutable and deterministic. This is target construction policy, not portable query semantics.</remarks>
public abstract class SqlDialect
{
    /// <summary>Initializes an adapter-owned SQL policy.</summary>
    protected SqlDialect() { }
    /// <summary>Stable identity of the dialect and its supported construction profile.</summary>
    public abstract string Name { get; }
    /// <summary>Validates an identifier against target-specific constraints.</summary>
    /// <param name="identifier">One already Unicode-validated, unquoted identifier.</param>
    /// <exception cref="ArgumentException">The identifier cannot be represented exactly by this target.</exception>
    public abstract void ValidateIdentifier(SqlIdentifier identifier);
    /// <summary>Checks that a captured or bound value preserves the target's exact parameter domain.</summary>
    /// <param name="value">Immutable normalized CLR value or null.</param>
    /// <exception cref="ArgumentException">The value is outside the supported target domain.</exception>
    public abstract void ValidateParameter(object? value);
    /// <summary>Resolves a scalar function to a target token.</summary>
    /// <param name="function">Requested function.</param>
    /// <returns>A trusted SQL function token.</returns>
    /// <exception cref="SqlConstructionException">The function is unavailable in this profile.</exception>
    public abstract string FunctionName(SqlFunction function);
    /// <summary>Resolves an aggregate function to a target token.</summary>
    /// <param name="function">Requested aggregate.</param>
    /// <returns>A trusted SQL aggregate token.</returns>
    /// <exception cref="SqlConstructionException">The aggregate is unavailable in this profile.</exception>
    public abstract string FunctionName(SqlAggregateFunction function);
    /// <summary>Requires one grammar facility before emitting it.</summary>
    /// <param name="feature">Requested target grammar facility.</param>
    /// <exception cref="SqlConstructionException">The facility is unavailable in this profile.</exception>
    public abstract void Require(SqlFeature feature);
}

/// <summary>Structured failure when a SQL construction cannot preserve its requested target contract.</summary>
public sealed class SqlConstructionException : NotSupportedException
{
    /// <summary>Creates an unsupported-construction diagnostic.</summary>
    /// <param name="dialect">Target profile identity.</param>
    /// <param name="construct">Unsupported grammar or function identity.</param>
    /// <param name="resolution">Action that makes the construction supported.</param>
    /// <exception cref="ArgumentNullException">A diagnostic field is null.</exception>
    /// <exception cref="ArgumentException">A diagnostic field is empty or white space.</exception>
    public SqlConstructionException(string dialect, string construct, string resolution)
        : base($"SQL dialect '{dialect}' does not support '{construct}'. {resolution}")
    {
        Dialect = Guard.RequireNotNullOrWhiteSpace(dialect);
        Construct = Guard.RequireNotNullOrWhiteSpace(construct);
        Resolution = Guard.RequireNotNullOrWhiteSpace(resolution);
    }
    /// <summary>Stable diagnostic code.</summary>
    public string Code => "sql.unsupported-construct";
    /// <summary>Target profile identity.</summary>
    public string Dialect { get; }
    /// <summary>Unavailable grammar or function identity.</summary>
    public string Construct { get; }
    /// <summary>Actionable target selection or lowering guidance.</summary>
    public string Resolution { get; }
}
