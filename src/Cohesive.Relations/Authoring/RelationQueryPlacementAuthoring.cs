using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Authoring;

/// <summary>Resolves a semantic field path to an adapter-interpreted physical selector.</summary>
/// <param name="semanticPath">Exact semantic field path demanded from the placed input.</param>
/// <returns>A stable non-empty physical selector.</returns>
public delegate string RelationQueryPlacementFieldSelector(FieldPath semanticPath);

/// <summary>Scoped, versioned defaults applied by plan-bound source-placement authoring.</summary>
public sealed class RelationQueryPlacementAuthoringOptions
{
    /// <summary>Creates scoped placement-authoring defaults.</summary>
    /// <param name="authority">Stable identity and version of this scoped profile.</param>
    /// <param name="conventionSetVersion">Placement convention-set version, or <see langword="null"/> for the framework default.</param>
    /// <param name="defaultLimits">Default source limits, or <see langword="null"/> for framework limits.</param>
    /// <param name="identitySourceSelector">Default identity selector, or <see langword="null"/> for the framework selector.</param>
    /// <param name="relationshipKeySourceSelector">
    /// Default inverse-relationship selector, or <see langword="null"/> for the framework selector.
    /// </param>
    /// <param name="fieldSourceSelector">
    /// Deterministic semantic-to-physical field selector, or <see langword="null"/> to use semantic path text.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is empty; or a supplied selector or convention version is empty.
    /// </exception>
    public RelationQueryPlacementAuthoringOptions(
        string authority,
        string? conventionSetVersion = null,
        RelationQuerySourcePlacementLimits? defaultLimits = null,
        string? identitySourceSelector = null,
        string? relationshipKeySourceSelector = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        ConventionSetVersion = Optional(conventionSetVersion, nameof(conventionSetVersion));
        DefaultLimits = defaultLimits;
        IdentitySourceSelector = Optional(identitySourceSelector, nameof(identitySourceSelector));
        RelationshipKeySourceSelector = Optional(relationshipKeySourceSelector, nameof(relationshipKeySourceSelector));
        FieldSourceSelector = fieldSourceSelector;
    }

    /// <summary>Stable identity and version of this scoped profile.</summary>
    public string Authority { get; }

    /// <summary>Scoped placement convention-set version, or <see langword="null"/>.</summary>
    public string? ConventionSetVersion { get; }

    /// <summary>Scoped source limits, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementLimits? DefaultLimits { get; }

    /// <summary>Scoped observation-identity selector, or <see langword="null"/>.</summary>
    public string? IdentitySourceSelector { get; }

    /// <summary>Scoped inverse-relationship selector, or <see langword="null"/>.</summary>
    public string? RelationshipKeySourceSelector { get; }

    /// <summary>Scoped semantic-to-physical field selector, or <see langword="null"/>.</summary>
    public RelationQueryPlacementFieldSelector? FieldSourceSelector { get; }

    static string? Optional(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An optional placement-authoring value cannot be empty.", parameterName);
        }

        return value;
    }
}

/// <summary>Stable diagnostic codes emitted by plan-bound source-placement authoring.</summary>
public static class RelationQueryPlacementAuthoringDiagnosticCodes
{
    /// <summary>A declared source instance identity is duplicated or conflicts.</summary>
    public const string SourceConflict = "relationQuery.authoring.placement.source.conflict";

    /// <summary>A placement refers to an unknown or foreign source instance.</summary>
    public const string SourceUnknown = "relationQuery.authoring.placement.source.unknown";

    /// <summary>A selected compiled input is absent, stale, or has the wrong kind.</summary>
    public const string InputInvalid = "relationQuery.authoring.placement.input.invalid";

    /// <summary>More than one compiled input matches a convention-based selection.</summary>
    public const string InputAmbiguous = "relationQuery.authoring.placement.input.ambiguous";

    /// <summary>A required compiled source or traversal input has no placement.</summary>
    public const string PlacementMissing = "relationQuery.authoring.placement.input.missing";

    /// <summary>The same compiled source or traversal input is placed more than once.</summary>
    public const string PlacementConflict = "relationQuery.authoring.placement.input.conflict";

    /// <summary>A CLR shape or selector does not belong to the selected semantic input.</summary>
    public const string ShapeMismatch = "relationQuery.authoring.placement.shape.mismatch";

    /// <summary>An acquisition override is incompatible with the exact compiled demand.</summary>
    public const string AcquisitionMismatch = "relationQuery.authoring.placement.acquisition.mismatch";

    /// <summary>A required field selector is missing, unknown, invalid, or conflicting.</summary>
    public const string FieldBindingInvalid = "relationQuery.authoring.placement.field.invalid";

    /// <summary>An identity selector is invalid or conflicting.</summary>
    public const string IdentityBindingInvalid = "relationQuery.authoring.placement.identity.invalid";

    /// <summary>An inverse-relationship selector is invalid, missing, or conflicting.</summary>
    public const string RelationshipKeyBindingInvalid = "relationQuery.authoring.placement.relationshipKey.invalid";

    /// <summary>A source target profile is invalid or incompatible with the exact compiled plan.</summary>
    public const string TargetProfileMismatch = "relationQuery.authoring.placement.targetProfile.mismatch";

    /// <summary>Configuration declarations or convention output conflict or are invalid.</summary>
    public const string ConfigurationInvalid = "relationQuery.authoring.placement.configuration.invalid";

    /// <summary>The normalized low-level source-placement artifact rejected derived content.</summary>
    public const string ArtifactInvalid = "relationQuery.authoring.placement.artifact.invalid";
}

/// <summary>Entry point for plan-bound source-placement authoring.</summary>
public static class RelationQueryPlacement
{
    /// <summary>Creates a placement builder bound to one exact demand-scoped compiled plan.</summary>
    /// <param name="plan">Exact compiled plan whose acquisition inputs will be placed.</param>
    /// <param name="options">Optional scoped authoring defaults.</param>
    /// <returns>A new mutable placement-authoring builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static RelationQueryPlacementBuilder For(
        CompiledRelationQueryPlan plan,
        RelationQueryPlacementAuthoringOptions? options = null) =>
        new(plan, options);
}

/// <summary>Handle for one source-instance declaration owned by a placement builder.</summary>
public sealed class RelationQueryPlacementSourceHandle
{
    internal RelationQueryPlacementSourceHandle(
        RelationQueryPlacementBuilder owner,
        string sourceKey,
        RelationQuerySourceInstanceId id)
    {
        Owner = owner;
        SourceKey = sourceKey;
        Id = id;
    }

    internal RelationQueryPlacementBuilder Owner { get; }

    /// <summary>
    /// Stable authoring key used to derive convention identities; it is not an adapter container, index, or endpoint.
    /// </summary>
    public string SourceKey { get; }

    /// <summary>Effective source-instance identity.</summary>
    public RelationQuerySourceInstanceId Id { get; }
}

/// <summary>
/// Mutable, single-threaded builder that lowers exact compiled input contracts to a normalized source placement.
/// </summary>
public sealed class RelationQueryPlacementBuilder
{
    /// <summary>Framework placement convention-set version.</summary>
    public const string FrameworkConventionSetVersion = "cohesive.relations.placement/conventions-v1";

    /// <summary>Stable authority for framework-derived placement values.</summary>
    public const string FrameworkDefaultAuthority = "cohesive.relations.placement/framework-defaults-v1";

    /// <summary>Stable authority for explicit local placement declarations.</summary>
    public const string ExplicitDeclarationAuthority = "cohesive.relations.placement/explicit-local-v1";

    const string DefaultIdentitySelector = "$identity";
    const string DefaultRelationshipKeySelector = "$relationship";
    static readonly RelationQuerySourcePlacementLimits FrameworkLimits = new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4);

    readonly CompiledRelationQueryPlan plan;
    readonly RelationQueryPlacementAuthoringOptions? options;
    readonly List<SourceDeclaration> sourceDeclarations = [];
    readonly List<InputDeclaration> inputDeclarations = [];
    readonly List<RelationQueryArtifactAuthoringDiagnostic> diagnostics = [];

    internal RelationQueryPlacementBuilder(
        CompiledRelationQueryPlan plan,
        RelationQueryPlacementAuthoringOptions? options)
    {
        this.plan = Guard.RequireNotNull(plan);
        this.options = options;
    }

    /// <summary>Exact demand-scoped compiled plan being placed.</summary>
    public CompiledRelationQueryPlan Plan => plan;

    /// <summary>Declares one concrete physical source instance.</summary>
    /// <param name="sourceKey">
    /// Stable authoring key used to derive default source and execution-domain identities. Adapter-specific physical
    /// locations such as containers and indexes belong in adapter bindings and are not inferred from this key.
    /// </param>
    /// <param name="targetProfile">Exact target capability profile snapshot associated with the source.</param>
    /// <param name="executionDomain">Explicit execution domain, or <see langword="null"/> for a deterministic domain.</param>
    /// <param name="limits">Explicit source limits, or <see langword="null"/> to apply scoped or framework defaults.</param>
    /// <param name="id">Explicit source-instance identity, or <see langword="null"/> to derive one deterministically.</param>
    /// <returns>A handle used to place one or more compiled inputs at the source.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sourceKey"/> or <paramref name="targetProfile"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceKey"/> is empty, or a supplied identity is default.
    /// </exception>
    public RelationQueryPlacementSourceHandle Source(
        string sourceKey,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryExecutionDomainId? executionDomain = null,
        RelationQuerySourcePlacementLimits? limits = null,
        RelationQuerySourceInstanceId? id = null)
    {
        sourceKey = Guard.RequireNotNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(targetProfile);
        if (executionDomain is { } domain && string.IsNullOrWhiteSpace(domain.Value))
        {
            throw new ArgumentException("An explicit execution domain cannot be default.", nameof(executionDomain));
        }

        if (id is { } sourceId && string.IsNullOrWhiteSpace(sourceId.Value))
        {
            throw new ArgumentException("An explicit source-instance identity cannot be default.", nameof(id));
        }

        var effectiveId = id ?? new RelationQuerySourceInstanceId(
            $"source/{Encode(targetProfile.Target.Value)}/{Encode(targetProfile.Id.Value)}/{Encode(sourceKey)}");
        var effectiveDomain = executionDomain ?? new RelationQueryExecutionDomainId(
            $"domain/{Encode(targetProfile.Target.Value)}/{Encode(sourceKey)}");
        var effectiveLimits = limits ?? options?.DefaultLimits ?? FrameworkLimits;
        var handle = new RelationQueryPlacementSourceHandle(this, sourceKey, effectiveId);
        sourceDeclarations.Add(new(
            handle,
            new(effectiveId, effectiveDomain, targetProfile, effectiveLimits),
            id is null ? Framework() : Explicit(),
            executionDomain is null ? Framework() : Explicit(),
            limits is not null ? Explicit() : options?.DefaultLimits is not null ? Scoped() : Framework()));
        return handle;
    }

    /// <summary>Places the only compiled source-set input using structural selectors.</summary>
    /// <param name="source">Declared physical source instance.</param>
    /// <returns>A mutable input-binding builder; build diagnostics report absent or ambiguous source inputs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder PlaceSource(RelationQueryPlacementSourceHandle source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var matches = plan.InputContract.Sources;
        if (matches.Length == 1)
        {
            var placed = Place(matches[0], source);
            placed.Declaration.SelectionExplicit = false;
            return placed;
        }

        diagnostics.Add(Error(
            matches.IsDefaultOrEmpty
                ? RelationQueryPlacementAuthoringDiagnosticCodes.InputInvalid
                : RelationQueryPlacementAuthoringDiagnosticCodes.InputAmbiguous,
            matches.IsDefaultOrEmpty
                ? "The compiled plan has no source-set input to place."
                : "The compiled plan has several source-set inputs; select one exact input contract."));
        return AddInvalid(source);
    }

    /// <summary>Places the only compiled source-set input matching a CLR shape.</summary>
    /// <typeparam name="T">CLR type represented by <paramref name="shape"/>.</typeparam>
    /// <param name="source">Declared physical source instance.</param>
    /// <param name="shape">Authoritative CLR semantic shape mapping.</param>
    /// <returns>A typed mutable input-binding builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="shape"/> is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder<T> PlaceSource<T>(
        RelationQueryPlacementSourceHandle source,
        RelationQueryClrShape<T> shape)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shape);
        var matches = plan.InputContract.Sources.Where(candidate => candidate.Shape == shape.Id).ToArray();
        if (matches.Length == 1)
        {
            var placed = Place(matches[0], source, shape);
            placed.Declaration.SelectionExplicit = false;
            return placed;
        }

        diagnostics.Add(Error(
            matches.Length == 0
                ? RelationQueryPlacementAuthoringDiagnosticCodes.ShapeMismatch
                : RelationQueryPlacementAuthoringDiagnosticCodes.InputAmbiguous,
            matches.Length == 0
                ? $"The compiled plan has no source-set input for CLR shape '{shape.Id}'."
                : $"The compiled plan has several source-set inputs for CLR shape '{shape.Id}'; select one exact input contract."));
        return AddInvalid(source, shape);
    }

    /// <summary>Places one exact compiled source-set input using structural selectors.</summary>
    /// <param name="input">Source-set contract selected from <see cref="CompiledRelationQueryPlan.InputContract"/>.</param>
    /// <param name="source">Declared physical source instance.</param>
    /// <returns>A mutable structural input-binding builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder Place(
        RelationQuerySourceInputContract input,
        RelationQueryPlacementSourceHandle source)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        var declaration = new InputDeclaration(this, input.Input.Id, source, InputContractKind.Source, input);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    /// <summary>Places one exact compiled source-set input with typed semantic selectors.</summary>
    /// <typeparam name="T">CLR type represented by <paramref name="shape"/>.</typeparam>
    /// <param name="input">Source-set contract selected from <see cref="CompiledRelationQueryPlan.InputContract"/>.</param>
    /// <param name="source">Declared physical source instance.</param>
    /// <param name="shape">Authoritative CLR semantic shape mapping.</param>
    /// <returns>A mutable typed input-binding builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder<T> Place<T>(
        RelationQuerySourceInputContract input,
        RelationQueryPlacementSourceHandle source,
        RelationQueryClrShape<T> shape)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shape);
        var declaration = new TypedInputDeclaration<T>(
            this,
            input.Input.Id,
            source,
            InputContractKind.Source,
            input,
            shape);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    /// <summary>Places one exact compiled relationship-traversal input using structural selectors.</summary>
    /// <param name="input">Traversal contract selected from <see cref="CompiledRelationQueryPlan.InputContract"/>.</param>
    /// <param name="source">Declared physical source instance that supplies traversal results.</param>
    /// <returns>A mutable structural input-binding builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder Place(
        RelationQueryTraversalInputContract input,
        RelationQueryPlacementSourceHandle source)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        var declaration = new InputDeclaration(this, input.Input.Id, source, InputContractKind.Traversal, input);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    /// <summary>Places one exact compiled relationship-traversal input with typed semantic selectors.</summary>
    /// <typeparam name="T">CLR type represented by <paramref name="shape"/>.</typeparam>
    /// <param name="input">Traversal contract selected from <see cref="CompiledRelationQueryPlan.InputContract"/>.</param>
    /// <param name="source">Declared physical source instance that supplies traversal results.</param>
    /// <param name="shape">Authoritative CLR semantic shape mapping.</param>
    /// <returns>A mutable typed input-binding builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public RelationQueryPlacementInputBuilder<T> Place<T>(
        RelationQueryTraversalInputContract input,
        RelationQueryPlacementSourceHandle source,
        RelationQueryClrShape<T> shape)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shape);
        var declaration = new TypedInputDeclaration<T>(
            this,
            input.Input.Id,
            source,
            InputContractKind.Traversal,
            input,
            shape);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    /// <summary>Builds the normalized v2 source-placement artifact and immutable placed-input views.</summary>
    /// <returns>A complete authored placement or structured fail-closed diagnostics.</returns>
    /// <exception cref="InvalidOperationException">
    /// The exact compiled-plan reference cannot be fingerprinted deterministically.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A semantic shape snapshot cannot be serialized for plan attribution.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A semantic shape snapshot contains a runtime value unsupported by canonical serialization.
    /// </exception>
    public RelationQueryArtifactAuthoringResult<RelationQueryAuthoredPlacement> Build()
    {
        List<RelationQueryArtifactAuthoringDiagnostic> found = [.. diagnostics];
        ValidateSources(found);

        var sourcesById = sourceDeclarations
            .GroupBy(static declaration => declaration.Instance.Id)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single());
        var currentInputs = CurrentInputs();
        var declaredByInput = inputDeclarations
            .Where(static declaration => declaration.Input is not null)
            .GroupBy(static declaration => declaration.Input!.Value)
            .ToArray();
        foreach (var group in declaredByInput.Where(static group => group.Count() > 1))
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.PlacementConflict,
                $"Compiled input '{group.Key.Value}' is placed more than once.",
                group.Key));
        }

        foreach (var expected in currentInputs.Keys.Except(declaredByInput.Select(static group => group.Key)))
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.PlacementMissing,
                $"Compiled acquisition input '{expected.Value}' has no source placement.",
                expected));
        }

        List<BindingBuild> builtBindings = [];
        foreach (var declaration in inputDeclarations)
        {
            if (declaration.Input is not { } input
                || declaredByInput.Single(group => group.Key == input).Count() != 1)
            {
                continue;
            }
            if (!ReferenceEquals(declaration.Owner, this)
                || !ReferenceEquals(declaration.Source.Owner, this)
                || !sourcesById.TryGetValue(declaration.Source.Id, out var source))
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.SourceUnknown,
                    $"Placement input '{input.Value}' refers to a source handle from another builder or an invalid source declaration.",
                    input));
                continue;
            }
            if (!currentInputs.TryGetValue(input, out var contract)
                || contract.Kind != declaration.Kind
                || !ReferenceEquals(
                    declaration.CompiledContract,
                    (object?)contract.Source ?? contract.Traversal))
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.InputInvalid,
                    $"Placement input '{input.Value}' is absent from, stale for, or has the wrong kind in the exact compiled plan.",
                    input));
                continue;
            }

            var binding = BuildBinding(declaration, contract, source, found);
            if (binding is not null)
            {
                builtBindings.Add(binding);
            }
        }

        if (HasErrors(found))
        {
            return new(null, [.. found]);
        }

        var sourceInstances = sourceDeclarations.Select(static declaration => declaration.Instance).ToImmutableArray();
        List<RelationQueryConfigurationDecision> decisions =
        [
            Decision(
                "placement/convention-set-version",
                options?.ConventionSetVersion is not null ? Scoped() : Framework())

        ];
        foreach (var source in sourceDeclarations)
        {
            source.AppendDecisions(decisions);
        }

        foreach (var binding in builtBindings)
        {
            decisions.AddRange(binding.Decisions);
        }

        RelationQuerySourcePlacement placement;
        try
        {
            placement = new(
                RelationQuerySourcePlacement.CurrentSchemaVersion,
                RelationQueryCompiledPlanReference.From(plan),
                options?.ConventionSetVersion ?? FrameworkConventionSetVersion,
                sourceInstances,
                [.. builtBindings.Select(static built => built.Binding)],
                configurationDecisions: [.. decisions]);
        }
        catch (ArgumentException exception)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.ArtifactInvalid,
                $"The normalized source-placement artifact rejected authored configuration: {exception.Message}"));
            return new(null, [.. found]);
        }

        var sourceInstancesById = placement.SourceInstances.ToDictionary(static source => source.Id);
        var bindingsByInput = placement.Bindings.ToDictionary(static binding => binding.Input);
        ImmutableArray<RelationQueryPlacedInput> placedInputs =
        [
            .. builtBindings
                .OrderBy(static binding => binding.Binding.Input.Value, StringComparer.Ordinal)
                .Select(binding => binding.Declaration.CreatePlacedInput(
                    plan,
                    placement,
                    bindingsByInput[binding.Binding.Input],
                    sourceInstancesById[binding.Binding.Source],
                    binding.Contract.Fields))
        ];
        return new(new RelationQueryAuthoredPlacement(
            plan,
            placement,
            placedInputs,
            [.. builtBindings.Select(static binding => binding.Declaration)]), [.. found]);
    }

    RelationQueryPlacementInputBuilder AddInvalid(RelationQueryPlacementSourceHandle source)
    {
        var declaration = new InputDeclaration(
            this,
            input: null,
            source,
            InputContractKind.Source,
            compiledContract: null);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    RelationQueryPlacementInputBuilder<T> AddInvalid<T>(
        RelationQueryPlacementSourceHandle source,
        RelationQueryClrShape<T> shape)
        where T : notnull
    {
        var declaration = new TypedInputDeclaration<T>(
            this,
            input: null,
            source,
            InputContractKind.Source,
            compiledContract: null,
            shape);
        inputDeclarations.Add(declaration);
        return new(declaration);
    }

    void ValidateSources(ICollection<RelationQueryArtifactAuthoringDiagnostic> found)
    {
        foreach (var group in sourceDeclarations.GroupBy(static declaration => declaration.Instance.Id))
        {
            if (group.Count() > 1)
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.SourceConflict,
                    $"Source-instance identity '{group.Key.Value}' is declared more than once.",
                    setting: SourceSetting(group.Key, "id")));
            }
        }

        foreach (var source in sourceDeclarations)
        {
            var profile = source.Instance.TargetProfile;
            var analysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(profile);
            if (!analysis.Issues.IsDefaultOrEmpty
                || !profile.SupportedDefinitionSchemaVersions.Contains(
                    plan.Provenance.DefinitionDocument.SchemaVersion,
                    StringComparer.Ordinal)
                || !profile.SupportedCompilerProfiles.Contains(
                    plan.Provenance.CompilerProfile,
                    StringComparer.Ordinal))
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.TargetProfileMismatch,
                    $"Source '{source.Instance.Id.Value}' has an invalid target profile or does not support the exact definition and compiler profiles.",
                    setting: SourceSetting(source.Instance.Id, "target-profile")));
            }
        }
    }

    BindingBuild? BuildBinding(
        InputDeclaration declaration,
        Contract contract,
        SourceDeclaration source,
        ICollection<RelationQueryArtifactAuthoringDiagnostic> found)
    {
        var errorCount = found.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (declaration.ClrShapeId is { } clrShape && clrShape != contract.Shape)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.ShapeMismatch,
                $"CLR shape '{clrShape}' does not match placed shape '{contract.Shape}'.",
                contract.Input));
            return null;
        }

        if (declaration.ClrShapeDocument is { } clrShapeDocument)
        {
            var compiledShapeDocuments = plan.Provenance.ShapeDocuments
                .Where(document => document.Graph.Id == contract.Shape.GraphId)
                .ToArray();
            if (compiledShapeDocuments.Length != 1
                || !Equals(
                    RelationQueryCompiledPlanFingerprinter.ComputeShapeSnapshot(clrShapeDocument),
                    RelationQueryCompiledPlanFingerprinter.ComputeShapeSnapshot(compiledShapeDocuments[0])))
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.ShapeMismatch,
                    $"CLR shape '{declaration.ClrShapeId}' belongs to a shape-graph snapshot that does not semantically match "
                    + $"the exact graph '{contract.Shape.GraphId.Value}' consumed by the compiled plan.",
                    contract.Input));
                return null;
            }
        }

        var expectedAcquisition = contract.Kind == InputContractKind.Traversal
            ? RelationQuerySourceAcquisitionKind.BoundedLookup
            : contract.Source!.Role == RelationQuerySourceInputRole.RelationRoot
                ? RelationQuerySourceAcquisitionKind.Supplied
                : RelationQuerySourceAcquisitionKind.BoundedEnumeration;
        var acquisition = declaration.Acquisition ?? expectedAcquisition;
        if (declaration.AcquisitionConflict)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid,
                $"Placement input '{contract.Input.Value}' declares more than one acquisition override.",
                contract.Input,
                setting: InputSetting(declaration.EffectiveId, "acquisition")));
        }
        if (acquisition != expectedAcquisition)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.AcquisitionMismatch,
                $"Acquisition '{acquisition}' cannot preserve compiled input '{contract.Input.Value}', which requires '{expectedAcquisition}'.",
                contract.Input,
                setting: InputSetting(declaration.EffectiveId, "acquisition")));
        }

        var bindingId = declaration.EffectiveId;
        List<RelationQueryConfigurationDecision> decisions =
        [
            Decision(InputSetting(bindingId, "id"), declaration.Id is null ? Framework() : Explicit()),
            Decision(
                InputSetting(bindingId, "source"),
                declaration.SelectionExplicit ? Explicit() : Framework()),
            Decision(InputSetting(bindingId, "acquisition"), declaration.Acquisition is null ? Framework() : Explicit())
        ];

        var fields = BuildFields(declaration, contract, bindingId, decisions, found);
        RelationQuerySourceIdentityBinding? identity = null;
        if (declaration.IdentitySelector is { } explicitIdentity)
        {
            identity = new(contract.Shape, explicitIdentity);
            decisions.Add(Decision(InputSetting(bindingId, "identity/source-selector"), Explicit()));
        }
        else if (acquisition != RelationQuerySourceAcquisitionKind.Supplied)
        {
            var effective = options?.IdentitySourceSelector ?? DefaultIdentitySelector;
            identity = new(contract.Shape, effective);
            decisions.Add(Decision(
                InputSetting(bindingId, "identity/source-selector"),
                options?.IdentitySourceSelector is not null ? Scoped() : Framework()));
        }

        if (declaration.IdentityConflict)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.IdentityBindingInvalid,
                $"Placement input '{contract.Input.Value}' declares more than one explicit identity selector.",
                contract.Input,
                setting: InputSetting(bindingId, "identity/source-selector")));
        }

        ImmutableArray<RelationQueryRelationshipKeyBinding> relationshipKeys = [];
        if (contract.Traversal is { } traversal
            && traversal.Input.Direction == RelationshipTraversalDirection.Inverse)
        {
            if (declaration.RelationshipKeySemanticPath is { } selectedPath
                && selectedPath != traversal.Definition.SourceReference)
            {
                found.Add(Error(
                    RelationQueryPlacementAuthoringDiagnosticCodes.RelationshipKeyBindingInvalid,
                    $"Selected relationship-key path '{selectedPath}' does not match the inverse traversal's canonical source reference '{traversal.Definition.SourceReference}'.",
                    contract.Input,
                    selectedPath,
                    InputSetting(bindingId, "relationship-key/source-selector")));
            }
            var selector = declaration.RelationshipKeySelector
                ?? options?.RelationshipKeySourceSelector
                ?? DefaultRelationshipKeySelector;
            relationshipKeys = [new(contract.Input, traversal.Definition.SourceReference, selector)];
            decisions.Add(Decision(
                InputSetting(bindingId, "relationship-key/source-selector"),
                declaration.RelationshipKeySelector is not null
                    ? Explicit()
                    : options?.RelationshipKeySourceSelector is not null ? Scoped() : Framework()));
        }
        else if (declaration.RelationshipKeySelector is not null)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.RelationshipKeyBindingInvalid,
                $"Placement input '{contract.Input.Value}' is not an inverse traversal and cannot declare a relationship-key selector.",
                contract.Input,
                setting: InputSetting(bindingId, "relationship-key/source-selector")));
        }
        if (declaration.RelationshipKeyConflict)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.RelationshipKeyBindingInvalid,
                $"Placement input '{contract.Input.Value}' declares more than one relationship-key selector.",
                contract.Input,
                setting: InputSetting(bindingId, "relationship-key/source-selector")));
        }

        RelationQueryPartitionBinding? partition = null;
        if (declaration.PartitionSelector is { } partitionSelector)
        {
            partition = new(partitionSelector);
            decisions.Add(Decision(InputSetting(bindingId, "partition/source-selector"), Explicit()));
        }
        if (declaration.PartitionConflict)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid,
                $"Placement input '{contract.Input.Value}' declares more than one partition selector.",
                contract.Input,
                setting: InputSetting(bindingId, "partition/source-selector")));
        }

        if (found.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) != errorCount)
        {
            return null;
        }

        var binding = new RelationQuerySourcePlacementBinding(
            bindingId,
            contract.Input,
            contract.Node,
            contract.Binding,
            contract.Shape,
            source.Instance.Id,
            contract.Kind == InputContractKind.Source
                ? RelationQuerySourcePlacementBindingKind.SourceSet
                : RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
            acquisition,
            declaration.SelectionExplicit
                ? RelationQuerySourcePlacementOrigin.Explicit
                : RelationQuerySourcePlacementOrigin.Convention,
            identity,
            fields,
            relationshipKeys,
            partition);
        return new(declaration, contract, binding, [.. decisions]);
    }

    ImmutableArray<RelationQuerySourceFieldBinding> BuildFields(
        InputDeclaration declaration,
        Contract contract,
        RelationQuerySourcePlacementBindingId binding,
        ICollection<RelationQueryConfigurationDecision> decisions,
        ICollection<RelationQueryArtifactAuthoringDiagnostic> found)
    {
        var expectedPaths = contract.Fields.Select(static field => field.Input.Field.Path).ToHashSet();
        foreach (var unknown in declaration.FieldSelectors.Keys.Where(path => !expectedPaths.Contains(path)))
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.FieldBindingInvalid,
                $"Semantic path '{unknown}' is not demanded from compiled input '{contract.Input.Value}'.",
                contract.Input,
                unknown,
                InputSetting(binding, $"field/{Encode(unknown.ToString())}/source-selector")));
        }
        foreach (var conflict in declaration.FieldConflicts)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.FieldBindingInvalid,
                $"Semantic path '{conflict}' has more than one explicit physical selector.",
                contract.Input,
                conflict,
                InputSetting(binding, $"field/{Encode(conflict.ToString())}/source-selector")));
        }

        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceFieldBinding>(contract.Fields.Length);
        foreach (var field in contract.Fields)
        {
            var path = field.Input.Field.Path;
            string selector;
            ValueDecision attribution;
            if (declaration.FieldSelectors.TryGetValue(path, out var explicitSelector))
            {
                selector = explicitSelector;
                attribution = Explicit();
            }
            else if (declaration.FieldsBySemanticPathExplicit)
            {
                selector = path.ToString();
                attribution = Explicit();
            }
            else
            {
                try
                {
                    selector = options?.FieldSourceSelector?.Invoke(path) ?? path.ToString();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    found.Add(Error(
                        RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid,
                        $"The scoped field-selector convention failed for semantic path '{path}': {exception.Message}",
                        contract.Input,
                        path));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(selector))
                {
                    found.Add(Error(
                        RelationQueryPlacementAuthoringDiagnosticCodes.FieldBindingInvalid,
                        $"The effective selector for required semantic path '{path}' is empty.",
                        contract.Input,
                        path));
                    continue;
                }
                attribution = options?.FieldSourceSelector is not null ? Scoped() : Framework();
            }

            fields.Add(new(field.Input.Id, path, selector));
            decisions.Add(Decision(
                InputSetting(binding, $"field/{Encode(field.Input.Id.Value)}/source-selector"),
                attribution));
        }

        if (fields.Count != contract.Fields.Length)
        {
            found.Add(Error(
                RelationQueryPlacementAuthoringDiagnosticCodes.FieldBindingInvalid,
                $"Placement input '{contract.Input.Value}' does not bind every exact demanded field.",
                contract.Input));
        }
        return fields.ToImmutable();
    }

    Dictionary<RelationQueryInputId, Contract> CurrentInputs()
    {
        Dictionary<RelationQueryInputId, Contract> inputs = [];
        foreach (var source in plan.InputContract.Sources)
        {
            inputs.Add(source.Input.Id, new(
                InputContractKind.Source,
                source.Input.Id,
                source.Node,
                source.Binding,
                source.Shape,
                source.Fields,
                source,
                Traversal: null));
        }
        foreach (var traversal in plan.InputContract.Traversals)
        {
            inputs.Add(traversal.Input.Id, new(
                InputContractKind.Traversal,
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                traversal.Fields,
                Source: null,
                traversal));
        }
        return inputs;
    }

    static bool HasErrors(IEnumerable<RelationQueryArtifactAuthoringDiagnostic> found) =>
        found.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    internal void RecordConfigurationConflict(InputDeclaration declaration, string setting) =>
        diagnostics.Add(Error(
            RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid,
            $"Placement input declares more than one explicit {setting}.",
            declaration.Input,
            setting: InputSetting(declaration.EffectiveId, setting)));

    ValueDecision Scoped() => new(RelationQueryConfigurationValueOrigin.ScopedProfile, options!.Authority);
    static ValueDecision Explicit() => new(RelationQueryConfigurationValueOrigin.Explicit, ExplicitDeclarationAuthority);
    static ValueDecision Framework() => new(RelationQueryConfigurationValueOrigin.FrameworkDefault, FrameworkDefaultAuthority);

    static RelationQueryConfigurationDecision Decision(string setting, ValueDecision value) =>
        new(setting, value.Origin, value.Authority);

    static RelationQueryArtifactAuthoringDiagnostic Error(
        string code,
        string message,
        RelationQueryInputId? input = null,
        FieldPath? path = null,
        string? setting = null) =>
        new(code, DiagnosticSeverity.Error, message, input, path, setting);

    static string SourceSetting(RelationQuerySourceInstanceId source, string setting) =>
        $"source/{Encode(source.Value)}/{setting}";

    static string InputSetting(RelationQuerySourcePlacementBindingId binding, string setting) =>
        $"placement/{Encode(binding.Value)}/{setting}";

    static string Encode(string value) => Uri.EscapeDataString(value);

    readonly record struct ValueDecision(RelationQueryConfigurationValueOrigin Origin, string Authority);
    internal enum InputContractKind { Source, Traversal }

    sealed record Contract(
        InputContractKind Kind,
        RelationQueryInputId Input,
        QueryNodeId Node,
        ValueBindingId Binding,
        QualifiedShapeId Shape,
        ImmutableArray<RelationQueryFieldInputContract> Fields,
        RelationQuerySourceInputContract? Source,
        RelationQueryTraversalInputContract? Traversal);

    sealed class SourceDeclaration(
        RelationQueryPlacementSourceHandle handle,
        RelationQuerySourceInstance instance,
        ValueDecision id,
        ValueDecision domain,
        ValueDecision limits
        )
    {
        public RelationQueryPlacementSourceHandle Handle { get; } = handle;
        public RelationQuerySourceInstance Instance { get; } = instance;

        public void AppendDecisions(ICollection<RelationQueryConfigurationDecision> decisions)
        {
            decisions.Add(Decision(SourceSetting(Instance.Id, "id"), id));
            decisions.Add(Decision(SourceSetting(Instance.Id, "execution-domain"), domain));
            decisions.Add(Decision(SourceSetting(Instance.Id, "target-profile"), Explicit()));
            decisions.Add(Decision(SourceSetting(Instance.Id, "limits/maximum-batch-size"), limits));
            decisions.Add(Decision(SourceSetting(Instance.Id, "limits/maximum-buffered-rows"), limits));
            decisions.Add(Decision(SourceSetting(Instance.Id, "limits/maximum-fan-out"), limits));
            decisions.Add(Decision(SourceSetting(Instance.Id, "limits/maximum-concurrency"), limits));
        }
    }

    internal class InputDeclaration(
        RelationQueryPlacementBuilder owner,
        RelationQueryInputId? input,
        RelationQueryPlacementSourceHandle source,
        InputContractKind kind,
        object? compiledContract
        )
    {
        readonly Dictionary<FieldPath, string> fieldSelectors = [];
        readonly HashSet<FieldPath> fieldConflicts = [];
        string? identitySelector;
        string? relationshipKeySelector;
        string? partitionSelector;

        public RelationQueryPlacementBuilder Owner { get; } = owner;
        public RelationQueryInputId? Input { get; } = input;
        public RelationQueryPlacementSourceHandle Source { get; } = source;
        public InputContractKind Kind { get; } = kind;
        public object? CompiledContract { get; } = compiledContract;
        public RelationQuerySourcePlacementBindingId? Id { get; set; }
        public RelationQuerySourceAcquisitionKind? Acquisition { get; set; }
        public bool AcquisitionConflict { get; set; }
        public bool IdentityConflict { get; private set; }
        public bool RelationshipKeyConflict { get; private set; }
        public FieldPath? RelationshipKeySemanticPath { get; private set; }
        public bool PartitionConflict { get; private set; }
        public bool FieldsBySemanticPathExplicit { get; private set; }
        public bool SelectionExplicit { get; set; } = true;
        public IReadOnlyDictionary<FieldPath, string> FieldSelectors => fieldSelectors;
        public IReadOnlySet<FieldPath> FieldConflicts => fieldConflicts;
        public string? IdentitySelector => identitySelector;
        public string? RelationshipKeySelector => relationshipKeySelector;
        public string? PartitionSelector => partitionSelector;
        public virtual QualifiedShapeId? ClrShapeId => null;
        public virtual ShapeGraphDocument? ClrShapeDocument => null;
        public RelationQuerySourcePlacementBindingId EffectiveId => Id
            ?? new($"placement/{Encode(Input?.Value ?? "invalid")}");
        public void AddField(FieldPath path, string selector)
        {
            if (!fieldSelectors.TryAdd(path, selector))
            {
                fieldConflicts.Add(path);
            }
        }

        public void SetIdentity(string selector)
        {
            if (identitySelector is not null)
            {
                IdentityConflict = true;
            }
            else
            {
                identitySelector = selector;
            }
        }

        public void SetRelationshipKey(string selector, FieldPath? semanticPath = null)
        {
            if (relationshipKeySelector is not null)
            {
                RelationshipKeyConflict = true;
            }
            else
            {
                relationshipKeySelector = selector;
                RelationshipKeySemanticPath = semanticPath;
            }
        }

        public void SetPartition(string selector)
        {
            if (partitionSelector is not null)
            {
                PartitionConflict = true;
            }
            else
            {
                partitionSelector = selector;
            }
        }

        public void UseFieldsBySemanticPath() => FieldsBySemanticPathExplicit = true;

        public virtual RelationQueryPlacedInput CreatePlacedInput(
            CompiledRelationQueryPlan compiledPlan,
            RelationQuerySourcePlacement placement,
            RelationQuerySourcePlacementBinding binding,
            RelationQuerySourceInstance source,
            ImmutableArray<RelationQueryFieldInputContract> fields) =>
            new(compiledPlan, placement, binding, source, fields);
    }

    internal sealed class TypedInputDeclaration<T>(
        RelationQueryPlacementBuilder owner,
        RelationQueryInputId? input,
        RelationQueryPlacementSourceHandle source,
        InputContractKind kind,
        object? compiledContract,
        RelationQueryClrShape<T> shape
        ) : InputDeclaration(owner, input, source, kind, compiledContract)
        where T : notnull
    {
        public RelationQueryClrShape<T> Shape { get; } = shape;
        public override QualifiedShapeId? ClrShapeId => Shape.Id;
        public override ShapeGraphDocument? ClrShapeDocument => Shape.Document;

        public override RelationQueryPlacedInput CreatePlacedInput(
            CompiledRelationQueryPlan compiledPlan,
            RelationQuerySourcePlacement placement,
            RelationQuerySourcePlacementBinding binding,
            RelationQuerySourceInstance source,
            ImmutableArray<RelationQueryFieldInputContract> fields) =>
            new RelationQueryPlacedInput<T>(compiledPlan, placement, binding, source, fields, Shape);
    }

    sealed record BindingBuild(
        InputDeclaration Declaration,
        Contract Contract,
        RelationQuerySourcePlacementBinding Binding,
        ImmutableArray<RelationQueryConfigurationDecision> Decisions);

    internal static string RequireSelector(string selector, string parameterName) =>
        Guard.RequireNotNullOrWhiteSpace(selector, parameterName);
}

/// <summary>Mutable structural configuration for one exact plan-scoped placement input.</summary>
public class RelationQueryPlacementInputBuilder
{
    readonly RelationQueryPlacementBuilder.InputDeclaration declaration;

    internal RelationQueryPlacementInputBuilder(RelationQueryPlacementBuilder.InputDeclaration declaration) =>
        this.declaration = declaration;

    internal RelationQueryPlacementBuilder.InputDeclaration Declaration => declaration;

    internal RelationQueryPlacementBuilder Owner => declaration.Owner;

    internal RelationQueryInputId? Input => declaration.Input;

    /// <summary>Overrides the deterministic placement-binding identity.</summary>
    /// <param name="id">Explicit non-default identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public virtual RelationQueryPlacementInputBuilder WithId(RelationQuerySourcePlacementBindingId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An explicit placement-binding identity cannot be default.", nameof(id));
        }

        if (Declaration.Id is not null)
        {
            Declaration.Owner.RecordConfigurationConflict(Declaration, "id");
        }
        else
        {
            Declaration.Id = id;
        }

        return this;
    }

    /// <summary>Overrides the convention-selected acquisition mode.</summary>
    /// <param name="acquisition">Explicit acquisition mode.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="acquisition"/> is unsupported.</exception>
    public virtual RelationQueryPlacementInputBuilder WithAcquisition(RelationQuerySourceAcquisitionKind acquisition)
    {
        if (!Enum.IsDefined(acquisition))
        {
            throw new ArgumentOutOfRangeException(nameof(acquisition), acquisition, "Unsupported source acquisition kind.");
        }

        if (Declaration.Acquisition is not null)
        {
            Declaration.AcquisitionConflict = true;
        }
        else
        {
            Declaration.Acquisition = acquisition;
        }

        return this;
    }

    /// <summary>Overrides the observation-identity physical selector.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public virtual RelationQueryPlacementInputBuilder Identity(string sourceSelector)
    {
        Declaration.SetIdentity(RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }

    /// <summary>Overrides one exact demanded field selector by structural semantic path.</summary>
    /// <param name="semanticPath">Exact semantic field path.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="semanticPath"/> is empty or <paramref name="sourceSelector"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    public virtual RelationQueryPlacementInputBuilder Field(FieldPath semanticPath, string sourceSelector)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An explicit field semantic path cannot be empty.", nameof(semanticPath));
        }

        Declaration.AddField(
            semanticPath,
            RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }

    /// <summary>Uses exact semantic path text for every demanded field not explicitly overridden.</summary>
    /// <returns>This builder.</returns>
    public virtual RelationQueryPlacementInputBuilder FieldsBySemanticPath()
    {
        Declaration.UseFieldsBySemanticPath();
        return this;
    }

    /// <summary>Overrides the physical selector for an inverse relationship's canonical reference field.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public virtual RelationQueryPlacementInputBuilder RelationshipKey(string sourceSelector)
    {
        Declaration.SetRelationshipKey(
            RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }

    /// <summary>Declares an optional physical partition selector.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted partition selector.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public virtual RelationQueryPlacementInputBuilder Partition(string sourceSelector)
    {
        Declaration.SetPartition(RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }
}

/// <summary>Mutable typed configuration for one exact CLR-backed plan-scoped placement input.</summary>
/// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
public sealed class RelationQueryPlacementInputBuilder<T> : RelationQueryPlacementInputBuilder
    where T : notnull
{
    readonly RelationQueryPlacementBuilder.TypedInputDeclaration<T> typedDeclaration;

    internal RelationQueryPlacementInputBuilder(RelationQueryPlacementBuilder.TypedInputDeclaration<T> declaration)
        : base(declaration) => typedDeclaration = declaration;

    RelationQueryPlacementBuilder.TypedInputDeclaration<T> TypedDeclaration =>
        typedDeclaration;

    /// <summary>Overrides the deterministic placement-binding identity while preserving typed chaining.</summary>
    /// <param name="id">Explicit non-default identity.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public override RelationQueryPlacementInputBuilder<T> WithId(RelationQuerySourcePlacementBindingId id)
    {
        base.WithId(id);
        return this;
    }

    /// <summary>Overrides the convention-selected acquisition mode while preserving typed chaining.</summary>
    /// <param name="acquisition">Explicit acquisition mode.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="acquisition"/> is unsupported.</exception>
    public override RelationQueryPlacementInputBuilder<T> WithAcquisition(
        RelationQuerySourceAcquisitionKind acquisition)
    {
        base.WithAcquisition(acquisition);
        return this;
    }

    /// <summary>Overrides the observation-identity selector structurally while preserving typed chaining.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public override RelationQueryPlacementInputBuilder<T> Identity(string sourceSelector)
    {
        base.Identity(sourceSelector);
        return this;
    }

    /// <summary>Overrides one demanded field structurally while preserving typed chaining.</summary>
    /// <param name="semanticPath">Exact demanded semantic path.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path or selector is empty.</exception>
    public override RelationQueryPlacementInputBuilder<T> Field(
        FieldPath semanticPath,
        string sourceSelector)
    {
        base.Field(semanticPath, sourceSelector);
        return this;
    }

    /// <summary>Explicitly maps otherwise-unmapped demanded fields by exact semantic path text.</summary>
    /// <returns>This typed builder.</returns>
    public override RelationQueryPlacementInputBuilder<T> FieldsBySemanticPath()
    {
        base.FieldsBySemanticPath();
        return this;
    }

    /// <summary>Overrides the inverse relationship-key selector structurally while preserving typed chaining.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public override RelationQueryPlacementInputBuilder<T> RelationshipKey(string sourceSelector)
    {
        base.RelationshipKey(sourceSelector);
        return this;
    }

    /// <summary>Overrides the partition selector structurally while preserving typed chaining.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty.</exception>
    public override RelationQueryPlacementInputBuilder<T> Partition(string sourceSelector)
    {
        base.Partition(sourceSelector);
        return this;
    }

    /// <summary>Overrides the identity selector using authoritative CLR member metadata.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <param name="sourceSelector">
    /// Explicit physical selector, or <see langword="null"/> to use the resolved semantic path text.
    /// </param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector is invalid or <paramref name="sourceSelector"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selector.</exception>
    public RelationQueryPlacementInputBuilder<T> Identity<TValue>(
        Expression<Func<T, TValue>> selector,
        string? sourceSelector = null)
    {
        var path = TypedDeclaration.Shape.ResolveMemberPath(RelationQueryPlacedInput<T>.ReadProperties(selector, nameof(selector)));
        TypedDeclaration.SetIdentity(sourceSelector is null
            ? path.ToString()
            : RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }

    /// <summary>Overrides one exact demanded field using authoritative CLR member metadata.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector is invalid or <paramref name="sourceSelector"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selector.</exception>
    public RelationQueryPlacementInputBuilder<T> Field<TValue>(
        Expression<Func<T, TValue>> selector,
        string sourceSelector)
    {
        var path = TypedDeclaration.Shape.ResolveMemberPath(RelationQueryPlacedInput<T>.ReadProperties(selector, nameof(selector)));
        TypedDeclaration.AddField(
            path,
            RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }

    /// <summary>Overrides an inverse-relationship selector using authoritative CLR member metadata.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <param name="sourceSelector">
    /// Explicit physical selector, or <see langword="null"/> to use the resolved semantic path text.
    /// </param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector is invalid or <paramref name="sourceSelector"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selector.</exception>
    public RelationQueryPlacementInputBuilder<T> RelationshipKey<TValue>(
        Expression<Func<T, TValue>> selector,
        string? sourceSelector = null)
    {
        var path = TypedDeclaration.Shape.ResolveMemberPath(RelationQueryPlacedInput<T>.ReadProperties(selector, nameof(selector)));
        TypedDeclaration.SetRelationshipKey(
            sourceSelector is null
                ? path.ToString()
                : RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)),
            path);
        return this;
    }

    /// <summary>Overrides the partition selector using authoritative CLR member metadata.</summary>
    /// <typeparam name="TValue">CLR value selected by the property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property chain rooted at its parameter.</param>
    /// <param name="sourceSelector">
    /// Explicit physical selector, or <see langword="null"/> to use the resolved semantic path text.
    /// </param>
    /// <returns>This typed builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector is invalid or <paramref name="sourceSelector"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The CLR metadata profile cannot resolve the selector.</exception>
    public RelationQueryPlacementInputBuilder<T> Partition<TValue>(
        Expression<Func<T, TValue>> selector,
        string? sourceSelector = null)
    {
        var path = TypedDeclaration.Shape.ResolveMemberPath(
            RelationQueryPlacedInput<T>.ReadProperties(selector, nameof(selector)));
        TypedDeclaration.SetPartition(sourceSelector is null
            ? path.ToString()
            : RelationQueryPlacementBuilder.RequireSelector(sourceSelector, nameof(sourceSelector)));
        return this;
    }
}

/// <summary>Complete authored placement artifact and its immutable plan-bound input views.</summary>
public sealed class RelationQueryAuthoredPlacement
{
    readonly IReadOnlyDictionary<RelationQueryInputId, RelationQueryPlacedInput> inputsById;
    readonly IReadOnlyDictionary<RelationQuerySourcePlacementBindingId, RelationQueryPlacedInput> inputsByBinding;
    readonly IReadOnlyDictionary<RelationQueryPlacementBuilder.InputDeclaration, RelationQueryPlacedInput>
        inputsByDeclaration;

    internal RelationQueryAuthoredPlacement(
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement,
        ImmutableArray<RelationQueryPlacedInput> inputs,
        ImmutableArray<RelationQueryPlacementBuilder.InputDeclaration> declarations)
    {
        Plan = Guard.RequireNotNull(plan);
        Placement = Guard.RequireNotNull(placement);
        Inputs = inputs;
        inputsById = Inputs.ToDictionary(static input => input.Binding.Input);
        inputsByBinding = Inputs.ToDictionary(static input => input.Binding.Id);
        inputsByDeclaration = declarations.ToDictionary(
            static declaration => declaration,
            declaration => inputsById[declaration.Input!.Value]);
    }

    /// <summary>Exact demand-scoped compiled plan owning the placement.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Normalized portable source-placement artifact.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    /// <summary>Placed source and traversal inputs in deterministic compiled-input order.</summary>
    public ImmutableArray<RelationQueryPlacedInput> Inputs { get; }

    /// <summary>Resolves one placed input by exact compiled input identity.</summary>
    /// <param name="input">Compiled source-set or traversal input identity.</param>
    /// <returns>The plan-bound placed-input view.</returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not placed.</exception>
    public RelationQueryPlacedInput GetInput(RelationQueryInputId input)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A compiled input identity cannot be default.", nameof(input));
        }

        return inputsById.TryGetValue(input, out var placed)
            ? placed
            : throw new KeyNotFoundException($"Compiled input '{input.Value}' is not placed.");
    }

    /// <summary>Resolves one placed input by exact placement-binding identity.</summary>
    /// <param name="binding">Plan-scoped placement-binding identity.</param>
    /// <returns>The plan-bound placed-input view.</returns>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="binding"/> is absent.</exception>
    public RelationQueryPlacedInput GetInput(RelationQuerySourcePlacementBindingId binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            throw new ArgumentException("A placement-binding identity cannot be default.", nameof(binding));
        }

        return inputsByBinding.TryGetValue(binding, out var placed)
            ? placed
            : throw new KeyNotFoundException($"Placement binding '{binding.Value}' is absent.");
    }

    /// <summary>Resolves the placed input retained by one structural declaration handle.</summary>
    /// <param name="authoredInput">Input declaration returned by the builder that produced this artifact.</param>
    /// <returns>The declaration's plan-bound placed-input view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authoredInput"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authoredInput"/> does not belong to the exact builder snapshot that produced this artifact.
    /// </exception>
    public RelationQueryPlacedInput GetInput(RelationQueryPlacementInputBuilder authoredInput)
    {
        ArgumentNullException.ThrowIfNull(authoredInput);
        return inputsByDeclaration.TryGetValue(authoredInput.Declaration, out var placed)
            ? placed
            : throw new ArgumentException(
                "An authored input handle must belong to the exact builder snapshot that produced this placement.",
                nameof(authoredInput));
    }

    /// <summary>Resolves a CLR-backed placed input by exact compiled input identity.</summary>
    /// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
    /// <param name="input">Compiled source-set or traversal input identity.</param>
    /// <returns>The typed plan-bound placed-input view.</returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is not placed.</exception>
    /// <exception cref="InvalidOperationException">The input was not authored with CLR type <typeparamref name="T"/>.</exception>
    public RelationQueryPlacedInput<T> GetInput<T>(RelationQueryInputId input)
        where T : notnull =>
        GetInput(input) as RelationQueryPlacedInput<T>
        ?? throw new InvalidOperationException(
            $"Placed input '{input.Value}' was not authored with CLR type '{typeof(T)}'.");

    /// <summary>Resolves the typed placed input retained by one typed declaration handle.</summary>
    /// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
    /// <param name="authoredInput">Typed declaration returned by the builder that produced this artifact.</param>
    /// <returns>The declaration's typed plan-bound placed-input view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authoredInput"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authoredInput"/> does not belong to the exact builder snapshot that produced this artifact.
    /// </exception>
    /// <exception cref="InvalidOperationException">The retained input is not CLR-backed by <typeparamref name="T"/>.</exception>
    public RelationQueryPlacedInput<T> GetInput<T>(RelationQueryPlacementInputBuilder<T> authoredInput)
        where T : notnull =>
        GetInput((RelationQueryPlacementInputBuilder)authoredInput) as RelationQueryPlacedInput<T>
        ?? throw new InvalidOperationException(
            $"The authored input was not retained with CLR type '{typeof(T)}'.");
}
