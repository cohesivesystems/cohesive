using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Api.Execution.Services;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Relations.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace Cohesive.Host.Composition;

/// <summary>Evidence boundary of an application explanation.</summary>
public enum ApplicationExplanationScope
{
    /// <summary>Successful offline compilation and admission; no native IO or runtime readiness evidence.</summary>
    OfflineAdmission
}

/// <summary>Immutable inspection report over existing canonical documents and successful admission evidence.</summary>
/// <remarks>Derived output, not an executable system IR, import format or independent fingerprint authority.
/// Native deployment and runtime health must be verified separately. Constructed only by successful explanation admission.</remarks>
public sealed class ApplicationExplanationDocument
{
    static readonly Lazy<JsonSerializerOptions> Options = new(() =>
    {
        // Retain the Relations parameter converter as well as the shared strict document conventions.
        var options = RelationQueryJsonSerializer.CreateOptions(indented: true);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    });

    internal ApplicationExplanationDocument(ImmutableArray<EntityDefinition> entities,
        ImmutableArray<ApplicationEndpointExplanation> endpoints, ServiceStorageExplanation storage,
        ImmutableArray<StorageHistoryDeclaration> history, ImmutableArray<RelationQueryDocument> queries,
        ImmutableArray<ExecutionDefinitionDocument> executions, InfrastructureDefinitionDocument infrastructure,
        InfrastructureDeploymentArtifact deployment)
    {
        Entities = entities; Endpoints = endpoints; Storage = storage; History = history;
        Queries = queries; Executions = executions; Infrastructure = infrastructure; Deployment = deployment;
    }

    /// <summary>Version of this inspection report's serialized structure, independent of embedded document versions.</summary>
    public string SchemaVersion => "cohesive.application.explanation/1";
    /// <summary>Explicit evidence boundary; this report contains no live observations.</summary>
    public ApplicationExplanationScope Scope => ApplicationExplanationScope.OfflineAdmission;
    /// <summary>Original canonical entities in ordinal name order.</summary>
    public ImmutableArray<EntityDefinition> Entities { get; }
    /// <summary>Portable endpoint links projected from original API operations; CLR binding types are not exported.</summary>
    public ImmutableArray<ApplicationEndpointExplanation> Endpoints { get; }
    /// <summary>Successful storage ownership and consistency admission.</summary>
    public ServiceStorageExplanation Storage { get; }
    /// <summary>History declarations from contributing native storage bindings; empty when none are declared.</summary>
    public ImmutableArray<StorageHistoryDeclaration> History { get; }
    /// <summary>Original query documents; their own fingerprints remain authoritative.</summary>
    public ImmutableArray<RelationQueryDocument> Queries { get; }
    /// <summary>Original execution documents; their own fingerprints remain authoritative.</summary>
    public ImmutableArray<ExecutionDefinitionDocument> Executions { get; }
    /// <summary>Original infrastructure declaration.</summary>
    public InfrastructureDefinitionDocument Infrastructure { get; }
    /// <summary>Validated compiled deployment intent, not evidence of a live deployment.</summary>
    public InfrastructureDeploymentArtifact Deployment { get; }

    /// <summary>Serializes the report directly, without intermediate JSON trees or re-parsing embedded documents.</summary>
    /// <returns>Indented JSON using the existing portable-document conventions.</returns>
    /// <exception cref="JsonException">An embedded value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An embedded value has no supported serializer.</exception>
    public string ToJson() => JsonSerializer.Serialize(this, Options.Value);
}

/// <summary>Derived endpoint inspection link, excluding CLR callbacks and transport binding types.</summary>
public sealed class ApplicationEndpointExplanation
{
    internal ApplicationEndpointExplanation(ServiceEndpointImplementation implementation)
    {
        var endpoint = implementation.Endpoint;
        Id = endpoint.Id; Kind = endpoint.Kind; ResultOf = endpoint.ResultOf;
        Http = endpoint.Http is { } http ? new(http.Method, http.Route) : null;
        Response = endpoint.PrimaryResult.Kind;
        Execution = implementation.Execution ?? endpoint.TransitionReference;
        Repository = implementation.Repository;
        SemanticReferences = [.. endpoint.SemanticReferences];
    }
    /// <summary>Original endpoint identity.</summary>
    public ApiEndpointId Id { get; }
    /// <summary>Declared operation semantics.</summary>
    public ApiOperationKind Kind { get; }
    /// <summary>Optional HTTP route projection.</summary>
    public ApplicationHttpRouteExplanation? Http { get; }
    /// <summary>Start endpoint whose retained result this endpoint reads, when applicable.</summary>
    public ApiEndpointId? ResultOf { get; }
    /// <summary>Declared primary response behavior.</summary>
    public ApiResultKind Response { get; }
    /// <summary>Exact admitted execution or transition reference.</summary>
    public ExecutionDefinitionReference? Execution { get; }
    /// <summary>Admitted entity observation contract, when applicable.</summary>
    public ValueContract? Repository { get; }
    /// <summary>Original semantic authority references in declaration order.</summary>
    public ImmutableArray<ApiSemanticReference> SemanticReferences { get; }
}

/// <summary>Inspection of native HTTP routing, without CLR binding metadata.</summary>
/// <param name="Method">Original HTTP method.</param>
/// <param name="Route">Original route template.</param>
public sealed record ApplicationHttpRouteExplanation(string Method, string Route);
