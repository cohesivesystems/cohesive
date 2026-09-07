using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Storage.Commits;

/// <summary>A logical item identity within an executor's configured storage authority.</summary>
public sealed record StorageCommitAddress
{
    /// <summary>Creates a logical address, independent of a table or container name.</summary>
    /// <param name="target">Logical repository or collection binding.</param>
    /// <param name="partition">Logical partition identity.</param>
    /// <param name="id">Item or operation identity within the target and partition.</param>
    /// <exception cref="ArgumentException">An identity is empty or contains invalid Unicode.</exception>
    public StorageCommitAddress(string target, string partition, string id)
    {
        Target = StorageCommitJson.RequireIdentity(target);
        Partition = StorageCommitJson.RequireIdentity(partition);
        Id = StorageCommitJson.RequireIdentity(id);
    }

    /// <summary>Logical target resolved by the executor configuration.</summary>
    public string Target { get; }
    /// <summary>Logical partition identity.</summary>
    public string Partition { get; }
    /// <summary>Identity within the target and partition.</summary>
    public string Id { get; }
}

/// <summary>A conditional creation or replacement; unconditional overwrites are outside this profile.</summary>
public sealed record StorageCommitWrite
{
    /// <summary>Creates a complete item replacement with an explicit presence/version precondition.</summary>
    /// <param name="address">Logical item identity.</param>
    /// <param name="value">Materialized, immutable portable value to retain.</param>
    /// <param name="expectedToken">Exact prior token; null requires absence.</param>
    /// <exception cref="ArgumentNullException">An address or value is null.</exception>
    /// <exception cref="ArgumentException">The value is unresolved or the token is empty.</exception>
    public StorageCommitWrite(StorageCommitAddress address, PortableValue value, EntityConcurrencyToken? expectedToken = null)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Value = StorageCommitJson.RequireValue(value);
        ExpectedToken = expectedToken;
        if (expectedToken is { } token) StorageCommitJson.RequireIdentity(token.Value);
    }

    /// <summary>Item to create or replace.</summary>
    public StorageCommitAddress Address { get; }
    /// <summary>Complete materialized replacement, never a callback.</summary>
    public PortableValue Value { get; }
    /// <summary>Null requires absence; otherwise the current token must match exactly.</summary>
    public EntityConcurrencyToken? ExpectedToken { get; }
}

/// <summary>An attributable query dependency protected by a participating guard write.</summary>
/// <remarks>
/// Capture the guard token BEFORE querying. Every writer affecting the predicate must advance this same guard.
/// The query must observe at least the guard read's committed state. The executor enforces the guard CAS, not
/// this application-wide writer protocol or query semantics. A missing guard is explicitly unsupported.
/// </remarks>
public sealed record StorageCommitQueryDependency
{
    /// <summary>Declares a query decision and its explicit guard protocol.</summary>
    /// <param name="queryFingerprint">Exact query, arguments and read-contract revision fingerprint.</param>
    /// <param name="guard">Address of the guard write in this intent; null denotes an unprotected query.</param>
    /// <exception cref="ArgumentException">The fingerprint is empty or invalid Unicode.</exception>
    public StorageCommitQueryDependency(string queryFingerprint, StorageCommitAddress? guard = null)
    {
        QueryFingerprint = StorageCommitJson.RequireIdentity(queryFingerprint);
        Guard = guard;
    }

    /// <summary>Provenance of the query and arguments that produced the decision.</summary>
    public string QueryFingerprint { get; }
    /// <summary>Guard whose prior token was captured before the query and is fenced by this intent.</summary>
    public StorageCommitAddress? Guard { get; }
}

/// <summary>Versioned, immutable all-or-nothing writes and retained result for one operation.</summary>
/// <remarks>
/// All writes and the receipt commit together. There is no weaker fallback. Write order is canonicalized by
/// address; duplicate addresses are invalid. A successful write's new token is the intent fingerprint.
/// Receipt identities occupy a separate namespace from item identities. Receipts must be retained for the
/// entire retry horizon; deleting them invalidates exact replay guarantees.
/// </remarks>
public sealed class StorageCommitIntent
{
    /// <summary>Creates and fingerprints a bounded atomic commit declaration.</summary>
    /// <param name="receiptAddress">Authority-scoped operation identity, reused for exact retries.</param>
    /// <param name="writes">Nonempty set of conditional item replacements.</param>
    /// <param name="result">Materialized application result retained with the writes.</param>
    /// <param name="queryDependencies">Query provenance and explicit guard requirements.</param>
    /// <param name="formatVersion">Wire contract revision; only one is supported.</param>
    /// <exception cref="ArgumentNullException">The receipt address or result is null.</exception>
    /// <exception cref="ArgumentException">Writes are empty, duplicated or null, or values are unresolved.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The wire revision is unsupported.</exception>
    [JsonConstructor]
    public StorageCommitIntent(StorageCommitAddress receiptAddress, ImmutableArray<StorageCommitWrite> writes,
        PortableValue result, ImmutableArray<StorageCommitQueryDependency> queryDependencies = default,
        int formatVersion = 1)
    {
        if (formatVersion != 1) throw new ArgumentOutOfRangeException(nameof(formatVersion));
        ReceiptAddress = receiptAddress ?? throw new ArgumentNullException(nameof(receiptAddress));
        if (writes.IsDefaultOrEmpty)
            throw new ArgumentException("A commit requires non-null conditional writes.", nameof(writes));
        HashSet<StorageCommitAddress> addresses = new(writes.Length);
        var canonical = true;
        for (var index = 0; index < writes.Length; index++)
        {
            var write = writes[index] ?? throw new ArgumentException("Writes cannot contain null.", nameof(writes));
            if (!addresses.Add(write.Address))
                throw new ArgumentException("A commit cannot write an item twice.", nameof(writes));
            if (index > 0 && CompareAddress(writes[index - 1].Address, write.Address) > 0) canonical = false;
        }
        Writes = canonical ? writes : writes.Sort(static (left, right) => CompareAddress(left.Address, right.Address));
        Result = StorageCommitJson.RequireValue(result);
        QueryDependencies = queryDependencies.IsDefault ? [] : queryDependencies;
        if (QueryDependencies.Any(static dependency => dependency is null))
            throw new ArgumentException("Query dependencies cannot contain null.", nameof(queryDependencies));
        FormatVersion = formatVersion;
        Fingerprint = StorageCommitJson.ComputeFingerprint(this);
    }

    static int CompareAddress(StorageCommitAddress left, StorageCommitAddress right)
    {
        var target = StringComparer.Ordinal.Compare(left.Target, right.Target);
        if (target != 0) return target;
        var partition = StringComparer.Ordinal.Compare(left.Partition, right.Partition);
        return partition != 0 ? partition : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    /// <summary>Persisted semantic wire revision.</summary>
    public int FormatVersion { get; }
    /// <summary>Operation identity resolved within the configured storage authority.</summary>
    public StorageCommitAddress ReceiptAddress { get; }
    /// <summary>Canonical address-ordered write set.</summary>
    public ImmutableArray<StorageCommitWrite> Writes { get; }
    /// <summary>Exact materialized result to retain.</summary>
    public PortableValue Result { get; }
    /// <summary>Declared query dependencies, including any unsupported unguarded dependencies.</summary>
    public ImmutableArray<StorageCommitQueryDependency> QueryDependencies { get; }
    /// <summary>Computed canonical content fingerprint; not an independently writable wire field.</summary>
    [JsonIgnore]
    public string Fingerprint { get; }
    /// <summary>Exact reconciliation identity; no later state read is needed to interpret its receipt.</summary>
    [JsonIgnore]
    public StorageCommitReference Reference => new(ReceiptAddress, Fingerprint);
}

/// <summary>Authority-scoped operation identity and exact expected content.</summary>
public sealed record StorageCommitReference
{
    /// <summary>Creates an exact operation reference.</summary>
    /// <param name="address">Operation receipt address.</param>
    /// <param name="fingerprint">Expected versioned canonical commit fingerprint.</param>
    /// <exception cref="ArgumentNullException">The address is null.</exception>
    /// <exception cref="ArgumentException">The fingerprint is empty.</exception>
    public StorageCommitReference(StorageCommitAddress address, string fingerprint)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Fingerprint = StorageCommitJson.RequireIdentity(fingerprint);
    }
    /// <summary>Receipt address, separate from ordinary item storage.</summary>
    public StorageCommitAddress Address { get; }
    /// <summary>Expected exact intent fingerprint.</summary>
    public string Fingerprint { get; }
}

/// <summary>Immutable state observed from a committed item.</summary>
/// <param name="Value">Retained portable value.</param>
/// <param name="Token">Opaque version to use in a conditional replacement.</param>
public sealed record StorageCommitItem(PortableValue Value, EntityConcurrencyToken Token);
