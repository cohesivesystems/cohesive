# Cohesive.Simulation alpha compatibility

The package family currently targets .NET 10 and is published as a pre-1.0 alpha. Public C# APIs and portable wire
formats may change between alpha versions, but every persisted contract is explicitly versioned and fails closed.

## Current contract identities

| Contract | Current identity |
| --- | --- |
| Generation catalog document | `cohesive-simulation-generation-catalog/v2` |
| External catalog-provider exchange | `cohesive-simulation-generation-catalog-provider/v1` |
| Mimesis record-provider configuration | `cohesive-simulation-mimesis-record/v1` |
| Generation document | `cohesive-simulation-generation/v4` |
| Core world document | `cohesive-simulation-world/v6` |
| Relationship world document | `cohesive-simulation-relations-world/v3` |
| World artifact manifest | `cohesive-simulation-world-artifact-manifest/v5` |
| JSONL world item | `cohesive-simulation-world-item/v5` |
| Reference interpreter | `cohesive-simulation-reference/v3` |
| Relationship interpreter | `cohesive-simulation-relations-reference/v1` |
| Generation replay token | `csimr2.` |
| Property-case replay token | `csimpc1.` |
| Relationship-world replay token | `csimwr1.` |
| CLI verification report | `cohesive-simulation-cli-verification/v1` |
| CLI catalog-verification report | `cohesive-simulation-cli-catalog-verification/v1` |

These values are diagnostic references. Application code should use the corresponding public constants where they
exist rather than copying strings.

## Upgrade behavior

- Deserializers reject unknown schemas, unknown or duplicate properties, noncanonical ordering, invalid content, and
  mismatched fingerprints.
- Interpreters reject unsupported interpreter and entropy identities before a provisioning sink receives a batch.
- Replay tokens reject definitions or shrinkers that do not exactly match their retained coordinates.
- The JSONL verifier rejects incomplete, extra, reordered, or tampered records and exposes no trusted generated item
  before full-stream success.
- No alpha migration or type forwarding is implied unless a release explicitly documents it.

Retain the package version beside long-lived simulation artifacts. Before upgrading, regenerate disposable fixtures
from source definitions. For artifacts that must remain replayable, keep the original package/tool version available
or introduce an explicit migration interpreter; do not rewrite fingerprints or schema fields by hand.

## Release checklist for consumers

1. Pin one prerelease package version across the Simulation package family.
2. Retain canonical source documents and manifests in addition to generated observations.
3. Run `cohesive-sim catalog verify` for retained provider catalogs and `cohesive-sim verify` or the library verifier
   before importing cross-process JSONL.
4. Preserve replay tokens and structured diagnostics in test reports.
5. Review release notes for schema, interpreter, entropy, identity, and shrinker changes before upgrading.

Virtual-time scenarios, runtime activity, queue/resource models, fault injection, additional external-provider
adapters, and learned synthesis are outside this static-world alpha and therefore have no compatibility contract yet.
Retained finite provider/catalog snapshots do have the generation-catalog contract listed above.
