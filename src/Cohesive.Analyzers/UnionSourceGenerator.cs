using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cohesive.Analyzers;

/// <summary>
/// Generates discriminated-union APIs for types marked with <c>[Union]</c>.
/// </summary>
[Generator]
public sealed class UnionSourceGenerator : IIncrementalGenerator
{
    const int DefaultEitherMaxArity = 16;
    const int HardMaxEitherArity = 32;

    static readonly SymbolDisplayFormat FullyQualifiedNullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor UnionTypeMustBePartial = CreateDiagnostic(
        id: "COHDU001",
        title: "Union type must be partial",
        messageFormat: "Type '{0}' is marked with [Union] and must be declared partial.",
        description: "Union generation adds members into the annotated type, which requires the declaration to include the partial modifier.");

    static readonly DiagnosticDescriptor UnionTypeMustBeTopLevel = CreateDiagnostic(
        id: "COHDU002",
        title: "Union type must be top-level",
        messageFormat: "Type '{0}' is marked with [Union] but nested union declarations are not supported.",
        description: "Only top-level union declarations are supported so generated nested type qualification remains predictable across targets.");

    static readonly DiagnosticDescriptor UnionTypeMustBeNonGeneric = CreateDiagnostic(
        id: "COHDU003",
        title: "Union type must be non-generic",
        messageFormat: "Type '{0}' is marked with [Union] but generic union declarations are not supported.",
        description: "Subtype unions currently require a closed case set from concrete derived types and therefore do not support generic union roots.");

    static readonly DiagnosticDescriptor UnsupportedUnionShape = CreateDiagnostic(
        id: "COHDU004",
        title: "Unsupported union shape",
        messageFormat: "Type '{0}' is marked with [Union] but does not match a supported union shape.",
        description: "Union generation supports subtype hierarchies and tagged unions with enum discriminators. Other shapes are rejected.");

    static readonly DiagnosticDescriptor UnsupportedUnionArity = CreateDiagnostic(
        id: "COHDU005",
        title: "Unsupported union arity",
        messageFormat: "Type '{0}' has {1} cases. Supported arity range is 2 through {2} (configure with EitherMaxArity).",
        description: "Generated Either support is bounded by the configured EitherMaxArity value.");

    static readonly DiagnosticDescriptor MissingTaggedUnionCaseProperty = CreateDiagnostic(
        id: "COHDU006",
        title: "Missing tagged union case property",
        messageFormat: "Type '{0}' has discriminator enum value '{1}' but no matching property '{1}'.",
        description: "Tagged unions require one case payload property per discriminator enum member.");

    static readonly DiagnosticDescriptor TaggedUnionCannotBeConstructed = CreateDiagnostic(
        id: "COHDU007",
        title: "Tagged union case construction is not possible",
        messageFormat: "Type '{0}' is a tagged union but no supported constructor or object-initializer path was found.",
        description: "Union factories require a constructible path so the generator can materialize values for each tagged case.");

    static DiagnosticDescriptor CreateDiagnostic(string id, string title, string messageFormat, string description) =>
        new(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: "Cohesive.Unions",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: $"https://github.com/eulerfx/Cohesive/blob/main/Cohesive.Analyzers/docs/{id}.md");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var eitherMaxArity = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) => ResolveEitherMaxArity(globalOptions: provider.GlobalOptions));

        context.RegisterSourceOutput(context.CompilationProvider.Combine(eitherMaxArity), static (productionContext, pair) =>
        {
            var compilation = pair.Left;
            var maxEitherArity = pair.Right;
            var hasPreludeSupport =
                compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: "Cohesive.Prelude.IEither`2") is not null;
            if (hasPreludeSupport)
            {
                return;
            }

            var supportSource = UnionSupportSourceEmitter.EmitPreludeSupportWithoutUnionAttribute(maxEitherArity: maxEitherArity);
            productionContext.AddSource(
                hintName: "Cohesive.Prelude.UnionSupport.g.cs",
                sourceText: SourceText.From(text: supportSource, encoding: Encoding.UTF8));
        });

        var unionTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "Cohesive.Prelude.UnionAttribute",
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (generatorContext, _) => (INamedTypeSymbol)generatorContext.TargetSymbol)
            .Collect();

        var input = context.CompilationProvider.Combine(unionTypes).Combine(eitherMaxArity);

        context.RegisterSourceOutput(input, static (productionContext, triple) =>
        {
            Execute(
                productionContext: productionContext,
                compilation: triple.Left.Left,
                unionTypeSymbols: triple.Left.Right,
                maxEitherArity: triple.Right);
        });
    }

    static int ResolveEitherMaxArity(AnalyzerConfigOptions globalOptions)
    {
        if (globalOptions.TryGetValue(key: "build_property.EitherMaxArity", value: out var configuredValue)
            && int.TryParse(s: configuredValue, result: out var parsedValue))
        {
            if (parsedValue < 2)
            {
                return 2;
            }

            if (parsedValue > HardMaxEitherArity)
            {
                return HardMaxEitherArity;
            }

            return parsedValue;
        }

        return DefaultEitherMaxArity;
    }

    /// <summary>
    /// Generates source for each discovered union declaration.
    /// </summary>
    static void Execute(
        SourceProductionContext productionContext,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> unionTypeSymbols,
        int maxEitherArity)
    {
        foreach (var unionType in unionTypeSymbols)
        {
            GenerateForUnionType(
                productionContext: productionContext,
                compilation: compilation,
                unionType: unionType,
                maxEitherArity: maxEitherArity);
        }
    }

    /// <summary>
    /// Generates source for a single union declaration.
    /// </summary>
    static void GenerateForUnionType(
        SourceProductionContext productionContext,
        Compilation compilation,
        INamedTypeSymbol unionType,
        int maxEitherArity)
    {
        if (!IsPartial(symbol: unionType))
        {
            Report(
                productionContext: productionContext,
                descriptor: UnionTypeMustBePartial,
                unionType: unionType,
                args: [unionType.ToDisplayString()]);
            return;
        }

        if (unionType.ContainingType is not null)
        {
            Report(
                productionContext: productionContext,
                descriptor: UnionTypeMustBeTopLevel,
                unionType: unionType,
                args: [unionType.ToDisplayString()]);
            return;
        }

        var hierarchyCases = GetConcreteDerivedTypes(
                compilation: compilation,
                unionType: unionType)
            .OrderBy(keySelector: static type => type.Name, comparer: StringComparer.Ordinal)
            .ToImmutableArray();

        if (!hierarchyCases.IsEmpty)
        {
            if (!ValidateArity(
                    productionContext: productionContext,
                    unionType: unionType,
                    arity: hierarchyCases.Length,
                    maxEitherArity: maxEitherArity))
            {
                return;
            }

            var supportsHierarchyEitherContract = SupportsEitherContract(arity: hierarchyCases.Length, maxEitherArity: maxEitherArity);
            var source = EmitHierarchyUnion(
                unionType: unionType,
                caseTypes: hierarchyCases,
                supportsEitherContract: supportsHierarchyEitherContract);

            productionContext.AddSource(
                hintName: BuildHintName(unionType: unionType),
                sourceText: SourceText.From(text: source, encoding: Encoding.UTF8));
            return;
        }

        if (!TryGetTaggedUnionModel(
                productionContext: productionContext,
                unionType: unionType,
                discriminatorPropertyName: GetDiscriminatorPropertyName(unionType: unionType),
                out var taggedUnionModel))
        {
            Report(
                productionContext: productionContext,
                descriptor: UnsupportedUnionShape,
                unionType: unionType,
                args: [unionType.ToDisplayString()]);
            return;
        }

        if (!ValidateArity(
                productionContext: productionContext,
                unionType: unionType,
                arity: taggedUnionModel.Cases.Length,
                maxEitherArity: maxEitherArity))
        {
            return;
        }

        var supportsTaggedEitherContract = SupportsEitherContract(arity: taggedUnionModel.Cases.Length, maxEitherArity: maxEitherArity);
        var taggedSource = EmitTaggedUnion(
            model: taggedUnionModel,
            supportsEitherContract: supportsTaggedEitherContract);

        productionContext.AddSource(
            hintName: BuildHintName(unionType: unionType),
            sourceText: SourceText.From(text: taggedSource, encoding: Encoding.UTF8));
    }

    /// <summary>
    /// Validates that union arity maps to generated <c>Either</c> support.
    /// </summary>
    static bool ValidateArity(SourceProductionContext productionContext, INamedTypeSymbol unionType, int arity, int maxEitherArity)
    {
        if (arity < 2)
        {
            Report(
                productionContext: productionContext,
                descriptor: UnsupportedUnionArity,
                unionType: unionType,
                args: [unionType.ToDisplayString(), arity, maxEitherArity]);
            return false;
        }

        if (SupportsEitherContract(arity: arity, maxEitherArity: maxEitherArity))
        {
            return true;
        }

        if (IsEitherExtensionUnionType(unionType: unionType))
        {
            return true;
        }

        Report(
            productionContext: productionContext,
            descriptor: UnsupportedUnionArity,
            unionType: unionType,
            args: [unionType.ToDisplayString(), arity, maxEitherArity]);
        return false;
    }

    static bool SupportsEitherContract(int arity, int maxEitherArity) => arity is >= 2 && arity <= maxEitherArity;

    static bool IsEitherExtensionUnionType(INamedTypeSymbol unionType) =>
        string.Equals(a: unionType.Name, b: "Either", comparisonType: StringComparison.Ordinal);

    /// <summary>
    /// Emits implementation source for subtype-discriminated unions.
    /// </summary>
    static string EmitHierarchyUnion(INamedTypeSymbol unionType, ImmutableArray<INamedTypeSymbol> caseTypes, bool supportsEitherContract)
    {
        var modelCases = caseTypes
            .Select((caseType, index) => new UnionCase(
                Name: caseType.Name,
                TypeName: FullyQualified(type: caseType),
                Index: index,
                PropertyName: string.Empty,
                EnumValueExpression: string.Empty))
            .ToImmutableArray();

        return EmitUnionCore(
            unionType: unionType,
            cases: modelCases,
            shape: UnionShape.Subtype,
            discriminatorPropertyName: string.Empty,
            discriminatorTypeName: string.Empty,
            taggedUnionFactory: null,
            supportsEitherContract: supportsEitherContract);
    }

    /// <summary>
    /// Emits implementation source for enum-tagged unions.
    /// </summary>
    static string EmitTaggedUnion(TaggedUnionModel model, bool supportsEitherContract)
    {
        var modelCases = model.Cases
            .Select((unionCase, index) => new UnionCase(
                Name: unionCase.Name,
                TypeName: unionCase.TypeName,
                Index: index,
                PropertyName: unionCase.PropertyName,
                EnumValueExpression: unionCase.EnumValueExpression))
            .ToImmutableArray();

        return EmitUnionCore(
            unionType: model.UnionType,
            cases: modelCases,
            shape: UnionShape.Tagged,
            discriminatorPropertyName: model.DiscriminatorPropertyName,
            discriminatorTypeName: model.DiscriminatorTypeName,
            taggedUnionFactory: model.Factory,
            supportsEitherContract: supportsEitherContract);
    }

    /// <summary>
    /// Emits the core union API surface shared by both union shapes.
    /// </summary>
    static string EmitUnionCore(
        INamedTypeSymbol unionType,
        ImmutableArray<UnionCase> cases,
        UnionShape shape,
        string discriminatorPropertyName,
        string discriminatorTypeName,
        TaggedUnionFactory? taggedUnionFactory,
        bool supportsEitherContract)
    {
        var unionTypeName = FullyQualified(type: unionType);
        var namespaceName = unionType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : unionType.ContainingNamespace.ToDisplayString();

        var genericTypeArguments = string.Join(", ", cases.Select(selector: static unionCase => unionCase.TypeName));
        var eitherTypeName = $"global::Cohesive.Prelude.Either<{genericTypeArguments}>";
        var iEitherTypeName = $"global::Cohesive.Prelude.IEither<{genericTypeArguments}>";

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        if (!string.IsNullOrWhiteSpace(value: namespaceName))
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
            builder.AppendLine();
        }

        builder.AppendLine("[global::System.Diagnostics.DebuggerDisplay(\"{DebuggerDisplay,nq}\")]");
        builder.Append(BuildTypeDeclarationHeader(unionType: unionType));
        if (supportsEitherContract)
        {
            builder
                .Append(" : global::Cohesive.Prelude.IDiscriminatedUnion, ")
                .Append(iEitherTypeName)
                .AppendLine();
        }
        else
        {
            builder.AppendLine(" : global::Cohesive.Prelude.IDiscriminatedUnion");
        }
        AppendTypeParameterConstraints(builder: builder, unionType: unionType);
        builder.AppendLine("{");

        builder.Append("    int DiscriminatedUnionCaseCount => ")
            .Append(cases.Length)
            .AppendLine(";")
            .AppendLine();

        builder.AppendLine("    int DiscriminatedUnionCaseIndex =>");
        if (shape is UnionShape.Subtype)
        {
            builder.AppendLine("        this switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.TypeName)
                    .Append(" => ")
                    .Append(unionCase.Index)
                    .AppendLine(",");
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
            builder.AppendLine("        };");
        }
        else
        {
            builder.Append("        ")
                .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                .AppendLine(" switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.EnumValueExpression)
                    .Append(" => ")
                    .Append(unionCase.Index)
                    .AppendLine(",");
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
            builder.AppendLine("        };");
        }

        builder.AppendLine();
        builder.AppendLine("    object? DiscriminatedUnionCaseValue =>");
        if (shape is UnionShape.Subtype)
        {
            builder.AppendLine("        this switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.TypeName)
                    .Append(" value => value,")
                    .AppendLine();
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
            builder.AppendLine("        };");
        }
        else
        {
            builder.Append("        ")
                .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                .AppendLine(" switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.EnumValueExpression)
                    .Append(" => ")
                    .Append(EscapeIdentifier(identifier: unionCase.PropertyName))
                    .AppendLine(",");
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
            builder.AppendLine("        };");
        }

        builder.AppendLine();
        builder.AppendLine("    int global::Cohesive.Prelude.IDiscriminatedUnion.CaseCount => DiscriminatedUnionCaseCount;");
        builder.AppendLine("    int global::Cohesive.Prelude.IDiscriminatedUnion.CaseIndex => DiscriminatedUnionCaseIndex;");
        builder.AppendLine("    object? global::Cohesive.Prelude.IDiscriminatedUnion.CaseValue => DiscriminatedUnionCaseValue;");
        builder.AppendLine("    string DebuggerDisplay => $\"CaseIndex = {DiscriminatedUnionCaseIndex}, CaseValue = {DiscriminatedUnionCaseValue}\";");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Deconstructs the union into case index and case value for tuple pattern matching.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    public void Deconstruct(out int caseIndex, out object? caseValue)");
        builder.AppendLine("    {");
        builder.AppendLine("        caseIndex = DiscriminatedUnionCaseIndex;");
        builder.AppendLine("        caseValue = DiscriminatedUnionCaseValue;");
        builder.AppendLine("    }");

        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Matches the union into a result value using one callback per case.");
        builder.AppendLine("    /// </summary>");

        builder.Append("    public TResult Match<TResult>(");
        builder.Append(string.Join(", ", cases.Select(unionCase =>
            $"global::System.Func<{unionCase.TypeName}, TResult> on{unionCase.Name}")));
        builder.AppendLine(")");
        builder.AppendLine("    {");
        foreach (var unionCase in cases)
        {
            builder
                .Append("        global::System.ArgumentNullException.ThrowIfNull(argument: on")
                .Append(unionCase.Name)
                .AppendLine(");");
        }

        builder.AppendLine();
        if (shape is UnionShape.Subtype)
        {
            builder.AppendLine("        return this switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.TypeName)
                    .Append(" value => on")
                    .Append(unionCase.Name)
                    .Append("(value),")
                    .AppendLine();
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"No matching union case callback was provided.\"),");
            builder.AppendLine("        };");
        }
        else
        {
            builder
                .Append("        return ")
                .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                .AppendLine(" switch");
            builder.AppendLine("        {");
            foreach (var unionCase in cases)
            {
                builder
                    .Append("            ")
                    .Append(unionCase.EnumValueExpression)
                    .Append(" => on")
                    .Append(unionCase.Name)
                    .Append("(")
                    .Append(EscapeIdentifier(identifier: unionCase.PropertyName))
                    .Append("),")
                    .AppendLine();
            }

            builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"No matching union case callback was provided.\"),");
            builder.AppendLine("        };");
        }

        builder.AppendLine("    }");

        foreach (var unionCase in cases)
        {
            var ordinal = unionCase.Index + 1;
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Indicates whether the active case is '")
                .Append(unionCase.Name)
                .AppendLine("'.");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public bool Is")
                .Append(unionCase.Name)
                .Append("() => ");
            if (shape is UnionShape.Subtype)
            {
                builder
                    .Append("this is ")
                    .Append(unionCase.TypeName)
                    .AppendLine(";");
            }
            else
            {
                builder
                    .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                    .Append(" == ")
                    .Append(unionCase.EnumValueExpression)
                    .AppendLine(";");
            }

            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Attempts to read Case")
                .Append(ordinal)
                .AppendLine(".");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public bool TryGetCase")
                .Append(ordinal)
                .Append("([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(returnValue: false)] out ")
                .Append(unionCase.TypeName)
                .AppendLine(" value)");
            builder.AppendLine("    {");
            if (shape is UnionShape.Subtype)
            {
                builder
                    .Append("        if (this is ")
                    .Append(unionCase.TypeName)
                    .AppendLine(" typedValue)");
                builder.AppendLine("        {");
                builder.AppendLine("            value = typedValue;");
                builder.AppendLine("            return true;");
                builder.AppendLine("        }");
            }
            else
            {
                builder
                    .Append("        if (")
                    .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                    .Append(" == ")
                    .Append(unionCase.EnumValueExpression)
                    .AppendLine(")");
                builder.AppendLine("        {");
                builder
                    .Append("            value = ")
                    .Append(EscapeIdentifier(identifier: unionCase.PropertyName))
                    .AppendLine(";");
                builder.AppendLine("            return true;");
                builder.AppendLine("        }");
            }

            builder.AppendLine();
            builder.AppendLine("        value = default!;");
            builder.AppendLine("        return false;");
            builder.AppendLine("    }");

            var namedMethod = $"TryGet{unionCase.Name}";
            var ordinalMethod = $"TryGetCase{ordinal}";
            if (!string.Equals(a: namedMethod, b: ordinalMethod, comparisonType: StringComparison.Ordinal))
            {
                builder.AppendLine();
                builder.AppendLine("    /// <summary>");
                builder
                    .Append("    /// Attempts to read the '")
                    .Append(unionCase.Name)
                    .AppendLine("' case.");
                builder.AppendLine("    /// </summary>");
                builder
                    .Append("    public bool ")
                    .Append(namedMethod)
                    .Append("([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(returnValue: false)] out ")
                    .Append(unionCase.TypeName)
                    .Append(" value) => ")
                    .Append(ordinalMethod)
                    .AppendLine("(out value);");
            }
        }

        if (supportsEitherContract)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine("    /// Converts the union value into the matching Either representation.");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public ")
                .Append(eitherTypeName)
                .AppendLine(" ToEither()");
            builder.AppendLine("    {");
            if (shape is UnionShape.Subtype)
            {
                builder.AppendLine("        return this switch");
                builder.AppendLine("        {");
                foreach (var unionCase in cases)
                {
                    var ordinal = unionCase.Index + 1;
                    builder
                        .Append("            ")
                        .Append(unionCase.TypeName)
                        .Append(" value => ")
                        .Append(eitherTypeName)
                        .Append(".FromCase")
                        .Append(ordinal)
                        .Append("(value: value),")
                        .AppendLine();
                }

                builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
                builder.AppendLine("        };");
            }
            else
            {
                builder
                    .Append("        return ")
                    .Append(EscapeIdentifier(identifier: discriminatorPropertyName))
                    .AppendLine(" switch");
                builder.AppendLine("        {");
                foreach (var unionCase in cases)
                {
                    var ordinal = unionCase.Index + 1;
                    builder
                        .Append("            ")
                        .Append(unionCase.EnumValueExpression)
                        .Append(" => ")
                        .Append(eitherTypeName)
                        .Append(".FromCase")
                        .Append(ordinal)
                        .Append("(value: ")
                        .Append(EscapeIdentifier(identifier: unionCase.PropertyName))
                        .Append("),")
                        .AppendLine();
                }

                builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown union case.\"),");
                builder.AppendLine("        };");
            }

            builder.AppendLine("    }");

            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine("    /// Creates a union value from an Either representation with matching arity.");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public static ")
                .Append(unionTypeName)
                .Append(" FromEither(")
                .Append(eitherTypeName)
                .AppendLine(" value)");
            builder.AppendLine("    {");
            builder.AppendLine("        return value.Match(");
            for (var index = 0; index < cases.Length; index++)
            {
                var unionCase = cases[index];
                builder
                    .Append("            onCase")
                    .Append(index + 1)
                    .Append(": static value => From")
                    .Append(unionCase.Name)
                    .Append("(value: value)");
                if (index < cases.Length - 1)
                {
                    builder.AppendLine(",");
                }
                else
                {
                    builder.AppendLine(");");
                }
            }

            builder.AppendLine("    }");
        }

        foreach (var unionCase in cases)
        {
            var fromNamedName = $"From{unionCase.Name}";

            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Creates a union value for the '")
                .Append(unionCase.Name)
                .AppendLine("' case.");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public static ")
                .Append(unionTypeName)
                .Append(" ")
                .Append(fromNamedName)
                .Append("(")
                .Append(unionCase.TypeName)
                .AppendLine(" value)");
            builder.AppendLine("    {");
            if (shape is UnionShape.Subtype)
            {
                builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(argument: value);");
                builder.AppendLine("        return value;");
            }
            else
            {
                builder
                    .Append("        return ")
                    .Append(EmitTaggedUnionConstructionExpression(
                        unionTypeName: unionTypeName,
                        unionCase: unionCase,
                        allCases: cases,
                        discriminatorPropertyName: discriminatorPropertyName,
                        discriminatorTypeName: discriminatorTypeName,
                        taggedUnionFactory: taggedUnionFactory!))
                    .AppendLine(";");
            }

            builder.AppendLine("    }");
        }

        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>
    /// Emits the construction expression for one tagged-union case.
    /// </summary>
    static string EmitTaggedUnionConstructionExpression(
        string unionTypeName,
        UnionCase unionCase,
        ImmutableArray<UnionCase> allCases,
        string discriminatorPropertyName,
        string discriminatorTypeName,
        TaggedUnionFactory taggedUnionFactory)
    {
        if (taggedUnionFactory.Kind is TaggedUnionFactoryKind.ObjectInitializer)
        {
            return $"new {unionTypeName} {{ {EscapeIdentifier(identifier: discriminatorPropertyName)} = {unionCase.EnumValueExpression}, {EscapeIdentifier(identifier: unionCase.PropertyName)} = value }}";
        }

        var constructor = taggedUnionFactory.Constructor!;
        var arguments = constructor.Parameters
            .Select(parameter =>
            {
                var argumentValue = ResolveTaggedConstructorParameterValue(
                    parameterName: parameter.Name,
                    unionCase: unionCase,
                    allCases: allCases,
                    discriminatorPropertyName: discriminatorPropertyName,
                    discriminatorTypeName: discriminatorTypeName);
                return $"{EscapeIdentifier(identifier: parameter.Name)}: {argumentValue}";
            });

        return $"new {unionTypeName}({string.Join(separator: ", ", values: arguments)})";
    }

    /// <summary>
    /// Resolves constructor argument source for tagged-union case creation.
    /// </summary>
    static string ResolveTaggedConstructorParameterValue(
        string parameterName,
        UnionCase unionCase,
        ImmutableArray<UnionCase> allCases,
        string discriminatorPropertyName,
        string discriminatorTypeName)
    {
        if (string.Equals(a: parameterName, b: discriminatorPropertyName, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return unionCase.EnumValueExpression;
        }

        var matchingCase = allCases.FirstOrDefault(caseItem =>
            string.Equals(
                a: caseItem.PropertyName,
                b: parameterName,
                comparisonType: StringComparison.OrdinalIgnoreCase));

        if (matchingCase is not null)
        {
            return string.Equals(
                    a: matchingCase.PropertyName,
                    b: unionCase.PropertyName,
                    comparisonType: StringComparison.Ordinal)
                ? "value"
                : "default";
        }

        _ = discriminatorTypeName;
        return "default";
    }

    /// <summary>
    /// Reads tagged-union metadata from a candidate union type.
    /// </summary>
    static bool TryGetTaggedUnionModel(
        SourceProductionContext productionContext,
        INamedTypeSymbol unionType,
        string discriminatorPropertyName,
        out TaggedUnionModel taggedUnionModel)
    {
        taggedUnionModel = null!;

        var discriminatorProperty = unionType
            .GetMembers(name: discriminatorPropertyName)
            .OfType<IPropertySymbol>()
            .FirstOrDefault(property =>
                !property.IsStatic && property.Type.TypeKind is TypeKind.Enum);

        if (discriminatorProperty is null || discriminatorProperty.Type is not INamedTypeSymbol discriminatorEnumType)
        {
            return false;
        }

        var enumCases = discriminatorEnumType
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(field => field is { IsImplicitlyDeclared: false, HasConstantValue: true })
            .ToImmutableArray();

        if (enumCases.IsEmpty)
        {
            return false;
        }

        var candidateCaseProperties = unionType
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property =>
                !property.IsStatic
                && !string.Equals(
                    a: property.Name,
                    b: discriminatorProperty.Name,
                    comparisonType: StringComparison.Ordinal))
            .ToImmutableArray();

        var usedCasePropertyNames = new HashSet<string>(StringComparer.Ordinal);
        var caseBuilder = ImmutableArray.CreateBuilder<TaggedUnionCase>();
        for (var index = 0; index < enumCases.Length; index++)
        {
            var enumCase = enumCases[index];
            var property = candidateCaseProperties.FirstOrDefault(candidate =>
                !usedCasePropertyNames.Contains(candidate.Name)
                && string.Equals(
                    a: candidate.Name,
                    b: enumCase.Name,
                    comparisonType: StringComparison.OrdinalIgnoreCase));

            if (property is null
                && index < candidateCaseProperties.Length
                && !usedCasePropertyNames.Contains(candidateCaseProperties[index].Name))
            {
                property = candidateCaseProperties[index];
            }

            if (property is null)
            {
                var remaining = candidateCaseProperties
                    .Where(candidate => !usedCasePropertyNames.Contains(candidate.Name))
                    .ToImmutableArray();

                if (remaining.Length is 1)
                {
                    property = remaining[0];
                }
            }

            if (property is null)
            {
                Report(
                    productionContext: productionContext,
                    descriptor: MissingTaggedUnionCaseProperty,
                    unionType: unionType,
                    args: [unionType.ToDisplayString(), enumCase.Name]);
                return false;
            }
            
            _ = usedCasePropertyNames.Add(property.Name);

            caseBuilder.Add(new TaggedUnionCase(
                Name: enumCase.Name,
                PropertyName: property.Name,
                TypeName: FullyQualified(type: property.Type),
                EnumValueExpression: $"{FullyQualified(type: discriminatorEnumType)}.{EscapeIdentifier(identifier: enumCase.Name)}",
                Index: index));
        }

        if (!TryCreateTaggedUnionFactory(
                unionType: unionType,
                discriminatorProperty: discriminatorProperty,
                cases: caseBuilder.ToImmutable(),
                out var factory))
        {
            Report(
                productionContext: productionContext,
                descriptor: TaggedUnionCannotBeConstructed,
                unionType: unionType,
                args: [unionType.ToDisplayString()]);
            return false;
        }

        taggedUnionModel = new TaggedUnionModel(
            UnionType: unionType,
            DiscriminatorPropertyName: discriminatorProperty.Name,
            DiscriminatorTypeName: FullyQualified(type: discriminatorEnumType),
            Cases: caseBuilder.ToImmutable(),
            Factory: factory);
        return true;
    }

    /// <summary>
    /// Chooses a tagged-union construction strategy.
    /// </summary>
    static bool TryCreateTaggedUnionFactory(
        INamedTypeSymbol unionType,
        IPropertySymbol discriminatorProperty,
        ImmutableArray<TaggedUnionCase> cases,
        out TaggedUnionFactory taggedUnionFactory)
    {
        var requiredNames = cases
            .Select(selector: static unionCase => unionCase.PropertyName)
            .Append(element: discriminatorProperty.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateConstructor = unionType.InstanceConstructors
            .Where(constructor =>
                constructor.DeclaredAccessibility is Accessibility.Public
                && !constructor.IsStatic
                && constructor.Parameters.All(parameter => !string.IsNullOrWhiteSpace(value: parameter.Name)))
            .OrderBy(constructor => constructor.Parameters.Length)
            .FirstOrDefault(constructor =>
            {
                var parameterNames = constructor.Parameters
                    .Select(selector: static parameter => parameter.Name)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                return requiredNames.IsSubsetOf(parameterNames);
            });

        if (candidateConstructor is not null)
        {
            taggedUnionFactory = new TaggedUnionFactory(
                Kind: TaggedUnionFactoryKind.Constructor,
                Constructor: candidateConstructor);
            return true;
        }

        if (!HasPublicParameterlessConstructor(unionType: unionType))
        {
            taggedUnionFactory = null!;
            return false;
        }

        var allSettable = cases
            .Select(unionCase => unionType.GetMembers(name: unionCase.PropertyName)
                .OfType<IPropertySymbol>()
                .First())
            .Append(element: discriminatorProperty)
            .All(property => property.SetMethod is { DeclaredAccessibility: Accessibility.Public });

        if (!allSettable)
        {
            taggedUnionFactory = null!;
            return false;
        }

        taggedUnionFactory = new TaggedUnionFactory(
            Kind: TaggedUnionFactoryKind.ObjectInitializer,
            Constructor: null);
        return true;
    }

    /// <summary>
    /// Determines whether a type can be created with <c>new T()</c>.
    /// </summary>
    static bool HasPublicParameterlessConstructor(INamedTypeSymbol unionType)
    {
        if (unionType.TypeKind is TypeKind.Struct)
        {
            return true;
        }

        return unionType.InstanceConstructors.Any(constructor =>
            constructor is
            {
                DeclaredAccessibility: Accessibility.Public,
                Parameters.Length: 0
            });
    }

    /// <summary>
    /// Enumerates concrete derived cases for subtype-discriminated unions.
    /// </summary>
    static ImmutableArray<INamedTypeSymbol> GetConcreteDerivedTypes(Compilation compilation, INamedTypeSymbol unionType)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (var candidate in EnumerateTypes(namespaceSymbol: compilation.Assembly.GlobalNamespace))
        {
            if (candidate.TypeKind is not TypeKind.Class)
            {
                continue;
            }

            if (candidate.IsAbstract)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(x: candidate, y: unionType))
            {
                continue;
            }

            if (InheritsFrom(type: candidate, baseType: unionType))
            {
                builder.Add(item: candidate);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Checks whether a type inherits from a particular base type.
    /// </summary>
    static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var target = baseType.OriginalDefinition;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(x: current.OriginalDefinition, y: target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates all named types in a namespace tree.
    /// </summary>
    static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            foreach (var nested in EnumerateNestedTypes(type: type))
            {
                yield return nested;
            }
        }

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var nestedType in EnumerateTypes(namespaceSymbol: nestedNamespace))
            {
                yield return nestedType;
            }
        }
    }

    /// <summary>
    /// Enumerates a type plus all of its nested types.
    /// </summary>
    static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        yield return type;

        foreach (var nestedType in type.GetTypeMembers())
        {
            foreach (var recursive in EnumerateNestedTypes(type: nestedType))
            {
                yield return recursive;
            }
        }
    }
    
    /// <summary>
    /// Resolves the discriminator property name requested by the union attribute.
    /// </summary>
    static string GetDiscriminatorPropertyName(INamedTypeSymbol unionType)
    {
        var unionAttribute = unionType.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == "global::Cohesive.Prelude.UnionAttribute");

        if (unionAttribute is null)
        {
            return "Type";
        }

        if (unionAttribute.ConstructorArguments.Length > 0
            && unionAttribute.ConstructorArguments[0].Value is string constructorValue
            && !string.IsNullOrWhiteSpace(value: constructorValue))
        {
            return constructorValue;
        }

        var namedArgument = unionAttribute.NamedArguments
            .FirstOrDefault(pair => string.Equals(
                a: pair.Key,
                b: "DiscriminatorPropertyName",
                comparisonType: StringComparison.Ordinal));

        if (namedArgument.Value.Value is string namedValue
            && !string.IsNullOrWhiteSpace(value: namedValue))
        {
            return namedValue;
        }

        return "Type";
    }

    /// <summary>
    /// Builds a deterministic source hint name for an annotated union type.
    /// </summary>
    static string BuildHintName(INamedTypeSymbol unionType)
    {
        var fullName = unionType
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace(oldValue: "global::", newValue: string.Empty)
            .Replace(oldChar: '.', newChar: '_')
            .Replace(oldChar: '+', newChar: '_')
            .Replace(oldChar: '<', newChar: '_')
            .Replace(oldChar: '>', newChar: '_')
            .Replace(oldChar: ',', newChar: '_')
            .Replace(oldChar: ' ', newChar: '_')
            .Replace(oldChar: '?', newChar: '_');

        return $"{fullName}.Union.g.cs";
    }

    /// <summary>
    /// Checks whether a type declaration includes the <c>partial</c> modifier.
    /// </summary>
    static bool IsPartial(INamedTypeSymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(static modifier => modifier.IsKind(kind: SyntaxKind.PartialKeyword)));
    }

    /// <summary>
    /// Formats a symbol as a fully-qualified type name.
    /// </summary>
    static string FullyQualified(ITypeSymbol type)
    {
        return type.ToDisplayString(FullyQualifiedNullableFormat);
    }

    /// <summary>
    /// Generates the partial type declaration heading.
    /// </summary>
    static string BuildTypeDeclarationHeader(INamedTypeSymbol unionType)
    {
        var builder = new StringBuilder();
        var accessibility = ToAccessibilityPrefix(accessibility: unionType.DeclaredAccessibility);
        var typeParameters = BuildTypeParameterList(unionType: unionType);

        if (!string.IsNullOrWhiteSpace(value: accessibility))
        {
            builder.Append(accessibility).Append(' ');
        }

        if (unionType.TypeKind is TypeKind.Class && unionType.IsAbstract)
        {
            builder.Append("abstract ");
        }

        if (unionType.TypeKind is TypeKind.Struct && unionType.IsReadOnly)
        {
            builder.Append("readonly ");
        }

        builder.Append("partial ");

        if (unionType.IsRecord)
        {
            builder.Append("record");
            if (unionType.TypeKind is TypeKind.Struct)
            {
                builder.Append(" struct");
            }

            builder.Append(' ').Append(unionType.Name).Append(typeParameters);
            return builder.ToString();
        }

        if (unionType.TypeKind is TypeKind.Struct)
        {
            builder.Append("struct ");
        }
        else
        {
            builder.Append("class ");
        }

        builder.Append(unionType.Name).Append(typeParameters);
        return builder.ToString();
    }
    
    /// <summary>
    /// Returns the generic type parameter list for a declaration header.
    /// </summary>
    static string BuildTypeParameterList(INamedTypeSymbol unionType)
    {
        if (unionType.TypeParameters.IsEmpty)
        {
            return string.Empty;
        }

        var names = string.Join(
            separator: ", ",
            values: unionType.TypeParameters.Select(selector: static typeParameter => typeParameter.Name));
        return $"<{names}>";
    }
    
    /// <summary>
    /// Appends type parameter constraints for a generated partial declaration.
    /// </summary>
    static void AppendTypeParameterConstraints(StringBuilder builder, INamedTypeSymbol unionType)
    {
        foreach (var typeParameter in unionType.TypeParameters)
        {
            var constraints = new List<string>();

            if (typeParameter.HasUnmanagedTypeConstraint)
            {
                constraints.Add(item: "unmanaged");
            }
            else if (typeParameter.HasValueTypeConstraint)
            {
                constraints.Add(item: "struct");
            }
            else if (typeParameter.HasReferenceTypeConstraint)
            {
                constraints.Add(item: typeParameter.ReferenceTypeConstraintNullableAnnotation is NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }
            else if (typeParameter.HasNotNullConstraint)
            {
                constraints.Add(item: "notnull");
            }

            constraints.AddRange(typeParameter.ConstraintTypes.Select(FullyQualified));

            if (typeParameter.HasConstructorConstraint)
            {
                constraints.Add(item: "new()");
            }

            if (constraints.Count is 0)
            {
                continue;
            }

            builder
                .Append("    where ")
                .Append(typeParameter.Name)
                .Append(" : ")
                .Append(string.Join(separator: ", ", values: constraints))
                .AppendLine();
        }
    }

    /// <summary>
    /// Maps Roslyn accessibility values to declaration keywords.
    /// </summary>
    static string ToAccessibilityPrefix(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedAndInternal => "protected internal",
            Accessibility.ProtectedOrInternal => "private protected",
            _ => ""
        };
    }

    /// <summary>
    /// Escapes C# keywords when used as generated identifiers.
    /// </summary>
    static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(text: identifier) is SyntaxKind.None
            ? identifier
            : $"@{identifier}";
    }

    /// <summary>
    /// Reports a diagnostic located at a union type declaration.
    /// </summary>
    static void Report(
        SourceProductionContext productionContext,
        DiagnosticDescriptor descriptor,
        INamedTypeSymbol unionType,
        object[] args)
    {
        var location = unionType.Locations.FirstOrDefault() ?? Location.None;
        productionContext.ReportDiagnostic(
            Diagnostic.Create(
                descriptor: descriptor,
                location: location,
                messageArgs: args));
    }

    enum UnionShape
    {
        /// <summary>
        /// Union cases are represented by derived runtime types.
        /// </summary>
        Subtype = 0,

        /// <summary>
        /// Union cases are represented by enum discriminator values.
        /// </summary>
        Tagged = 1
    }

    sealed record UnionCase(
        string Name,
        string TypeName,
        int Index,
        string PropertyName,
        string EnumValueExpression);

    sealed record TaggedUnionCase(
        string Name,
        string PropertyName,
        string TypeName,
        string EnumValueExpression,
        int Index);

    sealed record TaggedUnionModel(
        INamedTypeSymbol UnionType,
        string DiscriminatorPropertyName,
        string DiscriminatorTypeName,
        ImmutableArray<TaggedUnionCase> Cases,
        TaggedUnionFactory Factory);

    sealed record TaggedUnionFactory(TaggedUnionFactoryKind Kind, IMethodSymbol? Constructor);

    enum TaggedUnionFactoryKind
    {
        /// <summary>
        /// Values are created via constructor invocation.
        /// </summary>
        Constructor = 0,

        /// <summary>
        /// Values are created via object initializer.
        /// </summary>
        ObjectInitializer = 1
    }
}

/// <summary>
/// Emits generated source for the common union support surface.
/// </summary>
static class UnionSupportSourceEmitter
{
    /// <summary>
    /// Generates support source text for interfaces and Either types.
    /// </summary>
    public static string EmitPreludeSupportWithoutUnionAttribute(int maxEitherArity)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Cohesive.Prelude;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Runtime descriptor for a discriminated union value.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public interface IDiscriminatedUnion");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Total number of cases in the union definition.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    int CaseCount { get; }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Zero-based index of the currently active case.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    int CaseIndex { get; }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Value associated with the currently active case.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    object? CaseValue { get; }");
        builder.AppendLine("}");

        for (var arity = 2; arity <= maxEitherArity; arity++)
        {
            AppendIEitherInterface(builder: builder, arity: arity);
        }

        for (var arity = 2; arity <= maxEitherArity; arity++)
        {
            AppendEitherTypeEnum(builder: builder, arity: arity);
        }

        for (var arity = 2; arity <= maxEitherArity; arity++)
        {
            AppendEitherRecordStruct(builder: builder, arity: arity);
        }

        AppendEitherFactoryClass(builder: builder, maxEitherArity: maxEitherArity);

        return builder.ToString();
    }

    /// <summary>
    /// Appends an IEither interface with the requested arity.
    /// </summary>
    static void AppendIEitherInterface(StringBuilder builder, int arity)
    {
        var typeParameters = TypeParameters(arity: arity);

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder
            .Append("/// Contract for an Either with ")
            .Append(arity)
            .AppendLine(" cases.");
        builder.AppendLine("/// </summary>");
        builder
            .Append("public interface IEither<")
            .Append(typeParameters)
            .AppendLine("> : IDiscriminatedUnion");
        builder.AppendLine("{");

        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Matches the active case and projects it to a result value.");
        builder.AppendLine("    /// </summary>");
        builder.Append("    TResult Match<TResult>(");
        builder.Append(string.Join(", ", Enumerable.Range(start: 1, count: arity)
            .Select(index => $"global::System.Func<TCase{index}, TResult> onCase{index}")));
        builder.AppendLine(");");

        for (var index = 1; index <= arity; index++)
        {
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Attempts to get the value for case ")
                .Append(index)
                .AppendLine(".");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    bool TryGetCase")
                .Append(index)
                .Append("([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(returnValue: false)] out TCase")
                .Append(index)
                .AppendLine(" value);");
        }

        builder.AppendLine("}");
    }

    /// <summary>
    /// Appends the enum discriminator type for one Either arity.
    /// </summary>
    static void AppendEitherTypeEnum(StringBuilder builder, int arity)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder
            .Append("/// Tag values for Either arity ")
            .Append(arity)
            .AppendLine(".");
        builder.AppendLine("/// </summary>");
        builder
            .Append("public enum Either")
            .Append(arity)
            .AppendLine("Type");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Represents an uninitialized Either value.</summary>");
        builder.AppendLine("    None = 0,");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("    /// <summary>Represents case ")
                .Append(index)
                .AppendLine(".</summary>");
            builder
                .Append("    Case")
                .Append(index)
                .Append(" = ")
                .Append(index)
                .AppendLine(index < arity ? "," : string.Empty);
        }

        builder.AppendLine("}");
    }

    /// <summary>
    /// Appends a readonly record struct Either with one type parameter per case.
    /// </summary>
    static void AppendEitherRecordStruct(StringBuilder builder, int arity)
    {
        var typeParameters = TypeParameters(arity: arity);
        var interfaceName = $"IEither<{typeParameters}>";
        var typeName = $"Either<{typeParameters}>";
        var enumName = $"Either{arity}Type";

        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder
            .Append("/// Either value with arity ")
            .Append(arity)
            .AppendLine(".");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("[global::System.Diagnostics.DebuggerDisplay(\"{DebuggerDisplay,nq}\")]");
        builder
            .Append("public readonly record struct ")
            .Append(typeName)
            .Append(" : ")
            .Append(interfaceName)
            .AppendLine();
        builder.AppendLine("{");

        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("    readonly TCase")
                .Append(index)
                .Append(" case")
                .Append(index)
                .AppendLine(";");
        }

        builder.AppendLine();
        builder
            .Append("    ")
            .Append("Either")
            .Append("(")
            .Append(enumName)
            .Append(" type");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append(", TCase")
                .Append(index)
                .Append(" case")
                .Append(index)
                .Append(" = default!");
        }

        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.AppendLine("        Type = type;");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("        this.case")
                .Append(index)
                .Append(" = case")
                .Append(index)
                .AppendLine(";");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Gets the active case discriminator.");
        builder.AppendLine("    /// </summary>");
        builder
            .Append("    public ")
            .Append(enumName)
            .AppendLine(" Type { get; }");
        builder.AppendLine();
        builder
            .Append("    int DiscriminatedUnionCaseCount => ")
            .Append(arity)
            .AppendLine(";");
        builder.AppendLine("    int DiscriminatedUnionCaseIndex => Type switch");
        builder.AppendLine("    {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("        ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .Append(" => ")
                .Append(index - 1)
                .AppendLine(",");
        }

        builder.AppendLine("        _ => throw new global::System.InvalidOperationException(message: \"Either value is uninitialized or has an unknown case.\"),");
        builder.AppendLine("    };");
        builder.AppendLine("    object? DiscriminatedUnionCaseValue => Type switch");
        builder.AppendLine("    {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("        ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .Append(" => case")
                .Append(index)
                .AppendLine(",");
        }

        builder.AppendLine("        _ => throw new global::System.InvalidOperationException(message: \"Unknown Either case.\"),");
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    int global::Cohesive.Prelude.IDiscriminatedUnion.CaseCount => DiscriminatedUnionCaseCount;");
        builder.AppendLine("    int global::Cohesive.Prelude.IDiscriminatedUnion.CaseIndex => DiscriminatedUnionCaseIndex;");
        builder.AppendLine("    object? global::Cohesive.Prelude.IDiscriminatedUnion.CaseValue => DiscriminatedUnionCaseValue;");
        builder.AppendLine("    string DebuggerDisplay => $\"Type = {Type}, CaseIndex = {DiscriminatedUnionCaseIndex}, CaseValue = {DiscriminatedUnionCaseValue}\";");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Deconstructs the Either into tag and case value for tuple pattern matching.");
        builder.AppendLine("    /// </summary>");
        builder
            .Append("    public void Deconstruct(out ")
            .Append(enumName)
            .AppendLine(" type, out object? caseValue)");
        builder.AppendLine("    {");
        builder.AppendLine("        type = Type;");
        builder.AppendLine("        caseValue = DiscriminatedUnionCaseValue;");
        builder.AppendLine("    }");

        for (var index = 1; index <= arity; index++)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Determines whether case ")
                .Append(index)
                .AppendLine(" is active.");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public bool IsCase")
                .Append(index)
                .Append("() => Type == ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .AppendLine(";");
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Attempts to get the value for case ")
                .Append(index)
                .AppendLine(".");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public bool TryGetCase")
                .Append(index)
                .Append("([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(returnValue: false)] out TCase")
                .Append(index)
                .AppendLine(" value)");
            builder.AppendLine("    {");
            builder
                .Append("        if (Type == ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .AppendLine(")");
            builder.AppendLine("        {");
            builder
                .Append("            value = case")
                .Append(index)
                .AppendLine(";");
            builder.AppendLine("            return true;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        value = default!;");
            builder.AppendLine("        return false;");
            builder.AppendLine("    }");
        }

        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Matches the active case and projects it to a result value.");
        builder.AppendLine("    /// </summary>");
        builder.Append("    public TResult Match<TResult>(");
        builder.Append(string.Join(", ", Enumerable.Range(start: 1, count: arity)
            .Select(index => $"global::System.Func<TCase{index}, TResult> onCase{index}")));
        builder.AppendLine(")");
        builder.AppendLine("    {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("        global::System.ArgumentNullException.ThrowIfNull(argument: onCase")
                .Append(index)
                .AppendLine(");");
        }

        builder.AppendLine();
        builder.AppendLine("        return Type switch");
        builder.AppendLine("        {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("            ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .Append(" => onCase")
                .Append(index)
                .Append("(case")
                .Append(index)
                .Append("),")
                .AppendLine();
        }

        builder.AppendLine("            _ => throw new global::System.InvalidOperationException(message: \"Unknown Either case.\"),");
        builder.AppendLine("        };");
        builder.AppendLine("    }");

        for (var index = 1; index <= arity; index++)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder
                .Append("    /// Creates an Either value containing case ")
                .Append(index)
                .AppendLine(".");
            builder.AppendLine("    /// </summary>");
            builder
                .Append("    public static ")
                .Append(typeName)
                .Append(" FromCase")
                .Append(index)
                .Append("(TCase")
                .Append(index)
                .Append(" value) => new(type: ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .Append(", case")
                .Append(index)
                .AppendLine(": value);");
        }

        AppendEitherJsonConverter(
            builder: builder,
            arity: arity,
            typeName: typeName,
            enumName: enumName);

        builder.AppendLine("}");
    }
    
    /// <summary>
    /// Appends non-generic Either factory helpers for all generated arities.
    /// </summary>
    static void AppendEitherFactoryClass(StringBuilder builder, int maxEitherArity)
    {
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Non-generic factory surface for Either values.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public static class Either");
        builder.AppendLine("{");

        for (var arity = 2; arity <= maxEitherArity; arity++)
        {
            var typeParameters = TypeParameters(arity: arity);
            var eitherType = $"global::Cohesive.Prelude.Either<{typeParameters}>";

            for (var caseIndex = 1; caseIndex <= arity; caseIndex++)
            {
                builder.AppendLine("    /// <summary>");
                builder
                    .Append("    /// Creates an Either with arity ")
                    .Append(arity)
                    .Append(" using Case")
                    .Append(caseIndex)
                    .AppendLine(".");
                builder.AppendLine("    /// </summary>");
                builder
                    .Append("    public static ")
                    .Append(eitherType)
                    .Append(" FromCase")
                    .Append(caseIndex)
                    .Append("<")
                    .Append(typeParameters)
                    .Append(">(TCase")
                    .Append(caseIndex)
                    .Append(" value) => ")
                    .Append(eitherType)
                    .Append(".FromCase")
                    .Append(caseIndex)
                    .AppendLine("(value: value);");
                builder.AppendLine();
            }
        }

        builder.AppendLine("}");
    }

    /// <summary>
    /// Appends deterministic JSON serialization support for one Either arity.
    /// </summary>
    static void AppendEitherJsonConverter(StringBuilder builder, int arity, string typeName, string enumName)
    {
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Deterministic JSON converter for this Either arity.");
        builder.AppendLine("    /// </summary>");
        builder
            .Append("    public sealed class EitherJsonConverter : global::System.Text.Json.Serialization.JsonConverter<")
            .Append(typeName)
            .AppendLine(">");
        builder.AppendLine("    {");
        builder.AppendLine("        /// <inheritdoc />");
        builder
            .Append("        public override ")
            .Append(typeName)
            .AppendLine(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (reader.TokenType != global::System.Text.Json.JsonTokenType.StartObject)");
        builder.AppendLine("            {");
        builder.AppendLine("                throw new global::System.Text.Json.JsonException(message: \"Expected an object when reading Either JSON.\");");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder
            .Append("            var type = ")
            .Append(enumName)
            .AppendLine(".None;");
        builder.AppendLine("            var hasType = false;");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("            TCase")
                .Append(index)
                .Append(" case")
                .Append(index)
                .AppendLine(" = default!;");
            builder
                .Append("            var hasCase")
                .Append(index)
                .AppendLine(" = false;");
        }

        builder.AppendLine();
        builder.AppendLine("            while (reader.Read())");
        builder.AppendLine("            {");
        builder.AppendLine("                if (reader.TokenType == global::System.Text.Json.JsonTokenType.EndObject)");
        builder.AppendLine("                {");
        builder.AppendLine("                    break;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                if (reader.TokenType != global::System.Text.Json.JsonTokenType.PropertyName)");
        builder.AppendLine("                {");
        builder.AppendLine("                    throw new global::System.Text.Json.JsonException(message: \"Expected a property name when reading Either JSON.\");");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                var propertyName = reader.GetString() ?? throw new global::System.Text.Json.JsonException(message: \"JSON property name is required.\");");
        builder.AppendLine("                if (!reader.Read())");
        builder.AppendLine("                {");
        builder.AppendLine("                    throw new global::System.Text.Json.JsonException(message: \"Unexpected end of JSON while reading Either payload.\");");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                switch (propertyName)");
        builder.AppendLine("                {");
        builder.AppendLine("                    case \"type\":");
        builder.AppendLine("                    {");
        builder.AppendLine("                        if (hasType)");
        builder.AppendLine("                        {");
        builder.AppendLine("                            throw new global::System.Text.Json.JsonException(message: \"Duplicate 'type' property in Either JSON.\");");
        builder.AppendLine("                        }");
        builder.AppendLine();
        builder.AppendLine("                        if (reader.TokenType != global::System.Text.Json.JsonTokenType.String)");
        builder.AppendLine("                        {");
        builder.AppendLine("                            throw new global::System.Text.Json.JsonException(message: \"Either discriminator 'type' must be a string value.\");");
        builder.AppendLine("                        }");
        builder.AppendLine();
        builder.AppendLine("                        var discriminator = reader.GetString() ?? throw new global::System.Text.Json.JsonException(message: \"Either discriminator value is required.\");");
        builder.AppendLine("                        type = discriminator switch");
        builder.AppendLine("                        {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("                            \"Case")
                .Append(index)
                .Append("\" => ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .AppendLine(",");
        }

        builder.AppendLine("                            _ => throw new global::System.Text.Json.JsonException(message: $\"Unknown Either discriminator '{discriminator}'.\"),");
        builder.AppendLine("                        };");
        builder.AppendLine("                        hasType = true;");
        builder.AppendLine("                        break;");
        builder.AppendLine("                    }");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("                    case \"case")
                .Append(index)
                .AppendLine("\":");
            builder.AppendLine("                    {");
            builder
                .Append("                        if (hasCase")
                .Append(index)
                .AppendLine(")");
            builder.AppendLine("                        {");
            builder
                .Append("                            throw new global::System.Text.Json.JsonException(message: \"Duplicate 'case")
                .Append(index)
                .AppendLine("' property in Either JSON.\");");
            builder.AppendLine("                        }");
            builder
                .Append("                        case")
                .Append(index)
                .Append(" = global::System.Text.Json.JsonSerializer.Deserialize<TCase")
                .Append(index)
                .AppendLine(">(ref reader, options)!;");
            builder
                .Append("                        hasCase")
                .Append(index)
                .AppendLine(" = true;");
            builder.AppendLine("                        break;");
            builder.AppendLine("                    }");
        }

        builder.AppendLine("                    default:");
        builder.AppendLine("                        throw new global::System.Text.Json.JsonException(message: $\"Unknown Either property '{propertyName}'.\");");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (!hasType)");
        builder.AppendLine("            {");
        builder.AppendLine("                throw new global::System.Text.Json.JsonException(message: \"Missing required Either discriminator 'type'.\");");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return type switch");
        builder.AppendLine("            {");
        for (var index = 1; index <= arity; index++)
        {
            var otherCaseChecks = Enumerable.Range(start: 1, count: arity)
                .Where(predicate: otherIndex => otherIndex != index)
                .Select(selector: otherIndex => $"hasCase{otherIndex}")
                .ToArray();
            var mismatchCondition = otherCaseChecks.Length == 0
                ? "false"
                : string.Join(" || ", otherCaseChecks);

            builder
                .Append("                ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .AppendLine(" =>")
                .Append("                    ")
                .Append(mismatchCondition)
                .AppendLine()
                .AppendLine("                        ? throw new global::System.Text.Json.JsonException(message: \"Either payload does not match discriminator.\")")
                .Append("                        : ")
                .Append(typeName)
                .Append(".FromCase")
                .Append(index)
                .Append("(value: hasCase")
                .Append(index)
                .Append(" ? case")
                .Append(index)
                .AppendLine(" : default!),");
        }

        builder.AppendLine("                _ => throw new global::System.Text.Json.JsonException(message: \"Either discriminator is invalid.\"),");
        builder.AppendLine("            };");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        /// <inheritdoc />");
        builder
            .Append("        public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(typeName)
            .AppendLine(" value, global::System.Text.Json.JsonSerializerOptions options)");
        builder.AppendLine("        {");
        builder.AppendLine("            writer.WriteStartObject();");
        builder.AppendLine("            switch (value.Type)");
        builder.AppendLine("            {");
        for (var index = 1; index <= arity; index++)
        {
            builder
                .Append("                case ")
                .Append(enumName)
                .Append(".Case")
                .Append(index)
                .AppendLine(":");
            builder
                .Append("                    writer.WriteString(propertyName: \"type\", value: \"Case")
                .Append(index)
                .AppendLine("\");");
            builder
                .Append("                    if (value.case")
                .Append(index)
                .AppendLine(" is not null)");
            builder.AppendLine("                    {");
            builder
                .Append("                        writer.WritePropertyName(propertyName: \"case")
                .Append(index)
                .AppendLine("\");");
            builder
                .Append("                        global::System.Text.Json.JsonSerializer.Serialize<TCase")
                .Append(index)
                .AppendLine(">(writer: writer, value: value.case" + index + ", options: options);");
            builder.AppendLine("                    }");
            builder.AppendLine("                    break;");
        }

        builder.AppendLine("                default:");
        builder.AppendLine("                    throw new global::System.InvalidOperationException(message: \"Either value is uninitialized or has an unknown case.\");");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            writer.WriteEndObject();");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    /// <summary>
    /// Returns comma-separated type parameter names for a target arity.
    /// </summary>
    static string TypeParameters(int arity)
    {
        return string.Join(", ", Enumerable.Range(start: 1, count: arity).Select(index => $"TCase{index}"));
    }
}
