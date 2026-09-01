using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Stable diagnostics emitted while elaborating infrastructure binding contracts.</summary>
public static class InfrastructureBindingElaborationDiagnosticCodes
{
    /// <summary>The binding-elaboration profile uses a schema unsupported by this compiler.</summary>
    public const string ProfileSchemaUnsupported = "infra.bindings.profile.schemaUnsupported";

    /// <summary>The binding-elaboration profile does not understand the supplied definition schema.</summary>
    public const string DefinitionSchemaUnsupported = "infra.bindings.definition.schemaUnsupported";

    /// <summary>No rule elaborates one exact binding contract.</summary>
    public const string ContractUnavailable = "infra.bindings.elaboration.unavailable";

    /// <summary>Several rules elaborate one exact binding contract and no policy selected an authority.</summary>
    public const string ContractAmbiguous = "infra.bindings.elaboration.ambiguous";

    /// <summary>A binding-derived requirement identity conflicts with another exact definition requirement.</summary>
    public const string ObligationIdentityConflict = "infra.bindings.obligation.identityConflict";
}

/// <summary>Deterministic fingerprint of one complete binding-elaboration profile.</summary>
public sealed record InfrastructureBindingElaborationProfileFingerprint
{
    /// <summary>Digest algorithm used by the current profile fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current profile fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-binding-elaboration-profile/v2-c14n/v1";

    /// <summary>Creates binding-elaboration profile fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationProfileFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Exact versioned reference to one binding-elaboration profile.</summary>
public sealed record InfrastructureBindingElaborationProfileReference
{
    /// <summary>Creates an exact binding-elaboration profile reference.</summary>
    /// <param name="schemaVersion">Exact persisted profile schema.</param>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="fingerprint">Exact canonical profile fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> or <paramref name="id"/> is empty or default.</exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationProfileReference(
        string schemaVersion,
        InfrastructureBindingElaborationProfileId id,
        InfrastructureBindingElaborationProfileFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A binding-elaboration profile reference requires an identity.", nameof(id));

        Id = id;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted profile schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned profile identity.</summary>
    public InfrastructureBindingElaborationProfileId Id { get; }

    /// <summary>Exact canonical profile fingerprint.</summary>
    public InfrastructureBindingElaborationProfileFingerprint Fingerprint { get; }
}

/// <summary>One versioned rule that elaborates a provider-neutral binding contract into capability obligations.</summary>
/// <remarks>
/// Assurance demands such as authenticated access, durability, ordering, or telemetry are represented by the same
/// provider-neutral capability identities as facility demands. The capability compiler therefore proves both through
/// one authority instead of maintaining a parallel guarantee model. A rule is a pure mapping from one exact contract
/// identity; semantic contract variants requiring different obligations must use distinct versioned contract identities
/// rather than inspect ambient state or execute a host-language callback.
/// </remarks>
public sealed record InfrastructureBindingElaborationRule
{
    /// <summary>Creates a binding-elaboration rule.</summary>
    /// <param name="id">Stable versioned rule identity.</param>
    /// <param name="contract">Exact provider-neutral binding contract elaborated by the rule.</param>
    /// <param name="requiredCapabilities">Non-empty capability and assurance obligations induced by the contract.</param>
    /// <param name="sourceReferences">Non-empty producer or specification references supporting the rule.</param>
    /// <exception cref="ArgumentException">An identity or collection entry is empty, duplicated, or missing.</exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationRule(
        InfrastructureBindingElaborationRuleId id,
        InfrastructureBindingContractId contract,
        ImmutableArray<InfrastructureCapabilityId> requiredCapabilities,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A binding-elaboration rule requires a stable identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(contract.Value))
        {
            throw new ArgumentException("A binding-elaboration rule requires an exact contract identity.", nameof(contract));
        }

        Id = id;
        Contract = contract;
        RequiredCapabilities = InfrastructureCapabilityCollections.IdentitySet(
            requiredCapabilities,
            static capability => capability.Value,
            nameof(requiredCapabilities),
            requireNonEmpty: true);
        SourceReferences = InfrastructureCapabilityCollections.ReferenceSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Stable versioned rule identity.</summary>
    public InfrastructureBindingElaborationRuleId Id { get; }

    /// <summary>Exact provider-neutral binding contract elaborated by this rule.</summary>
    public InfrastructureBindingContractId Contract { get; }

    /// <summary>Induced capability and assurance obligations in ordinal capability order.</summary>
    public ImmutableArray<InfrastructureCapabilityId> RequiredCapabilities { get; }

    /// <summary>Attributable producer or specification references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares binding-elaboration rules structurally.</summary>
    /// <param name="other">Other rule.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBindingElaborationRule? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Contract == other.Contract
        && RequiredCapabilities.SequenceEqual(other.RequiredCapabilities)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this rule.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Contract);
        foreach (var capability in RequiredCapabilities)
        {
            hash.Add(capability);
        }

        foreach (var source in SourceReferences)
        {
            hash.Add(source);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Portable, exactly fingerprinted catalog of deterministic binding-elaboration rules.</summary>
public sealed record InfrastructureBindingElaborationProfile
{
    /// <summary>Current persisted binding-elaboration profile schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.bindings/2";

    /// <summary>Explicit empty profile used by the compatibility compiler overload to preserve fail-closed behavior.</summary>
    public static InfrastructureBindingElaborationProfile Empty { get; } = new(
        CurrentSchemaVersion,
        new("cohesive.infra.bindings/none/v1"),
        [InfrastructureDefinitionDocument.CurrentSchemaVersion],
        []);

    /// <summary>Creates a binding-elaboration profile.</summary>
    /// <param name="schemaVersion">Exact persisted profile schema.</param>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="supportedDefinitionSchemaVersions">Exact definition schemas understood by the profile.</param>
    /// <param name="rules">Versioned rules in any producer order.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schemaVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, schema, rule, or source is invalid or duplicated, or <paramref name="fingerprint"/> is not canonical.
    /// </exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationProfile(
        string schemaVersion,
        InfrastructureBindingElaborationProfileId id,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureBindingElaborationRule> rules = default,
        InfrastructureBindingElaborationProfileFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A binding-elaboration profile requires a stable identity.", nameof(id));

        Id = id;
        SupportedDefinitionSchemaVersions = InfrastructureCapabilityCollections.StringSet(
            supportedDefinitionSchemaVersions,
            nameof(supportedDefinitionSchemaVersions),
            requireNonEmpty: true);
        Rules = NormalizeRules(rules);
        var computed = InfrastructureBindingElaborationProfileFingerprinting.Compute(
            SchemaVersion,
            Id,
            SupportedDefinitionSchemaVersions,
            Rules);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied binding-elaboration profile fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted profile schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned profile identity.</summary>
    public InfrastructureBindingElaborationProfileId Id { get; }

    /// <summary>Supported infrastructure-definition schemas in ordinal order.</summary>
    public ImmutableArray<string> SupportedDefinitionSchemaVersions { get; }

    /// <summary>Rules in contract-then-rule identity order.</summary>
    public ImmutableArray<InfrastructureBindingElaborationRule> Rules { get; }

    /// <summary>Deterministic fingerprint of the exact profile and every rule authority.</summary>
    public InfrastructureBindingElaborationProfileFingerprint Fingerprint { get; }

    /// <summary>Creates an exact reference to this binding-elaboration profile.</summary>
    /// <returns>The exact schema, identity, and fingerprint fence.</returns>
    public InfrastructureBindingElaborationProfileReference ToReference() => new(SchemaVersion, Id, Fingerprint);

    /// <summary>Finds all rules claiming authority for one exact binding contract.</summary>
    /// <param name="contract">Exact contract to elaborate.</param>
    /// <returns>Matching rules in stable rule-identity order.</returns>
    /// <exception cref="ArgumentException"><paramref name="contract"/> is a default identity.</exception>
    public ImmutableArray<InfrastructureBindingElaborationRule> FindRules(InfrastructureBindingContractId contract)
    {
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("A default binding contract cannot be elaborated.", nameof(contract));

        return
        [
            .. Rules.Where(rule => rule.Contract == contract)
        ];
    }

    /// <summary>Compares binding-elaboration profiles structurally.</summary>
    /// <param name="other">Other profile.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBindingElaborationProfile? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && SupportedDefinitionSchemaVersions.SequenceEqual(other.SupportedDefinitionSchemaVersions, StringComparer.Ordinal)
        && Rules.SequenceEqual(other.Rules)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this profile.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        foreach (var version in SupportedDefinitionSchemaVersions)
            hash.Add(version, StringComparer.Ordinal);
        foreach (var rule in Rules)
            hash.Add(rule);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureBindingElaborationRule> NormalizeRules(
        ImmutableArray<InfrastructureBindingElaborationRule> rules)
    {
        if (rules.IsDefaultOrEmpty)
            return [];
        if (rules.Any(static rule => rule is null))
            throw new ArgumentException("Binding-elaboration rules cannot contain null.", nameof(rules));

        var ordered = rules.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Contract.Value, right.Contract.Value);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
        });
        var identities = new HashSet<InfrastructureBindingElaborationRuleId>();
        foreach (var rule in ordered)
        {
            if (!identities.Add(rule.Id))
                throw new ArgumentException($"Binding-elaboration rule '{rule.Id.Value}' is duplicated.", nameof(rules));
        }
        return ordered;
    }
}

/// <summary>Terminal status of deterministic elaboration for one exact binding.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureBindingElaborationStatus
{
    /// <summary>One exact rule produced a complete obligation set.</summary>
    Elaborated = 0,

    /// <summary>No rule recognizes the exact binding contract.</summary>
    Unavailable = 1,

    /// <summary>Several rules claim authority and no policy selected one.</summary>
    Ambiguous = 2,

    /// <summary>A recognized rule could not produce a valid obligation set.</summary>
    Invalid = 3
}

/// <summary>One binding-derived capability or assurance requirement with exact provenance.</summary>
public sealed record InfrastructureBindingObligation
{
    /// <summary>Creates a binding-derived obligation.</summary>
    /// <param name="binding">Exact source binding identity.</param>
    /// <param name="contract">Exact binding contract that induced the obligation.</param>
    /// <param name="rule">Selected elaboration-rule authority.</param>
    /// <param name="requirement">Stable requirement identity and demanded capability.</param>
    /// <param name="location">Canonical definition JSON Pointer that induced the obligation.</param>
    /// <param name="sourceReferences">Attributable rule or specification references.</param>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, location, or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureBindingObligation(
        InfrastructureBindingId binding,
        InfrastructureBindingContractId contract,
        InfrastructureBindingElaborationRuleId rule,
        InfrastructureCapabilityRequirement requirement,
        string location,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            throw new ArgumentException("A binding obligation requires a source binding identity.", nameof(binding));
        }

        if (string.IsNullOrWhiteSpace(contract.Value))
        {
            throw new ArgumentException("A binding obligation requires an exact contract identity.", nameof(contract));
        }

        if (string.IsNullOrWhiteSpace(rule.Value))
        {
            throw new ArgumentException("A binding obligation requires an elaboration-rule authority.", nameof(rule));
        }

        Binding = binding;
        Contract = contract;
        Rule = rule;
        Requirement = Guard.RequireNotNull(requirement);
        Location = Guard.RequireNotNullOrWhiteSpace(location);
        SourceReferences = InfrastructureCapabilityCollections.ReferenceSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Exact source binding identity.</summary>
    public InfrastructureBindingId Binding { get; }

    /// <summary>Exact binding contract that induced the obligation.</summary>
    public InfrastructureBindingContractId Contract { get; }

    /// <summary>Selected elaboration-rule authority.</summary>
    public InfrastructureBindingElaborationRuleId Rule { get; }

    /// <summary>Stable requirement identity and demanded capability.</summary>
    public InfrastructureCapabilityRequirement Requirement { get; }

    /// <summary>Canonical definition JSON Pointer that induced the obligation.</summary>
    public string Location { get; }

    /// <summary>Attributable rule or specification references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Derives the stable capability-requirement identity for one binding obligation.</summary>
    /// <param name="binding">Exact source binding.</param>
    /// <param name="capability">Capability or assurance demanded by the binding.</param>
    /// <returns>A deterministic definition-local requirement identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> or <paramref name="capability"/> is default.</exception>
    public static InfrastructureRequirementId DeriveRequirementId(
        InfrastructureBindingId binding,
        InfrastructureCapabilityId capability)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A binding-derived requirement requires a binding identity.", nameof(binding));
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("A binding-derived requirement requires a capability identity.", nameof(capability));

        return new($"binding/{Uri.EscapeDataString(binding.Value)}/requires/{Uri.EscapeDataString(capability.Value)}");
    }

    /// <summary>Compares binding obligations structurally.</summary>
    /// <param name="other">Other obligation.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBindingObligation? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Binding == other.Binding
        && Contract == other.Contract
        && Rule == other.Rule
        && Requirement == other.Requirement
        && string.Equals(Location, other.Location, StringComparison.Ordinal)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this obligation.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Binding);
        hash.Add(Contract);
        hash.Add(Rule);
        hash.Add(Requirement);
        hash.Add(Location, StringComparer.Ordinal);
        foreach (var source in SourceReferences)
        {
            hash.Add(source);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Machine-readable elaboration explanation for one exact infrastructure binding.</summary>
public sealed record InfrastructureBindingElaborationDecision
{
    /// <summary>Creates a per-binding elaboration decision.</summary>
    /// <param name="binding">Exact source binding identity.</param>
    /// <param name="contract">Exact source binding contract.</param>
    /// <param name="location">Canonical definition JSON Pointer for the contract.</param>
    /// <param name="status">Terminal elaboration status.</param>
    /// <param name="rules">Candidate or selected rule identities.</param>
    /// <param name="obligations">Elaborated obligations, when successful.</param>
    /// <exception cref="ArgumentException">An identity, collection, or status-specific invariant is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationDecision(
        InfrastructureBindingId binding,
        InfrastructureBindingContractId contract,
        string location,
        InfrastructureBindingElaborationStatus status,
        ImmutableArray<InfrastructureBindingElaborationRuleId> rules = default,
        ImmutableArray<InfrastructureBindingObligation> obligations = default)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A binding-elaboration decision requires a binding identity.", nameof(binding));
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("A binding-elaboration decision requires a contract identity.", nameof(contract));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported binding-elaboration status.");

        Binding = binding;
        Contract = contract;
        Location = Guard.RequireNotNullOrWhiteSpace(location);
        Status = status;
        Rules = InfrastructureCapabilityCollections.IdentitySet(rules, static rule => rule.Value, nameof(rules));
        Obligations = NormalizeObligations(obligations);

        if (status == InfrastructureBindingElaborationStatus.Elaborated
            && (Rules.Length != 1 || Obligations.IsDefaultOrEmpty))
        {
            throw new ArgumentException("An elaborated binding requires one selected rule and at least one obligation.", nameof(status));
        }
        if (status == InfrastructureBindingElaborationStatus.Unavailable
            && (!Rules.IsDefaultOrEmpty || !Obligations.IsDefaultOrEmpty))
        {
            throw new ArgumentException("An unavailable binding cannot claim rules or obligations.", nameof(status));
        }
        if (status == InfrastructureBindingElaborationStatus.Ambiguous
            && (Rules.Length < 2 || !Obligations.IsDefaultOrEmpty))
        {
            throw new ArgumentException("An ambiguous binding requires several candidate rules and no selected obligations.", nameof(status));
        }
        if (status == InfrastructureBindingElaborationStatus.Invalid && !Obligations.IsDefaultOrEmpty)
            throw new ArgumentException("An invalid binding cannot claim valid obligations.", nameof(status));

        foreach (var obligation in Obligations)
        {
            if (obligation.Binding != Binding
                || obligation.Contract != Contract
                || obligation.Rule != Rules[0]
                || !string.Equals(obligation.Location, Location, StringComparison.Ordinal))
            {
                throw new ArgumentException("A binding obligation does not match its elaboration decision.", nameof(obligations));
            }
        }
    }

    /// <summary>Exact source binding identity.</summary>
    public InfrastructureBindingId Binding { get; }

    /// <summary>Exact source binding contract.</summary>
    public InfrastructureBindingContractId Contract { get; }

    /// <summary>Canonical definition JSON Pointer for the contract.</summary>
    public string Location { get; }

    /// <summary>Terminal elaboration status.</summary>
    public InfrastructureBindingElaborationStatus Status { get; }

    /// <summary>Selected or candidate rule identities in ordinal order.</summary>
    public ImmutableArray<InfrastructureBindingElaborationRuleId> Rules { get; }

    /// <summary>Binding-derived obligations in stable requirement-identity order.</summary>
    public ImmutableArray<InfrastructureBindingObligation> Obligations { get; }

    /// <summary>Whether one exact rule produced a valid obligation set.</summary>
    [JsonIgnore]
    public bool IsElaborated => Status == InfrastructureBindingElaborationStatus.Elaborated;

    /// <summary>Compares elaboration decisions structurally.</summary>
    /// <param name="other">Other decision.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBindingElaborationDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Binding == other.Binding
        && Contract == other.Contract
        && string.Equals(Location, other.Location, StringComparison.Ordinal)
        && Status == other.Status
        && Rules.SequenceEqual(other.Rules)
        && Obligations.SequenceEqual(other.Obligations);

    /// <summary>Returns a structural hash code for this decision.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Binding);
        hash.Add(Contract);
        hash.Add(Location, StringComparer.Ordinal);
        hash.Add(Status);
        foreach (var rule in Rules)
            hash.Add(rule);
        foreach (var obligation in Obligations)
            hash.Add(obligation);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureBindingObligation> NormalizeObligations(
        ImmutableArray<InfrastructureBindingObligation> obligations)
    {
        if (obligations.IsDefaultOrEmpty)
            return [];
        if (obligations.Any(static obligation => obligation is null))
            throw new ArgumentException("Binding obligations cannot contain null.", nameof(obligations));

        var ordered = obligations.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Requirement.Id.Value, right.Requirement.Id.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Requirement.Id == ordered[index].Requirement.Id)
                throw new ArgumentException($"Binding obligation '{ordered[index].Requirement.Id.Value}' is duplicated.", nameof(obligations));
        }
        return ordered;
    }
}

/// <summary>Deterministic semantic fingerprint of one complete binding-elaboration report.</summary>
public sealed record InfrastructureBindingElaborationFingerprint
{
    /// <summary>Digest algorithm used by the current report fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current report fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-binding-elaboration/v2-c14n/v1";

    /// <summary>Creates binding-elaboration fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Exactly fingerprinted binding-elaboration result and machine-readable explanation artifact.</summary>
public sealed record InfrastructureBindingElaborationReport
{
    /// <summary>Creates or restores a binding-elaboration report.</summary>
    /// <param name="definition">Exact canonical definition elaborated by the report.</param>
    /// <param name="profile">Exact elaboration-profile reference.</param>
    /// <param name="decisions">One decision for every exact definition binding.</param>
    /// <param name="diagnostics">Structured elaboration diagnostics.</param>
    /// <param name="fingerprint">Persisted semantic fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="profile"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Decision coverage is not exact, diagnostics are invalid, or <paramref name="fingerprint"/> is not canonical.
    /// </exception>
    [JsonConstructor]
    public InfrastructureBindingElaborationReport(
        InfrastructureDefinitionDocument definition,
        InfrastructureBindingElaborationProfileReference profile,
        ImmutableArray<InfrastructureBindingElaborationDecision> decisions = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        InfrastructureBindingElaborationFingerprint? fingerprint = null)
    {
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        Decisions = NormalizeDecisions(decisions);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        ValidateCoverage();

        var computed = InfrastructureBindingElaborationFingerprinting.Compute(
            Definition.ToReference(),
            Profile,
            Decisions);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied binding-elaboration report fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact canonical definition elaborated by this report.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Exact binding-elaboration profile reference.</summary>
    public InfrastructureBindingElaborationProfileReference Profile { get; }

    /// <summary>One machine-readable explanation decision per binding.</summary>
    public ImmutableArray<InfrastructureBindingElaborationDecision> Decisions { get; }

    /// <summary>Structured diagnostics in deterministic portable-document order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Fingerprint of the exact definition, profile, decisions, and obligation sets; diagnostic prose is excluded.</summary>
    public InfrastructureBindingElaborationFingerprint Fingerprint { get; }

    /// <summary>Whether every binding produced obligations and no error diagnostic remains.</summary>
    [JsonIgnore]
    public bool IsComplete =>
        Decisions.All(static decision => decision.IsElaborated)
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>All successfully elaborated obligations in stable requirement-identity order.</summary>
    [JsonIgnore]
    public ImmutableArray<InfrastructureBindingObligation> Obligations =>
    [
        .. Decisions.SelectMany(static decision => decision.Obligations)
            .OrderBy(static obligation => obligation.Requirement.Id.Value, StringComparer.Ordinal)
    ];

    /// <summary>Finds the machine-readable explanation decision for one binding.</summary>
    /// <param name="binding">Exact binding identity to explain.</param>
    /// <returns>The matching decision, or <see langword="null"/> when the binding is absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is a default identity.</exception>
    public InfrastructureBindingElaborationDecision? FindDecision(InfrastructureBindingId binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("A default binding identity cannot be explained.", nameof(binding));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Decisions,
            binding,
            static (decision, sought) =>
                StringComparer.Ordinal.Compare(decision.Binding.Value, sought.Value));
        return index < 0 ? null : Decisions[index];
    }

    /// <summary>Compares binding-elaboration reports structurally.</summary>
    /// <param name="other">Other report.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBindingElaborationReport? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Definition == other.Definition
        && Profile == other.Profile
        && Decisions.SequenceEqual(other.Decisions)
        && Diagnostics.SequenceEqual(other.Diagnostics)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this report.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        hash.Add(Profile);
        foreach (var decision in Decisions)
            hash.Add(decision);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    void ValidateCoverage()
    {
        if (Definition.Definition.Bindings.Length != Decisions.Length)
            throw new ArgumentException("A binding-elaboration report requires one decision for every exact binding.", nameof(Decisions));

        for (var index = 0; index < Decisions.Length; index++)
        {
            var binding = Definition.Definition.Bindings[index];
            var expectedLocation = InfrastructureBindingElaborator.BindingLocation(index);
            if (Decisions[index].Binding != binding.Id
                || Decisions[index].Contract != binding.Contract
                || !string.Equals(Decisions[index].Location, expectedLocation, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Binding-elaboration decision '{Decisions[index].Binding.Value}' does not match the exact definition binding.",
                    nameof(Decisions));
            }
        }
    }

    static ImmutableArray<InfrastructureBindingElaborationDecision> NormalizeDecisions(
        ImmutableArray<InfrastructureBindingElaborationDecision> decisions)
    {
        if (decisions.IsDefaultOrEmpty)
            return [];
        if (decisions.Any(static decision => decision is null))
            throw new ArgumentException("Binding-elaboration decisions cannot contain null.", nameof(decisions));

        var ordered = decisions.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Binding.Value, right.Binding.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Binding == ordered[index].Binding)
                throw new ArgumentException($"Binding-elaboration decision '{ordered[index].Binding.Value}' is duplicated.", nameof(decisions));
        }
        return ordered;
    }

}

/// <summary>Deterministically elaborates exact binding contracts into provider-neutral capability obligations.</summary>
public static class InfrastructureBindingElaborator
{
    const string ProfileSelectionStage = "infrastructure-binding-profile-selection";
    const string BindingElaborationStage = "infrastructure-binding-elaboration";

    /// <summary>Elaborates every binding in one exact definition through one exact profile.</summary>
    /// <param name="definition">Exact canonical infrastructure definition.</param>
    /// <param name="profile">Exactly fingerprinted binding-elaboration profile.</param>
    /// <returns>A fingerprinted report with one explanation decision per binding and normalized diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    public static InfrastructureBindingElaborationReport Elaborate(
        InfrastructureDefinitionDocument definition,
        InfrastructureBindingElaborationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);

        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        var profileSchemaSupported = string.Equals(
            profile.SchemaVersion,
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            StringComparison.Ordinal);
        if (!profileSchemaSupported)
        {
            diagnostics.Add(new(
                InfrastructureBindingElaborationDiagnosticCodes.ProfileSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Binding-elaboration profile schema '{profile.SchemaVersion}' is unsupported; expected '{InfrastructureBindingElaborationProfile.CurrentSchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: profile.Id.Value,
                    sourceReferences: [InfrastructureDiagnosticReferences.BindingProfile(profile)],
                    resolutionOptions: ["Select an exact binding-elaboration profile using a supported schema."],
                    expected: InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
                    observed: profile.SchemaVersion)));
        }

        var definitionSchemaSupported = profile.SupportedDefinitionSchemaVersions.Contains(
            definition.SchemaVersion,
            StringComparer.Ordinal);
        if (!definitionSchemaSupported)
        {
            diagnostics.Add(new(
                InfrastructureBindingElaborationDiagnosticCodes.DefinitionSchemaUnsupported,
                DiagnosticSeverity.Error,
                $"Binding-elaboration profile '{profile.Id.Value}' does not support infrastructure definition schema '{definition.SchemaVersion}'.",
                Location: "/schemaVersion",
                Evidence: new(
                    stage: ProfileSelectionStage,
                    subject: $"{definition.Definition.Id.Value}@{definition.Definition.Revision.Value}",
                    sourceReferences:
                    [
                        InfrastructureDiagnosticReferences.Definition(definition),
                        InfrastructureDiagnosticReferences.BindingProfile(profile)
                    ],
                    resolutionOptions: ["Select an exact binding-elaboration profile supporting the definition schema."],
                    expected: string.Join(", ", profile.SupportedDefinitionSchemaVersions),
                    observed: definition.SchemaVersion)));
        }

        var explicitRequirements = definition.Definition.Workloads
            .SelectMany(static workload => workload.Requirements)
            .Concat(definition.Definition.Resources.SelectMany(static resource => resource.Requirements))
            .Select(static requirement => requirement.Id)
            .ToHashSet();
        var decisions = ImmutableArray.CreateBuilder<InfrastructureBindingElaborationDecision>(
            definition.Definition.Bindings.Length);

        for (var index = 0; index < definition.Definition.Bindings.Length; index++)
        {
            var binding = definition.Definition.Bindings[index];
            var location = BindingLocation(index);
            if (!profileSchemaSupported || !definitionSchemaSupported)
            {
                decisions.Add(new(
                    binding.Id,
                    binding.Contract,
                    location,
                    InfrastructureBindingElaborationStatus.Invalid));
                continue;
            }

            var rules = profile.FindRules(binding.Contract);
            if (rules.IsDefaultOrEmpty)
            {
                decisions.Add(new(
                    binding.Id,
                    binding.Contract,
                    location,
                    InfrastructureBindingElaborationStatus.Unavailable));
                diagnostics.Add(ContractDiagnostic(
                    InfrastructureBindingElaborationDiagnosticCodes.ContractUnavailable,
                    $"Binding contract '{binding.Contract.Value}' has no rule in exact elaboration profile '{profile.Id.Value}'.",
                    definition,
                    profile,
                    binding,
                    location,
                    [],
                    observed: "binding contract not elaborated"));
                continue;
            }

            if (rules.Length > 1)
            {
                var candidateIds = rules.Select(static rule => rule.Id).ToImmutableArray();
                decisions.Add(new(
                    binding.Id,
                    binding.Contract,
                    location,
                    InfrastructureBindingElaborationStatus.Ambiguous,
                    candidateIds));
                diagnostics.Add(ContractDiagnostic(
                    InfrastructureBindingElaborationDiagnosticCodes.ContractAmbiguous,
                    $"Binding contract '{binding.Contract.Value}' has several elaboration authorities; explicit compiler policy must select one.",
                    definition,
                    profile,
                    binding,
                    location,
                    rules,
                    observed: "several candidate elaboration rules"));
                continue;
            }

            var rule = rules[0];
            var obligations = ImmutableArray.CreateBuilder<InfrastructureBindingObligation>(rule.RequiredCapabilities.Length);
            InfrastructureRequirementId conflicting = default;
            foreach (var capability in rule.RequiredCapabilities)
            {
                var requirementId = InfrastructureBindingObligation.DeriveRequirementId(binding.Id, capability);
                if (explicitRequirements.Contains(requirementId))
                {
                    conflicting = requirementId;
                    break;
                }
                obligations.Add(new(
                    binding.Id,
                    binding.Contract,
                    rule.Id,
                    new(requirementId, capability),
                    location,
                    rule.SourceReferences));
            }

            if (!string.IsNullOrWhiteSpace(conflicting.Value))
            {
                decisions.Add(new(
                    binding.Id,
                    binding.Contract,
                    location,
                    InfrastructureBindingElaborationStatus.Invalid,
                    [rule.Id]));
                diagnostics.Add(new(
                    InfrastructureBindingElaborationDiagnosticCodes.ObligationIdentityConflict,
                    DiagnosticSeverity.Error,
                    $"Binding '{binding.Id.Value}' derives requirement identity '{conflicting.Value}', which conflicts with an explicitly declared requirement.",
                    Location: location,
                    SchemaLocation: binding.Contract.Value,
                    Evidence: new(
                        stage: BindingElaborationStage,
                        subject: binding.Id.Value,
                        relatedLocations: [RuleLocation(rule.Id)],
                        sourceReferences:
                        [
                            .. rule.SourceReferences.Select(static reference => reference.Value)
                                .Append(InfrastructureDiagnosticReferences.Definition(definition))
                                .Append(InfrastructureDiagnosticReferences.BindingProfile(profile))
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                        ],
                        resolutionOptions:
                        [
                            "Rename the explicit requirement so binding-derived identities remain unique.",
                            "Select a corrected elaboration rule that induces a non-conflicting requirement."
                        ],
                        expected: "a unique definition-local binding obligation identity",
                        observed: conflicting.Value)));
                continue;
            }

            decisions.Add(new(
                binding.Id,
                binding.Contract,
                location,
                InfrastructureBindingElaborationStatus.Elaborated,
                [rule.Id],
                obligations.MoveToImmutable()));
        }

        return new(
            definition,
            profile.ToReference(),
            decisions.MoveToImmutable(),
            diagnostics.Count == 0 ? [] : diagnostics.ToImmutable());
    }

    internal static string BindingLocation(int index) =>
        $"/definition/bindings/{index.ToString(CultureInfo.InvariantCulture)}/contract";

    static DocumentValidationDiagnostic ContractDiagnostic(
        string code,
        string message,
        InfrastructureDefinitionDocument definition,
        InfrastructureBindingElaborationProfile profile,
        InfrastructureBindingDefinition binding,
        string location,
        ImmutableArray<InfrastructureBindingElaborationRule> rules,
        string observed)
    {
        var relatedLocations = rules
            .Select(static rule => RuleLocation(rule.Id))
            .ToImmutableArray();
        var sourceReferences = rules
            .SelectMany(static rule => rule.SourceReferences)
            .Select(static reference => reference.Value)
            .Append(InfrastructureDiagnosticReferences.Definition(definition))
            .Append(InfrastructureDiagnosticReferences.BindingProfile(profile))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            code,
            DiagnosticSeverity.Error,
            message,
            Location: location,
            SchemaLocation: binding.Contract.Value,
            Evidence: new(
                stage: BindingElaborationStage,
                subject: binding.Id.Value,
                relatedLocations: relatedLocations,
                sourceReferences: sourceReferences,
                resolutionOptions: code == InfrastructureBindingElaborationDiagnosticCodes.ContractUnavailable
                    ?
                    [
                        "Add one attributable rule for the exact binding contract.",
                        "Replace the binding contract with one supported by the selected elaboration profile."
                    ]
                    : ["Configure explicit compiler policy to select one exact elaboration authority."],
                expected: "one exact rule producing capability and assurance obligations",
                observed: observed));
    }

    static string RuleLocation(InfrastructureBindingElaborationRuleId rule) =>
        $"binding-elaboration-rule/{Uri.EscapeDataString(rule.Value)}";
}

static class InfrastructureBindingElaborationProfileFingerprinting
{
    internal static InfrastructureBindingElaborationProfileFingerprint Compute(
        string schemaVersion,
        InfrastructureBindingElaborationProfileId id,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureBindingElaborationRule> rules)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, id, supportedDefinitionSchemaVersions, rules),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureBindingElaborationProfileFingerprint.CurrentAlgorithm,
            InfrastructureBindingElaborationProfileFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureBindingElaborationProfileId Id,
        ImmutableArray<string> SupportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureBindingElaborationRule> Rules);
}

static class InfrastructureBindingElaborationFingerprinting
{
    internal static InfrastructureBindingElaborationFingerprint Compute(
        InfrastructureDefinitionReference definition,
        InfrastructureBindingElaborationProfileReference profile,
        ImmutableArray<InfrastructureBindingElaborationDecision> decisions)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(definition, profile, decisions),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureBindingElaborationFingerprint.CurrentAlgorithm,
            InfrastructureBindingElaborationFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        InfrastructureDefinitionReference Definition,
        InfrastructureBindingElaborationProfileReference Profile,
        ImmutableArray<InfrastructureBindingElaborationDecision> Decisions);
}

static class InfrastructureDiagnosticReferences
{
    internal static string Definition(InfrastructureDefinitionDocument document) =>
        $"{document.Definition.Id.Value}@{document.Definition.Revision.Value}"
        + $"#{document.Fingerprint.Algorithm}:{document.Fingerprint.Canonicalization}:{document.Fingerprint.Value}";

    internal static string DefinitionReference(InfrastructureDefinitionReference definition) =>
        $"{definition.Id.Value}@{definition.Revision.Value}"
        + $"#{definition.Fingerprint.Algorithm}:{definition.Fingerprint.Canonicalization}:{definition.Fingerprint.Value}";

    internal static string CapabilityProfile(InfrastructureCapabilityProfile profile) =>
        $"{profile.Id.Value}"
        + $"#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";

    internal static string CapabilityProfileReference(InfrastructureCapabilityProfileReference profile) =>
        $"{profile.Id.Value}"
        + $"#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";

    internal static string BindingProfile(InfrastructureBindingElaborationProfile profile) =>
        $"{profile.Id.Value}"
        + $"#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";

    internal static string BindingProfileReference(InfrastructureBindingElaborationProfileReference profile) =>
        $"{profile.Id.Value}"
        + $"#{profile.Fingerprint.Algorithm}:{profile.Fingerprint.Canonicalization}:{profile.Fingerprint.Value}";

    internal static string BoundaryAcceptancePolicy(InfrastructureBoundaryAcceptancePolicy policy) =>
        $"{policy.Id.Value}"
        + $"#{policy.Fingerprint.Algorithm}:{policy.Fingerprint.Canonicalization}:{policy.Fingerprint.Value}";

    internal static string BoundaryAcceptancePolicyReference(InfrastructureBoundaryAcceptancePolicyReference policy) =>
        $"{policy.Id.Value}"
        + $"#{policy.Fingerprint.Algorithm}:{policy.Fingerprint.Canonicalization}:{policy.Fingerprint.Value}";
}
