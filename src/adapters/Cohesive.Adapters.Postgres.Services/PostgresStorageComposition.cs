using System.Collections.Immutable;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Api.Execution.Services;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>One native database's endpoint preparation and model-owned persistence policy.</summary>
public sealed class PostgresStorageComposition : IServiceStorageComposition
{
    readonly PostgresPersistenceBindings bindings;
    /// <summary>Prepares native evidence from exact endpoint selections without opening a connection.</summary>
    /// <param name="bindings">Declared mappings, receipt, history and outbox policy.</param>
    /// <param name="plan">Canonical endpoint query requirements.</param>
    public PostgresStorageComposition(PostgresPersistenceBindings bindings, ServiceImplementationPlan plan)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        Evidence = bindings.PrepareService(plan, new InfrastructureTargetFacilityId("postgres/" + bindings.Database.Value + "/implementation"));
    }
    /// <summary>Native evidence shared with infrastructure association.</summary>
    public PostgresPreparedQueryEvidence Evidence { get; }
    /// <inheritdoc />
    public ImmutableArray<object> Bindings => [bindings];
    /// <inheritdoc />
    public ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => BindAsync(services, null, null, cancellationToken);

    /// <summary>Resolves shared native clients and prepares the model's persistence attachments.</summary>
    /// <param name="services">Native data source keyed by the existing database resource name.</param>
    /// <param name="history">Optional prepared history from that same data source.</param>
    /// <param name="outbox">Optional prepared direct-transition outbox from that same data source.</param>
    /// <param name="cancellationToken">Cancels metadata validation.</param>
    /// <returns>Service persistence; native resources remain owned by DI or their external owner.</returns>
    /// <exception cref="InvalidOperationException">An explicit attachment would override model policy, or native services are missing.</exception>
    public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services,
        PostgresEntityHistoryStorage? history, PostgresEntityOutboxStorage? outbox,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (history is not null && bindings.HistoryConfiguration is not null || outbox is not null && bindings.OutboxOptions is not null)
            throw new InvalidOperationException("Explicit attachments cannot override model-owned history or outbox.");
        var database = services.GetRequiredKeyedService<NpgsqlDataSource>(bindings.Database.Value);
        if (bindings.HistoryConfiguration is { } configuration)
            history = await configuration.PrepareAsync(services, bindings.Database.Value, database, cancellationToken).ConfigureAwait(false);
        outbox ??= await bindings.PrepareOutboxAsync(database, cancellationToken).ConfigureAwait(false);
        var receipts = await bindings.PrepareReceiptsAsync(database, cancellationToken).ConfigureAwait(false);
        return bindings.Bind(database, "di/" + bindings.Database.Value).ForServices(receipts, history, outbox);
    }
}
