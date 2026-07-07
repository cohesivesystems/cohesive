using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Cohesive.Prelude;

/// <summary>
/// Describes a route template parameter.
/// </summary>
public abstract record Parameter(string Name)
{
    /// <summary>
    /// Gets the parameter name.
    /// </summary>
    public string Name { get; init; } = Guard.RequireNotNullOrWhiteSpace(Name);
}

/// <summary>
/// Describes a required parameter bound into a route path template.
/// </summary>
public sealed record PathParameter(string Name) : Parameter(Name);

/// <summary>
/// Describes a parameter bound into a route query string.
/// </summary>
public sealed record QueryParameter(
    string Name,
    bool Required = false,
    bool Repeatable = false,
    string? QueryName = null
    ) : Parameter(Name)
{
    /// <summary>
    /// Gets the query string field name.
    /// </summary>
    public string QueryName { get; init; } = string.IsNullOrWhiteSpace(QueryName) ? Name : QueryName;
}

/// <summary>
/// Represents a route path template and its path and query parameters.
/// </summary>
public sealed record RouteTemplate(string Template, IReadOnlyList<Parameter>? Parameters)
{
    /// <summary>
    /// Initializes a route template without route parameters.
    /// </summary>
    /// <param name="Template">The route template.</param>
    public RouteTemplate(string Template)
        : this(Template, [])
    {
    }

    /// <summary>
    /// Gets the path template.
    /// </summary>
    public string Template { get; init; } = Guard.RequireNotNull(Template);

    /// <summary>
    /// Gets the route parameters.
    /// </summary>
    public IReadOnlyList<Parameter> Parameters { get; init; } = Parameters ?? [];

    /// <summary>
    /// Binds the template without any route values.
    /// </summary>
    public string Bind() => BindCore<object?>(null);

    /// <summary>
    /// Parses a path and query route template.
    /// </summary>
    /// <param name="template">The route template to parse.</param>
    /// <exception cref="FormatException">Query template segment contains an unsupported parameter placeholder.</exception>
    /// <exception cref="FormatException">Route template contains an unmatched opening brace.</exception>
    /// <exception cref="FormatException">Route template contains a nested parameter placeholder.</exception>
    /// <exception cref="FormatException">Route template contains an empty parameter placeholder.</exception>
    public static RouteTemplate Parse(string template)
    {
        var fragmentIndex = template.IndexOf('#', StringComparison.Ordinal);
        var beforeFragment = fragmentIndex >= 0 ? template[..fragmentIndex] : template;
        var fragment = fragmentIndex >= 0 ? template[fragmentIndex..] : string.Empty;
        var queryIndex = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var pathTemplate = queryIndex >= 0 ? beforeFragment[..queryIndex] : beforeFragment;
        var queryTemplate = queryIndex >= 0 ? beforeFragment[(queryIndex + 1)..] : string.Empty;
        
        var parameters = new List<Parameter>();
        var pathParameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ParseTemplateParameterNames(pathTemplate))
        {
            if (pathParameterNames.Add(name))
                parameters.Add(new PathParameter(name));
        }

        var retainedQuerySegments = new List<string>();
        ParseQueryTemplate(queryTemplate, parameters, retainedQuerySegments);

        var parsedTemplate = pathTemplate;
        if (retainedQuerySegments.Count > 0)
            parsedTemplate += "?" + string.Join("&", retainedQuerySegments);

        parsedTemplate += fragment;
        return new(parsedTemplate, parameters);
    }

    /// <summary>
    /// Binds the template using public CLR object properties as route values.
    /// </summary>
    /// <param name="values">The object whose public properties provide route values. Null is treated as an empty value set.</param>
    /// <exception cref="InvalidOperationException">Route template does not contain path parameter.</exception>
    /// <exception cref="InvalidOperationException">Missing required path parameter.</exception>
    /// <exception cref="InvalidOperationException">Route template contains unbound parameters.</exception>
    /// <exception cref="InvalidOperationException">Required path parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Path parameter must bind to a scalar value.</exception>
    /// <exception cref="InvalidOperationException">Query parameter is not repeatable.</exception>
    public string Bind(object? values) =>
        values is null ? BindCore<object?>(null) : Bind(ReflectionExtensions.ToPropertyValueDictionary(values));

    /// <summary>
    /// Binds the template using dictionary values.
    /// </summary>
    /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
    /// <exception cref="InvalidOperationException">Route template does not contain path parameter.</exception>
    /// <exception cref="InvalidOperationException">Missing required path parameter.</exception>
    /// <exception cref="InvalidOperationException">Route template contains unbound parameters.</exception>
    /// <exception cref="InvalidOperationException">Required path parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Path parameter must bind to a scalar value.</exception>
    /// <exception cref="InvalidOperationException">Query parameter is not repeatable.</exception>
    public string Bind<TValue>(IReadOnlyDictionary<string, TValue>? values) =>
        BindCore(values);

    /// <summary>
    /// Binds the template using dictionary values.
    /// </summary>
    /// <param name="values">The route values keyed by parameter name. Null is treated as an empty value set.</param>
    /// <exception cref="InvalidOperationException">Route template does not contain path parameter.</exception>
    /// <exception cref="InvalidOperationException">Missing required path parameter.</exception>
    /// <exception cref="InvalidOperationException">Route template contains unbound parameters.</exception>
    /// <exception cref="InvalidOperationException">Required path parameter is null.</exception>
    /// <exception cref="InvalidOperationException">Path parameter must bind to a scalar value.</exception>
    /// <exception cref="InvalidOperationException">Query parameter is not repeatable.</exception>
    /// <remarks>Supports values of type string, StringValues, IEnumerable.</remarks>
    public string Bind(IReadOnlyDictionary<string, object?>? values) =>
        BindCore(values);

    string BindCore<TValue>(IReadOnlyDictionary<string, TValue>? values)
    {
        var path = Template;
        foreach (var parameter in Parameters.OfType<PathParameter>())
        {
            var token = "{" + parameter.Name + "}";
            if (!path.Contains(token, StringComparison.Ordinal))
                throw new InvalidOperationException($"Route template '{Template}' does not contain path parameter '{parameter.Name}'.");

            if (values is null || !values.TryGetValue(parameter.Name, out var value))
                throw new InvalidOperationException($"Missing required path parameter '{parameter.Name}'.");

            var pathValue = ConvertPathValue(parameter.Name, value);
            path = path.Replace(token, Uri.EscapeDataString(pathValue), StringComparison.Ordinal);
        }

        if (path.Contains('{', StringComparison.Ordinal) || path.Contains('}', StringComparison.Ordinal))
            throw new InvalidOperationException($"Route template '{Template}' contains unbound path parameters.");

        var queryValues = new List<KeyValuePair<string, string>>();
        foreach (var parameter in Parameters.OfType<QueryParameter>())
            AppendQueryValues(parameter, values, queryValues);

        return AppendQueryString(path, queryValues);
    }

    static string ConvertPathValue(string name, object? value)
    {
        if (value is null)
            throw new InvalidOperationException($"Required path parameter '{name}' is null.");

        if (value is StringValues stringValues)
        {
            if (stringValues.Count != 1)
                throw new InvalidOperationException($"Path parameter '{name}' must bind to a scalar value.");

            return stringValues[0] ?? string.Empty;
        }

        if (value is not string && value is IEnumerable)
            throw new InvalidOperationException($"Path parameter '{name}' must bind to a scalar value.");

        return ConvertScalar(value);
    }

    static void AppendQueryValues<TValue>(
        QueryParameter parameter,
        IReadOnlyDictionary<string, TValue>? values,
        List<KeyValuePair<string, string>> queryValues
        )
    {
        if (values is null || !values.TryGetValue(parameter.Name, out var value))
        {
            if (parameter.Required)
                throw new InvalidOperationException($"Missing required query parameter '{parameter.Name}'.");

            return;
        }

        object? routeValue = value;
        if (routeValue is null)
        {
            if (parameter.Required)
                throw new InvalidOperationException($"Required query parameter '{parameter.Name}' is null.");
            return;
        }

        var count = 0;
        foreach (var queryValue in ConvertQueryValues(parameter, routeValue))
        {
            queryValues.Add(new(parameter.QueryName, queryValue));
            count++;
        }

        if (parameter.Required && count == 0)
            throw new InvalidOperationException($"Required query parameter '{parameter.Name}' has no values.");
    }

    static IEnumerable<string> ConvertQueryValues(QueryParameter parameter, object value)
    {
        if (value is string)
        {
            yield return ConvertScalar(value);
            yield break;
        }

        if (value is StringValues stringValues)
        {
            if (stringValues.Count > 1 && !parameter.Repeatable)
                throw new InvalidOperationException($"Query parameter '{parameter.Name}' is not repeatable.");

            foreach (var item in stringValues)
            {
                if (item is not null)
                    yield return item;
            }

            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            if (!parameter.Repeatable)
                throw new InvalidOperationException($"Query parameter '{parameter.Name}' is not repeatable.");

            foreach (var item in enumerable)
            {
                if (item is not null)
                    yield return ConvertScalar(item);
            }

            yield break;
        }

        yield return ConvertScalar(value);
    }

    static string ConvertScalar(object value) =>
        value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;

    static IEnumerable<string> ParseTemplateParameterNames(string template)
    {
        var index = 0;
        while (index < template.Length)
        {
            var openIndex = template.IndexOf('{', index);
            if (openIndex < 0)
            {
                if (template.IndexOf('}', index) >= 0)
                    throw new FormatException($"Route template '{template}' contains an unmatched closing brace.");

                yield break;
            }

            var closeIndex = template.IndexOf('}', openIndex + 1);
            if (closeIndex < 0)
                throw new FormatException($"Route template '{template}' contains an unmatched opening brace.");

            if (template.IndexOf('{', openIndex + 1, closeIndex - openIndex - 1) >= 0)
                throw new FormatException($"Route template '{template}' contains a nested parameter placeholder.");

            var name = template[(openIndex + 1)..closeIndex];
            if (string.IsNullOrWhiteSpace(name))
                throw new FormatException($"Route template '{template}' contains an empty parameter placeholder.");

            yield return name;
            index = closeIndex + 1;
        }
    }

    static void ParseQueryTemplate(string queryTemplate, List<Parameter> parameters, List<string> retainedQuerySegments)
    {
        if (queryTemplate.Length == 0)
            return;

        var queryParameterIndexes = new Dictionary<(string Name, string QueryName), int>();
        var segments = queryTemplate.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var equalsIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex < 0)
            {
                retainedQuerySegments.Add(segment);
                continue;
            }

            var queryName = Uri.UnescapeDataString(segment[..equalsIndex]);
            var valueTemplate = segment[(equalsIndex + 1)..];
            if (!TryParseExactParameterPlaceholder(valueTemplate, out var parameterName))
            {
                if (valueTemplate.Contains('{', StringComparison.Ordinal) || valueTemplate.Contains('}', StringComparison.Ordinal))
                    throw new FormatException($"Query template segment '{segment}' contains an unsupported parameter placeholder.");

                retainedQuerySegments.Add(segment);
                continue;
            }

            var key = (parameterName, queryName);
            if (queryParameterIndexes.TryGetValue(key, out var parameterIndex))
            {
                parameters[parameterIndex] = ((QueryParameter)parameters[parameterIndex]) with { Repeatable = true };
                continue;
            }

            queryParameterIndexes.Add(key, parameters.Count);
            parameters.Add(new QueryParameter(parameterName, QueryName: queryName));
        }
    }

    static bool TryParseExactParameterPlaceholder(string value, out string name)
    {
        name = string.Empty;
        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
            return false;

        var candidate = value[1..^1];
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('{', StringComparison.Ordinal) || candidate.Contains('}', StringComparison.Ordinal))
            throw new FormatException($"Parameter placeholder '{value}' is not valid.");

        name = candidate;
        return true;
    }

    static string AppendQueryString(string path, IReadOnlyList<KeyValuePair<string, string>> queryValues)
    {
        if (queryValues.Count == 0)
            return path;

        var fragmentIndex = path.IndexOf('#', StringComparison.Ordinal);
        var pathWithoutFragment = fragmentIndex >= 0 ? path.AsSpan(0, fragmentIndex) : path.AsSpan();
        var fragment = fragmentIndex >= 0 ? path.AsSpan(fragmentIndex) : [];
        var hasExistingQuery = pathWithoutFragment.IndexOf('?') >= 0;
        var requiresSeparator = pathWithoutFragment.Length > 0 && pathWithoutFragment[^1] is not '?' and not '&';
        var estimatedLength = path.Length + 1;
        foreach (var (name, value) in queryValues)
            estimatedLength += name.Length + value.Length + 2;

        Span<char> initialBuffer = stackalloc char[Math.Min(estimatedLength, 512)];
        var builder = new ValueStringBuilder(initialBuffer);
        builder.Append(pathWithoutFragment);
        if (!hasExistingQuery)
            builder.Append('?');
        else if (requiresSeparator)
            builder.Append('&');

        for (var i = 0; i < queryValues.Count; i++)
        {
            if (i > 0)
                builder.Append('&');

            var (name, value) = queryValues[i];
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        builder.Append(fragment);
        return builder.ToString();
    }
}
