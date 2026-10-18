using Npgsql;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceBindings
{
    /// <summary>Optional adapter-owned history declaration; contains no native clients.</summary>
    public IPostgresEntityHistoryConfiguration? HistoryConfiguration { get; private set; }
    /// <summary>Optional outbox table and payload policy.</summary>
    public PostgresEntityOutboxOptions? OutboxOptions { get; private set; }
    /// <summary>Outbox schema admission policy, validation-only by default.</summary>
    public PostgresSchemaInitialization OutboxInitialization { get; private set; }

    /// <summary>Declares history for all repositories in this database.</summary>
    /// <param name="configuration">Adapter-specific immutable policy.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Configuration is null.</exception>
    /// <exception cref="InvalidOperationException">Already declared or frozen.</exception>
    public PostgresPersistenceBindings History(IPostgresEntityHistoryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return state.Edit(() =>
        {
            if (HistoryConfiguration is not null) throw new InvalidOperationException("History is already declared.");
            HistoryConfiguration = configuration;
            return this;
        });
    }

    /// <summary>Declares an atomic transition outbox; delivery remains a separate concern.</summary>
    /// <param name="options">Native tables and payload bound.</param>
    /// <param name="initialization">Explicit schema bootstrap or validation-only admission.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Policy is unknown.</exception>
    /// <exception cref="InvalidOperationException">Already declared or frozen.</exception>
    public PostgresPersistenceBindings Outbox(PostgresEntityOutboxOptions options,
        PostgresSchemaInitialization initialization = PostgresSchemaInitialization.ValidateOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(initialization)) throw new ArgumentOutOfRangeException(nameof(initialization));
        return state.Edit(() =>
        {
            if (OutboxOptions is not null) throw new InvalidOperationException("Outbox is already declared.");
            OutboxOptions = options;
            OutboxInitialization = initialization;
            return this;
        });
    }

    /// <summary>Prepares model-owned outbox tables without taking ownership of the source.</summary>
    /// <param name="database">Borrowed native relational source.</param>
    /// <param name="cancellationToken">Cancellation of DDL and validation.</param>
    /// <returns>Validated attachment, or null when undeclared.</returns>
    /// <exception cref="ArgumentNullException">Database is null.</exception>
    /// <exception cref="InvalidOperationException">Schema requires explicit migration.</exception>
    /// <exception cref="NpgsqlException">Native initialization or validation fails.</exception>
    public async Task<PostgresEntityOutboxStorage?> PrepareOutboxAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = state.Value;
        if (OutboxOptions is not { } options) return null;
        if (OutboxInitialization == PostgresSchemaInitialization.CreateIfMissing)
        {
            await using var create = database.CreateCommand(options.SchemaSql);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return await options.BindAsync(database, cancellationToken).ConfigureAwait(false);
    }
}
