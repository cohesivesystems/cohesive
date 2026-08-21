using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Relations.Compilation;

namespace Cohesive.MaterializationHarness.Model;

/// <summary>Single naming and schema authority for the local freight provider change-feed realizations.</summary>
public static class FreightMaterializationChangeFeedConventions
{
    /// <summary>PostgreSQL publication containing every canonical freight entity table.</summary>
    public const string PostgresPublicationName = "cohesive_freight_harness";

    /// <summary>Current Cosmos emulator change-envelope schema.</summary>
    public const string CosmosEnvelopeSchemaVersion = "materialization-scenario-change/v1";

    /// <summary>Cosmos document discriminator for emulator-compatible change envelopes.</summary>
    public const string CosmosEnvelopeDocumentKind = "materialization-scenario-change";

    /// <summary>Creates the dedicated PostgreSQL slot name for one exact tenant and acquisition input.</summary>
    /// <param name="tenant">Stable tenant identity.</param>
    /// <param name="input">Canonical Relations acquisition input.</param>
    /// <returns>A deterministic lowercase PostgreSQL replication-slot identifier.</returns>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    public static string PostgresSlotName(string tenant, RelationQueryInputId input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A freight change feed requires a canonical acquisition input.", nameof(input));
        return $"cohesive_freight_{Digest(tenant, 10)}_{Digest(input.Value, 16)}";
    }

    /// <summary>Creates the operator-owned PostgreSQL slot-incarnation identity for one scenario baseline.</summary>
    /// <param name="journal">Canonical scenario journal.</param>
    /// <param name="tenant">Stable tenant identity.</param>
    /// <param name="input">Canonical Relations acquisition input.</param>
    /// <returns>A deterministic non-secret slot generation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    public static string PostgresSlotGeneration(
        FreightScenarioJournal journal,
        string tenant,
        RelationQueryInputId input)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"materialization-harness/{Digest(journal.ScenarioId, 16)}@baseline-{journal.BaselineThroughSequence}/{PostgresSlotName(tenant, input)}");
    }

    static string Digest(string value, int characters)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..characters];
    }
}
