using System.Collections.Concurrent;

namespace Cohesive.AI.Training;

/// <summary>
/// In-memory implementation of <see cref="ICodeArtifactCache"/>.
/// </summary>
public sealed class InMemoryCodeArtifactCache : ICodeArtifactCache
{
    readonly ConcurrentDictionary<CodeRevision, TrainingCodeArtifact> artifacts = new();

    /// <summary>Gets the value asynchronously.</summary>
    public ValueTask<TrainingCodeArtifact?> GetAsync(CodeRevision revision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(artifacts.GetValueOrDefault(revision));
    }

    /// <summary>Sets the value asynchronously.</summary>
    public ValueTask SetAsync(CodeRevision revision, TrainingCodeArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ct.ThrowIfCancellationRequested();

        artifacts[revision] = artifact;
        return ValueTask.CompletedTask;
    }
}
