using Cohesive.Prelude;

namespace Cohesive.Adapters.Postgres;

/// <summary>Native product or attachment schema drift requiring explicit migration before admission.</summary>
public sealed class PostgresSchemaValidationException : PreparationException
{
    internal PostgresSchemaValidationException(string code, string table, string message, string? column = null)
        : base("schema", code, message) { Table = table; Column = column; }
    /// <summary>Quoted schema-qualified native table affected by drift.</summary>
    public string Table { get; }
    /// <summary>Native column or reference when the failure identifies one.</summary>
    public string? Column { get; }
}
