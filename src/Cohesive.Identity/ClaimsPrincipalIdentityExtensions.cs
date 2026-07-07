using System.Security.Claims;

namespace Cohesive.Identity;

/// <summary>
/// Common claims-principal helpers used by identity resolvers.
/// </summary>
public static class ClaimsPrincipalIdentityExtensions
{
    /// <summary>Standard OAuth scope claim type.</summary>
    public const string ScopeClaimType = "scp";

    /// <summary>Mapped Microsoft identity scope claim type.</summary>
    public const string MappedScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>Standard Microsoft Entra roles claim type.</summary>
    public const string RoleClaimType = "roles";

    static readonly string[] EmailClaimTypes =
    [
        "email",
        "emails",
        "preferred_username",
        "unique_name",
        "upn",
        ClaimTypes.Email,
        ClaimTypes.Upn
    ];

    static readonly string[] ClientIdClaimTypes = ["azp", "appid", "client_id"];

    extension(ClaimsPrincipal principal)
    {
        /// <summary>
        /// Returns whether the principal has the expected OAuth scope.
        /// </summary>
        public bool HasScope(string expectedScope) =>
            principal.GetScopes().Any(scope => string.Equals(scope, expectedScope, StringComparison.Ordinal));

        /// <summary>
        /// Returns whether the principal has at least one of the expected roles.
        /// </summary>
        public bool HasAnyRole(params string[] roles)
        {
            var tokenRoles = principal.GetRoles();
            return roles.Any(expectedRole => tokenRoles.Contains(expectedRole, StringComparer.Ordinal));
        }

        /// <summary>
        /// Reads normalized OAuth scopes from known scope claim types.
        /// </summary>
        public string[] GetScopes() =>
            GetClaimValues(principal, ScopeClaimType, MappedScopeClaimType);

        /// <summary>
        /// Reads normalized roles from known role claim types.
        /// </summary>
        public string[] GetRoles() =>
            GetClaimValues(principal, RoleClaimType, ClaimTypes.Role);

        /// <summary>
        /// Reads the first subject-like claim from the principal.
        /// </summary>
        public string? GetSubject() =>
            principal.FindFirst("sub")?.Value ??
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        /// <summary>
        /// Reads the first subject-like claim from the principal.
        /// </summary>
        public string? GetIdentitySubject() =>
            principal.GetSubject();

        /// <summary>
        /// Reads the first email-like claim from the principal.
        /// </summary>
        public string? GetEmail() =>
            EmailClaimTypes
                .Select(claimType => principal.FindFirst(claimType)?.Value)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        /// <summary>
        /// Reads the first email-like claim from the principal.
        /// </summary>
        public string? GetIdentityEmail() =>
            principal.GetEmail();

        /// <summary>
        /// Reads the first OAuth client-id-like claim from the principal.
        /// </summary>
        public string? GetClientId() =>
            ClientIdClaimTypes
                .Select(claimType => principal.FindFirst(claimType)?.Value)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        /// <summary>
        /// Reads the first OAuth client-id-like claim from the principal.
        /// </summary>
        public string? GetIdentityClientId() =>
            principal.GetClientId();

        /// <summary>
        /// Reads the display name from the principal.
        /// </summary>
        public string? GetDisplayName() =>
            principal.Identity?.Name ??
            principal.FindFirst("name")?.Value ??
            principal.GetEmail();

        /// <summary>
        /// Reads the display name from the principal.
        /// </summary>
        public string? GetIdentityDisplayName() =>
            principal.GetDisplayName();
    }

    static string[] GetClaimValues(ClaimsPrincipal principal, params string[] claimTypes) =>
    [
        ..claimTypes
            .SelectMany(principal.FindAll)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
    ];
}
