using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Durable exact evidence that one attempt-owned generation is sealed, successfully validated, and ready for its
/// retained target-pointer promotion intent to be reconciled.
/// </summary>
/// <remarks>
/// Readiness is deliberately not visibility. The reference retains the complete durable preparation prefix so a
/// later activation revalidates the exact convergence proof and replays the already-persisted promotion request;
/// it never derives a new compare-and-swap expectation from later target state.
/// </remarks>
public sealed record MaterializationReadyGenerationReference
{
    /// <summary>Current ready-generation-reference wire schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-ready-generation-reference/v1";

    /// <summary>Creates one exact ready-generation reference.</summary>
    /// <param name="schemaVersion">Exact supported wire schema.</param>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and placement-slice authority.</param>
    /// <param name="attempt">Exact Process attempt owning the candidate.</param>
    /// <param name="generation">Deterministic generation owned by <paramref name="authority"/> and <paramref name="attempt"/>.</param>
    /// <param name="preparation">
    /// Complete durable convergence, seal, successful validation, and retained promotion-intent prefix.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, attempt, generation, plan, placement, convergence, validation, or promotion intent is inexact.
    /// </exception>
    [JsonConstructor]
    public MaterializationReadyGenerationReference(
        string schemaVersion,
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationGenerationActivationState preparation)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Ready-generation-reference schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        Preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));

        var expectedGeneration = MaterializationRebuildIdentities.Generation(authority, attempt);
        var convergence = preparation.Convergence;
        var validation = preparation.ValidationReceipt;
        var promotion = preparation.PromotionRequest;
        if (generation != expectedGeneration
            || convergence.Generation != generation
            || convergence.RebuildPlan != authority.LeafPlan.Plan
            || convergence.Materialization != authority.PlacementSlice.Materialization.Materialization
            || convergence.DefinitionFingerprint != authority.PlacementSlice.Materialization.DefinitionFingerprint)
        {
            throw new ArgumentException(
                "Ready-generation convergence must identify the exact linked leaf, attempt, and deterministic generation.",
                nameof(preparation));
        }
        if (!convergence.IsValid
            || validation is not { Validation.IsValid: true }
            || promotion is null
            || preparation.PromotionReceipt is not null)
        {
            throw new ArgumentException(
                "Readiness requires valid convergence, successful target validation, a retained promotion intent, and no promotion receipt.",
                nameof(preparation));
        }
        if (validation.GenerationId != generation
            || promotion.GenerationId != generation
            || promotion.ExpectedGenerationRevision != validation.GenerationRevision
            || promotion.ValidationFingerprint != validation.Fingerprint
            || validation.ValidatedAtUtc < attempt.StartedAtUtc
            || promotion.PromotedAtUtc < validation.ValidatedAtUtc)
        {
            throw new ArgumentException(
                "Ready-generation validation and promotion intent must form one exact attempt-bound prefix.",
                nameof(preparation));
        }

        Generation = generation;
    }

    /// <summary>Exact ready-generation-reference wire schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact linked plan-set, leaf-plan, and placement-slice authority.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Exact Process attempt owning the candidate.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Deterministic candidate generation owned by <see cref="Attempt"/>.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Complete durable preparation prefix through successful target validation.</summary>
    public MaterializationGenerationActivationState Preparation { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint.</summary>
    [JsonIgnore]
    public MaterializationRebuildPlanFingerprint Plan => Authority.LeafPlan.Plan;

    /// <summary>Exact independently promoted placement authority.</summary>
    [JsonIgnore]
    public MaterializationPlacementSliceReference PlacementSlice => Authority.PlacementSlice;

    /// <summary>Fresh catalog-complete convergence evidence retained by the preparation.</summary>
    [JsonIgnore]
    public MaterializationConvergenceReceipt Convergence => Preparation.Convergence;

    /// <summary>Successful target-native validation receipt authorizing the retained intent.</summary>
    [JsonIgnore]
    public MaterializationValidationReceipt Validation => Preparation.ValidationReceipt!;

    /// <summary>Exact target-pointer compare-and-swap intent retained before activation.</summary>
    [JsonIgnore]
    public MaterializationPromoteGenerationRequest PromotionIntent => Preparation.PromotionRequest!;

    /// <summary>UTC successful-validation boundary at which this candidate became ready.</summary>
    [JsonIgnore]
    public DateTimeOffset ReadyAtUtc => Validation.ValidatedAtUtc;

    /// <summary>
    /// Determines whether active-generation evidence is the exact realization of this reference's retained
    /// promotion intent.
    /// </summary>
    /// <param name="activeGeneration">Active-generation evidence to compare with this ready reference.</param>
    /// <returns>
    /// <see langword="true"/> only when authority, generation, target revision, promotion identity and fence,
    /// validation fingerprint, and promotion time all match the retained intent.
    /// </returns>
    public bool MatchesActiveGeneration(MaterializationActiveGenerationReference? activeGeneration)
    {
        if (activeGeneration is null)
            return false;

        var intent = PromotionIntent;
        return activeGeneration.Authority == Authority
            && activeGeneration.Generation == Generation
            && intent.ExpectedTargetRevision.Ordinal < long.MaxValue
            && activeGeneration.TargetRevision.Ordinal == intent.ExpectedTargetRevision.Ordinal + 1
            && activeGeneration.Promotion == intent.PromotionId
            && activeGeneration.PromotionFence == intent.PromotionFence
            && activeGeneration.Validation == intent.ValidationFingerprint
            && activeGeneration.ActivatedAtUtc == intent.PromotedAtUtc;
    }
}

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationReadyGenerationReference"/>.</summary>
public static class MaterializationReadyGenerationReferenceJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one ready-generation reference as canonical compact JSON.</summary>
    /// <param name="reference">Exact ready-generation evidence.</param>
    /// <returns>Canonical JSON preserving every activation-authorizing input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationReadyGenerationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Deserializes and validates one exact ready-generation reference.</summary>
    /// <param name="json">Strict canonical ready-generation JSON.</param>
    /// <returns>The constructor-validated exact reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical, open, or violates readiness invariants.</exception>
    public static MaterializationReadyGenerationReference Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!TryDeserialize(json, out var reference, out var error) || reference is null)
            throw new JsonException(error.Message);
        return reference;
    }

    internal static bool TryDeserialize(
        string json,
        out MaterializationReadyGenerationReference? reference,
        out StrictDocumentJsonReadError error) => StrictDocumentJson.TryReadCanonicalObject(
            json,
            Options,
            "ready-generation reference",
            out reference,
            out error);
}
