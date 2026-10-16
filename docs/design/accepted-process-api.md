# Accepted process execution and linked results

An accepted endpoint promises **admission under its declared lifetime**, not completion. The service declares that lifetime;
HTTP response timing cannot turn host-owned execution into durable execution. A second service operation reads
the original retained process result under independent authorization.

## Declare, attach, bind

Given a typed `process` declaration, author both service operations using the existing service builder:

```csharp
var service = Service.Define(new("orders"), new("1"), provenance)
    .Operation("fulfill").Require(new("orders.fulfill"))
        .Run(process).ReturnAfterDurableAdmission()
    .Operation("fulfillment-result").Require(new("orders.results.read"))
        .ReadResultOf(process);

var api = service.AcceptedApi<FulfillCommand, FulfillmentResult>(
    startOperationId: "fulfill",
    resultOperationId: "fulfillment-result",
    startRoute: "/orders/fulfillment",
    resultRoute: "/orders/fulfillment/{instanceId}");
```

`api.Start` is a typed command returning `ServiceProcessReceipt`; `api.Result` is the typed terminal view.
`api.Result.Operation.ResultOf` points to the start endpoint ID. The relationship survives `WithHttp`,
API combination and OpenAPI emission (`x-cohesive-result-of`). The paired contract validates that both
operations refer to the same exact process definition/revision/fingerprint. Their authorization requirements
remain separate. Its service may contain other operations; the runtime must bind **all** of them.

Native execution stays explicit:

```csharp
var runtime = new ServiceRuntime(api.Declaration,
    [new ServiceProcessBinding("fulfill", plan, authority, durableStart),
     new ServiceProcessResultBinding("fulfillment-result", plan, authority, retainedValues)],
    authorization);

var model = new ServiceModelBuilder()
    .Implement(api, implementation => implementation.Bind(
        pair => pair,
        runtime,
        bind: (_, command) => (
            new(command.CommandId),
            new(command.IdempotencyKey),
            new(new(command.InstanceId), new(command.AttemptId)),
            ObservationValue.FromObject(command.Input)),
        project: value => DecodeFulfillmentResult(value)))
    .Build(builder.Services, persistence);

app.MapServices(model);
```

This host-attachment sketch assumes a prepared `plan`, qualified `durableStart` dispatcher, protected
`retainedValues` repository, application authorization, and a pure result decoder. They are dependencies,
not services this API silently creates. Existing durable storage/scheduler adapters own receipt retention,
activation and recovery. The API layer neither compiles a process per request nor launches background tasks.
`ApiModel` and the linked pair implement `IApiDeclaration`, so registration reads their own definition.
The three-argument `Implement(catalog, definition, configure)` remains for a plain catalog whose contract is
owned separately; it is not required here.
The same API can be included in a larger `ApiDefinition.Combine` and implemented alongside entity/query
endpoints. Existing instance-owned API catalogs can include `api.Start` and `api.Result`; the pair binding
checks both original operations belong to the contributed definition. Separately declared services remain supported.
`MapServiceAcceptedProcess` is available for hosts that use native route registration directly.

## Observable protocol

For a command with instance `order-42`:

1. POST admits the exact process through the native dispatcher. A new or replayed admission returns HTTP 202,
   a receipt containing the native `ProcessStartResult`, and `Location: /orders/fulfillment/b3JkZXItNDI`.
   The receipt repeats that local URL as `resultLocation`; it includes no process input or protected trace.
2. GET of that URL invokes only the protected result reader. Pending returns 202 with the existing
   `services.process.inProgress` problem. Completed returns the declared terminal view with HTTP 200.
3. A denied read returns 403 before storage access; missing or differently pinned execution returns 404.
   Retained unsuccessful execution uses the existing process-result failure classification (for example 422);
   missing terminal evidence returns 500. A 202 receipt does not imply those failures cannot happen.
4. A rejected start conflict retains its native conflict body and does not emit Location. Lost responses or
   cancellation can leave admission ambiguous. The adapter does not retry; callers retain the exact command,
   idempotency, continuation and input across a deliberate retry. Receipt replay is not process re-execution.

Start receipts and terminal results have different declared schemas. Pending polling is an API problem,
not a second admission receipt. Hosts may use ordinary API/OpenAPI tooling to inspect all alternatives.
The local polling route has exactly one `{instanceId}` parameter; instance IDs use reversible UTF-8/base64url tokens and PathBase is
preserved. This avoids routing ambiguity for IDs containing slashes or literal percent escapes; clients follow
the receipt URL rather than constructing it. Malformed tokens fail before protected reads. The URL conveys no authority and never embeds a credential.

## Evidence and limits

`ServiceAcceptedProcessTests` exercises actual HTTP routing, unified model registration, accepted/replayed
native receipts, conflicting retry input, pending/completed/failed/missing results, independent read denial,
lost response after native receipt commit, exact runtime selection, invalid result routes and OpenAPI linkage.
The tests use the existing in-memory native admission reference adapter and controlled retained values;
they prove composition and protocol behavior, **not crash durability or production worker readiness**.
Existing service/runtime/provider suites retain responsibility for durable storage and activation guarantees.

The running Aspire-first fulfillment demo uses Host lifetime: one bounded generic-host worker over native
in-memory admission and checkpoint execution. It reuses FulfillmentService and includes start/result in OrderApi.
It survives request disconnects but loses execution history on host exit. PostgreSQL entity receipts have their
own lifetime. No external scheduler, cloud resource or deployment is activated by this change.
SSE, SignalR, callbacks, cancellation APIs and event-authoritative state are separate capabilities.

## Response timing versus execution lifetime

The service admits work using `ReturnAfterAdmission(ProcessExecutionLifetime.Host)` or
`ReturnAfterDurableAdmission()`. `ServiceProcessBinding` checks the provider lifetime against that declaration.
Invocation-local `ExecuteEphemerally(timeout)` remains a separate terminal-only execution profile, whose
deadline requests cancellation; it cannot silently promise background continuation.

The linked API independently selects `ServiceProcessResponse.Acknowledge` (default) or
`ServiceProcessResponse.WaitForResult(maximumWait)`. Waiting adds the result endpoint's terminal alternatives
to the start API, while preserving the primary receipt contract and 202 fallback. Location is present after
successful admission even if result reading fails or returns a terminal value. Result authorization is performed
independently before waiting/disclosure. Expiration returns the original receipt; disconnect only cancels the
HTTP wait. The process is never redispatched by polling or a wait timeout.

`BindHosted` records complete offline API coverage and prepares execution only during model Build. It delegates
to the shared native process runtime, not a second orchestration interpretation. Retention is bounded by instance
count, includes completed instances, and never evicts retry evidence implicitly. One sequential worker runs
finite supported graphs; there is no timer driver, retry loop, distributed coordination or recovery after exit.
Tests hold a native query open to prove admission precedes completion, caller cancellation is detached,
waiting expires without cancelling work, replay does not execute twice, and a new worker has no old results.
The actual PostgreSQL demo HTTP test follows the linked receipt to the committed fulfillment result.
