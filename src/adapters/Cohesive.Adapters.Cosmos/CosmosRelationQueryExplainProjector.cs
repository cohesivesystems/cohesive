using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Projects Cosmos compilation results into the target-neutral canonical explain contract.</summary>
public static class CosmosRelationQueryExplainProjector
{
    /// <summary>
    /// Projects and attributes a native Cosmos result to the exact target-neutral request supplied to lowering.
    /// </summary>
    /// <param name="request">Exact native-compilation request that was attempted.</param>
    /// <param name="compilation">Canonical Cosmos native-compilation result to project.</param>
    /// <returns>An attributed native-compilation explain stage.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The projected result or request attribution is inconsistent.</exception>
    public static RelationQueryNativeCompilationExplainStage Project(
        RelationQueryNativeCompilationRequest request,
        CosmosRelationQueryCompilationResult compilation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(compilation);
        return RelationQueryExplainProjector.ProjectNativeCompilation(
            request,
            compilation.Status,
            compilation.Artifacts,
            compilation.Diagnostics,
            static artifact => ProjectArtifact(artifact));
    }

    /// <summary>Projects native Cosmos artifact identities and provenance without retaining SQL or SDK payloads.</summary>
    /// <param name="compilation">Canonical Cosmos native-compilation result to project.</param>
    /// <returns>An adapter-neutral native-compilation explanation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    public static RelationQueryNativeCompilationExplanation Project(
        CosmosRelationQueryCompilationResult compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return RelationQueryExplainProjector.ProjectNativeCompilation(
            compilation.Status,
            compilation.Artifacts,
            compilation.Diagnostics,
            static artifact => ProjectArtifact(artifact));
    }

    static RelationQueryNativeArtifactReference ProjectArtifact(CosmosRelationQueryCompiledArtifact artifact) =>
        new(
            artifact.Branch.Id,
            artifactSchemaVersion: null,
            new(
                artifact.Fingerprint.Algorithm,
                artifact.Fingerprint.Canonicalization,
                artifact.Fingerprint.Value),
            artifact.Provenance);
}
