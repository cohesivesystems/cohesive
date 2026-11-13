using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Canonical lowering and linked eligibility validation for explicit connection selection.</summary>
public static class IntegrationConnectionDocuments
{
    /// <summary>Canonical kind identifying a pinned connection resolution.</summary>
    public const string ResolutionKind = "integration.connection-resolution.v1";
    static readonly ExecutionDefinitionDocumentProjection<ProviderOperationProfileDefinition> Profiles = Projection<ProviderOperationProfileDefinition>("integration.operation-profile.v1");
    static readonly ExecutionDefinitionDocumentProjection<IntegrationConnectionDefinition> Connections = Projection<IntegrationConnectionDefinition>("integration.connection.v1");
    static readonly ExecutionDefinitionDocumentProjection<IntegrationConnectionResolution> Resolutions = Projection<IntegrationConnectionResolution>(ResolutionKind);
    static ExecutionDefinitionDocumentProjection<T> Projection<T>(string kind) where T : class => new(new(kind),
        "integrations.connection.kind", "integrations.connection.projection", "integrations.connection.wire", "Use the canonical connection declaration.");
    internal static ImmutableArray<string> Names(IEnumerable<string> values) => [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    static ExecutionProvenance Origin(ExecutionDefinitionId id) => new(new("cohesive.integrations.connections", "1"), new(id.Value), DocumentOrigin.Generated);
    static ExecutionDefinitionDocument Declare<T>(ExecutionDefinitionKind kind, ExecutionDefinitionId id, ExecutionRevisionId revision,
        T source, params string[] fields) where T : class =>
        ExecutionDefinitionDocument.Create(kind, id, revision, source, Origin(id),
            sourceMap: new([.. fields.Select(field => new ExecutionSourceProvenance(id.Value + "#/" + field, new([field])))]));

    internal static ExecutionDefinitionDocument DeclareProfile(ExecutionDefinitionId id, ExecutionRevisionId revision, ProviderOperationProfileDefinition source)
    { RequireValid(ValidateProfile(source)); return Declare(Profiles.Kind, id, revision, source, "provider", "operation", "medium", "authenticationMechanisms", "requiredEntitlements", "defaultBatchSize", "maximumBatchSize"); }
    internal static ExecutionDefinitionDocument DeclareConnection(ExecutionDefinitionId id, ExecutionRevisionId revision, IntegrationConnectionDefinition source)
    { RequireValid(ValidateConnection(source)); return Declare(Connections.Kind, id, revision, source, "provider", "resource", "medium", "endpoint", "authentication", "entitlements", "maximumBatchSize"); }
    internal static ExecutionDefinitionDocument DeclareResolution(ExecutionDefinitionId id, ExecutionRevisionId revision, IntegrationConnectionResolution source) =>
        Declare(Resolutions.Kind, id, revision, source, "logical", "profile", "connection", "requestedBatchSize", "effectiveBatchSize", "configuration");

    /// <summary>Validates both documents and determines the effective batch choice under every declared limit.</summary>
    /// <param name="profile">Exact profile document.</param>
    /// <param name="connection">Exact connection document.</param>
    /// <param name="requestedBatchSize">Explicit items per operation, or null for the profile default.</param>
    /// <param name="effectiveBatchSize">Receives the eligible value; zero on failure.</param>
    /// <returns>Structured eligibility diagnostics. Host entitlement declarations are not remote attestation.</returns>
    /// <exception cref="ArgumentNullException">A document is null.</exception>
    public static DocumentValidationResult Match(ExecutionDefinitionDocument profile, ExecutionDefinitionDocument connection,
        int? requestedBatchSize, out int effectiveBatchSize)
    {
        effectiveBatchSize = 0;
        var validation = Read(Profiles, profile, ValidateProfile, out var offered);
        if (!validation.IsValid) return validation;
        validation = Read(Connections, connection, ValidateConnection, out var configured);
        if (!validation.IsValid) return validation;
        if (offered!.Provider != configured!.Provider) return Error("provider", "Connection provider differs from the operation profile.", "/connection/provider");
        if (offered.Medium != configured.Medium) return Error("medium", "Connection medium is unsupported by the operation profile.", "/connection/medium");
        if (!offered.AuthenticationMechanisms.Contains(configured.Authentication.Mechanism, StringComparer.Ordinal))
            return Error("authentication", "Connection authentication is unsupported by the operation profile.", "/connection/authentication");
        if (offered.RequiredEntitlements.Any(required => !configured.Entitlements.Contains(required, StringComparer.Ordinal)))
            return Error("entitlement", "Connection lacks a required account entitlement.", "/connection/entitlements");
        var size = requestedBatchSize ?? offered.DefaultBatchSize;
        if (size <= 0 || size > offered.MaximumBatchSize || size > configured.MaximumBatchSize)
            return Error("batchSize", "The batch choice must be positive and satisfy both profile and connection hard limits; defaults cannot weaken limits.", "/effectiveBatchSize");
        effectiveBatchSize = size;
        return DocumentValidationResult.Valid;
    }

    /// <summary>Reopens and validates an exact pinned selection, including effective-value provenance and all linked constraints.</summary>
    /// <param name="document">Canonical connection-resolution document.</param>
    /// <param name="catalog">Exact dependency catalog.</param>
    /// <param name="resolution">Receives the validated selection on success.</param>
    /// <returns>Structured integrity, link and eligibility diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Document or catalog is null.</exception>
    public static DocumentValidationResult Inspect(ExecutionDefinitionDocument document, ExecutionDefinitionDocumentCatalog catalog,
        out IntegrationConnectionResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        resolution = null;
        var validation = Read(Resolutions, document, value => value.Logical is null || string.IsNullOrWhiteSpace(value.Logical.Name)
            || value.Profile is null || value.Connection is null ? Error("resolution", "Declare a logical reference and exact profile and connection.", "/") : DocumentValidationResult.Valid, out var candidate);
        if (!validation.IsValid) return validation;
        validation = catalog.ValidateReference(candidate!.Profile, "/profile", out var profile);
        if (!validation.IsValid) return validation;
        validation = catalog.ValidateReference(candidate.Connection, "/connection", out var connection);
        if (!validation.IsValid) return validation;
        validation = Match(profile!, connection!, candidate.RequestedBatchSize, out var effective);
        if (!validation.IsValid) return validation;
        if (effective != candidate.EffectiveBatchSize) return Error("effective", "Effective options differ from the pinned declarations.", "/effectiveBatchSize");
        if (candidate.Configuration is not null)
        {
            validation = catalog.ValidateReference(candidate.Configuration, "/configuration", out var snapshot);
            if (!validation.IsValid) return validation;
            validation = IntegrationSelection.Inspect(snapshot!, catalog, candidate);
            if (!validation.IsValid) return validation;
        }
        resolution = candidate;
        return validation;
    }

    static DocumentValidationResult ValidateProfile(ProviderOperationProfileDefinition profile) =>
        string.IsNullOrWhiteSpace(profile.Provider) || string.IsNullOrWhiteSpace(profile.Operation) || string.IsNullOrWhiteSpace(profile.Medium)
            || !ValidNames(profile.AuthenticationMechanisms) || profile.AuthenticationMechanisms.IsEmpty || !ValidNames(profile.RequiredEntitlements)
            || profile.DefaultBatchSize <= 0 || profile.MaximumBatchSize < profile.DefaultBatchSize
            ? Error("profile", "Declare provider, operation, medium, canonical authentication/entitlement sets and positive default/maximum batch sizes.", "/") : DocumentValidationResult.Valid;

    static DocumentValidationResult ValidateConnection(IntegrationConnectionDefinition connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Provider) || string.IsNullOrWhiteSpace(connection.Resource) || string.IsNullOrWhiteSpace(connection.Medium)
            || connection.MaximumBatchSize <= 0 || !ValidNames(connection.Entitlements))
            return Error("definition", "Declare provider, resource, medium, canonical entitlements and a positive batch limit.", "/");
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint) || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            return Error("endpoint", "Use an absolute endpoint without embedded credentials, query or fragment.", "/endpoint");
        var auth = connection.Authentication;
        if (auth is null || string.IsNullOrWhiteSpace(auth.Mechanism)
            || (auth.Mechanism == "anonymous" ? auth.Reference is not null : !Uri.TryCreate(auth.Reference, UriKind.Absolute, out _)))
            return Error("credentialReference", "Use anonymous access or an authentication mechanism with an absolute secret/identity reference; never a secret value.", "/authentication");
        return DocumentValidationResult.Valid;
    }

    static bool ValidNames(ImmutableArray<string> values) => !values.IsDefault && !values.Any(string.IsNullOrWhiteSpace)
        && values.SequenceEqual(Names(values));
    static DocumentValidationResult Read<T>(ExecutionDefinitionDocumentProjection<T> projection, ExecutionDefinitionDocument document,
        Func<T, DocumentValidationResult> validate, out T? value) where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        value = null;
        if (!document.Extensions.IsDefaultOrEmpty) return Error("extension", "Unsupported connection semantics extension.", "/extensions");
        return projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, validate, out value);
    }
    static void RequireValid(DocumentValidationResult result)
    { if (!result.IsValid) throw new ArgumentException(result.Diagnostics[0].Message); }
    internal static DocumentValidationResult Error(string code, string message, string path) =>
        new([new("integrations.connection." + code, DiagnosticSeverity.Error, message, path)]);
}
