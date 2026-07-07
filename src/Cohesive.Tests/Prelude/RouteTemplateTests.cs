using Microsoft.Extensions.Primitives;

namespace Cohesive.Tests.Prelude;

public sealed class RouteTemplateTests
{
    [Fact]
    public void Constructor_WithOnlyTemplate_CreatesTemplateWithEmptyParameters()
    {
        var template = new RouteTemplate("/tasks");

        Assert.Equal("/tasks", template.Template);
        Assert.Empty(template.Parameters);
        Assert.Equal("/tasks", template.Bind());
    }

    [Fact]
    public void Constructor_WithNullParameters_CreatesTemplateWithEmptyParameters()
    {
        var template = new RouteTemplate("/tasks", null);

        Assert.Empty(template.Parameters);
        Assert.Equal("/tasks", template.Bind());
    }

    [Fact]
    public void Bind_WithNullObjectValuesAndNoParameters_ReturnsTemplate()
    {
        var template = new RouteTemplate("/tasks");

        var href = template.Bind((object?)null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void Bind_WithNullDictionaryValuesAndNoParameters_ReturnsTemplate()
    {
        var template = new RouteTemplate("/tasks");

        var href = template.Bind((IReadOnlyDictionary<string, object?>?)null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void Bind_WithNullLiteralAndNoParameters_ReturnsTemplate()
    {
        var template = new RouteTemplate("/tasks");

        var href = template.Bind(null);

        Assert.Equal("/tasks", href);
    }

    [Fact]
    public void Bind_WithNullValuesAndUnboundPathPlaceholder_Throws()
    {
        var template = new RouteTemplate("/tasks/{processId}");

        var exception = Assert.Throws<InvalidOperationException>(() => template.Bind((object?)null));

        Assert.Equal("Route template '/tasks/{processId}' contains unbound path parameters.", exception.Message);
    }

    [Fact]
    public void Parse_WithPathAndQueryPlaceholders_CreatesRouteTemplate()
    {
        var template = RouteTemplate.Parse("/shapes/{id}/search?q={query}&type={type}");

        Assert.Equal("/shapes/{id}/search", template.Template);
        var pathParameter = Assert.IsType<PathParameter>(template.Parameters[0]);
        var queryParameter = Assert.IsType<QueryParameter>(template.Parameters[1]);
        var typeParameter = Assert.IsType<QueryParameter>(template.Parameters[2]);
        Assert.Equal("id", pathParameter.Name);
        Assert.Equal("query", queryParameter.Name);
        Assert.Equal("q", queryParameter.QueryName);
        Assert.Equal("type", typeParameter.Name);
        Assert.Equal("type", typeParameter.QueryName);
    }

    [Fact]
    public void Parse_WithPathAndQueryPlaceholders_BindsUsingQueryNames()
    {
        var template = RouteTemplate.Parse("/shapes/{id}/search?q={query}&type={type}");

        var href = template.Bind(new
        {
            id = "shape 1",
            query = "address.line1",
            type = "scalar"
        });

        Assert.Equal("/shapes/shape%201/search?q=address.line1&type=scalar", href);
    }

    [Fact]
    public void Parse_WithStaticQueryAndFragment_PreservesStaticQueryAndFragment()
    {
        var template = RouteTemplate.Parse("/tasks?prefix=compile&status={status}#results");

        var href = template.Bind(new { status = "Running" });

        Assert.Equal("/tasks?prefix=compile&status=Running#results", href);
    }

    [Fact]
    public void Parse_WithRepeatedQueryPlaceholder_MarksQueryParameterRepeatable()
    {
        var template = RouteTemplate.Parse("/tasks?status={status}&status={status}");

        var parameter = Assert.IsType<QueryParameter>(Assert.Single(template.Parameters));
        Assert.Equal("status", parameter.Name);
        Assert.Equal("status", parameter.QueryName);
        Assert.True(parameter.Repeatable);
    }

    [Fact]
    public void Parse_WithPartialQueryPlaceholder_Throws()
    {
        var exception = Assert.Throws<FormatException>(() => RouteTemplate.Parse("/search?q=prefix-{query}"));

        Assert.Equal("Query template segment 'q=prefix-{query}' contains an unsupported parameter placeholder.", exception.Message);
    }

    [Fact]
    public void Parse_WithUnmatchedPathBrace_Throws()
    {
        var exception = Assert.Throws<FormatException>(() => RouteTemplate.Parse("/shapes/{id/search"));

        Assert.Equal("Route template '/shapes/{id/search' contains an unmatched opening brace.", exception.Message);
    }

    [Fact]
    public void Bind_WithClrObject_BindsAndEscapesPathParameters()
    {
        var template = new RouteTemplate(
            "/edi-specs/{id}",
            [new PathParameter("id")]);

        var href = template.Bind(new { id = "x12 204/5030" });

        Assert.Equal("/edi-specs/x12%20204%2F5030", href);
    }

    [Fact]
    public void Bind_WithDictionary_BindsQueryParameters()
    {
        var template = new RouteTemplate(
            "/tasks",
            [
                new QueryParameter("status", Required: true),
                new QueryParameter("prefix")
            ]);

        var href = template.Bind(new Dictionary<string, string>
        {
            ["status"] = "Running",
            ["prefix"] = "CompileShapeGraph"
        });

        Assert.Equal("/tasks?status=Running&prefix=CompileShapeGraph", href);
    }

    [Fact]
    public void Bind_WithGenericDictionary_DoesNotEnumerateDictionary()
    {
        var template = new RouteTemplate(
            "/tasks/{processId}",
            [
                new PathParameter("processId"),
                new QueryParameter("status")
            ]);
        var values = new CountingReadOnlyDictionary<string>(new Dictionary<string, string>
        {
            ["processId"] = "compile-1",
            ["status"] = "Running"
        });

        var href = template.Bind(values);

        Assert.Equal("/tasks/compile-1?status=Running", href);
        Assert.Equal(0, values.EnumerationCount);
        Assert.Equal(2, values.TryGetValueCount);
    }

    [Fact]
    public void Bind_WithRepeatableQueryParameter_AppendsRepeatedQueryValues()
    {
        var template = new RouteTemplate(
            "/tasks",
            [new QueryParameter("status", Repeatable: true)]);

        var href = template.Bind(new Dictionary<string, IEnumerable<string>>
        {
            ["status"] = ["Running", "Failed"]
        });

        Assert.Equal("/tasks?status=Running&status=Failed", href);
    }

    [Fact]
    public void Bind_WithStringValuesPathParameter_BindsSingleValue()
    {
        var template = new RouteTemplate(
            "/edi-specs/{id}",
            [new PathParameter("id")]);

        var href = template.Bind(new Dictionary<string, StringValues>
        {
            ["id"] = new("x12 204/5030")
        });

        Assert.Equal("/edi-specs/x12%20204%2F5030", href);
    }

    [Fact]
    public void Bind_WithStringValuesPathParameterAndMultipleValues_Throws()
    {
        var template = new RouteTemplate(
            "/edi-specs/{id}",
            [new PathParameter("id")]);

        var exception = Assert.Throws<InvalidOperationException>(() => template.Bind(new Dictionary<string, StringValues>
        {
            ["id"] = new(["a", "b"])
        }));

        Assert.Equal("Path parameter 'id' must bind to a scalar value.", exception.Message);
    }

    [Fact]
    public void Bind_WithSingleStringValuesQueryParameter_BindsNonRepeatableValue()
    {
        var template = new RouteTemplate(
            "/tasks",
            [new QueryParameter("status")]);

        var href = template.Bind(new Dictionary<string, StringValues>
        {
            ["status"] = new("Running")
        });

        Assert.Equal("/tasks?status=Running", href);
    }

    [Fact]
    public void Bind_WithRepeatableStringValuesQueryParameter_AppendsRepeatedQueryValuesOnce()
    {
        var template = new RouteTemplate(
            "/tasks",
            [new QueryParameter("status", Repeatable: true)]);

        var href = template.Bind(new Dictionary<string, StringValues>
        {
            ["status"] = new(["Running", "Failed"])
        });

        Assert.Equal("/tasks?status=Running&status=Failed", href);
    }

    [Fact]
    public void Bind_WithMultipleStringValuesQueryParameterAndNonRepeatableParameter_Throws()
    {
        var template = new RouteTemplate(
            "/tasks",
            [new QueryParameter("status")]);

        var exception = Assert.Throws<InvalidOperationException>(() => template.Bind(new Dictionary<string, StringValues>
        {
            ["status"] = new(["Running", "Failed"])
        }));

        Assert.Equal("Query parameter 'status' is not repeatable.", exception.Message);
    }

    [Fact]
    public void Bind_WithExistingQueryAndFragment_AppendsQueryBeforeFragment()
    {
        var template = new RouteTemplate(
            "/tasks?prefix=compile#results",
            [new QueryParameter("status")]);

        var href = template.Bind(new { status = "Completed" });

        Assert.Equal("/tasks?prefix=compile&status=Completed#results", href);
    }

    [Fact]
    public void Bind_WithMissingRequiredQueryParameter_Throws()
    {
        var template = new RouteTemplate(
            "/tasks",
            [new QueryParameter("status", Required: true)]);

        var exception = Assert.Throws<InvalidOperationException>(template.Bind);

        Assert.Equal("Missing required query parameter 'status'.", exception.Message);
    }

    [Fact]
    public void Bind_WithEnumerablePathParameter_Throws()
    {
        var template = new RouteTemplate(
            "/tasks/{processId}",
            [new PathParameter("processId")]);

        var exception = Assert.Throws<InvalidOperationException>(() => template.Bind(new Dictionary<string, IEnumerable<string>>
        {
            ["processId"] = ["a", "b"]
        }));

        Assert.Equal("Path parameter 'processId' must bind to a scalar value.", exception.Message);
    }

    [Fact]
    public void ToPropertyValueDictionary_ReadsPublicProperties()
    {
        var values = ReflectionExtensions.ToPropertyValueDictionary(new TestRouteValues("edi-204", 25));

        Assert.Equal("edi-204", values["Id"]);
        Assert.Equal(25, values["Limit"]);
    }

    sealed record TestRouteValues(string Id, int Limit);

    sealed class CountingReadOnlyDictionary<TValue>(IReadOnlyDictionary<string, TValue> inner)
        : IReadOnlyDictionary<string, TValue>
    {
        public int EnumerationCount { get; private set; }

        public int TryGetValueCount { get; private set; }

        public TValue this[string key] => inner[key];

        public IEnumerable<string> Keys => inner.Keys;

        public IEnumerable<TValue> Values => inner.Values;

        public int Count => inner.Count;

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public bool TryGetValue(string key, out TValue value)
        {
            TryGetValueCount++;
            return inner.TryGetValue(key, out value!);
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            EnumerationCount++;
            return inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
