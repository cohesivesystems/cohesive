using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Aspire.Pulumi;

/// <summary>Deterministic identity of an exact Cohesive-to-Pulumi deployment handoff.</summary>
public sealed record AspirePulumiDeploymentHandoffFingerprint
{
    /// <summary>Digest algorithm used by the current handoff fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current handoff fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-aspire-pulumi-handoff/v1-c14n/v1";

    /// <summary>Creates handoff fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is empty or white-space.</exception>
    [JsonConstructor]
    public AspirePulumiDeploymentHandoffFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>
/// Portable, persisted handoff from one exact Cohesive infrastructure realization to an existing Pulumi program.
/// </summary>
/// <remarks>
/// Cohesive remains the authority for requirements, capability discharge, placement, and lifecycle intent. Aspire
/// owns pipeline orchestration. Pulumi remains the sole authority for provider reconciliation and physical state.
/// The handoff carries the full manifest and realization so a Pulumi program can consume them without rebuilding an
/// application-side infrastructure model.
/// </remarks>
public sealed record AspirePulumiDeploymentHandoff
{
    /// <summary>Current persisted handoff schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.aspire-pulumi-handoff/1";

    /// <summary>Creates or restores one exact deployment handoff.</summary>
    /// <param name="schemaVersion">Exact persisted handoff schema version.</param>
    /// <param name="environmentName">Cohesive/Aspire environment profile selected for this deployment.</param>
    /// <param name="pulumiProjectName">Expected project name from the existing Pulumi program.</param>
    /// <param name="pulumiStackName">Pulumi stack name or fully qualified stack identity.</param>
    /// <param name="programDirectory">Repository-relative directory containing the existing Pulumi program.</param>
    /// <param name="lifecycleAuthority">Pulumi state scope that owns resources managed by the selected target.</param>
    /// <param name="manifest">Exact target-deployment declaration compiled by Cohesive.Infra.</param>
    /// <param name="realization">Complete exact realization derived from <paramref name="manifest"/>.</param>
    /// <param name="diagnostics">Normalized non-error diagnostics retained from deployment compilation.</param>
    /// <param name="fingerprint">Persisted fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A scalar is invalid; the manifest and realization fences differ; capability or readiness discharge is
    /// incomplete; diagnostics contain an error; selected-target lifecycle ownership differs from
    /// <paramref name="lifecycleAuthority"/>; or <paramref name="fingerprint"/> is not canonical.
    /// </exception>
    [JsonConstructor]
    public AspirePulumiDeploymentHandoff(
        string schemaVersion,
        string environmentName,
        string pulumiProjectName,
        string pulumiStackName,
        RepositoryPath programDirectory,
        InfrastructureLifecycleAuthorityId lifecycleAuthority,
        InfrastructureTargetDeploymentManifest manifest,
        InfrastructureRealization realization,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        AspirePulumiDeploymentHandoffFingerprint? fingerprint = null)
    {
        SchemaVersion = RequireToken(schemaVersion, nameof(schemaVersion));
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Aspire-Pulumi handoff schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        EnvironmentName = RequireToken(environmentName, nameof(environmentName));
        PulumiProjectName = RequireToken(pulumiProjectName, nameof(pulumiProjectName));
        PulumiStackName = RequireToken(pulumiStackName, nameof(pulumiStackName));
        if (string.IsNullOrWhiteSpace(programDirectory.Value))
            throw new ArgumentException("A Pulumi handoff requires a repository-relative program directory.", nameof(programDirectory));
        if (string.IsNullOrWhiteSpace(lifecycleAuthority.Value))
            throw new ArgumentException("A Pulumi handoff requires a lifecycle authority.", nameof(lifecycleAuthority));

        ProgramDirectory = programDirectory;
        LifecycleAuthority = lifecycleAuthority;
        Manifest = Guard.RequireNotNull(manifest);
        Realization = Guard.RequireNotNull(realization);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        ValidateExactRealization(nameof(lifecycleAuthority));

        var computed = ComputeFingerprint(
            SchemaVersion,
            EnvironmentName,
            PulumiProjectName,
            PulumiStackName,
            ProgramDirectory,
            LifecycleAuthority,
            Manifest.ToReference(),
            Realization.ToReference(),
            Diagnostics);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied Aspire-Pulumi handoff fingerprint is not canonical.", nameof(fingerprint));

        Fingerprint = computed;
    }

    /// <summary>Exact persisted handoff schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Cohesive/Aspire environment profile selected for this deployment.</summary>
    public string EnvironmentName { get; }

    /// <summary>Expected project name from the existing Pulumi program.</summary>
    public string PulumiProjectName { get; }

    /// <summary>Pulumi stack name or fully qualified stack identity.</summary>
    public string PulumiStackName { get; }

    /// <summary>Repository-relative directory containing the existing Pulumi program.</summary>
    public RepositoryPath ProgramDirectory { get; }

    /// <summary>Pulumi state scope that owns resources managed by the selected target.</summary>
    public InfrastructureLifecycleAuthorityId LifecycleAuthority { get; }

    /// <summary>Exact target-deployment declaration compiled by Cohesive.Infra.</summary>
    public InfrastructureTargetDeploymentManifest Manifest { get; }

    /// <summary>Complete exact realization derived from <see cref="Manifest"/>.</summary>
    public InfrastructureRealization Realization { get; }

    /// <summary>Normalized non-error diagnostics retained from deployment compilation.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Deterministic fingerprint of the exact semantic and execution handoff.</summary>
    public AspirePulumiDeploymentHandoffFingerprint Fingerprint { get; }

    /// <summary>Creates a handoff from a completed Cohesive deployment compilation.</summary>
    /// <param name="plan">Completed target deployment plan.</param>
    /// <param name="environmentName">Cohesive/Aspire environment profile selected for this deployment.</param>
    /// <param name="pulumiProjectName">Expected project name from the existing Pulumi program.</param>
    /// <param name="pulumiStackName">Pulumi stack name or fully qualified stack identity.</param>
    /// <param name="programDirectory">Repository-relative directory containing the existing Pulumi program.</param>
    /// <param name="lifecycleAuthority">Pulumi state scope owning resources managed by the selected target.</param>
    /// <returns>An exact, serializable handoff fenced to the compiled manifest and realization.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The plan is incomplete, readiness obligations are incomplete, or another handoff invariant is violated.
    /// </exception>
    public static AspirePulumiDeploymentHandoff Create(
        InfrastructureTargetDeploymentPlan plan,
        string environmentName,
        string pulumiProjectName,
        string pulumiStackName,
        RepositoryPath programDirectory,
        InfrastructureLifecycleAuthorityId lifecycleAuthority)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsComplete || plan.Realization is null)
        {
            var codes = string.Join(", ", plan.Diagnostics.Select(static diagnostic => diagnostic.Code));
            throw new ArgumentException(
                $"A Pulumi handoff requires a complete target deployment plan. Diagnostics: {codes}",
                nameof(plan));
        }
        if (!plan.Realization.IsReadinessObligationComplete)
        {
            throw new ArgumentException(
                "A Pulumi handoff requires complete physical readiness-obligation lowering.",
                nameof(plan));
        }

        return new(
            CurrentSchemaVersion,
            environmentName,
            pulumiProjectName,
            pulumiStackName,
            programDirectory,
            lifecycleAuthority,
            plan.Manifest,
            plan.Realization,
            plan.Diagnostics);
    }

    /// <summary>Serializes the full handoff using Cohesive's strict portable-document options.</summary>
    /// <param name="formatting">Whether the JSON is compact or indented.</param>
    /// <returns>The complete handoff JSON.</returns>
    public string ToJson(PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented) =>
        JsonSerializer.Serialize(this, StrictDocumentJson.CreateOptions(formatting));

    /// <summary>Serializes the full handoff to canonical UTF-8 JSON bytes.</summary>
    /// <returns>Canonical bytes suitable for persistence and Pulumi consumption.</returns>
    public byte[] ToCanonicalBytes() =>
        StrictDocumentJson.GetCanonicalBytes(this, StrictDocumentJson.CreateOptions());

    /// <summary>Compares handoffs structurally.</summary>
    /// <param name="other">Other handoff.</param>
    /// <returns><see langword="true"/> when every persisted field is equal.</returns>
    public bool Equals(AspirePulumiDeploymentHandoff? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && string.Equals(EnvironmentName, other.EnvironmentName, StringComparison.Ordinal)
        && string.Equals(PulumiProjectName, other.PulumiProjectName, StringComparison.Ordinal)
        && string.Equals(PulumiStackName, other.PulumiStackName, StringComparison.Ordinal)
        && ProgramDirectory == other.ProgramDirectory
        && LifecycleAuthority == other.LifecycleAuthority
        && Manifest == other.Manifest
        && Realization == other.Realization
        && Diagnostics.SequenceEqual(other.Diagnostics)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for the persisted handoff.</summary>
    /// <returns>A hash code derived from every persisted field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(EnvironmentName, StringComparer.Ordinal);
        hash.Add(PulumiProjectName, StringComparer.Ordinal);
        hash.Add(PulumiStackName, StringComparer.Ordinal);
        hash.Add(ProgramDirectory);
        hash.Add(LifecycleAuthority);
        hash.Add(Manifest);
        hash.Add(Realization);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    void ValidateExactRealization(string lifecycleAuthorityParamName)
    {
        var realizationReference = Realization.ToReference();
        if (Manifest.Definition != realizationReference.Definition
            || Manifest.TargetFacilities.Profile.ToReference() != realizationReference.Profile
            || Manifest.TargetFacilities.Profile.Target != realizationReference.Target
            || Manifest.TargetFacilities.Variant != realizationReference.Variant)
        {
            throw new ArgumentException(
                "The Pulumi handoff manifest and realization do not share exact definition, profile, target, and variant fences.",
                nameof(Realization));
        }
        if (!Realization.IsCapabilityWitnessComplete)
            throw new ArgumentException("The Pulumi handoff realization has incomplete capability witnesses.", nameof(Realization));
        if (!Realization.IsReadinessObligationComplete)
            throw new ArgumentException("The Pulumi handoff realization has incomplete readiness obligations.", nameof(Realization));
        if (Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("A Pulumi handoff cannot retain error diagnostics.", nameof(Diagnostics));

        var selectedTarget = realizationReference.Target;
        var incompatible = Realization.Lifecycle.Bindings.FirstOrDefault(binding =>
            binding.Interpreter == selectedTarget
            && binding.Disposition == InfrastructureLifecycleDisposition.Managed
            && binding.Authority != LifecycleAuthority);
        if (incompatible is not null)
        {
            throw new ArgumentException(
                $"Selected target '{selectedTarget.Value}' manages resource '{incompatible.Resource.Value}' through "
                + $"authority '{incompatible.Authority.Value}', not declared Pulumi authority '{LifecycleAuthority.Value}'.",
                lifecycleAuthorityParamName);
        }
    }

    static string RequireToken(string value, string paramName)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value);
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Deployment handoff identities cannot contain white-space.", paramName);
        return value;
    }

    static AspirePulumiDeploymentHandoffFingerprint ComputeFingerprint(
        string schemaVersion,
        string environmentName,
        string pulumiProjectName,
        string pulumiStackName,
        RepositoryPath programDirectory,
        InfrastructureLifecycleAuthorityId lifecycleAuthority,
        InfrastructureTargetDeploymentManifestReference manifest,
        InfrastructureRealizationReference realization,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new HandoffIdentity(
                schemaVersion,
                environmentName,
                pulumiProjectName,
                pulumiStackName,
                programDirectory,
                lifecycleAuthority,
                manifest,
                realization,
                diagnostics),
            StrictDocumentJson.CreateOptions());
        return new(
            AspirePulumiDeploymentHandoffFingerprint.CurrentAlgorithm,
            AspirePulumiDeploymentHandoffFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record HandoffIdentity(
        string SchemaVersion,
        string EnvironmentName,
        string PulumiProjectName,
        string PulumiStackName,
        RepositoryPath ProgramDirectory,
        InfrastructureLifecycleAuthorityId LifecycleAuthority,
        InfrastructureTargetDeploymentManifestReference Manifest,
        InfrastructureRealizationReference Realization,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}
