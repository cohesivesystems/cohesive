namespace Cohesive.Adapters.GitHub;

/// <summary>Defines the contract for git hub auth provider.</summary>
public interface IGitHubAuthProvider
{
    /// <summary>Gets access token asynchronously.</summary>
    ValueTask<string> GetAccessTokenAsync(string owner, CancellationToken ct = default);
}
