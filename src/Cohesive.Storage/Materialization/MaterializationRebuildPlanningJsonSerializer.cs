using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON serialization for rebuild request, membership, placement, and plan-set IR.</summary>
public static class MaterializationRebuildPlanningJsonSerializer
{
    const string ReadStage = "materialization-rebuild-planning-document-read";

    /// <summary>Creates strict planning JSON options including canonical Relations converters.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        MaterializationJsonSerializer.CreateOptions(formatting);

    /// <summary>Serializes one exactly fingerprinted rebuild request.</summary>
    /// <param name="request">Canonical rebuild request.</param>
    /// <param name="formatting">Compact or human-readable formatting.</param>
    /// <returns>Deterministic request JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request fingerprint is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The request cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The request contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">The request has no canonical JSON representation.</exception>
    public static string SerializeRequest(
        MaterializationRebuildRequestDocument request,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        Serialize(request, formatting, ValidateRequest);

    /// <summary>Gets the canonical compact UTF-8 representation of one rebuild request.</summary>
    /// <param name="request">Canonical rebuild request.</param>
    /// <returns>Unique canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request fingerprint is stale.</exception>
    /// <exception cref="JsonException">The request cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The request contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">The request has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalRequestBytes(MaterializationRebuildRequestDocument request) =>
        CanonicalBytes(request, ValidateRequest);

    /// <summary>Deserializes and verifies one canonical rebuild request.</summary>
    /// <param name="json">Persisted canonical request JSON.</param>
    /// <returns>The exact normalized request.</returns>
    /// <exception cref="JsonException">The wire, schema, semantics, or fingerprint is invalid.</exception>
    public static MaterializationRebuildRequestDocument DeserializeRequest(string json) =>
        Deserialize<MaterializationRebuildRequestDocument>(json, "materialization rebuild request", ValidateRequest);

    /// <summary>Strictly reads one canonical rebuild request with structured diagnostics.</summary>
    /// <param name="json">Persisted canonical request JSON.</param>
    /// <param name="request">Verified request when reading succeeds.</param>
    /// <returns>Valid or one attributable wire/semantic diagnostic.</returns>
    public static DocumentValidationResult TryDeserializeRequest(
        string json,
        out MaterializationRebuildRequestDocument? request) =>
        TryDeserialize(json, "materialization rebuild request", MaterializationRebuildRequestDocument.CurrentSchemaVersion, ValidateRequest, out request);

    /// <summary>Serializes one complete frozen membership artifact.</summary>
    /// <param name="membership">Canonical membership evidence.</param>
    /// <param name="formatting">Compact or human-readable formatting.</param>
    /// <returns>Deterministic membership JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="membership"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The membership fingerprint is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">Membership cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Membership contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Membership has no canonical JSON representation.</exception>
    public static string SerializeMembership(
        MaterializationRebuildMembershipEvidence membership,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        Serialize(membership, formatting, ValidateMembership);

    /// <summary>Gets the canonical compact UTF-8 representation of frozen membership.</summary>
    /// <param name="membership">Canonical membership evidence.</param>
    /// <returns>Unique canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="membership"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The membership fingerprint is stale.</exception>
    /// <exception cref="JsonException">Membership cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Membership contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Membership has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalMembershipBytes(MaterializationRebuildMembershipEvidence membership) =>
        CanonicalBytes(membership, ValidateMembership);

    /// <summary>Deserializes and verifies one canonical frozen-membership artifact.</summary>
    /// <param name="json">Persisted canonical membership JSON.</param>
    /// <returns>The exact normalized membership evidence.</returns>
    /// <exception cref="JsonException">The wire, schema, semantics, or fingerprint is invalid.</exception>
    public static MaterializationRebuildMembershipEvidence DeserializeMembership(string json) =>
        Deserialize<MaterializationRebuildMembershipEvidence>(json, "materialization rebuild membership", ValidateMembership);

    /// <summary>Strictly reads frozen membership with structured diagnostics.</summary>
    /// <param name="json">Persisted canonical membership JSON.</param>
    /// <param name="membership">Verified membership when reading succeeds.</param>
    /// <returns>Valid or one attributable wire/semantic diagnostic.</returns>
    public static DocumentValidationResult TryDeserializeMembership(
        string json,
        out MaterializationRebuildMembershipEvidence? membership) =>
        TryDeserialize(json, "materialization rebuild membership", MaterializationRebuildMembershipEvidence.CurrentSchemaVersion, ValidateMembership, out membership);

    /// <summary>Serializes one canonical target-placement plan.</summary>
    /// <param name="placement">Canonical target placement.</param>
    /// <param name="formatting">Compact or human-readable formatting.</param>
    /// <returns>Deterministic placement JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The placement fingerprint is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">Placement cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Placement contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Placement has no canonical JSON representation.</exception>
    public static string SerializePlacement(
        MaterializationTargetPlacementPlan placement,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        Serialize(placement, formatting, ValidatePlacement);

    /// <summary>Gets the canonical compact UTF-8 representation of one target-placement plan.</summary>
    /// <param name="placement">Canonical target placement.</param>
    /// <returns>Unique canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The placement fingerprint is stale.</exception>
    /// <exception cref="JsonException">Placement cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Placement contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Placement has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalPlacementBytes(MaterializationTargetPlacementPlan placement) =>
        CanonicalBytes(placement, ValidatePlacement);

    /// <summary>Deserializes and verifies one canonical target-placement plan.</summary>
    /// <param name="json">Persisted canonical placement JSON.</param>
    /// <returns>The exact normalized target placement.</returns>
    /// <exception cref="JsonException">The wire, schema, semantics, or fingerprint is invalid.</exception>
    public static MaterializationTargetPlacementPlan DeserializePlacement(string json) =>
        Deserialize<MaterializationTargetPlacementPlan>(json, "materialization target placement", ValidatePlacement);

    /// <summary>Strictly reads one target-placement plan with structured diagnostics.</summary>
    /// <param name="json">Persisted canonical placement JSON.</param>
    /// <param name="placement">Verified placement when reading succeeds.</param>
    /// <returns>Valid or one attributable wire/semantic diagnostic.</returns>
    public static DocumentValidationResult TryDeserializePlacement(
        string json,
        out MaterializationTargetPlacementPlan? placement) =>
        TryDeserialize(json, "materialization target placement", MaterializationTargetPlacementPlan.CurrentSchemaVersion, ValidatePlacement, out placement);

    /// <summary>Serializes one fully linked rebuild plan set.</summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <param name="formatting">Compact or human-readable formatting.</param>
    /// <returns>Deterministic plan-set JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan-set fingerprint is stale.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The plan set cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The plan set contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">The plan set has no canonical JSON representation.</exception>
    public static string SerializePlanSet(
        MaterializationRebuildPlanSet planSet,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        Serialize(planSet, formatting, ValidatePlanSet);

    /// <summary>Gets the canonical compact UTF-8 representation of one linked rebuild plan set.</summary>
    /// <param name="planSet">Canonical linked plan set.</param>
    /// <returns>Unique canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan-set fingerprint is stale.</exception>
    /// <exception cref="JsonException">The plan set cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The plan set contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">The plan set has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalPlanSetBytes(MaterializationRebuildPlanSet planSet) =>
        CanonicalBytes(planSet, ValidatePlanSet);

    /// <summary>Deserializes and verifies one canonical linked rebuild plan set.</summary>
    /// <param name="json">Persisted canonical plan-set JSON.</param>
    /// <returns>The exact normalized linked plan set.</returns>
    /// <exception cref="JsonException">The wire, schema, semantics, or fingerprint is invalid.</exception>
    public static MaterializationRebuildPlanSet DeserializePlanSet(string json) =>
        Deserialize<MaterializationRebuildPlanSet>(json, "materialization rebuild plan set", ValidatePlanSet);

    /// <summary>Strictly reads one linked rebuild plan set with structured diagnostics.</summary>
    /// <param name="json">Persisted canonical plan-set JSON.</param>
    /// <param name="planSet">Verified plan set when reading succeeds.</param>
    /// <returns>Valid or one attributable wire/semantic diagnostic.</returns>
    public static DocumentValidationResult TryDeserializePlanSet(
        string json,
        out MaterializationRebuildPlanSet? planSet) =>
        TryDeserialize(json, "materialization rebuild plan set", MaterializationRebuildPlanSet.CurrentSchemaVersion, ValidatePlanSet, out planSet);

    static string Serialize<T>(
        T artifact,
        PortableDocumentJsonFormatting formatting,
        Action<T> validate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(artifact);
        validate(artifact);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(artifact, CreateOptions()))
            : JsonSerializer.Serialize(artifact, CreateOptions(formatting));
    }

    static byte[] CanonicalBytes<T>(T artifact, Action<T> validate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(artifact);
        validate(artifact);
        return StrictDocumentJson.GetCanonicalBytes(artifact, CreateOptions());
    }

    static T Deserialize<T>(string json, string subject, Action<T> validate)
        where T : class
    {
        var result = TryDeserialize(json, subject, "current", validate, out T? artifact);
        if (result.IsValid && artifact is not null)
            return artifact;
        throw new JsonException(result.Diagnostics.IsDefaultOrEmpty
            ? $"Failed to deserialize {subject}."
            : string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    static DocumentValidationResult TryDeserialize<T>(
        string json,
        string subject,
        string schema,
        Action<T> validate,
        out T? artifact)
        where T : class
    {
        artifact = null;
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                subject,
                out T? parsed,
                out var error)
            || parsed is null)
        {
            return MaterializationContract.ErrorResult(
                "materialization.rebuildPlanning.json.invalid",
                error.Message,
                error.Location,
                ReadStage,
                subject,
                [schema],
                "canonical closed-contract planning JSON",
                error.Failure.ToString());
        }

        try
        {
            validate(parsed);
            artifact = parsed;
            return DocumentValidationResult.Valid;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or JsonException
                                          or NotSupportedException
                                          or InvalidOperationException)
        {
            return MaterializationContract.ErrorResult(
                "materialization.rebuildPlanning.document.invalid",
                exception.Message,
                "$",
                ReadStage,
                subject,
                [schema],
                "current internally consistent planning artifact",
                exception.GetType().Name);
        }
    }

    static void ValidateRequest(MaterializationRebuildRequestDocument request)
    {
        if (MaterializationRebuildPlanningFingerprinters.ComputeRequest(request) != request.Fingerprint)
            throw new ArgumentException("The rebuild-request fingerprint is stale.", nameof(request));
    }

    static void ValidateMembership(MaterializationRebuildMembershipEvidence membership)
    {
        if (MaterializationRebuildPlanningFingerprinters.ComputeMembership(membership) != membership.Fingerprint)
            throw new ArgumentException("The rebuild-membership fingerprint is stale.", nameof(membership));
    }

    static void ValidatePlacement(MaterializationTargetPlacementPlan placement)
    {
        if (MaterializationRebuildPlanningFingerprinters.ComputePlacementPlan(placement) != placement.Fingerprint)
            throw new ArgumentException("The target-placement fingerprint is stale.", nameof(placement));
    }

    static void ValidatePlanSet(MaterializationRebuildPlanSet planSet)
    {
        if (MaterializationRebuildPlanningFingerprinters.ComputePlanSet(planSet) != planSet.Fingerprint)
            throw new ArgumentException("The rebuild plan-set fingerprint is stale.", nameof(planSet));
    }
}
