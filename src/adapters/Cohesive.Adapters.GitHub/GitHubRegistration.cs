using Azure.Core;
using Azure.Identity;
using Cohesive.AI.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.GitHub;

/// <summary>Defines git hub code repository settings.</summary>
public sealed record GitHubCodeRepositorySettings
{
    /// <summary>Gets the app id.</summary>
    public string? AppId { get; init; }

    /// <summary>Gets the key vault uri.</summary>
    public string? KeyVaultUri { get; init; }

    /// <summary>Gets the private key secret name.</summary>
    public string? PrivateKeySecretName { get; init; }

    /// <summary>Gets the api base uri.</summary>
    public string? ApiBaseUri { get; init; }

    /// <summary>Creates token credential.</summary>
    public TokenCredential CreateTokenCredential() => new DefaultAzureCredential();
}

/// <summary>Provides operations for git hub registration.</summary>
public static class GitHubRegistration
{
    const string GitHubHttpClientName = "GitHubApi";
    
    /// <summary>
    /// Registers a <see cref="GitHubCodeRepository"/> implementation of <see cref="ICodeRepository"/>.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="settings"></param>
    public static void RegisterGitHubCodeRepository(this IServiceCollection services, GitHubCodeRepositorySettings settings)
    {
        services.AddHttpClient(GitHubHttpClientName, (sp, client) =>
        {
            client.BaseAddress = new(settings.ApiBaseUri ?? "https://api.github.com/");
            client.DefaultRequestHeaders.Accept.Add(new(mediaType: "application/vnd.github+json"));
            client.DefaultRequestHeaders.UserAgent.Add(new(productName: "Cohesive", productVersion: "1.0"));
        });
        services.AddSingleton<IGitHubAuthProvider>(sp => new GitHubAppAuthProvider(
            httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubHttpClientName),
            credential: settings.CreateTokenCredential(),
            options: new(
                AppId: settings.AppId,
                KeyVaultUri: settings.KeyVaultUri,
                PrivateKeySecretName: settings.PrivateKeySecretName
            )
        ));
        services.AddSingleton(sp => new GitHubCodeRepository(
            httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubHttpClientName),
            authProvider: sp.GetRequiredService<IGitHubAuthProvider>()
        ));
    }
}
