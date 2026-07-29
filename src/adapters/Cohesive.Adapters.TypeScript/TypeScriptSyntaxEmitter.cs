using Cohesive.Adapters.TypeScript.Ast;
using Cohesive.CodeGen;

namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Renders a TypeScript syntax tree to source text.
/// </summary>
public static class TypeScriptSyntaxEmitter
{
    /// <summary>
    /// Writes a document into the supplied code writer.
    /// </summary>
    public static void WriteDocument<TWriter>(in TsDocument document, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        for (var i = 0; i < document.Statements.Length; i++)
        {
            WriteStatement(document.Statements[i], ref writer);
            writer.WriteLine();
            if (i + 1 < document.Statements.Length)
                writer.WriteLine();
        }
    }

    static void WriteStatement<TWriter>(TsStatement statement, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        switch (statement)
        {
            case TsImportDeclaration import:
                WriteImport(import, ref writer);
                return;

            case TsExportDeclaration export:
                WriteExport(export, ref writer);
                return;

            case TsInterfaceDeclaration @interface:
                WriteInterface(@interface, ref writer);
                return;

            case TsTypeAliasDeclaration alias:
                WriteTypeAlias(alias, ref writer);
                return;

            case TsFunctionDeclaration function:
                WriteFunction(function, ref writer);
                return;

            case TsConstDeclaration constant:
                WriteConst(constant, ref writer);
                return;

            default:
                throw new InvalidOperationException($"Unsupported TypeScript statement '{statement.GetType().Name}'.");
        }
    }

    static void WriteImport<TWriter>(TsImportDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write("import ");
        if (declaration.IsTypeOnly)
            writer.Write("type ");

        writer.Write("{ ");
        for (var i = 0; i < declaration.NamedImports.Length; i++)
        {
            if (i > 0)
                writer.Write(", ");

            var specifier = declaration.NamedImports[i];
            writer.Write(specifier.Name);
            if (!string.IsNullOrWhiteSpace(specifier.Alias))
            {
                writer.Write(" as ");
                writer.Write(specifier.Alias);
            }
        }

        writer.Write(" } from ");
        WriteStringLiteral(declaration.From, ref writer);
        writer.Write(";");
    }

    static void WriteExport<TWriter>(TsExportDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write("export ");
        if (declaration.IsTypeOnly)
            writer.Write("type ");

        writer.Write("{ ");
        for (var i = 0; i < declaration.NamedExports.Length; i++)
        {
            if (i > 0)
                writer.Write(", ");

            var specifier = declaration.NamedExports[i];
            writer.Write(specifier.Name);
            if (!string.IsNullOrWhiteSpace(specifier.Alias))
            {
                writer.Write(" as ");
                writer.Write(specifier.Alias);
            }
        }

        writer.Write(" };");
    }

    static void WriteInterface<TWriter>(TsInterfaceDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (declaration.IsExported)
            writer.Write("export ");

        writer.Write("interface ");
        writer.Write(declaration.Name);
        writer.WriteLine(" {");
        writer.PushIndent();
        for (var i = 0; i < declaration.Members.Length; i++)
            WriteProperty(declaration.Members[i], ref writer);
        writer.PopIndent();
        writer.Write("}");
    }

    static void WriteTypeAlias<TWriter>(TsTypeAliasDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (declaration.IsExported)
            writer.Write("export ");

        writer.Write("type ");
        writer.Write(declaration.Name);
        writer.Write(" = ");
        WriteType(declaration.Type, ref writer);
        writer.Write(";");
    }

    static void WriteFunction<TWriter>(TsFunctionDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (declaration.IsExported)
            writer.Write("export ");

        writer.Write("function ");
        writer.Write(declaration.Name);
        writer.Write('(');
        WriteParameters(declaration.Parameters, ref writer);
        writer.Write("): ");
        WriteType(declaration.ReturnType, ref writer);
        writer.WriteLine(" {");
        writer.PushIndent();
        for (var i = 0; i < declaration.BodyLines.Length; i++)
            writer.WriteLine(declaration.BodyLines[i]);
        writer.PopIndent();
        writer.Write("}");
    }

    static void WriteConst<TWriter>(TsConstDeclaration declaration, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (declaration.IsExported)
            writer.Write("export ");

        writer.Write("const ");
        writer.Write(declaration.Name);
        if (declaration.Type is not null)
        {
            writer.Write(": ");
            WriteType(declaration.Type, ref writer);
        }

        writer.Write(" = ");
        WriteExpression(declaration.Initializer, ref writer);
        if (declaration.AsConst)
            writer.Write(" as const");

        if (declaration.SatisfiesType is not null)
        {
            writer.Write(" satisfies ");
            WriteType(declaration.SatisfiesType, ref writer);
        }

        writer.Write(";");
    }

    static void WriteParameters<TWriter>(System.Collections.Immutable.ImmutableArray<TsParameterDeclaration> parameters, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
                writer.Write(", ");

            WriteParameter(parameters[i], ref writer);
        }
    }

    static void WriteParameter<TWriter>(TsParameterDeclaration parameter, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write(parameter.Name);
        if (parameter.IsOptional)
            writer.Write('?');

        writer.Write(": ");
        WriteType(parameter.Type, ref writer);
    }

    static void WriteProperty<TWriter>(TsPropertySignature property, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (property.IsReadonly)
            writer.Write("readonly ");

        WritePropertyName(property.Name, ref writer);
        if (property.IsOptional)
            writer.Write('?');

        writer.Write(": ");
        WriteType(property.Type, ref writer);
        writer.WriteLine(";");
    }

    static void WritePropertyName<TWriter>(string name, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (IsIdentifier(name))
        {
            writer.Write(name);
            return;
        }

        WriteStringLiteral(name, ref writer);
    }

    static void WriteObjectPropertyName<TWriter>(TsObjectProperty property, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (property.IsNumericName)
        {
            writer.Write(property.Name);
            return;
        }

        WritePropertyName(property.Name, ref writer);
    }

    static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!(char.IsLetter(value[0]) || value[0] == '_' || value[0] == '$'))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsLetterOrDigit(current) || current == '_' || current == '$')
                continue;

            return false;
        }

        return true;
    }

    static void WriteType<TWriter>(TsTypeNode type, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        switch (type)
        {
            case TsKeywordType keyword:
                WriteKeyword(keyword.Keyword, ref writer);
                return;

            case TsRawType raw:
                writer.Write(raw.Text);
                return;

            case TsTypeReference reference:
                writer.Write(reference.Name);
                if (!reference.TypeArguments.IsDefaultOrEmpty)
                {
                    writer.Write('<');
                    WriteDelimitedTypes(reference.TypeArguments, ", ", ref writer);
                    writer.Write('>');
                }
                return;

            case TsArrayType array:
                WriteArrayType(array, ref writer);
                return;

            case TsFunctionType function:
                WriteFunctionType(function, ref writer);
                return;

            case TsUnionType union:
                WriteDelimitedTypes(union.Members, " | ", ref writer);
                return;

            case TsIntersectionType intersection:
                WriteDelimitedTypes(intersection.Members, " & ", ref writer);
                return;

            case TsTypeLiteral literal:
                WriteTypeLiteral(literal, ref writer);
                return;

            case TsLiteralType literal:
                WriteLiteralType(literal, ref writer);
                return;

            case TsParenthesizedType parenthesized:
                writer.Write('(');
                WriteType(parenthesized.Type, ref writer);
                writer.Write(')');
                return;

            default:
                throw new InvalidOperationException($"Unsupported TypeScript type node '{type.GetType().Name}'.");
        }
    }

    static void WriteArrayType<TWriter>(TsArrayType array, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        WriteType(array.ElementType, ref writer);
        writer.Write("[]");
    }

    static void WriteFunctionType<TWriter>(TsFunctionType function, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write('(');
        WriteParameters(function.Parameters, ref writer);
        writer.Write(") => ");
        WriteType(function.ReturnType, ref writer);
    }

    static void WriteDelimitedTypes<TWriter>(System.Collections.Immutable.ImmutableArray<TsTypeNode> members, string separator, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        for (var i = 0; i < members.Length; i++)
        {
            if (i > 0)
                writer.Write(separator);

            WriteType(members[i], ref writer);
        }
    }

    static void WriteTypeLiteral<TWriter>(TsTypeLiteral literal, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (literal.Members.Length == 0)
        {
            writer.Write("{}");
            return;
        }

        writer.WriteLine("{");
        writer.PushIndent();
        for (var i = 0; i < literal.Members.Length; i++)
            WriteProperty(literal.Members[i], ref writer);
        writer.PopIndent();
        writer.Write("}");
    }

    static void WriteLiteralType<TWriter>(TsLiteralType literal, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        switch (literal.Kind)
        {
            case TsLiteralKind.String:
                WriteStringLiteral(literal.Value!, ref writer);
                return;

            case TsLiteralKind.Number:
                writer.Write(literal.NumericValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;

            case TsLiteralKind.Boolean:
                writer.Write(literal.BooleanValue ? "true" : "false");
                return;

            default:
                throw new InvalidOperationException($"Unsupported TypeScript literal kind '{literal.Kind}'.");
        }
    }

    static void WriteExpression<TWriter>(TsExpression expression, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        switch (expression)
        {
            case TsObjectLiteralExpression obj:
                WriteObjectLiteralExpression(obj, ref writer);
                return;

            case TsStringLiteralExpression literal:
                WriteStringLiteral(literal.Value, ref writer);
                return;

            case TsNumberLiteralExpression literal:
                writer.Write(literal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;

            case TsBooleanLiteralExpression literal:
                writer.Write(literal.Value ? "true" : "false");
                return;

            case TsNullLiteralExpression:
                writer.Write("null");
                return;

            case TsArrayLiteralExpression array:
                WriteArrayLiteralExpression(array, ref writer);
                return;

            default:
                throw new InvalidOperationException($"Unsupported TypeScript expression '{expression.GetType().Name}'.");
        }
    }

    static void WriteObjectLiteralExpression<TWriter>(TsObjectLiteralExpression obj, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (obj.Properties.Length == 0)
        {
            writer.Write("{}");
            return;
        }

        writer.WriteLine("{");
        writer.PushIndent();
        for (var i = 0; i < obj.Properties.Length; i++)
        {
            var property = obj.Properties[i];
            WriteObjectPropertyName(property, ref writer);
            writer.Write(": ");
            WriteExpression(property.Value, ref writer);
            writer.WriteLine(",");
        }

        writer.PopIndent();
        writer.Write("}");
    }

    static void WriteArrayLiteralExpression<TWriter>(TsArrayLiteralExpression array, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        if (array.Elements.Length == 0)
        {
            writer.Write("[]");
            return;
        }

        writer.WriteLine("[");
        writer.PushIndent();
        for (var i = 0; i < array.Elements.Length; i++)
        {
            WriteExpression(array.Elements[i], ref writer);
            writer.WriteLine(",");
        }
        writer.PopIndent();
        writer.Write("]");
    }

    static void WriteKeyword<TWriter>(TsKeyword keyword, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write(keyword switch
        {
            TsKeyword.String => "string",
            TsKeyword.Number => "number",
            TsKeyword.Boolean => "boolean",
            TsKeyword.Unknown => "unknown",
            TsKeyword.Null => "null",
            TsKeyword.Never => "never",
            _ => throw new InvalidOperationException($"Unsupported TypeScript keyword '{keyword}'.")
        });
    }

    static void WriteStringLiteral<TWriter>(string value, ref TWriter writer)
        where TWriter : ICodeWriter
    {
        writer.Write('\'');
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            switch (current)
            {
                case '\'':
                    writer.Write("\\'");
                    break;

                case '\\':
                    writer.Write("\\\\");
                    break;

                case '\r':
                    writer.Write("\\r");
                    break;

                case '\n':
                    writer.Write("\\n");
                    break;

                case '\t':
                    writer.Write("\\t");
                    break;

                default:
                    writer.Write(current);
                    break;
            }
        }

        writer.Write('\'');
    }
}
