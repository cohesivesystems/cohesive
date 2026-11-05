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
Start a new client session after configuration and confirm the eight tools appear. Initialization instructions
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
It never changes the original model. It builds/exports the real Orders project four times and uses the official
MCP SDK client with one retained server session to verify:

- Tool discovery, project guidance, fresh execution, native query and endpoint documents, source attribution and domain validation.
- The stock guard edit is stale before export; an old snapshot is rejected after export.
- The transition change propagates through the process, service and endpoint projections predicted by `impact`.
- An Orders query filter change updates the native query and its actual endpoint binding projection.
- A subsequent comment-only edit changes the input revision but not semantic fingerprints.

It removes the temporary copy and writes detailed evidence to `tmp/orders-development-exercise.json`. Source-map
line numbers in that report describe the isolated copy. The ten-minute bound includes restore/build; package restore
may require network access. Failures produce a nonzero exit and do not publish success evidence.

## Coverage and next gaps

The export includes four transitions, one generated process, its service, two native queries and endpoint projections. Their domain-owned IDs, explicit revisions,
fingerprints and compiler source maps remain intact; the export's input revision does not overwrite them.

Input discovery conservatively covers C# build files under `src/Cohesive*`, `src/adapters`, `Orders.Model`, and this host plus root props,
NuGet configuration and the embedded guide. This is broader than necessary and can mark unrelated edits stale. It does not prove
build dependency completeness or attest SDK/package-cache/environment state. New input files are discovered
at re-export, not during freshness checks.

Native query declarations retain the Relations serializer and fingerprint authority. Endpoint documents reuse
`ApplicationEndpointExplanation`, preserving route, response behavior, exact execution/service binding and attributed
references. Their fingerprint scope is explicitly an inspection projection; CLR request/response types, callbacks,
generated clients and full API compatibility are not represented. Relation/entity/shape dependency indexing and target
realization remain incomplete, explicitly reported by `incompleteDefinitions`. Endpoint checks report integrity success
with `domainValidationAvailable=false`. No query revision is synthesized to satisfy an execution reference.

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
