using System.Collections.Immutable;
using System.Text;
using Cohesive.CodeGen;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.CSharp;

/// <summary>
/// Validates and emits strongly typed C# relationship identifiers from a canonical catalog.
/// </summary>
/// <remarks>
/// This adapter is a pure interpretation of <see cref="RelationshipCatalog"/>. It does not infer,
/// repair, or persist relationships, and it does not require CLR types or compiler services.
/// Source-shape groups use the terminal non-version segment of the shape identifier. Relationship
/// members use the source-reference field name with a terminal <c>Id</c> or <c>Ids</c> removed.
/// </remarks>
public sealed class CSharpRelationshipCatalogEmitter
{
    const string GroupCollisionCode = "csharpRelationshipCatalog.groupSymbolCollision";
    const string MemberCollisionCode = "csharpRelationshipCatalog.memberSymbolCollision";
    const string RelationshipIdTypeName = "global::Cohesive.Relations.Model.RelationshipId";

    static readonly ImmutableHashSet<string> Keywords = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
        "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public", "readonly", "ref",
        "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        "add", "alias", "allows", "and", "ascending", "async", "await", "by", "descending",
        "dynamic", "equals", "extension", "field", "file", "from", "get", "global", "group",
        "init", "into", "join", "let", "managed", "nameof", "nint", "not", "notnull", "nuint",
        "on", "or", "orderby", "partial", "record", "remove", "required", "scoped", "select",
        "set", "unmanaged", "value", "var", "when", "where", "with", "yield");

    readonly CSharpRelationshipCatalogEmitterOptions options;
    readonly string namespaceName;
    readonly string rootClassName;
    readonly string rootSymbol;

    /// <summary>Creates a deterministic C# relationship catalog emitter.</summary>
    /// <param name="options">
    /// Optional output configuration. Framework defaults are used when the value is
    /// <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A configured namespace, root class name, or file name is empty or invalid, or the configured
    /// line terminator is not <c>\n</c> or <c>\r\n</c>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// A configured namespace, root class name, or file name is <see langword="null"/>.
    /// </exception>
    public CSharpRelationshipCatalogEmitter(CSharpRelationshipCatalogEmitterOptions? options = null)
    {
        this.options = options ?? new CSharpRelationshipCatalogEmitterOptions();
        namespaceName = ValidateNamespace(this.options.Namespace);
        rootSymbol = ValidateConfiguredIdentifier(this.options.RootClassName, nameof(options.RootClassName));
        rootClassName = EscapeIdentifier(rootSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.FileName);

        if (this.options.NewLine is not "\n" and not "\r\n")
        {
            throw new ArgumentException(
                "The C# emitter line terminator must be either '\\n' or '\\r\\n'.",
                nameof(options.NewLine));
        }
    }

    /// <summary>Emits deterministic C# identifiers for an exact relationship catalog.</summary>
    /// <param name="catalog">Canonical relationship catalog to validate, fingerprint, and emit.</param>
    /// <returns>
    /// The catalog fingerprint, structured diagnostics, and generated C# source when validation succeeds.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value with no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type unsupported by canonical relationship catalog serialization.
    /// </exception>
    public CSharpRelationshipCatalogEmissionResult Emit(RelationshipCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var catalogValidation = RelationshipCatalogValidator.Validate(catalog);
        if (!catalogValidation.IsValid)
            return new(catalogFingerprint: null, catalogValidation, emission: null);

        var fingerprint = RelationshipCatalogFingerprinter.Compute(catalog);

        var relationships = Symbolize(catalog);
        var symbolValidation = ValidateSymbols(relationships);
        var validation = DocumentValidationResult.Combine(catalogValidation, symbolValidation);
        if (!validation.IsValid)
            return new(fingerprint, validation, emission: null);

        return new(fingerprint, validation, EmitSource(relationships));
    }

    CodeEmission EmitSource(ImmutableArray<SymbolizedRelationship> relationships)
    {
        var writer = new PooledCodeWriter(
            initialCapacity: EstimateInitialCapacity(relationships),
            indentSize: 4,
            newLine: options.NewLine);

        try
        {
            if (options.EmitAutoGeneratedHeader)
            {
                writer.WriteLine("// <auto-generated/>");
                writer.WriteLine();
            }

            writer.Write("namespace ");
            writer.Write(namespaceName);
            writer.WriteLine(";");
            writer.WriteLine();
            writer.WriteLine("/// <summary>Canonical semantic relationship identifiers generated from a relationship catalog.</summary>");
            writer.Write("public static class ");
            writer.WriteLine(rootClassName);
            writer.WriteLine("{");
            writer.PushIndent();

            var groups = relationships
                .GroupBy(static relationship => relationship.Definition.SourceShape)
                .Select(static group => new SymbolizedGroup(
                    group.Key,
                    group.First().GroupSymbol,
                    [.. group
                        .OrderBy(static relationship => relationship.MemberSymbol, StringComparer.Ordinal)
                        .ThenBy(static relationship => relationship.Definition.Id.Value, StringComparer.Ordinal)]))
                .OrderBy(static group => group.GroupSymbol, StringComparer.Ordinal)
                .ThenBy(static group => group.Relationships[0].Definition.Id.Value, StringComparer.Ordinal)
                .ToArray();

            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex];
                writer.WriteLine("/// <summary>Relationship identifiers originating from one canonical source shape.</summary>");
                writer.Write("public static class ");
                writer.WriteLine(EscapeIdentifier(group.GroupSymbol));
                writer.WriteLine("{");
                writer.PushIndent();

                foreach (var relationship in group.Relationships)
                {
                    writer.WriteLine("/// <summary>Canonical semantic relationship identifier.</summary>");
                    writer.Write("public static readonly ");
                    writer.Write(RelationshipIdTypeName);
                    writer.Write(' ');
                    writer.Write(EscapeIdentifier(relationship.MemberSymbol));
                    writer.Write(" = new(\"");
                    WriteStringLiteralContent(relationship.Definition.Id.Value, ref writer);
                    writer.WriteLine("\");");
                }

                writer.PopIndent();
                writer.WriteLine("}");
                if (groupIndex < groups.Length - 1)
                    writer.WriteLine();
            }

            writer.PopIndent();
            writer.WriteLine("}");

            return new CodeEmission(
                language: "csharp",
                documents: [new GeneratedCodeDocument(options.FileName, writer.ToString())]);
        }
        finally
        {
            writer.Dispose();
        }
    }

    DocumentValidationResult ValidateSymbols(ImmutableArray<SymbolizedRelationship> relationships)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];

        var groups = relationships
            .GroupBy(static relationship => relationship.Definition.SourceShape)
            .Select(static group => new SymbolizedGroup(
                group.Key,
                group.First().GroupSymbol,
                [.. group]))
            .OrderBy(static group => group.GroupSymbol, StringComparer.Ordinal)
            .ThenBy(static group => group.Relationships[0].Definition.Id.Value, StringComparer.Ordinal)
            .ToArray();

        foreach (var collision in groups
                     .GroupBy(static group => group.GroupSymbol, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var sources = collision
                .Select(static group => group.SourceShape.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            var first = collision
                .SelectMany(static group => group.Relationships)
                .OrderBy(static relationship => relationship.Index)
                .First();

            diagnostics.Add(Error(
                GroupCollisionCode,
                $"Source shapes '{string.Join("', '", sources)}' normalize to the same C# group symbol '{collision.Key}'.",
                $"/relationships/{first.Index}/sourceShape"));
        }

        foreach (var group in groups)
        {
            var first = group.Relationships.OrderBy(static relationship => relationship.Index).First();
            if (string.Equals(group.GroupSymbol, rootSymbol, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    GroupCollisionCode,
                    $"Source shape '{group.SourceShape}' normalizes to C# group symbol '{group.GroupSymbol}', which conflicts with root class '{rootSymbol}'.",
                    $"/relationships/{first.Index}/sourceShape"));
            }

            foreach (var collision in group.Relationships
                         .GroupBy(static relationship => relationship.MemberSymbol, StringComparer.Ordinal)
                         .Where(static relationshipsBySymbol => relationshipsBySymbol.Count() > 1)
                         .OrderBy(static relationshipsBySymbol => relationshipsBySymbol.Key, StringComparer.Ordinal))
            {
                var relationshipIds = collision
                    .Select(static relationship => relationship.Definition.Id.Value)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var collisionFirst = collision.OrderBy(static relationship => relationship.Index).First();

                diagnostics.Add(Error(
                    MemberCollisionCode,
                    $"Relationships '{string.Join("', '", relationshipIds)}' on source shape '{group.SourceShape}' normalize to the same C# member symbol '{collision.Key}'.",
                    $"/relationships/{collisionFirst.Index}/sourceReference"));
            }

            foreach (var relationship in group.Relationships
                         .Where(relationship => string.Equals(
                             relationship.MemberSymbol,
                             group.GroupSymbol,
                             StringComparison.Ordinal))
                         .OrderBy(static relationship => relationship.Definition.Id.Value, StringComparer.Ordinal))
            {
                diagnostics.Add(Error(
                    MemberCollisionCode,
                    $"Relationship '{relationship.Definition.Id.Value}' normalizes to C# member symbol '{relationship.MemberSymbol}', which conflicts with its containing group.",
                    $"/relationships/{relationship.Index}/sourceReference"));
            }
        }

        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static ImmutableArray<SymbolizedRelationship> Symbolize(RelationshipCatalog catalog)
    {
        var builder = ImmutableArray.CreateBuilder<SymbolizedRelationship>(catalog.Relationships.Length);
        for (var index = 0; index < catalog.Relationships.Length; index++)
        {
            var relationship = catalog.Relationships[index];
            relationship.SourceReference.Segments[0].TryGetFieldIdentity(out var sourceReference);
            builder.Add(new(
                index,
                relationship,
                NormalizeShapeSymbol(relationship.SourceShape.ShapeId.Value),
                NormalizeMemberSymbol(sourceReference)));
        }

        return builder.MoveToImmutable();
    }

    static string NormalizeShapeSymbol(string shapeId)
    {
        const string clrShapePrefix = "clr:shape:";
        if (shapeId.StartsWith(clrShapePrefix, StringComparison.Ordinal))
        {
            var clrIdentity = shapeId[clrShapePrefix.Length..];
            var genericMarker = clrIdentity.IndexOfAny('<', '`');
            if (genericMarker > 0)
                clrIdentity = clrIdentity[..genericMarker];

            var separator = Math.Max(clrIdentity.LastIndexOf('.'), clrIdentity.LastIndexOf('+'));
            var candidate = separator >= 0 && separator < clrIdentity.Length - 1
                ? clrIdentity[(separator + 1)..]
                : clrIdentity;
            return NormalizeIdentifier(candidate, "Shape");
        }

        var segments = shapeId.Split(
            ['.', ':', '/', '+'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (!IsVersionSegment(segments[index]))
                return NormalizeIdentifier(segments[index], "Shape");
        }

        return NormalizeIdentifier(shapeId, "Shape");
    }

    static string NormalizeMemberSymbol(string sourceReference)
    {
        var symbol = NormalizeIdentifier(sourceReference, "Relationship");
        if (TryGetReferenceSuffixLength(symbol, out var suffixLength) && symbol.Length > suffixLength)
            return symbol[..^suffixLength];

        return symbol;
    }

    static string NormalizeIdentifier(string value, string fallback)
    {
        StringBuilder builder = new(value.Length + 1);
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                capitalize = true;
                continue;
            }

            if (builder.Length == 0 && char.IsDigit(character))
                builder.Append('_');

            builder.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    static bool TryGetReferenceSuffixLength(string value, out int suffixLength)
    {
        if (value.EndsWith("Ids", StringComparison.Ordinal)
            || value.EndsWith("IDs", StringComparison.Ordinal))
        {
            suffixLength = 3;
            return true;
        }

        if (value.EndsWith("Id", StringComparison.Ordinal)
            || value.EndsWith("ID", StringComparison.Ordinal))
        {
            suffixLength = 2;
            return true;
        }

        suffixLength = 0;
        return false;
    }

    static string ValidateNamespace(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var segments = value.Split('.');
        if (segments.Any(static segment => segment.Length == 0))
            throw new ArgumentException("The C# emitter namespace contains an empty segment.", nameof(value));

        return string.Join('.', segments.Select(static segment =>
            EscapeIdentifier(ValidateConfiguredIdentifier(segment, "Namespace"))));
    }

    static string ValidateConfiguredIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value[0] == '@' ? value[1..] : value;
        if (candidate.Length == 0
            || !IsIdentifierStart(candidate[0])
            || candidate.Skip(1).Any(static character => !IsIdentifierPart(character)))
        {
            throw new ArgumentException($"'{value}' is not a valid C# identifier.", parameterName);
        }

        return candidate;
    }

    static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

    static string EscapeIdentifier(string value) => Keywords.Contains(value) ? $"@{value}" : value;

    static bool IsVersionSegment(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] is not ('v' or 'V'))
            return false;

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsDigit(value[index]))
                return false;
        }

        return true;
    }

    static void WriteStringLiteralContent(string value, ref PooledCodeWriter writer)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    writer.Write("\\\\");
                    break;
                case '"':
                    writer.Write("\\\"");
                    break;
                case '\0':
                    writer.Write("\\0");
                    break;
                case '\a':
                    writer.Write("\\a");
                    break;
                case '\b':
                    writer.Write("\\b");
                    break;
                case '\f':
                    writer.Write("\\f");
                    break;
                case '\n':
                    writer.Write("\\n");
                    break;
                case '\r':
                    writer.Write("\\r");
                    break;
                case '\t':
                    writer.Write("\\t");
                    break;
                case '\v':
                    writer.Write("\\v");
                    break;
                default:
                    if (char.IsControl(character)
                        || char.IsSurrogate(character)
                        || character is '\u2028' or '\u2029')
                    {
                        writer.Write("\\u");
                        writer.Write(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        writer.Write(character);
                    }
                    break;
            }
        }
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(code, DiagnosticSeverity.Error, message, location);

    static int EstimateInitialCapacity(ImmutableArray<SymbolizedRelationship> relationships)
    {
        var estimate = 256;
        foreach (var relationship in relationships)
        {
            estimate += relationship.GroupSymbol.Length;
            estimate += relationship.MemberSymbol.Length;
            estimate += relationship.Definition.Id.Value.Length;
            estimate += 96;
        }

        return estimate;
    }

    sealed record SymbolizedGroup(
        QualifiedShapeId SourceShape,
        string GroupSymbol,
        ImmutableArray<SymbolizedRelationship> Relationships);

    sealed record SymbolizedRelationship(
        int Index,
        RelationshipDefinition Definition,
        string GroupSymbol,
        string MemberSymbol);
}
