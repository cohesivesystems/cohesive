# Cohesive.Adapters.Mimesis

`Cohesive.Adapters.Mimesis` imports finite [Mimesis](https://mimesis.name/) record snapshots into portable
`GenerationCatalogDocument` authority. Typed CLR expressions make ordinary authoring convenient; the expressions,
Python process, and Mimesis objects are discarded after import. Exact generated values and their provenance are what
simulation, replay, scripts, and provisioning retain.

This package builds on `Cohesive.Simulation.ExternalProcess`. The lower-level package owns bounded process execution,
strict correlated messages, cancellation, and response validation. This package adds Mimesis-specific typed member
bindings, configuration validation, package-version checks, capability evidence, and the bundled Python provider.

## Install

The current alpha targets .NET 10, requires Python 3.10 or newer, and pins its Python runtime dependencies:

```bash
dotnet add package Cohesive.Adapters.Mimesis --prerelease
python -m pip install "mimesis==21.0.0" "typing_extensions==4.15.0"
```

Use a virtual environment and pass its Python executable when reproducible environment selection matters. The .NET
package never installs Python packages, creates an environment, or invokes a command shell. `typing_extensions` is
explicitly pinned because the Mimesis 21.0.0 wheel imports it without declaring it as package metadata.

## Import a typed record catalog

```csharp
using Cohesive.Model;
using Cohesive.Simulation.Generation;
using Cohesive.Adapters.Mimesis;

var people = MimesisGenerationCatalog.Define<Person>(person => person
    .Member(value => value.Name, "person.full_name")
    .Member(
        value => value.Email,
        "person.email",
        new { Domains = new[] { "example.com" } })
    .Member(
        value => value.Age,
        "numeric.integer_number",
        new { Start = 18, End = 80 }));

GenerationCatalogDocument catalog = await MimesisGenerationCatalog.ImportAsync(
    people,
    new(
        id: "catalog/demo-people",
        revision: "r1",
        count: 100,
        seed: 1729,
        locale: "en",
        sourceReferences:
        [
            SourceReference.Repository(new("tests/DemoPeople.cs"))
        ]),
    new(pythonExecutable: ".venv/bin/python"));

public sealed record Person(string Name, string Email, int Age);
```

The builder requires every non-optional direct CLR property to have exactly one binding. It respects
`JsonPropertyNameAttribute`, canonicalizes bindings independently of callback order, rejects duplicate members, and
requires fully qualified Mimesis field names such as `person.full_name`. Argument objects use web JSON naming;
Mimesis keyword names that contain underscores should be spelled with those underscores in the C# argument object or
supplied as an exact `JsonElement`.

## Authority and reproducibility

Import uses the application catalog identity and revision, CLR-derived portable value contract, complete canonical
Mimesis configuration, locale, and signed 64-bit seed to derive the external request identity. The resulting catalog
retains that request identity plus exact versions for the Cohesive importer, this package, Mimesis, and its required
runtime dependency. Any changed coordinate or output therefore changes retained provenance or the catalog fingerprint.

Mimesis 21.0.0 is accepted exactly; a different installed version fails before generation. A local `Field` seed owns
randomness for one import and does not modify global Mimesis random state. The capability profile deliberately does
not claim a fixed date-time reference: Mimesis fields whose defaults depend on the current date can produce a different
snapshot at a later import even with the same seed. Supply explicit provider arguments for time-relative fields when
reproducible re-import is required. Once imported, the retained catalog itself is exact and replay does not call
Mimesis again.

The current `cohesive-simulation-mimesis-record/v1` configuration supports direct object properties. Provider results
must normalize to portable JSON values. Strings, booleans, signed integers, finite Decimal-range numbers, UUIDs,
dates, times, date-times, lists, tuples, string-keyed objects, and enum values are supported; unsupported or
non-portable results fail closed. Mimesis date-times retain their provider-produced offset-less civil representation,
so target `DateTime` or `string`; an absolute `DateTimeOffset` contract requires a provider field that emits an
explicit offset. Nested CLR member paths and implicit provider-field lookup are intentionally outside this first
schema.
