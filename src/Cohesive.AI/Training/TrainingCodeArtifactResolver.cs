namespace Cohesive.AI.Training;

/// <summary>
/// Resolves, packages, and caches training code artifacts.
/// </summary>
public sealed class TrainingCodeArtifactResolver(
    ICodeRepository repository,
    ICodePackager packager,
    ICodeArtifactCache cache
    )
{
    public async ValueTask<TrainingCodeArtifact> ResolveAsync(CodeReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var revision = await repository.ResolveRevisionAsync(reference, ct).ConfigureAwait(false);
        var cached = await cache.GetAsync(revision, ct).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        await using var archive = await repository.OpenArchiveAsync(revision, ct).ConfigureAwait(false);
        var artifact = await packager.PackageAsync(revision, archive, ct).ConfigureAwait(false);
        await cache.SetAsync(revision, artifact, ct).ConfigureAwait(false);
        return artifact;
    }
}
