# Cohesive.Simulation.ExternalProcess

`Cohesive.Simulation.ExternalProcess` imports a bounded, deterministic snapshot from an executable into a portable
`GenerationCatalogDocument`. It is the language-neutral process boundary intended for providers implemented in Python
or another runtime.

The executable is transient. Once import succeeds, exact values and provenance in the returned catalog are the only
generation authority; replay and provisioning do not invoke the process again.

## Import

```csharp
using System.Text.Json;
using Cohesive.Simulation.ExternalProcess;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

var profile = new GenerationCatalogCapabilityProfile(
    id: "example.python-provider/catalog-snapshot/v1",
    capabilities:
    [
        GenerationCatalogProducerCapability.FiniteSnapshot,
        GenerationCatalogProducerCapability.StructuredValues,
        GenerationCatalogProducerCapability.LocaleSelection,
        GenerationCatalogProducerCapability.LocalSeed,
        GenerationCatalogProducerCapability.FixedUtcDateTimeReference
    ],
    sourceReferences:
    [
        SourceReference.Repository(new("tools/python-provider.py")),
        SourceReference.Create("pypi", "example-provider/1.2.3")
    ]);

var provider = new ExternalGenerationCatalogProvider(
    executable: "python3",
    arguments: ["tools/python-provider.py"],
    provider: "example-provider",
    providerVersion: "1.2.3",
    randomAlgorithm: "example-provider/local-seed/v1",
    capabilityProfile: profile);

var options = new ExternalGenerationCatalogImportOptions(
    id: "catalog/demo-people",
    revision: "r1",
    count: 100,
    seed: 1729,
    configuration: JsonSerializer.SerializeToElement(
        new PersonProviderConfiguration("person"),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    sourceReferences: [SourceReference.Repository(new("tools/python-provider.py"))],
    locale: "en",
    dateTimeReferenceUtc: DateTimeOffset.UnixEpoch);

GenerationCatalogDocument catalog =
    await ExternalGenerationCatalogImporter.ImportAsync<Person>(provider, options);

public sealed record PersonProviderConfiguration(string Generator);
public sealed record Person(string Name, string Email);
```

The executable and arguments are passed directly to the platform process API; no command shell interprets them. The
child inherits the caller's environment. Use an explicit executable path, working directory, wrapper, or virtual
environment when environment reproducibility matters.

## Protocol

The process reads one UTF-8 JSON request from standard input and writes one UTF-8 JSON response to standard output.
Standard output must contain only the response. Human-readable diagnostics belong on standard error.

The current request schema is `cohesive-simulation-generation-catalog-provider/v1`. For example, a string-valued
provider request has this shape:

```json
{
  "catalogId": "catalog/demo-people",
  "catalogRevision": "r1",
  "configuration": { "generator": "given-name" },
  "count": 100,
  "dateTimeReferenceUtc": "1970-01-01T00:00:00+00:00",
  "locale": "en",
  "requestId": "csimcatalogrequest1_<sha256>",
  "schemaVersion": "cohesive-simulation-generation-catalog-provider/v1",
  "seed": "1729",
  "valueType": { "$type": "scalar", "format": "None", "kind": "String" }
}
```

`valueType` is Cohesive's portable type contract and may use a different closed form for the requested CLR type. The
seed is a canonical decimal JSON string so every signed 64-bit value survives runtimes whose JSON number type cannot
represent it exactly. `configuration` is an opaque JSON object owned by the provider-specific adapter or script.

The process replies using the same schema identity:

```json
{
  "provider": "example-provider",
  "providerVersion": "1.2.3",
  "requestId": "csimcatalogrequest1_<sha256>",
  "schemaVersion": "cohesive-simulation-generation-catalog-provider/v1",
  "values": [
    "Ada"
  ]
}
```

The process must echo `requestId`, report the configured provider identity and exact pinned provider version, and
return exactly `count` values in sequence order. The importer assigns stable `sample/00000000` entry identities and
validates every value against `valueType` before producing a catalog.

The final catalog automatically retains the request identity as a `csimcatalogrequest://` source reference. Because
that identity fingerprints catalog coordinates, seed, locale, reference time, value contract, and configuration, an
input change remains visible in catalog provenance even when it happens to produce the same values. The opaque
configuration itself is not embedded, which avoids copying possible secrets into a durable catalog; application source
references should identify the configuration or script that can reproduce and explain it.

`ExternalGenerationCatalogProtocol` exposes the same strict serializer and parser to .NET provider implementations.
Other languages should preserve the documented field meanings and emit ordinary finite JSON values.

## Failure and containment

`ExternalGenerationCatalogProvider` bounds request/response bytes, retained standard error, and wall-clock duration.
Cancellation and timeout terminate the complete child process tree. Responses fail closed when they are malformed,
contain unknown or duplicate properties, exceed the bound, use a different request or provider identity, return the
wrong number of values, or violate the requested portable value contract.

`ExternalGenerationCatalogException.Failure` provides a stable failure classification. Standard error is retained only
up to the configured diagnostic bound and reports whether it was truncated.

Capability claims are explicit evidence, not runtime discovery. The importer verifies that locale, local-seed, and
fixed-reference-time coordinates agree with the supplied profile. Provider-specific adapters should own a versioned
profile and conformance tests instead of asking each application to invent those assertions.
