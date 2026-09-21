using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Infra.Realization;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Azure.Infra;

/// <summary>Independently reviewed runtime endpoint declarations; not derived from producer responses.</summary>
/// <param name="SchemaVersion">Exact supported declaration version.</param>
/// <param name="Scope">Expected infrastructure deployment association.</param>
/// <param name="Endpoints">One complete runtime contract and unique endpoint per declared resource.</param>
public sealed record AzureInfrastructureRuntimeBindings(string SchemaVersion, AzureInfrastructureObservationScope Scope,
    ImmutableArray<AzureInfrastructureRuntimeEndpoint> Endpoints)
{
    /// <summary>Supported endpoint declaration schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.azure-runtime-bindings/1";
    static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Parses bounded declaration JSON, rejecting duplicate and unknown fields. Does not establish trust or admit declarations.</summary>
    /// <param name="utf8Json">At most four MiB of caller-owned UTF-8 JSON; never retained.</param>
    /// <returns>Declarations requiring independent trust and Validate before I/O.</returns>
    /// <exception cref="ArgumentException">The document is oversized, empty or contains duplicate fields.</exception>
    /// <exception cref="JsonException">JSON or its typed contract is invalid.</exception>
    public static AzureInfrastructureRuntimeBindings Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > 4 * 1024 * 1024) throw new ArgumentException("Runtime declarations exceed the input limit.");
        using var document = JsonDocument.Parse(utf8Json);
        if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, "", out _)) throw new ArgumentException("Duplicate runtime declaration fields.");
        return document.Deserialize<AzureInfrastructureRuntimeBindings>(JsonOptions) ?? throw new ArgumentException("Runtime declarations are required.");
    }

    /// <summary>Checks exact scope and native associations against the independently retained deployment before any network operation.</summary>
    /// <param name="realization">Authoritative realization.</param>
    /// <param name="expectedScope">Explicit trusted deployment scope.</param>
    /// <param name="nativeBindings">Checked deployment output; endpoint contracts must use its exact associations.</param>
    /// <param name="at">UTC validation time.</param>
    /// <param name="maximumAge">Positive source age limit.</param>
    /// <param name="futureTolerance">Nonnegative source clock tolerance.</param>
    /// <exception cref="ArgumentException">Version, scope, identity or declarations are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Time policy is invalid.</exception>
    public void Validate(InfrastructureRealization realization, AzureInfrastructureObservationScope expectedScope,
        AzureInfrastructureReadinessBindings nativeBindings, DateTimeOffset at, TimeSpan maximumAge, TimeSpan futureTolerance)
    {
        ArgumentNullException.ThrowIfNull(nativeBindings);
        nativeBindings.Validate(realization, expectedScope, at, maximumAge, futureTolerance);
        if (SchemaVersion != CurrentSchemaVersion || Scope != expectedScope) throw new ArgumentException("Runtime declaration version or scope mismatch.");
        AzureInfrastructureRuntimeCollector.Validate(realization, expectedScope, Endpoints, at, maximumAge, futureTolerance);
        var native = nativeBindings.Bindings.ToHashSet();
        if (Endpoints.Any(endpoint => !native.Contains(endpoint.Contract.Binding)))
            throw new ArgumentException("Runtime declarations must retain the exact deployment's native associations.");
    }
}
