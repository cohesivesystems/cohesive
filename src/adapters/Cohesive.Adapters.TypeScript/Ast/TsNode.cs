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
    /// <summary>Initializes a new instance of the ts import declaration type.</summary>
    public TsImportDeclaration(string from, ImmutableArray<TsImportSpecifier> namedImports, bool isTypeOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        From = from;
        NamedImports = namedImports.IsDefault ? [] : namedImports;
        IsTypeOnly = isTypeOnly;
    }

    /// <summary>Gets the from.</summary>
    public string From { get; init; }

    /// <summary>Gets the named imports.</summary>
    public ImmutableArray<TsImportSpecifier> NamedImports { get; init; }

    /// <summary>Gets whether the import is type-only.</summary>
    public bool IsTypeOnly { get; init; }
}

/// <summary>
/// TypeScript export declaration.
/// </summary>
public sealed record TsExportDeclaration : TsStatement
{
    /// <summary>Initializes a new instance of the ts export declaration type.</summary>
    public TsExportDeclaration(ImmutableArray<TsImportSpecifier> namedExports, bool isTypeOnly = false)
    {
        NamedExports = namedExports.IsDefault ? [] : namedExports;
        IsTypeOnly = isTypeOnly;
    }

    /// <summary>Gets the named exports.</summary>
    public ImmutableArray<TsImportSpecifier> NamedExports { get; init; }

    /// <summary>Gets whether the export is type-only.</summary>
    public bool IsTypeOnly { get; init; }
}

/// <summary>
/// Import specifier.
/// </summary>
public sealed record TsImportSpecifier
{
    /// <summary>Initializes a new instance of the ts import specifier type.</summary>
    public TsImportSpecifier(string name, string? alias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Alias = alias;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the alias.</summary>
    public string? Alias { get; init; }
}

/// <summary>
/// TypeScript interface declaration.
/// </summary>
public sealed record TsInterfaceDeclaration : TsStatement
{
    /// <summary>Initializes a new instance of the ts interface declaration type.</summary>
    public TsInterfaceDeclaration(string name, ImmutableArray<TsPropertySignature> members, bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Members = members.IsDefault ? [] : members;
        IsExported = isExported;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the members.</summary>
    public ImmutableArray<TsPropertySignature> Members { get; init; }

    /// <summary>Gets whether the interface is exported.</summary>
    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript type alias declaration.
/// </summary>
public sealed record TsTypeAliasDeclaration : TsStatement
{
    /// <summary>Initializes a new instance of the ts type alias declaration type.</summary>
    public TsTypeAliasDeclaration(string name, TsTypeNode type, bool isExported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsExported = isExported;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the type.</summary>
    public TsTypeNode Type { get; init; }

    /// <summary>Gets whether the type alias is exported.</summary>
    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript function declaration.
/// </summary>
public sealed record TsFunctionDeclaration : TsStatement
{
    /// <summary>Initializes a new instance of the ts function declaration type.</summary>
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

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the parameters.</summary>
    public ImmutableArray<TsParameterDeclaration> Parameters { get; init; }

    /// <summary>Gets the return type.</summary>
    public TsTypeNode ReturnType { get; init; }

    /// <summary>Gets the body lines.</summary>
    public ImmutableArray<string> BodyLines { get; init; }

    /// <summary>Gets whether the function is exported.</summary>
    public bool IsExported { get; init; }
}

/// <summary>
/// TypeScript const declaration.
/// </summary>
public sealed record TsConstDeclaration : TsStatement
{
    /// <summary>Initializes a new instance of the ts const declaration type.</summary>
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

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the type.</summary>
    public TsTypeNode? Type { get; init; }

    /// <summary>Gets the satisfies type.</summary>
    public TsTypeNode? SatisfiesType { get; init; }

    /// <summary>Gets the initializer.</summary>
    public TsExpression Initializer { get; init; }

    /// <summary>Gets the as const.</summary>
    public bool AsConst { get; init; }

    /// <summary>Gets whether the constant is exported.</summary>
    public bool IsExported { get; init; }
}

/// <summary>
/// Function parameter declaration.
/// </summary>
public sealed record TsParameterDeclaration : TsNode
{
    /// <summary>Initializes a new instance of the ts parameter declaration type.</summary>
    public TsParameterDeclaration(string name, TsTypeNode type, bool isOptional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsOptional = isOptional;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the type.</summary>
    public TsTypeNode Type { get; init; }

    /// <summary>Gets whether the parameter is optional.</summary>
    public bool IsOptional { get; init; }
}

/// <summary>
/// Property signature in an interface or type literal.
/// </summary>
public sealed record TsPropertySignature : TsNode
{
    /// <summary>Initializes a new instance of the ts property signature type.</summary>
    public TsPropertySignature(string name, TsTypeNode type, bool isOptional = false, bool isReadonly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        IsOptional = isOptional;
        IsReadonly = isReadonly;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the type.</summary>
    public TsTypeNode Type { get; init; }

    /// <summary>Gets whether the property is optional.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets whether the property is read-only.</summary>
    public bool IsReadonly { get; init; }
}

/// <summary>
/// Object literal expression.
/// </summary>
public sealed record TsObjectLiteralExpression : TsExpression
{
    /// <summary>Initializes a new instance of the ts object literal expression type.</summary>
    public TsObjectLiteralExpression(ImmutableArray<TsObjectProperty> properties)
    {
        Properties = properties.IsDefault ? [] : properties;
    }

    /// <summary>Gets the properties.</summary>
    public ImmutableArray<TsObjectProperty> Properties { get; init; }
}

/// <summary>
/// Object literal property.
/// </summary>
public sealed record TsObjectProperty : TsNode
{
    /// <summary>Initializes a new instance of the ts object property type.</summary>
    public TsObjectProperty(string name, TsExpression value, bool isNumericName = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsNumericName = isNumericName;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }

    /// <summary>Gets the value.</summary>
    public TsExpression Value { get; init; }

    /// <summary>Gets whether the property name is numeric.</summary>
    public bool IsNumericName { get; init; }
}

/// <summary>
/// String literal expression.
/// </summary>
public sealed record TsStringLiteralExpression : TsExpression
{
    /// <summary>Initializes a new instance of the ts string literal expression type.</summary>
    public TsStringLiteralExpression(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the value.</summary>
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
    /// <summary>Initializes a new instance of the ts array literal expression type.</summary>
    public TsArrayLiteralExpression(ImmutableArray<TsExpression> elements)
    {
        Elements = elements.IsDefault ? [] : elements;
    }

    /// <summary>Gets the elements.</summary>
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
    /// <summary>Initializes a new instance of the ts raw type type.</summary>
    public TsRawType(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
    }

    /// <summary>Gets the text.</summary>
    public string Text { get; init; }
}

/// <summary>
/// Reference to a named type.
/// </summary>
public sealed record TsTypeReference : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts type reference type.</summary>
    public TsTypeReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the name.</summary>
    public string Name { get; init; }
}

/// <summary>
/// Array type.
/// </summary>
public sealed record TsArrayType(TsTypeNode ElementType) : TsTypeNode
{
    /// <summary>Gets the element type.</summary>
    public TsTypeNode ElementType { get; init; } = ElementType ?? throw new ArgumentNullException(nameof(ElementType));
}

/// <summary>
/// Function type.
/// </summary>
public sealed record TsFunctionType : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts function type type.</summary>
    public TsFunctionType(ImmutableArray<TsParameterDeclaration> parameters, TsTypeNode returnType)
    {
        Parameters = parameters.IsDefault ? [] : parameters;
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
    }

    /// <summary>Gets the parameters.</summary>
    public ImmutableArray<TsParameterDeclaration> Parameters { get; init; }

    /// <summary>Gets the return type.</summary>
    public TsTypeNode ReturnType { get; init; }
}

/// <summary>
/// Union type.
/// </summary>
public sealed record TsUnionType : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts union type type.</summary>
    public TsUnionType(ImmutableArray<TsTypeNode> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    /// <summary>Gets the members.</summary>
    public ImmutableArray<TsTypeNode> Members { get; init; }
}

/// <summary>
/// Intersection type.
/// </summary>
public sealed record TsIntersectionType : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts intersection type type.</summary>
    public TsIntersectionType(ImmutableArray<TsTypeNode> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    /// <summary>Gets the members.</summary>
    public ImmutableArray<TsTypeNode> Members { get; init; }
}

/// <summary>
/// Inline type literal.
/// </summary>
public sealed record TsTypeLiteral : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts type literal type.</summary>
    public TsTypeLiteral(ImmutableArray<TsPropertySignature> members)
    {
        Members = members.IsDefault ? [] : members;
    }

    /// <summary>Gets the members.</summary>
    public ImmutableArray<TsPropertySignature> Members { get; init; }
}

/// <summary>
/// Literal type.
/// </summary>
public sealed record TsLiteralType : TsTypeNode
{
    /// <summary>Initializes a new instance of the ts literal type type.</summary>
    public TsLiteralType(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Kind = TsLiteralKind.String;
    }

    /// <summary>Initializes a new instance of the ts literal type type.</summary>
    public TsLiteralType(long value)
    {
        NumericValue = value;
        Kind = TsLiteralKind.Number;
    }

    /// <summary>Initializes a new instance of the ts literal type type.</summary>
    public TsLiteralType(bool value)
    {
        BooleanValue = value;
        Kind = TsLiteralKind.Boolean;
    }

    /// <summary>Gets the kind.</summary>
    public TsLiteralKind Kind { get; init; }

    /// <summary>Gets the value.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the numeric value.</summary>
    public long NumericValue { get; init; }

    /// <summary>Gets the boolean value.</summary>
    public bool BooleanValue { get; init; }
}

/// <summary>
/// Explicit parenthesized type.
/// </summary>
public sealed record TsParenthesizedType(TsTypeNode Type) : TsTypeNode
{
    /// <summary>Gets the type.</summary>
    public TsTypeNode Type { get; init; } = Type ?? throw new ArgumentNullException(nameof(Type));
}

/// <summary>
/// Primitive TypeScript keywords used by the emitter.
/// </summary>
public enum TsKeyword
{
    /// <summary>Represents the string option.</summary>
    String = 0,
    /// <summary>Represents the number option.</summary>
    Number = 1,
    /// <summary>Represents the boolean option.</summary>
    Boolean = 2,
    /// <summary>Represents an unknown option.</summary>
    Unknown = 3,
    /// <summary>Represents the null option.</summary>
    Null = 4,
    /// <summary>Represents the never option.</summary>
    Never = 5
}

/// <summary>
/// Supported literal type kinds.
/// </summary>
public enum TsLiteralKind
{
    /// <summary>Represents the string option.</summary>
    String = 0,
    /// <summary>Represents the number option.</summary>
    Number = 1,
    /// <summary>Represents the boolean option.</summary>
    Boolean = 2
}
