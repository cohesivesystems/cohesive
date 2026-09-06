using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.SQLite;

/// <summary>Physical presence bit for one optional placed field.</summary>
/// <param name="Input">Exact compiled field input.</param>
/// <param name="Column">INTEGER NOT NULL column containing only 0 (missing) or 1 (present).</param>
public sealed record SqliteRelationQueryFieldPresence(RelationQueryInputId Input, string Column);

/// <summary>Physical table and attributable guarantees supplementing canonical source placement.</summary>
/// <remarks>
/// The named authority must establish a complete table whose fields use <see cref="SqliteScalarCodec"/>
/// encodings, whose placement identity is a non-null unique integer/text tuple, and whose presence columns contain 0/1.
/// Missing fields must have SQL NULL payloads. Placement selectors are literal column names. The compiler trusts
/// this schema/ingestion evidence; it does not scan data or infer guarantees from a sample.
/// Scalar text identities must be nonblank, as required by canonical observation identities. Individual text
/// components of a composite identity may be empty because their framed tuple encoding is always nonblank.
/// </remarks>
/// <param name="Placement">Canonical placement binding selecting this table.</param>
/// <param name="Table">Table name in the connection's main database.</param>
/// <param name="Authority">Versioned schema/ingestion contract establishing the documented guarantees.</param>
/// <param name="Presence">Optional-field presence mappings; required fields need no mapping.</param>
/// <param name="IdentityFields">Ordered components of a declared unique key. When empty, the placement identity selects one field.
/// Components reference canonical field inputs; column mappings remain authoritative in placement. A composite identity uses
/// a source-native placement selector with no single semantic path.</param>
/// <param name="AsciiOrderingFields">Text field inputs whose values the table authority guarantees contain only U+0000 through
/// U+007F. In this domain SQLite BINARY ordering equals canonical UTF-16 ordinal ordering. No data sampling is performed.</param>
public sealed record SqliteRelationQueryTableBinding(
    RelationQuerySourcePlacementBindingId Placement, string Table, string Authority,
    ImmutableArray<SqliteRelationQueryFieldPresence> Presence = default,
    ImmutableArray<RelationQueryInputId> IdentityFields = default,
    ImmutableArray<RelationQueryInputId> AsciiOrderingFields = default);

/// <summary>Immutable, fingerprinted SQLite physical evidence pinned to one exact source placement.</summary>
public sealed class SqliteRelationQueryStorageBinding
{
    /// <summary>Storage interpretation and serialization contract version.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.sqlite-storage/v2";

    /// <summary>Normalizes table evidence and pins it to the placement and compiled plan.</summary>
    /// <param name="placement">Exact canonical placement; its selectors supply all field and identity mappings.</param>
    /// <param name="tables">One mapping per participating source placement.</param>
    /// <param name="schemaVersion">Persisted storage contract version; defaults to the current version.</param>
    /// <param name="fingerprint">Optional persisted fingerprint to verify against normalized declarations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is null.</exception>
    /// <exception cref="ArgumentException">A declaration is empty or repeated, the version is unsupported, or the fingerprint is stale.</exception>
    public SqliteRelationQueryStorageBinding(RelationQuerySourcePlacement placement,
        ImmutableArray<SqliteRelationQueryTableBinding> tables, string schemaVersion = CurrentSchemaVersion,
        RelationQueryAdapterBindingFingerprint? fingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported SQLite storage-binding version.", nameof(schemaVersion));
        SchemaVersion = schemaVersion;
        Placement = placement;
        if (tables.IsDefaultOrEmpty || tables.Any(static table => table is null
                || string.IsNullOrWhiteSpace(table.Placement.Value) || string.IsNullOrWhiteSpace(table.Table)
                || string.IsNullOrWhiteSpace(table.Authority))
            || tables.Select(static table => table.Placement).Distinct().Count() != tables.Length)
            throw new ArgumentException("Unique table placements and explicit schema authorities are required.", nameof(tables));
        var normalized = ImmutableArray.CreateBuilder<SqliteRelationQueryTableBinding>(tables.Length);
        foreach (var table in tables.OrderBy(static table => table.Placement.Value, StringComparer.Ordinal))
        {
            var presence = table.Presence.IsDefault ? [] : table.Presence;
            if (presence.Any(static field => field is null || string.IsNullOrWhiteSpace(field.Input.Value)
                    || string.IsNullOrWhiteSpace(field.Column))
                || presence.Select(static field => field.Input).Distinct().Count() != presence.Length)
                throw new ArgumentException("Presence columns require unique field inputs and nonempty column names.", nameof(tables));
            var identityFields = table.IdentityFields.IsDefault ? [] : table.IdentityFields;
            var asciiFields = table.AsciiOrderingFields.IsDefault ? [] : table.AsciiOrderingFields;
            if (identityFields.Concat(asciiFields).Any(static field => string.IsNullOrWhiteSpace(field.Value))
                || identityFields.Distinct().Count() != identityFields.Length
                || asciiFields.Distinct().Count() != asciiFields.Length)
                throw new ArgumentException("Identity and ASCII evidence require distinct, nonempty field inputs.", nameof(tables));
            normalized.Add(table with
            {
                Presence = [.. presence.OrderBy(static field => field.Input.Value, StringComparer.Ordinal)],
                IdentityFields = identityFields,
                AsciiOrderingFields = [.. asciiFields.OrderBy(static field => field.Value, StringComparer.Ordinal)]
            });
        }
        Tables = normalized.MoveToImmutable();
        Fingerprint = new("sha256", SchemaVersion + "-c14n/v1", SqliteRelationQueryHash.Compute(new
        {
            SchemaVersion, Placement = placement.Fingerprint, Tables
        }));
        if (fingerprint is not null && fingerprint != Fingerprint)
            throw new ArgumentException("SQLite storage-binding fingerprint does not match its declarations.", nameof(fingerprint));
    }

    /// <summary>Exact version of the persisted storage contract.</summary>
    public string SchemaVersion { get; }
    /// <summary>Canonical field selectors, source domains and plan affinity.</summary>
    public RelationQuerySourcePlacement Placement { get; }
    /// <summary>Normalized physical evidence in placement identity order.</summary>
    public ImmutableArray<SqliteRelationQueryTableBinding> Tables { get; }
    /// <summary>Fingerprint covering all mapping and guarantee declarations and exact placement.</summary>
    public RelationQueryAdapterBindingFingerprint Fingerprint { get; }

    internal RelationQueryAdapterBindingReference Reference(RelationQueryCompilationSelection selection) => new(
        SchemaVersion, "sqlite/storage", SqliteRelationQueryTargetProfile.Target, SqliteRelationQueryTargetProfile.ProfileId,
        Fingerprint, RelationQueryCompiledPlanReferenceFingerprinter.Compute(Placement.Plan), Placement.Fingerprint,
        [.. selection.SourceInstances.Select(static source => source.Id)],
        [.. selection.PlacementBindings.Select(static binding => binding.Id)],
        [new("compilerProfile", EffectiveConfigurationOrigin.AdapterConvention, SqliteRelationQueryCompiler.CompilerProfile),
         .. Tables.Select(static table => new EffectiveConfigurationDecision(
             $"table/{table.Placement.Value}", EffectiveConfigurationOrigin.Explicit, table.Authority))]);
}

static class SqliteRelationQueryHash
{
    // Inputs are normalized immutable records; declaration/property order is versioned by the c14n profile.
    public static string Compute<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
