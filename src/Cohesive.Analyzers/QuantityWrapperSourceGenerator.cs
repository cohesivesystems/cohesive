using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cohesive.Analyzers;

/// <summary>
/// Generates quantity-wrapper boilerplate for types marked with <c>[Quantity]</c>.
/// </summary>
[Generator]
public sealed class QuantityWrapperSourceGenerator : IIncrementalGenerator
{
    static readonly SymbolDisplayFormat FullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor QuantityTypeMustBePartial = new(
        id: "COHQTY001",
        title: "Quantity wrapper type must be partial",
        messageFormat: "Type '{0}' is marked with [Quantity] and must be declared partial.",
        category: "Cohesive.Quantities",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Quantity wrapper generation adds members into the annotated type and therefore requires a partial declaration.");

    static readonly DiagnosticDescriptor QuantityTypeMustBeTopLevelStruct = new(
        id: "COHQTY002",
        title: "Quantity wrapper must be a top-level struct",
        messageFormat: "Type '{0}' is marked with [Quantity] but only top-level struct declarations are supported.",
        category: "Cohesive.Quantities",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nested or non-struct quantity wrappers are not supported by source generation.");

    static readonly DiagnosticDescriptor QuantityTypeMustExposeValueProperty = new(
        id: "COHQTY003",
        title: "Quantity wrapper must expose a Value property",
        messageFormat: "Type '{0}' is marked with [Quantity] but does not expose an instance property named 'Value' of type Quantity<TDimension, TRep>.",
        category: "Cohesive.Quantities",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Generated members require a canonical Value property carrying Quantity<TDimension, TRep>.");

    static readonly DiagnosticDescriptor QuantityUnitMemberIsInvalid = new(
        id: "COHQTY004",
        title: "Quantity unit member metadata is invalid",
        messageFormat: "Type '{0}' contains invalid [QuantityUnitMember] metadata: {1}.",
        category: "Cohesive.Quantities",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each [QuantityUnitMember] must provide a valid member name and a unit type compatible with the wrapper dimension and representation.");

    static readonly DiagnosticDescriptor QuantityWrapperMetadataIsInvalid = new(
        id: "COHQTY005",
        title: "Quantity wrapper metadata is invalid",
        messageFormat: "Type '{0}' contains invalid [Quantity] metadata: {1}.",
        category: "Cohesive.Quantities",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The [Quantity] attribute must provide a default unit type that matches the wrapper dimension and representation.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var wrapperTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "Cohesive.Domain.QuantityAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (generatorContext, _) => (INamedTypeSymbol)generatorContext.TargetSymbol)
            .Collect();

        context.RegisterSourceOutput(wrapperTypes, static (productionContext, symbols) =>
        {
            var seen = new HashSet<INamedTypeSymbol>(comparer: SymbolEqualityComparer.Default);
            foreach (var symbol in symbols)
            {
                if (!seen.Add(symbol))
                {
                    continue;
                }

                GenerateForWrapperType(productionContext: productionContext, wrapperType: symbol);
            }
        });
    }

    static void GenerateForWrapperType(SourceProductionContext productionContext, INamedTypeSymbol wrapperType)
    {
        if (wrapperType.TypeKind != TypeKind.Struct || wrapperType.ContainingType is not null)
        {
            Report(
                productionContext: productionContext,
                descriptor: QuantityTypeMustBeTopLevelStruct,
                wrapperType: wrapperType,
                args: [wrapperType.ToDisplayString()]);
            return;
        }

        if (!IsPartial(symbol: wrapperType))
        {
            Report(
                productionContext: productionContext,
                descriptor: QuantityTypeMustBePartial,
                wrapperType: wrapperType,
                args: [wrapperType.ToDisplayString()]);
            return;
        }

        if (!TryGetValuePropertyType(wrapperType: wrapperType, out var valueType, out var dimensionType, out var representationType))
        {
            Report(
                productionContext: productionContext,
                descriptor: QuantityTypeMustExposeValueProperty,
                wrapperType: wrapperType,
                args: [wrapperType.ToDisplayString()]);
            return;
        }

        if (!TryGetWrapperMetadata(
                wrapperType: wrapperType,
                dimensionType: dimensionType,
                representationType: representationType,
                defaultUnitType: out var defaultUnitType,
                defaultFormat: out var defaultFormat,
                error: out var wrapperMetadataError))
        {
            Report(
                productionContext: productionContext,
                descriptor: QuantityWrapperMetadataIsInvalid,
                wrapperType: wrapperType,
                args: [wrapperType.ToDisplayString(), wrapperMetadataError]);
            return;
        }

        if (!TryGetUnitMembers(
                wrapperType: wrapperType,
                dimensionType: dimensionType,
                representationType: representationType,
                members: out var unitMembers,
                error: out var unitError))
        {
            Report(
                productionContext: productionContext,
                descriptor: QuantityUnitMemberIsInvalid,
                wrapperType: wrapperType,
                args: [wrapperType.ToDisplayString(), unitError]);
            return;
        }

        var source = EmitWrapper(
            wrapperType: wrapperType,
            valueType: valueType,
            dimensionType: dimensionType,
            representationType: representationType,
            defaultUnitType: defaultUnitType,
            defaultFormat: defaultFormat,
            unitMembers: unitMembers);

        productionContext.AddSource(
            hintName: BuildHintName(wrapperType: wrapperType),
            sourceText: SourceText.From(text: source, encoding: Encoding.UTF8));
    }

    static bool IsPartial(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(static syntaxReference =>
        {
            if (syntaxReference.GetSyntax() is not TypeDeclarationSyntax declaration)
            {
                return false;
            }

            return declaration.Modifiers.Any(static token => token.IsKind(SyntaxKind.PartialKeyword));
        });

    static bool TryGetValuePropertyType(
        INamedTypeSymbol wrapperType,
        out INamedTypeSymbol valueType,
        out ITypeSymbol dimensionType,
        out ITypeSymbol representationType)
    {
        valueType = null!;
        dimensionType = null!;
        representationType = null!;

        var valueProperty = wrapperType.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static propertySymbol => !propertySymbol.IsStatic && propertySymbol.Name == "Value");
        if (valueProperty is null)
        {
            return false;
        }

        if (valueProperty.Type is not INamedTypeSymbol namedType || namedType.TypeArguments.Length != 2)
        {
            return false;
        }

        if (!IsQuantityType(typeSymbol: namedType))
        {
            return false;
        }

        valueType = namedType;
        dimensionType = namedType.TypeArguments[0];
        representationType = namedType.TypeArguments[1];
        return true;
    }

    static bool TryGetWrapperMetadata(
        INamedTypeSymbol wrapperType,
        ITypeSymbol dimensionType,
        ITypeSymbol representationType,
        out INamedTypeSymbol defaultUnitType,
        out string defaultFormat,
        out string error)
    {
        defaultUnitType = null!;
        defaultFormat = "0.###";
        error = string.Empty;

        var wrapperAttribute = wrapperType.GetAttributes()
            .FirstOrDefault(static attributeData => IsAttribute(attributeData: attributeData, fullyQualifiedMetadataName: "Cohesive.Domain.QuantityAttribute"));
        if (wrapperAttribute is null)
        {
            error = "attribute is missing.";
            return false;
        }

        if (wrapperAttribute.ConstructorArguments.Length == 0
            || wrapperAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol configuredDefaultUnitType)
        {
            error = "default unit type argument is required.";
            return false;
        }

        if (!IsCompatibleUnitType(
                unitType: configuredDefaultUnitType,
                expectedDimension: dimensionType,
                expectedRepresentation: representationType))
        {
            error = $"default unit type '{configuredDefaultUnitType.ToDisplayString()}' is not compatible with Quantity<{dimensionType.ToDisplayString()}, {representationType.ToDisplayString()}>.";
            return false;
        }

        defaultUnitType = configuredDefaultUnitType;
        defaultFormat = wrapperAttribute.ConstructorArguments.Length > 1 && wrapperAttribute.ConstructorArguments[1].Value is string configuredDefaultFormat
            ? (string.IsNullOrWhiteSpace(value: configuredDefaultFormat) ? "0.###" : configuredDefaultFormat)
            : "0.###";
        return true;
    }

    static bool TryGetUnitMembers(
        INamedTypeSymbol wrapperType,
        ITypeSymbol dimensionType,
        ITypeSymbol representationType,
        out ImmutableArray<UnitMemberModel> members,
        out string error)
    {
        var builder = ImmutableArray.CreateBuilder<UnitMemberModel>();
        var usedMemberNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        error = string.Empty;

        foreach (var attribute in wrapperType.GetAttributes().Where(static attributeData => IsAttribute(attributeData: attributeData, fullyQualifiedMetadataName: "Cohesive.Domain.QuantityUnitMemberAttribute")))
        {
            if (attribute.ConstructorArguments.Length < 2)
            {
                error = "each [QuantityUnitMember] must provide unit type and member name.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            if (attribute.ConstructorArguments[0].Value is not INamedTypeSymbol unitType)
            {
                error = "unit type must be a concrete type.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            if (attribute.ConstructorArguments[1].Value is not string memberName || string.IsNullOrWhiteSpace(value: memberName))
            {
                error = "member name must be a non-empty identifier.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            if (!SyntaxFacts.IsValidIdentifier(memberName))
            {
                error = $"member name '{memberName}' is not a valid C# identifier.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            if (!usedMemberNames.Add(item: memberName))
            {
                error = $"member name '{memberName}' is duplicated.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            if (!IsCompatibleUnitType(
                    unitType: unitType,
                    expectedDimension: dimensionType,
                    expectedRepresentation: representationType))
            {
                error = $"unit type '{unitType.ToDisplayString()}' is not compatible with Quantity<{dimensionType.ToDisplayString()}, {representationType.ToDisplayString()}>.";
                members = ImmutableArray<UnitMemberModel>.Empty;
                return false;
            }

            builder.Add(new UnitMemberModel(unitType, memberName));
        }

        if (builder.Count == 0)
        {
            error = "at least one [QuantityUnitMember] is required.";
            members = ImmutableArray<UnitMemberModel>.Empty;
            return false;
        }

        members = builder.ToImmutable();
        return true;
    }

    static bool IsCompatibleUnitType(ITypeSymbol unitType, ITypeSymbol expectedDimension, ITypeSymbol expectedRepresentation)
    {
        if (unitType is not INamedTypeSymbol namedUnitType)
        {
            return false;
        }

        var quantityUnitInterface = namedUnitType.AllInterfaces.FirstOrDefault(static interfaceType => IsQuantityUnitType(interfaceType));
        if (quantityUnitInterface is null || quantityUnitInterface.TypeArguments.Length != 2)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(x: quantityUnitInterface.TypeArguments[0], y: expectedDimension)
               && SymbolEqualityComparer.Default.Equals(x: quantityUnitInterface.TypeArguments[1], y: expectedRepresentation);
    }

    static bool IsQuantityType(INamedTypeSymbol typeSymbol) =>
        typeSymbol.OriginalDefinition.ToDisplayString(format: FullyQualifiedFormat)
        == "global::Cohesive.Domain.Quantity<TDimension, TRep>";

    static bool IsQuantityUnitType(INamedTypeSymbol typeSymbol) =>
        typeSymbol.OriginalDefinition.ToDisplayString(format: FullyQualifiedFormat)
        == "global::Cohesive.Domain.IQuantityUnit<TDimension, TRep>";

    static bool IsAttribute(AttributeData attributeData, string fullyQualifiedMetadataName) =>
        attributeData.AttributeClass?.ToDisplayString() == fullyQualifiedMetadataName
        || attributeData.AttributeClass?.ToDisplayString(format: FullyQualifiedFormat) == $"global::{fullyQualifiedMetadataName}";

    static string EmitWrapper(
        INamedTypeSymbol wrapperType,
        INamedTypeSymbol valueType,
        ITypeSymbol dimensionType,
        ITypeSymbol representationType,
        INamedTypeSymbol defaultUnitType,
        string defaultFormat,
        ImmutableArray<UnitMemberModel> unitMembers)
    {
        var namespaceName = wrapperType.ContainingNamespace.IsGlobalNamespace
            ? null
            : wrapperType.ContainingNamespace.ToDisplayString();

        var wrapperTypeName = wrapperType.ToDisplayString(format: FullyQualifiedFormat);
        var valueTypeName = valueType.ToDisplayString(format: FullyQualifiedFormat);
        var dimensionTypeName = dimensionType.ToDisplayString(format: FullyQualifiedFormat);
        var representationTypeName = representationType.ToDisplayString(format: FullyQualifiedFormat);
        var defaultUnitTypeName = defaultUnitType.ToDisplayString(format: FullyQualifiedFormat);

        var accessibility = wrapperType.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal",
        };

        var declarationKind = wrapperType.IsRecord ? "record struct" : "struct";
        var readonlyModifier = wrapperType.IsReadOnly ? "readonly " : string.Empty;
        var typeName = wrapperType.Name;

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        if (namespaceName is not null)
        {
            source.Append("namespace ").Append(namespaceName).AppendLine(";");
            source.AppendLine();
        }

        source.Append(accessibility).Append(' ')
            .Append(readonlyModifier)
            .Append("partial ")
            .Append(declarationKind)
            .Append(' ')
            .Append(typeName)
            .AppendLine();
        source.AppendLine("{");
        source.Append("    public static ").Append(wrapperTypeName).Append(" FromValue(").Append(valueTypeName).AppendLine(" value) => new(value);");
        source.AppendLine();

        foreach (var member in unitMembers)
        {
            var escapedName = EscapeIdentifier(identifier: member.MemberName);
            var escapedFromName = EscapeIdentifier(identifier: $"From{member.MemberName}");
            var unitTypeName = member.UnitType.ToDisplayString(format: FullyQualifiedFormat);

            source.Append("    public static ").Append(wrapperTypeName).Append(' ').Append(escapedFromName).Append('(').Append(representationTypeName).Append(" value) => new(")
                .Append(valueTypeName).Append(".From<").Append(unitTypeName).AppendLine(">(value));");
            source.AppendLine();
            source.Append("    public ").Append(representationTypeName).Append(' ').Append(escapedName).Append(" => global::Cohesive.Domain.QuantityMath.As<")
                .Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).Append(", ").Append(unitTypeName).AppendLine(">(quantity: this);");
            source.AppendLine();
        }

        source.Append("    public int CompareTo(").Append(wrapperTypeName).AppendLine(" other) => global::Cohesive.Domain.QuantityMath.Compare<")
            .Append("        ").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(left: this, right: other);");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" AdditiveIdentity => FromValue(").Append(valueTypeName).AppendLine(".Zero);");
        source.AppendLine();
        source.Append("    public override string ToString() => global::Cohesive.Domain.QuantityMath.Format<")
            .Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).Append(", ").Append(defaultUnitTypeName).Append(">(")
            .Append("quantity: this, format: ").Append(EscapeStringLiteral(text: defaultFormat)).AppendLine(");");
        source.AppendLine();

        source.Append("    public static ").Append(wrapperTypeName).Append(" operator +(").Append(wrapperTypeName).Append(" left, ").Append(wrapperTypeName).AppendLine(" right) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Add<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(left: left, right: right);");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" operator -(").Append(wrapperTypeName).Append(" left, ").Append(wrapperTypeName).AppendLine(" right) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Subtract<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(left: left, right: right);");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" operator -(").Append(wrapperTypeName).AppendLine(" value) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Negate<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(value: value);");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" operator *(").Append(wrapperTypeName).Append(" value, ").Append(representationTypeName).AppendLine(" scalar) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Scale<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(value: value, scalar: scalar);");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" operator *(").Append(representationTypeName).Append(" scalar, ").Append(wrapperTypeName).AppendLine(" value) => value * scalar;");
        source.AppendLine();
        source.Append("    public static ").Append(wrapperTypeName).Append(" operator /(").Append(wrapperTypeName).Append(" value, ").Append(representationTypeName).AppendLine(" scalar) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Divide<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(value: value, scalar: scalar);");
        source.AppendLine();
        source.Append("    public static ").Append(representationTypeName).Append(" operator /(").Append(wrapperTypeName).Append(" left, ").Append(wrapperTypeName).AppendLine(" right) =>")
            .Append("        global::Cohesive.Domain.QuantityMath.Ratio<").Append(wrapperTypeName).Append(", ").Append(dimensionTypeName).Append(", ").Append(representationTypeName).AppendLine(">(left: left, right: right);");
        source.AppendLine("}");
        return source.ToString();
    }

    static string BuildHintName(INamedTypeSymbol wrapperType)
    {
        var rawName = wrapperType.ToDisplayString();
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            rawName = rawName.Replace(oldChar: character, newChar: '_');
        }

        return $"{rawName}.QuantityWrapper.g.cs";
    }

    static string EscapeIdentifier(string identifier)
    {
        var keywordKind = SyntaxFacts.GetKeywordKind(text: identifier);
        return keywordKind != SyntaxKind.None ? $"@{identifier}" : identifier;
    }

    static string EscapeStringLiteral(string text) =>
        "\"" + text.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "\"", newValue: "\\\"") + "\"";

    static void Report(
        SourceProductionContext productionContext,
        DiagnosticDescriptor descriptor,
        INamedTypeSymbol wrapperType,
        object[] args)
    {
        var location = wrapperType.Locations.FirstOrDefault() ?? Location.None;
        productionContext.ReportDiagnostic(
            diagnostic: Diagnostic.Create(
                descriptor: descriptor,
                location: location,
                messageArgs: args));
    }

    readonly record struct UnitMemberModel(INamedTypeSymbol UnitType, string MemberName);
}
