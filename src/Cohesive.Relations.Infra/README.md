# Cohesive.Relations.Infra

Optional bridge from canonical Relations queries to provider-neutral Infra requirements. Relations and
Infra retain their independent dependency graphs; provider adapters reference this integration package.

`QuerySnapshotRequirement(id, revision, request)` validates the semantic request and retains the exact
compiled plan for adapter validation. Its stable capability ID contains the explicit identity/revision,
not a compiler fingerprint. Authors must revise semantic contracts deliberately. Prepared target evidence
retains exact compiler and binding fingerprints. The class is a preparation object; the resulting Infra
requirement and target evidence are the persisted artifacts, not this host-composition object.

The guarantee is execution of the selected query within one snapshot. It does not promise a snapshot
across multiple invocations, durable processing or live readiness. PostgreSQL's optional Infra adapter
implements it only for complete, unpaged native statement queries.
