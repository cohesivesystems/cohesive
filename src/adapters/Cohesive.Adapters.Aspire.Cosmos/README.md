# Cohesive.Adapters.Aspire.Cosmos

Optional association between native Aspire Cosmos containers and Cohesive storage evidence.
It depends on Aspire Azure Cosmos hosting; the general Cosmos adapter does not.

Use `associations.Resource(node, container, storageBinding)` inside `builder.AttachCohesive`.
The canonical infrastructure node must contain `ComposedQueryResourceEvidence` from storage
preparation. The association checks the account resource name, physical database/container
names, single partition path and exact prepared source identity before attaching evidence.
Native provider options and resource construction remain ordinary Aspire calls.

The account name here is the logical Aspire resource identity used by the storage binding,
not a live Azure account lookup. Validation is offline: it starts no emulator, reads no cloud
metadata, and does not establish deployed readiness or a cross-store snapshot. Runtime source
acquisition retains its independent admission checks. Tests in the Aspire suite cover valid
mixed placement plus mismatched account, database, container, partition and prepared source.
