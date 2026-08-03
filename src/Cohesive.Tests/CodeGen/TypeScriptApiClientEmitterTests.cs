using Cohesive.Adapters.TypeScript;
using Cohesive.Api;
using Cohesive.Api.CodeGen;
using Cohesive.Api.Execution;
using Cohesive.CodeGen;
using Cohesive.Execution;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.CodeGen;

public sealed class TypeScriptApiClientEmitterTests
{
    [Fact]
    public void Emit_RouteLessSemanticOperation_RetainsIdentityButOmitsHttpClientAndMockBindings()
    {
        var source = new ExecutionSourceProvenance(
            reference: "notion://execution-kernel/operations/inspect",
            semanticPath: new(["operations", "inspect"]),
            description: "Normative inspect operation");
        var definition = Cohesive.Api.Api.Define("Execution")
            .Query("Inspect")
                .Returns<string>()
                .Requirement(new("execution.inspect"))
                .SemanticReference(new(
                    authority: "cohesive.execution.process-control",
                    schemaVersion: new("cohesive-process-control/v1"),
                    path: new(["commands", "inspect"]),
                    source: source))
                .Done()
            .Action("Health")
                .Route("GET", "/health")
                .Returns<string>()
                .Done()
            .Build();

        var client = new TypeScriptApiClientEmitter().Emit(new ApiCodeGenerationRequest(definition));
        var clientText = Assert.Single(client.Documents).Text;
        Assert.Contains("export type ApiOperationKey = 'inspect' | 'health';", clientText, StringComparison.Ordinal);
        Assert.Contains("export const apiOperationIds = {\n  inspect: 'Execution.Inspect',\n  health: 'Execution.Health',\n} as const satisfies Record<ApiOperationKey, string>;", clientText, StringComparison.Ordinal);
        Assert.Contains("export type ApiEndpointKey = 'health';", clientText, StringComparison.Ordinal);
        Assert.Contains("export const apiEndpointIds = {\n  health: 'Execution.Health',\n} as const satisfies Record<ApiEndpointKey, string>;", clientText, StringComparison.Ordinal);
        Assert.Contains("export interface ApiScopePolicyByOperation {", clientText, StringComparison.Ordinal);
        Assert.Contains("export type ApiScopePolicyByEndpoint = Pick<ApiScopePolicyByOperation, ApiEndpointKey>;", clientText, StringComparison.Ordinal);
        Assert.Contains("} as const satisfies ApiScopePolicyByOperation;", clientText, StringComparison.Ordinal);
        Assert.Contains("Execution.Inspect", clientText, StringComparison.Ordinal);
        Assert.DoesNotContain("export function inspect", clientText, StringComparison.Ordinal);
        Assert.Contains("export function health", clientText, StringComparison.Ordinal);
        Assert.Contains("export const apiOperationMetadata = {", clientText, StringComparison.Ordinal);
        Assert.Contains("authorizationRequirementIds: [\n      'execution.inspect',", clientText, StringComparison.Ordinal);
        Assert.Contains("authority: 'cohesive.execution.process-control'", clientText, StringComparison.Ordinal);
        Assert.Contains("schemaVersion: 'cohesive-process-control/v1'", clientText, StringComparison.Ordinal);
        Assert.Contains("path: '/commands/inspect'", clientText, StringComparison.Ordinal);
        Assert.Contains("reference: 'notion://execution-kernel/operations/inspect'", clientText, StringComparison.Ordinal);
        Assert.Contains("semanticPath: '/operations/inspect'", clientText, StringComparison.Ordinal);
        Assert.Contains("description: 'Normative inspect operation'", clientText, StringComparison.Ordinal);
        Assert.Contains("http: null", clientText, StringComparison.Ordinal);
        Assert.Contains("method: 'GET'", clientText, StringComparison.Ordinal);
        Assert.Contains("route: '/health'", clientText, StringComparison.Ordinal);

        var mock = new TypeScriptPlaywrightApiMockEmitter().Emit(new ApiCodeGenerationRequest(definition));
        var mockText = Assert.Single(mock.Documents).Text;
        Assert.DoesNotContain("Execution.Inspect", mockText, StringComparison.Ordinal);
        Assert.Contains("Execution.Health", mockText, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_ExecutionControlCatalog_RetainsControlAndDiagnosticsOperationContracts()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        ApiEndpoint[] expectedEndpoints =
        [
            catalog.Start,
            catalog.Inspect,
            catalog.Explain,
            catalog.Signal,
            catalog.Pause,
            catalog.Continue,
            catalog.RestartAttempt,
            catalog.Cancel,
            catalog.Terminate,
            catalog.UpdateLimits
        ];
        Assert.All(catalog.Definition.Operations, static operation => Assert.Null(operation.Http));

        var emitter = new TypeScriptApiClientEmitter(new TypeScriptApiClientEmitterOptions
        {
            ModuleName = "executionControl",
            NewLine = "\n"
        });
        var first = Assert.Single(emitter.Emit(catalog.Definition).Documents).Text;
        var second = Assert.Single(emitter.Emit(catalog.Definition).Documents).Text;

        Assert.Equal(first, second);
        Assert.Contains("export type ExecutionControlApiOperationKey =", first, StringComparison.Ordinal);
        Assert.Contains("'start'", first, StringComparison.Ordinal);
        Assert.Contains("'explain'", first, StringComparison.Ordinal);
        Assert.Contains("'updateLimits'", first, StringComparison.Ordinal);
        Assert.Contains("export const executionControlApiOperationIds = {", first, StringComparison.Ordinal);
        Assert.Contains("} as const satisfies Record<ExecutionControlApiOperationKey, string>;", first, StringComparison.Ordinal);
        Assert.Contains("export type ExecutionControlApiEndpointKey = never;", first, StringComparison.Ordinal);
        Assert.Contains("export const executionControlApiEndpointIds = {} as const satisfies Record<ExecutionControlApiEndpointKey, string>;", first, StringComparison.Ordinal);
        Assert.Contains("export const executionControlApiOperationMetadata = {", first, StringComparison.Ordinal);
        Assert.Contains("} as const satisfies Record<ExecutionControlApiOperationKey, unknown>;", first, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(first, "    http: null,"));
        Assert.DoesNotContain("export function ", first, StringComparison.Ordinal);

        var metadataOffset = first.IndexOf(
            "export const executionControlApiOperationMetadata = {",
            StringComparison.Ordinal);
        var previousOffset = -1;
        for (var operationIndex = 0; operationIndex < expectedEndpoints.Length; operationIndex++)
        {
            var operation = catalog.Definition.GetOperation(expectedEndpoints[operationIndex]);
            var endpointKey = char.ToLowerInvariant(operation.Name[0]) + operation.Name[1..];
            var identity = $"{endpointKey}: '{operation.Id.Value}'";
            Assert.Contains(identity, first, StringComparison.Ordinal);

            var operationHeader =
                $"{endpointKey}: {{\n    id: '{operation.Id.Value}',\n    kind: '{ApiWireNames.OperationKind(operation.Kind)}',\n    requestContract: '{TypeScriptContractName(operation.RequestType)}',";
            var offset = first.IndexOf(operationHeader, metadataOffset, StringComparison.Ordinal);
            Assert.True(offset > previousOffset, $"Endpoint '{operation.Id}' was not emitted in catalog order.");
            previousOffset = offset;

            foreach (var requirement in operation.AuthorizationRequirements)
                Assert.Contains($"'{requirement.Id}'", first, StringComparison.Ordinal);

            foreach (var result in operation.Results)
            {
                Assert.Contains(
                    $"id: '{result.Id}',\n        kind: '{ApiWireNames.ResultKind(result.Kind)}',\n        bodyContract: '{TypeScriptContractName(result.BodyType)}',\n        isPrimary: {result.IsPrimary.ToString().ToLowerInvariant()},",
                    first,
                    StringComparison.Ordinal);
            }

            foreach (var reference in operation.SemanticReferences)
            {
                Assert.Contains(
                    $"authority: '{reference.Authority}',\n        schemaVersion: '{reference.SchemaVersion.Value}',\n        path: '{reference.Path}',\n        source: null,",
                    first,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Emit_Definition_GeneratesComposableTypeScriptClientFunctions()
    {
        var definition = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("GetById")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .Body<DispatchShipmentRequest>()
                .Returns<ShipmentDto>()
                .Transition(new TransitionDefinition("Dispatch"))
                .Done()
            .Action("Health")
                .Route("GET", "/api/health")
                .QueryParameter<string>("scope")
                .Returns<HealthStatusDto>()
                .Done()
            .Action("Lookup")
                .Route("GET", "/api/lookup")
                .OptionalQueryParameter<string>("term")
                .OptionalQueryParameter<int?>("limit")
                .Returns<ShipmentDto[]>()
                .Done()
            .Action("Search")
                .Route("GET", "/api/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Done()
            .Build();

        var emission = new TypeScriptApiClientEmitter(new TypeScriptApiClientEmitterOptions
        {
            FileName = "sample.api.generated.ts",
            ShapesImportPath = "./sample.shapes.generated",
            NewLine = "\n",
            EmitAutoGeneratedHeader = true
        }).Emit(new ApiCodeGenerationRequest(definition));

        var document = Assert.Single(emission.Documents);
        Assert.Equal("sample.api.generated.ts", document.FileName);
        Assert.Contains("import type { DispatchShipmentRequest, HealthStatusDto, SearchShipmentsRequest, ShipmentDto } from './sample.shapes.generated';", document.Text, StringComparison.Ordinal);
        Assert.Contains("export type ApiHttpClient = (path: string, init: RequestInit) => Promise<unknown>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function getShipment(http: ApiHttpClient, id: string): Promise<ShipmentDto>", document.Text, StringComparison.Ordinal);
        Assert.Contains("const basePath = `/api/shipments/${encodeURIComponent(String(id))}`;", document.Text, StringComparison.Ordinal);
        Assert.Contains("return http(path, { method: 'GET' }) as Promise<ShipmentDto>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function dispatchShipment(http: ApiHttpClient, id: string, body: DispatchShipmentRequest): Promise<ShipmentDto>", document.Text, StringComparison.Ordinal);
        Assert.Contains("headers['content-type'] = 'application/json';", document.Text, StringComparison.Ordinal);
        Assert.Contains("body: JSON.stringify(body)", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function health(http: ApiHttpClient, scope: string): Promise<HealthStatusDto>", document.Text, StringComparison.Ordinal);
        Assert.Contains("queryParams.set('scope', String(scope));", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function lookup(http: ApiHttpClient, term?: string | null | undefined, limit?: number | null | undefined): Promise<ShipmentDto[]>", document.Text, StringComparison.Ordinal);
        Assert.Contains("if (term !== undefined && term !== null) queryParams.set('term', String(term));", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function search(http: ApiHttpClient, query?: SearchShipmentsRequest | null | undefined): Promise<ShipmentDto[]>", document.Text, StringComparison.Ordinal);
        Assert.Contains("if (query !== undefined && query !== null) {", document.Text, StringComparison.Ordinal);
        Assert.Contains("if (query.Term !== undefined && query.Term !== null) queryParams.set('term', String(query.Term));", document.Text, StringComparison.Ordinal);
        Assert.Contains("if (query.IncludeArchived !== undefined && query.IncludeArchived !== null) queryParams.set('include_archived', String(query.IncludeArchived));", document.Text, StringComparison.Ordinal);
        Assert.Contains("for (const value of query.Tags) queryParams.append('tags', String(value));", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_Definition_GeneratesEndpointAndScopeMetadata()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Header,
                    access: ApiScopeAccess.RequireSelected,
                    singleScopeParameterName: "X-Tenant-Id",
                    allowDefaultScope: false))
                .Done()
            .Action("Search")
                .Route("GET", "/api/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Multiple,
                    binding: ApiScopeBinding.Query,
                    access: ApiScopeAccess.FilterToAccessible,
                    multipleScopesParameterName: "tenant_ids"))
                .Done()
            .Action("GetProcess")
                .Route("GET", "/api/processes/{processId}")
                .RouteParameter<string>("processId")
                .Returns<ShipmentDto>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Resource,
                    access: ApiScopeAccess.ValidateAccessible,
                    resourceParameterName: "processId",
                    resourceDerivation: ScopedProcessInstanceIdScopeDerivation(),
                    allowDefaultScope: false))
                .Done()
            .Build();

        var emission = new TypeScriptApiClientEmitter(new TypeScriptApiClientEmitterOptions
        {
            FileName = "sample.api.generated.ts",
            ModuleName = "sample",
            ShapesImportPath = "./sample.shapes.generated",
            NewLine = "\n",
            EmitAutoGeneratedHeader = true
        }).Emit(new ApiCodeGenerationRequest(definition));

        var document = Assert.Single(emission.Documents);
        Assert.Contains("export type SampleApiOperationKey =", document.Text, StringComparison.Ordinal);
        Assert.Contains("export const sampleApiOperationIds = {", document.Text, StringComparison.Ordinal);
        Assert.Contains("export type SampleApiEndpointKey =", document.Text, StringComparison.Ordinal);
        Assert.Contains("'getShipment'", document.Text, StringComparison.Ordinal);
        Assert.Contains("'search'", document.Text, StringComparison.Ordinal);
        Assert.Contains("export const sampleApiEndpointIds = {", document.Text, StringComparison.Ordinal);
        Assert.Contains("getShipment: 'Shipping.Shipment.Get',", document.Text, StringComparison.Ordinal);
        Assert.Contains("search: 'Shipping.Search',", document.Text, StringComparison.Ordinal);
        Assert.Contains("export interface SampleApiScopePolicyMetadata {", document.Text, StringComparison.Ordinal);
        Assert.Contains("export interface SampleApiScopePolicyByOperation {", document.Text, StringComparison.Ordinal);
        Assert.Contains("export type SampleApiScopePolicyByEndpoint = Pick<SampleApiScopePolicyByOperation, SampleApiEndpointKey>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly getShipment: readonly SampleApiScopePolicyMetadata[];", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly getProcess: readonly SampleApiScopePolicyMetadata[];", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly search: readonly SampleApiScopePolicyMetadata[];", document.Text, StringComparison.Ordinal);
        Assert.Contains("export const sampleApiScopePolicies = {", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly resourceDerivation?: {", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly strategy: string;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly format?: string;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly scopeField?: string;", document.Text, StringComparison.Ordinal);
        Assert.Contains("kind: 'shipping.tenant',", document.Text, StringComparison.Ordinal);
        Assert.Contains("cardinality: 'single',", document.Text, StringComparison.Ordinal);
        Assert.Contains("binding: 'header',", document.Text, StringComparison.Ordinal);
        Assert.Contains("access: 'requireSelected',", document.Text, StringComparison.Ordinal);
        Assert.Contains("singleScopeParameterName: 'X-Tenant-Id',", document.Text, StringComparison.Ordinal);
        Assert.Contains("allowDefaultScope: false,", document.Text, StringComparison.Ordinal);
        Assert.Contains("multipleScopesParameterName: 'tenant_ids',", document.Text, StringComparison.Ordinal);
        Assert.Contains("resourceParameterName: 'processId',", document.Text, StringComparison.Ordinal);
        Assert.Contains("resourceDerivation: {", document.Text, StringComparison.Ordinal);
        Assert.Contains("strategy: 'structuredResourceId',", document.Text, StringComparison.Ordinal);
        Assert.Contains("format: 'scopedProcessInstanceId',", document.Text, StringComparison.Ordinal);
        Assert.Contains("scopeField: 'scopeId',", document.Text, StringComparison.Ordinal);
        Assert.Contains("} as const satisfies SampleApiScopePolicyByOperation;", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_PlaywrightApiMock_GeneratesTypedMockHost()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound)
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Header,
                    singleScopeParameterName: "X-Tenant-Id",
                    allowDefaultScope: false))
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .RouteParameter<Guid>("id")
                .Body<DispatchShipmentRequest>()
                .Returns<ShipmentDto>()
                .Done()
            .Action("Search")
                .Route("GET", "/api/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Done()
            .Action("GetProcess")
                .Route("GET", "/api/processes/{processId}")
                .RouteParameter<string>("processId")
                .Returns<ShipmentDto>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Resource,
                    access: ApiScopeAccess.ValidateAccessible,
                    resourceParameterName: "processId",
                    resourceDerivation: ScopedProcessInstanceIdScopeDerivation(),
                    allowDefaultScope: false))
                .Done()
            .Build();

        var emission = new TypeScriptPlaywrightApiMockEmitter(new TypeScriptPlaywrightApiMockEmitterOptions
        {
            FileName = "sample.api.playwright.generated.ts",
            ShapesImportPath = "./sample.shapes.generated",
            ModuleName = "sample",
            NewLine = "\n",
            EmitAutoGeneratedHeader = true
        }).Emit(new ApiCodeGenerationRequest(definition));

        var document = Assert.Single(emission.Documents);
        Assert.Equal("sample.api.playwright.generated.ts", document.FileName);
        Assert.Contains("import type { Page, Request, Route } from '@playwright/test';", document.Text, StringComparison.Ordinal);
        Assert.Contains("import type { ApiProblem, DispatchShipmentRequest, SearchShipmentsRequest, ShipmentDto } from './sample.shapes.generated';", document.Text, StringComparison.Ordinal);
        Assert.Contains("export type SampleApiEndpointKey =", document.Text, StringComparison.Ordinal);
        Assert.Contains("| 'getShipment'", document.Text, StringComparison.Ordinal);
        Assert.Contains("getShipment: 'Shipping.Shipment.Get'", document.Text, StringComparison.Ordinal);
        Assert.Contains("export interface SampleApiScopePolicyMetadata", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly resourceDerivation?: { readonly strategy: string; readonly format?: string; readonly scopeField?: string; };", document.Text, StringComparison.Ordinal);
        Assert.Contains("export const sampleApiScopePolicies = {", document.Text, StringComparison.Ordinal);
        Assert.Contains("{ kind: 'shipping.tenant', cardinality: 'single', binding: 'header', access: 'requireSelected', singleScopeParameterName: 'X-Tenant-Id', allowDefaultScope: false },", document.Text, StringComparison.Ordinal);
        Assert.Contains("resourceParameterName: 'processId', resourceDerivation: { strategy: 'structuredResourceId', format: 'scopedProcessInstanceId', scopeField: 'scopeId' }, allowDefaultScope: false", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly scopePolicies: SampleApiScopePolicyByEndpoint[TKey];", document.Text, StringComparison.Ordinal);
        Assert.Contains("scopePolicies: sampleApiScopePolicies[match.endpointKey],", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly getShipment: { readonly id: string; };", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly getShipment: ShipmentDto | ApiProblem;", document.Text, StringComparison.Ordinal);
        Assert.Contains("export const sampleApiResults = {", document.Text, StringComparison.Ordinal);
        Assert.Contains("notFound: { id: 'notFound', kind: 'notFound', status: 404, contentType: 'application/json' }", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly result: SampleApiResultBuilderByEndpoint[TKey];", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly notFound: (body: ApiProblem) => ApiMockResponse<ApiProblem>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("notFound: (body: ApiProblem) => semanticResult(sampleApiResults.getShipment.notFound, body),", document.Text, StringComparison.Ordinal);
        Assert.Contains("export async function installSampleApiMock(page: Page, handlers: SampleApiMockHandlers, options: SampleApiMockInstallOptions)", document.Text, StringComparison.Ordinal);
        Assert.Contains("requestCountFor<TKey extends SampleApiEndpointKey>(endpointKey: TKey): number;", document.Text, StringComparison.Ordinal);
        Assert.Contains("firstRequestFor<TKey extends SampleApiEndpointKey>(endpointKey: TKey): SampleApiMockRequestRecord<TKey> | undefined;", document.Text, StringComparison.Ordinal);
        Assert.Contains("lastRequestFor<TKey extends SampleApiEndpointKey>(endpointKey: TKey): SampleApiMockRequestRecord<TKey> | undefined;", document.Text, StringComparison.Ordinal);
        Assert.Contains("clearRequests(endpointKey?: SampleApiEndpointKey): void;", document.Text, StringComparison.Ordinal);
        Assert.Contains("return state.requests.filter((request) => request.endpointKey === endpointKey).length;", document.Text, StringComparison.Ordinal);
        Assert.Contains("return state.requests.find((request): request is SampleApiMockRequestRecord<typeof endpointKey> => request.endpointKey === endpointKey);", document.Text, StringComparison.Ordinal);
        Assert.Contains("state.requests.length = 0;", document.Text, StringComparison.Ordinal);
        Assert.Contains("state.requests.splice(index, 1);", document.Text, StringComparison.Ordinal);
        Assert.Contains("const route0 = matchApiMockRoute('/api/search', pathname);", document.Text, StringComparison.Ordinal);
        Assert.Contains("return { endpointKey: 'getShipment'", document.Text, StringComparison.Ordinal);
        Assert.Contains("function decodeSearchQuery(searchParams: URLSearchParams): SearchShipmentsRequest", document.Text, StringComparison.Ordinal);
        Assert.Contains("query.IncludeArchived = includeArchived as SearchShipmentsRequest['IncludeArchived'];", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function ok<TBody>(body: TBody): ApiMockResponse<TBody>", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function noContent(statusCode = 204): ApiMockResponse<void>", document.Text, StringComparison.Ordinal);
        Assert.Contains("export function unhandled(message?: string): ApiMockUnhandledResult", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_PlaywrightApiMock_RequiresExplicitInstallUrlPattern()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("Health")
                .Route("GET", "/api/health")
                .Returns<HealthStatusDto>()
                .Done()
            .Build();

        var document = EmitPlaywrightApiMock(definition);

        Assert.Contains("readonly urlPattern: string;", document.Text, StringComparison.Ordinal);
        Assert.Contains("export async function installSampleApiMock(page: Page, handlers: SampleApiMockHandlers, options: SampleApiMockInstallOptions)", document.Text, StringComparison.Ordinal);
        Assert.Contains("await page.route(options.urlPattern, async (route) => handleSampleApiRoute(route, handlers, state, options));", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultUrlPattern", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("options?:", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_PlaywrightApiMock_GeneratesSemanticResultBuilderContract()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("Submit")
                .Route("POST", "/api/shipments")
                .Body<SubmitShipmentRequest>()
                .Returns<ShipmentDto>()
                .Result<ValidationProblemDto>(ApiResultKind.ValidationFailed)
                .Result<ConflictProblemDto>(ApiResultKind.Conflict, httpStatusCode: 412, id: "concurrencyTokenMismatch")
                .Result(ApiResultKind.NoContent, id: "alreadyApplied")
                .Done()
            .Build();

        var document = EmitPlaywrightApiMock(definition);

        Assert.Contains("import type { ConflictProblemDto, ShipmentDto, SubmitShipmentRequest, ValidationProblemDto } from './sample.shapes.generated';", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly submit: ShipmentDto | ValidationProblemDto | ConflictProblemDto | void;", document.Text, StringComparison.Ordinal);
        Assert.Contains("validationFailed: { id: 'validationFailed', kind: 'validationFailed', status: 400, contentType: 'application/json' },", document.Text, StringComparison.Ordinal);
        Assert.Contains("concurrencyTokenMismatch: { id: 'concurrencyTokenMismatch', kind: 'conflict', status: 412, contentType: 'application/json' },", document.Text, StringComparison.Ordinal);
        Assert.Contains("alreadyApplied: { id: 'alreadyApplied', kind: 'noContent', status: 204, contentType: 'application/json' },", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly validationFailed: (body: ValidationProblemDto) => ApiMockResponse<ValidationProblemDto>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly concurrencyTokenMismatch: (body: ConflictProblemDto) => ApiMockResponse<ConflictProblemDto>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly alreadyApplied: () => ApiMockResponse<void>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("validationFailed: (body: ValidationProblemDto) => semanticResult(sampleApiResults.submit.validationFailed, body),", document.Text, StringComparison.Ordinal);
        Assert.Contains("concurrencyTokenMismatch: (body: ConflictProblemDto) => semanticResult(sampleApiResults.submit.concurrencyTokenMismatch, body),", document.Text, StringComparison.Ordinal);
        Assert.Contains("alreadyApplied: () => semanticNoContent(sampleApiResults.submit.alreadyApplied),", document.Text, StringComparison.Ordinal);
        Assert.Contains("resultId: metadata.id,", document.Text, StringComparison.Ordinal);
        Assert.Contains("resultKind: metadata.kind,", document.Text, StringComparison.Ordinal);
        Assert.Contains("'access-control-expose-headers': 'x-cohesive-result-id,x-cohesive-result-kind',", document.Text, StringComparison.Ordinal);
        Assert.Contains("...(response.resultId === undefined ? {} : { 'x-cohesive-result-id': response.resultId }),", document.Text, StringComparison.Ordinal);
        Assert.Contains("...(response.resultKind === undefined ? {} : { 'x-cohesive-result-kind': response.resultKind }),", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_PlaywrightApiMock_GeneratesRouteQueryAndBodyContracts()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Done()
            .Query("Search")
                .Route("GET", "/api/shipments/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .RouteParameter<Guid>("id")
                .Body<DispatchShipmentRequest>()
                .Returns<ShipmentDto>()
                .Done()
            .Build();

        var document = EmitPlaywrightApiMock(definition);

        Assert.Contains("readonly searchShipment: SearchShipmentsRequest;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly dispatchShipment: DispatchShipmentRequest;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly getShipment: { readonly id: string; };", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly searchShipment: Record<string, never>;", document.Text, StringComparison.Ordinal);
        Assert.Contains("readonly dispatchShipment: { readonly id: string; };", document.Text, StringComparison.Ordinal);
        Assert.Contains("case 'searchShipment':", document.Text, StringComparison.Ordinal);
        Assert.Contains("function decodeSearchShipmentQuery(searchParams: URLSearchParams): SearchShipmentsRequest", document.Text, StringComparison.Ordinal);
        Assert.Contains("const tags = searchParams.getAll('tags');", document.Text, StringComparison.Ordinal);
        Assert.Contains("query.Tags = tags.map((value) => readApiMockString(value) ?? '') as SearchShipmentsRequest['Tags'];", document.Text, StringComparison.Ordinal);
        Assert.Contains("const includeArchived = readApiMockBoolean(searchParams.get('include_archived'));", document.Text, StringComparison.Ordinal);
        Assert.Contains("body: body as SampleApiBodyByEndpoint[typeof match.endpointKey],", document.Text, StringComparison.Ordinal);

        var staticRouteIndex = document.Text.IndexOf("matchApiMockRoute('/api/shipments/search', pathname)", StringComparison.Ordinal);
        var parameterRouteIndex = document.Text.IndexOf("matchApiMockRoute('/api/shipments/{id}', pathname)", StringComparison.Ordinal);
        Assert.True(staticRouteIndex >= 0, "Expected static search route matcher to be emitted.");
        Assert.True(parameterRouteIndex >= 0, "Expected parameterized get route matcher to be emitted.");
        Assert.True(staticRouteIndex < parameterRouteIndex, "Static route matchers should be emitted before parameterized route matchers with the same method.");
    }

    static GeneratedCodeDocument EmitPlaywrightApiMock(ApiDefinition definition)
    {
        var emission = new TypeScriptPlaywrightApiMockEmitter(new TypeScriptPlaywrightApiMockEmitterOptions
        {
            FileName = "sample.api.playwright.generated.ts",
            ShapesImportPath = "./sample.shapes.generated",
            ModuleName = "sample",
            NewLine = "\n",
            EmitAutoGeneratedHeader = true
        }).Emit(new ApiCodeGenerationRequest(definition));

        return Assert.Single(emission.Documents);
    }

    static ApiResourceScopeDerivation ScopedProcessInstanceIdScopeDerivation() => new(
        strategy: ApiResourceScopeDerivationStrategies.StructuredResourceId,
        format: ApiResourceIdFormats.ScopedProcessInstanceId,
        scopeField: ApiResourceScopeFields.ScopeId);

    static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }

        return count;
    }

    static string TypeScriptContractName(Type type) => type == typeof(void)
        ? "void"
        : type == typeof(string) || type == typeof(Guid) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            ? "string"
            : type.Name.Split('`')[0];

    sealed record Shipment(string Id);

    sealed record ShipmentDto(string Id, string Status);

    sealed record ApiProblem(string Code, string Message);

    sealed record DispatchShipmentRequest(string Reason);

    sealed record HealthStatusDto(string Status);

    sealed record SubmitShipmentRequest(string ExternalId);

    sealed record ValidationProblemDto(string Code, string Message);

    sealed record ConflictProblemDto(string Code, string Message);

    sealed record SearchShipmentsRequest(
        string? Term,
        [property: JsonPropertyName("include_archived")] bool? IncludeArchived,
        string[]? Tags);
}
