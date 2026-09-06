# Cohesive.Adapters.Bogus

`Cohesive.Adapters.Bogus` imports finite, coherent Bogus samples into strict
`GenerationCatalogDocument` values. Bogus is an authoring/import dependency; it does not become part of canonical
generation IR or remain necessary for interpretation, replay, provisioning, or verification.

```bash
dotnet add package Cohesive.Adapters.Bogus --prerelease
```

```csharp
using Cohesive.Adapters.Bogus;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

var options = new BogusGenerationCatalogImportOptions(
    id: "catalog/customer-profiles",
    revision: "r1",
    count: 250,
    seed: 8675309,
    sourceReferences:
    [
        SourceReference.Repository(new("tests/CustomerProfileCatalog.cs"))
    ],
    locale: "en");

GenerationCatalogDocument profiles = BogusGenerationCatalog.Import(
    options,
    faker =>
    {
        var givenName = faker.Name.FirstName();
        var familyName = faker.Name.LastName();
        return new CustomerProfile(
            givenName,
            familyName,
            faker.Internet.Email(givenName, familyName));
    });

await File.WriteAllTextAsync(
    "person-profiles.catalog.json",
    GenerationCatalogJsonSerializer.Serialize(profiles));

public sealed record CustomerProfile(string GivenName, string FamilyName, string Email);
```

At a script or CI boundary, `cohesive-sim catalog verify --catalog person-profiles.catalog.json` validates the exact
retained document and emits structured identity and provenance evidence without loading Bogus.

Use the returned document through `Gen.Catalog<CustomerProfile>(profiles)`, commonly as a `SampleRecord` source when
multiple generated entity fields must remain correlated.

## What Cohesive adds beyond Bogus

Bogus remains the source of realistic, domain-shaped sample values. This adapter turns those transient samples into a
portable Cohesive generation asset that can participate in the rest of the simulation and assurance model:

- a strict, provider-neutral `GenerationCatalogDocument` that can be persisted, inspected, compared, and consumed
  without loading Bogus;
- exact provenance and fingerprints covering the sampled values, adapter and provider versions, locale, seed, profile,
  and application sources;
- deterministic, addressable selection through Cohesive generation entropy, including replay and shrinking behavior;
- composition with typed generators, correlated records, high-level worlds and scenarios, provisioning, JSONL
  artifacts, and verification workflows used by unit, script, and end-to-end tests.

Bogus alone is usually the simpler choice for a one-off fake created and consumed in one process. Use this adapter when
the generated data must become a durable, reviewable, reproducible input to tests, demo environments, or agentic
assurance. The finite catalog is the semantic boundary: Bogus and the callback produce it, while its materialized values
and provenance—not the callback or a future provider invocation—are the retained authority.

## Capability and determinism profile

`BogusGenerationCatalog.CapabilityProfile` is the exact retained import contract;
`BogusGenerationCatalog.CapabilityProfileIdentity` is its stable versioned identity. The current profile:

- materializes a finite sample with equally weighted stable entry identities;
- creates a fresh `Faker` for every import and uses `Randomizer(seed)` rather than global Bogus random state;
- applies one explicit locale to the complete import;
- fixes `Faker.DateTimeReference` to `DateTime.UnixEpoch` so date providers do not read wall-clock time;
- retains normalized capability assertions with the adapter README and exact adapter/Bogus packages as profile-level
  evidence;
- records the distinct adapter and provider identities and versions, locale, local seed, and caller sources in catalog
  provenance.

Profile evidence and import evidence are intentionally separate. The profile sources support claims about the adapter
contract; `BogusGenerationCatalogImportOptions.SourceReferences` identify the application callback or specification
that produced this particular catalog. Both are embedded in and fingerprinted with the returned document.

Repeated values remain separate equally weighted entries, preserving their observed frequency in the finite sample.

Bogus warns that provider upgrades can change seeded output. The adapter therefore records its exact Bogus package
version and immediately fingerprints the materialized values. Once retained, those values—not a claim that a future
Bogus invocation will recreate them—are authoritative.

The callback is intentionally transient, but it is still application code. For reproducible imports, use only the
supplied faker and deterministic application state, and include the callback's repository or specification source in
`sourceReferences`. Network calls, `DateTime.Now`, `Guid.NewGuid()`, process-global random state, and mutable ambient
services can make a repeated import differ; the resulting retained catalog remains deterministic after each import.
