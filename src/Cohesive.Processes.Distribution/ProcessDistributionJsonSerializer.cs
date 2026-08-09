using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Distribution;

/// <summary>Strict portable JSON wire contract for distribution definitions and ledger state.</summary>
public static class ProcessDistributionJsonSerializer
{
    static readonly JsonSerializerOptions CompactOptions = StrictDocumentJson.CreateOptions();

    /// <summary>Creates independently mutable strict distribution JSON options.</summary>
    /// <param name="formatting">Compact or indented output formatting.</param>
    /// <returns>Fresh closed, case-sensitive, unknown-member-rejecting options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one logical worker-pool definition.</summary>
    /// <param name="pool">Exact versioned pool definition.</param>
    /// <returns>Strict portable JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    public static string SerializePool(ProcessWorkerPoolDefinition pool) =>
        JsonSerializer.Serialize(pool ?? throw new ArgumentNullException(nameof(pool)), CompactOptions);

    /// <summary>Deserializes one logical worker-pool definition.</summary>
    /// <param name="json">Strict portable JSON.</param>
    /// <returns>The validated exact pool definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON or semantic constructor invariants are invalid.</exception>
    public static ProcessWorkerPoolDefinition DeserializePool(string json) =>
        JsonSerializer.Deserialize<ProcessWorkerPoolDefinition>(
            json ?? throw new ArgumentNullException(nameof(json)),
            CompactOptions) ?? throw new JsonException("A distribution pool document cannot be null.");

    /// <summary>Serializes one worker registration.</summary>
    /// <param name="worker">Exact worker offer, health, drain, and lease evidence.</param>
    /// <returns>Strict portable JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="worker"/> is <see langword="null"/>.</exception>
    public static string SerializeWorker(ProcessWorkerRegistration worker) =>
        JsonSerializer.Serialize(worker ?? throw new ArgumentNullException(nameof(worker)), CompactOptions);

    /// <summary>Deserializes one worker registration.</summary>
    /// <param name="json">Strict portable JSON.</param>
    /// <returns>The validated exact worker registration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON or semantic constructor invariants are invalid.</exception>
    public static ProcessWorkerRegistration DeserializeWorker(string json) =>
        JsonSerializer.Deserialize<ProcessWorkerRegistration>(
            json ?? throw new ArgumentNullException(nameof(json)),
            CompactOptions) ?? throw new JsonException("A distribution worker document cannot be null.");

    /// <summary>Serializes one complete work-ledger snapshot.</summary>
    /// <param name="work">Exact current work-ledger state.</param>
    /// <returns>Strict portable JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    public static string SerializeWork(ProcessWorkRecord work) =>
        JsonSerializer.Serialize(work ?? throw new ArgumentNullException(nameof(work)), CompactOptions);

    /// <summary>Deserializes one complete work-ledger snapshot.</summary>
    /// <param name="json">Strict portable JSON.</param>
    /// <returns>The validated exact work-ledger snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON or semantic constructor invariants are invalid.</exception>
    public static ProcessWorkRecord DeserializeWork(string json) =>
        JsonSerializer.Deserialize<ProcessWorkRecord>(
            json ?? throw new ArgumentNullException(nameof(json)),
            CompactOptions) ?? throw new JsonException("A distribution work document cannot be null.");

    /// <summary>Serializes one complete provider-neutral distribution ledger.</summary>
    /// <param name="ledger">Exact pool, fairness, worker, and work state.</param>
    /// <returns>Strict portable JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ledger"/> is <see langword="null"/>.</exception>
    public static string SerializeLedger(ProcessDistributionLedgerDocument ledger) =>
        JsonSerializer.Serialize(ledger ?? throw new ArgumentNullException(nameof(ledger)), CompactOptions);

    /// <summary>Deserializes one complete provider-neutral distribution ledger.</summary>
    /// <param name="json">Strict portable JSON.</param>
    /// <returns>The validated exact distribution ledger.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON or semantic constructor invariants are invalid.</exception>
    public static ProcessDistributionLedgerDocument DeserializeLedger(string json) =>
        JsonSerializer.Deserialize<ProcessDistributionLedgerDocument>(
            json ?? throw new ArgumentNullException(nameof(json)),
            CompactOptions) ?? throw new JsonException("A distribution ledger document cannot be null.");

    /// <summary>Gets deterministic canonical bytes for one pool definition.</summary>
    /// <param name="pool">Exact versioned pool definition.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    public static byte[] GetCanonicalPoolBytes(ProcessWorkerPoolDefinition pool) =>
        StrictDocumentJson.GetCanonicalBytes(
            pool ?? throw new ArgumentNullException(nameof(pool)),
            CompactOptions);

    /// <summary>Gets deterministic canonical bytes for one worker offer.</summary>
    /// <param name="offer">Exact versioned worker offer.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="offer"/> is <see langword="null"/>.</exception>
    public static byte[] GetCanonicalOfferBytes(ProcessWorkerOffer offer) =>
        StrictDocumentJson.GetCanonicalBytes(
            offer ?? throw new ArgumentNullException(nameof(offer)),
            CompactOptions);
}
