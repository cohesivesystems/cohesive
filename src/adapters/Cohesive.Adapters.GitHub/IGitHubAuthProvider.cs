namespace Cohesive.Adapters.GitHub;

public interface IGitHubAuthProvider
{
    ValueTask<string> GetAccessTokenAsync(string owner, CancellationToken ct = default);
}