using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Assurance;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Examples.ReplayAssurance;

/// <summary>Explicit realizations compared by the disposable reservation demonstration.</summary>
public enum ReservationRealization
{
    /// <summary>Existing PostgreSQL atomic state-and-receipt commit protocol.</summary>
    AtomicReceipt,
    /// <summary>Deliberately defective fixture that re-evaluates and writes on each retry without a receipt.</summary>
    UnprotectedRetry
}

/// <summary>Independent transaction-local audit of a physical stock update.</summary>
/// <param name="Id">Database-assigned effect identity; gaps from aborted transactions are permitted.</param>
/// <param name="Before">Available stock before the update.</param>
/// <param name="After">Available stock after the update.</param>
public sealed record StockEffect(long Id, int Before, int After);

/// <summary>Retained demo result after its private schema has been removed.</summary>
/// <param name="Realization">Explicit mechanism under test.</param>
/// <param name="Available">Final authoritative quantity.</param>
/// <param name="ReceiptCount">Number of retained operation receipts.</param>
/// <param name="ReplayDisposition">Native replay result, or null for the deliberately unprotected path.</param>
/// <param name="Effects">Independent committed stock update audit.</param>
/// <param name="Check">Exact claim input projected from the audit.</param>
public sealed record ReservationDemoResult(ReservationRealization Realization, int Available, long ReceiptCount,
    EntityTransitionOperationDisposition? ReplayDisposition, ImmutableArray<StockEffect> Effects, EffectUniquenessCheck Check);

/// <summary>Standalone reservation proof fixture sharing the current Aspire domain, without its composition or runtime.</summary>
public static class ReservationReplayDemo
{
    /// <summary>Explicit disposable database connection setting, never included in evidence or output.</summary>
    public const string ConnectionVariable = "COHESIVE_POSTGRES_TEST_CONNECTION_STRING";
    /// <summary>Fixture's initial available stock.</summary>
    public const int InitialAvailable = 10;
    /// <summary>Quantity in the exact operation retried after acknowledgement loss.</summary>
    public const int Quantity = 2;
    const string Partition = "assurance-demo";
    const string Sku = "book";
    const string Table = "inventory";
    const string Receipts = "receipts";
    const string Authority = "reservation-assurance";
    static readonly System.Text.Json.JsonSerializerOptions Json = StrictDocumentJson.CreateOptions();

    /// <summary>Runs one isolated native scenario and always drops only its owned schema.</summary>
    /// <param name="connectionString">Explicit disposable PostgreSQL connection, owned by the caller.</param>
    /// <param name="realization">Native receipt protocol or deliberate defective control.</param>
    /// <param name="cancellationToken">Cancels setup and scenario work; cleanup uses an independent timeout.</param>
    /// <returns>Audited effect evidence after all attempts ended and the private schema was removed.</returns>
    /// <exception cref="ArgumentException">Connection is blank or realization is unsupported.</exception>
    /// <exception cref="ArgumentNullException">Connection is null.</exception>
    /// <exception cref="InvalidOperationException">A fixture contract, retained receipt, or audit check fails.</exception>
    /// <exception cref="NpgsqlException">Database setup, execution, or cleanup fails.</exception>
    /// <exception cref="OperationCanceledException">Execution or bounded cleanup is canceled.</exception>
    public static async Task<ReservationDemoResult> RunAsync(string connectionString, ReservationRealization realization,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (!Enum.IsDefined(realization)) throw new ArgumentOutOfRangeException(nameof(realization));
        var schema = "assurance_" + Guid.NewGuid().ToString("N");
        // The identifier is entirely generated here and quoted using the driver's standard identifier escaping.
        using var quoting = new NpgsqlCommandBuilder();
        var quotedSchema = quoting.QuoteIdentifier(schema);
        var domain = new FulfillmentDomain();
        var entity = domain.Inventory.Definition;
        var compilation = domain.InventoryTransitions.Reserve.Compile();
        if (!compilation.Validation.IsValid) throw new InvalidOperationException("Reservation did not compile.");
        var plan = compilation.Plan!;
        var bindings = PostgresPersistenceBindings.Define(new("assurance"), storage => storage
            .Schema(PostgresSchemaInitialization.CreateIfMissing)
            .Entity(domain.Inventory, mapping => mapping.Table(schema, Table)
                .Identity(x => x.Sku, "sku").Partition(x => x.Partition, "partition_key")
                .Column(x => x.Available, "available"))
            .TransitionReceipts(new(schema, Receipts, Partition), PostgresSchemaInitialization.CreateIfMissing));
        var context = OperationContext.Create(cancellationToken: cancellationToken);
        EntityTransitionOperationCommit? exactCommit = null;
        var created = false;
        string serverVersion;
        try
        {
            // This data source and its entire pool are disposed before the retry.
            await using (var first = NpgsqlDataSource.Create(connectionString))
            {
                await Execute(first, $"CREATE SCHEMA {quotedSchema}", cancellationToken);
                created = true;
                await bindings.PrepareSchemaAsync(first, cancellationToken);
                var repository = await Repository(bindings, entity, first, cancellationToken);
                var initial = new InventoryItem(Sku, Partition, InitialAvailable);
                var before = await repository.Upsert(context, new(entity.CreateState(Sku,
                    ObservationValue.FromObject(initial).Fields!, version: 0).Snapshot));
                exactCommit = Prepare(plan, entity, before);
                await InstallAudit(first, quotedSchema, cancellationToken);
                await using (var version = first.CreateCommand("SHOW server_version"))
                    serverVersion = (string)(await version.ExecuteScalarAsync(cancellationToken))!;
                try
                {
                    await CommitAndLoseAcknowledgement(repository, context, exactCommit, realization);
                    throw new InvalidOperationException("The fault did not fire.");
                }
                catch (AcknowledgementLostException)
                {
                    // The observer injected this only after the repository confirmed its native COMMIT.
                }
            }

            EntityTransitionOperationDisposition? replayDisposition = null;
            int available;
            long receiptCount;
            ImmutableArray<StockEffect> effects;
            await using (var retry = NpgsqlDataSource.Create(connectionString))
            {
                var repository = await Repository(bindings, entity, retry, cancellationToken);
                if (realization == ReservationRealization.AtomicReceipt)
                {
                    var replay = await repository.CommitTransitionOperation(context, exactCommit!);
                    replayDisposition = replay.Disposition;
                    if (replay.Disposition != EntityTransitionOperationDisposition.Replayed
                        || replay.Receipt?.Commit.Fingerprint != exactCommit!.Fingerprint)
                        throw new InvalidOperationException("Retry did not recover the exact original receipt.");
                }
                else
                {
                    var current = await repository.TryGet(context, Sku, new(partitionKey: Partition))
                        ?? throw new InvalidOperationException("Inventory disappeared.");
                    // Explicitly broken realization: same operation, but a new decision against current stock.
                    await repository.Upsert(context, Prepare(plan, entity, current).Write);
                }
                var final = await repository.TryGet(context, Sku, new(partitionKey: Partition))
                    ?? throw new InvalidOperationException("Inventory disappeared.");
                available = ObservationValue.FromObject(final.Entity.Observation.Fields).Deserialize<InventoryItem>()!.Available;
                effects = await ReadAudit(retry, quotedSchema, cancellationToken);
                await using var count = retry.CreateCommand($"SELECT count(*) FROM {quotedSchema}.receipts");
                receiptCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            }
            // All attempts have completed; no other writers exist in this private schema. The audit was
            // acquired after the client pools closed their writes. This is finite fixture closure, not a
            // production watermark or a guarantee about future calls.
            var operation = Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(exactCommit!.Request.Operation, Json));
            var observations = ImmutableArray.CreateBuilder<CommittedEffectObservation>(effects.Length);
            foreach (var effect in effects)
                observations.Add(new(operation, effect.Id.ToString(CultureInfo.InvariantCulture)));
            var binaryFingerprint = BinaryFingerprint();
            var check = new EffectUniquenessCheck(
                ReservationClaim.Create(plan.DefinitionReference),
                new("demo/postgres-stock-audit", "1", binaryFingerprint, Authority + "/" + Partition, "inventory/" + Sku),
                new(AssuranceTargetKind.ImplementationCandidate, "postgres/" + realization + "/" + schema,
                    "postgres-" + serverVersion, binaryFingerprint),
                plan.DefinitionReference, ObservationScopeCompletion.ClosedComplete, observations.MoveToImmutable());
            return new(realization, available, receiptCount, replayDisposition, effects, check);
        }
        finally
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var source = NpgsqlDataSource.Create(connectionString);
                await Execute(source, $"DROP SCHEMA {quotedSchema} CASCADE", cleanup.Token);
            }
        }
    }

    static async Task<IEntityRepository> Repository(PostgresPersistenceBindings bindings, EntityDefinition entity,
        NpgsqlDataSource source, CancellationToken token)
    {
        var receipts = await bindings.PrepareReceiptsAsync(source, token);
        return bindings.Bind(source, Authority).Repository(ValueContract.FromShape(entity.Shape), transitionReceipts: receipts);
    }

    static EntityTransitionOperationCommit Prepare(CompiledTransitionPlan plan, EntityDefinition entity, EntitySnapshot before)
    {
        var occurrence = new ProcessOperationOccurrence(new(new("reservation-demo"), new("attempt-1")),
            new("activation-1"), new("token-1"), new("reserve"), 0);
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new StockQuantity(Quantity)));
        var request = new EntityTransitionOperationRequest(occurrence, new(Authority, Partition), plan.DefinitionReference,
            new(new(entity.Shape.Id.Value), before.Entity.EntityId), input);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, occurrence.Activation, input,
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(before.Entity.Observation.Fields)));
        var candidate = TransitionStateProjector.Apply(ObservationValue.FromObject(before.Entity.Observation.Fields), decision);
        var state = entity.CreateState(Sku, candidate.Fields!, before.Entity.Version + 1).Snapshot;
        return new(request, new(state, before.ConcurrencyToken), decision.Kind,
            ProcessOperationResult.Completed(decision.Outcome!), decision.GuaranteeDemands, decision.Evidence);
    }

    static async Task CommitAndLoseAcknowledgement(IEntityRepository repository, OperationContext context,
        EntityTransitionOperationCommit commit, ReservationRealization realization)
    {
        if (realization == ReservationRealization.AtomicReceipt)
        {
            var result = await repository.CommitTransitionOperation(context, commit);
            if (result.Disposition != EntityTransitionOperationDisposition.Committed)
                throw new InvalidOperationException("Initial reservation did not commit.");
        }
        else await repository.Upsert(context, commit.Write);
        throw new AcknowledgementLostException();
    }

    sealed class AcknowledgementLostException : IOException;

    static Task InstallAudit(NpgsqlDataSource source, string schema, CancellationToken token) => Execute(source, $$"""
        CREATE TABLE {{schema}}.effects (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            before_stock integer NOT NULL,
            after_stock integer NOT NULL);
        CREATE FUNCTION {{schema}}.observe_stock() RETURNS trigger LANGUAGE plpgsql AS $audit$
        BEGIN
            INSERT INTO {{schema}}.effects(before_stock, after_stock) VALUES (OLD.available, NEW.available);
            RETURN NEW;
        END
        $audit$;
        CREATE TRIGGER observe_stock AFTER UPDATE ON {{schema}}.inventory
            FOR EACH ROW EXECUTE FUNCTION {{schema}}.observe_stock();
        """, token);

    static async Task<ImmutableArray<StockEffect>> ReadAudit(NpgsqlDataSource source, string schema, CancellationToken token)
    {
        await using var command = source.CreateCommand($"SELECT id, before_stock, after_stock FROM {schema}.effects ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(token);
        var effects = ImmutableArray.CreateBuilder<StockEffect>();
        while (await reader.ReadAsync(token)) effects.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
        return effects.ToImmutable();
    }

    static async Task Execute(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(token);
    }

    static string BinaryFingerprint()
    {
        // Scope the attestation to the shipped demo dependencies, not a fabricated semantic version.
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var files = Directory.GetFiles(directory, "*.dll").Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
