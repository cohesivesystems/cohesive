using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Api;
using Cohesive.Api.CodeGen;
using Cohesive.CodeGen;
using Cohesive.Model;

namespace Cohesive.Adapters.GraphQL;

/// <summary>
/// Emits a GraphQL schema from a Cohesive API definition.
/// </summary>
public sealed class GraphQLSchemaEmitter : IApiCodeEmitter
{
    readonly GraphQLSchemaEmitterOptions options;

    /// <summary>
    /// Creates a GraphQL schema emitter.
    /// </summary>
    public GraphQLSchemaEmitter(GraphQLSchemaEmitterOptions? options = null)
    {
        this.options = options ?? new GraphQLSchemaEmitterOptions();
    }

    /// <inheritdoc />
    public string Language => "graphql";

    /// <inheritdoc />
    public CodeEmission Emit(in ApiCodeGenerationRequest request)
    {
        var schema = EmitSchema(request.Definition);
        return new(
            language: Language,
            documents:
            [
                new(options.SchemaFileName, schema.Sdl),
                new(options.IntrospectionFileName, schema.IntrospectionJson)
            ]);
    }

    /// <summary>
    /// Emits both schema views for a definition.
    /// </summary>
    public GraphQLSchemaEmission EmitSchema(ApiDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var builder = new GraphQlSchemaBuilder(definition, options);
        return builder.Build();
    }

    sealed class GraphQlSchemaBuilder(ApiDefinition definition, GraphQLSchemaEmitterOptions options)
    {
        readonly GraphQlTypeRegistry types = new();
        readonly List<GraphQLRootField> queryFields = [];
        readonly List<GraphQLRootField> mutationFields = [];
        readonly HashSet<string> queryFieldNames = new(StringComparer.Ordinal);
        readonly HashSet<string> mutationFieldNames = new(StringComparer.Ordinal);

        public GraphQLSchemaEmission Build()
        {
            BuildRootFields();
            var sdl = BuildSdl();
            var introspectionJson = BuildIntrospectionJson();
            return new(sdl: sdl, introspectionJson: introspectionJson);
        }

        void BuildRootFields()
        {
            for (var i = 0; i < definition.Operations.Count; i++)
            {
                var operation = definition.Operations[i];
                var root = SelectRoot(operation);
                var usedNames = root == GraphQLRootKind.Query ? queryFieldNames : mutationFieldNames;
                var field = new GraphQLRootField(
                    Name: CreateUniqueRootFieldName(operation, usedNames),
                    Description: operation.Description ?? operation.Summary,
                    Arguments: BuildArguments(operation),
                    Type: BuildOperationTypeRef(operation),
                    Operation: operation
                    );

                if (root == GraphQLRootKind.Query)
                    queryFields.Add(field);
                else
                    mutationFields.Add(field);
            }
        }

        GraphQLTypeRef BuildOperationTypeRef(ApiOperation operation)
        {
            if (operation.Results.Count <= 1)
                return types.OutputTypeRef(operation.ResponseType, required: operation.ResponseType != typeof(void));

            return types.OperationResultTypeRef(operation, required: true);
        }

        IReadOnlyList<GraphQArgument> BuildArguments(ApiOperation operation)
        {
            List<GraphQArgument> arguments = [];

            for (var i = 0; i < operation.Http.Parameters.Count; i++)
            {
                var parameter = operation.Http.Parameters[i];
                if (parameter.Source != HttpParameterSource.Route)
                    continue;

                arguments.Add(new(
                    Name: ResolveGraphQlName(parameter.Name),
                    Type: types.InputTypeRef(parameter.Type, required: true),
                    Description: null));
            }

            for (var i = 0; i < operation.Http.Parameters.Count; i++)
            {
                var parameter = operation.Http.Parameters[i];
                if (parameter.Source is not (HttpParameterSource.Query or HttpParameterSource.Header))
                    continue;

                arguments.Add(new(
                    Name: ResolveGraphQlName(parameter.Name),
                    Type: types.InputTypeRef(parameter.Type, required: !parameter.IsOptional && !CanSkipWhenUndefined(parameter.Type)),
                    Description: null));
            }

            if (operation.Http.Query is { } query)
            {
                arguments.Add(new(
                    Name: "request",
                    Type: types.InputTypeRef(query.QueryType, required: false),
                    Description: null));
            }

            if (operation.Http.Body is { } body)
            {
                arguments.Add(new(
                    Name: "request",
                    Type: types.InputTypeRef(body.BodyType, required: true),
                    Description: null));
            }

            return arguments;
        }

        string BuildSdl()
        {
            var builder = new StringBuilder(capacity: 8192);
            if (!string.IsNullOrWhiteSpace(options.SchemaName))
            {
                builder.Append("# ");
                builder.AppendLine(options.SchemaName.Trim());
                builder.AppendLine();
            }

            if (options.IncludeCohesiveDirectives)
            {
                builder.AppendLine("directive @cohesiveOperation(id: String!, method: String!, route: String!, kind: String!, entity: String) on FIELD_DEFINITION");
                builder.AppendLine("directive @scope(kind: String!, cardinality: String!, binding: String!, access: String!, singleScopeParameterName: String, multipleScopesParameterName: String, scopeModeParameterName: String, resourceParameterName: String, resourceDerivationStrategy: String, resourceDerivationFormat: String, resourceDerivationScopeField: String, allowDefaultScope: Boolean!) repeatable on FIELD_DEFINITION");
                builder.AppendLine();
            }

            WriteCustomScalars(builder);
            WriteSchemaDeclaration(builder);
            WriteRootType(builder, "Query", queryFields);
            if (mutationFields.Count > 0)
                WriteRootType(builder, "Mutation", mutationFields);

            foreach (var type in types.Definitions)
                WriteTypeDefinition(builder, type);

            return builder.ToString();
        }

        void WriteCustomScalars(StringBuilder builder)
        {
            foreach (var scalar in types.CustomScalars.OrderBy(static value => value, StringComparer.Ordinal))
            {
                builder.Append("scalar ");
                builder.AppendLine(scalar);
                builder.AppendLine();
            }
        }

        void WriteSchemaDeclaration(StringBuilder builder)
        {
            builder.AppendLine("schema {");
            builder.AppendLine("  query: Query");
            if (mutationFields.Count > 0)
                builder.AppendLine("  mutation: Mutation");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        void WriteRootType(StringBuilder builder, string name, IReadOnlyList<GraphQLRootField> fields)
        {
            builder.Append("type ");
            builder.Append(name);
            builder.AppendLine(" {");

            if (fields.Count == 0)
                builder.AppendLine("  _empty: Boolean");

            for (var i = 0; i < fields.Count; i++)
                WriteRootField(builder, fields[i]);

            builder.AppendLine("}");
            builder.AppendLine();
        }

        void WriteRootField(StringBuilder builder, GraphQLRootField field)
        {
            WriteDescription(builder, "  ", field.Description);
            builder.Append("  ");
            builder.Append(field.Name);
            WriteArguments(builder, field.Arguments);
            builder.Append(": ");
            builder.Append(field.Type.ToSdl());

            if (options.IncludeCohesiveDirectives)
            {
                builder.Append(" @cohesiveOperation(id: ");
                AppendQuotedGraphQlString(builder, field.Operation.Id.Value);
                builder.Append(", method: ");
                AppendQuotedGraphQlString(builder, field.Operation.Http.Method.ToUpperInvariant());
                builder.Append(", route: ");
                AppendQuotedGraphQlString(builder, field.Operation.Http.Route);
                builder.Append(", kind: ");
                AppendQuotedGraphQlString(builder, field.Operation.Kind.ToString());
                if (field.Operation.Entity is { } entity)
                {
                    builder.Append(", entity: ");
                    AppendQuotedGraphQlString(builder, entity.Value);
                }

                builder.Append(')');
                AppendScopeDirectives(builder, field.Operation.ScopePolicies);
            }

            builder.AppendLine();
        }

        static void AppendScopeDirectives(StringBuilder builder, IReadOnlyList<ApiScopePolicy> policies)
        {
            for (var i = 0; i < policies.Count; i++)
            {
                var policy = policies[i];
                builder.Append(" @scope(kind: ");
                AppendQuotedGraphQlString(builder, policy.ScopeKind);
                builder.Append(", cardinality: ");
                AppendQuotedGraphQlString(builder, ToCamelCase(policy.Cardinality.ToString()));
                builder.Append(", binding: ");
                AppendQuotedGraphQlString(builder, ToCamelCase(policy.Binding.ToString()));
                builder.Append(", access: ");
                AppendQuotedGraphQlString(builder, ToCamelCase(policy.Access.ToString()));
                AppendOptionalScopeDirectiveArgument(builder, "singleScopeParameterName", policy.SingleScopeParameterName);
                AppendOptionalScopeDirectiveArgument(builder, "multipleScopesParameterName", policy.MultipleScopesParameterName);
                AppendOptionalScopeDirectiveArgument(builder, "scopeModeParameterName", policy.ScopeModeParameterName);
                AppendOptionalScopeDirectiveArgument(builder, "resourceParameterName", policy.ResourceParameterName);
                if (policy.ResourceDerivation is { } resourceDerivation)
                {
                    AppendOptionalScopeDirectiveArgument(builder, "resourceDerivationStrategy", resourceDerivation.Strategy);
                    AppendOptionalScopeDirectiveArgument(builder, "resourceDerivationFormat", resourceDerivation.Format);
                    AppendOptionalScopeDirectiveArgument(builder, "resourceDerivationScopeField", resourceDerivation.ScopeField);
                }
                builder.Append(", allowDefaultScope: ");
                builder.Append(policy.AllowDefaultScope ? "true" : "false");
                builder.Append(')');
            }
        }

        static void AppendOptionalScopeDirectiveArgument(StringBuilder builder, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            builder.Append(", ");
            builder.Append(name);
            builder.Append(": ");
            AppendQuotedGraphQlString(builder, value);
        }

        static void WriteArguments(StringBuilder builder, IReadOnlyList<GraphQArgument> arguments)
        {
            if (arguments.Count == 0)
                return;

            builder.Append('(');
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                var argument = arguments[i];
                builder.Append(argument.Name);
                builder.Append(": ");
                builder.Append(argument.Type.ToSdl());
            }

            builder.Append(')');
        }

        static void WriteTypeDefinition(StringBuilder builder, GraphQLTypeDefinition type)
        {
            WriteDescription(builder, string.Empty, type.Description);

            switch (type.Kind)
            {
                case GraphQLNamedTypeKind.Object:
                    builder.Append("type ");
                    builder.Append(type.Name);
                    builder.AppendLine(" {");
                    for (var i = 0; i < type.Fields.Count; i++)
                    {
                        var field = type.Fields[i];
                        WriteDescription(builder, "  ", field.Description);
                        builder.Append("  ");
                        builder.Append(field.Name);
                        builder.Append(": ");
                        builder.AppendLine(field.Type.ToSdl());
                    }

                    builder.AppendLine("}");
                    builder.AppendLine();
                    break;

                case GraphQLNamedTypeKind.InputObject:
                    builder.Append("input ");
                    builder.Append(type.Name);
                    builder.AppendLine(" {");
                    for (var i = 0; i < type.InputFields.Count; i++)
                    {
                        var field = type.InputFields[i];
                        WriteDescription(builder, "  ", field.Description);
                        builder.Append("  ");
                        builder.Append(field.Name);
                        builder.Append(": ");
                        builder.AppendLine(field.Type.ToSdl());
                    }

                    builder.AppendLine("}");
                    builder.AppendLine();
                    break;

                case GraphQLNamedTypeKind.Enum:
                    builder.Append("enum ");
                    builder.Append(type.Name);
                    builder.AppendLine(" {");
                    for (var i = 0; i < type.EnumValues.Count; i++)
                    {
                        builder.Append("  ");
                        builder.AppendLine(ResolveGraphQlName(type.EnumValues[i], pascalCase: true));
                    }

                    builder.AppendLine("}");
                    builder.AppendLine();
                    break;

                case GraphQLNamedTypeKind.Union:
                    builder.Append("union ");
                    builder.Append(type.Name);
                    builder.Append(" = ");
                    for (var i = 0; i < type.UnionMembers.Count; i++)
                    {
                        if (i > 0)
                            builder.Append(" | ");

                        builder.Append(type.UnionMembers[i]);
                    }

                    builder.AppendLine();
                    builder.AppendLine();
                    break;
            }
        }

        string BuildIntrospectionJson()
        {
            var root = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["__schema"] = BuildSchemaIntrospection()
                }
            };

            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = options.WriteIndented
            }) + "\n";
        }

        JsonObject BuildSchemaIntrospection()
        {
            JsonArray introspectionTypes = [];
            AddScalarType(introspectionTypes, "String");
            AddScalarType(introspectionTypes, "Int");
            AddScalarType(introspectionTypes, "Float");
            AddScalarType(introspectionTypes, "Boolean");
            AddScalarType(introspectionTypes, "ID");
            foreach (var scalar in types.CustomScalars.OrderBy(static value => value, StringComparer.Ordinal))
                AddScalarType(introspectionTypes, scalar);

            introspectionTypes.Add(BuildObjectIntrospection("Query", queryFields));
            if (mutationFields.Count > 0)
                introspectionTypes.Add(BuildObjectIntrospection("Mutation", mutationFields));

            foreach (var type in types.Definitions)
                introspectionTypes.Add(BuildTypeIntrospection(type));

            return new JsonObject
            {
                ["queryType"] = new JsonObject { ["name"] = "Query" },
                ["mutationType"] = mutationFields.Count == 0 ? null : new JsonObject { ["name"] = "Mutation" },
                ["subscriptionType"] = null,
                ["types"] = introspectionTypes,
                ["directives"] = BuildDirectiveIntrospection()
            };
        }

        static void AddScalarType(JsonArray types, string name)
        {
            types.Add(new JsonObject
            {
                ["kind"] = "SCALAR",
                ["name"] = name,
                ["description"] = null,
                ["fields"] = null,
                ["inputFields"] = null,
                ["interfaces"] = null,
                ["enumValues"] = null,
                ["possibleTypes"] = null,
                ["specifiedByURL"] = null
            });
        }

        static JsonObject BuildObjectIntrospection(string name, IReadOnlyList<GraphQLRootField> fields)
        {
            JsonArray fieldNodes = [];
            if (fields.Count == 0)
            {
                fieldNodes.Add(new JsonObject
                {
                    ["name"] = "_empty",
                    ["description"] = null,
                    ["args"] = new JsonArray(),
                    ["type"] = GraphQLTypeRef.Named("Boolean", GraphQLNamedTypeKind.Scalar).ToIntrospection(),
                    ["isDeprecated"] = false,
                    ["deprecationReason"] = null
                });
            }

            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                fieldNodes.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["description"] = field.Description,
                    ["args"] = BuildInputValueIntrospection(field.Arguments),
                    ["type"] = field.Type.ToIntrospection(),
                    ["isDeprecated"] = false,
                    ["deprecationReason"] = null
                });
            }

            return new JsonObject
            {
                ["kind"] = "OBJECT",
                ["name"] = name,
                ["description"] = null,
                ["fields"] = fieldNodes,
                ["inputFields"] = null,
                ["interfaces"] = new JsonArray(),
                ["enumValues"] = null,
                ["possibleTypes"] = null
            };
        }

        static JsonObject BuildTypeIntrospection(GraphQLTypeDefinition type)
        {
            return type.Kind switch
            {
                GraphQLNamedTypeKind.Object => new JsonObject
                {
                    ["kind"] = "OBJECT",
                    ["name"] = type.Name,
                    ["description"] = type.Description,
                    ["fields"] = BuildObjectFieldIntrospection(type.Fields),
                    ["inputFields"] = null,
                    ["interfaces"] = new JsonArray(),
                    ["enumValues"] = null,
                    ["possibleTypes"] = null
                },
                GraphQLNamedTypeKind.InputObject => new JsonObject
                {
                    ["kind"] = "INPUT_OBJECT",
                    ["name"] = type.Name,
                    ["description"] = type.Description,
                    ["fields"] = null,
                    ["inputFields"] = BuildInputValueIntrospection(type.InputFields),
                    ["interfaces"] = null,
                    ["enumValues"] = null,
                    ["possibleTypes"] = null
                },
                GraphQLNamedTypeKind.Enum => new JsonObject
                {
                    ["kind"] = "ENUM",
                    ["name"] = type.Name,
                    ["description"] = type.Description,
                    ["fields"] = null,
                    ["inputFields"] = null,
                    ["interfaces"] = null,
                    ["enumValues"] = new JsonArray(type.EnumValues
                        .Select(static value => new JsonObject
                        {
                            ["name"] = ResolveGraphQlName(value, pascalCase: true),
                            ["description"] = null,
                            ["isDeprecated"] = false,
                            ["deprecationReason"] = null
                        })
                        .ToArray<JsonNode?>()),
                    ["possibleTypes"] = null
                },
                GraphQLNamedTypeKind.Union => new JsonObject
                {
                    ["kind"] = "UNION",
                    ["name"] = type.Name,
                    ["description"] = type.Description,
                    ["fields"] = null,
                    ["inputFields"] = null,
                    ["interfaces"] = null,
                    ["enumValues"] = null,
                    ["possibleTypes"] = new JsonArray(type.UnionMembers
                        .Select(static value => new JsonObject
                        {
                            ["kind"] = "OBJECT",
                            ["name"] = value,
                            ["ofType"] = null
                        })
                        .ToArray<JsonNode?>())
                },
                _ => throw new InvalidOperationException($"Unsupported GraphQL type kind '{type.Kind}'.")
            };
        }

        static JsonArray BuildObjectFieldIntrospection(IReadOnlyList<GraphQLObjectField> fields)
        {
            JsonArray nodes = [];
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                nodes.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["description"] = field.Description,
                    ["args"] = new JsonArray(),
                    ["type"] = field.Type.ToIntrospection(),
                    ["isDeprecated"] = false,
                    ["deprecationReason"] = null
                });
            }

            return nodes;
        }

        static JsonArray BuildInputValueIntrospection(IReadOnlyList<GraphQLInputValue> fields)
        {
            JsonArray nodes = [];
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                nodes.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["description"] = field.Description,
                    ["type"] = field.Type.ToIntrospection(),
                    ["defaultValue"] = null
                });
            }

            return nodes;
        }

        JsonArray BuildDirectiveIntrospection()
        {
            JsonArray directives =
            [
                new JsonObject
                {
                    ["name"] = "include",
                    ["description"] = "Directs the executor to include this field or fragment only when the `if` argument is true.",
                    ["locations"] = new JsonArray(
                        JsonValue.Create("FIELD"),
                        JsonValue.Create("FRAGMENT_SPREAD"),
                        JsonValue.Create("INLINE_FRAGMENT")
                        ),
                    ["args"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "if",
                        ["description"] = null,
                        ["type"] = GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("Boolean", GraphQLNamedTypeKind.Scalar))
                            .ToIntrospection(),
                        ["defaultValue"] = null
                    }),
                    ["isRepeatable"] = false
                },

                new JsonObject
                {
                    ["name"] = "skip",
                    ["description"] = "Directs the executor to skip this field or fragment when the `if` argument is true.",
                    ["locations"] = new JsonArray(
                        JsonValue.Create("FIELD"),
                        JsonValue.Create("FRAGMENT_SPREAD"),
                        JsonValue.Create("INLINE_FRAGMENT")
                        ),
                    ["args"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "if",
                        ["description"] = null,
                        ["type"] = GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("Boolean", GraphQLNamedTypeKind.Scalar)).ToIntrospection(),
                        ["defaultValue"] = null
                    }),
                    ["isRepeatable"] = false
                }

            ];

            if (options.IncludeCohesiveDirectives)
            {
                directives.Add(new JsonObject
                {
                    ["name"] = "cohesiveOperation",
                    ["description"] = "Binds a projected GraphQL field back to a Cohesive API operation.",
                    ["locations"] = new JsonArray(JsonValue.Create("FIELD_DEFINITION")),
                    ["args"] = new JsonArray(
                        BuildDirectiveArg("id", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("method", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("route", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("kind", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("entity", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))
                        ),
                    ["isRepeatable"] = false
                });

                directives.Add(new JsonObject
                {
                    ["name"] = "scope",
                    ["description"] = "Describes the semantic scope policy for a Cohesive API operation.",
                    ["locations"] = new JsonArray(JsonValue.Create("FIELD_DEFINITION")),
                    ["args"] = new JsonArray(
                        BuildDirectiveArg("kind", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("cardinality", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("binding", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("access", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar))),
                        BuildDirectiveArg("singleScopeParameterName", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("multipleScopesParameterName", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("scopeModeParameterName", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("resourceParameterName", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("resourceDerivationStrategy", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("resourceDerivationFormat", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("resourceDerivationScopeField", GraphQLTypeRef.Named("String", GraphQLNamedTypeKind.Scalar)),
                        BuildDirectiveArg("allowDefaultScope", GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("Boolean", GraphQLNamedTypeKind.Scalar)))
                        ),
                    ["isRepeatable"] = true
                });
            }

            return directives;
        }

        static JsonObject BuildDirectiveArg(string name, GraphQLTypeRef type) => new()
        {
            ["name"] = name,
            ["description"] = null,
            ["type"] = type.ToIntrospection(),
            ["defaultValue"] = null
        };

        static GraphQLRootKind SelectRoot(ApiOperation operation)
        {
            if (operation.Kind == ApiOperationKind.Query)
                return GraphQLRootKind.Query;
            if (operation.Kind == ApiOperationKind.Command)
                return GraphQLRootKind.Mutation;

            return string.Equals(operation.Http.Method, "GET", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(operation.Http.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
                ? GraphQLRootKind.Query
                : GraphQLRootKind.Mutation;
        }

        static string CreateUniqueRootFieldName(ApiOperation operation, HashSet<string> usedNames)
        {
            var candidate = CreateRootFieldName(operation);
            if (usedNames.Add(candidate))
                return candidate;

            candidate = CreateFieldNameFromEndpointId(operation.Id.Value);
            if (usedNames.Add(candidate))
                return candidate;

            var baseName = candidate;
            var suffix = 2;
            while (!usedNames.Add(candidate))
            {
                candidate = $"{baseName}{suffix}";
                suffix++;
            }

            return candidate;
        }

        static string CreateRootFieldName(ApiOperation operation)
        {
            if (operation.Entity is not { } entity)
                return ResolveGraphQlName(operation.Name);

            var entityName = StripEntitySuffix(entity.Value);
            var operationName = operation.Name;
            if (string.Equals(operationName, "Query", StringComparison.OrdinalIgnoreCase))
                return ResolveGraphQlName($"Query{Pluralize(entityName)}");
            if (string.Equals(operationName, "ValidateSample", StringComparison.OrdinalIgnoreCase))
                return ResolveGraphQlName($"Validate{entityName}Sample");
            if (IsEntityOperationName(operationName))
                return ResolveGraphQlName($"{operationName}{entityName}");
            if (ContainsWord(operationName, entityName))
                return ResolveGraphQlName(operationName);
            if (operationName.StartsWith("Compile", StringComparison.Ordinal) && operationName.Length > "Compile".Length)
                return ResolveGraphQlName($"Compile{entityName}To{operationName["Compile".Length..]}");

            return ResolveGraphQlName($"{operationName}{entityName}");
        }

        static bool IsEntityOperationName(string value) =>
            string.Equals(value, "Get", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Create", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Revise", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Archive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Validate", StringComparison.OrdinalIgnoreCase);

        static string CreateFieldNameFromEndpointId(string endpointId)
        {
            var segments = endpointId.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
                return "operation";

            var builder = new StringBuilder();
            for (var i = Math.Max(0, segments.Length - 2); i < segments.Length; i++)
                builder.Append(StripEntitySuffix(segments[i]));

            return ResolveGraphQlName(builder.ToString());
        }

        static bool ContainsWord(string value, string word) =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase);

        static string StripEntitySuffix(string value)
        {
            foreach (var suffix in new[] { "Resource", "Dto", "Entity", "Record" })
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
                    return value[..^suffix.Length];
            }

            return value;
        }

        static string Pluralize(string value)
        {
            if (value.EndsWith("y", StringComparison.OrdinalIgnoreCase) && value.Length > 1 && !IsVowel(value[^2]))
                return $"{value[..^1]}ies";
            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("z", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
                return $"{value}es";

            return $"{value}s";
        }

        static bool IsVowel(char value) => char.ToLowerInvariant(value) is 'a' or 'e' or 'i' or 'o' or 'u';
    }

    sealed class GraphQlTypeRegistry
    {
        readonly Dictionary<Type, string> outputNameByType = new();
        readonly Dictionary<Type, string> inputNameByType = new();
        readonly Dictionary<Type, string> enumNameByType = new();
        readonly Dictionary<ApiEndpointId, string> operationResultNameByEndpoint = new();
        readonly HashSet<string> usedNames = new(StringComparer.Ordinal);
        readonly List<GraphQLTypeDefinition> definitions = [];

        public IReadOnlyList<GraphQLTypeDefinition> Definitions => definitions;

        public HashSet<string> CustomScalars { get; } = new(StringComparer.Ordinal);

        public GraphQLTypeRef OutputTypeRef(Type type, bool required) => TypeRef(type, required, input: false);

        public GraphQLTypeRef InputTypeRef(Type type, bool required) => TypeRef(type, required, input: true);

        public GraphQLTypeRef OperationResultTypeRef(ApiOperation operation, bool required) =>
            ApplyRequired(GraphQLTypeRef.Named(GetOrAddOperationResultUnion(operation), GraphQLNamedTypeKind.Union), required);

        GraphQLTypeRef TypeRef(Type type, bool required, bool input)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return TypeRef(nullable, required: false, input);

            if (TryGetScalarName(type, out var scalarName))
                return ApplyRequired(GraphQLTypeRef.Named(scalarName, GraphQLNamedTypeKind.Scalar), required);

            if (type.IsArray)
                return ApplyRequired(GraphQLTypeRef.List(TypeRef(type.GetElementType()!, required: true, input)), required);

            if (TryGetSequenceElementType(type, out var elementType))
                return ApplyRequired(GraphQLTypeRef.List(TypeRef(elementType, required: true, input)), required);

            if (IsDictionary(type))
            {
                CustomScalars.Add("JSON");
                return ApplyRequired(GraphQLTypeRef.Named("JSON", GraphQLNamedTypeKind.Scalar), required);
            }

            if (type.IsEnum)
                return ApplyRequired(GraphQLTypeRef.Named(GetOrAddEnum(type), GraphQLNamedTypeKind.Enum), required);

            if (input)
                return ApplyRequired(GraphQLTypeRef.Named(GetOrAddInputObject(type), GraphQLNamedTypeKind.InputObject), required);

            return ApplyRequired(GraphQLTypeRef.Named(GetOrAddOutputObject(type), GraphQLNamedTypeKind.Object), required);
        }

        static GraphQLTypeRef ApplyRequired(GraphQLTypeRef type, bool required) =>
            required && type.Kind != GraphQLTypeRefKind.NonNull ? GraphQLTypeRef.NonNull(type) : type;

        bool TryGetScalarName(Type type, out string name)
        {
            if (type == typeof(void))
            {
                CustomScalars.Add("Void");
                name = "Void";
                return true;
            }

            if (type == typeof(string) || type == typeof(char))
            {
                name = "String";
                return true;
            }

            if (type == typeof(Guid))
            {
                name = "ID";
                return true;
            }

            if (type == typeof(DateOnly)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeOnly))
            {
                name = "String";
                return true;
            }

            if (type == typeof(bool))
            {
                name = "Boolean";
                return true;
            }

            if (IsInteger(type))
            {
                name = "Int";
                return true;
            }

            if (IsNumber(type))
            {
                name = "Float";
                return true;
            }

            if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(JsonNode))
            {
                CustomScalars.Add("JSON");
                name = "JSON";
                return true;
            }

            name = string.Empty;
            return false;
        }

        string GetOrAddOutputObject(Type type)
        {
            if (outputNameByType.TryGetValue(type, out var existing))
                return existing;

            var name = CreateUniqueName(CreateTypeName(type));
            outputNameByType[type] = name;
            var definition = new GraphQLTypeDefinition(name, GraphQLNamedTypeKind.Object);
            definitions.Add(definition);

            var properties = ShapeTypeInspector.GetReadablePropertyMetadata(type);
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                definition.Fields.Add(new GraphQLObjectField(
                    Name: ResolvePropertyGraphQlName(property.Property),
                    Type: OutputTypeRef(property.Property.PropertyType, required: !property.IsOptional),
                    Description: null));
            }

            return name;
        }

        string GetOrAddInputObject(Type type)
        {
            if (inputNameByType.TryGetValue(type, out var existing))
                return existing;

            var name = CreateUniqueName($"{CreateTypeName(type)}Input");
            inputNameByType[type] = name;
            var definition = new GraphQLTypeDefinition(name, GraphQLNamedTypeKind.InputObject);
            definitions.Add(definition);

            var properties = ShapeTypeInspector.GetReadablePropertyMetadata(type);
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                definition.InputFields.Add(new GraphQLInputValue(
                    Name: ResolvePropertyGraphQlName(property.Property),
                    Type: InputTypeRef(property.Property.PropertyType, required: !property.IsOptional),
                    Description: null));
            }

            return name;
        }

        string GetOrAddEnum(Type type)
        {
            if (enumNameByType.TryGetValue(type, out var existing))
                return existing;

            var name = CreateUniqueName(CreateTypeName(type));
            enumNameByType[type] = name;
            var definition = new GraphQLTypeDefinition(name, GraphQLNamedTypeKind.Enum);
            definitions.Add(definition);
            definition.EnumValues.AddRange(Enum.GetNames(type));
            return name;
        }

        string GetOrAddOperationResultUnion(ApiOperation operation)
        {
            if (operationResultNameByEndpoint.TryGetValue(operation.Id, out var existing))
                return existing;

            var baseName = CreateOperationResultTypeName(operation);
            var unionName = CreateUniqueName($"{baseName}Result");
            operationResultNameByEndpoint[operation.Id] = unionName;

            var memberNames = new List<string>(operation.Results.Count);
            for (var i = 0; i < operation.Results.Count; i++)
            {
                var result = operation.Results[i];
                var memberName = CreateUniqueName($"{baseName}{ToPascalCase(result.Id)}Result");
                var definition = new GraphQLTypeDefinition(memberName, GraphQLNamedTypeKind.Object)
                {
                    Description = result.Description
                };
                definitions.Add(definition);

                if (result.BodyType == typeof(void))
                {
                    definition.Fields.Add(new GraphQLObjectField(
                        Name: "ok",
                        Type: GraphQLTypeRef.NonNull(GraphQLTypeRef.Named("Boolean", GraphQLNamedTypeKind.Scalar)),
                        Description: null));
                }
                else
                {
                    definition.Fields.Add(new GraphQLObjectField(
                        Name: "body",
                        Type: OutputTypeRef(result.BodyType, required: true),
                        Description: null));
                }

                memberNames.Add(memberName);
            }

            var union = new GraphQLTypeDefinition(unionName, GraphQLNamedTypeKind.Union);
            union.UnionMembers.AddRange(memberNames);
            definitions.Add(union);
            return unionName;
        }

        string CreateUniqueName(string preferredName)
        {
            var name = SanitizeGraphQlName(preferredName, pascalCase: true);
            if (usedNames.Add(name))
                return name;

            var baseName = name;
            var suffix = 2;
            while (!usedNames.Add(name))
            {
                name = $"{baseName}{suffix}";
                suffix++;
            }

            return name;
        }

        static string CreateTypeName(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return CreateTypeName(nullable);

            if (!type.IsGenericType)
                return type.Name;

            var typeName = type.Name;
            var tick = typeName.IndexOf('`');
            if (tick >= 0)
                typeName = typeName[..tick];

            var builder = new StringBuilder(typeName);
            builder.Append("Of");
            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append("And");

                builder.Append(CreateTypeName(arguments[i]));
            }

            return builder.ToString();
        }

        static string CreateOperationResultTypeName(ApiOperation operation)
        {
            if (operation.Entity is { } entity)
                return $"{StripResultTypeSuffix(entity.Value)}{operation.Name}";

            return operation.Name;
        }

        static string StripResultTypeSuffix(string value)
        {
            foreach (var suffix in new[] { "Resource", "Dto", "Entity", "Record" })
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
                    return value[..^suffix.Length];
            }

            return value;
        }

        static string ToPascalCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Result";

            var builder = new StringBuilder(value.Length);
            var makeUpper = true;
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (!char.IsLetterOrDigit(current))
                {
                    makeUpper = true;
                    continue;
                }

                builder.Append(makeUpper ? char.ToUpperInvariant(current) : current);
                makeUpper = false;
            }

            return builder.Length == 0 ? "Result" : builder.ToString();
        }
    }

    sealed record GraphQLRootField(
        string Name,
        string? Description,
        IReadOnlyList<GraphQArgument> Arguments,
        GraphQLTypeRef Type,
        ApiOperation Operation);

    sealed record GraphQArgument(string Name, GraphQLTypeRef Type, string? Description) : GraphQLInputValue(Name, Type, Description);

    record GraphQLInputValue(string Name, GraphQLTypeRef Type, string? Description);

    sealed record GraphQLObjectField(string Name, GraphQLTypeRef Type, string? Description);

    sealed class GraphQLTypeDefinition(string name, GraphQLNamedTypeKind kind)
    {
        public string Name { get; } = name;

        public GraphQLNamedTypeKind Kind { get; } = kind;

        public string? Description { get; init; }

        public List<GraphQLObjectField> Fields { get; } = [];

        public List<GraphQLInputValue> InputFields { get; } = [];

        public List<string> EnumValues { get; } = [];

        public List<string> UnionMembers { get; } = [];
    }

    sealed record GraphQLTypeRef(GraphQLTypeRefKind Kind, string? Name, GraphQLNamedTypeKind? NamedKind, GraphQLTypeRef? OfType)
    {
        public static GraphQLTypeRef Named(string name, GraphQLNamedTypeKind kind) => new(GraphQLTypeRefKind.Named, name, kind, null);

        public static GraphQLTypeRef List(GraphQLTypeRef ofType) => new(GraphQLTypeRefKind.List, null, null, ofType);

        public static GraphQLTypeRef NonNull(GraphQLTypeRef ofType) => new(GraphQLTypeRefKind.NonNull, null, null, ofType);

        public string ToSdl() => Kind switch
        {
            GraphQLTypeRefKind.Named => Name ?? throw new InvalidOperationException("Named GraphQL type reference has no name."),
            GraphQLTypeRefKind.List => $"[{OfType?.ToSdl() ?? throw new InvalidOperationException("List GraphQL type reference has no item type.")}]",
            GraphQLTypeRefKind.NonNull => $"{OfType?.ToSdl() ?? throw new InvalidOperationException("Non-null GraphQL type reference has no wrapped type.")}!",
            _ => throw new InvalidOperationException($"Unsupported GraphQL type reference kind '{Kind}'.")
        };

        public JsonObject ToIntrospection() => Kind switch
        {
            GraphQLTypeRefKind.Named => new JsonObject
            {
                ["kind"] = NamedKindToIntrospectionKind(NamedKind ?? throw new InvalidOperationException("Named GraphQL type reference has no kind.")),
                ["name"] = Name,
                ["ofType"] = null
            },
            GraphQLTypeRefKind.List => new JsonObject
            {
                ["kind"] = "LIST",
                ["name"] = null,
                ["ofType"] = OfType?.ToIntrospection() ?? throw new InvalidOperationException("List GraphQL type reference has no item type.")
            },
            GraphQLTypeRefKind.NonNull => new JsonObject
            {
                ["kind"] = "NON_NULL",
                ["name"] = null,
                ["ofType"] = OfType?.ToIntrospection() ?? throw new InvalidOperationException("Non-null GraphQL type reference has no wrapped type.")
            },
            _ => throw new InvalidOperationException($"Unsupported GraphQL type reference kind '{Kind}'.")
        };
    }

    enum GraphQLRootKind
    {
        Query,
        Mutation
    }

    enum GraphQLTypeRefKind
    {
        Named,
        List,
        NonNull
    }

    enum GraphQLNamedTypeKind
    {
        Scalar,
        Object,
        InputObject,
        Enum,
        Union
    }

    static string NamedKindToIntrospectionKind(GraphQLNamedTypeKind kind) => kind switch
    {
        GraphQLNamedTypeKind.Scalar => "SCALAR",
        GraphQLNamedTypeKind.Object => "OBJECT",
        GraphQLNamedTypeKind.InputObject => "INPUT_OBJECT",
        GraphQLNamedTypeKind.Enum => "ENUM",
        GraphQLNamedTypeKind.Union => "UNION",
        _ => throw new InvalidOperationException($"Unsupported GraphQL named type kind '{kind}'.")
    };

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

    static bool IsDictionary(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Dictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>))
            {
                var arguments = type.GetGenericArguments();
                if (arguments[0] == typeof(string))
                    return true;
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

            if (candidate.GetGenericArguments()[0] == typeof(string))
                return true;
        }

        return false;
    }

    static bool IsInteger(Type type) =>
        type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int);

    static bool IsNumber(Type type) =>
        type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal);

    static string ResolvePropertyGraphQlName(PropertyInfo property)
    {
        var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name;
        return string.IsNullOrWhiteSpace(jsonName)
            ? ResolveGraphQlName(property.Name)
            : ResolveGraphQlName(jsonName, preserveCase: true);
    }

    static string ResolveGraphQlName(string value, bool pascalCase = false, bool preserveCase = false) =>
        SanitizeGraphQlName(value, pascalCase, preserveCase);

    static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length == 1
            ? char.ToLowerInvariant(value[0]).ToString()
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    static string SanitizeGraphQlName(string value, bool pascalCase = false, bool preserveCase = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return pascalCase ? "Value" : "value";

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsLetterOrDigit(current) || current == '_')
                builder.Append(current);
            else if (builder.Length == 0 || builder[^1] != '_')
                builder.Append('_');
        }

        if (builder.Length == 0)
            builder.Append(pascalCase ? "Value" : "value");

        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');

        if (!preserveCase)
        {
            builder[0] = pascalCase
                ? char.ToUpperInvariant(builder[0])
                : char.ToLowerInvariant(builder[0]);
        }

        return builder.ToString();
    }

    static void WriteDescription(StringBuilder builder, string indent, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;

        builder.Append(indent);
        builder.Append("\"\"\"");
        builder.Append(description.Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal));
        builder.AppendLine("\"\"\"");
    }

    static void AppendQuotedGraphQlString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            switch (current)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(current);
                    break;
            }
        }

        builder.Append('"');
    }
}
