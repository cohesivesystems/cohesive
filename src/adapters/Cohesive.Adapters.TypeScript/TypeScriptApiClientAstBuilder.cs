using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Adapters.TypeScript.Ast;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Adapters.TypeScript;

/// <summary>
/// Builds a TypeScript AST for API client functions.
/// </summary>
public sealed class TypeScriptApiClientAstBuilder
{
    readonly ApiDefinition definition;
    readonly TypeScriptApiClientEmitterOptions options;

    /// <summary>
    /// Creates the AST builder.
    /// </summary>
    public TypeScriptApiClientAstBuilder(ApiDefinition definition, TypeScriptApiClientEmitterOptions options)
    {
        this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Builds the document.
    /// </summary>
    public TsDocument Build()
    {
        var statements = ImmutableArray.CreateBuilder<TsStatement>();
        var metadataNames = BuildMetadataNames();

        var imports = BuildTypeImports();
        if (imports.Length > 0)
        {
            statements.Add(new TsImportDeclaration(
                from: options.ShapesImportPath,
                namedImports: imports,
                isTypeOnly: true));
        }

        statements.Add(new TsTypeAliasDeclaration(
            name: options.HttpClientTypeName,
            type: new TsFunctionType(
                parameters:
                [
                    new TsParameterDeclaration("path", new TsKeywordType(TsKeyword.String)),
                    new TsParameterDeclaration("init", new TsRawType("RequestInit"))
                ],
                returnType: new TsRawType("Promise<unknown>"))));

        AppendMetadataDeclarations(statements, metadataNames);

        for (var i = 0; i < definition.Operations.Count; i++)
        {
            var operation = definition.Operations[i];
            if (operation.Http is not null)
                statements.Add(BuildFunction(operation));
        }

        return new TsDocument(statements.ToImmutable());
    }

    ImmutableArray<TsImportSpecifier> BuildTypeImports()
    {
        var names = new List<string>();

        for (var i = 0; i < definition.Operations.Count; i++)
        {
            var operation = definition.Operations[i];
            if (operation.Http is not { } http)
                continue;

            AppendTypeImports(names, operation.ResponseType);

            if (http.Body is not null)
                AppendTypeImports(names, http.Body.BodyType);

            if (http.Query is not null)
                AppendTypeImports(names, http.Query.QueryType);

            for (var parameterIndex = 0; parameterIndex < http.Parameters.Count; parameterIndex++)
                AppendTypeImports(names, http.Parameters[parameterIndex].Type);
        }

        names.Sort(StringComparer.Ordinal);

        var imports = ImmutableArray.CreateBuilder<TsImportSpecifier>(names.Count);
        for (var i = 0; i < names.Count; i++)
            imports.Add(new TsImportSpecifier(names[i]));

        return imports.ToImmutable();
    }

    void AppendTypeImports(List<string> names, Type type)
    {
        var elementType = UnwrapType(type);
        if (IsBuiltInType(elementType))
            return;

        var name = GetTypeName(elementType);
        if (!ContainsName(names, name))
            names.Add(name);
    }

    void AppendMetadataDeclarations(
        ImmutableArray<TsStatement>.Builder statements,
        in TypeScriptApiClientMetadataNames names)
    {
        var operationKeyMembers = ImmutableArray.CreateBuilder<TsTypeNode>(definition.Operations.Count);
        var operationIdProperties = ImmutableArray.CreateBuilder<TsObjectProperty>(definition.Operations.Count);
        var endpointKeyMembers = ImmutableArray.CreateBuilder<TsTypeNode>(definition.Operations.Count);
        var endpointIdProperties = ImmutableArray.CreateBuilder<TsObjectProperty>(definition.Operations.Count);
        var operationMetadataProperties = ImmutableArray.CreateBuilder<TsObjectProperty>(definition.Operations.Count);
        var scopePolicyMembers = ImmutableArray.CreateBuilder<TsPropertySignature>(definition.Operations.Count);
        var scopePolicyProperties = ImmutableArray.CreateBuilder<TsObjectProperty>(definition.Operations.Count);

        for (var i = 0; i < definition.Operations.Count; i++)
        {
            var operation = definition.Operations[i];
            var operationKey = BuildFunctionName(operation);
            operationKeyMembers.Add(new TsLiteralType(operationKey));
            operationIdProperties.Add(new TsObjectProperty(
                operationKey,
                new TsStringLiteralExpression(operation.Id.Value)));
            operationMetadataProperties.Add(new TsObjectProperty(
                operationKey,
                BuildOperationMetadataExpression(operation)));
            scopePolicyMembers.Add(new TsPropertySignature(
                operationKey,
                new TsRawType($"readonly {names.ScopePolicyMetadataName}[]"),
                isReadonly: true));
            scopePolicyProperties.Add(new TsObjectProperty(
                operationKey,
                BuildScopePoliciesExpression(operation.ScopePolicies)));

            if (operation.Http is not null)
            {
                endpointKeyMembers.Add(new TsLiteralType(operationKey));
                endpointIdProperties.Add(new TsObjectProperty(
                    operationKey,
                    new TsStringLiteralExpression(operation.Id.Value)));
            }
        }

        statements.Add(new TsTypeAliasDeclaration(
            name: names.OperationKeyTypeName,
            type: operationKeyMembers.Count == 0
                ? new TsKeywordType(TsKeyword.Never)
                : new TsUnionType(operationKeyMembers.ToImmutable())));

        statements.Add(new TsConstDeclaration(
            name: names.OperationIdsConstName,
            initializer: new TsObjectLiteralExpression(operationIdProperties.ToImmutable()),
            satisfiesType: new TsRawType($"Record<{names.OperationKeyTypeName}, string>"),
            asConst: true));

        statements.Add(new TsTypeAliasDeclaration(
            name: names.EndpointKeyTypeName,
            type: endpointKeyMembers.Count == 0
                ? new TsKeywordType(TsKeyword.Never)
                : new TsUnionType(endpointKeyMembers.ToImmutable())));

        statements.Add(new TsConstDeclaration(
            name: names.EndpointIdsConstName,
            initializer: new TsObjectLiteralExpression(endpointIdProperties.ToImmutable()),
            satisfiesType: new TsRawType($"Record<{names.EndpointKeyTypeName}, string>"),
            asConst: true));

        statements.Add(new TsConstDeclaration(
            name: names.OperationMetadataConstName,
            initializer: new TsObjectLiteralExpression(operationMetadataProperties.ToImmutable()),
            satisfiesType: new TsRawType($"Record<{names.OperationKeyTypeName}, unknown>"),
            asConst: true));

        statements.Add(new TsInterfaceDeclaration(
            name: names.ScopePolicyMetadataName,
            members:
            [
                new TsPropertySignature("kind", new TsKeywordType(TsKeyword.String), isReadonly: true),
                new TsPropertySignature("cardinality", CreateStringUnionType("single", "multiple"), isReadonly: true),
                new TsPropertySignature("binding", CreateStringUnionType("ambient", "header", "query", "route", "body", "resource"), isReadonly: true),
                new TsPropertySignature("access", CreateStringUnionType("requireSelected", "filterToAccessible", "validateAccessible"), isReadonly: true),
                new TsPropertySignature("singleScopeParameterName", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
                new TsPropertySignature("multipleScopesParameterName", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
                new TsPropertySignature("scopeModeParameterName", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
                new TsPropertySignature("resourceParameterName", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
                new TsPropertySignature("resourceDerivation", BuildResourceScopeDerivationType(), isOptional: true, isReadonly: true),
                new TsPropertySignature("allowDefaultScope", new TsKeywordType(TsKeyword.Boolean), isReadonly: true),
            ]));

        statements.Add(new TsInterfaceDeclaration(
            name: names.ScopePolicyByOperationName,
            members: scopePolicyMembers.ToImmutable()));

        statements.Add(new TsTypeAliasDeclaration(
            name: names.ScopePolicyByEndpointName,
            type: new TsRawType(
                $"Pick<{names.ScopePolicyByOperationName}, {names.EndpointKeyTypeName}>")));

        statements.Add(new TsConstDeclaration(
            name: names.ScopePoliciesConstName,
            initializer: new TsObjectLiteralExpression(scopePolicyProperties.ToImmutable()),
            satisfiesType: new TsTypeReference(names.ScopePolicyByOperationName),
            asConst: true));
    }

    static TsExpression BuildOperationMetadataExpression(ApiOperation operation)
    {
        ImmutableArray<TsObjectProperty> properties =
        [
            new("id", new TsStringLiteralExpression(operation.Id.Value)),
            new("kind", new TsStringLiteralExpression(ApiWireNames.OperationKind(operation.Kind))),
            new("requestContract", new TsStringLiteralExpression(GetTypeScriptTypeText(operation.RequestType))),
            new("authorizationRequirementIds", BuildAuthorizationRequirementIdsExpression(operation.AuthorizationRequirements)),
            new("results", BuildResultsExpression(operation.Results)),
            new("semanticReferences", BuildSemanticReferencesExpression(operation.SemanticReferences)),
            new("http", BuildHttpBindingExpression(operation.Http))
        ];
        return new TsObjectLiteralExpression(properties);
    }

    static TsExpression BuildAuthorizationRequirementIdsExpression(
        IReadOnlyList<ApiAuthorizationRequirement> requirements)
    {
        var expressions = ImmutableArray.CreateBuilder<TsExpression>(requirements.Count);
        for (var index = 0; index < requirements.Count; index++)
            expressions.Add(new TsStringLiteralExpression(requirements[index].Id));

        return new TsArrayLiteralExpression(expressions.MoveToImmutable());
    }

    static TsExpression BuildResultsExpression(IReadOnlyList<ApiResultDefinition> results)
    {
        var expressions = ImmutableArray.CreateBuilder<TsExpression>(results.Count);
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            ImmutableArray<TsObjectProperty> properties =
            [
                new("id", new TsStringLiteralExpression(result.Id)),
                new("kind", new TsStringLiteralExpression(ApiWireNames.ResultKind(result.Kind))),
                new("bodyContract", new TsStringLiteralExpression(GetTypeScriptTypeText(result.BodyType))),
                new("isPrimary", new TsBooleanLiteralExpression(result.IsPrimary))
            ];
            expressions.Add(new TsObjectLiteralExpression(properties));
        }

        return new TsArrayLiteralExpression(expressions.MoveToImmutable());
    }

    static TsExpression BuildSemanticReferencesExpression(IReadOnlyList<ApiSemanticReference> references)
    {
        var expressions = ImmutableArray.CreateBuilder<TsExpression>(references.Count);
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            ImmutableArray<TsObjectProperty> properties =
            [
                new("authority", new TsStringLiteralExpression(reference.Authority)),
                new("schemaVersion", new TsStringLiteralExpression(reference.SchemaVersion.Value)),
                new("path", new TsStringLiteralExpression(reference.Path.ToString())),
                new("source", BuildSemanticSourceExpression(reference.Source))
            ];
            expressions.Add(new TsObjectLiteralExpression(properties));
        }

        return new TsArrayLiteralExpression(expressions.MoveToImmutable());
    }

    static TsExpression BuildSemanticSourceExpression(ExecutionSourceProvenance? source)
    {
        if (source is null)
            return new TsNullLiteralExpression();

        ImmutableArray<TsObjectProperty> properties =
        [
            new("reference", new TsStringLiteralExpression(source.Reference)),
            new("semanticPath", BuildNullableStringExpression(source.SemanticPath?.ToString())),
            new("description", BuildNullableStringExpression(source.Description))
        ];
        return new TsObjectLiteralExpression(properties);
    }

    static TsExpression BuildHttpBindingExpression(HttpBinding? http)
    {
        if (http is null)
            return new TsNullLiteralExpression();

        ImmutableArray<TsObjectProperty> properties =
        [
            new("method", new TsStringLiteralExpression(http.Method)),
            new("route", new TsStringLiteralExpression(http.Route))
        ];
        return new TsObjectLiteralExpression(properties);
    }

    static TsExpression BuildNullableStringExpression(string? value) =>
        value is null ? new TsNullLiteralExpression() : new TsStringLiteralExpression(value);

    TsExpression BuildScopePoliciesExpression(IReadOnlyList<ApiScopePolicy> policies)
    {
        var expressions = ImmutableArray.CreateBuilder<TsExpression>(policies.Count);
        for (var i = 0; i < policies.Count; i++)
            expressions.Add(BuildScopePolicyExpression(policies[i]));

        return new TsArrayLiteralExpression(expressions.ToImmutable());
    }

    TsExpression BuildScopePolicyExpression(ApiScopePolicy policy)
    {
        var properties = ImmutableArray.CreateBuilder<TsObjectProperty>(10);
        properties.Add(new TsObjectProperty("kind", new TsStringLiteralExpression(policy.ScopeKind)));
        properties.Add(new TsObjectProperty("cardinality", new TsStringLiteralExpression(ToCamelCase(policy.Cardinality.ToString()))));
        properties.Add(new TsObjectProperty("binding", new TsStringLiteralExpression(ToCamelCase(policy.Binding.ToString()))));
        properties.Add(new TsObjectProperty("access", new TsStringLiteralExpression(ToCamelCase(policy.Access.ToString()))));
        AppendOptionalScopePolicyProperty(properties, "singleScopeParameterName", policy.SingleScopeParameterName);
        AppendOptionalScopePolicyProperty(properties, "multipleScopesParameterName", policy.MultipleScopesParameterName);
        AppendOptionalScopePolicyProperty(properties, "scopeModeParameterName", policy.ScopeModeParameterName);
        AppendOptionalScopePolicyProperty(properties, "resourceParameterName", policy.ResourceParameterName);
        if (policy.ResourceDerivation is { } resourceDerivation)
            properties.Add(new TsObjectProperty("resourceDerivation", BuildResourceScopeDerivationExpression(resourceDerivation)));
        properties.Add(new TsObjectProperty("allowDefaultScope", new TsBooleanLiteralExpression(policy.AllowDefaultScope)));
        return new TsObjectLiteralExpression(properties.ToImmutable());
    }

    static TsTypeNode BuildResourceScopeDerivationType() => new TsTypeLiteral(
    [
        new TsPropertySignature("strategy", new TsKeywordType(TsKeyword.String), isReadonly: true),
        new TsPropertySignature("format", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
        new TsPropertySignature("scopeField", new TsKeywordType(TsKeyword.String), isOptional: true, isReadonly: true),
    ]);

    static TsExpression BuildResourceScopeDerivationExpression(ApiResourceScopeDerivation derivation)
    {
        var properties = ImmutableArray.CreateBuilder<TsObjectProperty>(3);
        properties.Add(new TsObjectProperty("strategy", new TsStringLiteralExpression(derivation.Strategy)));
        AppendOptionalScopePolicyProperty(properties, "format", derivation.Format);
        AppendOptionalScopePolicyProperty(properties, "scopeField", derivation.ScopeField);
        return new TsObjectLiteralExpression(properties.ToImmutable());
    }

    static void AppendOptionalScopePolicyProperty(
        ImmutableArray<TsObjectProperty>.Builder properties,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        properties.Add(new TsObjectProperty(name, new TsStringLiteralExpression(value)));
    }

    static TsTypeNode CreateStringUnionType(params string[] values)
    {
        var members = ImmutableArray.CreateBuilder<TsTypeNode>(values.Length);
        for (var i = 0; i < values.Length; i++)
            members.Add(new TsLiteralType(values[i]));

        return new TsUnionType(members.ToImmutable());
    }

    TypeScriptApiClientMetadataNames BuildMetadataNames()
    {
        var modulePrefix = ToPascalCaseIdentifier(options.ModuleName);
        if (string.IsNullOrWhiteSpace(modulePrefix))
        {
            return new TypeScriptApiClientMetadataNames(
                OperationIdsConstName: "apiOperationIds",
                OperationKeyTypeName: "ApiOperationKey",
                EndpointIdsConstName: "apiEndpointIds",
                EndpointKeyTypeName: "ApiEndpointKey",
                OperationMetadataConstName: "apiOperationMetadata",
                ScopePoliciesConstName: "apiScopePolicies",
                ScopePolicyByOperationName: "ApiScopePolicyByOperation",
                ScopePolicyByEndpointName: "ApiScopePolicyByEndpoint",
                ScopePolicyMetadataName: "ApiScopePolicyMetadata");
        }

        var camelPrefix = ToCamelCaseIdentifier(modulePrefix);
        return new TypeScriptApiClientMetadataNames(
            OperationIdsConstName: $"{camelPrefix}ApiOperationIds",
            OperationKeyTypeName: $"{modulePrefix}ApiOperationKey",
            EndpointIdsConstName: $"{camelPrefix}ApiEndpointIds",
            EndpointKeyTypeName: $"{modulePrefix}ApiEndpointKey",
            OperationMetadataConstName: $"{camelPrefix}ApiOperationMetadata",
            ScopePoliciesConstName: $"{camelPrefix}ApiScopePolicies",
            ScopePolicyByOperationName: $"{modulePrefix}ApiScopePolicyByOperation",
            ScopePolicyByEndpointName: $"{modulePrefix}ApiScopePolicyByEndpoint",
            ScopePolicyMetadataName: $"{modulePrefix}ApiScopePolicyMetadata");
    }

    TsFunctionDeclaration BuildFunction(ApiOperation operation)
    {
        var http = RequireHttp(operation);
        var identifiers = TypeScriptHttpParameterIdentifiers.Create(operation, http);
        var parameters = ImmutableArray.CreateBuilder<TsParameterDeclaration>();
        parameters.Add(new TsParameterDeclaration("http", new TsTypeReference(options.HttpClientTypeName)));

        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var parameter = http.Parameters[i];
            if (parameter.Source == HttpParameterSource.Query)
                continue;

            parameters.Add(new TsParameterDeclaration(
                name: identifiers[parameter],
                type: new TsRawType(GetParameterTypeScriptTypeText(parameter)),
                isOptional: CanRenderAsOptionalParameter(http, parameter)));
        }

        if (http.Query is { } query)
        {
            parameters.Add(new TsParameterDeclaration(
                name: identifiers.QueryObject,
                type: new TsRawType(AddNullAndUndefined(GetTypeScriptTypeText(query.QueryType))),
                isOptional: CanRenderQueryObjectAsOptionalParameter(http)));
        }

        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var parameter = http.Parameters[i];
            if (parameter.Source != HttpParameterSource.Query)
                continue;

            parameters.Add(new TsParameterDeclaration(
                name: identifiers[parameter],
                type: new TsRawType(GetParameterTypeScriptTypeText(parameter)),
                isOptional: CanRenderAsOptionalParameter(http, parameter)));
        }

        if (http.Body is not null)
            parameters.Add(new TsParameterDeclaration(identifiers.Body, new TsRawType(GetTypeScriptTypeText(http.Body.BodyType))));

        var bodyLines = BuildBody(operation, http, identifiers);
        return new TsFunctionDeclaration(
            name: BuildFunctionName(operation),
            parameters: parameters.ToImmutable(),
            returnType: new TsRawType($"Promise<{GetTypeScriptTypeText(operation.ResponseType)}>"),
            bodyLines: bodyLines);
    }

    ImmutableArray<string> BuildBody(
        ApiOperation operation,
        HttpBinding http,
        TypeScriptHttpParameterIdentifiers identifiers)
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        lines.Add($"const basePath = {BuildRouteExpression(http, identifiers)};");

        var hasQuery = http.Query is not null || CountParameters(http, HttpParameterSource.Query) > 0;
        if (hasQuery)
        {
            lines.Add("const queryParams = new URLSearchParams();");

            if (http.Query is { } query)
                AppendQueryObjectLines(lines, identifiers.QueryObject, query);

            for (var i = 0; i < http.Parameters.Count; i++)
            {
                var parameter = http.Parameters[i];
                if (parameter.Source != HttpParameterSource.Query)
                    continue;

                AppendQueryLines(lines, parameter, identifiers[parameter]);
            }

            lines.Add("const queryText = queryParams.toString();");
            lines.Add("const path = queryText.length === 0 ? basePath : `${basePath}?${queryText}`;");
        }
        else
        {
            lines.Add("const path = basePath;");
        }

        var hasHeaders = CountParameters(http, HttpParameterSource.Header) > 0 || http.Body is not null;
        if (hasHeaders)
        {
            lines.Add("const headers: Record<string, string> = {};");
            for (var i = 0; i < http.Parameters.Count; i++)
            {
                var parameter = http.Parameters[i];
                if (parameter.Source != HttpParameterSource.Header)
                    continue;

                AppendHeaderLines(lines, parameter, identifiers[parameter]);
            }

            if (http.Body is not null)
                lines.Add("headers['content-type'] = 'application/json';");
        }

        lines.Add(BuildReturnLine(operation, http, hasHeaders, identifiers.Body));
        return lines.ToImmutable();
    }

    void AppendQueryLines(
        ImmutableArray<string>.Builder lines,
        HttpParameter parameter,
        string parameterName)
    {
        if (IsSequenceType(parameter.Type))
        {
            lines.Add($"if ({parameterName} !== undefined && {parameterName} !== null) {{");
            lines.Add($"  for (const value of {parameterName}) queryParams.append('{parameter.Name}', String(value));");
            lines.Add("}");
            return;
        }

        if (CanSkipParameter(parameter))
        {
            lines.Add($"if ({parameterName} !== undefined && {parameterName} !== null) queryParams.set('{parameter.Name}', String({parameterName}));");
            return;
        }

        lines.Add($"queryParams.set('{parameter.Name}', String({parameterName}));");
    }

    void AppendQueryObjectLines(
        ImmutableArray<string>.Builder lines,
        string parameterName,
        HttpQueryBinding query)
    {
        var properties = ShapeTypeInspector.GetReadableProperties(query.QueryType);
        if (properties.Length == 0)
            return;

        lines.Add($"if ({parameterName} !== undefined && {parameterName} !== null) {{");
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            var propertyName = property.Name;
            var queryParameterName = ResolveQueryParameterName(property);
            if (IsSequenceType(property.PropertyType))
            {
                lines.Add($"  if ({parameterName}.{propertyName} !== undefined && {parameterName}.{propertyName} !== null) {{");
                lines.Add($"    for (const value of {parameterName}.{propertyName}) queryParams.append('{queryParameterName}', String(value));");
                lines.Add("  }");
                continue;
            }

            lines.Add($"  if ({parameterName}.{propertyName} !== undefined && {parameterName}.{propertyName} !== null) queryParams.set('{queryParameterName}', String({parameterName}.{propertyName}));");
        }

        lines.Add("}");
    }

    void AppendHeaderLines(
        ImmutableArray<string>.Builder lines,
        HttpParameter parameter,
        string parameterName)
    {
        if (CanSkipParameter(parameter))
        {
            lines.Add($"if ({parameterName} !== undefined && {parameterName} !== null) headers['{parameter.Name}'] = String({parameterName});");
            return;
        }

        lines.Add($"headers['{parameter.Name}'] = String({parameterName});");
    }

    string BuildReturnLine(
        ApiOperation operation,
        HttpBinding http,
        bool hasHeaders,
        string bodyParameterName)
    {
        var boundBodyParameterName = http.Body is null ? null : bodyParameterName;
        var init = new List<string>
        {
            $"method: '{http.Method}'"
        };

        if (hasHeaders)
            init.Add("headers");

        if (boundBodyParameterName is not null)
            init.Add($"body: JSON.stringify({boundBodyParameterName})");

        var responseType = GetTypeScriptTypeText(operation.ResponseType);
        return $"return http(path, {{ {string.Join(", ", init)} }}) as Promise<{responseType}>;";
    }

    string BuildRouteExpression(
        HttpBinding binding,
        TypeScriptHttpParameterIdentifiers identifiers)
    {
        var routeParameters = GetParameters(binding, HttpParameterSource.Route);
        if (routeParameters.Count == 0)
            return Quote(binding.Route);

        var builder = new System.Text.StringBuilder(binding.Route.Length + 16);
        builder.Append('`');

        for (var index = 0; index < binding.Route.Length; index += 1)
        {
            var current = binding.Route[index];
            if (current != '{')
            {
                if (current == '`')
                    builder.Append("\\`");
                else
                    builder.Append(current);
                continue;
            }

            var end = binding.Route.IndexOf('}', index + 1);
            if (end <= index)
            {
                builder.Append(current);
                continue;
            }

            var token = binding.Route.Substring(index + 1, end - index - 1);
            var parameterName = NormalizeRouteToken(token);
            builder.Append("${encodeURIComponent(String(");
            builder.Append(identifiers.Get(HttpParameterSource.Route, parameterName));
            builder.Append("))}");
            index = end;
        }

        builder.Append('`');
        return builder.ToString();
    }

    static string BuildFunctionName(ApiOperation operation)
    {
        if (operation.Entity is { } entity)
        {
            var entityName = GetTypeName(entity.Value);
            if (operation.Kind == ApiOperationKind.Query
                && (string.Equals(operation.Name, "GetById", StringComparison.Ordinal)
                    || string.Equals(operation.Name, "Get", StringComparison.Ordinal)))
            {
                return $"get{entityName}";
            }

            return $"{ToCamelCase(operation.Name)}{entityName}";
        }

        return ToCamelCase(operation.Name);
    }

    static int CountParameters(HttpBinding binding, HttpParameterSource source)
    {
        var count = 0;
        for (var i = 0; i < binding.Parameters.Count; i++)
        {
            if (binding.Parameters[i].Source == source)
                count++;
        }

        return count;
    }

    static IReadOnlyList<HttpParameter> GetParameters(HttpBinding binding, HttpParameterSource source)
    {
        var parameters = new List<HttpParameter>();
        for (var i = 0; i < binding.Parameters.Count; i++)
        {
            if (binding.Parameters[i].Source == source)
                parameters.Add(binding.Parameters[i]);
        }

        return parameters;
    }

    static string GetTypeScriptTypeText(Type type)
    {
        if (type == typeof(void))
            return "void";

        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return $"{GetTypeScriptTypeText(nullable)} | null";

        if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "string";
        }

        if (type == typeof(bool))
            return "boolean";

        if (IsNumeric(type))
            return "number";

        if (type.IsArray)
            return $"{GetTypeScriptTypeText(type.GetElementType()!)}[]";

        if (TryGetSequenceElementType(type, out var elementType))
            return $"{GetTypeScriptTypeText(elementType!)}[]";

        return GetTypeName(type);
    }

    static string GetParameterTypeScriptTypeText(HttpParameter parameter)
    {
        var text = GetTypeScriptTypeText(parameter.Type);
        return parameter.IsOptional ? AddNullAndUndefined(text) : text;
    }

    static string AddNullAndUndefined(string typeText)
    {
        var text = typeText;
        if (!text.Contains("null", StringComparison.Ordinal))
            text += " | null";

        if (!text.Contains("undefined", StringComparison.Ordinal))
            text += " | undefined";

        return text;
    }

    static Type UnwrapType(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return UnwrapType(nullable);

        if (type.IsArray)
            return UnwrapType(type.GetElementType()!);

        if (TryGetSequenceElementType(type, out var elementType))
            return UnwrapType(elementType!);

        return type;
    }

    static bool IsBuiltInType(Type type)
    {
        if (type == typeof(void)
            || type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(bool))
        {
            return true;
        }

        return IsNumeric(type);
    }

    static bool IsNumeric(Type type)
    {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);
    }

    static bool CanSkipWhenUndefined(Type type) =>
        Nullable.GetUnderlyingType(type) is not null || !type.IsValueType;

    static bool CanSkipParameter(HttpParameter parameter) =>
        parameter.IsOptional || CanSkipWhenUndefined(parameter.Type);

    static bool CanRenderAsOptionalParameter(HttpBinding http, HttpParameter parameter)
    {
        if (!parameter.IsOptional)
            return false;

        if (http.Body is not null)
            return false;

        if (parameter.Source != HttpParameterSource.Query)
            return false;

        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var candidate = http.Parameters[i];
            if (candidate.Source == HttpParameterSource.Query && !candidate.IsOptional)
                return false;
        }

        return true;
    }

    static bool CanRenderQueryObjectAsOptionalParameter(HttpBinding http)
    {
        if (http.Body is not null)
            return false;

        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var candidate = http.Parameters[i];
            if (candidate.Source == HttpParameterSource.Query && !candidate.IsOptional)
                return false;
        }

        return true;
    }

    static HttpBinding RequireHttp(ApiOperation operation) =>
        operation.Http ?? throw new InvalidOperationException(
            $"API operation '{operation.Id}' does not declare an HTTP projection.");

    static string ResolveQueryParameterName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? ToSnakeCase(property.Name);

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

    static bool IsSequenceType(Type type) =>
        type != typeof(string) && TryGetSequenceElementType(type, out _);

    static bool TryGetSequenceElementType(Type type, out Type? elementType)
    {
        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
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

        elementType = null;
        return false;
    }

    static string Quote(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('\'');
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            switch (current)
            {
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    builder.Append(current);
                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    static bool ContainsName(List<string> names, string value)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static string NormalizeRouteToken(string token)
    {
        var separator = token.IndexOfAny([':', '=', '?']);
        return separator >= 0 ? token[..separator] : token;
    }

    static string GetTypeName(Type type)
    {
        var name = type.Name;
        var genericTick = name.IndexOf('`');
        return genericTick >= 0 ? name[..genericTick] : name;
    }

    static string GetTypeName(string typeName)
    {
        var genericTick = typeName.IndexOf('`');
        return genericTick >= 0 ? typeName[..genericTick] : typeName;
    }

    static string ToPascalCaseIdentifier(string? value) => SanitizeIdentifier(value, pascalCase: true);

    static string ToCamelCaseIdentifier(string? value)
    {
        var pascal = SanitizeIdentifier(value, pascalCase: true);
        return string.IsNullOrWhiteSpace(pascal)
            ? string.Empty
            : pascal.Length == 1
                ? char.ToLowerInvariant(pascal[0]).ToString()
                : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    static string SanitizeIdentifier(string? value, bool pascalCase)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        var upperNext = pascalCase;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (!char.IsLetterOrDigit(current))
            {
                upperNext = true;
                continue;
            }

            if (builder.Length == 0 && char.IsDigit(current))
                builder.Append('_');

            builder.Append(upperNext ? char.ToUpperInvariant(current) : current);
            upperNext = false;
        }

        return builder.ToString();
    }

    static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (value.Length == 1)
            return char.ToLowerInvariant(value[0]).ToString();

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    readonly record struct TypeScriptApiClientMetadataNames(
        string OperationIdsConstName,
        string OperationKeyTypeName,
        string EndpointIdsConstName,
        string EndpointKeyTypeName,
        string OperationMetadataConstName,
        string ScopePoliciesConstName,
        string ScopePolicyByOperationName,
        string ScopePolicyByEndpointName,
        string ScopePolicyMetadataName);
}
