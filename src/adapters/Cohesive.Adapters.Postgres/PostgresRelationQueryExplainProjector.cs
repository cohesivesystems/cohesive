using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;

namespace Cohesive.Adapters.Postgres;

/// <summary>Projects PostgreSQL compilation results into the target-neutral canonical explain contract.</summary>
public static class PostgresRelationQueryExplainProjector
{
    /// <summary>
    /// Projects and attributes a native PostgreSQL result to the exact target-neutral request supplied to lowering.
    /// </summary>
    /// <param name="request">Exact native-compilation request that was attempted.</param>
    /// <param name="compilation">Canonical PostgreSQL native-compilation result to project.</param>
    /// <returns>An attributed native-compilation explain stage.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The projected result or request attribution is inconsistent.</exception>
    public static RelationQueryNativeCompilationExplainStage Project(
        RelationQueryNativeCompilationRequest request,
        PostgresRelationQueryCompilationResult compilation)
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

    /// <summary>Projects native PostgreSQL artifact identities and provenance without retaining SQL payloads.</summary>
    /// <param name="compilation">Canonical PostgreSQL native-compilation result to project.</param>
    /// <returns>An adapter-neutral native-compilation explanation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    public static RelationQueryNativeCompilationExplanation Project(
        PostgresRelationQueryCompilationResult compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return RelationQueryExplainProjector.ProjectNativeCompilation(
            compilation.Status,
            compilation.Artifacts,
            compilation.Diagnostics,
            static artifact => ProjectArtifact(artifact));
    }

    static RelationQueryNativeArtifactReference ProjectArtifact(PostgresRelationQueryCompiledArtifact artifact) =>
        new(
            artifact.Branch.Id,
            artifact.SchemaVersion,
            new(
                artifact.Fingerprint.Algorithm,
                artifact.Fingerprint.Canonicalization,
                artifact.Fingerprint.Value),
            artifact.Provenance);
}
