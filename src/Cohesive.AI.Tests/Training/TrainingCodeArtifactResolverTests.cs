using Cohesive.AI.Training;

namespace Cohesive.AI.Tests.Training;

public sealed class TrainingCodeArtifactResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsCachedArtifactWithoutRepackaging()
    {
        var revision = new CodeRevision("owner/repo", "abc123", "train");
        var cachedArtifact = new TrainingCodeArtifact("https://storage.example.net/code.zip", "sha256:cafebabe");
        var repository = new StubCodeRepository(revision);
        var packager = new StubCodePackager();
        var cache = new InMemoryCodeArtifactCache();
        await cache.SetAsync(revision, cachedArtifact);
        var resolver = new TrainingCodeArtifactResolver(repository, packager, cache);

        var artifact = await resolver.ResolveAsync(new("owner/repo", "main", "train"));

        Assert.Equal(cachedArtifact, artifact);
        Assert.Equal(1, repository.ResolveCalls);
        Assert.Equal(0, repository.OpenArchiveCalls);
        Assert.Equal(0, packager.PackageCalls);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesPackagesAndCachesArtifact()
    {
        var revision = new CodeRevision("owner/repo", "deadbeef");
        var expectedArtifact = new TrainingCodeArtifact("https://storage.example.net/code.zip", "sha256:deadbeef");
        var repository = new StubCodeRepository(revision);
        var packager = new StubCodePackager(expectedArtifact);
        var cache = new InMemoryCodeArtifactCache();
        var resolver = new TrainingCodeArtifactResolver(repository, packager, cache);

        var artifact = await resolver.ResolveAsync(new("owner/repo", "main"));

        Assert.Equal(expectedArtifact, artifact);
        Assert.Equal(1, repository.ResolveCalls);
        Assert.Equal(1, repository.OpenArchiveCalls);
        Assert.Equal(1, packager.PackageCalls);
        Assert.Equal(expectedArtifact, await cache.GetAsync(revision));
    }

    sealed class StubCodeRepository(CodeRevision resolvedRevision) : ICodeRepository
    {
        public int ResolveCalls { get; private set; }

        public int OpenArchiveCalls { get; private set; }

        public ValueTask<CodeRevision> ResolveRevisionAsync(CodeReference reference, CancellationToken ct = default)
        {
            ResolveCalls++;
            return ValueTask.FromResult(resolvedRevision);
        }

        public ValueTask<CodeArchive> OpenArchiveAsync(CodeRevision revision, CancellationToken ct = default)
        {
            OpenArchiveCalls++;
            return ValueTask.FromResult(new CodeArchive(new MemoryStream([1, 2, 3]), "repo.zip"));
        }
    }

    sealed class StubCodePackager(TrainingCodeArtifact? artifact = null) : ICodePackager
    {
        readonly TrainingCodeArtifact artifact = artifact ?? new("https://storage.example.net/default.zip", "sha256:1234");

        public int PackageCalls { get; private set; }

        public ValueTask<TrainingCodeArtifact> PackageAsync(CodeRevision revision, CodeArchive archive, CancellationToken ct = default)
        {
            PackageCalls++;
            return ValueTask.FromResult(artifact);
        }
    }
}
