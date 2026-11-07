# Develop Orders with semantic MCP tools

This host exports the actual `Orders.Model` used by the Aspire workers. It exposes the existing MCP tools with
project-specific guidance; it does not start workers, databases, containers or Aspire. Authoring stays in
`Orders.Model/FulfillmentDomain.cs` and lowering stays in its existing transition/process builders.

## Build, export and connect

From the repository root, with .NET 10:

```sh
dotnet run --project eng/examples/aspire-first/Development -- export "$PWD"
```

This builds the host and publishes `tmp/orders-development.json`. Repeat this command after source edits;
`--no-build` would export stale compiled authoring code. Normal incremental compilation is supported.
The host can start before the bundle exists: `learn` works, while `workspace` reports the missing artifact.

For Codex, either merge [codex.toml.example](codex.toml.example) into your configuration with the checkout's
absolute path, or run the following **once** from the repository root:

```sh
codex mcp add cohesive_orders -- dotnet \
  "$PWD/eng/examples/aspire-first/Development/bin/Debug/net10.0/Orders.Development.dll" \
  serve "$PWD"
codex mcp list
```

Codex supports user configuration in `~/.codex/config.toml` and project configuration in `.codex/config.toml`
for trusted projects. See the [official MCP configuration instructions](https://developers.openai.com/codex/mcp).
The CLI command registers this checkout's absolute path; point it at the intended worktree, and update it if that
worktree moves. This repository ships configuration without rewriting anyone's personal client settings.
Start a new client session after configuration and confirm the nine tools appear. Initialization instructions
explain the cross-tool workflow; tool descriptions/schemas explain arguments; `learn` supplies this project's
export command and scope. Clients may prefix tool names with `cohesive_orders`.

Other stdio MCP clients can use the same `dotnet` command and three arguments from the template.
Launch the compiled DLL directly: build output must never share the server's stdout protocol stream.

## One agent task

Ask an agent:

> Inspect the Orders stock-reservation rule using the Cohesive tools. Propose a change that keeps at least one
> unit in stock, identify the owning source and downstream process changes, then rebuild, export, validate and
> compare. Explain which guarantees the tools establish and which still need tests or review.

The model currently allows a reservation when `Available >= Quantity`. An illustrative proposal changes that
guard to `Available > Quantity`. This is a **policy change requiring review**, not a correction to the current
application. The repeatable exercise below applies it only in a disposable copy.

1. Call `learn({})`, then `workspace({})`; save `snapshotId` and check `changedInputs`.
2. `resolve({"query":"fulfillment/reserve","expectedSnapshot":"<before>"})` identifies the transition.
3. `inspect({"id":"fulfillment/reserve","expectedSnapshot":"<before>","pointer":"/definition"})`
   returns the canonical declaration and `sourceMappings` containing structured `location` values:
   repository-relative `path`, one-based `startLine`, and inclusive `endLine`. Narrow inspection to
   `/definition/preconditions/1` for the stock guard. The current producers identify declaration lines;
   they do not claim full expression character spans.
4. Call `references({"id":"fulfillment/reserve","expectedSnapshot":"<before>"})` to discover
   `fulfillment/submit` as a direct consumer. Inspect that returned identity to locate its owning declaration.
   Edit the typed builder and bump the transition and consuming process revisions. Review coverage:
   `available: null` means a referenced target is missing; `isExactMatch: false` with an available target
   requires review of fingerprint and optional revision evidence; unknown revisions are not stale. `incompleteDefinitions` lists unknown or extension-bearing documents.
5. Before re-export, `workspace` reports the changed source while retaining the old bundle's snapshot.
6. Rebuild/export with the command above, then call `workspace` for the new snapshot and `check` with it.
   Reusing the old expected snapshot fails explicitly.
7. `compare({"baselineSnapshot":"<before>","expectedSnapshot":"<after>"})` reports the changed reservation
   transition and the process that captured its exact reference. Compare observes these rebuilt documents;
   use `impact` before editing for bounded potential consumer paths; neither tool proves compatibility or runtime behavior.

Review `isValid`, `domainValidationAvailable`, diagnostics, `changedInputs` and scope together. Successful
validation is evidence about these documents, not proof of the proposed business policy or runtime behavior.

## Repeatable isolated exercise

```sh
dotnet run --project eng/examples/aspire-first/Development -- exercise "$PWD"
```

The exercise copies tracked files from the current working tree to a temporary directory, including local edits,
and adds this host's source files if it is not yet committed. Other untracked authoring files are not included;
add them to Git before using this exercise. Missing tracked files and symbolic-link files fail explicitly.
It never changes the original model. It builds/exports the real Orders project six times and uses the official
MCP SDK client with one retained server session to verify:

- Tool discovery, project guidance, fresh execution, native query and endpoint documents, source attribution and domain validation.
- The stock guard edit is stale before export; an old snapshot is rejected after export.
- The transition change propagates through the process, service and endpoint projections predicted by `impact`.
- An Orders query filter change updates the native query and its actual endpoint binding projection.
- A subsequent comment-only edit changes the input revision but not semantic fingerprints.

It removes the temporary copy and writes detailed evidence to `tmp/orders-development-exercise.json`. Source-map
line numbers in that report describe the isolated copy. The ten-minute bound includes restore/build; package restore
may require network access. Failures produce a nonzero exit and do not publish success evidence.

## Replay the authoring exercise

From a checkout containing the pre-feature commit and the authored source, run:

```sh
dotnet run --project eng/examples/aspire-first/Development -- exercise "$PWD" 89bec9bb
```

The optional final argument identifies a commit whose Orders query and API source predate the SKU endpoint. In the disposable copy, the exercise builds those two source files with the current host, calls `learn`, captures the baseline, then restores the authored files and rebuilds. It verifies that `compare` reports the query and endpoint as additions, that `references` links the endpoint to the query fingerprint, and that `compatibility` reports Compatible for an existing client and Breaking for a proposed client calling the old server. The JSON report retains both verdicts. The default exercise still checks the new binding with a shallow checkout.

## Coverage and next gaps

The export includes four transitions, one generated process, its service, three native queries, entity/shape and relationship documents, and portable API contracts. Their domain-owned IDs, explicit revisions,
fingerprints and compiler source maps remain intact; the export's input revision does not overwrite them.

Input discovery conservatively covers C# build files under `src/Cohesive*`, `src/adapters`, `Orders.Model`, and this host plus root props,
NuGet configuration and the embedded guide. This is broader than necessary and can mark unrelated edits stale. It does not prove
build dependency completeness or attest SDK/package-cache/environment state. New input files are discovered
at re-export, not during freshness checks.

The portable API document retains HTTP bindings, result variants, serializer profile, qualified core types and execution/service references. Use `compatibility` with an endpoint ID, retained baseline snapshot and fresh expected snapshot. The default direction is existing client to proposed server; the reverse direction tests the proposed client against the existing server. Under `http-json-tolerant-readers/v1`, requests must remain acceptable to the server and responses readable by the client. Findings identify wire paths and return Compatible, Breaking or Unknown. Custom serializer behavior, authorization changes and unsupported type semantics can prevent a proof. This policy does not qualify arbitrary clients, deployed routes, or runtime behavior.

For the response exercise, resolve `AvailableStock` and use `impact` on the qualified field identity. The exercise follows the field through the Orders query to `api:Order.Details`, then rebuilds twice. Adding optional `inventoryPartition` preserves the old reader; renaming required `availableStock` to `remainingStock` breaks it. The generated JSON writer and original generated reader provide a separate check on both classifications. Expression-level dependencies and target realization remain partial.

For the query exercise, inspect `fulfillment/order-details`, call `references` to resolve the bound `api:` endpoint,
and inspect that returned ID. Source is `Orders.Model/FulfillmentQueries.cs`; bindings are in `Orders.Model/OrderApi.cs`.
The disposable edit adds `order.Status == OrderStatus.Submitted` to the query filter. After rebuilding, `compare`
checks that only that query and the bound endpoint projection changed. This is a proposed policy change, not a fix.

`impact({"id":"fulfillment/reserve","expectedSnapshot":"<before>","maxDepth":4,"limit":20})` traces
transition → process → service → endpoint. Each result contains one shortest path, including the reference evidence
at every hop. Review both truncation flags and coverage gaps; neither absence nor reachability proves runtime behavior.

The Orders project maps its repository root to the compiler virtual path `/_` through `PathMap`. Both typed
transition authoring and generated process authoring preserve structured line locations. Other producers that
have no portable mapping continue to return references/descriptions with no location. The workspace never guesses
from a filename description. These optional metadata fields do not change semantic fingerprints; older strict
readers need updating before they can read documents containing the new fields.

## Qualification

The repeatable exercise uses the official SDK over stdio, checks stale-source reporting and old-snapshot rejection,
validates the mixed documents with explicit coverage, compares transition and query changes, and verifies comment-only
equivalence. It runs offline in CI. No live infrastructure, API invocation, or generated client is exercised. Assertions
fail when a source-edit recipe no longer matches exactly, so qualification evolves with the owning model.

## Cancellation authoring qualification

The cancellation recipe is returned by `learn`. The authored command accepts a `ServiceProcessCommand<CancelOrder>` at `POST /fulfillment/cancellations` and returns a protected polling location. Use the same command, instance and attempt identities when retrying. A persisted begin-transition receipt gives the original occurrence exclusive ownership of stock release. A new occurrence gets `InProgress` while cancellation is unfinished and `AlreadyCancelled` after completion.

This recipe supports the one-item fulfillment flow. It requires exactly one reservation whose identity equals the order identity, and rejects unsupported reservation sets before any write. Read projections expose `Released`. Recovery tests recreate process preparation after injected failures before release, after release and after marking the reservation, and verify the stock total.

Apply the explicit reservation migration before starting an existing database:

```sh
psql "$ORDERS_TEST_DATABASE_URL" -f eng/examples/aspire-first/migrations/002-reservation-cancellation.sql
```

With a disposable PostgreSQL test database configured:

```sh
dotnet test src/Cohesive.Adapters.Aspire.Tests --filter FullyQualifiedName~CancellationIntegrationTests
```

Set `COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING` for this command and confirm tests execute. To replay source authoring through one MCP session, retain the pre-cancellation Git commit and run:

```sh
dotnet run --project eng/examples/aspire-first/Development -- cancellation-exercise "$PWD" a2461a5098bd337977af8540ff233f7753501d36
```

The replay builds an isolated baseline tree, calls `learn`, applies the tracked working source, rebuilds and exports. It saves validation, dependency paths and directional contract findings to `tmp/orders-cancellation-exercise.json`. Both new endpoints must be Compatible for existing clients and Breaking for proposed clients against the old server. Existing Order responses gain lifecycle enum values, for which the current policy reports Unknown in both directions. Clients consuming those states need an explicit enum-evolution review.

The replay records all eight existing endpoints as well as the two additions. Against the pre-cancellation baseline:

| Contract | Existing client → proposed server | Proposed client → existing server |
| --- | --- | --- |
| Order.Create, Order.availability | Compatible | Compatible |
| Order.Get, Order.Submit | Unknown (enum) | Unknown (enum) |
| Order.Details | Unknown (enum) | Breaking (required Released field), plus enum uncertainty |
| Order.ReservationsBySku | Compatible | Breaking (required Released field) |
| Fulfillment admission and polling | Unknown (structural wire coverage) | Unknown (structural wire coverage) |
| Cancellation admission and polling | Compatible (addition) | Breaking (missing endpoint) |

A C# optional constructor argument is not a portable promise that an older server emits the new field.
The impact exercise requires an untruncated exact-reference path from inventory release to cancellation admission;
it also preserves incomplete-definition coverage in the report. Reachability is not a whole-system compatibility proof.
