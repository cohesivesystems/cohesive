# Adoption and agentic assurance

The Simulation package family is ready for bounded alpha adoption where deterministic data, replay, and inspectable
test evidence matter more than broad fake-data coverage. Start with `Cohesive.Simulation`, add only the interpretations
the application uses, and pin one exact prerelease version across the family.

## Choose the smallest surface

| Goal | Package or tool |
| --- | --- |
| Generate typed examples and bounded sequences in tests | `Cohesive.Simulation` |
| Run replayable property cases | `Cohesive.Simulation`, plus `Cohesive.Simulation.Xunit` only for xUnit reporting |
| Define populations, exemplars, artifacts, and scenarios | `Cohesive.Simulation` |
| Complete references between generated populations | `Cohesive.Simulation.Relations` |
| Seed generic Cohesive entity repositories | `Cohesive.Simulation.Storage` |
| Execute scenario actions through canonical Transitions | `Cohesive.Simulation.Transitions` |
| Provision and verify portable JSONL from scripts or Playwright | `Cohesive.Simulation.Cli` (`cohesive-sim`) |
| Snapshot Bogus or Mimesis data into a retained catalog | `Cohesive.Adapters.Bogus` or `Cohesive.Adapters.Mimesis` |
| Import a bounded catalog from another runtime | `Cohesive.Simulation.ExternalProcess` |

The core package has no dependency on Relations, Transitions, Storage, xUnit, the CLI, or provider integrations. The
[core package consumer](../../../eng/package-smoke/Cohesive.Simulation.Core.Consumer/Program.cs) is an executable
release check that installs only `Cohesive.Simulation` and exercises typed generation, property cases, world and
scenario serialization, state evolution, trace restoration, and final-world reconstruction.

## Keep one application-owned simulation module

A practical application layout keeps semantic source together and projects it into each test or seeding environment:

```text
test/Simulation/
  DemoShapes.cs       shared CLR shape authoring
  DemoGeneration.cs   member generators and retained provider catalogs
  DemoWorld.cs        populations, relationships, and named exemplars
  DemoScenario.cs     actors, operations, and scheduled actions
  SeedDemo/           application-owned repository or import policy
```

The typed C# builders are the reviewable authoring projection. Persist their canonical documents when another
process, a retained test result, or a long-lived environment needs authority independent of the compiled test
assembly. Do not make generated observations or hand-edited JSONL a second fixture model.

Use stable semantic names for definitions, populations, exemplars, operations, actors, and actions. An agent can then
change generation rules without rediscovering which arbitrary generated row a browser test intended to use.

## Build one assurance spine

For an end-to-end test or agent-authored change, use the same source through these interpretations:

1. Compile generation, world, relationship, Transition, and scenario definitions and require valid structured
   diagnostics.
2. Run focused generated examples or property cases in unit tests. Retain a failing replay token.
3. Create and retain a `WorldArtifactManifest` before data crosses a process or storage boundary.
4. Seed repositories through the application-owned .NET seeder, or provision and verify JSONL before invoking an
   application-owned importer.
5. Address browser fixtures through named exemplars rather than search order or unretained predicates.
6. Execute scheduled activity through a versioned interpreter identity and retain the scenario trace. For canonical
   Transitions, retain their exact definition documents beside the trace.
7. Attach the package version, manifest, verification report, trace, replay tokens, and destination receipt to the
   test or agent run.

This is deliberately a composition of existing semantic artifacts, not a second `AssuranceSuite` model. The source
definitions remain authoritative while generation, property checking, seeding, browser setup, scenario execution,
and verification are interpretations of them.

## Script and Playwright boundary

The portable command path is manifest-first:

```bash
cohesive-sim manifest \
  --world test/Simulation/demo.world.json \
  --seed 42 \
  --out test-results/demo.manifest.json

cohesive-sim provision \
  --manifest test-results/demo.manifest.json \
  --target playwright/demo-import \
  --out test-results/demo.jsonl

cohesive-sim verify \
  --manifest test-results/demo.manifest.json \
  --jsonl test-results/demo.jsonl \
  > test-results/demo.verification.json
```

`cohesive-sim` does not know application repositories, credentials, cleanup policy, or transaction guarantees. A
Playwright `globalSetup` can invoke these commands and then call the application-owned importer, or invoke a .NET
seeder that uses `Cohesive.Simulation.Storage` directly. See [repository seeding and Playwright](seeding-and-playwright.md)
for both paths.

## Alpha boundaries

The following constraints are explicit in the current packages:

- worlds describe static initial state; scenario actions evolve existing actor observations through complete,
  evidence-backed replacements;
- scheduled actions use deterministic virtual instants, not a continuous or wall-clock activity engine;
- actor creation, deletion, general event and queue semantics, resources, fault injection, and learned synthesis are
  not implemented;
- the Transition scenario interpreter supports one existing actor aggregate per binding and fails closed for subject
  creation, emission intents, and Machine movements;
- the generic repository sink performs deterministic upserts but does not claim durable exactly-once provisioning;
- Bogus, Mimesis, and external providers become finite retained catalogs rather than live portable dependencies; and
- alpha schema and API breaks fail closed and require either fixture regeneration or an explicit migration
  interpreter.

The complete package family is installed and exercised from packed NuGet artifacts by
[`eng/test-simulation-tool.sh`](../../../eng/test-simulation-tool.sh) in pull-request and release workflows. Current
wire identities and consumer upgrade rules are listed in [alpha compatibility](compatibility.md).
