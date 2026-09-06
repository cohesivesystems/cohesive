using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.ExternalProcess;

/// <summary>One strict, correlated request for a finite generation-catalog snapshot.</summary>
/// <remarks>
/// The request is transport input to a transient producer. It is not retained executable simulation authority;
/// successful imports materialize exact response values into a <c>GenerationCatalogDocument</c> instead.
/// </remarks>
public sealed class ExternalGenerationCatalogRequest
{
    /// <summary>Restores and validates one protocol request.</summary>
    /// <param name="schemaVersion">Exact external catalog-provider protocol version.</param>
    /// <param name="requestId">Deterministic identity of every other request field.</param>
    /// <param name="catalogId">Stable logical identity of the catalog being produced.</param>
    /// <param name="catalogRevision">Exact application-owned revision being produced.</param>
    /// <param name="count">Positive number of values requested.</param>
    /// <param name="seed">Signed 64-bit producer-local seed.</param>
    /// <param name="locale">Optional exact provider locale.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time.</param>
    /// <param name="valueType">Portable contract every returned value must satisfy.</param>
    /// <param name="configuration">Provider-owned declarative configuration object.</param>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required identity is empty, the schema or request identity is invalid, <paramref name="count"/> is not
    /// positive, the reference time is not UTC, or configuration is not a JSON object.
    /// </exception>
    [JsonConstructor]
    public ExternalGenerationCatalogRequest(
        string schemaVersion,
        string requestId,
        string catalogId,
        string catalogRevision,
        int count,
        long seed,
        string? locale,
        DateTimeOffset? dateTimeReferenceUtc,
        TypeRef valueType,
        JsonElement configuration)
    {
        SchemaVersion = ExternalGenerationCatalogProtocol.RequireSchema(schemaVersion);
        CatalogId = Guard.RequireNotNullOrWhiteSpace(catalogId);
        CatalogRevision = Guard.RequireNotNullOrWhiteSpace(catalogRevision);
        Count = count > 0
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, "An external catalog request requires at least one value.");
        Seed = seed;
        Locale = ExternalGenerationCatalogProtocol.NormalizeOptionalCoordinate(locale, nameof(locale));
        DateTimeReferenceUtc = ExternalGenerationCatalogProtocol.RequireUtcDateTimeReference(
            dateTimeReferenceUtc,
            nameof(dateTimeReferenceUtc));
        ValueType = Guard.RequireNotNull(valueType);
        Configuration = ExternalGenerationCatalogProtocol.NormalizeConfiguration(configuration, nameof(configuration));

        var expectedRequestId = ExternalGenerationCatalogProtocol.ComputeRequestId(this);
        RequestId = string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)
            ? requestId
            : throw new ArgumentException(
                $"External catalog request identity '{requestId}' does not match '{expectedRequestId}'.",
                nameof(requestId));
    }

    internal ExternalGenerationCatalogRequest(
        string catalogId,
        string catalogRevision,
        int count,
        long seed,
        string? locale,
        DateTimeOffset? dateTimeReferenceUtc,
        TypeRef valueType,
        JsonElement configuration)
    {
        SchemaVersion = ExternalGenerationCatalogProtocol.CurrentSchemaVersion;
        CatalogId = Guard.RequireNotNullOrWhiteSpace(catalogId);
        CatalogRevision = Guard.RequireNotNullOrWhiteSpace(catalogRevision);
        Count = count > 0
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, "An external catalog request requires at least one value.");
        Seed = seed;
        Locale = ExternalGenerationCatalogProtocol.NormalizeOptionalCoordinate(locale, nameof(locale));
        DateTimeReferenceUtc = ExternalGenerationCatalogProtocol.RequireUtcDateTimeReference(
            dateTimeReferenceUtc,
            nameof(dateTimeReferenceUtc));
        ValueType = Guard.RequireNotNull(valueType);
        Configuration = ExternalGenerationCatalogProtocol.NormalizeConfiguration(configuration, nameof(configuration));
        RequestId = ExternalGenerationCatalogProtocol.ComputeRequestId(this);
    }

    /// <summary>Gets the exact external catalog-provider protocol version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the deterministic identity of every other request field.</summary>
    public string RequestId { get; }

    /// <summary>Gets the stable logical identity of the catalog being produced.</summary>
    public string CatalogId { get; }

    /// <summary>Gets the exact application-owned revision being produced.</summary>
    public string CatalogRevision { get; }

    /// <summary>Gets the positive number of values requested.</summary>
    public int Count { get; }

    /// <summary>Gets the producer-local seed encoded as a portable JSON string.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Seed { get; }

    /// <summary>Gets the exact provider locale when locale selection is requested.</summary>
    public string? Locale { get; }

    /// <summary>Gets the fixed UTC provider reference time when one is requested.</summary>
    public DateTimeOffset? DateTimeReferenceUtc { get; }

    /// <summary>Gets the portable contract every returned value must satisfy.</summary>
    public TypeRef ValueType { get; }

    /// <summary>Gets a detached provider-owned declarative configuration object.</summary>
    public JsonElement Configuration { get; }

}

/// <summary>One strict correlated response containing exact provider-produced values.</summary>
public sealed class ExternalGenerationCatalogResponse
{
    /// <summary>Creates or restores one external catalog-provider response.</summary>
    /// <param name="schemaVersion">Exact external catalog-provider protocol version.</param>
    /// <param name="requestId">Identity copied exactly from the corresponding request.</param>
    /// <param name="provider">Stable external provider identity.</param>
    /// <param name="providerVersion">Exact external provider version used by the process.</param>
    /// <param name="values">Non-empty provider-produced values in requested sequence order.</param>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, request identity, provider identity, or version is invalid, or values are empty.
    /// </exception>
    [JsonConstructor]
    public ExternalGenerationCatalogResponse(
        string schemaVersion,
        string requestId,
        string provider,
        string providerVersion,
        ImmutableArray<ObservationValue> values)
    {
        SchemaVersion = ExternalGenerationCatalogProtocol.RequireSchema(schemaVersion);
        RequestId = ExternalGenerationCatalogProtocol.IsRequestId(requestId)
            ? requestId
            : throw new ArgumentException("An external catalog response requires a canonical request identity.", nameof(requestId));
        Provider = Guard.RequireNotNullOrWhiteSpace(provider);
        ProviderVersion = Guard.RequireNotNullOrWhiteSpace(providerVersion);
        Values = !values.IsDefaultOrEmpty
            ? values
            : throw new ArgumentException("An external catalog response requires at least one value.", nameof(values));
    }

    /// <summary>Gets the exact external catalog-provider protocol version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the identity copied from the corresponding request.</summary>
    public string RequestId { get; }

    /// <summary>Gets the stable external provider identity.</summary>
    public string Provider { get; }

    /// <summary>Gets the exact external provider version used by the process.</summary>
    public string ProviderVersion { get; }

    /// <summary>Gets provider-produced values in requested sequence order.</summary>
    public ImmutableArray<ObservationValue> Values { get; }
}

/// <summary>Strict deterministic JSON boundary shared by external generation-catalog producers and consumers.</summary>
public static class ExternalGenerationCatalogProtocol
{
    const string ContractName = "external generation-catalog provider message";
    const string RequestIdPrefix = "csimcatalogrequest1_";
    internal const string RequestReferenceScheme = "csimcatalogrequest";
    const int Sha256HexLength = 64;

    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Current language-neutral external catalog-provider protocol.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-generation-catalog-provider/v1";

    /// <summary>Creates a correlated request from validated import inputs.</summary>
    /// <param name="catalogId">Stable logical catalog identity.</param>
    /// <param name="catalogRevision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of values requested.</param>
    /// <param name="seed">Signed 64-bit producer-local seed.</param>
    /// <param name="valueType">Portable contract every response value must satisfy.</param>
    /// <param name="configuration">Provider-owned declarative configuration object.</param>
    /// <param name="locale">Optional exact provider locale.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time.</param>
    /// <returns>A request with a deterministic identity covering every supplied field.</returns>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An input is invalid.</exception>
    public static ExternalGenerationCatalogRequest CreateRequest(
        string catalogId,
        string catalogRevision,
        int count,
        long seed,
        TypeRef valueType,
        JsonElement configuration,
        string? locale = null,
        DateTimeOffset? dateTimeReferenceUtc = null) =>
        new(
            catalogId,
            catalogRevision,
            count,
            seed,
            locale,
            dateTimeReferenceUtc,
            valueType,
            configuration);

    /// <summary>Serializes one validated request as canonical compact UTF-8 JSON text.</summary>
    /// <param name="request">Request to serialize.</param>
    /// <returns>Canonical protocol request JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The request has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">The request violates its JSON contract.</exception>
    /// <exception cref="NotSupportedException">A request value has no configured serializer.</exception>
    public static string SerializeRequest(ExternalGenerationCatalogRequest request) => Serialize(request);

    /// <summary>Strictly deserializes one current-version canonical request.</summary>
    /// <param name="json">Request JSON.</param>
    /// <returns>A validated correlated request.</returns>
    /// <exception cref="JsonException">The JSON is malformed, open, noncanonical, or semantically invalid.</exception>
    public static ExternalGenerationCatalogRequest DeserializeRequest(string json) =>
        Deserialize<ExternalGenerationCatalogRequest>(json);

    /// <summary>Serializes one validated response as canonical compact UTF-8 JSON text.</summary>
    /// <param name="response">Response to serialize.</param>
    /// <returns>Canonical protocol response JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The response has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">The response violates its JSON contract.</exception>
    /// <exception cref="NotSupportedException">A response value has no configured serializer.</exception>
    public static string SerializeResponse(ExternalGenerationCatalogResponse response) => Serialize(response);

    /// <summary>Strictly deserializes one current-version canonical response.</summary>
    /// <param name="json">Response JSON.</param>
    /// <returns>A validated correlated response.</returns>
    /// <exception cref="JsonException">The JSON is malformed, open, noncanonical, or semantically invalid.</exception>
    public static ExternalGenerationCatalogResponse DeserializeResponse(string json) =>
        Deserialize<ExternalGenerationCatalogResponse>(json);

    internal static string ComputeRequestId(ExternalGenerationCatalogRequest request)
    {
        var identity = new RequestIdentityMaterial(
            request.SchemaVersion,
            request.CatalogId,
            request.CatalogRevision,
            request.Count,
            request.Seed,
            request.Locale,
            request.DateTimeReferenceUtc,
            request.ValueType,
            request.Configuration);
        var hash = SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(identity, Options));
        return RequestIdPrefix + Convert.ToHexStringLower(hash);
    }

    internal static string RequireSchema(string schemaVersion) =>
        string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            ? schemaVersion
            : throw new ArgumentException(
                $"External catalog-provider schema '{schemaVersion}' is unsupported; expected "
                + $"'{CurrentSchemaVersion}'.",
                nameof(schemaVersion));

    internal static string? NormalizeOptionalCoordinate(string? value, string parameterName)
    {
        if (value is null)
            return null;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An optional external catalog coordinate cannot be empty.", parameterName);
    }

    internal static DateTimeOffset? RequireUtcDateTimeReference(DateTimeOffset? value, string parameterName) =>
        value is not { } referenceTime || referenceTime.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException(
                "An external catalog date-time reference must use the UTC offset.",
                parameterName);

    internal static JsonElement NormalizeConfiguration(JsonElement configuration, string parameterName)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "External catalog provider configuration must be a JSON object.",
                parameterName);
        }
        if (StrictDocumentJson.TryFindDuplicateProperty(
                configuration,
                "/configuration",
                out var duplicateLocation))
        {
            throw new ArgumentException(
                $"External catalog provider configuration contains a duplicate property at '{duplicateLocation}'.",
                parameterName);
        }
        return configuration.Clone();
    }

    internal static bool IsRequestId(string? value)
    {
        if (value is null
            || value.Length != RequestIdPrefix.Length + Sha256HexLength
            || !value.StartsWith(RequestIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(RequestIdPrefix.Length))
        {
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character))
                return false;
        }
        return true;
    }

    static string Serialize<T>(T message)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(message, Options));
    }

    static T Deserialize<T>(string json)
        where T : class
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                ContractName,
                out T? message,
                out var error)
            && message is not null)
        {
            return message;
        }

        throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
    }

    sealed record RequestIdentityMaterial(
        string SchemaVersion,
        string CatalogId,
        string CatalogRevision,
        int Count,
        [property: JsonConverter(typeof(StringEncodedInt64JsonConverter))] long Seed,
        string? Locale,
        DateTimeOffset? DateTimeReferenceUtc,
        TypeRef ValueType,
        JsonElement Configuration);
}
