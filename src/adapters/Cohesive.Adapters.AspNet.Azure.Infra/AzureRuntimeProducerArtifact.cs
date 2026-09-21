using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.AspNet.Azure.Infra;

/// <summary>Portable producer inputs projected from a reviewed deployment, without credentials or product health logic.</summary>
/// <param name="SchemaVersion">Exact supported artifact version.</param>
/// <param name="Realization">Canonical infrastructure realization retained by the deployment.</param>
/// <param name="NativeBindings">Native output associations for that realization.</param>
/// <param name="RuntimeBindings">Reviewed producer declarations and deployment identities.</param>
public sealed record AzureRuntimeProducerArtifact(string SchemaVersion, InfrastructureRealization Realization,
    AzureInfrastructureReadinessBindings NativeBindings, AzureInfrastructureRuntimeBindings RuntimeBindings)
{
    /// <summary>Supported producer artifact schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.azure-runtime-producer/1";
    static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Reads an operator-supplied artifact with a four-MiB limit and strict JSON parsing; does not establish trust.</summary>
    /// <param name="path">Explicit artifact path. The host owns delivery, permissions and provenance.</param>
    /// <returns>Parsed inputs requiring independent expected scope and actual host attribution at producer construction.</returns>
    /// <exception cref="IOException">File access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">File access is denied.</exception>
    /// <exception cref="ArgumentException">Size, version or duplicate fields are invalid.</exception>
    /// <exception cref="JsonException">JSON or a canonical constructor rejects the document.</exception>
    public static AzureRuntimeProducerArtifact Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new ArgumentException("Producer artifact exceeds the input limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes);
        if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, "", out _)) throw new ArgumentException("Duplicate producer artifact fields.");
        var artifact = document.Deserialize<AzureRuntimeProducerArtifact>(JsonOptions);
        if (artifact?.SchemaVersion != CurrentSchemaVersion || artifact.Realization is null || artifact.NativeBindings is null || artifact.RuntimeBindings is null)
            throw new ArgumentException("Unsupported or incomplete producer artifact.");
        return artifact;
    }

    /// <summary>Creates a producer after comparing independently supplied scope and actual host identities with retained declarations.</summary>
    /// <param name="environment">Independently configured environment.</param>
    /// <param name="tenant">Independently configured tenant.</param>
    /// <param name="subscription">Independently configured subscription.</param>
    /// <param name="handoff">Independently reviewed handoff fingerprint reference.</param>
    /// <param name="producer">Application-code producer identity.</param>
    /// <param name="checkContract">Application-code versioned admission contract.</param>
    /// <param name="deployment">Actual host deployment identity, not taken from this artifact.</param>
    /// <param name="checkName">Exact registered application health check.</param>
    /// <returns>A validated producer without executing health checks.</returns>
    /// <exception cref="ArgumentException">Artifact, scope or host attribution mismatches.</exception>
    public AzureRuntimeHealthCheckProducer CreateProducer(string environment, Guid tenant, Guid subscription, SourceReference handoff,
        SourceReference producer, SourceReference checkContract, SourceReference deployment, string checkName)
    {
        if (Realization is null || NativeBindings is null || RuntimeBindings is null) throw new ArgumentException("Incomplete producer artifact.");
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentException("Unsupported producer artifact version.");
        return new(Realization, NativeBindings, RuntimeBindings,
            new(environment, tenant, subscription, Realization.ToReference(), handoff), producer, checkContract, deployment, checkName);
    }
}
