using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Api;
using Cohesive.Api.CodeGen;
using Cohesive.CodeGen;
using Cohesive.Model;

namespace Cohesive.Adapters.OpenApi;

/// <summary>
/// Emits an OpenAPI 3.1 document from a Cohesive API definition.
/// </summary>
public sealed class OpenApiEmitter : IApiCodeEmitter
{
    readonly OpenApiEmitterOptions options;

    /// <summary>
    /// Creates an OpenAPI emitter.
    /// </summary>
    public OpenApiEmitter(OpenApiEmitterOptions? options = null)
    {
        this.options = options ?? new OpenApiEmitterOptions();
    }

    /// <inheritdoc />
    public string Language => "openapi";

    /// <inheritdoc />
    public CodeEmission Emit(in ApiCodeGenerationRequest request)
    {
        var builder = new OpenApiDocumentBuilder(request.Definition, options);
        var document = builder.Build();
        var text = document.ToJsonString(options: new()
        {
            WriteIndented = options.WriteIndented
        });

        return new(
            language: Language,
            documents: [new(fileName: options.FileName, text: text + "\n")]
            );
    }

    /// <summary>
    /// Emits a definition directly.
    /// </summary>
    public CodeEmission Emit(ApiDefinition definition) => Emit(new ApiCodeGenerationRequest(definition));

    sealed class OpenApiDocumentBuilder(ApiDefinition definition, OpenApiEmitterOptions options)
    {
        readonly SchemaRegistry schemas = new();

        public JsonObject Build()
        {
            var root = new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["info"] = new JsonObject
                {
                    ["title"] = options.Title,
                    ["version"] = options.Version
                },
                ["paths"] = BuildPaths(),
                ["components"] = new JsonObject
                {
                    ["schemas"] = schemas.BuildComponents()
                }
            };

            return root;
        }

        JsonObject BuildPaths()
        {
            JsonObject paths = [];
            for (var i = 0; i < definition.Operations.Count; i++)
            {
                var operation = definition.Operations[i];
                var path = NormalizeOpenApiPath(operation.Http.Route);
                if (paths[path] is not JsonObject pathItem)
                {
                    pathItem = [];
                    paths[path] = pathItem;
                }

                pathItem[operation.Http.Method.ToLowerInvariant()] = BuildOperation(operation);
            }

            return paths;
        }

        JsonObject BuildOperation(ApiOperation operation)
        {
            JsonObject node = new()
            {
                ["operationId"] = operation.Id.Value,
                ["summary"] = operation.Summary,
                ["tags"] = new JsonArray(operation.Tags.Select(static tag => JsonValue.Create(tag)).ToArray())
            };

            if (!string.IsNullOrWhiteSpace(operation.Description))
                node["description"] = operation.Description;

            if (operation.ScopePolicies.Count > 0)
                node["x-cohesive-scope-policies"] = BuildScopePolicies(operation.ScopePolicies);

            var parameters = BuildParameters(operation);
            if (parameters.Count > 0)
                node["parameters"] = parameters;

            if (operation.Http.Body is { } body)
            {
                node["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = schemas.SchemaFor(body.BodyType)
                        }
                    }
                };
            }

            node["responses"] = BuildResponses(operation);
            return node;
        }

        JsonArray BuildParameters(ApiOperation operation)
        {
            JsonArray parameters = [];
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            AppendRouteParameters(parameters, emitted, operation);
            AppendExplicitParameters(parameters, emitted, operation.Http.Parameters, HttpParameterSource.Query);
            AppendExplicitParameters(parameters, emitted, operation.Http.Parameters, HttpParameterSource.Header);

            if (operation.Http.Query is { } query)
                AppendQueryDtoParameters(parameters, emitted, query.QueryType);

            AppendScopeParameters(parameters, emitted, operation.ScopePolicies);

            return parameters;
        }

        void AppendRouteParameters(JsonArray parameters, HashSet<string> emitted, ApiOperation operation)
        {
            var routeNames = ParseRouteParameters(operation.Http.Route);
            for (var i = 0; i < routeNames.Count; i++)
            {
                var routeName = routeNames[i];
                var declared = FindParameter(operation.Http.Parameters, routeName, HttpParameterSource.Route);
                AppendParameter(parameters, emitted, BuildParameter(
                    name: routeName,
                    location: "path",
                    type: declared?.Type ?? typeof(string),
                    required: true));
            }
        }

        void AppendExplicitParameters(
            JsonArray parameters,
            HashSet<string> emitted,
            IReadOnlyList<HttpParameter> declaredParameters,
            HttpParameterSource source
            )
        {
            for (var i = 0; i < declaredParameters.Count; i++)
            {
                var parameter = declaredParameters[i];
                if (parameter.Source != source)
                    continue;

                AppendParameter(parameters, emitted, BuildParameter(
                    name: parameter.Name,
                    location: source == HttpParameterSource.Query ? "query" : "header",
                    type: parameter.Type,
                    required: !parameter.IsOptional && !CanSkipWhenUndefined(parameter.Type)));
            }
        }

        void AppendQueryDtoParameters(JsonArray parameters, HashSet<string> emitted, Type queryType)
        {
            var properties = ShapeTypeInspector.GetReadablePropertyMetadata(queryType);
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                AppendParameter(parameters, emitted, BuildParameter(
                    name: ResolveHttpName(property.Property),
                    location: "query",
                    type: property.Property.PropertyType,
                    required: !property.IsOptional));
            }
        }

        void AppendScopeParameters(
            JsonArray parameters,
            HashSet<string> emitted,
            IReadOnlyList<ApiScopePolicy> policies
            )
        {
            for (var i = 0; i < policies.Count; i++)
            {
                var policy = policies[i];
                if (policy.Binding == ApiScopeBinding.Header)
                {
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.SingleScopeParameterName,
                        location: "header",
                        type: typeof(string),
                        required: !policy.AllowDefaultScope && policy.Cardinality == ApiScopeCardinality.Single);
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.MultipleScopesParameterName,
                        location: "header",
                        type: typeof(string),
                        required: false);
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.ScopeModeParameterName,
                        location: "header",
                        type: typeof(string),
                        required: false);
                    continue;
                }

                if (policy.Binding == ApiScopeBinding.Query)
                {
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.SingleScopeParameterName,
                        location: "query",
                        type: typeof(string),
                        required: !policy.AllowDefaultScope && policy.Cardinality == ApiScopeCardinality.Single);
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.MultipleScopesParameterName,
                        location: "query",
                        type: typeof(string[]),
                        required: !policy.AllowDefaultScope && policy.Cardinality == ApiScopeCardinality.Multiple);
                    AppendScopeParameter(
                        parameters,
                        emitted,
                        policy.ScopeModeParameterName,
                        location: "query",
                        type: typeof(string),
                        required: false);
                }
            }
        }

        void AppendScopeParameter(
            JsonArray parameters,
            HashSet<string> emitted,
            string? name,
            string location,
            Type type,
            bool required
            )
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            AppendParameter(parameters, emitted, BuildParameter(name, location, type, required));
        }

        static void AppendParameter(JsonArray parameters, HashSet<string> emitted, JsonObject parameter)
        {
            var name = parameter["name"]?.GetValue<string>();
            var location = parameter["in"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
                return;

            if (!emitted.Add($"{location}\u001f{name}"))
                return;

            parameters.Add(parameter);
        }

        JsonObject BuildParameter(string name, string location, Type type, bool required) => new()
        {
            ["name"] = name,
            ["in"] = location,
            ["required"] = required,
            ["schema"] = schemas.SchemaFor(type)
        };

        static JsonArray BuildScopePolicies(IReadOnlyList<ApiScopePolicy> policies)
        {
            JsonArray values = [];
            for (var i = 0; i < policies.Count; i++)
                values.Add(BuildScopePolicy(policies[i]));

            return values;
        }

        static JsonObject BuildScopePolicy(ApiScopePolicy policy)
        {
            JsonObject value = new()
            {
                ["kind"] = policy.ScopeKind,
                ["cardinality"] = ToCamelCase(policy.Cardinality.ToString()),
                ["binding"] = ToCamelCase(policy.Binding.ToString()),
                ["access"] = ToCamelCase(policy.Access.ToString()),
                ["allowDefaultScope"] = policy.AllowDefaultScope
            };

            if (!string.IsNullOrWhiteSpace(policy.SingleScopeParameterName))
                value["singleScopeParameterName"] = policy.SingleScopeParameterName;
            if (!string.IsNullOrWhiteSpace(policy.MultipleScopesParameterName))
                value["multipleScopesParameterName"] = policy.MultipleScopesParameterName;
            if (!string.IsNullOrWhiteSpace(policy.ScopeModeParameterName))
                value["scopeModeParameterName"] = policy.ScopeModeParameterName;
            if (!string.IsNullOrWhiteSpace(policy.ResourceParameterName))
                value["resourceParameterName"] = policy.ResourceParameterName;
            if (policy.ResourceDerivation is { } resourceDerivation)
                value["resourceDerivation"] = BuildResourceScopeDerivation(resourceDerivation);

            return value;
        }

        static JsonObject BuildResourceScopeDerivation(ApiResourceScopeDerivation derivation)
        {
            JsonObject value = new()
            {
                ["strategy"] = derivation.Strategy
            };

            if (!string.IsNullOrWhiteSpace(derivation.Format))
                value["format"] = derivation.Format;
            if (!string.IsNullOrWhiteSpace(derivation.ScopeField))
                value["scopeField"] = derivation.ScopeField;

            return value;
        }

        JsonObject BuildResponses(ApiOperation operation)
        {
            var groupedResults = new SortedDictionary<int, List<ApiResultDefinition>>();
            for (var i = 0; i < operation.Results.Count; i++)
            {
                var result = operation.Results[i];
                if (result.Http is not { } http)
                    continue;

                if (!groupedResults.TryGetValue(http.StatusCode, out var values))
                {
                    values = [];
                    groupedResults[http.StatusCode] = values;
                }

                values.Add(result);
            }

            if (groupedResults.Count == 0)
                return [];

            JsonObject responses = [];
            foreach (var (statusCode, results) in groupedResults)
                responses[statusCode.ToString()] = BuildResponse(statusCode, results);

            return responses;
        }

        JsonObject BuildResponse(int statusCode, IReadOnlyList<ApiResultDefinition> results)
        {
            var response = new JsonObject
            {
                ["description"] = BuildResponseDescription(statusCode, results)
            };
            AppendResultExtensions(response, statusCode, results);

            var bodyResults = results.Where(static result => result.BodyType != typeof(void)).ToArray();
            if (bodyResults.Length == 0)
                return response;

            var schema = bodyResults.Length == 1
                ? schemas.SchemaFor(bodyResults[0].BodyType)
                : new()
                {
                    ["oneOf"] = new JsonArray(bodyResults
                        .Select(result => schemas.SchemaFor(result.BodyType))
                        .ToArray<JsonNode?>()
                    )
                };

            response["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = schema
                }
            };

            return response;
        }

        static void AppendResultExtensions(JsonObject response, int statusCode, IReadOnlyList<ApiResultDefinition> results)
        {
            if (results.Count == 1)
            {
                response["x-cohesive-result-id"] = results[0].Id;
                response["x-cohesive-result-kind"] = results[0].Kind.ToString();
            }

            response["x-cohesive-results"] = new JsonArray(results
                .Select(result => new JsonObject
                {
                    ["id"] = result.Id,
                    ["kind"] = result.Kind.ToString(),
                    ["isPrimary"] = result.IsPrimary,
                    ["httpStatusCode"] = result.Http?.StatusCode ?? statusCode
                })
                .ToArray<JsonNode?>());
        }

        static string BuildResponseDescription(int statusCode, IReadOnlyList<ApiResultDefinition> results)
        {
            if (results.Count == 1)
            {
                var result = results[0];
                if (!string.IsNullOrWhiteSpace(result.Description))
                    return result.Description;

                return DefaultStatusDescription(statusCode, result.Kind);
            }

            var descriptions = results
                .Select(result => string.IsNullOrWhiteSpace(result.Description)
                    ? result.Id
                    : result.Description)
                .ToArray();
            return string.Join("; ", descriptions);
        }

        static string DefaultStatusDescription(int statusCode, ApiResultKind kind) => statusCode switch
        {
            200 => "OK",
            201 => "Created",
            202 => "Accepted",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            412 => "Precondition Failed",
            422 => "Unprocessable Entity",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            _ => kind.ToString()
        };

        static HttpParameter? FindParameter(IReadOnlyList<HttpParameter> parameters, string name, HttpParameterSource source)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (parameter.Source == source && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
                    return parameter;
            }

            return null;
        }
    }

    sealed class SchemaRegistry
    {
        readonly Dictionary<Type, string> componentNameByType = new();
        readonly Dictionary<string, Type> typeByComponentName = new(StringComparer.Ordinal);
        readonly JsonObject components = [];

        public JsonObject SchemaFor(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return NullableSchema(SchemaFor(nullable));

            if (IsOpenNullableReference(type))
            {
                return NullableSchema(SchemaForNonNullable(type));
            }

            return SchemaForNonNullable(type);
        }

        public JsonObject BuildComponents() => components;

        JsonObject SchemaForNonNullable(Type type)
        {
            if (type == typeof(void))
                return [];

            if (TryGetPrimitiveSchema(type, out var primitive))
                return primitive;

            if (type.IsArray)
                return ArraySchema(type.GetElementType()!);

            if (TryGetSequenceElementType(type, out var elementType))
                return ArraySchema(elementType);

            if (type.IsEnum)
                return EnumSchema(type);

            if (IsDictionary(type, out var valueType))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = SchemaFor(valueType)
                };
            }

            return ReferenceSchema(type);
        }

        JsonObject ReferenceSchema(Type type)
        {
            var componentName = GetOrAddComponent(type);
            return new JsonObject
            {
                ["$ref"] = $"#/components/schemas/{componentName}"
            };
        }

        string GetOrAddComponent(Type type)
        {
            if (componentNameByType.TryGetValue(type, out var existing))
                return existing;

            var componentName = CreateUniqueComponentName(type);
            componentNameByType[type] = componentName;
            typeByComponentName[componentName] = type;

            components[componentName] = new JsonObject();
            components[componentName] = ObjectSchema(type);
            return componentName;
        }

        JsonObject ObjectSchema(Type type)
        {
            JsonObject propertiesNode = [];
            JsonArray requiredNode = [];
            var properties = ShapeTypeInspector.GetReadablePropertyMetadata(type);
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                var propertyName = ResolveJsonName(property.Property);
                propertiesNode[propertyName] = SchemaFor(property.Property.PropertyType);
                if (!property.IsOptional)
                    requiredNode.Add(propertyName);
            }

            JsonObject schema = new()
            {
                ["type"] = "object",
                ["properties"] = propertiesNode
            };

            if (requiredNode.Count > 0)
                schema["required"] = requiredNode;

            return schema;
        }

        JsonObject ArraySchema(Type elementType) => new()
        {
            ["type"] = "array",
            ["items"] = SchemaFor(elementType)
        };

        JsonObject EnumSchema(Type type)
        {
            var values = Enum.GetNames(type)
                .Select(static name => JsonValue.Create(name))
                .ToArray();
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(values)
            };
        }

        JsonObject NullableSchema(JsonObject schema) => new()
        {
            ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" })
        };

        string CreateUniqueComponentName(Type type)
        {
            var baseName = CreateComponentName(type);
            var name = baseName;
            var suffix = 2;
            while (typeByComponentName.TryGetValue(name, out var existing) && existing != type)
            {
                name = $"{baseName}{suffix}";
                suffix++;
            }

            return name;
        }

        static bool TryGetPrimitiveSchema(Type type, out JsonObject schema)
        {
            if (type == typeof(string) || type == typeof(char))
            {
                schema = new JsonObject { ["type"] = "string" };
                return true;
            }

            if (type == typeof(Guid))
            {
                schema = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
                return true;
            }

            if (type == typeof(DateOnly))
            {
                schema = new JsonObject { ["type"] = "string", ["format"] = "date" };
                return true;
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                schema = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
                return true;
            }

            if (type == typeof(TimeOnly))
            {
                schema = new JsonObject { ["type"] = "string", ["format"] = "time" };
                return true;
            }

            if (type == typeof(bool))
            {
                schema = new JsonObject { ["type"] = "boolean" };
                return true;
            }

            if (IsInteger(type))
            {
                schema = new JsonObject { ["type"] = "integer", ["format"] = type == typeof(long) || type == typeof(ulong) ? "int64" : "int32" };
                return true;
            }

            if (IsNumber(type))
            {
                schema = new JsonObject { ["type"] = "number" };
                return true;
            }

            if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(JsonNode))
            {
                schema = [];
                return true;
            }

            schema = [];
            return false;
        }

        static bool IsInteger(Type type) =>
            type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong);

        static bool IsNumber(Type type) =>
            type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);

        static bool IsOpenNullableReference(Type type) => false;
    }

    static bool CanSkipWhenUndefined(Type type) =>
        Nullable.GetUnderlyingType(type) is not null || !type.IsValueType;

    static bool TryGetSequenceElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(void);
            return false;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var candidate = interfaces[i];
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(void);
        return false;
    }

    static bool IsDictionary(Type type, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Dictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>))
            {
                var arguments = type.GetGenericArguments();
                if (arguments[0] == typeof(string))
                {
                    valueType = arguments[1];
                    return true;
                }
            }
        }

        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var candidate = interfaces[i];
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();
            if (definition != typeof(IReadOnlyDictionary<,>) && definition != typeof(IDictionary<,>))
                continue;

            var arguments = candidate.GetGenericArguments();
            if (arguments[0] != typeof(string))
                continue;

            valueType = arguments[1];
            return true;
        }

        valueType = typeof(void);
        return false;
    }

    static string ResolveHttpName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? ToSnakeCase(property.Name);

    static string ResolveJsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? property.Name;

    static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length == 1
            ? char.ToLowerInvariant(value[0]).ToString()
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    static string NormalizeOpenApiPath(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return route;

        var builder = new System.Text.StringBuilder(route.Length);
        for (var index = 0; index < route.Length; index++)
        {
            if (route[index] != '{')
            {
                builder.Append(route[index]);
                continue;
            }

            var end = route.IndexOf('}', index + 1);
            if (end < 0)
            {
                builder.Append(route[index]);
                continue;
            }

            var token = route.Substring(index + 1, end - index - 1);
            builder.Append('{');
            builder.Append(NormalizeRouteToken(token));
            builder.Append('}');
            index = end;
        }

        return builder.ToString();
    }

    static IReadOnlyList<string> ParseRouteParameters(string route)
    {
        var values = new List<string>();
        for (var index = 0; index < route.Length; index++)
        {
            if (route[index] != '{')
                continue;

            var end = route.IndexOf('}', index + 1);
            if (end <= index + 1)
                break;

            var normalized = NormalizeRouteToken(route.Substring(index + 1, end - index - 1));
            if (!string.IsNullOrWhiteSpace(normalized) && !values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                values.Add(normalized);

            index = end;
        }

        return values;
    }

    static string NormalizeRouteToken(string token)
    {
        var separator = token.IndexOfAny([':', '=', '?']);
        return separator >= 0 ? token[..separator] : token;
    }

    static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1])
                              || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    static string CreateComponentName(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return CreateComponentName(nullable);

        if (!type.IsGenericType)
            return SanitizeIdentifier(type.Name);

        var typeName = type.Name;
        var tick = typeName.IndexOf('`');
        if (tick >= 0)
            typeName = typeName[..tick];

        var builder = new System.Text.StringBuilder(typeName);
        builder.Append("Of");
        var arguments = type.GetGenericArguments();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                builder.Append("And");

            builder.Append(CreateComponentName(arguments[i]));
        }

        return SanitizeIdentifier(builder.ToString());
    }

    static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Schema";

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            builder.Append(char.IsLetterOrDigit(current) || current is '_' or '-' or '.' ? current : '_');
        }

        return builder.ToString();
    }
}
