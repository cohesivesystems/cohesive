using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Cohesive.Adapters.GitHub;

/// <summary>
/// Options for authenticating to GitHub as an installation-scoped GitHub App.
/// </summary>
/// <param name="AppId">The GitHub App identifier.</param>
/// <param name="KeyVaultUri">The KeyVault used to store the GitHub App private key.</param>
/// <param name="PrivateKeySecretName">The name of the KeyVault secret containing the GitHub App private key.</param>
public sealed record GitHubAppAuthProviderOptions(
    string? AppId,
    string? KeyVaultUri,
    string? PrivateKeySecretName
    );

/// <summary>
/// Resolves GitHub installation tokens for repository owners using a GitHub App private key stored in Azure Key Vault.
/// </summary>
public sealed class GitHubAppAuthProvider(
    HttpClient httpClient,
    TokenCredential credential,
    GitHubAppAuthProviderOptions options
    ) : IGitHubAuthProvider
{
    static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, InstallationToken> tokenCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> installationIdCache = new(StringComparer.OrdinalIgnoreCase);
    string? privateKeyPem;

    /// <summary>Gets access token asynchronously.</summary>
    public async ValueTask<string> GetAccessTokenAsync(string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ct.ThrowIfCancellationRequested();

        if (tokenCache.TryGetValue(owner, out var cached)
            && cached.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(TokenRefreshSkew))
        {
            return cached.Token;
        }

        var appJwt = await CreateAppJwtAsync(ct).ConfigureAwait(false);
        var installationId = await ResolveInstallationIdAsync(owner, appJwt, ct).ConfigureAwait(false);
        var installationToken = await CreateInstallationTokenAsync(installationId, appJwt, ct).ConfigureAwait(false);
        tokenCache[owner] = installationToken;
        return installationToken.Token;
    }

    async ValueTask<string> CreateAppJwtAsync(CancellationToken ct)
    {
        var appId = options.AppId;
        if (string.IsNullOrWhiteSpace(appId))
            throw new InvalidOperationException("GitHub App authentication requires a configured application identifier.");

        var pem = await GetPrivateKeyAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iat = now.AddMinutes(-1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId
        })));

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64UrlEncode(signature)}";
    }

    async ValueTask<string> GetPrivateKeyAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(privateKeyPem))
            return privateKeyPem;

        if (string.IsNullOrWhiteSpace(options.KeyVaultUri))
            throw new InvalidOperationException("GitHub App authentication requires a configured Key Vault URI.");

        if (string.IsNullOrWhiteSpace(options.PrivateKeySecretName))
            throw new InvalidOperationException("GitHub App authentication requires a configured Key Vault secret name.");

        var vaultUri = new Uri(uriString: options.KeyVaultUri, UriKind.Absolute);
        var client = new SecretClient(vaultUri, credential);
        var response = await client.GetSecretAsync(name: options.PrivateKeySecretName, version: null, cancellationToken: ct).ConfigureAwait(false);
        var value = response.Value.Value;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"GitHub App private key secret '{options.PrivateKeySecretName}' was empty.");

        privateKeyPem = value.Replace("\\n", "\n", StringComparison.Ordinal);
        return privateKeyPem;
    }

    async ValueTask<long> ResolveInstallationIdAsync(string owner, string appJwt, CancellationToken ct)
    {
        if (installationIdCache.TryGetValue(owner, out var cached))
            return cached;

        var orgResponse = await SendAsync(HttpMethod.Get, $"orgs/{owner}/installation", appJwt, ct).ConfigureAwait(false);
        if (orgResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            orgResponse.Dispose();
            var userResponse = await SendAsync(HttpMethod.Get, $"users/{owner}/installation", appJwt, ct).ConfigureAwait(false);
            var installationId = await ReadInstallationIdAsync(userResponse, owner, ct).ConfigureAwait(false);
            installationIdCache[owner] = installationId;
            return installationId;
        }

        var resolved = await ReadInstallationIdAsync(orgResponse, owner, ct).ConfigureAwait(false);
        installationIdCache[owner] = resolved;
        return resolved;
    }

    static async ValueTask<long> ReadInstallationIdAsync(HttpResponseMessage response, string owner, CancellationToken ct)
    {
        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, $"Unable to resolve GitHub App installation for '{owner}'.", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var installationId))
            throw new InvalidOperationException($"GitHub installation lookup for '{owner}' did not return an installation identifier.");

        return installationId;
    }

    async ValueTask<InstallationToken> CreateInstallationTokenAsync(long installationId, string appJwt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Authorization = new("Bearer", appJwt);

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateFailureAsync(
                response,
                $"Unable to exchange a GitHub installation token for installation '{installationId}'.",
                ct).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var token = document.RootElement.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var expiresAtText = document.RootElement.TryGetProperty("expires_at", out var expiresAtElement)
            ? expiresAtElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expiresAtText))
        {
            throw new InvalidOperationException(
                $"GitHub installation token response for installation '{installationId}' did not contain the expected fields.");
        }

        return new(token, DateTimeOffset.Parse(expiresAtText, CultureInfo.InvariantCulture));
    }

    async ValueTask<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, string bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new("Bearer", bearerToken);
        return await httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }

    static async ValueTask<InvalidOperationException> CreateFailureAsync(HttpResponseMessage response, string message, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new InvalidOperationException($"{message} GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    readonly record struct InstallationToken(
        string Token,
        DateTimeOffset ExpiresAtUtc
        );
}
