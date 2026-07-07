using System.Collections.Immutable;

namespace Cohesive.Identity;

/// <summary>
/// Configuration for a bootstrap principal that can be materialized into an in-memory identity directory.
/// </summary>
public class IdentityBootstrapPrincipalOptions
{
    /// <summary>
    /// Gets or sets the stable principal id. When omitted, the id is derived from email, subject, or client id.
    /// </summary>
    public string? PrincipalId { get; init; }

    /// <summary>
    /// Gets or sets the email address used to match identity-provider claims.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Gets or sets the identity-provider subject used to match identity-provider claims.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// Gets or sets the OAuth client id used to match machine-to-machine identity claims.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Gets or sets the human-facing display name for the principal.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Helper methods for materializing bootstrap identity records.
/// </summary>
public static class IdentityBootstrap
{
    /// <summary>Default principal-id prefix used for user principals derived from bootstrap identity hints.</summary>
    public const string UserPrincipalIdPrefix = "user";

    /// <summary>
    /// Creates a principal account record from bootstrap principal options.
    /// </summary>
    /// <param name="principal">Bootstrap principal configuration.</param>
    /// <param name="kind">Principal kind to assign to the generated account record.</param>
    /// <param name="derivedPrincipalIdPrefix">Prefix used when deriving an id from email, subject, or client id.</param>
    /// <returns>A normalized in-memory principal record.</returns>
    public static PrincipalAccountRecord CreatePrincipal(
        IdentityBootstrapPrincipalOptions principal,
        PrincipalKind kind = PrincipalKind.User,
        string derivedPrincipalIdPrefix = UserPrincipalIdPrefix
        )
    {
        ArgumentNullException.ThrowIfNull(principal);
        var principalId = ResolvePrincipalId(principal, derivedPrincipalIdPrefix);
        return new(
            Id: principalId,
            Kind: kind,
            DisplayName: NormalizeOptional(principal.DisplayName)
                ?? NormalizeEmail(principal.Email)
                ?? NormalizeOptional(principal.Subject)
                ?? NormalizeOptional(principal.ClientId),
            Email: NormalizeEmail(principal.Email),
            Subject: NormalizeOptional(principal.Subject),
            ClientId: NormalizeOptional(principal.ClientId)
            );
    }

    /// <summary>
    /// Resolves the stable principal id for bootstrap principal options.
    /// </summary>
    /// <param name="principal">Bootstrap principal configuration.</param>
    /// <param name="derivedPrincipalIdPrefix">Prefix used when deriving an id from email, subject, or client id.</param>
    /// <returns>The configured or derived principal id.</returns>
    public static string ResolvePrincipalId(
        IdentityBootstrapPrincipalOptions principal,
        string derivedPrincipalIdPrefix = UserPrincipalIdPrefix
        )
    {
        ArgumentNullException.ThrowIfNull(principal);
        var principalId = NormalizeOptional(principal.PrincipalId);
        if (principalId is not null)
            return principalId;

        var email = NormalizeEmail(principal.Email);
        if (email is not null)
            return $"{derivedPrincipalIdPrefix}:{email}";

        var subject = NormalizeOptional(principal.Subject);
        if (subject is not null)
            return $"{derivedPrincipalIdPrefix}:{subject}";

        var clientId = NormalizeOptional(principal.ClientId);
        if (clientId is not null)
            return $"{derivedPrincipalIdPrefix}:{clientId}";

        throw new InvalidOperationException("Bootstrap principal configuration must specify PrincipalId, Email, Subject, or ClientId.");
    }

    /// <summary>
    /// Resolves configured scope ids, falling back to defaults when no configured ids are supplied.
    /// </summary>
    /// <param name="configuredScopeIds">Scope ids supplied by configuration.</param>
    /// <param name="defaultScopeIds">Default scope ids used when configuration omits scope ids.</param>
    /// <param name="scopeName">Human-facing scope name used in validation messages.</param>
    /// <returns>Normalized distinct scope ids.</returns>
    public static ImmutableArray<string> ResolveScopeIds(
        IEnumerable<string>? configuredScopeIds,
        IEnumerable<string> defaultScopeIds,
        string scopeName = "scope"
        )
    {
        ArgumentNullException.ThrowIfNull(defaultScopeIds);
        var source = configuredScopeIds is not null && configuredScopeIds.Any()
            ? configuredScopeIds
            : defaultScopeIds;
        var scopeIds = source
            .Select(NormalizeOptional)
            .Where(static scopeId => scopeId is not null)
            .Select(static scopeId => scopeId!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        if (scopeIds.Length == 0)
            throw new InvalidOperationException($"Bootstrap principal configuration must include at least one {scopeName}.");

        return scopeIds;
    }

    /// <summary>
    /// Resolves the default scope id for a set of granted scopes.
    /// </summary>
    /// <param name="configuredDefaultScopeId">Configured default scope id.</param>
    /// <param name="scopeIds">Granted scope ids.</param>
    /// <param name="preferredDefaultScopeId">Preferred default scope id used when present and no configured default is supplied.</param>
    /// <param name="scopeName">Human-facing scope name used in validation messages.</param>
    /// <returns>The resolved default scope id.</returns>
    public static string ResolveDefaultScopeId(
        string? configuredDefaultScopeId,
        ImmutableArray<string> scopeIds,
        string? preferredDefaultScopeId = null,
        string scopeName = "scope"
        )
    {
        if (scopeIds.Length == 0)
            throw new InvalidOperationException($"Bootstrap principal configuration must include at least one {scopeName}.");

        var defaultScopeId = NormalizeOptional(configuredDefaultScopeId);
        if (defaultScopeId is not null)
        {
            if (!scopeIds.Any(scopeId => string.Equals(scopeId, defaultScopeId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Bootstrap default {scopeName} '{defaultScopeId}' must be included in the bootstrap principal {scopeName} list.");

            return defaultScopeId;
        }

        var preferred = NormalizeOptional(preferredDefaultScopeId);
        if (preferred is not null
            && scopeIds.Any(scopeId => string.Equals(scopeId, preferred, StringComparison.Ordinal)))
        {
            return preferred;
        }

        return scopeIds[0];
    }

    /// <summary>
    /// Normalizes optional configuration text by trimming whitespace and converting blank strings to <see langword="null"/>.
    /// </summary>
    /// <param name="value">Configuration value to normalize.</param>
    /// <returns>The normalized value, or <see langword="null"/> when the value is blank.</returns>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Normalizes an email address for identity matching and derived principal ids.
    /// </summary>
    /// <param name="value">Email value to normalize.</param>
    /// <returns>The trimmed lower-case email, or <see langword="null"/> when the value is blank.</returns>
    public static string? NormalizeEmail(string? value)
    {
        var email = NormalizeOptional(value);
        return email?.ToLowerInvariant();
    }
}
