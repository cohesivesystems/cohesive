using Npgsql;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceBindings
{
    Func<NpgsqlDataSource, CancellationToken, Task>? prepareSchema;

    /// <summary>Declares native product-schema validation or migration before persistence admission.</summary>
    /// <param name="prepare">Setup callback borrowing the DI-owned data source. It owns its commands and
    /// transactions, must honor cancellation, and must explicitly implement any migration or validation policy.</param>
    /// <returns>This authoring builder.</returns>
    /// <remarks>Declaring the callback performs no IO. Without a callback no product-schema work is implied.
    /// This is not a schema generator or a create-only guarantee; native SQL or a migration library remains
    /// authoritative. Host preparation runs once, retains failures and publishes no routes on failure.
    /// Direct rebinding runs the callback again. Coordinate multi-host migrations externally.</remarks>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="InvalidOperationException">Already declared or frozen.</exception>
    public PostgresPersistenceBindings SchemaPreparation(Func<NpgsqlDataSource, CancellationToken, Task> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return state.Edit(() =>
        {
            if (prepareSchema is not null) throw new InvalidOperationException("Product schema preparation is already declared.");
            prepareSchema = prepare;
            return this;
        });
    }

    /// <summary>Executes the explicitly declared native product-schema preparation.</summary>
    /// <param name="database">Borrowed native data source; never disposed by this operation.</param>
    /// <param name="cancellationToken">Cancellation before and during preparation.</param>
    /// <returns>The callback completion, or a completed task when no preparation is declared.</returns>
    /// <remarks>Callback exceptions propagate unchanged. Partial migration effects are governed by the native
    /// callback's transaction policy; no retry, rollback emulation or schema validation is inferred.</remarks>
    /// <exception cref="ArgumentNullException">Database is null.</exception>
    /// <exception cref="OperationCanceledException">Preparation is canceled.</exception>
    public Task PrepareSchemaAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        _ = state.Value;
        return prepareSchema?.Invoke(database, cancellationToken) ?? Task.CompletedTask;
    }

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
