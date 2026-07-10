namespace Cohesive.Api;

/// <summary>
/// Declares how an API operation is scoped to a semantic identity boundary such as a tenant.
/// </summary>
public sealed record ApiScopePolicy
{
    /// <summary>
    /// Creates an API scope policy.
    /// </summary>
    /// <param name="scopeKind">Semantic scope kind, such as <c>sample.tenant</c>.</param>
    /// <param name="cardinality">Number of scopes the operation can evaluate.</param>
    /// <param name="binding">Where the requested scope is bound in a concrete transport projection.</param>
    /// <param name="access">How the requested or resource-derived scope is checked against caller grants.</param>
    /// <param name="singleScopeParameterName">Transport parameter that carries one scope id.</param>
    /// <param name="multipleScopesParameterName">Transport parameter that carries a set of scope ids.</param>
    /// <param name="scopeModeParameterName">Transport parameter that selects how scope ids are interpreted.</param>
    /// <param name="resourceParameterName">Transport parameter for the resource id that carries or implies scope.</param>
    /// <param name="resourceDerivation">Structured metadata used to derive scope from a resource-bound identifier.</param>
    /// <param name="allowDefaultScope">Whether callers may omit an explicit scope and use the identity default scope.</param>
    public ApiScopePolicy(
        string scopeKind,
        ApiScopeCardinality cardinality,
        ApiScopeBinding binding,
        ApiScopeAccess access = ApiScopeAccess.RequireSelected,
        string? singleScopeParameterName = null,
        string? multipleScopesParameterName = null,
        string? scopeModeParameterName = null,
        string? resourceParameterName = null,
        ApiResourceScopeDerivation? resourceDerivation = null,
        bool allowDefaultScope = true
        )
    {
        var normalizedResourceParameterName = Normalize(resourceParameterName);
        ValidateResourceBinding(
            binding: binding,
            resourceParameterName: normalizedResourceParameterName,
            resourceDerivation: resourceDerivation
            );

        ScopeKind = Guard.RequireNotNullOrWhiteSpace(scopeKind);
        Cardinality = cardinality;
        Binding = binding;
        Access = access;
        SingleScopeParameterName = Normalize(singleScopeParameterName);
        MultipleScopesParameterName = Normalize(multipleScopesParameterName);
        ScopeModeParameterName = Normalize(scopeModeParameterName);
        ResourceParameterName = normalizedResourceParameterName;
        ResourceDerivation = resourceDerivation;
        AllowDefaultScope = allowDefaultScope;
    }

    /// <summary>
    /// Semantic scope kind, such as <c>sample.tenant</c>.
    /// </summary>
    public string ScopeKind { get; }

    /// <summary>
    /// Number of scopes the operation can evaluate.
    /// </summary>
    public ApiScopeCardinality Cardinality { get; }

    /// <summary>
    /// Where the requested scope is bound in a concrete transport projection.
    /// </summary>
    public ApiScopeBinding Binding { get; }

    /// <summary>
    /// How the requested or resource-derived scope is checked against caller grants.
    /// </summary>
    public ApiScopeAccess Access { get; }

    /// <summary>
    /// Transport parameter that carries one scope id.
    /// </summary>
    public string? SingleScopeParameterName { get; }

    /// <summary>
    /// Transport parameter that carries a set of scope ids.
    /// </summary>
    public string? MultipleScopesParameterName { get; }

    /// <summary>
    /// Transport parameter that selects how scope ids are interpreted.
    /// </summary>
    public string? ScopeModeParameterName { get; }

    /// <summary>
    /// Transport parameter for the resource id that carries or implies scope.
    /// </summary>
    public string? ResourceParameterName { get; }

    /// <summary>
    /// Structured metadata used to derive scope from a resource identifier.
    /// </summary>
    public ApiResourceScopeDerivation? ResourceDerivation { get; }

    /// <summary>
    /// Whether callers may omit an explicit scope and use the identity default scope.
    /// </summary>
    public bool AllowDefaultScope { get; }

    static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static void ValidateResourceBinding(
        ApiScopeBinding binding,
        string? resourceParameterName,
        ApiResourceScopeDerivation? resourceDerivation
        )
    {
        if (binding == ApiScopeBinding.Resource)
        {
            if (resourceParameterName is null)
                throw new ArgumentException("Resource-bound scope policies require a resource parameter name.", nameof(resourceParameterName));

            return;
        }

        if (resourceParameterName is not null)
            throw new ArgumentException("Only resource-bound scope policies may declare a resource parameter name.", nameof(resourceParameterName));

        if (resourceDerivation is not null)
            throw new ArgumentException("Only resource-bound scope policies may declare a resource derivation.", nameof(resourceDerivation));
    }
}

/// <summary>
/// Declares how a scope is derived from a resource-bound identifier.
/// </summary>
public sealed record ApiResourceScopeDerivation
{
    /// <summary>
    /// Creates an API resource scope derivation declaration.
    /// </summary>
    /// <param name="strategy">General derivation strategy understood by adapters.</param>
    /// <param name="format">Identifier format interpreted by the strategy.</param>
    /// <param name="scopeField">Structured field within the identifier that carries the scope id.</param>
    public ApiResourceScopeDerivation(
        string strategy,
        string? format = null,
        string? scopeField = null
        )
    {
        Strategy = Guard.RequireNotNullOrWhiteSpace(strategy).Trim();
        Format = Normalize(format);
        ScopeField = Normalize(scopeField);
    }

    /// <summary>
    /// General derivation strategy understood by adapters.
    /// </summary>
    public string Strategy { get; }

    /// <summary>
    /// Identifier format interpreted by the strategy.
    /// </summary>
    public string? Format { get; }

    /// <summary>
    /// Structured field within the identifier that carries the scope id.
    /// </summary>
    public string? ScopeField { get; }

    static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Well-known resource scope derivation strategies.
/// </summary>
public static class ApiResourceScopeDerivationStrategies
{
    /// <summary>
    /// Derives scope by parsing a structured resource identifier and reading one named field.
    /// </summary>
    public const string StructuredResourceId = "structuredResourceId";
}

/// <summary>
/// Well-known structured resource identifier formats.
/// </summary>
public static class ApiResourceIdFormats
{
    /// <summary>
    /// Resource identifier format used for scoped process instances.
    /// </summary>
    public const string ScopedProcessInstanceId = "scopedProcessInstanceId";
}

/// <summary>
/// Well-known fields available from structured resource identifier formats.
/// </summary>
public static class ApiResourceScopeFields
{
    /// <summary>
    /// Field containing the semantic scope id.
    /// </summary>
    public const string ScopeId = "scopeId";
}

/// <summary>
/// Number of scopes an operation can evaluate.
/// </summary>
public enum ApiScopeCardinality
{
    /// <summary>
    /// The operation evaluates exactly one effective scope.
    /// </summary>
    Single = 0,

    /// <summary>
    /// The operation evaluates a set of zero or more effective scopes.
    /// </summary>
    Multiple = 1
}

/// <summary>
/// Transport or semantic binding used to resolve an operation scope.
/// </summary>
public enum ApiScopeBinding
{
    /// <summary>
    /// Scope is resolved from the ambient operation identity context.
    /// </summary>
    Ambient = 0,

    /// <summary>
    /// Scope is carried in HTTP header parameters.
    /// </summary>
    Header = 1,

    /// <summary>
    /// Scope is carried in query string parameters.
    /// </summary>
    Query = 2,

    /// <summary>
    /// Scope is carried directly in route parameters.
    /// </summary>
    Route = 3,

    /// <summary>
    /// Scope is carried in the request body.
    /// </summary>
    Body = 4,

    /// <summary>
    /// Scope is implied by a resource identifier and validated from the loaded or parsed resource.
    /// </summary>
    Resource = 5
}

/// <summary>
/// Access check semantics for a scoped operation.
/// </summary>
public enum ApiScopeAccess
{
    /// <summary>
    /// The operation requires the selected scope or scopes to be valid for the current identity.
    /// </summary>
    RequireSelected = 0,

    /// <summary>
    /// The operation filters the requested scope set to scopes accessible by the current identity.
    /// </summary>
    FilterToAccessible = 1,

    /// <summary>
    /// The operation validates that the resource-derived scope is accessible by the current identity.
    /// </summary>
    ValidateAccessible = 2
}
