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

public sealed record CustomerProfile(string GivenName, string FamilyName, string Email);
```

Use the returned document through `Gen.Catalog<CustomerProfile>(profiles)`, commonly as a `SampleRecord` source when
multiple generated entity fields must remain correlated.

## Capability and determinism profile

`BogusGenerationCatalog.CapabilityProfileIdentity` identifies the exact import contract. The current profile:

- materializes a finite sample with equally weighted stable entry identities;
- creates a fresh `Faker` for every import and uses `Randomizer(seed)` rather than global Bogus random state;
- applies one explicit locale to the complete import;
- fixes `Faker.DateTimeReference` to `DateTime.UnixEpoch` so date providers do not read wall-clock time;
- records the adapter package, Bogus package, locale, local seed, profile documentation, and caller sources in catalog
  provenance.

Repeated values remain separate equally weighted entries, preserving their observed frequency in the finite sample.

Bogus warns that provider upgrades can change seeded output. The adapter therefore records its exact Bogus package
version and immediately fingerprints the materialized values. Once retained, those values—not a claim that a future
Bogus invocation will recreate them—are authoritative.

The callback is intentionally transient, but it is still application code. For reproducible imports, use only the
supplied faker and deterministic application state, and include the callback's repository or specification source in
`sourceReferences`. Network calls, `DateTime.Now`, `Guid.NewGuid()`, process-global random state, and mutable ambient
services can make a repeated import differ; the resulting retained catalog remains deterministic after each import.
