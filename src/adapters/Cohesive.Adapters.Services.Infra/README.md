# Service infrastructure association

This adapter associates canonical service operations with a workload's existing infrastructure
consumer bindings. Service definitions own operation identity and behavior; Cohesive.Infra owns
workloads, resources, contracts and capability requirements. The adapter retains exact references
to both authorities and rejects incomplete operation coverage, unknown bindings and bindings
consumed by another workload. It copies and normalizes caller-owned selections.

An explicit empty binding set is allowed: placement validation cannot infer all infrastructure
requirements from an operation family. Callers must qualify declared dependencies against the
existing capability compiler and observed readiness. This initial boundary is not a readiness
certificate, authorization grant, deployment action or proof of runtime dependency completeness.
No runtime, repository or provider SDK is constructed during association.

For example, `publish` hosted by `api` may select `api-scheduler`; selecting a worker's
`worker-scheduler` is rejected even when both bindings target the same resource. The conformance
tests reside in `Cohesive.Infra.Tests/ServiceInfrastructureAssociationTests.cs`.

`ValidateCapabilityClosure` admits a report only for the exact associated infrastructure reference
and requires the entire deployment's native capability closure. It preserves native compiler
diagnostics, rejects unrelated reports even if closed, and does not reinterpret capability evidence.
This is static target qualification, not observed runtime readiness or proof that the caller selected
every dependency needed by an operation. The full Infra suite passes 120 tests with this boundary.

`CreateWithSharedPrerequisites` applies an explicit service-wide binding set to all canonical
operations. This removes application-owned operation enumeration when the deployment profile requires
identical prerequisites throughout the service. It copies selections, rejects duplicates and preserves
the same workload/binding validation as per-operation `Create`. Adding a result-read operation therefore
inherits the selected prerequisites without a second operation catalog. This is explicit placement
policy, not automatic dependency discovery; use `Create` for genuinely different per-operation needs.

`ValidateReadiness` consumes the native assessment already produced by a trusted provider pipeline
and an independently selected exact realization reference. It rejects topology or physical realization
mismatch, requires an explicit ready decision for the associated workload, preserves native diagnostics
and requires the whole assessment to be ready. It does not recollect or reevaluate the same observations
for each service. No service-specific readiness model is created. Selected bindings are not implicitly
converted into readiness edges. Scope, freshness and persisted-artifact integrity remain provider concerns;
this method does not admit arbitrary untrusted assessment documents. For example, an API observed ready
while its declared state dependency has no evidence remains unknown/not ready.
