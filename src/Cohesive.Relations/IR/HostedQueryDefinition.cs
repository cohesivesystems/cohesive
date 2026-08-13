using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.IR;

/// <summary>Stable wire identities for canonical hosted-Query documents.</summary>
public static class HostedQueryWireNames
{
    /// <summary>Shared execution-definition kind for hosted Queries.</summary>
    public const string DefinitionKind = "hosted-query";
}

/// <summary>Exact semantic implementation contract selected by a hosted Query definition.</summary>
/// <remarks>
/// This identifies a host capability contract, not a CLR type, delegate, service instance, or deployment. Runtime
/// registration maps the exact identity and version to executable infrastructure outside canonical content.
/// </remarks>
public sealed record HostedQueryImplementationReference
{
    /// <summary>Creates an exact hosted-Query implementation reference.</summary>
    /// <param name="id">Stable implementation-family identity.</param>
    /// <param name="version">Exact semantic version of the implementation contract.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="version"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="version"/> is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public HostedQueryImplementationReference(string id, string version)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Version = Guard.RequireNotNullOrWhiteSpace(version);
    }

    /// <summary>Stable implementation-family identity.</summary>
    public string Id { get; }

    /// <summary>Exact semantic version of the implementation contract.</summary>
    public string Version { get; }
}

/// <summary>Role-named exact execution-definition dependency of one hosted Query.</summary>
public sealed record HostedQueryDependency
{
    /// <summary>Creates an exact role-named hosted-Query dependency.</summary>
    /// <param name="role">Stable semantic role this dependency fulfills for the host implementation.</param>
    /// <param name="definition">Exact dependency identity, revision, and fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="role"/> or <paramref name="definition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty or white-space.</exception>
    [JsonConstructor]
    public HostedQueryDependency(string role, ExecutionDefinitionReference definition)
    {
        Role = Guard.RequireNotNullOrWhiteSpace(role);
        Definition = Guard.RequireNotNull(definition);
    }

    /// <summary>Stable semantic role this dependency fulfills for the host implementation.</summary>
    public string Role { get; }

    /// <summary>Exact dependency identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }
}

/// <summary>Canonical portable contract for one host-executed singular Query.</summary>
/// <remarks>
/// The definition describes invocation and result semantics, the exact host capability contract, its complete direct
/// definition dependencies, and portable configuration. Executable handlers, repositories, credentials, ambient
/// configuration, and runtime placement are deliberately excluded.
/// </remarks>
public sealed record HostedQueryDefinition
{
    /// <summary>Creates a canonical hosted-Query definition.</summary>
    /// <param name="input">Portable invocation-input contract.</param>
    /// <param name="result">Portable singular-result contract.</param>
    /// <param name="implementation">Exact semantic host implementation contract.</param>
    /// <param name="configuration">Concrete portable configuration interpreted by <paramref name="implementation"/>.</param>
    /// <param name="dependencies">Complete direct exact definition dependencies.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/>, <paramref name="result"/>, <paramref name="implementation"/>, or
    /// <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="dependencies"/> contains a null entry, repeats a role, or repeats an exact definition.
    /// </exception>
    [JsonConstructor]
    public HostedQueryDefinition(
        ValueContract input,
        ValueContract result,
        HostedQueryImplementationReference implementation,
        PortableValue configuration,
        ImmutableArray<HostedQueryDependency> dependencies = default)
    {
        Input = Guard.RequireNotNull(input);
        Result = Guard.RequireNotNull(result);
        Implementation = Guard.RequireNotNull(implementation);
        Configuration = Guard.RequireNotNull(configuration);

        var materialized = dependencies.IsDefault ? [] : dependencies;
        if (materialized.Any(static dependency => dependency is null))
        {
            throw new ArgumentException(
                "Hosted-Query dependencies cannot contain null entries.",
                nameof(dependencies));
        }
        if (materialized.Select(static dependency => dependency.Role).Distinct(StringComparer.Ordinal).Count()
            != materialized.Length)
        {
            throw new ArgumentException(
                "Hosted-Query dependencies cannot repeat a semantic role.",
                nameof(dependencies));
        }
        if (materialized.Select(static dependency => dependency.Definition).Distinct().Count()
            != materialized.Length)
        {
            throw new ArgumentException(
                "Hosted-Query dependencies cannot repeat an exact definition reference.",
                nameof(dependencies));
        }

        Dependencies = [.. materialized.OrderBy(static dependency => dependency.Role, StringComparer.Ordinal)];
    }

    /// <summary>Portable invocation-input contract.</summary>
    public ValueContract Input { get; }

    /// <summary>Portable singular-result contract.</summary>
    public ValueContract Result { get; }

    /// <summary>Exact semantic host implementation contract.</summary>
    public HostedQueryImplementationReference Implementation { get; }

    /// <summary>Concrete portable configuration interpreted by <see cref="Implementation"/>.</summary>
    public PortableValue Configuration { get; }

    /// <summary>Complete direct exact definition dependencies in deterministic role order.</summary>
    public ImmutableArray<HostedQueryDependency> Dependencies { get; }

    /// <summary>Compares canonical hosted Queries by their complete persisted semantic state.</summary>
    /// <param name="other">Hosted Query to compare.</param>
    /// <returns><see langword="true"/> when every canonical semantic member is equal.</returns>
    public bool Equals(HostedQueryDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Input == other.Input
        && Result == other.Result
        && Implementation == other.Implementation
        && Configuration == other.Configuration
        && Dependencies.SequenceEqual(other.Dependencies);

    /// <summary>Returns a structural hash code for complete hosted-Query semantics.</summary>
    /// <returns>A hash code derived from contracts, implementation, configuration, and dependencies.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Input);
        hash.Add(Result);
        hash.Add(Implementation);
        hash.Add(Configuration);
        foreach (var dependency in Dependencies)
            hash.Add(dependency);
        return hash.ToHashCode();
    }
}

/// <summary>Stable diagnostic codes emitted while validating canonical hosted Queries.</summary>
public static class HostedQueryDefinitionDiagnosticCodes
{
    /// <summary>An invocation, result, or configuration value contract is not portable or coherent.</summary>
    public const string ValueInvalid = "hostedQueries.definition.value.invalid";

    /// <summary>The invocation or result boundary is not singular.</summary>
    public const string CardinalityInvalid = "hostedQueries.definition.cardinality.invalid";

    /// <summary>Hosted-Query configuration is not a concrete portable value.</summary>
    public const string ConfigurationStateInvalid = "hostedQueries.definition.configuration.stateInvalid";
}

/// <summary>Semantic and portability validation for canonical hosted Queries.</summary>
public static class HostedQueryDefinitionValidator
{
    /// <summary>Validates a hosted Query without a graph for resolving named portable types.</summary>
    /// <param name="definition">Canonical hosted-Query definition.</param>
    /// <returns>Deterministically ordered validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(HostedQueryDefinition definition) =>
        ValidateCore(definition, graph: null);

    /// <summary>Validates a hosted Query using a graph that resolves named portable types and shapes.</summary>
    /// <param name="definition">Canonical hosted-Query definition.</param>
    /// <param name="graph">Shape graph used to resolve named and qualified value contracts.</param>
    /// <returns>Deterministically ordered validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(HostedQueryDefinition definition, ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return ValidateCore(definition, graph);
    }

    static DocumentValidationResult ValidateCore(HostedQueryDefinition definition, ShapeGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        ValidateContract(definition.Input, "/input", graph, diagnostics);
        ValidateContract(definition.Result, "/result", graph, diagnostics);
        ValidateConfiguration(definition.Configuration, graph, diagnostics);

        if (definition.Input.Cardinality != FieldCardinality.Single)
        {
            diagnostics.Add(Error(
                HostedQueryDefinitionDiagnosticCodes.CardinalityInvalid,
                "A hosted Query invocation requires one input value.",
                "/input/cardinality",
                FieldCardinality.Single.ToString(),
                definition.Input.Cardinality.ToString()));
        }
        if (definition.Result.Cardinality != FieldCardinality.Single)
        {
            diagnostics.Add(Error(
                HostedQueryDefinitionDiagnosticCodes.CardinalityInvalid,
                "A typed hosted Query requires one result value; represent result collections in the result type.",
                "/result/cardinality",
                FieldCardinality.Single.ToString(),
                definition.Result.Cardinality.ToString()));
        }
        if (definition.Configuration.State != PortableValueState.Concrete)
        {
            diagnostics.Add(Error(
                HostedQueryDefinitionDiagnosticCodes.ConfigurationStateInvalid,
                "Hosted-Query configuration must be a concrete portable value.",
                "/configuration/state",
                PortableValueState.Concrete.ToString(),
                definition.Configuration.State.ToString()));
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateContract(
        ValueContract contract,
        string location,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validation = graph is null
            ? PortableExecutionValidator.Validate(contract)
            : PortableExecutionValidator.Validate(contract, graph);
        AddPortableDiagnostics(validation, location, diagnostics);
    }

    static void ValidateConfiguration(
        PortableValue configuration,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validation = PortableExecutionValidator.Validate(configuration, graph);
        AddPortableDiagnostics(validation, "/configuration", diagnostics);
    }

    static void AddPortableDiagnostics(
        DocumentValidationResult validation,
        string prefix,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(diagnostic with
            {
                Code = HostedQueryDefinitionDiagnosticCodes.ValueInvalid,
                Location = Prefix(prefix, diagnostic.Location),
                Evidence = new(
                    stage: "hostedQueryDefinitionValidation",
                    subject: diagnostic.Evidence?.Subject,
                    relatedLocations: diagnostic.Evidence?.RelatedLocations ?? [],
                    sourceReferences: diagnostic.Evidence?.SourceReferences ?? [],
                    resolutionOptions: diagnostic.Evidence?.ResolutionOptions ?? [],
                    expected: diagnostic.Evidence?.Expected ?? "portable Cohesive value",
                    observed: diagnostic.Evidence?.Observed ?? diagnostic.Code)
            });
        }
    }

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string expected,
        string observed) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            Location: location,
            Evidence: new(
                stage: "hostedQueryDefinitionValidation",
                expected: expected,
                observed: observed));

    static string Prefix(string prefix, string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "$")
            return prefix;
        return location[0] == '/' ? prefix + location : prefix;
    }
}
