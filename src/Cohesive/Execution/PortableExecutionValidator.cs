using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostic codes emitted by <see cref="PortableExecutionValidator"/>.</summary>
public static class PortableExecutionDiagnosticCodes
{
    /// <summary>An opaque runtime type entered a portable execution boundary.</summary>
    public const string OpaqueRuntimeType = "execution.portability.opaqueRuntimeType";

    /// <summary>A type node or type member is outside the recognized portable subset.</summary>
    public const string UnsupportedType = "execution.portability.unsupportedType";

    /// <summary>A named type could not be resolved from the supplied shape graph.</summary>
    public const string UnresolvedType = "execution.portability.unresolvedType";

    /// <summary>An expression node is outside the recognized portable subset.</summary>
    public const string UnsupportedExpression = "execution.portability.unsupportedExpression";

    /// <summary>A recognized semantic node contains malformed or unsupported state.</summary>
    public const string InvalidNode = "execution.portability.invalidNode";

    /// <summary>A value contract has neither a portable type nor a durable shape identity.</summary>
    public const string UntypedContract = "execution.portability.untypedContract";

    /// <summary>A graph-qualified shape could not be resolved from the supplied graph.</summary>
    public const string UnresolvedShape = "execution.portability.unresolvedShape";

    /// <summary>A missing or absent state violates a required-presence contract.</summary>
    public const string PresenceMismatch = "execution.portability.presenceMismatch";

    /// <summary>An explicit null state violates a non-nullable contract.</summary>
    public const string NullabilityMismatch = "execution.portability.nullabilityMismatch";

    /// <summary>A concrete observation is incompatible with its declared type or shape.</summary>
    public const string ConcreteTypeMismatch = "execution.portability.concreteTypeMismatch";

    /// <summary>An undefined observation appeared where an explicit portable value state is required.</summary>
    public const string UndefinedObservation = "execution.portability.undefinedObservation";

    /// <summary>A floating-point observation is NaN or infinite.</summary>
    public const string NonFiniteNumber = "execution.portability.nonFiniteNumber";

    /// <summary>An observation payload does not satisfy the invariant implied by its kind.</summary>
    public const string MalformedObservation = "execution.portability.malformedObservation";
}

/// <summary>
/// Validates canonical execution values and semantic nodes against the closed portable subset.
/// </summary>
/// <remarks>
/// Validation is deliberately fail-closed: an unrecognized subclass is an error even if it can be serialized by a
/// local runtime. Named types and qualified shapes require a matching <see cref="ShapeGraph"/> so validation never
/// treats unresolved semantics as compatible by assumption.
/// </remarks>
public static class PortableExecutionValidator
{
    /// <summary>Validates a portable value, its contract, and every nested observation.</summary>
    /// <param name="value">Portable value to validate.</param>
    /// <param name="graph">Optional graph used to resolve named types and graph-qualified shapes.</param>
    /// <returns>Every portability diagnostic found in deterministic traversal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(PortableValue value, ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var context = new ValidationContext(graph);
        context.ValidatePortableValue(value, location: string.Empty);
        return context.ToResult();
    }

    /// <summary>Validates a semantic value contract and every referenced portable type.</summary>
    /// <param name="contract">Value contract to validate.</param>
    /// <param name="graph">Optional graph used to resolve named types and graph-qualified shapes.</param>
    /// <returns>Every portability diagnostic found in deterministic traversal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(ValueContract contract, ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var context = new ValidationContext(graph);
        context.ValidateContract(contract, location: string.Empty);
        return context.ToResult();
    }

    /// <summary>Validates a type reference and all transitively referenced named type definitions.</summary>
    /// <param name="type">Type reference to validate.</param>
    /// <param name="graph">Optional graph used to resolve named types.</param>
    /// <returns>Every portability diagnostic found in deterministic traversal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(TypeRef type, ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var context = new ValidationContext(graph);
        context.ValidateType(type, location: string.Empty);
        return context.ToResult();
    }

    /// <summary>Validates an expression and every nested expression, type reference, and constant.</summary>
    /// <param name="expression">Expression to validate.</param>
    /// <param name="graph">Optional graph used to resolve named types.</param>
    /// <returns>Every portability diagnostic found in deterministic traversal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(Expr expression, ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var context = new ValidationContext(graph);
        context.ValidateExpression(expression, location: string.Empty);
        return context.ToResult();
    }

    enum Compatibility
    {
        Compatible,
        Incompatible,
        Unresolved
    }

    sealed class ValidationContext(ShapeGraph? graph)
    {
        const int MaximumValueCompatibilityDepth = 64;

        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly HashSet<TypeId> activeTypeDefinitions = [];

        public DocumentValidationResult ToResult() =>
            DocumentValidationResult.FromDiagnostics(diagnostics);

        public void ValidatePortableValue(PortableValue value, string location)
        {
            ValidateContract(value.Contract, Child(location, "contract"));
            if (!Enum.IsDefined(value.State))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    $"Portable value state '{(int)value.State}' is not recognized.",
                    Child(location, "state"));
                return;
            }

            switch (value.State)
            {
                case PortableValueState.Missing:
                case PortableValueState.Absent:
                    if (value.Contract.Presence == FieldPresence.Required)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.PresenceMismatch,
                            $"State '{value.State}' does not satisfy a required-presence contract.",
                            Child(location, "state"));
                    }
                    break;
                case PortableValueState.Null:
                    if (value.Contract.Nullability == FieldNullability.NonNullable)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.NullabilityMismatch,
                            "An explicit null does not satisfy a non-nullable contract.",
                            Child(location, "state"));
                    }
                    break;
                case PortableValueState.Unknown:
                case PortableValueState.Failed:
                    break;
                case PortableValueState.Concrete:
                    if (value.Value is not { } observation)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A concrete portable value has no observation payload.",
                            Child(location, "value"));
                        return;
                    }

                    ValidateObservation(observation, Child(location, "value"));
                    ValidateConcreteContract(value.Contract, observation, location);
                    break;
            }
        }

        public void ValidateContract(ValueContract contract, string location)
        {
            if (!Enum.IsDefined(contract.Cardinality))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    $"Value cardinality '{(int)contract.Cardinality}' is not recognized.",
                    Child(location, "cardinality"));
            }
            if (!Enum.IsDefined(contract.Presence))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    $"Value presence '{(int)contract.Presence}' is not recognized.",
                    Child(location, "presence"));
            }
            if (!Enum.IsDefined(contract.Nullability))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    $"Value nullability '{(int)contract.Nullability}' is not recognized.",
                    Child(location, "nullability"));
            }

            if (contract.Type is { } type)
                ValidateType(type, Child(location, "type"));

            if (contract.Shape is { } shapeId)
            {
                if (graph is null || !graph.TryGetShape(shapeId, out var shape))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.UnresolvedShape,
                        $"Qualified shape '{shapeId}' cannot be resolved from the supplied graph.",
                        Child(location, "shape"));
                }
                else
                {
                    ValidateShape(shape, Child(location, "shape"));
                }
            }

            if (contract.Type is null && contract.Shape is null)
            {
                Error(
                    PortableExecutionDiagnosticCodes.UntypedContract,
                    "A portable value contract requires a type or durable graph-qualified shape.",
                    location);
            }
        }

        public void ValidateType(TypeRef? type, string location)
        {
            if (type is null)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A portable type reference cannot be null.",
                    location);
                return;
            }

            switch (type)
            {
                case OpaqueRuntimeTypeRef opaque:
                    Error(
                        PortableExecutionDiagnosticCodes.OpaqueRuntimeType,
                        $"Opaque runtime type '{opaque.RuntimeType}' is not portable execution state.",
                        location);
                    break;
                case ScalarTypeRef scalar:
                    if (!Enum.IsDefined(scalar.Kind) || !Enum.IsDefined(scalar.Format))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.UnsupportedType,
                            $"Scalar type '{scalar.Kind}' with format '{scalar.Format}' is not recognized.",
                            location);
                    }
                    break;
                case EnumTypeRef enumType:
                    ValidateInlineEnum(enumType, location);
                    break;
                case EntityReferenceTypeRef entityReference:
                    if (string.IsNullOrWhiteSpace(entityReference.Entity.Value))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "An entity reference requires a stable entity name.",
                            Child(location, "entity"));
                    }
                    break;
                case ArrayTypeRef array:
                    ValidateType(array.ElementType, Child(location, "elementType"));
                    break;
                case ObjectTypeRef objectType:
                    ValidateInlineObject(objectType, location);
                    break;
                case QuantityTypeRef quantity:
                    if (string.IsNullOrWhiteSpace(quantity.Quantity) || !Enum.IsDefined(quantity.BaseKind))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.UnsupportedType,
                            "A quantity type requires a name and recognized scalar base kind.",
                            location);
                    }
                    break;
                case JsonTypeRef json:
                    if (!Enum.IsDefined(json.Kind))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.UnsupportedType,
                            $"JSON type kind '{(int)json.Kind}' is not recognized.",
                            Child(location, "kind"));
                    }
                    break;
                case NamedTypeRef named:
                    ValidateNamedType(named, location);
                    break;
                default:
                    Error(
                        PortableExecutionDiagnosticCodes.UnsupportedType,
                        $"Type node '{type.GetType().FullName}' is not in the closed portable type union.",
                        location);
                    break;
            }
        }

        public void ValidateExpression(Expr? expression, string location)
        {
            if (expression is null)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A portable expression node cannot be null.",
                    location);
                return;
            }

            switch (expression)
            {
                case BindingExpr bindingExpression:
                    if (string.IsNullOrWhiteSpace(bindingExpression.Binding.Value))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A binding expression requires a stable identifier.",
                            Child(location, "binding"));
                    }
                    break;
                case FieldExpr field:
                    ValidatePath(field.Path, Child(location, "path"));
                    if (field.Binding is { } binding && string.IsNullOrWhiteSpace(binding.Value))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A field binding requires a stable identifier.",
                            Child(location, "binding"));
                    }
                    break;
                case CurrentItemExpr:
                    break;
                case ParameterExpr parameter:
                    if (string.IsNullOrWhiteSpace(parameter.Parameter))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A parameter expression requires a name.",
                            Child(location, "parameter"));
                    }
                    break;
                case ConstantExpr constant:
                    ValidateObservation(constant.Value, Child(location, "value"));
                    break;
                case UnaryExpr unary:
                    if (!Enum.IsDefined(unary.Operator))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            $"Unary operator '{(int)unary.Operator}' is not recognized.",
                            Child(location, "operator"));
                    }
                    ValidateExpression(unary.Operand, Child(location, "operand"));
                    break;
                case BinaryExpr binary:
                    if (!Enum.IsDefined(binary.Operator))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            $"Binary operator '{(int)binary.Operator}' is not recognized.",
                            Child(location, "operator"));
                    }
                    ValidateExpression(binary.Left, Child(location, "left"));
                    ValidateExpression(binary.Right, Child(location, "right"));
                    break;
                case ConditionalExpr conditional:
                    ValidateExpression(conditional.Test, Child(location, "test"));
                    ValidateExpression(conditional.IfTrue, Child(location, "ifTrue"));
                    ValidateExpression(conditional.IfFalse, Child(location, "ifFalse"));
                    ValidateType(conditional.ReturnType, Child(location, "returnType"));
                    break;
                case CallExpr call:
                    if (string.IsNullOrWhiteSpace(call.Function))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A function expression requires a stable function identifier.",
                            Child(location, "function"));
                    }
                    if (call.Arguments.IsDefault)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "A function argument collection must be initialized.",
                            Child(location, "arguments"));
                    }
                    else
                    {
                        for (var index = 0; index < call.Arguments.Length; index++)
                            ValidateExpression(call.Arguments[index], Index(Child(location, "arguments"), index));
                    }
                    ValidateType(call.ReturnType, Child(location, "returnType"));
                    break;
                case FieldRefExpr fieldReference:
                    ValidatePath(fieldReference.Path, Child(location, "path"));
                    ValidateType(fieldReference.Type, Child(location, "type"));
                    break;
                case LiteralExpr literal:
                    ValidateType(literal.Type, Child(location, "type"));
                    ValidateObservation(literal.Value, Child(location, "value"));
                    if (MatchType(literal.Type, literal.Value, MaximumValueCompatibilityDepth)
                        != Compatibility.Compatible)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.ConcreteTypeMismatch,
                            "The literal observation is incompatible with its declared type.",
                            Child(location, "value"));
                    }
                    break;
                case AggregateExpr aggregate:
                    if (!Enum.IsDefined(aggregate.Operator))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            $"Aggregate operator '{(int)aggregate.Operator}' is not recognized.",
                            Child(location, "operator"));
                    }
                    ValidateExpression(aggregate.Source, Child(location, "source"));
                    ValidateType(aggregate.ReturnType, Child(location, "returnType"));
                    if (aggregate.GroupBy.IsDefault)
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.InvalidNode,
                            "An aggregate grouping collection must be initialized.",
                            Child(location, "groupBy"));
                    }
                    else
                    {
                        for (var index = 0; index < aggregate.GroupBy.Length; index++)
                            ValidateExpression(aggregate.GroupBy[index], Index(Child(location, "groupBy"), index));
                    }
                    break;
                default:
                    Error(
                        PortableExecutionDiagnosticCodes.UnsupportedExpression,
                        $"Expression node '{expression.GetType().FullName}' is not in the closed portable expression union.",
                        location);
                    break;
            }
        }

        void ValidateConcreteContract(ValueContract contract, ObservationValue value, string location)
        {
            if (contract.GetEffectiveType() is { } effectiveType
                && MatchType(effectiveType, value, MaximumValueCompatibilityDepth)
                    != Compatibility.Compatible)
            {
                Error(
                    PortableExecutionDiagnosticCodes.ConcreteTypeMismatch,
                    "The concrete observation is incompatible with the value contract's effective type.",
                    Child(location, "value"));
            }

            if (contract.Shape is not { } shapeId)
                return;
            if (graph is null || !graph.TryGetShape(shapeId, out var shape))
                return;
            var shapeCompatibility = contract.Cardinality == FieldCardinality.Many
                ? MatchShapeArray(shape, value)
                : MatchShape(shape, value, MaximumValueCompatibilityDepth);
            if (shapeCompatibility != Compatibility.Compatible)
            {
                Error(
                    PortableExecutionDiagnosticCodes.ConcreteTypeMismatch,
                    $"The concrete observation is incompatible with shape '{shapeId}'.",
                    Child(location, "value"));
            }
        }

        Compatibility MatchShapeArray(Shape shape, ObservationValue value)
        {
            if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
                return Compatibility.Incompatible;

            var result = Compatibility.Compatible;
            foreach (var item in value.Array)
            {
                result = Combine(
                    result,
                    MatchShape(shape, item, MaximumValueCompatibilityDepth - 1));
            }
            return result;
        }

        void ValidateInlineEnum(EnumTypeRef enumType, string location)
        {
            if (string.IsNullOrWhiteSpace(enumType.Name) || enumType.Members.IsDefaultOrEmpty)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "An inline enum requires a name and at least one member.",
                    location);
                return;
            }

            HashSet<string> members = new(StringComparer.Ordinal);
            for (var index = 0; index < enumType.Members.Length; index++)
            {
                var member = enumType.Members[index];
                if (string.IsNullOrWhiteSpace(member) || !members.Add(member))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Inline enum members must be non-empty and unique using ordinal comparison.",
                        Index(Child(location, "members"), index));
                }
            }
        }

        void ValidateInlineObject(ObjectTypeRef objectType, string location)
        {
            HashSet<string> fieldNames = new(StringComparer.Ordinal);
            for (var index = 0; index < objectType.Fields.Length; index++)
            {
                var fieldLocation = Index(Child(location, "fields"), index);
                var field = objectType.Fields[index];
                if (field is null)
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "An inline object field cannot be null.",
                        fieldLocation);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(field.Name) || !fieldNames.Add(field.Name))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Inline object field names must be non-empty and unique using ordinal comparison.",
                        Child(fieldLocation, "name"));
                }
                ValidateFieldMetadata(
                    field.Cardinality,
                    field.Presence,
                    field.Nullability,
                    fieldLocation);
                ValidateType(field.Type, Child(fieldLocation, "type"));
            }
        }

        void ValidateNamedType(NamedTypeRef named, string location)
        {
            if (string.IsNullOrWhiteSpace(named.TypeId.Value))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A named type reference requires a stable type identifier.",
                    Child(location, "typeId"));
                return;
            }

            if (graph is null || !graph.TryGetType(named.TypeId, out var definition))
            {
                Error(
                    PortableExecutionDiagnosticCodes.UnresolvedType,
                    $"Named type '{named.TypeId.Value}' cannot be resolved from the supplied graph.",
                    location);
                return;
            }

            ValidateTypeDefinition(definition, location);
        }

        void ValidateTypeDefinition(TypeDefinition definition, string location)
        {
            if (!activeTypeDefinitions.Add(definition.Id))
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(definition.Id.Value) || string.IsNullOrWhiteSpace(definition.Name))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "A named type definition requires stable identity and name.",
                        location);
                }

                switch (definition)
                {
                    case TypeDefinition.Structural structural:
                        ValidateStructuralType(structural, location);
                        break;
                    case TypeDefinition.Enum enumType:
                        ValidateNamedEnum(enumType, location);
                        break;
                    case TypeDefinition.Union union:
                        ValidateUnion(union, location);
                        break;
                    default:
                        Error(
                            PortableExecutionDiagnosticCodes.UnsupportedType,
                            $"Named type definition '{definition.GetType().FullName}' is not recognized.",
                            location);
                        break;
                }
            }
            finally
            {
                activeTypeDefinitions.Remove(definition.Id);
            }
        }

        void ValidateStructuralType(TypeDefinition.Structural structural, string location)
        {
            if (structural.Fields.IsDefault)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A structural type's field collection must be initialized.",
                    Child(location, "fields"));
                return;
            }

            HashSet<string> fieldNames = new(StringComparer.Ordinal);
            for (var index = 0; index < structural.Fields.Length; index++)
            {
                var fieldLocation = Index(Child(location, "fields"), index);
                var field = structural.Fields[index];
                if (field is null)
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "A structural field cannot be null.",
                        fieldLocation);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(field.Name.Value) || !fieldNames.Add(field.Name.Value))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Structural field names must be non-empty and unique using ordinal comparison.",
                        Child(fieldLocation, "name"));
                }
                ValidateFieldMetadata(field.Cardinality, field.Presence, field.Nullability, fieldLocation);
                ValidateType(field.Type, Child(fieldLocation, "type"));
                ValidateConstraints(field.Constraints, Child(fieldLocation, "constraints"));
            }

            ValidateConstraints(structural.Constraints, Child(location, "constraints"));
        }

        void ValidateNamedEnum(TypeDefinition.Enum enumType, string location)
        {
            if (!Enum.IsDefined(enumType.Underlying) || enumType.Values.IsDefaultOrEmpty)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A named enum requires a recognized primitive representation and at least one value.",
                    location);
                return;
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            for (var index = 0; index < enumType.Values.Length; index++)
            {
                var value = enumType.Values[index];
                if (value is null || string.IsNullOrWhiteSpace(value.Name) || !names.Add(value.Name))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Named enum values must have non-empty, unique names.",
                        Index(Child(location, "values"), index));
                }
            }
        }

        void ValidateUnion(TypeDefinition.Union union, string location)
        {
            if (union.Discriminator is null
                || string.IsNullOrWhiteSpace(union.Discriminator.FieldName)
                || !Enum.IsDefined(union.Discriminator.Type))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A union requires a named discriminator with a recognized primitive type.",
                    Child(location, "discriminator"));
            }
            if (union.Cases.IsDefaultOrEmpty)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A union requires at least one initialized case.",
                    Child(location, "cases"));
                return;
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            HashSet<string> discriminators = new(StringComparer.Ordinal);
            for (var index = 0; index < union.Cases.Length; index++)
            {
                var caseLocation = Index(Child(location, "cases"), index);
                var unionCase = union.Cases[index];
                if (unionCase is null)
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "A union case cannot be null.",
                        caseLocation);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(unionCase.Name) || !names.Add(unionCase.Name))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Union case names must be non-empty and unique.",
                        Child(caseLocation, "name"));
                }
                if (string.IsNullOrWhiteSpace(unionCase.DiscriminatorValue)
                    || !discriminators.Add(unionCase.DiscriminatorValue))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Union discriminator values must be non-empty and unique.",
                        Child(caseLocation, "discriminatorValue"));
                }
                ValidateType(unionCase.Type, Child(caseLocation, "type"));
            }
        }

        void ValidateShape(Shape shape, string location)
        {
            if (shape.Fields.IsDefault)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A shape's field collection must be initialized.",
                    Child(location, "fields"));
                return;
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            for (var index = 0; index < shape.Fields.Length; index++)
            {
                var fieldLocation = Index(Child(location, "fields"), index);
                var field = shape.Fields[index];
                if (field is null)
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "A shape field cannot be null.",
                        fieldLocation);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(field.Name.Value) || !names.Add(field.Name.Value))
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "Shape field names must be non-empty and unique.",
                        Child(fieldLocation, "name"));
                }
                ValidateFieldMetadata(field.Cardinality, field.Presence, field.Nullability, fieldLocation);
                ValidateType(field.Type, Child(fieldLocation, "type"));
                if (field.Compute is { } compute)
                    ValidateExpression(compute.Expression, Child(Child(fieldLocation, "compute"), "expression"));
                ValidateConstraints(field.Constraints, Child(fieldLocation, "constraints"));
            }

            ValidateConstraints(shape.Constraints, Child(location, "constraints"));
        }

        void ValidateConstraints(ImmutableArray<ShapeConstraint> constraints, string location)
        {
            if (constraints.IsDefault)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A semantic constraint collection must be initialized.",
                    location);
                return;
            }

            for (var index = 0; index < constraints.Length; index++)
                ValidateConstraint(constraints[index], Index(location, index));
        }

        void ValidateConstraint(ShapeConstraint? constraint, string location)
        {
            if (constraint is null)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A semantic constraint cannot be null.",
                    location);
                return;
            }

            FieldPath? field = constraint switch
            {
                RequiredConstraint required => required.Field,
                MinLengthConstraint minimumLength => minimumLength.Field,
                MaxLengthConstraint maximumLength => maximumLength.Field,
                RangeConstraint range => range.Field,
                RegexConstraint regex => regex.Field,
                AllowedValuesConstraint allowed => allowed.Field,
                OccurrenceConstraint occurrence => occurrence.Field,
                _ => null
            };
            if (field is { } path)
                ValidatePath(path, Child(location, "field"));

            switch (constraint)
            {
                case RequiredConstraint:
                    break;
                case MinLengthConstraint minimumLength when minimumLength.Value < 0:
                    InvalidConstraint("Minimum length cannot be negative.", location);
                    break;
                case MinLengthConstraint:
                    break;
                case MaxLengthConstraint maximumLength when maximumLength.Value < 0:
                    InvalidConstraint("Maximum length cannot be negative.", location);
                    break;
                case MaxLengthConstraint:
                    break;
                case RangeConstraint range when range.Minimum is null && range.Maximum is null:
                    InvalidConstraint("A range requires at least one bound.", location);
                    break;
                case RangeConstraint range when range.Minimum > range.Maximum:
                    InvalidConstraint("A range's minimum cannot exceed its maximum.", location);
                    break;
                case RangeConstraint:
                    break;
                case RegexConstraint regex when string.IsNullOrWhiteSpace(regex.Pattern):
                    InvalidConstraint("A regex constraint requires a pattern.", location);
                    break;
                case RegexConstraint:
                    break;
                case AllowedValuesConstraint allowed when allowed.Values.IsDefaultOrEmpty:
                    InvalidConstraint("An allowed-values constraint requires initialized values.", location);
                    break;
                case AllowedValuesConstraint allowed when allowed.Values.Any(string.IsNullOrWhiteSpace):
                    InvalidConstraint("Allowed values must be non-empty strings.", location);
                    break;
                case AllowedValuesConstraint:
                    break;
                case OccurrenceConstraint occurrence when occurrence.Minimum is null && occurrence.Maximum is null:
                    InvalidConstraint("An occurrence constraint requires at least one bound.", location);
                    break;
                case OccurrenceConstraint occurrence when occurrence.Minimum < 0 || occurrence.Maximum < 0:
                    InvalidConstraint("Occurrence bounds cannot be negative.", location);
                    break;
                case OccurrenceConstraint occurrence when occurrence.Minimum > occurrence.Maximum:
                    InvalidConstraint("An occurrence minimum cannot exceed its maximum.", location);
                    break;
                case OccurrenceConstraint:
                    break;
                default:
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        $"Constraint node '{constraint.GetType().FullName}' is not in the closed portable constraint union.",
                        location);
                    break;
            }
        }

        void InvalidConstraint(string message, string location) =>
            Error(PortableExecutionDiagnosticCodes.InvalidNode, message, location);

        void ValidateFieldMetadata(
            FieldCardinality cardinality,
            FieldPresence presence,
            FieldNullability nullability,
            string location)
        {
            if (!Enum.IsDefined(cardinality) || !Enum.IsDefined(presence) || !Enum.IsDefined(nullability))
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "Field cardinality, presence, or nullability is not recognized.",
                    location);
            }
        }

        void ValidatePath(FieldPath path, string location)
        {
            if (path.Segments.IsDefaultOrEmpty)
            {
                Error(
                    PortableExecutionDiagnosticCodes.InvalidNode,
                    "A field path requires at least one initialized segment.",
                    location);
                return;
            }

            for (var index = 0; index < path.Segments.Length; index++)
            {
                var segment = path.Segments[index];
                if (!Enum.IsDefined(segment.Kind)
                    || segment.Kind == SegmentKind.Field && string.IsNullOrWhiteSpace(segment.Segment)
                    || segment.Kind == SegmentKind.Element && segment.Segment is not null)
                {
                    Error(
                        PortableExecutionDiagnosticCodes.InvalidNode,
                        "A field path contains a malformed or unrecognized segment.",
                        Index(location, index));
                }
            }
        }

        void ValidateObservation(ObservationValue value, string location)
        {
            switch (value.Kind)
            {
                case ObservationValueKind.Undefined:
                    Error(
                        PortableExecutionDiagnosticCodes.UndefinedObservation,
                        "Undefined cannot represent a portable value; use an explicit Missing, Absent, or Unknown state.",
                        location);
                    break;
                case ObservationValueKind.Null:
                case ObservationValueKind.Int64:
                case ObservationValueKind.Decimal:
                case ObservationValueKind.Bool:
                case ObservationValueKind.Bytes:
                    break;
                case ObservationValueKind.Double:
                    if (!double.IsFinite(value.Double))
                    {
                        Error(
                            PortableExecutionDiagnosticCodes.NonFiniteNumber,
                            "Portable doubles must be finite.",
                            location);
                    }
                    break;
                case ObservationValueKind.String:
                    if (value.String is null)
                        MalformedObservation(value.Kind, location);
                    break;
                case ObservationValueKind.DateTimeOffset:
                    if (!value.TryGetDateTimeOffset(out _))
                        MalformedObservation(value.Kind, location);
                    break;
                case ObservationValueKind.DateOnly:
                    if (!value.TryGetDateOnly(out _))
                        MalformedObservation(value.Kind, location);
                    break;
                case ObservationValueKind.TimeOnly:
                    if (!value.TryGetTimeOnly(out _))
                        MalformedObservation(value.Kind, location);
                    break;
                case ObservationValueKind.TimeSpan:
                    if (!value.TryGetTimeSpan(out _))
                        MalformedObservation(value.Kind, location);
                    break;
                case ObservationValueKind.Object:
                    if (value.Fields is null)
                    {
                        MalformedObservation(value.Kind, location);
                        break;
                    }
                    foreach (var field in value.Fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(field.Key))
                        {
                            Error(
                                PortableExecutionDiagnosticCodes.MalformedObservation,
                                "Object observation property names must be non-empty.",
                                location);
                        }
                        ValidateObservation(field.Value, Child(location, field.Key));
                    }
                    break;
                case ObservationValueKind.Array:
                    if (value.Array.IsDefault)
                    {
                        MalformedObservation(value.Kind, location);
                        break;
                    }
                    for (var index = 0; index < value.Array.Length; index++)
                        ValidateObservation(value.Array[index], Index(location, index));
                    break;
                default:
                    Error(
                        PortableExecutionDiagnosticCodes.MalformedObservation,
                        $"Observation kind '{(int)value.Kind}' is not recognized.",
                        location);
                    break;
            }
        }

        void MalformedObservation(ObservationValueKind kind, string location) =>
            Error(
                PortableExecutionDiagnosticCodes.MalformedObservation,
                $"Observation kind '{kind}' does not contain a valid payload.",
                location);

        Compatibility MatchType(TypeRef? type, ObservationValue value, int remainingDepth)
        {
            if (remainingDepth < 0)
                return Compatibility.Unresolved;
            if (type is null || value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
                return Compatibility.Incompatible;
            if (type is ScalarTypeRef or EnumTypeRef or EntityReferenceTypeRef or QuantityTypeRef or JsonTypeRef)
                return FromLocalCompatibility(ValueContractSemantics.Evaluate(type, value));
            if (type is ArrayTypeRef array)
                return MatchArrayLocallyOrResolve(array, value, remainingDepth);
            if (type is ObjectTypeRef objectType)
                return MatchObjectLocallyOrResolve(objectType, value, remainingDepth);
            if (type is NamedTypeRef named)
                return MatchNamed(named, value, remainingDepth);
            if (type is OpaqueRuntimeTypeRef)
                return Compatibility.Unresolved;

            return Compatibility.Incompatible;
        }

        Compatibility MatchArrayLocallyOrResolve(
            ArrayTypeRef type,
            ObservationValue value,
            int remainingDepth)
        {
            var local = ValueContractSemantics.Evaluate(type, value);
            return local == ValueConstantCompatibility.Unknown
                ? MatchArray(type, value, remainingDepth)
                : FromLocalCompatibility(local);
        }

        Compatibility MatchObjectLocallyOrResolve(
            ObjectTypeRef type,
            ObservationValue value,
            int remainingDepth)
        {
            var local = ValueContractSemantics.Evaluate(type, value);
            return local == ValueConstantCompatibility.Unknown
                ? MatchObject(type, value, remainingDepth)
                : FromLocalCompatibility(local);
        }

        Compatibility MatchArray(ArrayTypeRef type, ObservationValue value, int remainingDepth)
        {
            if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
                return Compatibility.Incompatible;

            var result = Compatibility.Compatible;
            foreach (var item in value.Array)
                result = Combine(result, MatchType(type.ElementType, item, remainingDepth - 1));
            return result;
        }

        Compatibility MatchObject(ObjectTypeRef type, ObservationValue value, int remainingDepth)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                return Compatibility.Incompatible;

            var result = Compatibility.Compatible;
            foreach (var field in type.Fields)
            {
                if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Type is null)
                    return Compatibility.Unresolved;
                if (!value.Fields.TryGetValue(field.Name, out var fieldValue))
                {
                    if (field.Presence == FieldPresence.Required)
                        return Compatibility.Incompatible;
                    continue;
                }
                result = Combine(
                    result,
                    MatchField(
                        field.Type,
                        field.Cardinality,
                        field.Nullability,
                        fieldValue,
                        remainingDepth - 1));
            }
            return result;
        }

        Compatibility MatchNamed(NamedTypeRef named, ObservationValue value, int remainingDepth)
        {
            if (graph is null || !graph.TryGetType(named.TypeId, out var definition))
                return Compatibility.Unresolved;
            if (remainingDepth < 0)
                return Compatibility.Unresolved;

            return definition switch
            {
                TypeDefinition.Structural structural => MatchStructural(
                    structural,
                    value,
                    remainingDepth),
                TypeDefinition.Enum enumType => MatchNamedEnum(enumType, value),
                TypeDefinition.Union union => MatchUnion(union, value, remainingDepth),
                _ => Compatibility.Incompatible
            };
        }

        Compatibility MatchStructural(
            TypeDefinition.Structural structural,
            ObservationValue value,
            int remainingDepth)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                return Compatibility.Incompatible;

            var result = Compatibility.Compatible;
            foreach (var field in structural.Fields)
            {
                if (field is null || field.Type is null)
                    return Compatibility.Unresolved;
                if (!value.Fields.TryGetValue(field.Name.Value, out var fieldValue))
                {
                    if (field.Presence == FieldPresence.Required)
                        return Compatibility.Incompatible;
                    continue;
                }
                result = Combine(
                    result,
                    MatchField(
                        field.Type,
                        field.Cardinality,
                        field.Nullability,
                        fieldValue,
                        remainingDepth - 1));
            }
            return result;
        }

        Compatibility MatchNamedEnum(TypeDefinition.Enum type, ObservationValue value)
        {
            foreach (var enumValue in type.Values)
            {
                if (enumValue is not null
                    && PrimitiveTypeSemantics.MatchesLiteral(type.Underlying, enumValue.Value ?? enumValue.Name, value))
                {
                    return Compatibility.Compatible;
                }
            }
            return Compatibility.Incompatible;
        }

        Compatibility MatchUnion(TypeDefinition.Union type, ObservationValue value, int remainingDepth)
        {
            if (value.Kind != ObservationValueKind.Object
                || value.Fields is null
                || type.Discriminator is null
                || !value.Fields.TryGetValue(type.Discriminator.FieldName, out var discriminator))
            {
                return Compatibility.Incompatible;
            }

            foreach (var unionCase in type.Cases)
            {
                if (unionCase is not null
                    && PrimitiveTypeSemantics.MatchesLiteral(type.Discriminator.Type, unionCase.DiscriminatorValue, discriminator))
                {
                    return MatchType(unionCase.Type, value, remainingDepth - 1);
                }
            }
            return Compatibility.Incompatible;
        }

        Compatibility MatchShape(Shape shape, ObservationValue value, int remainingDepth)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
                return Compatibility.Incompatible;

            var result = Compatibility.Compatible;
            foreach (var field in shape.Fields)
            {
                if (field is null || field.Type is null)
                    return Compatibility.Unresolved;
                if (!value.Fields.TryGetValue(field.Name.Value, out var fieldValue))
                {
                    if (field.Presence == FieldPresence.Required)
                        return Compatibility.Incompatible;
                    continue;
                }
                result = Combine(
                    result,
                    MatchField(
                        field.Type,
                        field.Cardinality,
                        field.Nullability,
                        fieldValue,
                        remainingDepth - 1));
            }
            return result;
        }

        Compatibility MatchField(
            TypeRef type,
            FieldCardinality cardinality,
            FieldNullability nullability,
            ObservationValue value,
            int remainingDepth)
        {
            if (value.Kind == ObservationValueKind.Null)
                return FromBoolean(nullability == FieldNullability.Nullable);
            if (value.Kind == ObservationValueKind.Undefined)
                return Compatibility.Incompatible;
            return cardinality == FieldCardinality.Many
                ? MatchArray(new ArrayTypeRef(type), value, remainingDepth)
                : MatchType(type, value, remainingDepth);
        }

        static Compatibility Combine(Compatibility left, Compatibility right)
        {
            if (left == Compatibility.Incompatible || right == Compatibility.Incompatible)
                return Compatibility.Incompatible;
            if (left == Compatibility.Unresolved || right == Compatibility.Unresolved)
                return Compatibility.Unresolved;
            return Compatibility.Compatible;
        }

        static Compatibility FromBoolean(bool value) =>
            value ? Compatibility.Compatible : Compatibility.Incompatible;

        static Compatibility FromLocalCompatibility(ValueConstantCompatibility value) => value switch
        {
            ValueConstantCompatibility.Compatible => Compatibility.Compatible,
            ValueConstantCompatibility.Incompatible => Compatibility.Incompatible,
            _ => Compatibility.Unresolved
        };

        void Error(string code, string message, string location) =>
            diagnostics.Add(new(
                code,
                DiagnosticSeverity.Error,
                message,
                string.IsNullOrEmpty(location) ? "/" : location));

        static string Child(string location, string segment) =>
            $"{location}/{Escape(segment)}";

        static string Index(string location, int index) =>
            $"{location}/{index.ToString(CultureInfo.InvariantCulture)}";

        static string Escape(string segment) =>
            segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }
}
