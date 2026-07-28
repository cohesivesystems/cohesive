using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// Stable diagnostic codes emitted while adapting transition expression sites to shared expression analysis.
/// </summary>
public static class TransitionExpressionAnalysisDiagnosticCodes
{
    /// <summary>The analyzed entity definition has no stable logical identity.</summary>
    public const string EntityIdentityMissing = "transitions.expression.entity.identityMissing";

    /// <summary>The analyzed entity definition has no canonical shape.</summary>
    public const string EntityShapeMissing = "transitions.expression.entity.shapeMissing";

    /// <summary>The canonical entity shape contains invalid field value-contract metadata.</summary>
    public const string EntityShapeInvalid = "transitions.expression.entity.shapeInvalid";

    /// <summary>Two transition expression sites resolve to the same semantic site identity.</summary>
    public const string DuplicateSiteIdentity = "transitions.expression.site.duplicate";

    /// <summary>A field update names a target field that the owning entity does not declare.</summary>
    public const string UpdateTargetMissing = "transitions.expression.update.targetMissing";

    /// <summary>A transition definition collection contains a missing entry.</summary>
    public const string DefinitionEntryMissing = "transitions.expression.definition.entryMissing";

    /// <summary>A transition expression definition has no usable semantic identity.</summary>
    public const string DefinitionIdentityMissing = "transitions.expression.definition.identityMissing";

    /// <summary>A transition expression definition identity is ambiguous because it is declared more than once.</summary>
    public const string DefinitionIdentityDuplicate = "transitions.expression.definition.identityDuplicate";

    /// <summary>A transition input declaration has no semantic type.</summary>
    public const string InputTypeMissing = "transitions.expression.input.typeMissing";
}

/// <summary>
/// Adapts transition, computed-field, and entity-invariant expressions to the shared portable expression analyzer.
/// </summary>
/// <remarks>
/// This analyzer models semantic expression scope and interpreter capabilities. It does not execute transitions or
/// replace <see cref="DeclarativeEntityRuntime"/>. Transition preconditions and updates expose declared transition
/// inputs, while computed fields and entity invariants intentionally expose only entity state.
/// </remarks>
public static class TransitionExpressionAnalyzer
{
    static readonly ExprDependencyKind TransitionDependencies =
        ExprDependencyKind.Binding | ExprDependencyKind.Parameter | ExprDependencyKind.Ambient;

    static readonly ExprDependencyKind EntityStateDependencies =
        ExprDependencyKind.Binding | ExprDependencyKind.Ambient;

    /// <summary>Stable binding used for the entity state visible at every transition expression site.</summary>
    public static ValueBindingId EntityStateBinding { get; } = new("transition.entityState");

    /// <summary>
    /// Analyzes all precondition, update, computed-field, and entity-invariant expression sites in an entity definition.
    /// </summary>
    /// <param name="entityDefinition">Entity definition that owns the expression sites.</param>
    /// <returns>
    /// Deterministically ordered per-site analyses together with combined requirements and structured validation.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityDefinition"/> is <see langword="null"/>.</exception>
    public static TransitionExpressionAnalysisResult Analyze(EntityDefinition entityDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityDefinition);
        var semantics = ExprSemanticsCatalog.Default;
        List<DocumentValidationDiagnostic> adapterDiagnostics = [];
        if (string.IsNullOrWhiteSpace(entityDefinition.Name.Value))
        {
            adapterDiagnostics.Add(new(
                Code: TransitionExpressionAnalysisDiagnosticCodes.EntityIdentityMissing,
                Severity: DiagnosticSeverity.Error,
                Message: "An entity definition must have a non-empty logical identity.",
                Location: "/entity/name"));
        }
        if (entityDefinition.Shape is null)
        {
            adapterDiagnostics.Add(new(
                Code: TransitionExpressionAnalysisDiagnosticCodes.EntityShapeMissing,
                Severity: DiagnosticSeverity.Error,
                Message: "An entity definition must have a canonical shape.",
                Location: "/entity/shape"));
            return new(
                [],
                ExprRequirements.Empty,
                CombineDeterministically(
                [
                    DocumentValidationResult.FromDiagnostics(adapterDiagnostics)
                ]));
        }
        if (!ValidateEntityShape(entityDefinition.Shape, adapterDiagnostics))
        {
            return new(
                [],
                ExprRequirements.Empty,
                CombineDeterministically(
                [
                    DocumentValidationResult.FromDiagnostics(adapterDiagnostics)
                ]));
        }

        var entityScope = CreateEntityScope(entityDefinition);
        var profile = CreateRuntimeCapabilityProfile();
        List<SiteCandidate> candidates = [];
        AddComputedFieldSites(candidates, adapterDiagnostics, entityDefinition, entityScope, profile);
        AddEntityInvariantSites(candidates, adapterDiagnostics, entityDefinition, entityScope, profile);
        AddTransitionSites(candidates, adapterDiagnostics, entityDefinition, entityScope, profile);

        List<ExprAnalysisResult> sites = [];
        foreach (var group in candidates
                     .GroupBy(static candidate => candidate.Site.Id)
                     .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
        {
            var groupedCandidates = group.ToArray();
            if (groupedCandidates.Length > 1)
            {
                adapterDiagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.DuplicateSiteIdentity,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Transition expression site identity '{group.Key.Value}' is declared more than once.",
                    Location: groupedCandidates[0].Site.DiagnosticLocation));
                continue;
            }

            var candidate = groupedCandidates[0];
            sites.Add(ExprAnalyzer.Analyze(candidate.Site, semantics));
        }

        var orderedSites = sites
            .OrderBy(static site => site.Site.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var requirements = ExprRequirements.Combine(orderedSites.Select(static site => site.Requirements));
        var validation = CombineDeterministically(
            orderedSites
                .Select(static site => site.Validation)
                .Prepend(DocumentValidationResult.FromDiagnostics(adapterDiagnostics)));
        return new(orderedSites, requirements, validation);
    }

    static bool ValidateEntityShape(
        Shape shape,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var isValid = true;
        foreach (var field in shape.Fields)
        {
            var location = $"/entity/shape/fields/{Encode(field.Name.Value)}";
            if (field.Type is null)
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.EntityShapeInvalid,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Entity field '{field.Name.Value}' must declare a semantic type.",
                    Location: $"{location}/type"));
                isValid = false;
            }
            if (!Enum.IsDefined(field.Cardinality))
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.EntityShapeInvalid,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Entity field '{field.Name.Value}' has unsupported cardinality '{((int)field.Cardinality).ToString(CultureInfo.InvariantCulture)}'.",
                    Location: $"{location}/cardinality"));
                isValid = false;
            }
            if (!Enum.IsDefined(field.Presence))
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.EntityShapeInvalid,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Entity field '{field.Name.Value}' has unsupported presence '{((int)field.Presence).ToString(CultureInfo.InvariantCulture)}'.",
                    Location: $"{location}/presence"));
                isValid = false;
            }
            if (!Enum.IsDefined(field.Nullability))
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.EntityShapeInvalid,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Entity field '{field.Name.Value}' has unsupported nullability '{((int)field.Nullability).ToString(CultureInfo.InvariantCulture)}'.",
                    Location: $"{location}/nullability"));
                isValid = false;
            }
        }

        return isValid;
    }

    static void AddComputedFieldSites(
        ICollection<SiteCandidate> candidates,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        EntityDefinition entityDefinition,
        ExprScope entityScope,
        ExprCapabilityProfile profile)
    {
        var entityPrefix = EntityPrefix(entityDefinition);
        foreach (var field in entityDefinition.Fields
                     .Where(static field => field.Compute is not null)
                     .OrderBy(static field => field.Name.Value, StringComparer.Ordinal))
        {
            var id = $"{entityPrefix}/computed/{Encode(field.Name.Value)}";
            AddSite(
                candidates,
                diagnostics,
                id,
                field.Compute!.Expression,
                entityScope,
                CreateFieldExpectation(field, EntityStateDependencies),
                profile,
                $"/entity/shape/fields/{field.Name.Value}/compute/expression");
        }
    }

    static void AddEntityInvariantSites(
        ICollection<SiteCandidate> candidates,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        EntityDefinition entityDefinition,
        ExprScope entityScope,
        ExprCapabilityProfile profile)
    {
        var entityPrefix = EntityPrefix(entityDefinition);
        var invariants = entityDefinition.Invariants.IsDefault ? [] : entityDefinition.Invariants;
        ReportNullEntries(invariants, "/entity/invariants", diagnostics);
        foreach (var invariant in SelectUnambiguous(
                     invariants,
                     static invariant => invariant.Name,
                     "/entity/invariants",
                     "entity invariant",
                     diagnostics))
        {
            var id = $"{entityPrefix}/invariant/{Encode(invariant.Name)}";
            AddSite(
                candidates,
                diagnostics,
                id,
                invariant.Expression,
                entityScope,
                BooleanExpectation(EntityStateDependencies),
                profile,
                $"/entity/invariants/{invariant.Name}/expression");
        }
    }

    static void AddTransitionSites(
        ICollection<SiteCandidate> candidates,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        EntityDefinition entityDefinition,
        ExprScope entityScope,
        ExprCapabilityProfile profile)
    {
        var entityPrefix = EntityPrefix(entityDefinition);
        var transitions = entityDefinition.Transitions.IsDefault ? [] : entityDefinition.Transitions;
        ReportNullEntries(transitions, "/entity/transitions", diagnostics);
        foreach (var transition in SelectUnambiguous(
                     transitions,
                     static transition => transition.Name,
                     "/entity/transitions",
                     "transition",
                     diagnostics))
        {
            var inputs = transition.Inputs.IsDefault ? [] : transition.Inputs;
            ReportNullEntries(
                inputs,
                $"/entity/transitions/{transition.Name}/inputs",
                diagnostics);
            var transitionScope = CreateTransitionScope(
                entityScope,
                transition,
                inputs,
                diagnostics);
            var transitionPrefix = $"{entityPrefix}/transition/{Encode(transition.Name)}";
            var preconditions = transition.Preconditions.IsDefault ? [] : transition.Preconditions;
            ReportNullEntries(
                preconditions,
                $"/entity/transitions/{transition.Name}/preconditions",
                diagnostics);
            foreach (var precondition in SelectUnambiguous(
                         preconditions,
                         static precondition => precondition.Name,
                         $"/entity/transitions/{transition.Name}/preconditions",
                         "transition precondition",
                         diagnostics))
            {
                var id = $"{transitionPrefix}/precondition/{Encode(precondition.Name)}";
                AddSite(
                    candidates,
                    diagnostics,
                    id,
                    precondition.Expression,
                    transitionScope,
                    BooleanExpectation(TransitionDependencies),
                    profile,
                    $"/entity/transitions/{transition.Name}/preconditions/{precondition.Name}/expression");
            }

            var updates = transition.Updates.IsDefault ? [] : transition.Updates;
            ReportNullEntries(
                updates,
                $"/entity/transitions/{transition.Name}/updates",
                diagnostics);
            foreach (var update in SelectUnambiguous(
                         updates,
                         static update => update.Field,
                         $"/entity/transitions/{transition.Name}/updates",
                         "field update",
                         diagnostics))
            {
                var id = $"{transitionPrefix}/update/{Encode(update.Field)}";
                var location = $"/entity/transitions/{transition.Name}/updates/{update.Field}/valueExpression";
                ExprExpectation expectation;
                if (entityDefinition.Shape.TryGetField(update.Field, out var target))
                {
                    expectation = CreateFieldExpectation(target, TransitionDependencies);
                }
                else
                {
                    expectation = new(allowedDependencies: TransitionDependencies);
                    diagnostics.Add(new(
                        Code: TransitionExpressionAnalysisDiagnosticCodes.UpdateTargetMissing,
                        Severity: DiagnosticSeverity.Error,
                        Message: $"Transition '{transition.Name}' updates unknown entity field '{update.Field}'.",
                        Location: location));
                }

                AddSite(
                    candidates,
                    diagnostics,
                    id,
                    update.ValueExpression,
                    transitionScope,
                    expectation,
                    profile,
                    location);
            }
        }
    }

    static void AddSite(
        ICollection<SiteCandidate> candidates,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string id,
        Expr? expression,
        ExprScope scope,
        ExprExpectation expectation,
        ExprCapabilityProfile profile,
        string location)
    {
        if (expression is null)
        {
            diagnostics.Add(new(
                Code: ExprAnalysisDiagnosticCodes.ExpressionMissing,
                Severity: DiagnosticSeverity.Error,
                Message: $"Transition expression site '{id}' must contain an expression.",
                Location: location));
            return;
        }

        candidates.Add(new(new(
            new(id),
            expression,
            scope,
            expectation,
            profile,
            location)));
    }

    static ExprScope CreateEntityScope(EntityDefinition entityDefinition)
    {
        var binding = new ExprScopeBinding(
            EntityStateBinding,
            ValueContract.FromShape(entityDefinition.Shape));
        return new(
            bindings: [binding],
            implicitBinding: binding.Id,
            ambientCapabilities: [ExprCapabilities.EntityIdentity]);
    }

    static ExprScope CreateTransitionScope(
        ExprScope entityScope,
        TransitionDefinition transition,
        ImmutableArray<TransitionParameterDefinition> inputs,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        List<ExprScopeParameter> parameters = [];
        foreach (var parameter in SelectUnambiguous(
                     inputs,
                     static parameter => parameter.Name,
                     $"/entity/transitions/{transition.Name}/inputs",
                     "transition input",
                     diagnostics))
        {
            if (parameter.Type is null)
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.InputTypeMissing,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Transition input '{parameter.Name}' must declare a semantic type.",
                    Location: $"/entity/transitions/{transition.Name}/inputs/{parameter.Name}/type"));
                continue;
            }

            parameters.Add(new(
                parameter.Name,
                parameter.Type,
                parameter.IsRequired ? FieldPresence.Required : FieldPresence.Optional));
        }

        return new(
            entityScope.Bindings,
            entityScope.ImplicitBinding,
            parameters,
            ambientCapabilities: entityScope.AmbientCapabilities);
    }

    static ExprCapabilityProfile CreateRuntimeCapabilityProfile()
    {
        var semantics = ExprSemanticsCatalog.Default;
        var capabilities = new List<ExprCapabilityId>
        {
            ExprCapabilities.Field,
            ExprCapabilities.Parameter,
            ExprCapabilities.Constant,
            ExprCapabilities.Conditional
        };
        capabilities.AddRange(semantics.UnaryOperators
            .Where(static definition => Enum.IsDefined(definition.Operator))
            .Select(static definition => definition.OperationCapability));
        capabilities.AddRange(semantics.BinaryOperators
            .Where(static definition => Enum.IsDefined(definition.Operator))
            .Select(static definition => definition.OperationCapability));
        capabilities.AddRange(semantics.Functions
            .Where(definition => DeclarativeEntityRuntime.SupportedExpressionFunctions.Contains(definition.Id.Value))
            .Select(static definition => definition.OperationCapability));
        return new(capabilities);
    }

    static ExprExpectation BooleanExpectation(ExprDependencyKind allowedDependencies) => new(
        ExprResultCategory.Boolean,
        ExprExpectation.Boolean.Value,
        allowedDependencies);

    static ExprExpectation CreateFieldExpectation(
        FieldDefinition field,
        ExprDependencyKind allowedDependencies)
    {
        var contract = ValueContract.FromField(field);
        var category = contract.GetResultCategory() == ExprResultCategory.Integer
            ? ExprResultCategory.Numeric
            : contract.GetResultCategory();
        return new(category, contract, allowedDependencies);
    }

    static ImmutableArray<T> SelectUnambiguous<T>(
        ImmutableArray<T> values,
        Func<T, string?> identity,
        string location,
        string description,
        ICollection<DocumentValidationDiagnostic> diagnostics)
        where T : class
    {
        var candidates = (values.IsDefault ? [] : values)
            .Select(static (value, index) => (Value: value, Index: index))
            .Where(static candidate => candidate.Value is not null)
            .ToArray();
        foreach (var (value, index) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(identity(value!)))
                continue;
            diagnostics.Add(new(
                Code: TransitionExpressionAnalysisDiagnosticCodes.DefinitionIdentityMissing,
                Severity: DiagnosticSeverity.Error,
                Message: $"A {description} must have a non-empty identity.",
                Location: $"{location}/{index}/name"));
        }

        List<T> result = [];
        foreach (var group in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(identity(candidate.Value!)))
                     .GroupBy(candidate => identity(candidate.Value!)!, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var grouped = group.Take(2).ToArray();
            if (grouped.Length > 1)
            {
                diagnostics.Add(new(
                    Code: TransitionExpressionAnalysisDiagnosticCodes.DefinitionIdentityDuplicate,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"The {description} identity '{group.Key}' is declared more than once.",
                    Location: $"{location}/{Encode(group.Key)}"));
                continue;
            }

            result.Add(grouped[0].Value!);
        }

        return [.. result];
    }

    static void ReportNullEntries<T>(
        ImmutableArray<T> values,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
        where T : class
    {
        if (values.IsDefault)
            return;

        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not null)
                continue;
            diagnostics.Add(new(
                Code: TransitionExpressionAnalysisDiagnosticCodes.DefinitionEntryMissing,
                Severity: DiagnosticSeverity.Error,
                Message: "A transition expression definition collection contains a missing entry.",
                Location: $"{location}/{index}"));
        }
    }

    static DocumentValidationResult CombineDeterministically(IEnumerable<DocumentValidationResult> results) =>
        DocumentValidationResult.FromDiagnostics(results
            .SelectMany(static result => result.Diagnostics)
            .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.SchemaLocation, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal));

    static string EntityPrefix(EntityDefinition entityDefinition) =>
        $"transitions/entity/{Encode(entityDefinition.Name.Value)}";

    static string Encode(string? value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value) ? "missing" : value);

    sealed record SiteCandidate(ExprSite Site);
}

/// <summary>
/// Deterministic shared expression analysis for an entity's transition, computed-field, and invariant sites.
/// </summary>
public sealed class TransitionExpressionAnalysisResult
{
    /// <summary>Creates a transition expression-analysis result.</summary>
    /// <param name="sites">Per-site shared expression analyses.</param>
    /// <param name="requirements">Combined requirements derived from all unambiguous sites.</param>
    /// <param name="validation">Combined adapter and shared expression diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="requirements"/> or <paramref name="validation"/> is <see langword="null"/>.
    /// </exception>
    internal TransitionExpressionAnalysisResult(
        ImmutableArray<ExprAnalysisResult> sites,
        ExprRequirements requirements,
        DocumentValidationResult validation)
    {
        Sites = sites.IsDefault ? [] : sites;
        Requirements = Guard.RequireNotNull(requirements);
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Per-site expression analyses sorted by stable semantic site identity.</summary>
    public ImmutableArray<ExprAnalysisResult> Sites { get; }

    /// <summary>Deterministic union of requirements derived from all unambiguous expression sites.</summary>
    public ExprRequirements Requirements { get; }

    /// <summary>Combined adapter and shared expression validation.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Combined structured diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Whether all adapted sites and expressions satisfy their declared scope and runtime profile.</summary>
    public bool IsValid => Validation.IsValid;
}
