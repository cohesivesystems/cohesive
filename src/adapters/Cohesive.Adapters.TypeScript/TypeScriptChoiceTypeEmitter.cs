using Cohesive.CodeGen;

namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Emits simple TypeScript choice types, such as string-literal unions backed by presentation state choices.
/// </summary>
public static class TypeScriptChoiceTypeEmitter
{
    /// <summary>
    /// Emits a TypeScript string-literal union type alias.
    /// </summary>
    public static string EmitStringUnionTypeAlias(
        string name,
        IReadOnlyList<string> choices,
        bool isExported = true
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
            throw new ArgumentException("A TypeScript union requires at least one choice.", nameof(choices));

        var writer = new PooledCodeWriter(initialCapacity: EstimateInitialCapacity(name, choices));
        try
        {
            if (isExported)
                writer.Write("export ");

            writer.Write("type ");
            writer.Write(name);
            writer.Write(" = ");

            for (var i = 0; i < choices.Count; i++)
            {
                if (i > 0)
                    writer.Write(" | ");

                WriteStringLiteral(choices[i], ref writer);
            }

            writer.Write(';');
            return writer.ToString();
        }
        finally
        {
            writer.Dispose();
        }
    }

    static int EstimateInitialCapacity(string name, IReadOnlyList<string> choices) =>
        32 + name.Length + choices.Sum(static choice => choice.Length + 5);

    static void WriteStringLiteral(string value, ref PooledCodeWriter writer)
    {
        writer.Write('\'');
        foreach (var current in value)
        {
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
