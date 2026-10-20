using Npgsql;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceBindings
{
    Func<IServiceProvider, NpgsqlDataSource, CancellationToken, Task>? prepareSchema;
    bool schemaSelected;
    Action<PostgresEntitySchemaBuilder>? configureSchema;
    PostgresSchemaInitialization schemaInitialization;
    PostgresEntitySchema? productSchema;

    /// <summary>Projected schema, or null when native schema ownership was selected instead.</summary>
    public PostgresEntitySchema? ProductSchema { get { _ = state.Value; return productSchema; } }

    /// <summary>Projects product DDL and validation from the exact entity mappings at definition completion.</summary>
    /// <param name="initialization">Validate only by default; explicit creation never updates existing tables.</param>
    /// <param name="configure">Optional native collation and explicit relationship enforcement.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Policy is unknown.</exception>
    /// <exception cref="InvalidOperationException">Already selected or frozen.</exception>
    public PostgresPersistenceBindings Schema(PostgresSchemaInitialization initialization = PostgresSchemaInitialization.ValidateOnly,
        Action<PostgresEntitySchemaBuilder>? configure = null)
    {
        if (!Enum.IsDefined(initialization)) throw new ArgumentOutOfRangeException(nameof(initialization));
        return state.Edit(() =>
        {
            if (schemaSelected) throw new InvalidOperationException("Product schema projection is already selected.");
            schemaSelected = true; schemaInitialization = initialization; configureSchema = configure;
            return this;
        });
    }

    /// <summary>Declares native additions or validation after projected-schema admission; cannot repair projected drift.</summary>
    /// <param name="prepare">Setup callback borrowing the DI-owned data source. It owns its commands and
    /// transactions, must honor cancellation, and must explicitly implement its native additions or validation policy.</param>
    /// <returns>This authoring builder.</returns>
    /// <remarks>Declaring the callback performs no IO. Without a callback no additional native work is implied.
    /// Projected drift rejects admission before this callback. Apply drift-repair migrations separately before
    /// preparation. This callback is for additions such as indexes/defaults, not projected-contract migrations. Host preparation runs once, retains failures and publishes no routes on failure.
    /// Explicit asynchronous preparation runs the callback again; synchronous Bind performs no IO.
    /// Coordinate custom multi-host migrations externally; built-in creation uses a transaction advisory lock.</remarks>
    /// <exception cref="ArgumentNullException">Callback is null.</exception>
    /// <exception cref="InvalidOperationException">Already declared or frozen.</exception>
    public PostgresPersistenceBindings SchemaPreparation(Func<NpgsqlDataSource, CancellationToken, Task> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return SchemaPreparation((_, database, token) => prepare(database, token));
    }

    /// <summary>Registers a post-admission native callback borrowing host services such as logging or configuration.</summary>
    /// <param name="prepare">Host-scoped additions/validation callback; projected drift must be migrated before preparation.</param>
    /// <returns>This declaration.</returns>
    public PostgresPersistenceBindings SchemaPreparation(Func<IServiceProvider, NpgsqlDataSource, CancellationToken, Task> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return state.Edit(() =>
        {
            if (prepareSchema is not null) throw new InvalidOperationException("Product schema preparation is already declared.");
            prepareSchema = prepare;
            return this;
        });
    }

    /// <summary>Declares atomic embedded SQL under the shared schema advisory lock; verifies the resource at definition.</summary>
    /// <param name="assembly">Assembly owning the SQL resource.</param><param name="resourceName">Exact manifest resource name.</param>
    /// <returns>This declaration.</returns>
    /// <remarks>SQL must participate in the supplied transaction (no explicit transaction control or
    /// nontransactional operations). Use the callback overload for custom native-addition protocols.</remarks>
    /// <exception cref="ArgumentException">Resource is missing.</exception>
    public PostgresPersistenceBindings SchemaPreparation(System.Reflection.Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (assembly.GetManifestResourceInfo(resourceName) is null)
            throw new ArgumentException($"Embedded schema resource '{resourceName}' does not exist in {assembly.GetName().Name}.", nameof(resourceName));
        return SchemaPreparation(async (database, token) =>
        {
            using var stream = new StreamReader(assembly.GetManifestResourceStream(resourceName)!);
            await PostgresSchemaPreparation.CreateAsync(database,
                await stream.ReadToEndAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        });
    }

    /// <summary>Prepares without host services, using the same callback lifecycle and an empty provider.</summary>
    /// <param name="database">Borrowed native source.</param><param name="cancellationToken">Preparation cancellation.</param>
    /// <returns>Completion after schema admission.</returns>
    /// <remarks>Callbacks requiring services should use the provider-taking overload.</remarks>
    public Task PrepareSchemaAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default) =>
        PrepareSchemaAsync(EmptyServices.Instance, database, cancellationToken);

    sealed class EmptyServices : IServiceProvider
    {
        internal static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Creates/validates the projected schema, then runs explicit native preparation and revalidates.</summary>
    /// <param name="database">Borrowed native data source; never disposed by this operation.</param>
    /// <param name="cancellationToken">Cancellation before and during preparation.</param>
    /// <param name="services">Host services when the selected native callback requires them.</param>
    /// <returns>Completion after projected and native schema preparation, or no work when neither is declared.</returns>
    /// <remarks>Callback exceptions propagate unchanged. Partial native effects are governed by the native
    /// callback's transaction policy; no retry or rollback emulation is inferred. A selected projection is
    /// validated again after native additions so they cannot invalidate its contract.</remarks>
    /// <exception cref="ArgumentNullException">Database or services is null.</exception>
    /// <exception cref="OperationCanceledException">Preparation is canceled.</exception>
    public async Task PrepareSchemaAsync(IServiceProvider services, NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var missing = await AdmitAsync(database, cancellationToken).ConfigureAwait(false);
        await PrepareAdmittedSchemaAsync(services, database, missing, cancellationToken).ConfigureAwait(false);
    }

    // Admission belongs to the bindings, and is repeated per explicit preparation, never cached across startups.
    async Task<int> AdmitAsync(NpgsqlDataSource database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        _ = state.Value;
        return productSchema is { } schema
            ? await schema.ValidateAsync(database, schemaInitialization, cancellationToken).ConfigureAwait(false) : 0;
    }

    async Task PrepareAdmittedSchemaAsync(IServiceProvider services, NpgsqlDataSource database, int missing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (productSchema is { } schema)
        {
            if (missing > 0)
            {
                await PostgresSchemaPreparation.CreateAsync(database, schema.CreateSql, cancellationToken).ConfigureAwait(false);
                await schema.ValidateAsync(database, cancellationToken).ConfigureAwait(false);
            }
        }
        if (prepareSchema is { } prepare)
        {
            await prepare(services, database, cancellationToken).ConfigureAwait(false);
            if (productSchema is { } projected) await projected.ValidateAsync(database, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Admits existing product tables before preparing history, schema, receipts and outbox.</summary>
    /// <param name="services">Host services supplying adapter-owned native history clients.</param>
    /// <param name="database">Borrowed native data source; ownership remains with the caller.</param>
    /// <param name="authority">Authority for the resulting repository registration.</param>
    /// <param name="cancellationToken">Cancellation of admission and preparation.</param>
    /// <returns>Prepared registration and declared attachments, without ownership of native clients.</returns>
    /// <remarks>Admission and preparation are scoped to this invocation. ValidateOnly uses one product catalog
    /// batch; creation and custom callbacks require post-validation. Preparation is not a transaction across
    /// attachments: failures after admission can retain completed DDL. No retries are implied. Concurrent
    /// external schema migrations must be coordinated by the caller.</remarks>
    /// <exception cref="ArgumentNullException">Services or database is null.</exception>
    /// <exception cref="ArgumentException">Authority is empty or a native history client does not match this source.</exception>
    /// <exception cref="PostgresSchemaValidationException">An existing product contract requires migration.</exception>
    /// <exception cref="NpgsqlException">Database admission or initialization fails.</exception>
    /// <exception cref="OperationCanceledException">Preparation is canceled.</exception>
    public async Task<PostgresPreparedPersistence> PrepareAsync(IServiceProvider services, NpgsqlDataSource database,
        string authority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        var missing = await AdmitAsync(database, cancellationToken).ConfigureAwait(false);
        var history = HistoryConfiguration is { } configuration
            ? await configuration.PrepareAsync(services, Database.Value, database, cancellationToken).ConfigureAwait(false) : null;
        await PrepareAdmittedSchemaAsync(services, database, missing, cancellationToken).ConfigureAwait(false);
        var receipts = await PrepareReceiptsAsync(database, cancellationToken).ConfigureAwait(false);
        var outbox = await PrepareOutboxAsync(database, cancellationToken).ConfigureAwait(false);
        return new(Bind(database, authority), receipts, history, outbox);
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
    /// <exception cref="ArgumentNullException">Database or services is null.</exception>
    /// <exception cref="InvalidOperationException">Schema requires explicit migration.</exception>
    /// <exception cref="NpgsqlException">Native initialization or validation fails.</exception>
    public async Task<PostgresEntityOutboxStorage?> PrepareOutboxAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = state.Value;
        return await PostgresSchemaPreparation.PrepareAsync(database, OutboxOptions, OutboxInitialization,
            static options => options.SchemaSql, static (options, source, policy, token) => options.AdmitAsync(source, policy, token), cancellationToken).ConfigureAwait(false);
    }
}
