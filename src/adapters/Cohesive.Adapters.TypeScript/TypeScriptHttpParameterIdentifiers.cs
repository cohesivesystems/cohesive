using Cohesive.Api;

namespace Cohesive.Adapters.TypeScript;

/// <summary>Projects HTTP wire parameter names to deterministic valid TypeScript identifiers.</summary>
internal sealed class TypeScriptHttpParameterIdentifiers
{
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "arguments", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements",
        "import", "in", "instanceof", "interface", "let", "new", "null", "package", "private", "protected",
        "eval", "public", "return", "static", "super", "switch", "this", "throw", "true", "try", "typeof", "var",
        "void", "while", "with", "yield"
    };

    readonly Dictionary<HttpParameter, string> byParameter;
    readonly Dictionary<(HttpParameterSource Source, string WireName), string> byWireName;

    TypeScriptHttpParameterIdentifiers(
        Dictionary<HttpParameter, string> byParameter,
        Dictionary<(HttpParameterSource Source, string WireName), string> byWireName,
        string queryObject,
        string body)
    {
        this.byParameter = byParameter;
        this.byWireName = byWireName;
        QueryObject = queryObject;
        Body = body;
    }

    public string QueryObject { get; }

    public string Body { get; }

    public string this[HttpParameter parameter] => byParameter[parameter];

    public string Get(HttpParameterSource source, string wireName) =>
        byWireName.TryGetValue(WireKey(source, wireName), out var identifier)
            ? identifier
            : throw new InvalidOperationException(
                $"HTTP {source} parameter '{wireName}' has no generated TypeScript identifier.");

    public static TypeScriptHttpParameterIdentifiers Create(ApiOperation operation, HttpBinding http)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(http);
        var used = new HashSet<string>(StringComparer.Ordinal) { "http" };
        var byParameter = new Dictionary<HttpParameter, string>();
        var byWireName = new Dictionary<(HttpParameterSource, string), string>();
        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var parameter = http.Parameters[i];
            var key = WireKey(parameter.Source, parameter.Name);
            if (byWireName.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"API operation '{operation.Id}' repeats HTTP {parameter.Source} parameter wire name "
                    + $"'{parameter.Name}', so generated TypeScript cannot address it unambiguously.");
            }

            var identifier = Unique(ToIdentifier(parameter.Name, "parameter"), used);
            byParameter.Add(parameter, identifier);
            byWireName.Add(key, identifier);
        }

        return new(
            byParameter,
            byWireName,
            Unique("query", used),
            Unique("body", used));
    }

    static (HttpParameterSource Source, string WireName) WireKey(
        HttpParameterSource source,
        string wireName) =>
        (source, source == HttpParameterSource.Header ? wireName.ToUpperInvariant() : wireName);

    static string Unique(string baseName, HashSet<string> used)
    {
        var candidate = baseName;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName}{suffix}";
            suffix++;
        }
        return candidate;
    }

    internal static string ToIdentifier(string? wireName, string fallback)
    {
        var value = wireName?.Trim();
        var builder = new System.Text.StringBuilder(value?.Length ?? fallback.Length);
        var upperNext = false;
        if (!string.IsNullOrEmpty(value))
        {
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (!char.IsLetterOrDigit(current) && current != '_' && current != '$')
                {
                    upperNext = builder.Length > 0;
                    continue;
                }

                if (builder.Length == 0 && char.IsDigit(current))
                    builder.Append('_');

                builder.Append(upperNext ? char.ToUpperInvariant(current) : current);
                upperNext = false;
            }
        }

        if (builder.Length == 0)
            builder.Append(fallback);
        else
            builder[0] = char.ToLowerInvariant(builder[0]);

        var identifier = builder.ToString();
        return Reserved.Contains(identifier) ? $"_{identifier}" : identifier;
    }
}
