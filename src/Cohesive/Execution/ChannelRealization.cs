using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while realizing a Channel definition against a target profile.</summary>
public static class ChannelRealizationDiagnosticCodes
{
    /// <summary>The persisted realization plan uses a schema unsupported by this validator.</summary>
    public const string PlanSchemaUnsupported = "channels.realization.plan.schemaUnsupported";

    /// <summary>The capability profile uses a schema unsupported by this compiler.</summary>
    public const string ProfileSchemaUnsupported = "channels.realization.profile.schemaUnsupported";

    /// <summary>No evidence in the selected coherent variant preserves one exact requirement.</summary>
    public const string RequirementUnavailable = "channels.realization.requirement.unavailable";

    /// <summary>The supplied definition document is not a valid current canonical Channel authority.</summary>
    public const string DefinitionContextInvalid = "channels.realization.resolution.definitionInvalid";

    /// <summary>The plan does not cite the exact supplied Channel definition.</summary>
    public const string DefinitionReferenceMismatch = "channels.realization.resolution.definitionReferenceMismatch";

    /// <summary>The plan does not cite the exact supplied capability profile.</summary>
    public const string ProfileReferenceMismatch = "channels.realization.resolution.profileReferenceMismatch";

    /// <summary>The plan names no variant in the supplied exact profile.</summary>
    public const string VariantUnknown = "channels.realization.resolution.variantUnknown";

    /// <summary>The plan names a variant other than the deterministic compiler selection.</summary>
    public const string VariantSelectionMismatch = "channels.realization.resolution.variantSelectionMismatch";

    /// <summary>The plan does not contain exactly one decision for every definition requirement.</summary>
    public const string DecisionCoverageMismatch = "channels.realization.resolution.decisionCoverageMismatch";

    /// <summary>A plan decision does not match the selected variant's exact attributable evidence.</summary>
    public const string DecisionEvidenceMismatch = "channels.realization.resolution.decisionEvidenceMismatch";

    /// <summary>The plan's effective configuration attribution does not match its exact realization.</summary>
    public const string ConfigurationMismatch = "channels.realization.resolution.configurationMismatch";

    /// <summary>The plan was not produced with the exact trusted compiler provenance.</summary>
    public const string ProvenanceMismatch = "channels.realization.resolution.provenanceMismatch";

    /// <summary>The plan's retained realization diagnostics do not match deterministic recompilation.</summary>
    public const string ValidationMismatch = "channels.realization.resolution.validationMismatch";
}

/// <summary>Deterministic fingerprint of one complete Channel realization plan.</summary>
public sealed record ChannelRealizationPlanFingerprint
{
    /// <summary>Creates a Channel realization-plan fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelRealizationPlanFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>One exact evidence-backed or unavailable decision for a canonical Channel requirement.</summary>
public sealed record ChannelRealizationDecision
{
    /// <summary>Creates one Channel realization decision.</summary>
    /// <param name="requirement">Exact definition-local requirement identity.</param>
    /// <param name="realization">Native, composed, constrained, override, or unavailable classification.</param>
    /// <param name="evidence">Selected target evidence, or <see langword="null"/> when unavailable.</param>
    /// <param name="auxiliaries">Transitive auxiliary evidence used by the selected assertion.</param>
    /// <param name="operatingBoundaries">Attributable limits under which the realization holds.</param>
    /// <param name="sourceReferences">Evidence sources supporting the decision.</param>
    /// <exception cref="ArgumentException">An identity, collection, or availability invariant is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unknown.</exception>
    [JsonConstructor]
    public ChannelRealizationDecision(
        ChannelRequirementId requirement,
        CapabilityRealizationKind realization,
        ChannelCapabilityEvidenceId? evidence = null,
        ImmutableArray<ChannelCapabilityEvidenceId> auxiliaries = default,
        ImmutableArray<ChannelLimitRequirement> operatingBoundaries = default,
        ImmutableArray<string> sourceReferences = default)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A Channel realization decision requires a requirement identity.", nameof(requirement));
        if (!Enum.IsDefined(realization) || realization == CapabilityRealizationKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "A Channel decision requires an exact realization classification.");

        Requirement = requirement;
        Auxiliaries = ChannelCapabilityNormalization.IdentitySet(auxiliaries, nameof(auxiliaries));
        OperatingBoundaries = ChannelCapabilityNormalization.Boundaries(operatingBoundaries, nameof(operatingBoundaries));
        SourceReferences = ChannelCapabilityNormalization.StringSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: realization != CapabilityRealizationKind.Unavailable);

        var unavailable = realization == CapabilityRealizationKind.Unavailable;
        if (unavailable != (evidence is null))
            throw new ArgumentException("Unavailable decisions omit evidence and available decisions require it.", nameof(evidence));
        if (unavailable && (!Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty))
            throw new ArgumentException("Unavailable decisions cannot claim auxiliary or boundary evidence.", nameof(auxiliaries));
        if (evidence is { } selected && Auxiliaries.Contains(selected))
            throw new ArgumentException("A selected evidence identity cannot also be its own auxiliary.", nameof(auxiliaries));
        switch (realization)
        {
            case CapabilityRealizationKind.Native
                when !Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Native realization decisions cannot claim auxiliary or boundary evidence.",
                    nameof(realization));
            case CapabilityRealizationKind.Composed when Auxiliaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Composed realization decisions require auxiliary evidence.",
                    nameof(auxiliaries));
            case CapabilityRealizationKind.Constrained when OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Constrained realization decisions require an operating boundary.",
                    nameof(operatingBoundaries));
        }

        Realization = realization;
        Evidence = evidence;
    }

    /// <summary>Exact definition-local requirement identity.</summary>
    public ChannelRequirementId Requirement { get; }

    /// <summary>Final realization classification.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Selected target evidence, or <see langword="null"/> when unavailable.</summary>
    public ChannelCapabilityEvidenceId? Evidence { get; }

    /// <summary>Transitive auxiliary evidence in stable identity order.</summary>
    public ImmutableArray<ChannelCapabilityEvidenceId> Auxiliaries { get; }

    /// <summary>Operating boundaries in deterministic scope and dimension order.</summary>
    public ImmutableArray<ChannelLimitRequirement> OperatingBoundaries { get; }

    /// <summary>Evidence source references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Compares normalized realization decisions structurally.</summary>
    /// <param name="other">Other realization decision.</param>
    /// <returns><see langword="true"/> when every decision and evidence field is equal.</returns>
    public bool Equals(ChannelRealizationDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Requirement == other.Requirement
        && Realization == other.Realization
        && Evidence == other.Evidence
        && Auxiliaries.SequenceEqual(other.Auxiliaries)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && SourceReferences.SequenceEqual(other.SourceReferences, StringComparer.Ordinal);

    /// <summary>Returns a structural hash code for the normalized realization decision.</summary>
    /// <returns>A hash code derived from every decision and evidence field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Requirement);
        hash.Add(Realization);
        hash.Add(Evidence);
        foreach (var auxiliary in Auxiliaries)
            hash.Add(auxiliary);
        foreach (var boundary in OperatingBoundaries)
            hash.Add(boundary);
        foreach (var sourceReference in SourceReferences)
            hash.Add(sourceReference, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Deterministic result of compiling one exact Channel definition against one exact target profile.</summary>
public sealed record ChannelRealizationPlan
{
    /// <summary>Current Channel realization-plan schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-channel-realization-plan/v1";

    /// <summary>Creates a current-version fingerprinted Channel realization plan.</summary>
    /// <param name="definition">Exact canonical Channel definition.</param>
    /// <param name="profile">Exact target capability profile.</param>
    /// <param name="variant">One coherent selected target variant.</param>
    /// <param name="decisions">One decision for every exact Channel requirement.</param>
    /// <param name="configuration">Effective configuration attribution.</param>
    /// <param name="provenance">Compiler and source attribution.</param>
    /// <param name="validation">Structured unavailable diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, decision, configuration, diagnostic, or fingerprint invariant is invalid.</exception>
    public ChannelRealizationPlan(
        ExecutionDefinitionReference definition,
        ChannelCapabilityProfileReference profile,
        ChannelCapabilityVariantId variant,
        ImmutableArray<ChannelRealizationDecision> decisions,
        ImmutableArray<EffectiveConfigurationDecision> configuration,
        ExecutionProvenance provenance,
        DocumentValidationResult validation)
        : this(
            CurrentSchemaVersion,
            definition,
            profile,
            variant,
            decisions,
            configuration,
            provenance,
            validation,
            fingerprint: null)
    {
    }

    /// <summary>Creates or deserializes an exactly fingerprinted Channel realization plan.</summary>
    /// <param name="schemaVersion">Exact plan schema version.</param>
    /// <param name="definition">Exact canonical Channel definition.</param>
    /// <param name="profile">Exact target capability profile.</param>
    /// <param name="variant">One coherent selected target variant.</param>
    /// <param name="decisions">One decision for every exact Channel requirement.</param>
    /// <param name="configuration">Effective configuration attribution.</param>
    /// <param name="provenance">Compiler and source attribution.</param>
    /// <param name="validation">Structured unavailable diagnostics.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, decision, configuration, diagnostic, or supplied fingerprint is invalid.</exception>
    [JsonConstructor]
    public ChannelRealizationPlan(
        string schemaVersion,
        ExecutionDefinitionReference definition,
        ChannelCapabilityProfileReference profile,
        ChannelCapabilityVariantId variant,
        ImmutableArray<ChannelRealizationDecision> decisions,
        ImmutableArray<EffectiveConfigurationDecision> configuration,
        ExecutionProvenance provenance,
        DocumentValidationResult validation,
        ChannelRealizationPlanFingerprint? fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Channel realization-plan schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A Channel realization plan requires one coherent target variant.", nameof(variant));
        Variant = variant;
        Decisions = NormalizeDecisions(decisions);
        Configuration = ChannelCapabilityNormalization.Configuration(configuration, nameof(configuration));
        Provenance = Guard.RequireNotNull(provenance);
        Validation = NormalizeValidation(validation);

        var computed = ChannelRealizationPlanFingerprinter.Compute(
            SchemaVersion,
            Definition,
            Profile,
            Variant,
            Decisions,
            Configuration,
            Provenance,
            Validation);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied Channel realization-plan fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact plan schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact canonical Channel definition reference.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Exact target capability-profile reference.</summary>
    public ChannelCapabilityProfileReference Profile { get; }

    /// <summary>Single coherent target variant selected for the complete definition.</summary>
    public ChannelCapabilityVariantId Variant { get; }

    /// <summary>One decision per exact requirement in stable requirement-identity order.</summary>
    public ImmutableArray<ChannelRealizationDecision> Decisions { get; }

    /// <summary>Effective configuration attribution in stable setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Compiler and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Structured deterministic capability diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Deterministic fingerprint of every execution-affecting plan field.</summary>
    public ChannelRealizationPlanFingerprint Fingerprint { get; }

    /// <summary>Whether the persisted plan claims every decision is available and retains no error diagnostic.</summary>
    /// <remarks>
    /// This is structural content, not authority. Resolve the plan against its exact definition, capability profile,
    /// and trusted compiler provenance before acting on the claim.
    /// </remarks>
    [JsonIgnore]
    public bool ClaimsRealizable => Validation.IsValid
        && Decisions.All(static decision => decision.Realization != CapabilityRealizationKind.Unavailable);

    /// <summary>Compares normalized realization plans structurally.</summary>
    /// <param name="other">Other realization plan.</param>
    /// <returns><see langword="true"/> when every persisted plan field is equal.</returns>
    public bool Equals(ChannelRealizationPlan? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Definition == other.Definition
        && Profile == other.Profile
        && Variant == other.Variant
        && Decisions.SequenceEqual(other.Decisions)
        && Configuration.SequenceEqual(other.Configuration)
        && Provenance == other.Provenance
        && Validation == other.Validation
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for the normalized persisted plan.</summary>
    /// <returns>A hash code derived from every persisted plan field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Definition);
        hash.Add(Profile);
        hash.Add(Variant);
        foreach (var decision in Decisions)
            hash.Add(decision);
        foreach (var configuration in Configuration)
            hash.Add(configuration);
        hash.Add(Provenance);
        hash.Add(Validation);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    internal static ImmutableArray<ChannelRealizationDecision> NormalizeDecisions(
        ImmutableArray<ChannelRealizationDecision> decisions)
    {
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A Channel realization plan requires at least one requirement decision.", nameof(decisions));
        if (normalized.Any(static decision => decision is null))
            throw new ArgumentException("Channel realization decisions cannot contain null entries.", nameof(decisions));
        if (normalized.GroupBy(static decision => decision.Requirement).Any(static group => group.Count() > 1))
            throw new ArgumentException("Channel realization decisions cannot repeat a requirement.", nameof(decisions));
        return CanonicalDocumentCollections.SortIfNeeded(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.Requirement.Value,
                right.Requirement.Value));
    }

    internal static DocumentValidationResult NormalizeValidation(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.Diagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Channel realization diagnostics cannot contain null entries.", nameof(validation));
        var diagnostics = validation.Diagnostics.IsDefaultOrEmpty
            ? []
            : CanonicalDocumentCollections.SortIfNeeded(
                validation.Diagnostics,
                DocumentValidationDiagnosticComparer.Ordinal.Compare);
        return diagnostics.IsDefaultOrEmpty ? DocumentValidationResult.Valid : new(diagnostics);
    }
}

/// <summary>Deterministic fingerprinting for Channel realization plans.</summary>
public static class ChannelRealizationPlanFingerprinter
{
    /// <summary>Digest algorithm used by Channel realization-plan fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by Channel realization-plan fingerprints.</summary>
    public const string Canonicalization = "cohesive-channel-realization-plan/v1-c14n/v1";

    /// <summary>Computes a deterministic fingerprint of every execution-affecting realization field.</summary>
    /// <param name="schemaVersion">Exact plan schema version.</param>
    /// <param name="definition">Exact Channel definition reference.</param>
    /// <param name="profile">Exact capability-profile reference.</param>
    /// <param name="variant">Selected coherent variant.</param>
    /// <param name="decisions">Normalized requirement decisions.</param>
    /// <param name="configuration">Normalized effective configuration attribution.</param>
    /// <param name="provenance">Compiler and source attribution.</param>
    /// <param name="validation">Deterministic structured diagnostics.</param>
    /// <returns>A deterministic SHA-256 plan fingerprint.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, decision, configuration, or diagnostic is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Plan content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Plan content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Plan content has no canonical JSON representation.</exception>
    public static ChannelRealizationPlanFingerprint Compute(
        string schemaVersion,
        ExecutionDefinitionReference definition,
        ChannelCapabilityProfileReference profile,
        ChannelCapabilityVariantId variant,
        ImmutableArray<ChannelRealizationDecision> decisions,
        ImmutableArray<EffectiveConfigurationDecision> configuration,
        ExecutionProvenance provenance,
        DocumentValidationResult validation)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A realization-plan fingerprint requires a coherent variant.", nameof(variant));
        decisions = ChannelRealizationPlan.NormalizeDecisions(decisions);
        configuration = ChannelCapabilityNormalization.Configuration(configuration, nameof(configuration));
        ArgumentNullException.ThrowIfNull(provenance);
        validation = ChannelRealizationPlan.NormalizeValidation(validation);

        var bytes = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                definition,
                profile,
                variant,
                decisions,
                configuration,
                provenance,
                validation),
            StrictDocumentJson.CreateOptions());
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        ExecutionDefinitionReference Definition,
        ChannelCapabilityProfileReference Profile,
        ChannelCapabilityVariantId Variant,
        ImmutableArray<ChannelRealizationDecision> Decisions,
        ImmutableArray<EffectiveConfigurationDecision> Configuration,
        ExecutionProvenance Provenance,
        DocumentValidationResult Validation);
}

/// <summary>An exact Channel realization plan resolved against trusted definition, profile, and producer context.</summary>
/// <remarks>
/// Only <see cref="ChannelRealizationPlanValidator.TryResolve"/> and the context-bound JSON deserializer create this
/// authority token. A structurally valid <see cref="ChannelRealizationPlan"/> is not interchangeable with a resolved
/// plan because its references, evidence, configuration, diagnostics, and provenance may have been forged together
/// with a new self-consistent fingerprint.
/// </remarks>
public sealed class ResolvedChannelRealizationPlan
{
    internal ResolvedChannelRealizationPlan(ChannelRealizationPlan plan) => Plan = plan;

    /// <summary>The exact context-validated persisted realization plan.</summary>
    public ChannelRealizationPlan Plan { get; }

    /// <summary>Whether the context-validated plan realizes every exact definition requirement.</summary>
    public bool IsRealizable => Plan.ClaimsRealizable;
}

/// <summary>Authoritative validation of persisted Channel realization plans against exact trusted context.</summary>
public static class ChannelRealizationPlanValidator
{
    const string ValidationStage = "channel-realization-resolution";

    /// <summary>Validates a plan against its exact definition, capability profile, and trusted compiler provenance.</summary>
    /// <param name="plan">Persisted or manually constructed plan to validate.</param>
    /// <param name="definitionDocument">Exact canonical Channel definition authority.</param>
    /// <param name="profile">Exact target capability profile authority.</param>
    /// <param name="expectedProvenance">Exact trusted producer and source attribution expected on the plan.</param>
    /// <returns>Deterministically ordered authoritative-resolution diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The definition cannot be projected through its strict typed contract.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    public static DocumentValidationResult Validate(
        ChannelRealizationPlan plan,
        ExecutionDefinitionDocument definitionDocument,
        ChannelCapabilityProfile profile,
        ExecutionProvenance expectedProvenance) =>
        ValidateCore(plan, definitionDocument, profile, expectedProvenance);

    /// <summary>Attempts to issue a resolved authority token for one exact persisted realization plan.</summary>
    /// <param name="plan">Persisted or manually constructed plan to resolve.</param>
    /// <param name="definitionDocument">Exact canonical Channel definition authority.</param>
    /// <param name="profile">Exact target capability profile authority.</param>
    /// <param name="expectedProvenance">Exact trusted producer and source attribution expected on the plan.</param>
    /// <param name="resolved">Resolved authority token when validation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns>Deterministically ordered authoritative-resolution diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Any required input is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The definition cannot be projected through its strict typed contract.</exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    public static DocumentValidationResult TryResolve(
        ChannelRealizationPlan plan,
        ExecutionDefinitionDocument definitionDocument,
        ChannelCapabilityProfile profile,
        ExecutionProvenance expectedProvenance,
        out ResolvedChannelRealizationPlan? resolved)
    {
        var validation = ValidateCore(plan, definitionDocument, profile, expectedProvenance);
        resolved = validation.IsValid ? new(plan) : null;
        return validation;
    }

    static DocumentValidationResult ValidateCore(
        ChannelRealizationPlan plan,
        ExecutionDefinitionDocument definitionDocument,
        ChannelCapabilityProfile profile,
        ExecutionProvenance expectedProvenance)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(definitionDocument);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(expectedProvenance);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (!string.Equals(plan.SchemaVersion, ChannelRealizationPlan.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.PlanSchemaUnsupported,
                $"Channel realization-plan schema '{plan.SchemaVersion}' is unsupported.",
                "/schemaVersion",
                subject: plan.Fingerprint.Value,
                expected: ChannelRealizationPlan.CurrentSchemaVersion,
                observed: plan.SchemaVersion));
        }

        if (!string.Equals(profile.SchemaVersion, ChannelCapabilityProfile.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.ProfileSchemaUnsupported,
                $"Channel capability-profile schema '{profile.SchemaVersion}' is unsupported for authoritative plan resolution.",
                "/profile/schemaVersion",
                subject: profile.Id.Value,
                expected: ChannelCapabilityProfile.CurrentSchemaVersion,
                observed: profile.SchemaVersion,
                sourceReferences: [profile.Provenance.Source.Reference]));
        }

        var definitionValidation = ChannelDefinitionDocuments.Validate(definitionDocument);
        ChannelDefinition? definition = null;
        if (!definitionValidation.IsValid)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.DefinitionContextInvalid,
                "The supplied execution document is not a valid current canonical Channel definition.",
                "/definition",
                subject: definitionDocument.Metadata.DefinitionId.Value,
                expected: "valid current canonical Channel definition",
                observed: string.Join(",", definitionValidation.Diagnostics.Select(static item => item.Code)),
                sourceReferences: [definitionDocument.Metadata.Provenance.Source.Reference]));
        }
        else
        {
            definition = definitionDocument.GetDefinition<ChannelDefinition>();
        }

        var expectedDefinitionReference = new ExecutionDefinitionReference(
            definitionDocument.Metadata.DefinitionId,
            definitionDocument.Metadata.RevisionId,
            definitionDocument.Metadata.Fingerprint);
        if (plan.Definition != expectedDefinitionReference)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.DefinitionReferenceMismatch,
                "The realization plan does not cite the exact supplied Channel definition revision and fingerprint.",
                "/definition",
                subject: plan.Definition.DefinitionId.Value,
                expected: DefinitionReference(expectedDefinitionReference),
                observed: DefinitionReference(plan.Definition),
                sourceReferences: [definitionDocument.Metadata.Provenance.Source.Reference]));
        }

        var expectedProfileReference = profile.ToReference();
        if (plan.Profile != expectedProfileReference)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.ProfileReferenceMismatch,
                "The realization plan does not cite the exact supplied capability profile and fingerprint.",
                "/profile",
                subject: plan.Profile.Id.Value,
                expected: ProfileReference(expectedProfileReference),
                observed: ProfileReference(plan.Profile),
                sourceReferences: [profile.Provenance.Source.Reference]));
        }

        if (plan.Provenance != expectedProvenance)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.ProvenanceMismatch,
                "The realization plan does not carry the exact trusted compiler provenance.",
                "/provenance",
                subject: plan.Fingerprint.Value,
                expected: Provenance(expectedProvenance),
                observed: Provenance(plan.Provenance),
                sourceReferences: [expectedProvenance.Source.Reference]));
        }

        var selectedVariant = profile.Variants.FirstOrDefault(variant => variant.Id == plan.Variant);
        if (selectedVariant is null)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.VariantUnknown,
                $"Capability variant '{plan.Variant.Value}' does not exist in the supplied exact profile.",
                "/variant",
                subject: plan.Variant.Value,
                expected: string.Join(",", profile.Variants.Select(static variant => variant.Id.Value)),
                observed: plan.Variant.Value,
                sourceReferences: [profile.Provenance.Source.Reference]));
        }
        else
        {
            var evidenceIds = selectedVariant.Evidence.Select(static item => item.Id).ToHashSet();
            for (var index = 0; index < plan.Decisions.Length; index++)
            {
                var decision = plan.Decisions[index];
                if (decision.Evidence is { } evidence && !evidenceIds.Contains(evidence))
                {
                    diagnostics.Add(Error(
                        ChannelRealizationDiagnosticCodes.DecisionEvidenceMismatch,
                        $"Decision '{decision.Requirement.Value}' cites evidence outside selected variant '{plan.Variant.Value}'.",
                        $"/decisions/{index}/evidence",
                        subject: decision.Requirement.Value,
                        expected: "evidence owned by the selected coherent variant",
                        observed: evidence.Value,
                        sourceReferences: [profile.Provenance.Source.Reference]));
                }
            }
        }

        if (definition is not null)
            ValidateDecisionCoverage(plan, definition, diagnostics);

        var contextsCurrent = definition is not null
            && string.Equals(plan.SchemaVersion, ChannelRealizationPlan.CurrentSchemaVersion, StringComparison.Ordinal)
            && string.Equals(profile.SchemaVersion, ChannelCapabilityProfile.CurrentSchemaVersion, StringComparison.Ordinal);
        if (contextsCurrent)
        {
            var expectedPlan = ChannelRealizationCompiler.Compile(
                definitionDocument,
                profile,
                expectedProvenance);
            ValidateAgainstExpected(plan, expectedPlan, profile, diagnostics);
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateDecisionCoverage(
        ChannelRealizationPlan plan,
        ChannelDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var required = definition.Requirements.Select(static requirement => requirement.Id).ToHashSet();
        var observed = plan.Decisions.Select(static decision => decision.Requirement).ToHashSet();
        var missing = required.Except(observed).OrderBy(static id => id.Value, StringComparer.Ordinal).ToArray();
        var unexpected = observed.Except(required).OrderBy(static id => id.Value, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && unexpected.Length == 0 && plan.Decisions.Length == definition.Requirements.Length)
            return;

        diagnostics.Add(Error(
            ChannelRealizationDiagnosticCodes.DecisionCoverageMismatch,
            "A realization plan requires exactly one decision for every exact Channel requirement and no others.",
            "/decisions",
            subject: plan.Definition.DefinitionId.Value,
            expected: string.Join(",", required.OrderBy(static id => id.Value, StringComparer.Ordinal).Select(static id => id.Value)),
            observed: $"missing=[{string.Join(',', missing.Select(static id => id.Value))}];unexpected=[{string.Join(',', unexpected.Select(static id => id.Value))}]"));
    }

    static void ValidateAgainstExpected(
        ChannelRealizationPlan plan,
        ChannelRealizationPlan expected,
        ChannelCapabilityProfile profile,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (plan.Variant != expected.Variant)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.VariantSelectionMismatch,
                "The realization plan does not retain the deterministic coherent variant selected by recompilation.",
                "/variant",
                subject: plan.Variant.Value,
                expected: expected.Variant.Value,
                observed: plan.Variant.Value,
                sourceReferences: [profile.Provenance.Source.Reference]));
        }

        var expectedByRequirement = expected.Decisions.ToDictionary(static decision => decision.Requirement);
        for (var index = 0; index < plan.Decisions.Length; index++)
        {
            var decision = plan.Decisions[index];
            if (!expectedByRequirement.TryGetValue(decision.Requirement, out var expectedDecision)
                || decision == expectedDecision)
            {
                continue;
            }

            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.DecisionEvidenceMismatch,
                $"Decision '{decision.Requirement.Value}' does not match deterministic evidence resolution.",
                $"/decisions/{index}",
                subject: decision.Requirement.Value,
                expected: Decision(expectedDecision),
                observed: Decision(decision),
                sourceReferences: [profile.Provenance.Source.Reference]));
        }

        if (!plan.Configuration.SequenceEqual(expected.Configuration))
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.ConfigurationMismatch,
                "The realization plan's effective configuration attribution differs from deterministic recompilation.",
                "/configuration",
                subject: plan.Fingerprint.Value,
                expected: Configuration(expected.Configuration),
                observed: Configuration(plan.Configuration),
                sourceReferences: [profile.Provenance.Source.Reference]));
        }

        if (plan.Validation != expected.Validation)
        {
            diagnostics.Add(Error(
                ChannelRealizationDiagnosticCodes.ValidationMismatch,
                "The realization plan's retained diagnostics differ from deterministic recompilation.",
                "/validation",
                subject: plan.Fingerprint.Value,
                expected: DiagnosticCodes(expected.Validation),
                observed: DiagnosticCodes(plan.Validation),
                sourceReferences: [profile.Provenance.Source.Reference]));
        }
    }

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string subject,
        string expected,
        string observed,
        ImmutableArray<string> sourceReferences = default) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: ValidationStage,
                subject: subject,
                sourceReferences: sourceReferences,
                resolutionOptions: ["Recompile the plan from the exact trusted definition, profile, and compiler context."],
                expected: expected,
                observed: observed));

    static string DefinitionReference(ExecutionDefinitionReference reference) =>
        $"{reference.DefinitionId.Value}@{reference.RevisionId.Value}#{reference.Fingerprint.Value}";

    static string ProfileReference(ChannelCapabilityProfileReference reference) =>
        $"{reference.SchemaVersion}:{reference.Id.Value}#{reference.Fingerprint.Value}";

    static string Provenance(ExecutionProvenance provenance) =>
        $"{provenance.Producer.Producer}@{provenance.Producer.Version ?? "?"}:{provenance.Source.Reference}:{provenance.Origin}";

    static string Decision(ChannelRealizationDecision decision) =>
        $"{decision.Realization}:{decision.Evidence?.Value ?? "-"}:aux=[{string.Join(',', decision.Auxiliaries.Select(static id => id.Value))}]:boundaries=[{string.Join(',', decision.OperatingBoundaries.Select(static boundary => $"{ChannelRequirementCompatibility.Slot(boundary)}={boundary.Value}"))}]:sources=[{string.Join(',', decision.SourceReferences)}]";

    static string Configuration(ImmutableArray<EffectiveConfigurationDecision> configuration) =>
        configuration.IsDefaultOrEmpty
            ? "none"
            : string.Join(",", configuration.Select(static item => $"{item.Setting}:{item.Origin}:{item.Authority}"));

    static string DiagnosticCodes(DocumentValidationResult validation) =>
        validation.Diagnostics.IsDefaultOrEmpty
            ? "none"
            : string.Join(",", validation.Diagnostics.Select(static item => item.Code));
}

/// <summary>Deterministic capability compiler for canonical Channel definitions.</summary>
public static class ChannelRealizationCompiler
{
    const string VariantSelectionSetting = "channel.realization.variant";

    /// <summary>Compiles one exact canonical Channel definition against one complete target profile.</summary>
    /// <remarks>
    /// Every profile variant is evaluated independently. The compiler selects the first fully realizable variant in
    /// canonical identity order, or the variant satisfying the greatest number of requirements when none is complete.
    /// Evidence is never combined across variants.
    /// </remarks>
    /// <param name="document">Validated shared execution-definition document containing canonical Channel IR.</param>
    /// <param name="profile">Exact versioned target capability profile.</param>
    /// <param name="provenance">Compiler and source attribution for the derived plan.</param>
    /// <returns>An evidence-backed deterministic plan with one decision for every exact requirement.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="document"/> is not a valid canonical Channel document.</exception>
    public static ChannelRealizationPlan Compile(
        ExecutionDefinitionDocument document,
        ChannelCapabilityProfile profile,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provenance);

        var definitionValidation = ChannelDefinitionDocuments.Validate(document);
        if (!definitionValidation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", definitionValidation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        var definition = document.GetDefinition<ChannelDefinition>();
        var evaluations = profile.Variants
            .Select(variant => Evaluate(definition, profile, variant))
            .ToArray();
        var selected = evaluations
            .OrderByDescending(static evaluation => evaluation.Validation.IsValid)
            .ThenByDescending(static evaluation => evaluation.AvailableCount)
            .ThenBy(static evaluation => evaluation.Variant.Id.Value, StringComparer.Ordinal)
            .First();

        var diagnostics = selected.Validation.Diagnostics.ToBuilder();
        if (!string.Equals(profile.SchemaVersion, ChannelCapabilityProfile.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                ChannelRealizationDiagnosticCodes.ProfileSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Channel capability-profile schema '{profile.SchemaVersion}' is not supported by this compiler.",
                "/profile/schemaVersion",
                Evidence: new(
                    stage: "channel-capability-matching",
                    subject: profile.Id.Value,
                    sourceReferences: [profile.Id.Value, profile.Provenance.Source.Reference],
                    resolutionOptions: ["Select a supported exact Channel capability-profile schema."],
                    expected: ChannelCapabilityProfile.CurrentSchemaVersion,
                    observed: profile.SchemaVersion)));
        }
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);

        var configuration = SelectedConfiguration(profile, selected);
        return new(
            definition: new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint),
            profile: profile.ToReference(),
            variant: selected.Variant.Id,
            decisions: selected.Decisions,
            configuration: configuration,
            provenance: provenance,
            validation: DocumentValidationResult.FromDiagnostics(diagnostics));
    }

    static VariantEvaluation Evaluate(
        ChannelDefinition definition,
        ChannelCapabilityProfile profile,
        ChannelCapabilityVariant variant)
    {
        var byId = variant.Evidence.ToDictionary(static evidence => evidence.Id);
        var demandedLimits = definition.Requirements.OfType<ChannelLimitRequirement>().ToImmutableArray();
        var decisions = ImmutableArray.CreateBuilder<ChannelRealizationDecision>(definition.Requirements.Length);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var available = 0;
        for (var index = 0; index < definition.Requirements.Length; index++)
        {
            var requirement = definition.Requirements[index];
            var evidence = variant.Evidence.FirstOrDefault(candidate =>
                ChannelRequirementCompatibility.Satisfies(requirement, candidate.Capability)
                && OperatingBoundariesSatisfy(demandedLimits, candidate, byId));
            if (evidence is null)
            {
                decisions.Add(new(requirement.Id, CapabilityRealizationKind.Unavailable));
                var related = variant.Evidence
                    .Where(candidate => candidate.Capability.GetType() == requirement.GetType()
                        && candidate.Capability.Scope == requirement.Scope)
                    .Select(candidate => $"/profile/variants/{variant.Id.Value}/evidence/{candidate.Id.Value}")
                    .ToImmutableArray();
                diagnostics.Add(new(
                    ChannelRealizationDiagnosticCodes.RequirementUnavailable,
                    DiagnosticSeverity.Error,
                    $"Target variant '{variant.Id.Value}' cannot preserve Channel requirement '{requirement.Id.Value}' without weakening it.",
                    $"/definition/requirements/{index}",
                    Evidence: new(
                        stage: "channel-capability-matching",
                        subject: requirement.Id.Value,
                        relatedLocations: related,
                        sourceReferences: [profile.Id.Value, profile.Provenance.Source.Reference],
                        resolutionOptions:
                        [
                            "Select a coherent target variant that preserves the exact requirement.",
                            "Declare an attributable composition, sufficient operating boundary, or explicit override."
                        ],
                        expected: ChannelRequirementCompatibility.Slot(requirement),
                        observed: related.IsDefaultOrEmpty
                            ? "not advertised"
                            : "incompatible capability or operating boundary")));
                continue;
            }

            available++;
            var closure = EvidenceClosure(evidence, byId);
            var auxiliaries = closure
                .Where(item => item.Id != evidence.Id)
                .Select(static item => item.Id)
                .ToImmutableArray();
            var boundaries = MostRestrictiveBoundaries(
                closure.SelectMany(static item => item.OperatingBoundaries));
            var sources = closure.SelectMany(static item => item.SourceReferences).Distinct(StringComparer.Ordinal).ToImmutableArray();
            decisions.Add(new(
                requirement.Id,
                evidence.Realization,
                evidence.Id,
                auxiliaries,
                boundaries,
                sources));
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return new(
            variant,
            decisions.MoveToImmutable(),
            available,
            DocumentValidationResult.FromDiagnostics(diagnostics));
    }

    static bool OperatingBoundariesSatisfy(
        ImmutableArray<ChannelLimitRequirement> demandedLimits,
        ChannelCapabilityEvidence root,
        IReadOnlyDictionary<ChannelCapabilityEvidenceId, ChannelCapabilityEvidence> byId)
    {
        var boundaries = EvidenceClosure(root, byId)
            .SelectMany(static item => item.OperatingBoundaries);
        foreach (var boundary in boundaries)
        {
            var slot = ChannelRequirementCompatibility.Slot(boundary);
            var demand = demandedLimits.FirstOrDefault(candidate =>
                string.Equals(ChannelRequirementCompatibility.Slot(candidate), slot, StringComparison.Ordinal));
            if (demand is null || !ChannelRequirementCompatibility.Satisfies(demand, boundary))
                return false;
        }

        return true;
    }

    static ImmutableArray<ChannelLimitRequirement> MostRestrictiveBoundaries(
        IEnumerable<ChannelLimitRequirement> boundaries) =>
    [
        .. boundaries
            .GroupBy(ChannelRequirementCompatibility.Slot, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group
                .OrderBy(static boundary => boundary.Value)
                .ThenBy(static boundary => boundary.Id.Value, StringComparer.Ordinal)
                .First())
    ];

    static ImmutableArray<ChannelCapabilityEvidence> EvidenceClosure(
        ChannelCapabilityEvidence root,
        IReadOnlyDictionary<ChannelCapabilityEvidenceId, ChannelCapabilityEvidence> byId)
    {
        Dictionary<ChannelCapabilityEvidenceId, ChannelCapabilityEvidence> closure = [];
        Visit(root);
        return [.. closure.Values.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];

        void Visit(ChannelCapabilityEvidence evidence)
        {
            if (!closure.TryAdd(evidence.Id, evidence))
                return;
            foreach (var auxiliary in evidence.Auxiliaries)
                Visit(byId[auxiliary]);
        }
    }

    static ImmutableArray<EffectiveConfigurationDecision> SelectedConfiguration(
        ChannelCapabilityProfile profile,
        VariantEvaluation selected)
    {
        var selectedEvidence = selected.Decisions
            .Where(static decision => decision.Evidence is not null)
            .SelectMany(decision => decision.Auxiliaries.Append(decision.Evidence!.Value))
            .Distinct()
            .ToHashSet();
        var decisions = selected.Variant.Evidence
            .Where(evidence => selectedEvidence.Contains(evidence.Id))
            .SelectMany(static evidence => evidence.Configuration)
            .Distinct()
            .ToList();
        if (!decisions.Any(static decision => decision.Setting == VariantSelectionSetting))
        {
            decisions.Add(new(
                setting: VariantSelectionSetting,
                origin: EffectiveConfigurationOrigin.AdapterConvention,
                authority: $"{profile.Id.Value}/{selected.Variant.Id.Value}"));
        }
        return ChannelCapabilityNormalization.Configuration(
            [.. decisions],
            "configuration");
    }

    sealed record VariantEvaluation(
        ChannelCapabilityVariant Variant,
        ImmutableArray<ChannelRealizationDecision> Decisions,
        int AvailableCount,
        DocumentValidationResult Validation);
}

/// <summary>Strict canonical JSON serialization for Channel capability profiles and realization plans.</summary>
public static class ChannelRealizationJsonSerializer
{
    /// <summary>Creates strict serializer options for Channel capability and realization artifacts.</summary>
    /// <param name="formatting">Compact or human-readable formatting.</param>
    /// <returns>Strict, case-sensitive portable JSON options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes a capability profile as deterministic canonical JSON.</summary>
    /// <param name="profile">Profile to serialize.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The profile violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The profile contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The profile has no canonical JSON representation.</exception>
    public static string SerializeProfile(ChannelCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(profile, CreateOptions()));
    }

    /// <summary>Deserializes and fingerprint-validates canonical capability-profile JSON.</summary>
    /// <param name="json">Canonical compact profile JSON.</param>
    /// <returns>The exact validated profile.</returns>
    /// <exception cref="JsonException">The wire contract or profile fingerprint is invalid.</exception>
    public static ChannelCapabilityProfile DeserializeProfile(string json) =>
        Read<ChannelCapabilityProfile>(json, "Channel capability profile");

    /// <summary>Serializes a realization plan as deterministic canonical JSON.</summary>
    /// <param name="plan">Plan to serialize.</param>
    /// <returns>Canonical compact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The plan violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">The plan contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The plan has no canonical JSON representation.</exception>
    public static string SerializePlan(ChannelRealizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(plan, CreateOptions()));
    }

    /// <summary>Parses and fingerprint-validates canonical realization-plan JSON without resolving its authority.</summary>
    /// <remarks>
    /// A successfully parsed plan may still cite a different definition, profile, variant, evidence graph,
    /// configuration, or producer. Use the context-bound <see cref="DeserializePlan(string,
    /// ExecutionDefinitionDocument, ChannelCapabilityProfile, ExecutionProvenance)"/> before acting on it.
    /// </remarks>
    /// <param name="json">Canonical compact plan JSON.</param>
    /// <returns>The structurally valid, fingerprint-consistent, but unresolved realization plan.</returns>
    /// <exception cref="JsonException">The wire contract, current schema, or plan fingerprint is invalid.</exception>
    public static ChannelRealizationPlan ParsePlan(string json) =>
        Read<ChannelRealizationPlan>(json, "Channel realization plan");

    /// <summary>Deserializes and authoritatively resolves a realization plan against exact trusted context.</summary>
    /// <param name="json">Canonical compact plan JSON.</param>
    /// <param name="definitionDocument">Exact canonical Channel definition authority.</param>
    /// <param name="profile">Exact target capability-profile authority.</param>
    /// <param name="expectedProvenance">Exact trusted compiler producer and source attribution.</param>
    /// <returns>A resolved authority token whose realizability claim is safe to inspect.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionDocument"/>, <paramref name="profile"/>, or <paramref name="expectedProvenance"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">
    /// The wire contract, current schema, fingerprint, exact references, evidence decisions, configuration,
    /// diagnostics, or provenance is invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">The definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The definition contains an unsupported runtime value.</exception>
    public static ResolvedChannelRealizationPlan DeserializePlan(
        string json,
        ExecutionDefinitionDocument definitionDocument,
        ChannelCapabilityProfile profile,
        ExecutionProvenance expectedProvenance)
    {
        var plan = ParsePlan(json);
        var validation = ChannelRealizationPlanValidator.TryResolve(
            plan,
            definitionDocument,
            profile,
            expectedProvenance,
            out var resolved);
        if (validation.IsValid)
            return resolved!;

        throw new JsonException(
            "Channel realization plan is not authoritative for the supplied context: "
            + string.Join(
                " ",
                validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} at '{diagnostic.Location ?? "/"}': {diagnostic.Message}")));
    }

    static T Read<T>(string json, string subject)
        where T : class
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                subject,
                out T? value,
                out var error)
            && value is not null)
        {
            return value;
        }

        throw new JsonException($"{subject} is invalid at '{error.Location}': {error.Message}");
    }
}
