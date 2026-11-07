using Cohesive.Model;

namespace Cohesive.Adapters.TypeScript;

// Shared CLR/IR naming for shapes, API clients and mocks.
internal static class TypeScriptTypeNames
{
    internal static string FromClr(Type type) => FromId(ClrShapeIdentityConvention.GetShapeId(type).Value);

    internal static string FromId(string id)
    {
        var value = id;
        var isClrId = value.StartsWith("clr:", StringComparison.Ordinal);
        var lastColon = value.LastIndexOf(':');
        if (lastColon >= 0 && lastColon + 1 < value.Length)
            value = value[(lastColon + 1)..];

        if (isClrId)
        {
            // Strip qualification independently for each generic argument. Taking the last dot
            // of the entire identity loses the enclosing generic name.
            return string.Concat(value.Split(['<', '>', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var separator = Math.Max(part.LastIndexOf('.'), part.LastIndexOf('+'));
                    var simple = part[(separator + 1)..];
                    var tick = simple.IndexOf('`');
                    return ToIdentifierPart(tick < 0 ? simple : simple[..tick]);
                }));
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

}
