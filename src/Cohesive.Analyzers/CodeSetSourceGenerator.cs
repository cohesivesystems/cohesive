using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cohesive.Analyzers;

/// <summary>
/// Generates catalog members for code-set types marked with <c>[CodeSet]</c>.
/// </summary>
[Generator]
public sealed class CodeSetSourceGenerator : IIncrementalGenerator
{
    const string CodeAttributeMetadataName = "Cohesive.Domain.CodeAttribute";
    const string CodeSetAttributeMetadataName = "Cohesive.Domain.CodeSetAttribute";
    const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";

    static readonly SymbolDisplayFormat FullyQualifiedNullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor CodeSetTypeMustBePartial = CreateDiagnostic(
        id: "COHCODE001",
        title: "Code set type must be partial",
        messageFormat: "Type '{0}' is marked with [CodeSet] and must be declared partial.",
        description: "Code-set generation adds catalog members into the annotated type, which requires a partial declaration.");

    static readonly DiagnosticDescriptor CodeSetTypeMustBeStaticTopLevelClass = CreateDiagnostic(
        id: "COHCODE002",
        title: "Code set type must be a static top-level class",
        messageFormat: "Type '{0}' is marked with [CodeSet] but only static top-level classes are supported.",
        description: "Code-set generation targets static classes of named fields so generated member access remains predictable.");

    static readonly DiagnosticDescriptor CodeFieldIsInvalid = CreateDiagnostic(
        id: "COHCODE003",
        title: "Code field is invalid",
        messageFormat: "Field '{0}' is included in a code set but must be a public static const or readonly field.",
        description: "Code catalogs expose public static code values and do not include mutable or instance fields.");

    static readonly DiagnosticDescriptor CodeSetFieldsMustShareValueType = CreateDiagnostic(
        id: "COHCODE004",
        title: "Code set fields must share one value type",
        messageFormat: "Type '{0}' contains code fields with multiple value types.",
        description: "Generated All and Definitions members require a single value type for every code in a set.");

    static readonly DiagnosticDescriptor DuplicateCodeValue = CreateDiagnostic(
        id: "COHCODE005",
        title: "Duplicate constant code value",
        messageFormat: "Field '{0}' duplicates constant code value '{1}' already declared by field '{2}'.",
        description: "Constant code values in one generated code set must be unique.");

    static readonly DiagnosticDescriptor GeneratedMemberConflict = CreateDiagnostic(
        id: "COHCODE006",
        title: "Code set generated member conflicts with an existing member",
        messageFormat: "Type '{0}' already declares member '{1}', which conflicts with generated code-set members.",
        description: "Code-set generation emits All, Definitions, and TryGet members.");

    static readonly DiagnosticDescriptor DuplicateEnumCodeValue = CreateDiagnostic(
        id: "COHCODE007",
        title: "Duplicate enum code value",
        messageFormat: "Enum member '{0}' duplicates code value '{1}' already declared by member '{2}'.",
        description: "Generated enum code schemas require unique external code values so Parse remains deterministic.");

    static DiagnosticDescriptor CreateDiagnostic(string id, string title, string messageFormat, string description) =>
        new(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: "Cohesive.Codes",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: description);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var codeSetSymbols = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: CodeSetAttributeMetadataName,
                predicate: static (_, _) => true,
                transform: static (generatorContext, _) => generatorContext.TargetSymbol)
            .Collect();

        var codeSymbols = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: CodeAttributeMetadataName,
                predicate: static (_, _) => true,
                transform: static (generatorContext, _) => generatorContext.TargetSymbol)
            .Collect();

        var annotatedSymbols = codeSetSymbols
            .Combine(codeSymbols);

        context.RegisterSourceOutput(annotatedSymbols, static (productionContext, symbols) =>
        {
            var combinedSymbols = symbols.Left
                .AddRange(symbols.Right);
            var codeSetTypes = CollectCodeSetTypes(combinedSymbols);
            foreach (var type in codeSetTypes)
                GenerateForCodeSetType(productionContext: productionContext, codeSetType: type);
        });
    }

    static ImmutableArray<INamedTypeSymbol> CollectCodeSetTypes(ImmutableArray<ISymbol> symbols)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (var symbol in symbols)
        {
            INamedTypeSymbol? type = symbol switch
            {
                INamedTypeSymbol namedType => namedType,
                IFieldSymbol field => field.ContainingType,
                _ => null
            };

            if (type is not null && seen.Add(type))
                builder.Add(type);
        }

        builder.Sort(static (left, right) => string.CompareOrdinal(left.ToDisplayString(), right.ToDisplayString()));
        return builder.ToImmutable();
    }

    static void GenerateForCodeSetType(SourceProductionContext productionContext, INamedTypeSymbol codeSetType)
    {
        if (codeSetType.TypeKind == TypeKind.Enum)
        {
            GenerateForEnumCodeSet(productionContext: productionContext, enumType: codeSetType);
            return;
        }

        if (codeSetType.TypeKind != TypeKind.Class
            || !codeSetType.IsStatic
            || codeSetType.ContainingType is not null
            || codeSetType.TypeParameters.Length != 0)
        {
            Report(
                productionContext: productionContext,
                descriptor: CodeSetTypeMustBeStaticTopLevelClass,
                symbol: codeSetType,
                args: [codeSetType.ToDisplayString()]);
            return;
        }

        if (!IsPartial(symbol: codeSetType))
        {
            Report(
                productionContext: productionContext,
                descriptor: CodeSetTypeMustBePartial,
                symbol: codeSetType,
                args: [codeSetType.ToDisplayString()]);
            return;
        }

        if (!ValidateGeneratedMemberAvailability(productionContext: productionContext, codeSetType: codeSetType))
            return;

        var typeHasCodeSetAttribute = HasCodeSetAttribute(symbol: codeSetType);
        var fields = GetCodeFields(codeSetType: codeSetType, includeByConvention: typeHasCodeSetAttribute);
        if (fields.IsDefaultOrEmpty)
            return;

        foreach (var field in fields)
        {
            if (!IsValidCodeField(field))
            {
                Report(
                    productionContext: productionContext,
                    descriptor: CodeFieldIsInvalid,
                    symbol: field,
                    args: [field.ToDisplayString()]);
                return;
            }
        }

        if (!ValidateSingleValueType(
                productionContext: productionContext,
                codeSetType: codeSetType,
                fields: fields,
                valueType: out var valueType))
        {
            return;
        }

        if (!ValidateConstantUniqueness(
                productionContext: productionContext,
                fields: fields))
        {
            return;
        }

        var definitions = fields
            .Select(static field => CodeFieldDefinition.From(field))
            .ToImmutableArray();

        var source = EmitCodeSet(
            codeSetType: codeSetType,
            valueType: valueType,
            definitions: definitions);

        productionContext.AddSource(
            hintName: BuildHintName(codeSetType: codeSetType),
            sourceText: SourceText.From(text: source, encoding: Encoding.UTF8));
    }

    static void GenerateForEnumCodeSet(SourceProductionContext productionContext, INamedTypeSymbol enumType)
    {
        if (!HasCodeSetAttribute(enumType)
            && !enumType.GetMembers().OfType<IFieldSymbol>().Any(HasCodeAttribute))
        {
            return;
        }

        var fields = GetEnumFields(enumType);
        if (fields.IsDefaultOrEmpty)
            return;

        var definitions = fields
            .Select(static field => EnumCodeDefinition.From(field))
            .ToImmutableArray();

        if (!ValidateEnumCodeUniqueness(
                productionContext: productionContext,
                definitions: definitions))
        {
            return;
        }

        var source = EmitEnumCodeSet(
            enumType: enumType,
            definitions: definitions);

        productionContext.AddSource(
            hintName: BuildHintName(codeSetType: enumType),
            sourceText: SourceText.From(text: source, encoding: Encoding.UTF8));
    }

    static bool IsPartial(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(static syntaxReference =>
        {
            if (syntaxReference.GetSyntax() is not TypeDeclarationSyntax declaration)
                return false;

            return declaration.Modifiers.Any(static token => token.IsKind(SyntaxKind.PartialKeyword));
        });

    static ImmutableArray<IFieldSymbol> GetCodeFields(INamedTypeSymbol codeSetType, bool includeByConvention)
    {
        var fields = codeSetType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => includeByConvention
                ? field.DeclaredAccessibility == Accessibility.Public && field.IsStatic
                : HasCodeSetAttribute(field))
            .Where(static field => !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static field => field.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
            .ThenBy(static field => field.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        return fields;
    }

    static bool IsValidCodeField(IFieldSymbol field) =>
        field.DeclaredAccessibility == Accessibility.Public
        && field.IsStatic
        && (field.IsConst || field.IsReadOnly);

    static bool ValidateGeneratedMemberAvailability(SourceProductionContext productionContext, INamedTypeSymbol codeSetType)
    {
        if (HasNonMethodMember(codeSetType: codeSetType, name: "All"))
        {
            ReportConflict(productionContext: productionContext, codeSetType: codeSetType, memberName: "All");
            return false;
        }

        if (HasNonMethodMember(codeSetType: codeSetType, name: "Definitions"))
        {
            ReportConflict(productionContext: productionContext, codeSetType: codeSetType, memberName: "Definitions");
            return false;
        }

        if (codeSetType.GetMembers(name: "TryGet")
            .OfType<IMethodSymbol>()
            .Any(static method => method.Parameters.Length == 2))
        {
            ReportConflict(productionContext: productionContext, codeSetType: codeSetType, memberName: "TryGet");
            return false;
        }

        return true;
    }

    static bool HasNonMethodMember(INamedTypeSymbol codeSetType, string name) =>
        codeSetType.GetMembers(name).Any(static member => member.Kind != SymbolKind.Method);

    static void ReportConflict(SourceProductionContext productionContext, INamedTypeSymbol codeSetType, string memberName)
    {
        Report(
            productionContext: productionContext,
            descriptor: GeneratedMemberConflict,
            symbol: codeSetType,
            args: [codeSetType.ToDisplayString(), memberName]);
    }

    static bool ValidateSingleValueType(
        SourceProductionContext productionContext,
        INamedTypeSymbol codeSetType,
        ImmutableArray<IFieldSymbol> fields,
        out ITypeSymbol valueType)
    {
        valueType = fields[0].Type;
        foreach (var field in fields.Skip(1))
        {
            if (SymbolEqualityComparer.IncludeNullability.Equals(valueType, field.Type))
                continue;

            Report(
                productionContext: productionContext,
                descriptor: CodeSetFieldsMustShareValueType,
                symbol: field,
                args: [codeSetType.ToDisplayString()]);
            return false;
        }

        return true;
    }

    static bool ValidateConstantUniqueness(SourceProductionContext productionContext, ImmutableArray<IFieldSymbol> fields)
    {
        var constantsByValue = new Dictionary<object, IFieldSymbol>();
        foreach (var field in fields)
        {
            if (!field.HasConstantValue || field.ConstantValue is null)
                continue;

            if (!constantsByValue.TryGetValue(field.ConstantValue, out var existingField))
            {
                constantsByValue[field.ConstantValue] = field;
                continue;
            }

            Report(
                productionContext: productionContext,
                descriptor: DuplicateCodeValue,
                symbol: field,
                args: [field.ToDisplayString(), field.ConstantValue, existingField.ToDisplayString()]);
            return false;
        }

        return true;
    }

    static ImmutableArray<IFieldSymbol> GetEnumFields(INamedTypeSymbol enumType)
    {
        return enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field is { IsStatic: true, HasConstantValue: true, IsImplicitlyDeclared: false })
            .OrderBy(static field => field.Locations.FirstOrDefault()?.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static field => field.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
            .ThenBy(static field => field.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    static bool ValidateEnumCodeUniqueness(SourceProductionContext productionContext, ImmutableArray<EnumCodeDefinition> definitions)
    {
        var definitionsByCode = new Dictionary<string, EnumCodeDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!definitionsByCode.TryGetValue(definition.Code, out var existingDefinition))
            {
                definitionsByCode[definition.Code] = definition;
                continue;
            }

            Report(
                productionContext: productionContext,
                descriptor: DuplicateEnumCodeValue,
                symbol: definition.Symbol,
                args: [definition.Symbol.ToDisplayString(), definition.Code, existingDefinition.Symbol.ToDisplayString()]);
            return false;
        }

        return true;
    }

    static string EmitCodeSet(
        INamedTypeSymbol codeSetType,
        ITypeSymbol valueType,
        ImmutableArray<CodeFieldDefinition> definitions)
    {
        var valueTypeName = valueType.ToDisplayString(FullyQualifiedNullableFormat);
        var accessibility = GetAccessibilityKeyword(accessibility: codeSetType.DeclaredAccessibility);
        var typeName = EscapeIdentifier(codeSetType.Name);
        var namespaceName = codeSetType.ContainingNamespace.IsGlobalNamespace
            ? null
            : codeSetType.ContainingNamespace.ToDisplayString();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine();
            builder.AppendLine("{");
        }

        var indent = string.IsNullOrWhiteSpace(namespaceName) ? string.Empty : "    ";
        builder.Append(indent)
            .Append(accessibility)
            .Append(" static partial class ")
            .Append(typeName)
            .AppendLine();
        builder.Append(indent).AppendLine("{");

        EmitAll(builder: builder, indent: indent + "    ", valueTypeName: valueTypeName, definitions: definitions);
        builder.AppendLine();
        EmitDefinitions(builder: builder, indent: indent + "    ", valueTypeName: valueTypeName, definitions: definitions);
        builder.AppendLine();
        EmitTryGet(builder: builder, indent: indent + "    ", valueTypeName: valueTypeName);

        builder.Append(indent).AppendLine("}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            builder.AppendLine("}");

        return builder.ToString();
    }

    static string EmitEnumCodeSet(INamedTypeSymbol enumType, ImmutableArray<EnumCodeDefinition> definitions)
    {
        var enumTypeName = enumType.ToDisplayString(FullyQualifiedNullableFormat);
        var extensionReceiverTypeName = BuildExtensionReceiverTypeName(enumType);
        var underlyingTypeName = enumType.EnumUnderlyingType?.ToDisplayString(FullyQualifiedNullableFormat) ?? "int";
        var accessibility = GetAccessibilityKeyword(accessibility: enumType.DeclaredAccessibility);
        var extensionTypeName = EscapeIdentifier(enumType.Name + "Extensions");
        var namespaceName = enumType.ContainingNamespace.IsGlobalNamespace
            ? null
            : enumType.ContainingNamespace.ToDisplayString();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine();
            builder.AppendLine("{");
        }

        var indent = string.IsNullOrWhiteSpace(namespaceName) ? string.Empty : "    ";
        builder.Append(indent)
            .Append(accessibility)
            .Append(" static partial class ")
            .Append(extensionTypeName)
            .AppendLine();
        builder.Append(indent).AppendLine("{");

        EmitEnumDefinitions(builder: builder, indent: indent + "    ", definitions: definitions);
        builder.AppendLine();
        EmitEnumGetAll(builder: builder, indent: indent + "    ", extensionReceiverTypeName: extensionReceiverTypeName);
        builder.AppendLine();
        EmitEnumGetCode(builder: builder, indent: indent + "    ", enumTypeName: enumTypeName, extensionReceiverTypeName: extensionReceiverTypeName, underlyingTypeName: underlyingTypeName, definitions: definitions);
        builder.AppendLine();
        EmitEnumGetDescription(builder: builder, indent: indent + "    ", enumTypeName: enumTypeName, extensionReceiverTypeName: extensionReceiverTypeName, definitions: definitions);
        builder.AppendLine();
        EmitEnumGetLabel(builder: builder, indent: indent + "    ", enumTypeName: enumTypeName, extensionReceiverTypeName: extensionReceiverTypeName, definitions: definitions);
        builder.AppendLine();
        EmitEnumParse(builder: builder, indent: indent + "    ", enumTypeName: enumTypeName, extensionReceiverTypeName: extensionReceiverTypeName, definitions: definitions);

        builder.Append(indent).AppendLine("}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            builder.AppendLine("}");

        return builder.ToString();
    }

    static void EmitEnumDefinitions(StringBuilder builder, string indent, ImmutableArray<EnumCodeDefinition> definitions)
    {
        builder.Append(indent)
            .AppendLine("static global::System.Collections.Generic.IReadOnlyList<global::Cohesive.Domain.CodeDefinition<string>> Definitions { get; } = new global::Cohesive.Domain.CodeDefinition<string>[]");
        builder.Append(indent).AppendLine("{");
        foreach (var definition in definitions)
        {
            builder.Append(indent)
                .Append("    new(")
                .Append(Literal(definition.MemberName))
                .Append(", ")
                .Append(Literal(definition.Code))
                .Append(", ")
                .Append(Literal(definition.Label))
                .Append(", ")
                .Append(Literal(definition.Description))
                .AppendLine("),");
        }

        builder.Append(indent).AppendLine("};");
    }

    static void EmitEnumGetAll(StringBuilder builder, string indent, string extensionReceiverTypeName)
    {
        builder.Append(indent)
            .Append("extension(")
            .Append(extensionReceiverTypeName)
            .AppendLine(")");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>");
        builder.Append(indent).AppendLine("    /// Gets every code definition declared by this code set.");
        builder.Append(indent).AppendLine("    /// </summary>");
        builder.Append(indent)
            .AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<global::Cohesive.Domain.CodeDefinition<string>> GetAll() => Definitions;");
        builder.Append(indent).AppendLine("}");
    }

    static void EmitEnumGetCode(
        StringBuilder builder,
        string indent,
        string enumTypeName,
        string extensionReceiverTypeName,
        string underlyingTypeName,
        ImmutableArray<EnumCodeDefinition> definitions)
    {
        builder.Append(indent)
            .Append("extension(")
            .Append(extensionReceiverTypeName)
            .AppendLine(" value)");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>");
        builder.Append(indent).AppendLine("    /// Gets the external code value for this enum value.");
        builder.Append(indent).AppendLine("    /// </summary>");
        builder.Append(indent).AppendLine("    public string GetCode()");
        builder.Append(indent).AppendLine("    {");
        foreach (var definition in definitions)
        {
            builder.Append(indent)
                .Append("        if (value == ")
                .Append(MemberAccess(enumTypeName: enumTypeName, memberName: definition.MemberName))
                .Append(") return ")
                .Append(Literal(definition.Code))
                .AppendLine(";");
        }

        builder.Append(indent)
            .Append("        return ((")
            .Append(underlyingTypeName)
            .Append(")value).ToString(global::System.Globalization.CultureInfo.InvariantCulture);")
            .AppendLine();
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");
    }

    static void EmitEnumGetDescription(
        StringBuilder builder,
        string indent,
        string enumTypeName,
        string extensionReceiverTypeName,
        ImmutableArray<EnumCodeDefinition> definitions)
    {
        builder.Append(indent)
            .Append("extension(")
            .Append(extensionReceiverTypeName)
            .AppendLine(" value)");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>");
        builder.Append(indent).AppendLine("    /// Gets the optional description for this enum value.");
        builder.Append(indent).AppendLine("    /// </summary>");
        builder.Append(indent).AppendLine("    public string? GetDescription()");
        builder.Append(indent).AppendLine("    {");
        foreach (var definition in definitions.Where(static definition => definition.Description is not null))
        {
            builder.Append(indent)
                .Append("        if (value == ")
                .Append(MemberAccess(enumTypeName: enumTypeName, memberName: definition.MemberName))
                .Append(") return ")
                .Append(Literal(definition.Description))
                .AppendLine(";");
        }

        builder.Append(indent).AppendLine("        return null;");
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");
    }

    static void EmitEnumGetLabel(
        StringBuilder builder,
        string indent,
        string enumTypeName,
        string extensionReceiverTypeName,
        ImmutableArray<EnumCodeDefinition> definitions)
    {
        builder.Append(indent)
            .Append("extension(")
            .Append(extensionReceiverTypeName)
            .AppendLine(" value)");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>");
        builder.Append(indent).AppendLine("    /// Gets the human-readable label for this enum value.");
        builder.Append(indent).AppendLine("    /// </summary>");
        builder.Append(indent).AppendLine("    public string GetLabel()");
        builder.Append(indent).AppendLine("    {");
        foreach (var definition in definitions)
        {
            builder.Append(indent)
                .Append("        if (value == ")
                .Append(MemberAccess(enumTypeName: enumTypeName, memberName: definition.MemberName))
                .Append(") return ")
                .Append(Literal(definition.Label))
                .AppendLine(";");
        }

        builder.Append(indent).AppendLine("        return value.ToString();");
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");
    }

    static void EmitEnumParse(
        StringBuilder builder,
        string indent,
        string enumTypeName,
        string extensionReceiverTypeName,
        ImmutableArray<EnumCodeDefinition> definitions)
    {
        builder.Append(indent)
            .Append("extension(")
            .Append(extensionReceiverTypeName)
            .AppendLine(")");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    /// <summary>");
        builder.Append(indent).AppendLine("    /// Parses an external code value into the corresponding enum value.");
        builder.Append(indent).AppendLine("    /// </summary>");
        builder.Append(indent)
            .Append("    public static ")
            .Append(enumTypeName)
            .AppendLine(" Parse(string code)");
        builder.Append(indent).AppendLine("    {");
        builder.Append(indent).AppendLine("        global::System.ArgumentNullException.ThrowIfNull(code);");
        foreach (var definition in definitions)
        {
            builder.Append(indent)
                .Append("        if (global::System.StringComparer.Ordinal.Equals(code, ")
                .Append(Literal(definition.Code))
                .Append(")) return ")
                .Append(MemberAccess(enumTypeName: enumTypeName, memberName: definition.MemberName))
                .AppendLine(";");
        }

        builder.Append(indent)
            .Append("        throw new global::System.ArgumentException(\"Code is not defined for ")
            .Append(enumTypeName.Replace("global::", string.Empty))
            .Append(".\", nameof(code));")
            .AppendLine();
        builder.Append(indent).AppendLine("    }");
        builder.Append(indent).AppendLine("}");
    }

    static void EmitAll(StringBuilder builder, string indent, string valueTypeName, ImmutableArray<CodeFieldDefinition> definitions)
    {
        builder.Append(indent).AppendLine("/// <summary>");
        builder.Append(indent).AppendLine("/// Gets every raw code value declared by this code set.");
        builder.Append(indent).AppendLine("/// </summary>");
        builder.Append(indent)
            .Append("public static global::System.Collections.Generic.IReadOnlyList<")
            .Append(valueTypeName)
            .Append("> All { get; } = new ")
            .Append(valueTypeName)
            .AppendLine("[]");
        builder.Append(indent).AppendLine("{");
        foreach (var definition in definitions)
            builder.Append(indent).Append("    ").Append(EscapeIdentifier(definition.FieldName)).AppendLine(",");
        builder.Append(indent).AppendLine("};");
    }

    static void EmitDefinitions(StringBuilder builder, string indent, string valueTypeName, ImmutableArray<CodeFieldDefinition> definitions)
    {
        builder.Append(indent).AppendLine("/// <summary>");
        builder.Append(indent).AppendLine("/// Gets every code definition declared by this code set.");
        builder.Append(indent).AppendLine("/// </summary>");
        builder.Append(indent)
            .Append("public static global::System.Collections.Generic.IReadOnlyList<global::Cohesive.Domain.CodeDefinition<")
            .Append(valueTypeName)
            .Append(">> Definitions { get; } = new global::Cohesive.Domain.CodeDefinition<")
            .Append(valueTypeName)
            .AppendLine(">[]");
        builder.Append(indent).AppendLine("{");
        foreach (var definition in definitions)
        {
            builder.Append(indent)
                .Append("    new(")
                .Append(Literal(definition.FieldName))
                .Append(", ")
                .Append(EscapeIdentifier(definition.FieldName))
                .Append(", ")
                .Append(Literal(definition.Label))
                .Append(", ")
                .Append(Literal(definition.Description))
                .AppendLine("),");
        }

        builder.Append(indent).AppendLine("};");
    }

    static void EmitTryGet(StringBuilder builder, string indent, string valueTypeName)
    {
        builder.Append(indent).AppendLine("/// <summary>");
        builder.Append(indent).AppendLine("/// Attempts to resolve metadata for a raw code value.");
        builder.Append(indent).AppendLine("/// </summary>");
        builder.Append(indent)
            .Append("public static bool TryGet(")
            .Append(valueTypeName)
            .Append(" value, out global::Cohesive.Domain.CodeDefinition<")
            .Append(valueTypeName)
            .AppendLine("> definition)");
        builder.Append(indent).AppendLine("{");
        builder.Append(indent).AppendLine("    foreach (var candidate in Definitions)");
        builder.Append(indent).AppendLine("    {");
        builder.Append(indent)
            .Append("        if (global::System.Collections.Generic.EqualityComparer<")
            .Append(valueTypeName)
            .AppendLine(">.Default.Equals(candidate.Value, value))");
        builder.Append(indent).AppendLine("        {");
        builder.Append(indent).AppendLine("            definition = candidate;");
        builder.Append(indent).AppendLine("            return true;");
        builder.Append(indent).AppendLine("        }");
        builder.Append(indent).AppendLine("    }");
        builder.AppendLine();
        builder.Append(indent).AppendLine("    definition = default;");
        builder.Append(indent).AppendLine("    return false;");
        builder.Append(indent).AppendLine("}");
    }

    static string GetAccessibilityKeyword(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "private"
        };

    static string BuildHintName(INamedTypeSymbol codeSetType)
    {
        var name = codeSetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty)
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('.', '_');

        return $"{name}.CodeSet.g.cs";
    }

    static string BuildExtensionReceiverTypeName(INamedTypeSymbol enumType)
    {
        var containingTypes = new Stack<string>();
        for (var current = enumType; current is not null; current = current.ContainingType)
            containingTypes.Push(EscapeIdentifier(current.Name));

        return string.Join(".", containingTypes);
    }

    static bool HasCodeSetAttribute(ISymbol symbol) =>
        GetCodeSetAttribute(symbol: symbol) is not null;

    static AttributeData? GetCodeSetAttribute(ISymbol symbol) =>
        symbol.GetAttributes()
            .FirstOrDefault(static attribute => string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                CodeSetAttributeMetadataName,
                StringComparison.Ordinal));

    static bool HasCodeAttribute(ISymbol symbol) =>
        GetCodeAttribute(symbol: symbol) is not null;

    static AttributeData? GetCodeAttribute(ISymbol symbol) =>
        symbol.GetAttributes()
            .FirstOrDefault(static attribute => string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                CodeAttributeMetadataName,
                StringComparison.Ordinal));

    static string? GetCodeAttributeCode(ISymbol symbol)
    {
        var attribute = GetCodeAttribute(symbol: symbol);
        if (attribute is null || attribute.ConstructorArguments.Length == 0)
            return null;

        return attribute.ConstructorArguments[0].Value as string;
    }

    static string? GetCodeAttributeLabel(ISymbol symbol)
        => GetCodeAttributeStringProperty(symbol: symbol, name: "Label");

    static string? GetCodeAttributeDescription(ISymbol symbol)
        => GetCodeAttributeStringProperty(symbol: symbol, name: "Description");

    static string? GetCodeAttributeStringProperty(ISymbol symbol, string name)
    {
        var attribute = GetCodeAttribute(symbol: symbol);
        if (attribute is null)
            return null;

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == name)
                return NormalizeOptional(namedArgument.Value.Value as string);
        }

        return null;
    }

    static string? GetCodeSetAttributeLabel(ISymbol symbol)
    {
        var attribute = GetCodeSetAttribute(symbol: symbol);
        if (attribute is null || attribute.ConstructorArguments.Length == 0)
            return null;

        var value = attribute.ConstructorArguments[0].Value as string;
        return NormalizeOptional(value);
    }

    static string? GetCodeSetAttributeDescription(ISymbol symbol)
    {
        var attribute = GetCodeSetAttribute(symbol: symbol);
        if (attribute is null)
            return null;

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "Description")
                return NormalizeOptional(namedArgument.Value.Value as string);
        }

        return null;
    }

    static string? GetDescriptionAttributeValue(ISymbol symbol)
    {
        var attribute = symbol.GetAttributes()
            .FirstOrDefault(static candidate => string.Equals(
                candidate.AttributeClass?.ToDisplayString(),
                DescriptionAttributeMetadataName,
                StringComparison.Ordinal));

        if (attribute is null || attribute.ConstructorArguments.Length == 0)
            return null;

        return NormalizeOptional(attribute.ConstructorArguments[0].Value as string);
    }

    static string SplitIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return string.Empty;

        var builder = new StringBuilder(identifier.Length + 8);
        for (var index = 0; index < identifier.Length; index++)
        {
            if (index > 0 && ShouldInsertSpace(identifier, index))
                builder.Append(' ');

            builder.Append(identifier[index]);
        }

        return builder.ToString();
    }

    static bool ShouldInsertSpace(string value, int index)
    {
        var current = value[index];
        var previous = value[index - 1];

        if (char.IsDigit(current))
            return char.IsLower(previous);

        if (char.IsDigit(previous))
            return true;

        if (!char.IsUpper(current))
            return false;

        return char.IsLower(previous)
               || (char.IsUpper(previous)
                   && index + 1 < value.Length
                   && char.IsLower(value[index + 1]));
    }

    static string EscapeIdentifier(string identifier)
    {
        if (SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
            || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None)
        {
            return "@" + identifier;
        }

        return identifier;
    }

    static string MemberAccess(string enumTypeName, string memberName) =>
        enumTypeName + "." + EscapeIdentifier(memberName);

    static string Literal(string? value) =>
        value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    static void Report(SourceProductionContext productionContext, DiagnosticDescriptor descriptor, ISymbol symbol, object?[] args)
    {
        productionContext.ReportDiagnostic(Diagnostic.Create(
            descriptor: descriptor,
            location: symbol.Locations.FirstOrDefault(),
            messageArgs: args));
    }

    readonly record struct CodeFieldDefinition(string FieldName, string Label, string? Description)
    {
        public static CodeFieldDefinition From(IFieldSymbol field)
        {
            var label = GetCodeSetAttributeLabel(symbol: field) ?? SplitIdentifier(identifier: field.Name);
            var description = GetCodeSetAttributeDescription(symbol: field) ?? GetDescriptionAttributeValue(symbol: field);
            return new(
                FieldName: field.Name,
                Label: label,
                Description: description);
        }
    }

    readonly record struct EnumCodeDefinition(
        IFieldSymbol Symbol,
        string MemberName,
        string Code,
        string Label,
        string? Description)
    {
        public static EnumCodeDefinition From(IFieldSymbol field)
        {
            var code = GetCodeAttributeCode(symbol: field) ?? FormatEnumNumericValue(field.ConstantValue);
            var label = GetCodeAttributeLabel(symbol: field) ?? GetCodeSetAttributeLabel(symbol: field) ?? field.Name;
            var description = GetCodeAttributeDescription(symbol: field)
                              ?? GetCodeSetAttributeDescription(symbol: field)
                              ?? GetDescriptionAttributeValue(symbol: field);

            return new(
                Symbol: field,
                MemberName: field.Name,
                Code: code,
                Label: label,
                Description: description);
        }

        static string FormatEnumNumericValue(object? value)
        {
            return global::System.Convert.ToString(
                       value,
                       global::System.Globalization.CultureInfo.InvariantCulture)
                   ?? string.Empty;
        }
    }
}
