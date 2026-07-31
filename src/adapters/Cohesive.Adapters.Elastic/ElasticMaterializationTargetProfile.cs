using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Projects exact Elasticsearch target bindings and operating policy into Storage capability evidence.</summary>
public static class ElasticMaterializationTargetProfile
{
    const string ProfilePrefix = "cohesive.adapters.elastic.materialization-target/v1/";

    /// <summary>Creates the exact capability profile for one bound Elasticsearch target runtime.</summary>
    /// <param name="binding">Persisted physical target binding and query/template evidence.</param>
    /// <param name="policy">Explicit deployment operating bounds.</param>
    /// <param name="runtimeBinding">Exact borrowed-client runtime attestation.</param>
    /// <returns>
    /// Target-role capability evidence whose profile identity changes with binding, policy, cluster, authority, or
    /// client-version evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/>, <paramref name="policy"/>, or <paramref name="runtimeBinding"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The persisted target and runtime attest different Elasticsearch cluster identities.
    /// </exception>
    public static MaterializationCapabilityProfile Create(
        ElasticMaterializationTargetBinding binding,
        ElasticMaterializationTargetPolicy policy,
        ElasticElasticsearchRuntimeBinding runtimeBinding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        RequireCompatibleCluster(binding, runtimeBinding);

        var profileId = GetProfileId(binding, policy, runtimeBinding);
        ImmutableArray<string> sources =
        [
            $"elastic-target-binding:{binding.Fingerprint.Value}",
            $"elastic-index-template:{binding.IndexTemplate.Fingerprint.Value}",
            $"elastic-single-writer-authority:{binding.SingleWriter.Authority}",
            $"elastic-single-writer-scope:{binding.SingleWriter.Scope}",
            $"elastic-indexed-identity-characters:{ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters}",
            $"elastic-runtime-attestation:{runtimeBinding.Fingerprint.Value}",
            $"elastic-client-version:{runtimeBinding.ClientVersion}"
        ];
        ImmutableArray<MaterializationOperatingLimit> writeLimits =
        [
            new(MaterializationLimitKind.WriteItems, policy.MaximumBatchItems),
            new(MaterializationLimitKind.WriteBytes, policy.MaximumBatchBytes),
            new(MaterializationLimitKind.Parallelism, policy.MaximumParallelism),
            new(
                MaterializationLimitKind.IndexedIdentityCharacters,
                ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters)
        ];
        ImmutableArray<MaterializationOperatingLimit> parallelism =
        [new(MaterializationLimitKind.Parallelism, policy.MaximumParallelism)];
        ImmutableArray<MaterializationOperatingLimit> generationLimits =
        [
            new(MaterializationLimitKind.Parallelism, policy.MaximumParallelism),
            new(
                MaterializationLimitKind.IndexedIdentityCharacters,
                ElasticMaterializationTargetBinding.MaximumIndexedIdentityCharacters)
        ];

        return new(
            profileId,
            MaterializationEndpointRole.Target,
            binding.TargetId.Value,
            [
                Evidence(
                    MaterializationCapabilityKind.TargetGenerationIsolation,
                    [
                        MaterializationGuaranteeKind.GenerationIsolation,
                        MaterializationGuaranteeKind.FencedMutation
                    ],
                    generationLimits,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "A caller-identified physical index remains outside the stable read alias until promotion."),
                Evidence(
                    MaterializationCapabilityKind.TargetBulkUpsert,
                    [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                    writeLimits,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Bounded Elasticsearch bulk writes retain canonical mutation and version evidence under the persisted external single-writer authority."),
                Evidence(
                    MaterializationCapabilityKind.TargetBulkDelete,
                    [
                        MaterializationGuaranteeKind.FencedMutation,
                        MaterializationGuaranteeKind.IdempotentWrite,
                        MaterializationGuaranteeKind.VersionConditionalWrite
                    ],
                    writeLimits,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Deletes are retained as versioned tombstones under the persisted external single-writer authority until generation cleanup."),
                Evidence(
                    MaterializationCapabilityKind.TargetPerItemOutcomes,
                    [MaterializationGuaranteeKind.ExactPerItemOutcome],
                    writeLimits,
                    sources,
                    MaterializationCapabilityRealizationKind.Composed,
                    "Adapter preflight and bulk item responses produce one request-order terminal outcome per input."),
                Evidence(
                    MaterializationCapabilityKind.TargetSeal,
                    [MaterializationGuaranteeKind.FencedMutation],
                    parallelism,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Sealing is serialized with generation writes and records attributable immutable evidence."),
                Evidence(
                    MaterializationCapabilityKind.TargetValidation,
                    [MaterializationGuaranteeKind.FencedMutation],
                    parallelism,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Validation checks the exact sealed generation and carries the binding-declared index-template fingerprint as provenance; live template drift requires deployment validation."),
                Evidence(
                    MaterializationCapabilityKind.TargetFencedPromotion,
                    [
                        MaterializationGuaranteeKind.AtomicPromotion,
                        MaterializationGuaranteeKind.FencedPromotion
                    ],
                    parallelism,
                    sources,
                    MaterializationCapabilityRealizationKind.Composed,
                    "An atomic Elasticsearch alias-marker compare-and-swap fences and applies the stable read-alias promotion."),
                Evidence(
                    MaterializationCapabilityKind.TargetRetirement,
                    [MaterializationGuaranteeKind.FencedMutation],
                    parallelism,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Retirement is a fenced logical lifecycle transition separate from physical cleanup."),
                Evidence(
                    MaterializationCapabilityKind.TargetCleanup,
                    [MaterializationGuaranteeKind.FencedMutation],
                    parallelism,
                    sources,
                    MaterializationCapabilityRealizationKind.Constrained,
                    "Cleanup removes only a fenced retired generation while retaining its identity tombstone.")
            ],
            "Generation-per-index Elasticsearch target with bounded bulk mutation and fenced stable-alias promotion.");
    }

    /// <summary>Creates a canonical target descriptor from the same evidence used by runtime construction.</summary>
    /// <param name="binding">Persisted physical target binding.</param>
    /// <param name="policy">Explicit deployment operating bounds.</param>
    /// <param name="runtimeBinding">Exact borrowed-client runtime attestation.</param>
    /// <returns>A descriptor whose identity and materialization match <paramref name="binding"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/>, <paramref name="policy"/>, or <paramref name="runtimeBinding"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The binding and runtime cluster identities differ.</exception>
    public static MaterializationTargetDescriptor CreateDescriptor(
        ElasticMaterializationTargetBinding binding,
        ElasticMaterializationTargetPolicy policy,
        ElasticElasticsearchRuntimeBinding runtimeBinding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new(
            binding.TargetId,
            binding.MaterializationId,
            Create(binding, policy, runtimeBinding));
    }

    /// <summary>Computes the deterministic versioned profile identity for exact binding, policy, and runtime evidence.</summary>
    /// <param name="binding">Persisted physical target binding.</param>
    /// <param name="policy">Explicit deployment operating bounds.</param>
    /// <param name="runtimeBinding">Exact borrowed-client runtime attestation.</param>
    /// <returns>A stable content-addressed materialization capability profile identity.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/>, <paramref name="policy"/>, or <paramref name="runtimeBinding"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The binding and runtime cluster identities differ.</exception>
    public static MaterializationCapabilityProfileId GetProfileId(
        ElasticMaterializationTargetBinding binding,
        ElasticMaterializationTargetPolicy policy,
        ElasticElasticsearchRuntimeBinding runtimeBinding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(runtimeBinding);
        RequireCompatibleCluster(binding, runtimeBinding);
        StringBuilder canonical = new(768);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, ProfilePrefix);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, binding.Fingerprint.Value);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, binding.SingleWriter.Authority);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, binding.SingleWriter.Scope);
        policy.AppendCanonical(canonical);
        ElasticMaterializationTargetBindingFingerprinter.Append(canonical, runtimeBinding.Fingerprint.Value);
        return new(
            ProfilePrefix
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    static void RequireCompatibleCluster(
        ElasticMaterializationTargetBinding binding,
        ElasticElasticsearchRuntimeBinding runtimeBinding)
    {
        if (binding.Cluster != runtimeBinding.Cluster)
        {
            throw new ArgumentException(
                "The Elasticsearch target binding and runtime attestation must address the same exact cluster identity.",
                nameof(runtimeBinding));
        }
    }

    static MaterializationCapabilityEvidence Evidence(
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationGuaranteeKind> guarantees,
        ImmutableArray<MaterializationOperatingLimit> limits,
        ImmutableArray<string> sources,
        MaterializationCapabilityRealizationKind realization,
        string description) => new(
        new($"elastic/materialization-target/{(int)capability}"),
        capability,
        realization,
        guarantees,
        limits,
        sources,
        description);
}
