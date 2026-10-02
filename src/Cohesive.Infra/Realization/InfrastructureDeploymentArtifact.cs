using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Portable exact-plan attribution, independent of the deployment orchestrator.</summary>
/// <remarks>
/// This document records compiled intent, not a successful deployment or runtime observation.
/// Its fingerprint detects mismatches; it does not authenticate the producer. Consumers must independently
/// trust its origin and select the expected environment, deployment source and fingerprint.
/// Construction and parsing are deployment-boundary operations, not request-path work.
/// </remarks>
public sealed record InfrastructureDeploymentArtifact
{
    /// <summary>Version of the persisted document and canonical fingerprint input.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.deployment-artifact/1";

    /// <summary>Creates or restores an immutable exact-plan artifact.</summary>
    /// <param name="schemaVersion">Supported document version.</param>
    /// <param name="environmentName">Explicit environment identity.</param>
    /// <param name="deploymentSource">Adapter-owned execution location, such as a project/stack/program reference.</param>
    /// <param name="manifest">Canonical deployment declarations.</param>
    /// <param name="realization">Complete realization with the same exact semantic fences.</param>
    /// <param name="diagnostics">Non-error compiler diagnostics.</param>
    /// <param name="fingerprint">Expected SHA-256 digest, or null when producing a new document.</param>
    /// <exception cref="ArgumentException">Version, identity, completeness, fences or fingerprint are invalid.</exception>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    [JsonConstructor]
    public InfrastructureDeploymentArtifact(string schemaVersion, string environmentName,
        SourceReference deploymentSource, InfrastructureTargetDeploymentManifest manifest,
        InfrastructureRealization realization, ImmutableArray<DocumentValidationDiagnostic> diagnostics = default,
        string? fingerprint = null)
    {
        if (schemaVersion != CurrentSchemaVersion) throw new ArgumentException("Unsupported deployment artifact version.", nameof(schemaVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (environmentName.Any(char.IsWhiteSpace)) throw new ArgumentException("Environment identity contains whitespace.", nameof(environmentName));
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSource.Value);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(realization);
        SchemaVersion = schemaVersion;
        EnvironmentName = environmentName;
        DeploymentSource = deploymentSource;
        Manifest = manifest;
        Realization = realization;
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        RequireCompleteRealization(manifest, realization, Diagnostics);
        var bytes = StrictDocumentJson.GetCanonicalBytes(new Identity(SchemaVersion, EnvironmentName,
            DeploymentSource, Manifest.ToReference(), Realization.ToReference(), Diagnostics), StrictDocumentJson.CreateOptions());
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (fingerprint is not null && !string.Equals(fingerprint, Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Deployment artifact fingerprint is not canonical.", nameof(fingerprint));
    }

    /// <summary>Persisted format version.</summary>
    public string SchemaVersion { get; }
    /// <summary>Explicit environment selected by the producer.</summary>
    public string EnvironmentName { get; }
    /// <summary>Adapter-owned deployment location; never inferred from ambient configuration.</summary>
    public SourceReference DeploymentSource { get; }
    /// <summary>Canonical deployment declaration.</summary>
    public InfrastructureTargetDeploymentManifest Manifest { get; }
    /// <summary>Complete compiled realization, not live evidence.</summary>
    public InfrastructureRealization Realization { get; }
    /// <summary>Normalized compiler diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
    /// <summary>Lowercase SHA-256 of versioned canonical attribution and semantic references.</summary>
    public string Fingerprint { get; }

    /// <summary>Returns the exact source reference used by native bindings and observations.</summary>
    public SourceReference ToSourceReference() => SourceReference.Create("cohesive-infra-deployment", Fingerprint);

    /// <summary>Produces an artifact from a successful deployment compilation.</summary>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    /// <exception cref="ArgumentException">The plan or attribution is incomplete or invalid.</exception>
    public static InfrastructureDeploymentArtifact Create(InfrastructureTargetDeploymentPlan plan,
        string environmentName, SourceReference deploymentSource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsComplete || plan.Realization is null) throw new ArgumentException("A complete deployment plan is required.", nameof(plan));
        return new(CurrentSchemaVersion, environmentName, deploymentSource, plan.Manifest, plan.Realization, plan.Diagnostics);
    }

    /// <summary>Serializes the complete artifact with strict portable-document conventions.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StrictDocumentJson.CreateOptions(PortableDocumentJsonFormatting.Indented));

    /// <summary>Parses a bounded document and requires independently selected attribution before use.</summary>
    /// <param name="json">UTF-8 document, at most four MiB.</param>
    /// <param name="environmentName">Independently selected environment.</param>
    /// <param name="deploymentSource">Independently selected deployment location.</param>
    /// <param name="fingerprint">Independently reviewed fingerprint.</param>
    /// <returns>The validated immutable artifact.</returns>
    /// <exception cref="ArgumentException">Size or expected attribution is invalid.</exception>
    /// <exception cref="JsonException">The document is malformed, has duplicate/unknown fields, or is null.</exception>
    public static InfrastructureDeploymentArtifact Parse(ReadOnlyMemory<byte> json, string environmentName,
        SourceReference deploymentSource, string fingerprint)
    {
        if (json.Length > 4 * 1024 * 1024) throw new ArgumentException("Deployment artifact exceeds the input limit.", nameof(json));
        using var document = JsonDocument.Parse(json);
        if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, "", out _)) throw new JsonException("Duplicate deployment artifact fields.");
        var artifact = document.Deserialize<InfrastructureDeploymentArtifact>(StrictDocumentJson.CreateOptions())
            ?? throw new JsonException("Missing deployment artifact.");
        if (artifact.EnvironmentName != environmentName || artifact.DeploymentSource != deploymentSource
            || !string.Equals(artifact.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Deployment artifact does not match independently selected attribution.");
        return artifact;
    }

    /// <summary>Requires this document to match the consuming program's freshly compiled plan.</summary>
    /// <exception cref="ArgumentException">The plan is incomplete or differs from this artifact.</exception>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    public void RequireExactPlan(InfrastructureTargetDeploymentPlan plan)
    {
        var candidate = Create(plan, EnvironmentName, DeploymentSource);
        if (candidate.Fingerprint != Fingerprint) throw new ArgumentException("Deployment artifact does not match the exact compiled plan.", nameof(plan));
    }

    /// <summary>Checks the common exact-plan boundary used by deployment adapters.</summary>
    /// <exception cref="ArgumentException">Fences, witnesses, readiness lowering or diagnostics are invalid.</exception>
    /// <exception cref="ArgumentNullException">The manifest or realization is null.</exception>
    public static void RequireCompleteRealization(InfrastructureTargetDeploymentManifest manifest,
        InfrastructureRealization realization, ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(realization);
        var reference = realization.ToReference();
        if (manifest.Definition != reference.Definition
            || manifest.TargetFacilities.Profile.ToReference() != reference.Profile
            || manifest.TargetFacilities.Profile.Target != reference.Target
            || manifest.TargetFacilities.Variant != reference.Variant)
            throw new ArgumentException("Deployment manifest and realization have different exact semantic fences.");
        if (!realization.IsCapabilityWitnessComplete || !realization.IsReadinessObligationComplete)
            throw new ArgumentException("Deployment realization has incomplete capability or readiness lowering.");
        if (!diagnostics.IsDefaultOrEmpty && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("Deployment artifact cannot retain error diagnostics.");
    }

    /// <summary>Compares the canonical semantic identity, including normalized diagnostics.</summary>
    public bool Equals(InfrastructureDeploymentArtifact? other) => other is not null && Fingerprint == other.Fingerprint;
    /// <summary>Returns a hash of the canonical semantic identity.</summary>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);

    sealed record Identity(string SchemaVersion, string EnvironmentName, SourceReference DeploymentSource,
        InfrastructureTargetDeploymentManifestReference Manifest, InfrastructureRealizationReference Realization,
        ImmutableArray<DocumentValidationDiagnostic> Diagnostics);
}
