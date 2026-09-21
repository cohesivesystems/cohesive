# ASP.NET Azure runtime evidence producers

This optional adapter connects one explicitly reviewed ASP.NET `IHealthCheck` to the Azure runtime response contract. It references the existing Azure Infra boundary plus the shared ASP.NET framework. It does not pull Aspire.Hosting or Pulumi.Automation into application hosts. Product checks and identity policy remain in the application; generic status normalization, deployment admission, serialization and protected route composition live here.

## Concrete example

A Jobs host runs application build `build-42`. Its reviewed contract says `process-inbox-admission/v1` and names the existing Process deployment/inbox check. A matching host emits Ready when that exact check is Healthy, NotReady when it fails or degrades, and Unknown when the check is absent. A host actually running `build-41` cannot construct the producer. An unrelated healthy check cannot stand in for the missing named check. This is workload admission, not proof that a task executed or that Blob storage is accessible; child-resource obligations remain separate in the canonical evaluator.

## Authority and flow

Deployment tooling projects an `AzureRuntimeProducerArtifact` from the canonical realization, native binding artifact and independently reviewed runtime endpoint declarations. The artifact has schema `cohesive.azure-runtime-producer/1`; it contains no credentials. Deliver it through a reviewed configuration path with file access restricted to the application identity. `Read` caps input at four MiB and rejects duplicate/unmapped fields and unsupported versions. Construction/deserialization is not admission.

`CreateProducer` receives independently configured environment, tenant, subscription and handoff identity, application-code producer/check-contract identifiers, the host's actual immutable deployment identity, and the exact named product check. The declared resource and host identities must match before a route can be registered. A versioned complete check contract must describe the workload's own admission requirement; it must not represent a partial dependency check as full child-resource readiness.

Call `Map` with an explicit route and an explicit authorization policy. The host owns JWT/Entra setup, issuer/audience validation and caller authorization. The policy must require the intended authenticated inspection principal; do not supply an allow-all policy or add AllowAnonymous metadata. Standard ASP.NET authorization executes before the handler. Neither configuration nor route mapping resolves the health service or expensive check dependencies. Missing/invalid configuration should fail host startup when the feature is deliberately enabled; applications should leave the route disabled by default until qualification.

On an authorized request, `ObserveAsync` executes only the selected check. Work is serialized per producer instance, with cancellation-aware waiting, no result cache and no background polling. The source timestamp is taken before the check, so a slow check cannot manufacture freshness. Healthy maps to Healthy/Ready, Unhealthy to Unhealthy/NotReady, Degraded to Degraded/NotReady, and missing/unknown status to Unknown/Unknown. Health descriptions, arbitrary data and exceptions never enter the response. Response JSON uses the shared contract's exact naming independently of host-wide JSON settings, and responses are marked no-store.

Admission references are retained as a set. A response may already include its producer/check/deployment references; the consumer must not reject them simply because it adds the same checked references. Tests exercise the complete producer-to-consumer round trip, including unhealthy and degraded results.

## Qualification and limits

The host must independently verify delivery of the correct artifact, actual application revision and configuration association. The adapter does not authenticate the artifact, grant Entra access, provision endpoints or infer deployment identities from response payloads. It never reads secrets, reparses legacy health JSON or treats Azure availability as application admission. Route-level authorization, API/worker product contracts, deployment association and actual runtime capabilities must be qualified before claiming live readiness.

Tests use synthetic identities and the existing canonical Infra fixture. They cover exact identity mismatch, check filtering, absent checks, sanitized payloads, lazy protected mapping and cancellation. No live provider operations are part of these tests.
