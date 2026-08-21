using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Microsoft.Azure.Cosmos;
using Npgsql;
using NpgsqlTypes;

namespace Cohesive.MaterializationHarness.Seed;

/// <summary>
/// Projects the deterministic scenario suffix into the real PostgreSQL and Cosmos source replicas.
/// The journal remains the semantic authority; this type owns only provider-specific persistence.
/// </summary>
internal static class FreightScenarioMutationProjection
{
    internal const string CosmosChangeDocumentKind = "materialization-scenario-change";
    const string PostgresSchema = "freight_harness";

    internal static async Task ApplyAsync(
        string postgresConnectionString,
        string cosmosConnectionString,
        string cosmosDatabase,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(cosmosConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(cosmosDatabase);
        ArgumentNullException.ThrowIfNull(journal);

        await ApplyPostgresAsync(postgresConnectionString, journal, cancellationToken).ConfigureAwait(false);
        await ApplyCosmosAsync(cosmosConnectionString, cosmosDatabase, journal, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyEvidenceAsync(
        string postgresConnectionString,
        string cosmosConnectionString,
        string cosmosDatabase,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(cosmosConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(cosmosDatabase);
        ArgumentNullException.ThrowIfNull(journal);

        var expected = journal.MutationTransactions
            .SelectMany(static value => value.Transitions)
            .ToDictionary(static value => value.DeliveryId, StringComparer.Ordinal);
        await VerifyPostgresEvidenceAsync(postgresConnectionString, journal.ScenarioId, expected, cancellationToken)
            .ConfigureAwait(false);
        await VerifyCosmosEvidenceAsync(cosmosConnectionString, cosmosDatabase, expected, cancellationToken)
            .ConfigureAwait(false);
    }

    static async Task VerifyPostgresEvidenceAsync(
        string connectionString,
        string scenarioId,
        IReadOnlyDictionary<string, FreightScenarioTransition> expected,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreatePostgresCommand(
            PostgresCommands.VerifyEvidence,
            connection,
            transaction: null,
            ("scenario_id", scenarioId));
        HashSet<string> observed = new(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var deliveryId = reader.GetString(0);
            if (!observed.Add(deliveryId) || !expected.TryGetValue(deliveryId, out var transition))
                throw new InvalidOperationException($"PostgreSQL contains unexpected mutation evidence '{deliveryId}'.");
            Require(
                reader.GetString(1) == transition.ScenarioId
                && reader.GetInt64(2) == transition.Sequence
                && reader.GetString(3) == transition.TransactionId
                && reader.GetString(4) == EntityName(transition.Entity)
                && reader.GetString(5) == transition.Key.TenantId
                && reader.GetString(6) == transition.Key.Id
                && reader.GetInt64(7) == transition.Version
                && reader.GetString(8) == OperationName(transition.Operation)
                && reader.GetString(9) == transition.Fingerprint
                && reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime() == transition.OccurredAtUtc,
                $"PostgreSQL mutation evidence '{deliveryId}' has different scalar evidence.");
            Require(
                JsonEquals(transition.BeforeState, reader.IsDBNull(11) ? null : reader.GetString(11))
                && JsonEquals(transition.AfterState, reader.IsDBNull(12) ? null : reader.GetString(12)),
                $"PostgreSQL mutation evidence '{deliveryId}' has different state evidence.");
        }
        Require(
            observed.SetEquals(expected.Keys),
            "PostgreSQL mutation evidence differs from the deterministic scenario suffix.");
    }

    static async Task VerifyCosmosEvidenceAsync(
        string connectionString,
        string databaseId,
        IReadOnlyDictionary<string, FreightScenarioTransition> expected,
        CancellationToken cancellationToken)
    {
        using var client = Program.CreateCosmosClient(connectionString);
        var database = client.GetDatabase(databaseId);
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (var containerName in expected.Values
                     .Select(static value => Physical(value.Entity).Container)
                     .Distinct(StringComparer.Ordinal))
        {
            var container = database.GetContainer(containerName);
            using var iterator = container.GetItemQueryIterator<JsonElement>(
                new QueryDefinition("SELECT * FROM c WHERE c.documentKind = @documentKind")
                    .WithParameter("@documentKind", CosmosChangeDocumentKind),
                requestOptions: new QueryRequestOptions { MaxItemCount = 32 });
            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    var deliveryId = document.GetProperty("deliveryId").GetString()
                        ?? throw new InvalidOperationException("Cosmos mutation evidence has no delivery identity.");
                    if (!observed.Add(deliveryId) || !expected.TryGetValue(deliveryId, out var transition))
                        throw new InvalidOperationException($"Cosmos contains unexpected mutation evidence '{deliveryId}'.");
                    Require(
                        document.GetProperty("id").GetString() == ChangeItemId(transition)
                        && document.GetProperty("scenarioId").GetString() == transition.ScenarioId
                        && document.GetProperty("sequence").GetInt64() == transition.Sequence
                        && document.GetProperty("transactionId").GetString() == transition.TransactionId
                        && document.GetProperty("entityKind").GetString() == EntityName(transition.Entity)
                        && document.GetProperty("entityId").GetString() == transition.Key.Id
                        && document.GetProperty("entityVersion").GetInt64() == transition.Version
                        && document.GetProperty("operation").GetString() == OperationName(transition.Operation)
                        && document.GetProperty("fingerprint").GetString() == transition.Fingerprint
                        && document.GetProperty("occurredAtUtc").GetDateTimeOffset().ToUniversalTime()
                        == transition.OccurredAtUtc,
                        $"Cosmos mutation evidence '{deliveryId}' has different scalar evidence.");
                    Require(
                        JsonEquals(transition.BeforeState, OptionalState(document, "beforeState"))
                        && JsonEquals(transition.AfterState, OptionalState(document, "afterState")),
                        $"Cosmos mutation evidence '{deliveryId}' has different state evidence.");
                }
            }
        }
        Require(
            observed.SetEquals(expected.Keys),
            "Cosmos mutation evidence differs from the deterministic scenario suffix.");
    }

    static async Task ApplyPostgresAsync(
        string connectionString,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sourceTransaction in journal.MutationTransactions)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var persisted = new PostgresMutationEvidence?[sourceTransaction.Transitions.Length];
            var persistedCount = 0;
            for (var index = 0; index < sourceTransaction.Transitions.Length; index++)
            {
                var transition = sourceTransaction.Transitions[index];
                persisted[index] = await ReadPostgresEvidenceAsync(
                        connection,
                        transaction,
                        transition.DeliveryId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (persisted[index] is { } evidence)
                {
                    persistedCount++;
                    RequireReplayMatch(transition, evidence.Fingerprint, evidence.Version, "PostgreSQL");
                }
            }

            if (persistedCount == sourceTransaction.Transitions.Length)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (persistedCount != 0)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL contains a partial projection of scenario transaction '{sourceTransaction.Id}'.");
            }

            foreach (var transition in sourceTransaction.Transitions)
            {
                await ApplyPostgresTransitionAsync(connection, transaction, transition, cancellationToken)
                    .ConfigureAwait(false);
                await WritePostgresEvidenceAsync(connection, transaction, transition, cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<PostgresMutationEvidence?> ReadPostgresEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string deliveryId,
        CancellationToken cancellationToken)
    {
        await using var command = CreatePostgresCommand(
            PostgresCommands.ReadEvidence,
            connection,
            transaction,
            ("operation_id", deliveryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    static async Task ApplyPostgresTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        var affected = transition.Entity switch
        {
            FreightScenarioEntityKind.Order => await ApplyPostgresOrderAsync(
                    connection,
                    transaction,
                    transition,
                    cancellationToken)
                .ConfigureAwait(false),
            FreightScenarioEntityKind.CustomerAccount => await ApplyPostgresCustomerAsync(
                    connection,
                    transaction,
                    transition,
                    cancellationToken)
                .ConfigureAwait(false),
            FreightScenarioEntityKind.OrderStop => await ApplyPostgresStopAsync(
                    connection,
                    transaction,
                    transition,
                    cancellationToken)
                .ConfigureAwait(false),
            FreightScenarioEntityKind.Location => await ApplyPostgresLocationAsync(
                    connection,
                    transaction,
                    transition,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported freight entity kind '{transition.Entity}'.")
        };
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL transition '{transition.DeliveryId}' expected one source row but affected {affected}.");
        }
    }

    static Task<int> ApplyPostgresOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Operation == FreightScenarioOperationKind.Delete)
        {
            return DeletePostgresAsync(
                connection,
                transaction,
                transition,
                cancellationToken);
        }
        var order = transition.GetAfter<FreightOrder>()
            ?? throw new InvalidOperationException($"Order transition '{transition.DeliveryId}' has no current state.");
        return transition.BeforeState is null
            ? ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.InsertOrder,
                cancellationToken,
                ("tenant_id", order.TenantId),
                ("order_id", order.Id),
                ("order_number", order.OrderNumber),
                ("customer_account_id", order.CustomerAccountId),
                ("equipment_class", order.EquipmentClass),
                ("created_at", order.CreatedAt),
                ("observation_version", transition.Version))
            : ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.UpdateOrder,
                cancellationToken,
                ("tenant_id", order.TenantId),
                ("order_id", order.Id),
                ("order_number", order.OrderNumber),
                ("customer_account_id", order.CustomerAccountId),
                ("equipment_class", order.EquipmentClass),
                ("created_at", order.CreatedAt),
                ("observation_version", transition.Version),
                (PostgresCommands.ExpectedVersionBinding, transition.Version - 1));
    }

    static Task<int> ApplyPostgresCustomerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Operation == FreightScenarioOperationKind.Delete)
        {
            return DeletePostgresAsync(
                connection,
                transaction,
                transition,
                cancellationToken);
        }
        var customer = transition.GetAfter<FreightCustomerAccount>()
            ?? throw new InvalidOperationException($"Customer transition '{transition.DeliveryId}' has no current state.");
        return transition.BeforeState is null
            ? ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.InsertCustomer,
                cancellationToken,
                ("tenant_id", customer.TenantId),
                ("customer_account_id", customer.Id),
                ("display_name", customer.DisplayName),
                ("observation_version", transition.Version))
            : ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.UpdateCustomer,
                cancellationToken,
                ("tenant_id", customer.TenantId),
                ("customer_account_id", customer.Id),
                ("display_name", customer.DisplayName),
                ("observation_version", transition.Version),
                (PostgresCommands.ExpectedVersionBinding, transition.Version - 1));
    }

    static Task<int> ApplyPostgresStopAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Operation == FreightScenarioOperationKind.Delete)
        {
            return DeletePostgresAsync(
                connection,
                transaction,
                transition,
                cancellationToken);
        }
        var stop = transition.GetAfter<FreightOrderStop>()
            ?? throw new InvalidOperationException($"Stop transition '{transition.DeliveryId}' has no current state.");
        return transition.BeforeState is null
            ? ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.InsertStop,
                cancellationToken,
                ("tenant_id", stop.TenantId),
                ("order_stop_id", stop.Id),
                ("order_id", stop.OrderId),
                ("sequence", stop.Sequence),
                ("stop_type", stop.StopType),
                ("location_id", stop.LocationId),
                ("scheduled_start", stop.ScheduledStart),
                ("scheduled_end", stop.ScheduledEnd),
                ("observation_version", transition.Version))
            : ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.UpdateStop,
                cancellationToken,
                ("tenant_id", stop.TenantId),
                ("order_stop_id", stop.Id),
                ("order_id", stop.OrderId),
                ("sequence", stop.Sequence),
                ("stop_type", stop.StopType),
                ("location_id", stop.LocationId),
                ("scheduled_start", stop.ScheduledStart),
                ("scheduled_end", stop.ScheduledEnd),
                ("observation_version", transition.Version),
                (PostgresCommands.ExpectedVersionBinding, transition.Version - 1));
    }

    static Task<int> ApplyPostgresLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Operation == FreightScenarioOperationKind.Delete)
        {
            return DeletePostgresAsync(
                connection,
                transaction,
                transition,
                cancellationToken);
        }
        var location = transition.GetAfter<FreightLocation>()
            ?? throw new InvalidOperationException($"Location transition '{transition.DeliveryId}' has no current state.");
        return transition.BeforeState is null
            ? ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.InsertLocation,
                cancellationToken,
                ("tenant_id", location.TenantId),
                ("location_id", location.Id),
                ("display_name", location.DisplayName),
                ("city", location.City),
                ("region", location.Region),
                ("observation_version", transition.Version))
            : ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.UpdateLocation,
                cancellationToken,
                ("tenant_id", location.TenantId),
                ("location_id", location.Id),
                ("display_name", location.DisplayName),
                ("city", location.City),
                ("region", location.Region),
                ("observation_version", transition.Version),
                (PostgresCommands.ExpectedVersionBinding, transition.Version - 1));
    }

    static Task<int> DeletePostgresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        var (template, identityBinding) = transition.Entity switch
        {
            FreightScenarioEntityKind.Order => (PostgresCommands.DeleteOrder, "order_id"),
            FreightScenarioEntityKind.CustomerAccount => (PostgresCommands.DeleteCustomer, "customer_account_id"),
            FreightScenarioEntityKind.OrderStop => (PostgresCommands.DeleteStop, "order_stop_id"),
            FreightScenarioEntityKind.Location => (PostgresCommands.DeleteLocation, "location_id"),
            _ => throw new InvalidOperationException($"Unsupported freight entity kind '{transition.Entity}'.")
        };
        return ExecutePostgresAsync(
            connection,
            transaction,
            template,
            cancellationToken,
            ("tenant_id", transition.Key.TenantId),
            (identityBinding, transition.Key.Id),
            (PostgresCommands.ExpectedVersionBinding, transition.Version - 1));
    }

    static async Task<int> ExecutePostgresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSqlCommandTemplate template,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreatePostgresCommand(template, connection, transaction, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static NpgsqlCommand CreatePostgresCommand(
        PostgresSqlCommandTemplate template,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        var values = parameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => parameter.Value,
            StringComparer.Ordinal);
        var statement = template.Bind(values);
        var command = new NpgsqlCommand(statement.Text, connection, transaction);
        foreach (var parameter in statement.Parameters)
        {
            var providerParameter = new NpgsqlParameter { Value = parameter.Value ?? DBNull.Value };
            if (parameter.Binding is PostgresCommands.BeforeStateBinding or PostgresCommands.AfterStateBinding)
                providerParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
            command.Parameters.Add(providerParameter);
        }
        return command;
    }

    static async Task WritePostgresEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FreightScenarioTransition transition,
        CancellationToken cancellationToken)
    {
        var affected = await ExecutePostgresAsync(
                connection,
                transaction,
                PostgresCommands.InsertEvidence,
                cancellationToken,
                ("operation_id", transition.DeliveryId),
                ("scenario_id", transition.ScenarioId),
                ("sequence", transition.Sequence),
                ("transaction_id", transition.TransactionId),
                ("entity_kind", EntityName(transition.Entity)),
                ("tenant_id", transition.Key.TenantId),
                ("entity_id", transition.Key.Id),
                ("entity_version", transition.Version),
                ("operation", OperationName(transition.Operation)),
                ("fingerprint", transition.Fingerprint),
                ("occurred_at_utc", transition.OccurredAtUtc),
                (PostgresCommands.BeforeStateBinding, transition.BeforeState?.GetRawText()),
                (PostgresCommands.AfterStateBinding, transition.AfterState?.GetRawText()))
            .ConfigureAwait(false);
        if (affected != 1)
            throw new InvalidOperationException($"PostgreSQL did not retain mutation evidence '{transition.DeliveryId}'.");
    }

    static async Task ApplyCosmosAsync(
        string connectionString,
        string databaseId,
        FreightScenarioJournal journal,
        CancellationToken cancellationToken)
    {
        using var client = Program.CreateCosmosClient(connectionString);
        var database = client.GetDatabase(databaseId);
        foreach (var sourceTransaction in journal.MutationTransactions)
        {
            var physical = Physical(sourceTransaction.Transitions[0].Entity);
            var container = database.GetContainer(physical.Container);
            var partitionKey = new PartitionKey(sourceTransaction.Transitions[0].Key.TenantId);
            var evidence = new JsonElement?[sourceTransaction.Transitions.Length];
            var persistedCount = 0;
            for (var index = 0; index < sourceTransaction.Transitions.Length; index++)
            {
                var transition = sourceTransaction.Transitions[index];
                evidence[index] = (await TryReadCosmosItemAsync(
                        container,
                        ChangeItemId(transition),
                        partitionKey,
                        cancellationToken)
                    .ConfigureAwait(false))?.Resource;
                if (evidence[index] is { } document)
                {
                    persistedCount++;
                    RequireReplayMatch(
                        transition,
                        document.GetProperty("fingerprint").GetString(),
                        document.GetProperty("entityVersion").GetInt64(),
                        "Cosmos");
                }
            }

            if (persistedCount == sourceTransaction.Transitions.Length)
                continue;
            if (persistedCount != 0)
            {
                throw new InvalidOperationException(
                    $"Cosmos contains a partial projection of scenario transaction '{sourceTransaction.Id}'.");
            }

            var batch = container.CreateTransactionalBatch(partitionKey);
            foreach (var transition in sourceTransaction.Transitions)
            {
                var itemId = $"{physical.ItemPrefix}/{transition.Key.Id}";
                var existing = await TryReadCosmosItemAsync(container, itemId, partitionKey, cancellationToken)
                    .ConfigureAwait(false);
                ValidateCosmosPrior(transition, existing?.Resource);
                if (transition.Operation == FreightScenarioOperationKind.Delete)
                {
                    if (existing is null)
                    {
                        throw new InvalidOperationException(
                            $"Cosmos transition '{transition.DeliveryId}' expected source item '{itemId}'.");
                    }
                    batch.DeleteItem(
                        itemId,
                        new TransactionalBatchItemRequestOptions { IfMatchEtag = existing.ETag });
                }
                else
                {
                    var current = CosmosState(transition, before: false);
                    var envelope = new CosmosEntityEnvelope(
                        Id: itemId,
                        PartitionKey: transition.Key.TenantId,
                        DocumentKind: CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
                        ObservationType: physical.ObservationType,
                        ObservationId: transition.Key.Id,
                        ObservationVersion: transition.Version,
                        Observation: current,
                        OccurredAtUtc: transition.OccurredAtUtc);
                    if (existing is null)
                    {
                        batch.CreateItem(envelope);
                    }
                    else
                    {
                        batch.ReplaceItem(
                            itemId,
                            envelope,
                            new TransactionalBatchItemRequestOptions { IfMatchEtag = existing.ETag });
                    }
                }
                batch.CreateItem(CosmosChangeEnvelope.From(transition));
            }
            using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Cosmos scenario transaction '{sourceTransaction.Id}' failed with {(int)response.StatusCode} "
                    + $"({response.StatusCode}): {response.ErrorMessage}");
            }
        }
    }

    static async Task<CosmosExistingItem?> TryReadCosmosItemAsync(
        Container container,
        string itemId,
        PartitionKey partitionKey,
        CancellationToken cancellationToken)
    {
        using var iterator = container.GetItemQueryIterator<JsonElement>(
            new QueryDefinition("SELECT * FROM c WHERE c.id = @itemId")
                .WithParameter("@itemId", itemId),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = partitionKey,
                MaxItemCount = 2
            });
        CosmosExistingItem? result = null;
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (result is not null)
                    throw new InvalidOperationException($"Cosmos repeats item id '{itemId}' within one partition.");
                result = new(
                    document,
                    document.GetProperty("_etag").GetString()
                    ?? throw new InvalidOperationException($"Cosmos item '{itemId}' has no ETag."));
            }
        }
        return result;
    }

    static void ValidateCosmosPrior(FreightScenarioTransition transition, JsonElement? existing)
    {
        if (transition.BeforeState is null)
        {
            if (existing is not null)
            {
                throw new InvalidOperationException(
                    $"Cosmos transition '{transition.DeliveryId}' expected an absent source item.");
            }
            return;
        }
        if (existing is not { } document)
        {
            throw new InvalidOperationException(
                $"Cosmos transition '{transition.DeliveryId}' expected an existing source item.");
        }
        var expected = CosmosState(transition, before: true);
        if (document.GetProperty("observationVersion").GetInt64() != transition.Version - 1
            || !JsonElement.DeepEquals(document.GetProperty("observation"), expected))
        {
            throw new InvalidOperationException(
                $"Cosmos source item for transition '{transition.DeliveryId}' differs from its exact prior image. "
                + $"Expected version {transition.Version - 1} and state {expected.GetRawText()}; "
                + $"actual version {document.GetProperty("observationVersion").GetInt64()} and state "
                + document.GetProperty("observation").GetRawText() + ".");
        }
    }

    static JsonElement CosmosState(FreightScenarioTransition transition, bool before)
    {
        object? value = transition.Entity switch
        {
            FreightScenarioEntityKind.Order => before
                ? transition.GetBefore<FreightOrder>()
                : transition.GetAfter<FreightOrder>(),
            FreightScenarioEntityKind.CustomerAccount => before
                ? transition.GetBefore<FreightCustomerAccount>()
                : transition.GetAfter<FreightCustomerAccount>(),
            FreightScenarioEntityKind.OrderStop => before
                ? transition.GetBefore<FreightOrderStop>()
                : transition.GetAfter<FreightOrderStop>(),
            FreightScenarioEntityKind.Location => before
                ? transition.GetBefore<FreightLocation>()
                : transition.GetAfter<FreightLocation>(),
            _ => throw new InvalidOperationException($"Unsupported freight entity kind '{transition.Entity}'.")
        };
        var state = ObservationValue.FromObject(value
            ?? throw new InvalidOperationException(
                $"Transition '{transition.DeliveryId}' has no {(before ? "prior" : "current")} state."));
        return JsonSerializer.SerializeToElement(
            state.Fields ?? throw new InvalidOperationException(
                $"Transition '{transition.DeliveryId}' state is not an object."),
            CosmosSystemTextJsonSerializer.CreateDefaultOptions());
    }

    static void RequireReplayMatch(
        FreightScenarioTransition transition,
        string? fingerprint,
        long version,
        string provider)
    {
        if (!string.Equals(fingerprint, transition.Fingerprint, StringComparison.Ordinal)
            || version != transition.Version)
        {
            throw new InvalidOperationException(
                $"{provider} delivery identity '{transition.DeliveryId}' is already bound to different scenario evidence.");
        }
    }

    static CosmosPhysicalEntity Physical(FreightScenarioEntityKind entity) => entity switch
    {
        FreightScenarioEntityKind.Order => new(
            Container: "orders",
            ItemPrefix: "order",
            ObservationType: FreightOrderMaterializationModel.OrderShapeId.ShapeId.Value),
        FreightScenarioEntityKind.CustomerAccount => new(
            Container: "customerAccounts",
            ItemPrefix: "customer",
            ObservationType: FreightOrderMaterializationModel.CustomerAccountShapeId.ShapeId.Value),
        FreightScenarioEntityKind.OrderStop => new(
            Container: "orderStops",
            ItemPrefix: "stop",
            ObservationType: FreightOrderMaterializationModel.OrderStopShapeId.ShapeId.Value),
        FreightScenarioEntityKind.Location => new(
            Container: "locations",
            ItemPrefix: "location",
            ObservationType: FreightOrderMaterializationModel.LocationShapeId.ShapeId.Value),
        _ => throw new InvalidOperationException($"Unsupported freight entity kind '{entity}'.")
    };

    static string EntityName(FreightScenarioEntityKind entity) => entity switch
    {
        FreightScenarioEntityKind.Order => "order",
        FreightScenarioEntityKind.CustomerAccount => "customerAccount",
        FreightScenarioEntityKind.OrderStop => "orderStop",
        FreightScenarioEntityKind.Location => "location",
        _ => throw new InvalidOperationException($"Unsupported freight entity kind '{entity}'.")
    };

    static string OperationName(FreightScenarioOperationKind operation) => operation switch
    {
        FreightScenarioOperationKind.Upsert => "upsert",
        FreightScenarioOperationKind.Delete => "delete",
        _ => throw new InvalidOperationException($"Unsupported freight operation kind '{operation}'.")
    };

    static string ChangeItemId(FreightScenarioTransition transition) =>
        $"scenario-change/{Uri.EscapeDataString(transition.ScenarioId)}/{transition.Sequence}";

    static bool JsonEquals(JsonElement? expected, string? actual) =>
        JsonNode.DeepEquals(
            expected is { } expectedValue ? JsonNode.Parse(expectedValue.GetRawText()) : null,
            actual is not null ? JsonNode.Parse(actual) : null);

    static bool JsonEquals(JsonElement? expected, JsonElement? actual) =>
        JsonNode.DeepEquals(
            expected is { } expectedValue ? JsonNode.Parse(expectedValue.GetRawText()) : null,
            actual is { ValueKind: not JsonValueKind.Null } actualValue
                ? JsonNode.Parse(actualValue.GetRawText())
                : null);

    static JsonElement? OptionalState(JsonElement document, string propertyName) =>
        document.TryGetProperty(propertyName, out var state) ? state : null;

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static class PostgresCommands
    {
        internal const string ExpectedVersionBinding = "expected_observation_version";
        internal const string BeforeStateBinding = "before_state";
        internal const string AfterStateBinding = "after_state";

        internal static readonly PostgresSqlCommandTemplate InsertOrder = Insert(
            table: "orders",
            columns:
            [
                "tenant_id",
                "order_id",
                "order_number",
                "customer_account_id",
                "equipment_class",
                "created_at",
                "observation_version"
            ]);

        internal static readonly PostgresSqlCommandTemplate UpdateOrder = Update(
            table: "orders",
            identityColumn: "order_id",
            assignmentColumns:
            [
                "order_number",
                "customer_account_id",
                "equipment_class",
                "created_at",
                "observation_version"
            ]);

        internal static readonly PostgresSqlCommandTemplate DeleteOrder = Delete(
            table: "orders",
            identityColumn: "order_id");

        internal static readonly PostgresSqlCommandTemplate InsertCustomer = Insert(
            table: "customer_accounts",
            columns: ["tenant_id", "customer_account_id", "display_name", "observation_version"]);

        internal static readonly PostgresSqlCommandTemplate UpdateCustomer = Update(
            table: "customer_accounts",
            identityColumn: "customer_account_id",
            assignmentColumns: ["display_name", "observation_version"]);

        internal static readonly PostgresSqlCommandTemplate DeleteCustomer = Delete(
            table: "customer_accounts",
            identityColumn: "customer_account_id");

        internal static readonly PostgresSqlCommandTemplate InsertStop = Insert(
            table: "order_stops",
            columns:
            [
                "tenant_id",
                "order_stop_id",
                "order_id",
                "sequence",
                "stop_type",
                "location_id",
                "scheduled_start",
                "scheduled_end",
                "observation_version"
            ]);

        internal static readonly PostgresSqlCommandTemplate UpdateStop = Update(
            table: "order_stops",
            identityColumn: "order_stop_id",
            assignmentColumns:
            [
                "order_id",
                "sequence",
                "stop_type",
                "location_id",
                "scheduled_start",
                "scheduled_end",
                "observation_version"
            ]);

        internal static readonly PostgresSqlCommandTemplate DeleteStop = Delete(
            table: "order_stops",
            identityColumn: "order_stop_id");

        internal static readonly PostgresSqlCommandTemplate InsertLocation = Insert(
            table: "locations",
            columns: ["tenant_id", "location_id", "display_name", "city", "region", "observation_version"]);

        internal static readonly PostgresSqlCommandTemplate UpdateLocation = Update(
            table: "locations",
            identityColumn: "location_id",
            assignmentColumns: ["display_name", "city", "region", "observation_version"]);

        internal static readonly PostgresSqlCommandTemplate DeleteLocation = Delete(
            table: "locations",
            identityColumn: "location_id");

        internal static readonly PostgresSqlCommandTemplate InsertEvidence = Insert(
            table: "scenario_mutations",
            columns:
            [
                "operation_id",
                "scenario_id",
                "sequence",
                "transaction_id",
                "entity_kind",
                "tenant_id",
                "entity_id",
                "entity_version",
                "operation",
                "fingerprint",
                "occurred_at_utc",
                BeforeStateBinding,
                AfterStateBinding
            ]);

        internal static readonly PostgresSqlCommandTemplate ReadEvidence = Select(
            table: "scenario_mutations",
            columns: ["fingerprint", "entity_version"],
            predicateColumn: "operation_id");

        internal static readonly PostgresSqlCommandTemplate VerifyEvidence = Select(
            table: "scenario_mutations",
            columns:
            [
                "operation_id",
                "scenario_id",
                "sequence",
                "transaction_id",
                "entity_kind",
                "tenant_id",
                "entity_id",
                "entity_version",
                "operation",
                "fingerprint",
                "occurred_at_utc",
                BeforeStateBinding,
                AfterStateBinding
            ],
            predicateColumn: "scenario_id",
            orderColumn: "sequence");

        static PostgresSqlCommandTemplate Insert(string table, IReadOnlyList<string> columns)
        {
            PostgresSqlInsertBuilder builder = new(new PostgresSqlQualifiedTable(PostgresSchema, table));
            foreach (var column in columns)
                builder.Value(column, PostgresSqlExpression.RuntimeParameter(column));
            return builder.BuildTemplate();
        }

        static PostgresSqlCommandTemplate Update(
            string table,
            string identityColumn,
            IReadOnlyList<string> assignmentColumns)
        {
            PostgresSqlUpdateBuilder builder = new(new PostgresSqlQualifiedTable(PostgresSchema, table));
            foreach (var column in assignmentColumns)
                builder.Set(column, PostgresSqlExpression.RuntimeParameter(column));
            AddVersionedIdentityPredicates(predicate => builder.Where(predicate), identityColumn);
            return builder.BuildTemplate();
        }

        static PostgresSqlCommandTemplate Delete(string table, string identityColumn)
        {
            PostgresSqlDeleteBuilder builder = new(new PostgresSqlQualifiedTable(PostgresSchema, table));
            AddVersionedIdentityPredicates(predicate => builder.Where(predicate), identityColumn);
            return builder.BuildTemplate();
        }

        static PostgresSqlCommandTemplate Select(
            string table,
            IReadOnlyList<string> columns,
            string predicateColumn,
            string? orderColumn = null)
        {
            const string alias = "source";
            PostgresSqlSelectBuilder builder = new(
                new PostgresSqlQualifiedTable(PostgresSchema, table),
                alias);
            foreach (var column in columns)
                builder.Select(PostgresSqlExpression.Column(alias, column), column);
            builder.Where(PostgresSqlExpression.Binary(
                @operator: PostgresSqlBinaryOperator.Equal,
                left: PostgresSqlExpression.Column(alias, predicateColumn),
                right: PostgresSqlExpression.RuntimeParameter(predicateColumn)));
            if (orderColumn is not null)
            {
                builder.OrderBy(
                    PostgresSqlExpression.Column(alias, orderColumn),
                    direction: PostgresSqlSortDirection.Ascending,
                    nullPlacement: PostgresSqlNullPlacement.Last);
            }
            return builder.BuildTemplate();
        }

        static void AddVersionedIdentityPredicates(
            Action<PostgresSqlExpression> add,
            string identityColumn)
        {
            add(Equal("tenant_id", "tenant_id"));
            add(Equal(identityColumn, identityColumn));
            add(Equal("observation_version", ExpectedVersionBinding));
        }

        static PostgresSqlExpression Equal(string column, string binding) => PostgresSqlExpression.Binary(
            @operator: PostgresSqlBinaryOperator.Equal,
            left: PostgresSqlExpression.UnqualifiedColumn(column),
            right: PostgresSqlExpression.RuntimeParameter(binding));
    }

    sealed record PostgresMutationEvidence(string Fingerprint, long Version);

    sealed record CosmosExistingItem(JsonElement Resource, string ETag);

    readonly record struct CosmosPhysicalEntity(
        string Container,
        string ItemPrefix,
        string ObservationType);

    sealed record CosmosEntityEnvelope(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("partitionKey")] string PartitionKey,
        [property: JsonPropertyName("documentKind")] string DocumentKind,
        [property: JsonPropertyName("observationType")] string ObservationType,
        [property: JsonPropertyName("observationId")] string ObservationId,
        [property: JsonPropertyName("observationVersion")] long ObservationVersion,
        [property: JsonPropertyName("observation")] JsonElement Observation,
        [property: JsonPropertyName("occurredAtUtc")] DateTimeOffset OccurredAtUtc);

    sealed record CosmosChangeEnvelope(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("partitionKey")] string PartitionKey,
        [property: JsonPropertyName("documentKind")] string DocumentKind,
        [property: JsonPropertyName("scenarioId")] string ScenarioId,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("transactionId")] string TransactionId,
        [property: JsonPropertyName("deliveryId")] string DeliveryId,
        [property: JsonPropertyName("entityKind")] string EntityKind,
        [property: JsonPropertyName("entityId")] string EntityId,
        [property: JsonPropertyName("entityVersion")] long EntityVersion,
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("fingerprint")] string Fingerprint,
        [property: JsonPropertyName("occurredAtUtc")] DateTimeOffset OccurredAtUtc,
        [property: JsonPropertyName("beforeState")] JsonElement? BeforeState,
        [property: JsonPropertyName("afterState")] JsonElement? AfterState)
    {
        internal static CosmosChangeEnvelope From(FreightScenarioTransition transition) => new(
            Id: ChangeItemId(transition),
            PartitionKey: transition.Key.TenantId,
            DocumentKind: CosmosChangeDocumentKind,
            ScenarioId: transition.ScenarioId,
            Sequence: transition.Sequence,
            TransactionId: transition.TransactionId,
            DeliveryId: transition.DeliveryId,
            EntityKind: EntityName(transition.Entity),
            EntityId: transition.Key.Id,
            EntityVersion: transition.Version,
            Operation: OperationName(transition.Operation),
            Fingerprint: transition.Fingerprint,
            OccurredAtUtc: transition.OccurredAtUtc,
            BeforeState: transition.BeforeState,
            AfterState: transition.AfterState);
    }
}
