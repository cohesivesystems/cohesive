using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>Typed C# handle for one canonical singular hosted-Query definition.</summary>
/// <remarks>
/// The persisted <see cref="Document"/> remains the sole durable semantic authority. CLR generic arguments are
/// authoring projections of its portable invocation and result contracts. The handle retains no executable handler,
/// repository, service, credential, ambient configuration, or runtime placement.
/// </remarks>
/// <typeparam name="TInput">CLR type projected into the hosted Query invocation contract.</typeparam>
/// <typeparam name="TResult">CLR type projected into the hosted Query singular result contract.</typeparam>
public sealed class HostedQuery<TInput, TResult>
    where TInput : notnull
    where TResult : notnull
{
    static readonly IClrTypeRefMapper TypeMapper = new DefaultClrTypeRefMapper();

    internal HostedQuery(ExecutionDefinitionDocument document, DocumentValidationResult validation)
    {
        Document = Guard.RequireNotNull(document);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Canonical persisted execution-definition document and sole durable semantic authority.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Canonical document and hosted-Query semantic validation diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether canonical document and hosted-Query validation found no errors.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Typed projection of the canonical hosted-Query definition.</summary>
    /// <exception cref="JsonException">The canonical payload cannot be projected as a hosted Query.</exception>
    /// <exception cref="NotSupportedException">The strict execution serializer does not support a payload value.</exception>
    public HostedQueryDefinition Definition => Document.GetDefinition<HostedQueryDefinition>();

    /// <summary>Exact identity, revision, and semantic fingerprint of this hosted Query.</summary>
    public ExecutionDefinitionReference Reference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

    /// <summary>Portable invocation contract projected from the canonical definition.</summary>
    public ValueContract InputContract => Definition.Input;

    /// <summary>Portable singular-result contract projected from the canonical definition.</summary>
    public ValueContract ResultContract => Definition.Result;

    /// <summary>Exact semantic host implementation contract projected from the canonical definition.</summary>
    public HostedQueryImplementationReference Implementation => Definition.Implementation;

    /// <summary>Concrete portable implementation configuration projected from the canonical definition.</summary>
    public PortableValue Configuration => Definition.Configuration;

    /// <summary>Complete direct exact definition dependencies in deterministic semantic-role order.</summary>
    public ImmutableArray<HostedQueryDependency> Dependencies => Definition.Dependencies;

    /// <summary>Authors a canonical hosted Query from typed invocation, result, and configuration declarations.</summary>
    /// <typeparam name="TConfiguration">
    /// CLR configuration type projected into a concrete portable value retained by the canonical definition.
    /// </typeparam>
    /// <param name="definitionId">Stable identity shared by every revision of the hosted Query.</param>
    /// <param name="revisionId">Stable identity of this accepted hosted-Query revision.</param>
    /// <param name="implementation">Exact semantic host implementation contract.</param>
    /// <param name="configuration">Typed configuration interpreted by <paramref name="implementation"/>.</param>
    /// <param name="provenance">Producer and root-source attribution for the authored definition.</param>
    /// <param name="dependencies">Complete direct exact definition dependencies.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from fingerprinting.</param>
    /// <returns>
    /// A typed immutable handle containing the canonical document, exact reference, contracts, dependencies, and
    /// retained validation diagnostics.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="implementation"/>, <paramref name="configuration"/>, or <paramref name="provenance"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity, dependency, extension, configuration, or descriptive metadata value is invalid.
    /// </exception>
    /// <exception cref="JsonException">
    /// Typed configuration or canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical content has no stable JSON representation.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime type.</exception>
    public static HostedQuery<TInput, TResult> Create<TConfiguration>(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        HostedQueryImplementationReference implementation,
        TConfiguration configuration,
        ExecutionProvenance provenance,
        IEnumerable<HostedQueryDependency>? dependencies = null,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null)
        where TConfiguration : notnull
    {
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(provenance);

        var configurationContract = new ValueContract(TypeMapper.Map(typeof(TConfiguration), null));
        var configurationOptions = ExecutionDefinitionJsonSerializer.CreateOptions();
        // CLR contract inference uses explicit JsonPropertyName values or stable CLR property names and deliberately
        // excludes ambient naming policies. Serialize the value through that same durable property identity policy.
        configurationOptions.PropertyNamingPolicy = null;
        var configurationElement = JsonSerializer.SerializeToElement(
            configuration,
            configurationOptions);
        var configurationObservation = ObservationValue.FromJsonElement(configurationElement);
        if (configurationObservation.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
        {
            throw new ArgumentException(
                "Hosted-Query configuration must serialize as a concrete non-null portable value.",
                nameof(configuration));
        }

        var definition = new HostedQueryDefinition(
            new(TypeMapper.Map(typeof(TInput), null)),
            new(TypeMapper.Map(typeof(TResult), null)),
            implementation,
            PortableValue.Concrete(configurationContract, configurationObservation),
            dependencies is null ? [] : [.. dependencies]);
        var initial = HostedQueryDefinitionDocuments.Create(
            definitionId,
            revisionId,
            definition,
            provenance,
            extensions,
            displayName,
            description);
        var validation = HostedQueryDefinitionDocuments.ValidateAuthored(initial, definition);
        var document = initial.WithRetainedDiagnostics(validation.Diagnostics);
        return new(document, validation);
    }
}

/// <summary>Typed dependency projections for hosted-Query authoring.</summary>
public static class HostedQueryDependencyAuthoringExtensions
{
    /// <summary>Projects one typed canonical Relation as an exact hosted-Query dependency.</summary>
    /// <typeparam name="TInput">CLR type of the Relation root.</typeparam>
    /// <typeparam name="TResult">CLR type of the singular Relation result.</typeparam>
    /// <param name="relation">Typed canonical Relation that remains authoritative for the exact dependency.</param>
    /// <param name="role">Stable semantic role the Relation fulfills for the hosted Query.</param>
    /// <returns>A role-named exact dependency derived from <paramref name="relation"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty or white-space.</exception>
    public static HostedQueryDependency AsHostedQueryDependency<TInput, TResult>(
        this Relation<TInput, TResult> relation,
        string role)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(relation);
        return new(role, relation.Reference);
    }
}
