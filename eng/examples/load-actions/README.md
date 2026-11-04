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
