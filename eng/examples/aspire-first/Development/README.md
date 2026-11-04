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
Start a new client session after configuration and confirm the six tools appear. Initialization instructions
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
   returns the canonical declaration. At this root, source attribution is only the semantic identity.
   Inspect `/metadata/sourceMap` for `FulfillmentDomain.cs` filename/line descriptions, then inspect
   `/definition/preconditions/1` for the stock guard's specific source reference. Use source search to locate
   the file named above; the source map does not yet carry a structured repository-relative path.
   Follow child pointers if a value is too large.
4. Edit the owning builder. Bump the transition's explicit revision and the referencing process revision.
   Review `FulfillmentProcess` in source: the server does not yet provide reverse-reference search.
5. Before re-export, `workspace` reports the changed source while retaining the old bundle's snapshot.
6. Rebuild/export with the command above, then call `workspace` for the new snapshot and `check` with it.
   Reusing the old expected snapshot fails explicitly.
7. `compare({"baselineSnapshot":"<before>","expectedSnapshot":"<after>"})` reports the changed reservation
   transition and the process that captured its exact reference. Compare observes these rebuilt documents;
   it does not independently discover or prove all impacts.

Review `isValid`, `domainValidationAvailable`, diagnostics, `changedInputs` and scope together. Successful
validation is evidence about these documents, not proof of the proposed business policy or runtime behavior.

## Repeatable isolated exercise

```sh
dotnet run --project eng/examples/aspire-first/Development -- exercise "$PWD"
```

The exercise copies tracked files from the current working tree to a temporary directory, including local edits,
and adds this host's source files if it is not yet committed. Other untracked authoring files are not included;
add them to Git before using this exercise. Missing tracked files and symbolic-link files fail explicitly.
It never changes the original model. It builds/exports the real Orders project three times and uses the official
MCP SDK client with one retained server session to verify:

- Tool discovery, project guidance, five fresh definitions, source attribution and domain validation.
- The stock guard edit is stale before export; an old snapshot is rejected after export.
- Only `fulfillment/reserve` and its consuming `fulfillment/submit` process change fingerprints.
- A subsequent comment-only edit changes the input revision but not semantic fingerprints.

It removes the temporary copy and writes detailed evidence to `tmp/orders-development-exercise.json`. Source-map
line numbers in that report describe the isolated copy. The ten-minute bound includes restore/build; package restore
may require network access. Failures produce a nonzero exit and do not publish success evidence.

## Coverage and next gaps

The export includes four transitions and one generated process. Their domain-owned IDs, explicit revisions,
fingerprints and compiler source maps remain intact; the export's input revision does not overwrite them.

Input discovery conservatively covers C# build files under `src/Cohesive*`, `src/adapters`, `Orders.Model`, and this host plus root props,
NuGet configuration and the embedded guide. This is broader than necessary and can mark unrelated edits stale. It does not prove
build dependency completeness or attest SDK/package-cache/environment state. New input files are discovered
at re-export, not during freshness checks.

The existing queries use relation documents rather than the workspace's execution-document format. Queries,
API endpoints, entity graphs, generated clients, storage and target realization remain outside this export.
Use the existing [application explain workflow](../README.md#inspect-the-compiled-system-without-running-it)
and ordinary source search for them. The integration's next priorities are support for those domain-owned
artifacts and reverse-reference traversal; adding parallel query or dependency models here would hide the gap.
The observed source references are semantic identity/member/line strings. Filename/line descriptions live in
the full source map; this host does not invent missing structured repository paths or portable source spans.
Improving owner-supplied source locations is a prerequisite for reliable automated source navigation.

## Recorded qualification

On 2026-11-03, the SDK-driven exercise passed locally on .NET 10.0.13:

- All six tools were discovered; the project guide supplied the export workflow.
- All five execution documents passed available domain validation with no diagnostics.
- Changing the reserve guard from `>=` to `>` and bumping the authored reserve/process revisions yielded
  exactly two semantic differences: `fulfillment/reserve` and `fulfillment/submit`.
- The edited file appeared in `changedInputs` before export; the old snapshot was rejected afterward.
- A rebuilt comment-only edit changed input identity without changing semantic identity.
- Detailed guard attribution and filename/line descriptions were retrieved through `inspect`.

The application policy in the working checkout was not changed. No live infrastructure, API or generated client
was exercised. CI now runs this same offline exercise; its assertions intentionally fail if the illustrative
source-edit recipe no longer matches the owning model, so the qualification must evolve with that source.
