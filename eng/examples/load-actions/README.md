# Load actions

A runnable console consumer of actor-specific transition discovery. It displays a load's available
actions, collects a complete command, refines the choices, and optionally executes one using the
inspected concurrency token. Action names, contracts and availability come from the service catalog;
the menu has no copy of machine guards or permission rules.

Run from the repository root:

```sh
dotnet run --project eng/examples/load-actions -- dispatcher
dotnet run --project eng/examples/load-actions -- dispatcher --missing-documents
dotnet run --project eng/examples/load-actions -- supervisor --execute cancel
dotnet run --project eng/examples/load-actions -- dispatcher --execute release
dotnet run --project eng/examples/load-actions -- viewer
```

A dispatcher sees release; a supervisor sees release and cancel; a viewer sees no actions.
Missing documents make release unavailable even for an authorized dispatcher. Without a complete
command, actions report RequiresInput. Execution rechecks authorization, state and the version token.

Each run starts a fresh in-memory load in Ready. The sample's explicit local role argument is a demo
identity resolver, not a production authentication mechanism. A host supplies trusted identity grants;
role names never enter the machine. `EntityTransitionAuthoring` shares contract configuration across
the typed enum discriminator, guard, entity and transition.

The source uses only existing repository projects; it requires no external service or new dependency.
For the API contract, see [transition discovery](../../../src/Cohesive.Api.Execution/Services/TRANSITION_DISCOVERY.md).

## Browser example and generated API

Install the repository's existing frontend dependencies once, then build and run from the repository root:

```sh
corepack pnpm install --frozen-lockfile
bash eng/examples/load-actions/build.sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build --project eng/examples/load-actions -- --serve --urls http://127.0.0.1:5189
```

Open <http://127.0.0.1:5189>. Select a role and load, select an action, enter a reason, check availability,
then submit. `LD-2041` has complete documents; `LD-2042` is missing documents. A supervisor can cancel either
load. Editing the reason, choosing another action, or changing the load or role invalidates the checked
version. The browser never infers permissions or machine guards. To see conflict recovery, check an action
in one tab, complete a competing action in another, then submit the first tab's stale action.

`LoadApplication` owns the shared entity, machine, transitions, service operations and API declarations.
Both the console and web host consume it. `LoadClientGeneration` projects those declarations through the
existing TypeScript shape and API emitters into `ui/generated`; these files are derived and must not be
edited. The HTTP endpoint adapters consume the same API projection. The small DOM view in `ui/app.ts`
uses generated contracts, availability classifications and API identities; `transport.ts` joins returned
actions to generated invocation functions. The page layout and command field rendering are a local sample
renderer, not a general Presentation compiler. The typed inspection endpoint currently requires one shared
command contract per entity and fails registration for unsupported input families.

The host exposes catalog/detail reads, typed action inspection, and service-projected invocation routes.
The role header is an explicit **Development-only demo identity resolver**. Native ASP.NET authorization
policies defer to runtime capability/scope/resource authorization. A production host must use trusted
identity enrichment and its actual endpoint policies instead. Storage is in memory; restarting resets the
loads. This source example is intended to run from its project build output, not as a published deployment.

Verify generated artifacts and exercise the actual server through the compiled TypeScript client:

```sh
dotnet run --project eng/examples/load-actions -- --check-generated
node node_modules/typescript/bin/tsc -p eng/examples/load-actions
node --test eng/examples/load-actions/test.mjs
```

CI runs these checks. The integration test starts its own server on an ephemeral loopback port and verifies
role filtering, required input, document guards, no-write inspection, unauthorized invocation, unknown
operation rejection, successful cancellation and stale-version conflict. The .NET tests additionally cover
projection contracts, HTTP serialization and shared generic naming across shape/client/mock generation.

The web host maps the command catalog with `MapServiceTransitions<Command, string>`, retaining the same
endpoint declarations used for code generation. Details use the existing `MapEntityApi<Load>` point-read
pipeline and its identity-aware `Get` projection. Loading, missing-entity responses and typed materialization
(including enum conversion) are framework-owned; only construction of `LoadDetails` is domain-specific.
The host's HTTP JSON naming profile matches the profile used to generate the client.
