using System.Collections.Frozen;
using Cohesive.Adapters.Sql;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Immutable SQLite binding plan over a shared SQL command template.</summary>
/// <remarks>Construct once and reuse concurrently. Every binding creates independent provider parameters;
/// runtime byte arrays are borrowed until the returned command is disposed, and must not be mutated during use.</remarks>
public sealed class SqliteCommandTemplate
{
    const int StackBindingLimit = 256;
    readonly FrozenDictionary<string, int> runtimePositions;
    readonly string[] placeholders;
    readonly object?[] constants;

    /// <summary>Compiles parameter-name lookup and validates SQLite constants without rebuilding SQL on invocation.</summary>
    /// <param name="template">Trusted shared SQL template produced for the SQLite dialect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    /// <exception cref="ArgumentException">The dialect differs or a constant is outside SQLite's encoded scalar domain.</exception>
    public SqliteCommandTemplate(SqlCommandTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Dialect != SqliteSqlDialect.Instance.Name)
            throw new ArgumentException("A SQLite binding plan requires a SQLite SQL template.", nameof(template));
        Template = template;
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        placeholders = new string[template.Parameters.Length];
        constants = new object?[template.Parameters.Length];
        foreach (var slot in template.Parameters)
        {
            var index = slot.Position - 1;
            placeholders[index] = slot.Placeholder;
            if (slot.Kind == SqlParameterBindingKind.Runtime)
                positions.Add(slot.Binding!, index);
            else
            {
                var value = slot.ConstantValue;
                ValidateValue(value);
                constants[index] = value;
            }
        }
        runtimePositions = positions.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Canonical shared construction artifact underlying this provider binding plan.</summary>
    public SqlCommandTemplate Template { get; }

    internal SqliteCommand Bind(SqliteDatabase database, SqliteConnection connection, SqliteTransaction? transaction,
        ReadOnlySpan<(string Binding, object? Value)> values)
    {
        if (values.Length != runtimePositions.Count)
            throw new ArgumentException($"Expected {runtimePositions.Count} SQLite runtime bindings; received {values.Length}.", nameof(values));
        var command = database.CreateCommand(connection, transaction, Template.Text);
        try
        {
            foreach (var slot in Template.Parameters)
            {
                var index = slot.Position - 1;
                var constant = constants[index];
                // Captured mutable values must not escape into caller-owned provider parameters.
                command.Parameters.AddWithValue(placeholders[index],
                    constant is byte[] bytes ? bytes.ToArray() : constant ?? DBNull.Value);
            }
            Span<bool> assigned = placeholders.Length <= StackBindingLimit ? stackalloc bool[placeholders.Length] : new bool[placeholders.Length];
            assigned.Clear();
            foreach (var (binding, value) in values)
            {
                if (binding is null || !runtimePositions.TryGetValue(binding, out var index))
                    throw new ArgumentException($"Unknown SQLite runtime binding '{binding}'.", nameof(values));
                if (assigned[index])
                    throw new ArgumentException($"Repeated SQLite runtime binding '{binding}'.", nameof(values));
                ValidateValue(value);
                assigned[index] = true;
                command.Parameters[index].Value = value ?? DBNull.Value;
            }
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    static void ValidateValue(object? value)
    {
        SqliteSqlDialect.Instance.ValidateParameter(value);
        if (value is string text) SqliteScalarCodec.RequireText(text);
    }
}
