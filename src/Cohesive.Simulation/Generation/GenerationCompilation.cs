using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Immutable executable index over one exact canonical generation definition.</summary>
/// <remarks>
/// The plan retains the supplied definition as semantic authority and adds deterministic ordering and qualified shape
/// evidence for interpreters. It contains no seed or generated runtime state.
/// </remarks>
public sealed class CompiledGenerationPlan
{
    internal CompiledGenerationPlan(
        GenerationDefinition definition,
        GraphShapeId outputShape,
        ImmutableArray<RecordGenerationMember> members,
        string fingerprint)
    {
        Definition = definition;
        OutputShape = outputShape;
        Members = members;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the exact canonical generation definition.</summary>
    public GenerationDefinition Definition { get; }

    /// <summary>Gets the exact graph-scoped shape governing generated observations.</summary>
    public GraphShapeId OutputShape { get; }

    /// <summary>Gets generated members ordered by stable semantic identity.</summary>
    public ImmutableArray<RecordGenerationMember> Members { get; }

    /// <summary>Gets the lowercase SHA-256 semantic fingerprint of this plan's generation content.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the fingerprint algorithm identity.</summary>
    public string FingerprintAlgorithm => GenerationCanonicalizer.FingerprintAlgorithm;

    /// <summary>Gets the canonicalization profile used by <see cref="Fingerprint"/>.</summary>
    public string FingerprintCanonicalization => GenerationCanonicalizer.CanonicalizationProfile;
}

/// <summary>Result of attempting target-independent generation compilation.</summary>
public sealed class GenerationCompilationResult
{
    internal GenerationCompilationResult(
        GenerationDefinition definition,
        CompiledGenerationPlan? plan,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Gets the exact supplied canonical definition.</summary>
    public GenerationDefinition Definition { get; }

    /// <summary>Gets an executable plan only when validation succeeds.</summary>
    public CompiledGenerationPlan? Plan { get; }

    /// <summary>Gets deterministically ordered structured diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether compilation produced a complete plan.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}

/// <summary>Compiles canonical generator IR into a provider-neutral executable plan.</summary>
public static class GenerationCompiler
{
    /// <summary>Compiles and validates one canonical generation definition.</summary>
    /// <param name="definition">Canonical definition to compile.</param>
    /// <returns>A result containing either a complete plan or precise structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static GenerationCompilationResult Compile(GenerationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];

        foreach (var graphDiagnostic in definition.ShapeGraph.Diagnostics)
        {
            Add(
                diagnostics,
                code: $"simulation.shapeGraph.{graphDiagnostic.Id.Value}",
                message: graphDiagnostic.Message,
                location: graphDiagnostic.FieldIdentity is null
                    ? "/shapeGraph"
                    : $"/shapeGraph/shapes/{graphDiagnostic.ShapeId?.Value}/fields/{graphDiagnostic.FieldIdentity}",
                severity: graphDiagnostic.Severity);
        }

        var root = definition.Root;
        if (string.IsNullOrWhiteSpace(root.ShapeId.Value)
            || !definition.ShapeGraph.TryGetShape(root.ShapeId, out var shape))
        {
            Add(
                diagnostics,
                code: "simulation.generation.outputShapeMissing",
                message: $"Generation root shape '{root.ShapeId.Value}' is absent from graph '{definition.ShapeGraph.Id.Value}'.",
                location: "/root/shapeId");
            return Invalid(definition, diagnostics);
        }

        if (root.Members.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.generation.membersMissing",
                message: "A record generator must contain at least one generated member.",
                location: "/root/members");
        }

        Dictionary<string, (RecordGenerationMember Member, int Index)> membersByIdentity =
            new(StringComparer.Ordinal);
        for (var index = 0; index < root.Members.Length; index++)
        {
            var member = root.Members[index];
            var location = $"/root/members/{index}";
            if (member is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.memberMissing",
                    message: "A record generator cannot contain a null member.",
                    location: location);
                continue;
            }

            if (string.IsNullOrWhiteSpace(member.Identity.Value))
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.memberIdentityMissing",
                    message: "A generated member requires a stable semantic identity.",
                    location: $"{location}/identity");
                continue;
            }

            if (!membersByIdentity.TryAdd(member.Identity.Value, (member, index)))
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.memberIdentityDuplicate",
                    message: $"Generated member identity '{member.Identity.Value}' is declared more than once.",
                    location: $"{location}/identity");
                continue;
            }

            if (!shape.TryGetField(member.Identity.Value, out var field))
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.memberShapeFieldMissing",
                    message: $"Generated member '{member.Identity.Value}' has no field in output shape '{shape.Id.Value}'.",
                    location: $"{location}/identity");
                continue;
            }

            ValidateFieldContract(field, member.Generator, definition.ShapeGraph, location, diagnostics);
        }

        for (var fieldIndex = 0; fieldIndex < shape.Fields.Length; fieldIndex++)
        {
            var field = shape.Fields[fieldIndex];
            if (!membersByIdentity.ContainsKey(field.Name.Value))
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.shapeFieldBindingMissing",
                    message: $"Output shape field '{field.Name.Value}' has no generated member binding.",
                    location: $"/shapeGraph/shapes/{shape.Id.Value}/fields/{fieldIndex}");
            }
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return Invalid(definition, diagnostics);

        var orderedMembers = root.Members
            .OrderBy(static member => member.Identity.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var outputShape = new GraphShapeId(definition.ShapeGraph, root.ShapeId);
        var fingerprint = GenerationCanonicalizer.ComputeDefinitionFingerprint(definition);
        var validation = CreateValidation(diagnostics);
        return new(
            definition,
            new CompiledGenerationPlan(definition, outputShape, orderedMembers, fingerprint),
            validation);
    }

    static void ValidateFieldContract(
        FieldDefinition field,
        ValueGeneratorNode generator,
        ShapeGraph graph,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (field.Cardinality != FieldCardinality.Single
            || field.Presence != FieldPresence.Required
            || field.Nullability != FieldNullability.NonNullable)
        {
            Add(
                diagnostics,
                code: "simulation.generation.fieldContractUnsupported",
                message: $"Generated field '{field.Name.Value}' must be required, non-nullable, and single-valued in this generation profile.",
                location: $"{location}/generator");
        }

        if (!AreEquivalent(field.Type, generator.ValueType))
        {
            Add(
                diagnostics,
                code: "simulation.generation.fieldTypeMismatch",
                message: $"Generator for field '{field.Name.Value}' produces '{Describe(generator.ValueType)}', not shape type '{Describe(field.Type)}'.",
                location: $"{location}/generator/valueType");
        }

        switch (generator)
        {
            case ConstantGenerationNode constant:
                ValidateValue(field, constant.Value, graph, $"{location}/generator/value", diagnostics);
                break;

            case Int32GenerationNode integer when integer.Minimum > integer.Maximum:
                Add(
                    diagnostics,
                    code: "simulation.generation.int32RangeInvalid",
                    message: $"Int32 generator minimum '{Format(integer.Minimum)}' exceeds maximum '{Format(integer.Maximum)}'.",
                    location: $"{location}/generator");
                break;

            case BernoulliGenerationNode bernoulli
                when !double.IsFinite(bernoulli.Probability)
                     || bernoulli.Probability is < 0d or > 1d:
                Add(
                    diagnostics,
                    code: "simulation.generation.bernoulliProbabilityInvalid",
                    message: $"Bernoulli probability '{Format(bernoulli.Probability)}' must be finite and from 0 through 1.",
                    location: $"{location}/generator/probability");
                break;

            case WeightedCategoricalGenerationNode categorical:
                ValidateCategorical(field, categorical, graph, location, diagnostics);
                break;

            case ConstantGenerationNode or Int32GenerationNode or BernoulliGenerationNode:
                break;

            default:
                Add(
                    diagnostics,
                    code: "simulation.generation.nodeUnsupported",
                    message: $"Generator node '{generator.GetType().Name}' is not supported by this compiler.",
                    location: $"{location}/generator");
                break;
        }
    }

    static void ValidateCategorical(
        FieldDefinition field,
        WeightedCategoricalGenerationNode categorical,
        ShapeGraph graph,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (categorical.Options.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.generation.categoricalOptionsMissing",
                message: "A weighted categorical generator requires at least one option.",
                location: $"{location}/generator/options");
            return;
        }

        var totalWeight = 0d;
        for (var index = 0; index < categorical.Options.Length; index++)
        {
            var option = categorical.Options[index];
            if (option is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.categoricalOptionMissing",
                    message: "A weighted categorical generator cannot contain a null option.",
                    location: $"{location}/generator/options/{index}");
                continue;
            }

            if (!double.IsFinite(option.Weight) || option.Weight <= 0d)
            {
                Add(
                    diagnostics,
                    code: "simulation.generation.categoricalWeightInvalid",
                    message: $"Categorical option weight '{Format(option.Weight)}' must be finite and positive.",
                    location: $"{location}/generator/options/{index}/weight");
            }
            else
            {
                totalWeight += option.Weight;
            }

            ValidateValue(
                field,
                option.Value,
                graph,
                $"{location}/generator/options/{index}/value",
                diagnostics);
        }

        if (!double.IsFinite(totalWeight) || totalWeight <= 0d)
        {
            Add(
                diagnostics,
                code: "simulation.generation.categoricalWeightTotalInvalid",
                message: "Categorical option weights must have a finite positive total.",
                location: $"{location}/generator/options");
        }
    }

    static void ValidateValue(
        FieldDefinition field,
        ObservationValue value,
        ShapeGraph graph,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validationShape = new Shape(new("simulation:validation"), [field]);
        var fields = new Dictionary<string, ObservationValue>(1, StringComparer.Ordinal)
        {
            [field.Name.Value] = value
        };
        if (ObservationValidator.TryValidateAgainstShape(fields, validationShape, out var error, graph))
            return;

        Add(
            diagnostics,
            code: "simulation.generation.valueInvalid",
            message: $"Generated value for field '{field.Name.Value}' is invalid: {error}",
            location: location);
    }

    static bool AreEquivalent(TypeRef left, TypeRef right) => (left, right) switch
    {
        (ScalarTypeRef l, ScalarTypeRef r) => l.Kind == r.Kind && l.Format == r.Format,
        (EnumTypeRef l, EnumTypeRef r) => string.Equals(l.Name, r.Name, StringComparison.Ordinal)
                                           && l.Members.Order(StringComparer.Ordinal)
                                               .SequenceEqual(r.Members.Order(StringComparer.Ordinal)),
        (EntityReferenceTypeRef l, EntityReferenceTypeRef r) => l.Entity == r.Entity,
        (ArrayTypeRef l, ArrayTypeRef r) => AreEquivalent(l.ElementType, r.ElementType),
        (ObjectTypeRef l, ObjectTypeRef r) => AreEquivalent(l.Fields, r.Fields),
        (NamedTypeRef l, NamedTypeRef r) => l.TypeId == r.TypeId,
        (QuantityTypeRef l, QuantityTypeRef r) => string.Equals(l.Quantity, r.Quantity, StringComparison.Ordinal)
                                                  && l.BaseKind == r.BaseKind,
        (OpaqueRuntimeTypeRef l, OpaqueRuntimeTypeRef r) => string.Equals(l.RuntimeType, r.RuntimeType, StringComparison.Ordinal),
        (JsonTypeRef l, JsonTypeRef r) => l.Kind == r.Kind,
        _ => false
    };

    static bool AreEquivalent(
        ImmutableArray<ObjectFieldTypeDef> left,
        ImmutableArray<ObjectFieldTypeDef> right)
    {
        if (left.Length != right.Length)
            return false;

        var orderedLeft = left.OrderBy(static field => field.Name, StringComparer.Ordinal).ToArray();
        var orderedRight = right.OrderBy(static field => field.Name, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < orderedLeft.Length; index++)
        {
            if (!string.Equals(orderedLeft[index].Name, orderedRight[index].Name, StringComparison.Ordinal)
                || orderedLeft[index].Presence != orderedRight[index].Presence
                || orderedLeft[index].Cardinality != orderedRight[index].Cardinality
                || orderedLeft[index].Nullability != orderedRight[index].Nullability
                || !AreEquivalent(orderedLeft[index].Type, orderedRight[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    static string Describe(TypeRef type) => type switch
    {
        ScalarTypeRef scalar => scalar.Kind.ToString(),
        EnumTypeRef @enum => $"enum:{@enum.Name}",
        EntityReferenceTypeRef entity => $"entity:{entity.Entity.Value}",
        ArrayTypeRef array => $"array<{Describe(array.ElementType)}>",
        ObjectTypeRef => "object",
        NamedTypeRef named => $"named:{named.TypeId.Value}",
        QuantityTypeRef quantity => $"quantity:{quantity.Quantity}",
        OpaqueRuntimeTypeRef opaque => $"opaque:{opaque.RuntimeType}",
        JsonTypeRef json => $"json:{json.Kind}",
        _ => type.GetType().Name
    };

    static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static GenerationCompilationResult Invalid(
        GenerationDefinition definition,
        IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(definition, null, CreateValidation(diagnostics));

    static DocumentValidationResult CreateValidation(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutableArray()));

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        diagnostics.Add(new(
            Code: code,
            Severity: severity,
            Message: message,
            Location: location,
            Evidence: new(stage: "generation-compilation")));
}
