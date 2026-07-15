using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Cohesive.Adapters.TypeScript.Ast;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Builds a TypeScript syntax tree from a shape graph.
/// </summary>
public sealed class TypeScriptShapeAstBuilder
{
    readonly ShapeGraph graph;
    readonly ImmutableArray<TypeScriptExternalTypeModule> externalTypeModules;
    readonly Dictionary<TypeId, string> typeNameById = [];
    readonly HashSet<TypeId> suppressedNamedTypes = [];

    /// <summary>
    /// Creates the AST builder.
    /// </summary>
    public TypeScriptShapeAstBuilder(
        ShapeGraph graph,
        ImmutableArray<TypeScriptExternalTypeModule> externalTypeModules = default)
    {
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        this.externalTypeModules = externalTypeModules.IsDefault ? [] : externalTypeModules;
        PrepareTypeNames();
    }

    /// <summary>
    /// Builds a TypeScript document for the graph.
    /// </summary>
    public TsDocument Build()
    {
        var statements = ImmutableArray.CreateBuilder<TsStatement>();
        var externalTypeNamesByImportPath = CollectExternalTypeNamesByImportPath();
        AddExternalTypeStatements(statements, externalTypeNamesByImportPath);

        for (var i = 0; i < graph.NamedTypes.Length; i++)
        {
            var namedType = graph.NamedTypes[i];
            if (suppressedNamedTypes.Contains(namedType.Id) || IsExternalType(namedType.Id))
                continue;

            statements.Add(TranslateNamedType(namedType));
            if (namedType is TypeDefinition.Enum @enum)
            {
                statements.Add(BuildEnumValueMap(@enum));
                statements.Add(BuildEnumLabelMap(@enum));
            }
        }

        for (var i = 0; i < graph.Shapes.Length; i++)
        {
            if (IsExternalShape(graph.Shapes[i].Id))
                continue;

            statements.Add(TranslateShape(graph.Shapes[i]));
        }

        return new TsDocument(statements.ToImmutable());
    }

    Dictionary<string, SortedSet<string>> CollectExternalTypeNamesByImportPath()
    {
        var namesByImportPath = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < graph.NamedTypes.Length; i++)
        {
            var namedType = graph.NamedTypes[i];
            if (TryGetExternalTypeModule(namedType.Id, out var module))
                AddExternalTypeName(namesByImportPath, module.ImportPath, ResolveDefinitionName(namedType.Id));
        }

        for (var i = 0; i < graph.Shapes.Length; i++)
        {
            var shape = graph.Shapes[i];
            if (TryGetExternalShapeModule(shape.Id, out var module))
                AddExternalTypeName(namesByImportPath, module.ImportPath, ResolveShapeName(shape.Id));
        }

        return namesByImportPath;
    }

    static void AddExternalTypeName(
        Dictionary<string, SortedSet<string>> namesByImportPath,
        string importPath,
        string typeName)
    {
        if (!namesByImportPath.TryGetValue(importPath, out var names))
        {
            names = new(StringComparer.Ordinal);
            namesByImportPath.Add(importPath, names);
        }

        names.Add(typeName);
    }

    static void AddExternalTypeStatements(
        ImmutableArray<TsStatement>.Builder statements,
        Dictionary<string, SortedSet<string>> namesByImportPath)
    {
        foreach (var pair in namesByImportPath.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var imports = pair.Value
                .Select(static name => new TsImportSpecifier(name))
                .ToImmutableArray();

            statements.Add(new TsImportDeclaration(pair.Key, imports, isTypeOnly: true));
            statements.Add(new TsExportDeclaration(imports, isTypeOnly: true));
        }
    }

    void PrepareTypeNames()
    {
        for (var i = 0; i < graph.NamedTypes.Length; i++)
            typeNameById[graph.NamedTypes[i].Id] = IdentifierFromId(graph.NamedTypes[i].Id.Value);

        for (var i = 0; i < graph.NamedTypes.Length; i++)
        {
            if (graph.NamedTypes[i] is not TypeDefinition.Structural structural)
                continue;

            for (var j = 0; j < graph.Shapes.Length; j++)
            {
                var shape = graph.Shapes[j];
                if (!string.Equals(ResolveDefinitionName(structural.Id), ResolveShapeName(shape.Id), StringComparison.Ordinal))
                    continue;

                if (!StructuralMatchesShape(structural, shape))
                    continue;

                suppressedNamedTypes.Add(structural.Id);
                typeNameById[structural.Id] = ResolveShapeName(shape.Id);
                break;
            }
        }
    }

    TsStatement TranslateNamedType(TypeDefinition namedType)
    {
        if (TryTranslateJsonRepresentation(GetAnnotations(namedType), out var representation))
            return new TsTypeAliasDeclaration(ResolveDefinitionName(namedType.Id), representation);

        return namedType switch
        {
            TypeDefinition.Structural structural => TranslateStructuralType(structural),
            TypeDefinition.Enum @enum => new TsTypeAliasDeclaration(ResolveDefinitionName(@enum.Id), BuildEnumType(@enum)),
            TypeDefinition.Union union => new TsTypeAliasDeclaration(ResolveDefinitionName(union.Id), BuildUnionType(union)),
            _ => throw new InvalidOperationException($"Unsupported named type definition '{namedType.GetType().Name}'.")
        };
    }

    TsStatement TranslateStructuralType(TypeDefinition.Structural structural)
    {
        var members = ImmutableArray.CreateBuilder<TsPropertySignature>(structural.Fields.Length);
        for (var i = 0; i < structural.Fields.Length; i++)
            members.Add(TranslateField(structural.Fields[i]));

        return members.Count == 0
            ? new TsTypeAliasDeclaration(ResolveDefinitionName(structural.Id), EmptyObjectType())
            : new TsInterfaceDeclaration(ResolveDefinitionName(structural.Id), members.ToImmutable());
    }

    TsStatement TranslateShape(Shape shape)
    {
        if (TryTranslateJsonRepresentation(shape.Annotations, out var representation))
            return new TsTypeAliasDeclaration(ResolveShapeName(shape.Id), representation);

        var members = ImmutableArray.CreateBuilder<TsPropertySignature>(shape.Fields.Length);
        for (var i = 0; i < shape.Fields.Length; i++)
            members.Add(TranslateField(shape.Fields[i]));

        return members.Count == 0
            ? new TsTypeAliasDeclaration(ResolveShapeName(shape.Id), EmptyObjectType())
            : new TsInterfaceDeclaration(ResolveShapeName(shape.Id), members.ToImmutable());
    }

    static TsTypeNode EmptyObjectType() => new TsRawType("Record<never, never>");

    TsPropertySignature TranslateField(StructuralField field)
    {
        var type = TranslateWireFieldType(
            field.Type,
            field.Cardinality,
            field.Nullability,
            field.Annotations);
        return new TsPropertySignature(
            name: field.Name.Value,
            type: type,
            isOptional: field.Presence == FieldPresence.Optional);
    }

    TsPropertySignature TranslateField(FieldDefinition field)
    {
        var type = TranslateWireFieldType(
            field.Type,
            field.Cardinality,
            field.Nullability,
            field.Annotations);
        return new TsPropertySignature(
            name: field.Name.Value,
            type: type,
            isOptional: field.Presence == FieldPresence.Optional,
            isReadonly: field.Mutability != FieldMutability.Mutable);
    }

    TsTypeNode TranslateWireFieldType(
        TypeRef type,
        FieldCardinality cardinality,
        FieldNullability nullability,
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations)
    {
        if (TryTranslateJsonRepresentation(annotations, out var representation))
            return ApplyFieldModifiers(representation, cardinality, nullability);

        if (HasBooleanAnnotation(annotations, SystemTextJsonShapeAnnotations.UndefinedNumericEnumValues))
        {
            return ApplyFieldModifiers(
                new TsUnionType(
                [
                    TranslateType(type),
                    new TsKeywordType(TsKeyword.Number)
                ]),
                cardinality,
                nullability);
        }

        if (!HasBooleanAnnotation(annotations, SystemTextJsonShapeAnnotations.Dictionary))
            return TranslateFieldType(type, cardinality, nullability);

        if (type is not ObjectTypeRef dictionaryEntry
            || dictionaryEntry.Fields.SingleOrDefault(static field =>
                string.Equals(field.Name, "Value", StringComparison.Ordinal)) is not { } valueField)
        {
            throw new InvalidOperationException(
                "A System.Text.Json dictionary field must retain a key/value entry type in its CLR shape projection.");
        }

        TsTypeNode result = new TsTypeReference(
            "Record",
            [
                new TsKeywordType(TsKeyword.String),
                TranslateType(valueField.Type)
            ]);
        return nullability == FieldNullability.Nullable
            ? UnionWithNull(result)
            : result;
    }

    TsTypeNode TranslateFieldType(TypeRef type, FieldCardinality cardinality, FieldNullability nullability)
    {
        return ApplyFieldModifiers(TranslateType(type), cardinality, nullability);
    }

    TsTypeNode ApplyFieldModifiers(
        TsTypeNode type,
        FieldCardinality cardinality,
        FieldNullability nullability)
    {
        TsTypeNode result = type;
        if (cardinality == FieldCardinality.Many)
            result = new TsArrayType(ParenthesizeIfNeeded(result));

        if (nullability == FieldNullability.Nullable)
            result = UnionWithNull(result);

        return result;
    }

    TsTypeNode TranslateType(TypeRef type)
    {
        return type switch
        {
            NamedTypeRef named => new TsTypeReference(ResolveTypeName(named)),
            OpaqueRuntimeTypeRef => new TsKeywordType(TsKeyword.Unknown),
            JsonTypeRef => new TsKeywordType(TsKeyword.Unknown),
            ScalarTypeRef scalar => TranslateScalar(scalar),
            EnumTypeRef @enum => BuildInlineEnumType(@enum),
            EntityReferenceTypeRef => new TsKeywordType(TsKeyword.String),
            ArrayTypeRef array => new TsArrayType(ParenthesizeIfNeeded(TranslateType(array.ElementType))),
            ObjectTypeRef obj => TranslateObjectType(obj),
            QuantityTypeRef => new TsKeywordType(TsKeyword.Number),
            _ => throw new InvalidOperationException($"Unsupported type reference '{type.GetType().Name}'.")
        };
    }

    TsTypeNode TranslateScalar(ScalarTypeRef scalar)
    {
        return scalar.Kind switch
        {
            ScalarTypeKind.Bool => new TsKeywordType(TsKeyword.Boolean),
            ScalarTypeKind.Int32 => new TsKeywordType(TsKeyword.Number),
            ScalarTypeKind.Int64 => new TsKeywordType(TsKeyword.Number),
            ScalarTypeKind.Decimal => new TsKeywordType(TsKeyword.Number),
            ScalarTypeKind.String => new TsKeywordType(TsKeyword.String),
            ScalarTypeKind.Guid => new TsKeywordType(TsKeyword.String),
            ScalarTypeKind.Date => new TsKeywordType(TsKeyword.String),
            ScalarTypeKind.DateTime => new TsKeywordType(TsKeyword.String),
            ScalarTypeKind.Instant => new TsKeywordType(TsKeyword.String),
            ScalarTypeKind.Bytes => new TsTypeReference("Uint8Array"),
            _ => new TsKeywordType(TsKeyword.Unknown)
        };
    }

    TsTypeNode TranslateObjectType(ObjectTypeRef obj)
    {
        var members = ImmutableArray.CreateBuilder<TsPropertySignature>(obj.Fields.Length);
        for (var i = 0; i < obj.Fields.Length; i++)
        {
            var field = obj.Fields[i];
            members.Add(new TsPropertySignature(
                name: field.Name,
                type: TranslateType(field.Type),
                isOptional: field.Presence == FieldPresence.Optional));
        }

        return new TsTypeLiteral(members.ToImmutable());
    }

    TsTypeNode BuildInlineEnumType(EnumTypeRef @enum)
    {
        var members = ImmutableArray.CreateBuilder<TsTypeNode>(@enum.Members.Length);
        for (var i = 0; i < @enum.Members.Length; i++)
            members.Add(new TsLiteralType(@enum.Members[i]));

        return members.Count == 1
            ? members[0]
            : new TsUnionType(members.ToImmutable());
    }

    TsTypeNode BuildEnumType(TypeDefinition.Enum @enum)
    {
        var members = ImmutableArray.CreateBuilder<TsTypeNode>(@enum.Values.Length);
        for (var i = 0; i < @enum.Values.Length; i++)
        {
            var value = @enum.Values[i];
            members.Add(BuildEnumLiteral(@enum, value));
        }

        return members.Count == 1
            ? members[0]
            : new TsUnionType(members.ToImmutable());
    }

    TsTypeNode BuildEnumLiteral(TypeDefinition.Enum @enum, EnumValue value)
    {
        if (!TryGetJsonEnumValue(@enum, value, out var wireValue)
            && (@enum.Underlying == PrimitiveType.Int32 || @enum.Underlying == PrimitiveType.Int64)
            && long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return new TsLiteralType(numeric);
        }

        return new TsLiteralType(wireValue ?? value.Value ?? value.Name);
    }

    TsStatement BuildEnumLabelMap(TypeDefinition.Enum @enum)
    {
        var typeName = ResolveDefinitionName(@enum.Id);
        var properties = ImmutableArray.CreateBuilder<TsObjectProperty>(@enum.Values.Length);
        for (var i = 0; i < @enum.Values.Length; i++)
        {
            var value = @enum.Values[i];
            properties.Add(new TsObjectProperty(
                name: GetEnumObjectKey(@enum, value),
                value: new TsStringLiteralExpression(GetEnumLabel(value)),
                isNumericName: IsNumericEnum(@enum)));
        }

        return new TsConstDeclaration(
            name: ToCamelCase(typeName) + "Labels",
            initializer: new TsObjectLiteralExpression(properties.ToImmutable()),
            type: new TsRawType($"Record<{typeName}, string>"));
    }

    TsStatement BuildEnumValueMap(TypeDefinition.Enum @enum)
    {
        var typeName = ResolveDefinitionName(@enum.Id);
        var properties = ImmutableArray.CreateBuilder<TsObjectProperty>(@enum.Values.Length);
        for (var i = 0; i < @enum.Values.Length; i++)
        {
            var value = @enum.Values[i];
            properties.Add(new TsObjectProperty(
                name: ToCamelCase(value.Name),
                value: BuildEnumLiteralExpression(@enum, value)));
        }

        return new TsConstDeclaration(
            name: ToPluralCamelCase(typeName),
            initializer: new TsObjectLiteralExpression(properties.ToImmutable()),
            satisfiesType: new TsRawType($"Record<string, {typeName}>"),
            asConst: true);
    }

    TsExpression BuildEnumLiteralExpression(TypeDefinition.Enum @enum, EnumValue value)
    {
        if (!TryGetJsonEnumValue(@enum, value, out var wireValue)
            && IsNumericEnum(@enum)
            && long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return new TsNumberLiteralExpression(numeric);
        }

        if (wireValue is null
            && @enum.Underlying == PrimitiveType.Bool
            && bool.TryParse(value.Value, out var boolean))
            return new TsBooleanLiteralExpression(boolean);

        return new TsStringLiteralExpression(wireValue ?? value.Value ?? value.Name);
    }

    static string GetEnumObjectKey(TypeDefinition.Enum @enum, EnumValue value)
    {
        if (!TryGetJsonEnumValue(@enum, value, out var wireValue)
            && IsNumericEnum(@enum)
            && long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric.ToString(CultureInfo.InvariantCulture);
        }

        return wireValue ?? value.Value ?? value.Name;
    }

    static bool IsNumericEnum(TypeDefinition.Enum @enum) =>
        !HasAnnotation(@enum.Annotations, SystemTextJsonShapeAnnotations.EnumValues)
        && (@enum.Underlying == PrimitiveType.Int32 || @enum.Underlying == PrimitiveType.Int64);

    static string GetEnumLabel(EnumValue value)
    {
        var normalized = value.Label?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? value.Name : normalized;
    }

    static bool TryGetJsonEnumValue(
        TypeDefinition.Enum @enum,
        EnumValue value,
        out string? wireValue)
    {
        if (!@enum.Annotations.TryGetValue(
                new AnnotationKey(SystemTextJsonShapeAnnotations.EnumValues),
                out var annotation))
        {
            wireValue = null;
            return false;
        }

        if (annotation.Value is not JsonObject values
            || values[value.Name] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out wireValue))
        {
            throw new InvalidOperationException(
                $"System.Text.Json enum metadata for '{@enum.Id.Value}' does not define string member '{value.Name}'.");
        }

        return true;
    }

    static bool TryTranslateJsonRepresentation(
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations,
        out TsTypeNode representation)
    {
        if (!TryGetStringAnnotation(
                annotations,
                SystemTextJsonShapeAnnotations.Representation,
                out var value))
        {
            representation = null!;
            return false;
        }

        representation = value switch
        {
            SystemTextJsonShapeAnnotations.String => new TsKeywordType(TsKeyword.String),
            SystemTextJsonShapeAnnotations.Number => new TsKeywordType(TsKeyword.Number),
            SystemTextJsonShapeAnnotations.Boolean => new TsKeywordType(TsKeyword.Boolean),
            SystemTextJsonShapeAnnotations.Unknown => new TsKeywordType(TsKeyword.Unknown),
            _ => throw new InvalidOperationException(
                $"Unsupported System.Text.Json shape representation '{value}'.")
        };
        return true;
    }

    static IReadOnlyDictionary<AnnotationKey, AnnotationValue> GetAnnotations(TypeDefinition definition) =>
        definition switch
        {
            TypeDefinition.Structural structural => structural.Annotations,
            TypeDefinition.Enum @enum => @enum.Annotations,
            TypeDefinition.Union union => union.Annotations,
            _ => throw new InvalidOperationException(
                $"Unsupported named type definition '{definition.GetType().Name}'.")
        };

    static bool HasBooleanAnnotation(
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations,
        string key) =>
        annotations.TryGetValue(new(key), out var annotation)
        && annotation.Value is JsonValue value
        && value.TryGetValue<bool>(out var result)
        && result;

    static bool HasAnnotation(
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations,
        string key) => annotations.ContainsKey(new(key));

    static bool TryGetStringAnnotation(
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations,
        string key,
        out string value)
    {
        if (annotations.TryGetValue(new(key), out var annotation)
            && annotation.Value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    TsTypeNode BuildUnionType(TypeDefinition.Union union)
    {
        var members = ImmutableArray.CreateBuilder<TsTypeNode>(union.Cases.Length);
        for (var i = 0; i < union.Cases.Length; i++)
            members.Add(TranslateUnionCase(union, union.Cases[i]));

        return members.Count == 1
            ? members[0]
            : new TsUnionType(members.ToImmutable());
    }

    TsTypeNode TranslateUnionCase(TypeDefinition.Union union, UnionCase unionCase)
    {
        var discriminator = new TsTypeLiteral(
            [
                new TsPropertySignature(
                    name: union.Discriminator.FieldName,
                    type: BuildDiscriminatorLiteral(union.Discriminator.Type, unionCase.DiscriminatorValue),
                    isReadonly: true)
            ]);

        if (CanMergeUnionPayload(unionCase.Type))
        {
            return new TsIntersectionType(
                [
                    discriminator,
                    TranslateType(unionCase.Type)
                ]);
        }

        return new TsTypeLiteral(
            [
                new TsPropertySignature(
                    name: union.Discriminator.FieldName,
                    type: BuildDiscriminatorLiteral(union.Discriminator.Type, unionCase.DiscriminatorValue),
                    isReadonly: true),
                new TsPropertySignature(
                    name: "value",
                    type: TranslateType(unionCase.Type),
                    isReadonly: true)
            ]);
    }

    bool CanMergeUnionPayload(TypeRef type)
    {
        if (type is ObjectTypeRef)
            return true;

        if (type is not NamedTypeRef named)
            return false;

        if (!graph.TryGetType(named.TypeId, out var definition))
            return false;

        return definition is TypeDefinition.Structural;
    }

    TsTypeNode BuildDiscriminatorLiteral(PrimitiveType primitiveType, string value)
    {
        if ((primitiveType == PrimitiveType.Int32 || primitiveType == PrimitiveType.Int64)
            && long.TryParse(value, out var numeric))
        {
            return new TsLiteralType(numeric);
        }

        if (primitiveType == PrimitiveType.Bool && bool.TryParse(value, out var boolean))
            return new TsLiteralType(boolean);

        return new TsLiteralType(value);
    }

    TsTypeNode UnionWithNull(TsTypeNode type)
    {
        if (type is TsUnionType union)
        {
            var members = ImmutableArray.CreateBuilder<TsTypeNode>(union.Members.Length + 1);
            for (var i = 0; i < union.Members.Length; i++)
                members.Add(union.Members[i]);
            members.Add(new TsKeywordType(TsKeyword.Null));
            return new TsUnionType(members.ToImmutable());
        }

        return new TsUnionType([type, new TsKeywordType(TsKeyword.Null)]);
    }

    static TsTypeNode ParenthesizeIfNeeded(TsTypeNode type)
    {
        return type is TsUnionType or TsIntersectionType or TsTypeLiteral
            ? new TsParenthesizedType(type)
            : type;
    }

    string ResolveTypeName(NamedTypeRef named)
    {
        if (typeNameById.TryGetValue(named.TypeId, out var name))
            return name;

        return IdentifierFromId(named.TypeId.Value);
    }

    string ResolveDefinitionName(TypeId id) => typeNameById.TryGetValue(id, out var name) ? name : IdentifierFromId(id.Value);

    static string ResolveShapeName(ShapeId id) => IdentifierFromId(id.Value);

    bool IsExternalType(TypeId id) => TryGetExternalTypeModule(id, out _);

    bool IsExternalShape(ShapeId id) => TryGetExternalShapeModule(id, out _);

    bool TryGetExternalTypeModule(TypeId id, out TypeScriptExternalTypeModule module)
    {
        var value = id.Value;
        for (var i = 0; i < externalTypeModules.Length; i++)
        {
            var candidate = externalTypeModules[i];
            if (value.StartsWith(candidate.TypeIdPrefix, StringComparison.Ordinal))
            {
                module = candidate;
                return true;
            }
        }

        module = null!;
        return false;
    }

    bool TryGetExternalShapeModule(ShapeId id, out TypeScriptExternalTypeModule module)
    {
        var value = id.Value;
        for (var i = 0; i < externalTypeModules.Length; i++)
        {
            var candidate = externalTypeModules[i];
            if (!string.IsNullOrWhiteSpace(candidate.ShapeIdPrefix)
                && value.StartsWith(candidate.ShapeIdPrefix, StringComparison.Ordinal))
            {
                module = candidate;
                return true;
            }
        }

        module = null!;
        return false;
    }

    static string IdentifierFromId(string id)
    {
        var value = id;
        var isClrId = value.StartsWith("clr:", StringComparison.Ordinal);
        var lastColon = value.LastIndexOf(':');
        if (lastColon >= 0 && lastColon + 1 < value.Length)
            value = value[(lastColon + 1)..];

        if (isClrId)
        {
            var lastDot = value.LastIndexOf('.');
            var lastNested = value.LastIndexOf('+');
            var lastSeparator = Math.Max(lastDot, lastNested);
            if (lastSeparator >= 0 && lastSeparator + 1 < value.Length)
                value = value[(lastSeparator + 1)..];

            var tickIndex = value.IndexOf('`', StringComparison.Ordinal);
            if (tickIndex >= 0)
                value = value[..tickIndex];

            return ToIdentifierPart(value);
        }

        var parts = value
            .Split(['.', ':', '/', '\\', '+', '-', '_', '`', '<', '>', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.Equals(x, "shape", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x, "type", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x, "clr", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (parts.Length == 0)
            return "_";

        return string.Concat(parts.Select(ToIdentifierPart));
    }

    static string ToIdentifierPart(string value)
    {
        if (value.Length == 0)
            return string.Empty;

        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        if (chars.Length == 0)
            return string.Empty;

        var text = new string(chars);
        return char.IsDigit(text[0])
            ? $"_{text}"
            : char.ToUpperInvariant(text[0]) + text[1..];
    }

    static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (!char.IsUpper(value[0]))
            return value;

        var chars = value.ToCharArray();
        var leadingUpperCount = 1;
        while (leadingUpperCount < chars.Length && char.IsUpper(chars[leadingUpperCount]))
            leadingUpperCount++;

        if (leadingUpperCount == chars.Length)
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = char.ToLowerInvariant(chars[i]);

            return new string(chars);
        }

        var charsToLower = leadingUpperCount == 1 ? 1 : leadingUpperCount - 1;
        for (var i = 0; i < charsToLower; i++)
            chars[i] = char.ToLowerInvariant(chars[i]);

        return new string(chars);
    }

    static string ToPluralCamelCase(string value) => Pluralize(ToCamelCase(value));

    static string Pluralize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.EndsWith("y", StringComparison.Ordinal)
            && value.Length > 1
            && !IsAsciiVowel(value[^2]))
        {
            return value[..^1] + "ies";
        }

        if (value.EndsWith("s", StringComparison.Ordinal)
            || value.EndsWith("x", StringComparison.Ordinal)
            || value.EndsWith("z", StringComparison.Ordinal)
            || value.EndsWith("ch", StringComparison.Ordinal)
            || value.EndsWith("sh", StringComparison.Ordinal))
        {
            return value + "es";
        }

        return value + "s";
    }

    static bool IsAsciiVowel(char value) =>
        value is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';

    static bool StructuralMatchesShape(TypeDefinition.Structural structural, Shape shape)
    {
        if (structural.Fields.Length != shape.Fields.Length)
            return false;

        for (var i = 0; i < structural.Fields.Length; i++)
        {
            var left = structural.Fields[i];
            var right = shape.Fields[i];
            if (left.Name != right.Name
                || left.Type != right.Type
                || left.Cardinality != right.Cardinality
                || left.Presence != right.Presence
                || left.Nullability != right.Nullability
                || left.Role != right.Role)
            {
                return false;
            }
        }

        return true;
    }
}
