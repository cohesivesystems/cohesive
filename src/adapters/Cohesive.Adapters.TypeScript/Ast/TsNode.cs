using System.Collections.Immutable;

namespace Cohesive.Adapters.TypeScript.Ast;

/// <summary>
/// Base node for the TypeScript syntax tree.
/// </summary>
public abstract record TsNode;

/// <summary>
/// TypeScript document.
/// </summary>
public sealed record TsDocument : TsNode
{
    /// <summary>
    /// Creates a TypeScript document.
    /// </summary>
    public TsDocument(ImmutableArray<TsStatement> statements)
    {
        Statements = statements.IsDefault ? [] : statements;
    }

    /// <summary>
    /// Top-level statements.
    /// </summary>
    public ImmutableArray<TsStatement> Statements { get; init; }
}

/// <summary>
/// Base TypeScript statement.
/// </summary>
public abstract record TsStatement : TsNode;

/// <summary>
/// Base TypeScript type node.
/// </summary>
public abstract record TsTypeNode : TsNode;

/// <summary>
/// Base TypeScript expression node.
/// </summary>
public abstract record TsExpression : TsNode;

/// <summary>
/// TypeScript import declaration.
/// </summary>
public sealed record TsImportDeclaration : TsStatement
{
    public TsImportDeclaration(string from, ImmutableArray<TsImportSpecifier> namedImports, bool isTypeOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        From = from;
        NamedImports = namedImports.IsDefault ? [] : namedImports;
        IsTypeOnly = isTypeOnly;
    }

    public string From { get; init; }

    public ImmutableArray<TsImportSpecifier> NamedImports { get; init; }

    public bool IsTypeOnly { get; init; }
}

/// <summary>
/// TypeScript export declaration.
/// </summary>
public sealed record TsExportDeclaration : TsStatement
{
    public TsExportDeclaration(ImmutableArray<TsImportSpecifier> namedExports, bool isTypeOnly = false)
    {
        NamedExports = namedExports.IsDefault ? [] : namedExports;
        IsTypeOnly = isTypeOnly;
    }

    public ImmutableArray<TsImportSpecifier> NamedExports { get; init; }

    public bool IsTypeOnly { get; init; }
}

/// <summary>
/// Import specifier.
/// </summary>
public sealed record TsImportSpecifier
{
    public TsImportSpecifier(string name, string? alias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Alias = alias;
    }

    public string Name { get; init; }

    public string? Alias { get; init; }
}

/// <summary>
/// TypeScript interface declaration.
/// </summary>
public sealed record TsInterfaceDeclaration : TsStatement
{
    public TsInterfaceDeclaration(string name, ImmutableArray<TsPropertySignature> members, bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Members = members.IsDefault ? [] : members;
        IsExported = isExported;
    }

    public string Name { get; init; }

    public ImmutableArray<TsPropertySignature> Members { get; init; }

    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript type alias declaration.
/// </summary>
public sealed record TsTypeAliasDeclaration : TsStatement
{
    public TsTypeAliasDeclaration(string name, TsTypeNode type, bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsExported = isExported;
    }

    public string Name { get; init; }

    public TsTypeNode Type { get; init; }

    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript function declaration.
/// </summary>
public sealed record TsFunctionDeclaration : TsStatement
{
    public TsFunctionDeclaration(
        string name,
        ImmutableArray<TsParameterDeclaration> parameters,
        TsTypeNode returnType,
        ImmutableArray<string> bodyLines,
        bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Parameters = parameters.IsDefault ? [] : parameters;
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
        BodyLines = bodyLines.IsDefault ? [] : bodyLines;
        IsExported = isExported;
    }

    public string Name { get; init; }

    public ImmutableArray<TsParameterDeclaration> Parameters { get; init; }

    public TsTypeNode ReturnType { get; init; }

    public ImmutableArray<string> BodyLines { get; init; }

    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript const declaration.
/// </summary>
public sealed record TsConstDeclaration : TsStatement
{
    public TsConstDeclaration(
        string name,
        TsExpression initializer,
        TsTypeNode? type = null,
        TsTypeNode? satisfiesType = null,
        bool asConst = false,
        bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        Type = type;
        SatisfiesType = satisfiesType;
        AsConst = asConst;
        IsExported = isExported;
    }

    public string Name { get; init; }

    public TsTypeNode? Type { get; init; }

    public TsTypeNode? SatisfiesType { get; init; }

    public TsExpression Initializer { get; init; }

    public bool AsConst { get; init; }

    public bool IsExported { get; init; }
}

/// <summary>
/// Function parameter declaration.
/// </summary>
public sealed record TsParameterDeclaration : TsNode
{
    public TsParameterDeclaration(string name, TsTypeNode type, bool isOptional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsOptional = isOptional;
    }

    public string Name { get; init; }

    public TsTypeNode Type { get; init; }

    public bool IsOptional { get; init; }
}

/// <summary>
/// Property signature in an interface or type literal.
/// </summary>
public sealed record TsPropertySignature : TsNode
{
    public TsPropertySignature(string name, TsTypeNode type, bool isOptional = false, bool isReadonly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsOptional = isOptional;
        IsReadonly = isReadonly;
    }

    public string Name { get; init; }

    public TsTypeNode Type { get; init; }

    public bool IsOptional { get; init; }

    public bool IsReadonly { get; init; }
}

/// <summary>
/// Object literal expression.
/// </summary>
public sealed record TsObjectLiteralExpression : TsExpression
{
    public TsObjectLiteralExpression(ImmutableArray<TsObjectProperty> properties)
    {
        Properties = properties.IsDefault ? [] : properties;
    }

    public ImmutableArray<TsObjectProperty> Properties { get; init; }
}

/// <summary>
/// Object literal property.
/// </summary>
public sealed record TsObjectProperty : TsNode
{
    public TsObjectProperty(string name, TsExpression value, bool isNumericName = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsNumericName = isNumericName;
    }

    public string Name { get; init; }

    public TsExpression Value { get; init; }

    public bool IsNumericName { get; init; }
}

/// <summary>
/// String literal expression.
/// </summary>
public sealed record TsStringLiteralExpression : TsExpression
{
    public TsStringLiteralExpression(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; init; }
}

/// <summary>
/// Number literal expression.
/// </summary>
public sealed record TsNumberLiteralExpression(long Value) : TsExpression;

/// <summary>
/// Boolean literal expression.
/// </summary>
public sealed record TsBooleanLiteralExpression(bool Value) : TsExpression;

/// <summary>
/// Array literal expression.
/// </summary>
public sealed record TsArrayLiteralExpression : TsExpression
{
    public TsArrayLiteralExpression(ImmutableArray<TsExpression> elements)
    {
        Elements = elements.IsDefault ? [] : elements;
    }

    public ImmutableArray<TsExpression> Elements { get; init; }
}

/// <summary>
/// Primitive keyword type.
/// </summary>
public sealed record TsKeywordType(TsKeyword Keyword) : TsTypeNode;

/// <summary>
/// Raw TypeScript type text.
/// </summary>
public sealed record TsRawType : TsTypeNode
{
    public TsRawType(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
    }

    public string Text { get; init; }
}

/// <summary>
/// Reference to a named type.
/// </summary>
public sealed record TsTypeReference : TsTypeNode
{
    public TsTypeReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; init; }
}

/// <summary>
/// Array type.
/// </summary>
public sealed record TsArrayType(TsTypeNode ElementType) : TsTypeNode
{
    public TsTypeNode ElementType { get; init; } = ElementType ?? throw new ArgumentNullException(nameof(ElementType));
}

/// <summary>
/// Function type.
/// </summary>
public sealed record TsFunctionType : TsTypeNode
{
    public TsFunctionType(ImmutableArray<TsParameterDeclaration> parameters, TsTypeNode returnType)
    {
        Parameters = parameters.IsDefault ? [] : parameters;
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
    }

    public ImmutableArray<TsParameterDeclaration> Parameters { get; init; }

    public TsTypeNode ReturnType { get; init; }
}

/// <summary>
/// Union type.
/// </summary>
public sealed record TsUnionType : TsTypeNode
{
    public TsUnionType(ImmutableArray<TsTypeNode> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    public ImmutableArray<TsTypeNode> Members { get; init; }
}

/// <summary>
/// Intersection type.
/// </summary>
public sealed record TsIntersectionType : TsTypeNode
{
    public TsIntersectionType(ImmutableArray<TsTypeNode> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    public ImmutableArray<TsTypeNode> Members { get; init; }
}

/// <summary>
/// Inline type literal.
/// </summary>
public sealed record TsTypeLiteral : TsTypeNode
{
    public TsTypeLiteral(ImmutableArray<TsPropertySignature> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    public ImmutableArray<TsPropertySignature> Members { get; init; }
}

/// <summary>
/// Literal type.
/// </summary>
public sealed record TsLiteralType : TsTypeNode
{
    public TsLiteralType(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Kind = TsLiteralKind.String;
    }

    public TsLiteralType(long value)
    {
        NumericValue = value;
        Kind = TsLiteralKind.Number;
    }

    public TsLiteralType(bool value)
    {
        BooleanValue = value;
        Kind = TsLiteralKind.Boolean;
    }

    public TsLiteralKind Kind { get; init; }

    public string? Value { get; init; }

    public long NumericValue { get; init; }

    public bool BooleanValue { get; init; }
}

/// <summary>
/// Explicit parenthesized type.
/// </summary>
public sealed record TsParenthesizedType(TsTypeNode Type) : TsTypeNode
{
    public TsTypeNode Type { get; init; } = Type ?? throw new ArgumentNullException(nameof(Type));
}

/// <summary>
/// Primitive TypeScript keywords used by the emitter.
/// </summary>
public enum TsKeyword
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Unknown = 3,
    Null = 4,
    Never = 5
}

/// <summary>
/// Supported literal type kinds.
/// </summary>
public enum TsLiteralKind
{
    String = 0,
    Number = 1,
    Boolean = 2
}
