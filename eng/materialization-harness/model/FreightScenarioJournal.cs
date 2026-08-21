using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cohesive.MaterializationHarness.Model;

/// <summary>Canonical freight entity kinds admitted by the materialization harness scenario journal.</summary>
public enum FreightScenarioEntityKind
{
    /// <summary>An immutable customer order root.</summary>
    Order = 0,

    /// <summary>A customer account referenced by one or more orders.</summary>
    CustomerAccount = 1,

    /// <summary>An ordered stop owned by an order.</summary>
    OrderStop = 2,

    /// <summary>A location referenced by one or more order stops.</summary>
    Location = 3
}

/// <summary>Mutation kinds admitted by the freight scenario journal.</summary>
public enum FreightScenarioOperationKind
{
    /// <summary>Create a new entity or replace an existing entity with its next version.</summary>
    Upsert = 0,

    /// <summary>Delete an existing entity while retaining its exact prior image.</summary>
    Delete = 1
}

/// <summary>Tenant-local identity of one canonical freight scenario entity.</summary>
public readonly record struct FreightScenarioEntityKey
{
    /// <summary>Creates one tenant-local freight entity identity.</summary>
    /// <param name="tenantId">Owning tenant identity.</param>
    /// <param name="id">Tenant-local entity identity.</param>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    public FreightScenarioEntityKey(string tenantId, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        TenantId = tenantId;
        Id = id;
    }

    /// <summary>Owning tenant identity.</summary>
    public string TenantId { get; }

    /// <summary>Tenant-local entity identity.</summary>
    public string Id { get; }

    /// <summary>Formats the tenant-local identity for diagnostics.</summary>
    /// <returns>The canonical tenant/id diagnostic representation.</returns>
    public override string ToString() => $"{TenantId}/{Id}";
}

/// <summary>One fully resolved scenario transition with exact before/after images.</summary>
public sealed class FreightScenarioTransition
{
    readonly object? before;
    readonly object? after;

    internal FreightScenarioTransition(
        string scenarioId,
        long sequence,
        string transactionId,
        DateTimeOffset occurredAtUtc,
        FreightScenarioEntityKind entity,
        FreightScenarioOperationKind operation,
        FreightScenarioEntityKey key,
        long version,
        object? before,
        object? after,
        JsonElement? beforeState,
        JsonElement? afterState)
    {
        ScenarioId = scenarioId;
        Sequence = sequence;
        TransactionId = transactionId;
        OccurredAtUtc = occurredAtUtc;
        Entity = entity;
        Operation = operation;
        Key = key;
        Version = version;
        this.before = before;
        this.after = after;
        BeforeState = beforeState;
        AfterState = afterState;
        DeliveryId = $"scenario/{Uri.EscapeDataString(scenarioId)}/operation/{sequence}";
        Fingerprint = ComputeFingerprint(this);
    }

    /// <summary>Owning scenario identity.</summary>
    public string ScenarioId { get; }

    /// <summary>Contiguous journal sequence.</summary>
    public long Sequence { get; }

    /// <summary>Stable source-transaction identity.</summary>
    public string TransactionId { get; }

    /// <summary>Deterministic UTC occurrence time derived from the journal authority.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Canonical entity kind changed by this transition.</summary>
    public FreightScenarioEntityKind Entity { get; }

    /// <summary>Canonical mutation kind.</summary>
    public FreightScenarioOperationKind Operation { get; }

    /// <summary>Tenant-local entity identity.</summary>
    public FreightScenarioEntityKey Key { get; }

    /// <summary>Monotonic entity version after this transition, including delete tombstones.</summary>
    public long Version { get; }

    /// <summary>Stable delivery identity shared by every provider projection.</summary>
    public string DeliveryId { get; }

    /// <summary>SHA-256 fingerprint of the complete resolved transition.</summary>
    public string Fingerprint { get; }

    /// <summary>Canonical prior entity state, or <see langword="null"/> for creation.</summary>
    public JsonElement? BeforeState { get; }

    /// <summary>Canonical current entity state, or <see langword="null"/> for deletion.</summary>
    public JsonElement? AfterState { get; }

    /// <summary>Gets the exact typed prior entity value.</summary>
    /// <typeparam name="T">Expected canonical freight entity type.</typeparam>
    /// <returns>The prior value, or <see langword="null"/> for creation.</returns>
    /// <exception cref="InvalidOperationException">The prior value has another entity type.</exception>
    public T? GetBefore<T>()
        where T : class => Get<T>(before, "prior");

    /// <summary>Gets the exact typed current entity value.</summary>
    /// <typeparam name="T">Expected canonical freight entity type.</typeparam>
    /// <returns>The current value, or <see langword="null"/> for deletion.</returns>
    /// <exception cref="InvalidOperationException">The current value has another entity type.</exception>
    public T? GetAfter<T>()
        where T : class => Get<T>(after, "current");

    T? Get<T>(object? value, string role)
        where T : class => value switch
        {
            null => null,
            T typed => typed,
            _ => throw new InvalidOperationException(
                $"Scenario transition '{DeliveryId}' has a {role} {Entity} value, not {typeof(T).Name}.")
        };

    static string ComputeFingerprint(FreightScenarioTransition transition)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "cohesive.materialization-harness/scenario-transition/v1",
            transition.ScenarioId,
            transition.Sequence,
            transition.TransactionId,
            occurredAtUtc = transition.OccurredAtUtc.ToUniversalTime(),
            entity = transition.Entity.ToString(),
            operation = transition.Operation.ToString(),
            transition.Key.TenantId,
            transition.Key.Id,
            transition.Version,
            transition.BeforeState,
            transition.AfterState
        });
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}

/// <summary>One source-aligned transaction in the incremental scenario suffix.</summary>
public sealed class FreightScenarioTransaction
{
    internal FreightScenarioTransaction(
        string id,
        DateTimeOffset occurredAtUtc,
        ImmutableArray<FreightScenarioTransition> transitions)
    {
        Id = id;
        OccurredAtUtc = occurredAtUtc;
        Transitions = transitions;
    }

    /// <summary>Stable transaction identity.</summary>
    public string Id { get; }

    /// <summary>Deterministic UTC transaction occurrence time.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Contiguous transitions committed atomically by each provider interpretation.</summary>
    public ImmutableArray<FreightScenarioTransition> Transitions { get; }
}

internal readonly record struct FreightScenarioVersionKey(
    FreightScenarioEntityKind Entity,
    FreightScenarioEntityKey Key);

/// <summary>Exact semantic freight state projected through one scenario-journal cut.</summary>
public sealed class FreightScenarioState
{
    readonly ImmutableDictionary<FreightScenarioVersionKey, long> versions;

    internal FreightScenarioState(
        string scenarioId,
        long throughSequence,
        DateTimeOffset occurredAtUtc,
        ImmutableArray<FreightOrder> orders,
        ImmutableArray<FreightCustomerAccount> customers,
        ImmutableArray<FreightOrderStop> stops,
        ImmutableArray<FreightLocation> locations,
        ImmutableDictionary<FreightScenarioVersionKey, long> versions)
    {
        ScenarioId = scenarioId;
        ThroughSequence = throughSequence;
        OccurredAtUtc = occurredAtUtc;
        Orders = orders;
        Customers = customers;
        Stops = stops;
        Locations = locations;
        this.versions = versions;
    }

    /// <summary>Owning scenario identity.</summary>
    public string ScenarioId { get; }

    /// <summary>Last journal sequence represented by this state.</summary>
    public long ThroughSequence { get; }

    /// <summary>Deterministic UTC projection instant.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Orders in canonical tenant and identity order.</summary>
    public ImmutableArray<FreightOrder> Orders { get; }

    /// <summary>Customer accounts in canonical tenant and identity order.</summary>
    public ImmutableArray<FreightCustomerAccount> Customers { get; }

    /// <summary>Order stops in canonical tenant, order, sequence, and identity order.</summary>
    public ImmutableArray<FreightOrderStop> Stops { get; }

    /// <summary>Locations in canonical tenant and identity order.</summary>
    public ImmutableArray<FreightLocation> Locations { get; }

    /// <summary>Number of distinct tenant partitions represented by the state.</summary>
    public int TenantCount => Orders.Select(static order => order.TenantId)
        .Distinct(StringComparer.Ordinal)
        .Count();

    /// <summary>Gets the exact entity version retained at this cut.</summary>
    /// <param name="entity">Canonical entity kind.</param>
    /// <param name="tenantId">Owning tenant identity.</param>
    /// <param name="id">Tenant-local entity identity.</param>
    /// <returns>The positive retained version.</returns>
    /// <exception cref="KeyNotFoundException">The entity is absent at this cut.</exception>
    public long GetVersion(FreightScenarioEntityKind entity, string tenantId, string id) =>
        versions[new(entity, new(tenantId, id))];
}

/// <summary>Materialized deterministic freight scenario journal.</summary>
public sealed class FreightScenarioJournal
{
    /// <summary>Current persisted journal schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.materialization-harness/scenario-journal/v3";

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    FreightScenarioJournal(
        string scenarioId,
        DateTimeOffset occurredAtUtc,
        long baselineThroughSequence,
        FreightScenarioState baseline,
        FreightScenarioState final,
        ImmutableArray<FreightScenarioTransaction> mutationTransactions)
    {
        ScenarioId = scenarioId;
        OccurredAtUtc = occurredAtUtc;
        BaselineThroughSequence = baselineThroughSequence;
        Baseline = baseline;
        Final = final;
        MutationTransactions = mutationTransactions;
    }

    /// <summary>Stable scenario identity.</summary>
    public string ScenarioId { get; }

    /// <summary>Stable UTC time authority from which operation instants are derived.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Inclusive sequence that defines the rebuild baseline cut.</summary>
    public long BaselineThroughSequence { get; }

    /// <summary>Exact semantic state at the rebuild baseline cut.</summary>
    public FreightScenarioState Baseline { get; }

    /// <summary>Exact semantic state after every incremental mutation transaction.</summary>
    public FreightScenarioState Final { get; }

    /// <summary>Incremental source transactions in journal order.</summary>
    public ImmutableArray<FreightScenarioTransaction> MutationTransactions { get; }

    /// <summary>Loads, resolves, and validates one persisted scenario journal.</summary>
    /// <param name="path">Path to the persisted journal JSON document.</param>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>The fully resolved baseline, transitions, transactions, and final semantic state.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The journal is structurally or semantically invalid.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="JsonException">The journal JSON is malformed.</exception>
    /// <exception cref="OperationCanceledException">Reading is cancelled.</exception>
    public static async Task<FreightScenarioJournal> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<JournalDocument>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The scenario journal is empty.");
        return Materialize(document);
    }

    static FreightScenarioJournal Materialize(JournalDocument document)
    {
        if (!string.Equals(document.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported scenario journal schema '{document.SchemaVersion}'.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(document.ScenarioId);
        if (document.OccurredAtUtc == default || document.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("The scenario journal requires a non-default UTC time authority.");
        if (document.Operations.IsDefaultOrEmpty)
            throw new InvalidOperationException("The scenario journal requires at least one operation.");
        if (document.BaselineThroughSequence <= 0
            || document.BaselineThroughSequence >= document.Operations.Length)
        {
            throw new InvalidOperationException(
                "The scenario journal requires a nonempty baseline followed by at least one incremental operation.");
        }

        Dictionary<FreightScenarioVersionKey, VersionedEntity> entities = [];
        ImmutableArray<FreightScenarioTransition>.Builder mutations = ImmutableArray.CreateBuilder<FreightScenarioTransition>(
            document.Operations.Length - checked((int)document.BaselineThroughSequence));
        FreightScenarioState? baseline = null;
        long expectedSequence = 1;
        foreach (var operation in document.Operations)
        {
            if (operation.Sequence != expectedSequence)
            {
                throw new InvalidOperationException(
                    $"Scenario operation sequence must be contiguous; expected {expectedSequence} and found {operation.Sequence}.");
            }
            expectedSequence++;
            var transition = Apply(document, operation, entities);
            if (operation.Sequence <= document.BaselineThroughSequence
                && (transition.Operation != FreightScenarioOperationKind.Upsert
                    || transition.GetBefore<object>() is not null))
            {
                throw new InvalidOperationException(
                    $"Baseline operation {operation.Sequence} must create one previously absent entity.");
            }
            if (operation.Sequence == document.BaselineThroughSequence)
            {
                baseline = CreateState(document, operation.Sequence, entities);
                ValidateState(baseline, "baseline", requireCoverage: true);
            }
            else if (operation.Sequence > document.BaselineThroughSequence)
            {
                mutations.Add(transition);
            }
        }

        var exactBaseline = baseline
            ?? throw new InvalidOperationException("The scenario journal did not materialize its declared baseline cut.");
        var transactions = GroupTransactions(document, mutations.MoveToImmutable(), entities);
        var final = CreateState(document, document.Operations[^1].Sequence, entities);
        ValidateState(final, "final", requireCoverage: true);
        return new(
            scenarioId: document.ScenarioId,
            occurredAtUtc: document.OccurredAtUtc,
            baselineThroughSequence: document.BaselineThroughSequence,
            baseline: exactBaseline,
            final: final,
            mutationTransactions: transactions);
    }

    static FreightScenarioTransition Apply(
        JournalDocument journal,
        OperationDocument operation,
        Dictionary<FreightScenarioVersionKey, VersionedEntity> entities)
    {
        var entityKind = ParseEntity(operation.Entity, operation.Sequence);
        var operationKind = ParseOperation(operation.Operation, operation.Sequence);
        var transactionId = string.IsNullOrWhiteSpace(operation.Transaction)
            ? $"operation/{operation.Sequence}"
            : operation.Transaction;
        object? before;
        object? after;
        FreightScenarioEntityKey key;
        long version;
        switch (operationKind)
        {
            case FreightScenarioOperationKind.Upsert:
                if (operation.Document is not { ValueKind: JsonValueKind.Object } document)
                    throw new InvalidOperationException($"Scenario upsert {operation.Sequence} requires a document.");
                if (operation.Identity is not null)
                    throw new InvalidOperationException($"Scenario upsert {operation.Sequence} cannot also declare an identity.");
                after = DeserializeEntity(entityKind, document, operation.Sequence);
                key = Key(entityKind, after);
                var versionKey = new FreightScenarioVersionKey(entityKind, key);
                entities.TryGetValue(versionKey, out var prior);
                before = prior?.Value;
                version = checked((prior?.Version ?? 0) + 1);
                entities[versionKey] = new(after, version);
                break;
            case FreightScenarioOperationKind.Delete:
                if (operation.Document is not null)
                    throw new InvalidOperationException($"Scenario delete {operation.Sequence} cannot contain a document.");
                var identity = operation.Identity
                    ?? throw new InvalidOperationException($"Scenario delete {operation.Sequence} requires an identity.");
                key = new(identity.TenantId, identity.Id);
                var deleteKey = new FreightScenarioVersionKey(entityKind, key);
                if (!entities.Remove(deleteKey, out var deleted))
                    throw new InvalidOperationException($"Scenario delete {operation.Sequence} targets absent {entityKind} '{key}'.");
                before = deleted.Value;
                after = null;
                version = checked(deleted.Version + 1);
                break;
            default:
                throw new InvalidOperationException($"Unsupported scenario operation '{operation.Operation}'.");
        }

        var occurredAtUtc = journal.OccurredAtUtc.AddSeconds(operation.Sequence);
        return new(
            scenarioId: journal.ScenarioId,
            sequence: operation.Sequence,
            transactionId: transactionId,
            occurredAtUtc: occurredAtUtc,
            entity: entityKind,
            operation: operationKind,
            key: key,
            version: version,
            before: before,
            after: after,
            beforeState: CanonicalState(before),
            afterState: CanonicalState(after));
    }

    static ImmutableArray<FreightScenarioTransaction> GroupTransactions(
        JournalDocument document,
        ImmutableArray<FreightScenarioTransition> mutations,
        Dictionary<FreightScenarioVersionKey, VersionedEntity> finalEntities)
    {
        if (mutations.IsEmpty)
            return [];
        HashSet<string> completed = new(StringComparer.Ordinal);
        ImmutableArray<FreightScenarioTransaction>.Builder transactions = ImmutableArray.CreateBuilder<FreightScenarioTransaction>();
        var start = 0;
        while (start < mutations.Length)
        {
            var id = mutations[start].TransactionId;
            if (!completed.Add(id))
                throw new InvalidOperationException($"Scenario transaction '{id}' is not contiguous.");
            var end = start + 1;
            while (end < mutations.Length && string.Equals(mutations[end].TransactionId, id, StringComparison.Ordinal))
                end++;
            var transitions = mutations[start..end];
            var first = transitions[0];
            if (transitions.Any(transition => transition.Entity != first.Entity || transition.Key.TenantId != first.Key.TenantId))
            {
                throw new InvalidOperationException(
                    $"Scenario transaction '{id}' crosses an entity container or tenant partition and cannot be atomic in every provider.");
            }
            transactions.Add(new(id, first.OccurredAtUtc, transitions));
            start = end;
        }

        // Replay the baseline and validate every committed mutation cut. Validation occurs here rather than while
        // parsing individual transitions so a transaction may atomically exchange relationship state.
        Dictionary<FreightScenarioVersionKey, VersionedEntity> replay = [];
        foreach (var operation in document.Operations.Take(checked((int)document.BaselineThroughSequence)))
            _ = Apply(document, operation, replay);
        foreach (var transaction in transactions)
        {
            foreach (var operation in document.Operations.Where(candidate =>
                         candidate.Sequence >= transaction.Transitions[0].Sequence
                         && candidate.Sequence <= transaction.Transitions[^1].Sequence))
            {
                _ = Apply(document, operation, replay);
            }
            var state = CreateState(document, transaction.Transitions[^1].Sequence, replay);
            ValidateState(state, $"transaction '{transaction.Id}'", requireCoverage: false);
        }
        if (!Equivalent(replay, finalEntities))
            throw new InvalidOperationException("Scenario transaction replay differs from the final journal projection.");
        return transactions.ToImmutable();
    }

    static FreightScenarioState CreateState(
        JournalDocument journal,
        long throughSequence,
        IReadOnlyDictionary<FreightScenarioVersionKey, VersionedEntity> entities)
    {
        var orders = entities
            .Where(static pair => pair.Key.Entity == FreightScenarioEntityKind.Order)
            .Select(static pair => (FreightOrder)pair.Value.Value)
            .OrderBy(static value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var customers = entities
            .Where(static pair => pair.Key.Entity == FreightScenarioEntityKind.CustomerAccount)
            .Select(static pair => (FreightCustomerAccount)pair.Value.Value)
            .OrderBy(static value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var stops = entities
            .Where(static pair => pair.Key.Entity == FreightScenarioEntityKind.OrderStop)
            .Select(static pair => (FreightOrderStop)pair.Value.Value)
            .OrderBy(static value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(static value => value.OrderId, StringComparer.Ordinal)
            .ThenBy(static value => value.Sequence)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var locations = entities
            .Where(static pair => pair.Key.Entity == FreightScenarioEntityKind.Location)
            .Select(static pair => (FreightLocation)pair.Value.Value)
            .OrderBy(static value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            scenarioId: journal.ScenarioId,
            throughSequence: throughSequence,
            occurredAtUtc: journal.OccurredAtUtc.AddSeconds(throughSequence),
            orders: orders,
            customers: customers,
            stops: stops,
            locations: locations,
            versions: entities.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value.Version));
    }

    static void ValidateState(
        FreightScenarioState state,
        string cut,
        bool requireCoverage)
    {
        if (requireCoverage)
        {
            if (state.Orders.Length < 6)
                throw new InvalidOperationException($"Scenario {cut} must cross the two-item root-page boundary.");
            if (state.Stops.Length < 12)
                throw new InvalidOperationException($"Scenario {cut} must cross contributor lookup boundaries.");
            if (state.TenantCount < 2)
                throw new InvalidOperationException($"Scenario {cut} requires at least two tenants.");
        }
        HashSet<FreightScenarioEntityKey> customers = state.Customers
            .Select(static value => new FreightScenarioEntityKey(value.TenantId, value.Id))
            .ToHashSet();
        HashSet<FreightScenarioEntityKey> locations = state.Locations
            .Select(static value => new FreightScenarioEntityKey(value.TenantId, value.Id))
            .ToHashSet();
        HashSet<FreightScenarioEntityKey> orders = state.Orders
            .Select(static value => new FreightScenarioEntityKey(value.TenantId, value.Id))
            .ToHashSet();
        foreach (var order in state.Orders)
        {
            RequireText(order.TenantId, $"Scenario {cut} Order tenant");
            RequireText(order.Id, $"Scenario {cut} Order identity");
            RequireText(order.OrderNumber, $"Scenario {cut} Order number");
            RequireText(order.EquipmentClass, $"Scenario {cut} Order equipment");
            if (!customers.Contains(new(order.TenantId, order.CustomerAccountId)))
            {
                throw new InvalidOperationException(
                    $"Scenario {cut} Order '{order.TenantId}/{order.Id}' references a missing customer.");
            }
        }
        foreach (var stop in state.Stops)
        {
            RequireText(stop.TenantId, $"Scenario {cut} OrderStop tenant");
            RequireText(stop.Id, $"Scenario {cut} OrderStop identity");
            if (stop.Sequence <= 0)
                throw new InvalidOperationException($"Scenario {cut} OrderStop '{stop.TenantId}/{stop.Id}' has a nonpositive sequence.");
            if (stop.StopType is not ("Pickup" or "Drop"))
                throw new InvalidOperationException($"Scenario {cut} OrderStop '{stop.TenantId}/{stop.Id}' has an unsupported type.");
            if (!orders.Contains(new(stop.TenantId, stop.OrderId)))
                throw new InvalidOperationException($"Scenario {cut} OrderStop '{stop.TenantId}/{stop.Id}' references a missing order.");
            if (!locations.Contains(new(stop.TenantId, stop.LocationId)))
                throw new InvalidOperationException($"Scenario {cut} OrderStop '{stop.TenantId}/{stop.Id}' references a missing location.");
            if (stop.ScheduledStart > stop.ScheduledEnd)
                throw new InvalidOperationException($"Scenario {cut} OrderStop '{stop.TenantId}/{stop.Id}' has an inverted schedule.");
        }
        foreach (var group in state.Stops.GroupBy(static stop => new FreightScenarioEntityKey(stop.TenantId, stop.OrderId)))
        {
            if (group.Select(static stop => stop.Sequence).Distinct().Count() != group.Count())
                throw new InvalidOperationException($"Scenario {cut} Order '{group.Key}' repeats a stop sequence.");
            if (group.Count(static stop => stop.StopType == "Pickup") != 1)
                throw new InvalidOperationException($"Scenario {cut} Order '{group.Key}' requires one pickup.");
            if (!group.Any(static stop => stop.StopType == "Drop"))
                throw new InvalidOperationException($"Scenario {cut} Order '{group.Key}' requires a drop.");
        }
        if (requireCoverage)
        {
            if (!state.Stops.Select(static stop => new FreightScenarioEntityKey(stop.TenantId, stop.OrderId)).ToHashSet()
                .SetEquals(orders))
            {
                throw new InvalidOperationException($"Scenario {cut} requires stops for every order.");
            }
            if (!state.Orders.GroupBy(static order => new FreightScenarioEntityKey(order.TenantId, order.CustomerAccountId))
                .Any(static group => group.Count() > 1))
            {
                throw new InvalidOperationException($"Scenario {cut} requires a customer shared by multiple orders.");
            }
            if (!state.Stops.GroupBy(static stop => new FreightScenarioEntityKey(stop.TenantId, stop.LocationId))
                .Any(static group => group.Select(static stop => stop.OrderId).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                throw new InvalidOperationException($"Scenario {cut} requires a location shared by multiple orders.");
            }
        }
    }

    static object DeserializeEntity(FreightScenarioEntityKind entity, JsonElement document, long sequence) => entity switch
    {
        FreightScenarioEntityKind.Order => Deserialize<OrderDocument>(document, sequence).ToEntity(),
        FreightScenarioEntityKind.CustomerAccount => Deserialize<CustomerDocument>(document, sequence).ToEntity(),
        FreightScenarioEntityKind.OrderStop => Deserialize<StopDocument>(document, sequence).ToEntity(),
        FreightScenarioEntityKind.Location => Deserialize<LocationDocument>(document, sequence).ToEntity(),
        _ => throw new InvalidOperationException($"Scenario operation {sequence} has unsupported entity '{entity}'.")
    };

    static T Deserialize<T>(JsonElement document, long sequence)
        where T : class => document.Deserialize<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Scenario operation {sequence} has an empty entity document.");

    static FreightScenarioEntityKey Key(FreightScenarioEntityKind entity, object value) => (entity, value) switch
    {
        (FreightScenarioEntityKind.Order, FreightOrder order) => new(order.TenantId, order.Id),
        (FreightScenarioEntityKind.CustomerAccount, FreightCustomerAccount customer) => new(customer.TenantId, customer.Id),
        (FreightScenarioEntityKind.OrderStop, FreightOrderStop stop) => new(stop.TenantId, stop.Id),
        (FreightScenarioEntityKind.Location, FreightLocation location) => new(location.TenantId, location.Id),
        _ => throw new InvalidOperationException($"Scenario value does not match entity kind '{entity}'.")
    };

    static JsonElement? CanonicalState(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);

    static FreightScenarioEntityKind ParseEntity(string value, long sequence) => value switch
    {
        "order" => FreightScenarioEntityKind.Order,
        "customerAccount" => FreightScenarioEntityKind.CustomerAccount,
        "orderStop" => FreightScenarioEntityKind.OrderStop,
        "location" => FreightScenarioEntityKind.Location,
        _ => throw new InvalidOperationException($"Scenario operation {sequence} has unsupported entity '{value}'.")
    };

    static FreightScenarioOperationKind ParseOperation(string value, long sequence) => value switch
    {
        "upsert" => FreightScenarioOperationKind.Upsert,
        "delete" => FreightScenarioOperationKind.Delete,
        _ => throw new InvalidOperationException($"Scenario operation {sequence} has unsupported mutation '{value}'.")
    };

    static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} cannot be empty.");
    }

    static bool Equivalent(
        IReadOnlyDictionary<FreightScenarioVersionKey, VersionedEntity> left,
        IReadOnlyDictionary<FreightScenarioVersionKey, VersionedEntity> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var candidate)
                || value.Version != candidate.Version
                || !Equals(value.Value, candidate.Value))
            {
                return false;
            }
        }
        return true;
    }

    sealed record VersionedEntity(object Value, long Version);

    sealed record JournalDocument(
        string SchemaVersion,
        string ScenarioId,
        DateTimeOffset OccurredAtUtc,
        long BaselineThroughSequence,
        ImmutableArray<OperationDocument> Operations);

    sealed record OperationDocument(
        long Sequence,
        string Entity,
        string Operation,
        string? Transaction,
        JsonElement? Document,
        IdentityDocument? Identity);

    sealed record IdentityDocument(string TenantId, string Id);

    sealed record OrderDocument(
        string TenantId,
        string OrderId,
        string OrderNumber,
        string CustomerAccountId,
        string EquipmentClass,
        DateTimeOffset CreatedAt)
    {
        internal FreightOrder ToEntity() => new()
        {
            Id = OrderId,
            TenantId = TenantId,
            OrderNumber = OrderNumber,
            CustomerAccountId = CustomerAccountId,
            EquipmentClass = EquipmentClass,
            CreatedAt = CreatedAt
        };
    }

    sealed record CustomerDocument(string TenantId, string CustomerAccountId, string DisplayName)
    {
        internal FreightCustomerAccount ToEntity() => new()
        {
            Id = CustomerAccountId,
            TenantId = TenantId,
            DisplayName = DisplayName
        };
    }

    sealed record StopDocument(
        string TenantId,
        string OrderStopId,
        string OrderId,
        int Sequence,
        string StopType,
        string LocationId,
        DateTimeOffset ScheduledStart,
        DateTimeOffset ScheduledEnd)
    {
        internal FreightOrderStop ToEntity() => new()
        {
            Id = OrderStopId,
            TenantId = TenantId,
            OrderId = OrderId,
            Sequence = Sequence,
            StopType = StopType,
            LocationId = LocationId,
            ScheduledStart = ScheduledStart,
            ScheduledEnd = ScheduledEnd
        };
    }

    sealed record LocationDocument(
        string TenantId,
        string LocationId,
        string DisplayName,
        string City,
        string Region)
    {
        internal FreightLocation ToEntity() => new()
        {
            Id = LocationId,
            TenantId = TenantId,
            DisplayName = DisplayName,
            City = City,
            Region = Region
        };
    }
}
