# Cohesive.Adapters.Aspire

Two starting points are supported:

- **Cohesive-first:** compile a canonical local realization and create its native model with `AddCohesiveLocalInfrastructure`.
- **Aspire-first:** keep an existing AppHost and use `AspireInfrastructureAssociation.Attach` to associate selected native objects with canonical requirements.

See the [runnable Aspire-first order example](../../../eng/examples/aspire-first/README.md).
`Attach` neither creates resources nor alters native options, references or waits. Its result
contains the original objects and the ordinary `InfrastructureTargetDeploymentPlan`; inspect
`Deployment.IsComplete` and `Deployment.Diagnostics` before starting the application.
Requirements and explicit implementation evidence use existing Cohesive.Infra authoring.
Evidence is not inferred from native CLR types. This association API is not a runtime
readiness check and does not use the local materializer's observation collector.

Native membership is captured once before authoring and checked by object identity, including a check after the synchronous
callback. Each canonical node may be associated once. Unmodeled native resources remain
allowed; missing modeled nodes remain compiler diagnostics. Builders freeze on success or
failure. The result is an invocation-local association snapshot, not a cache. Aspire retains
ownership of the referenced objects and can continue configuring them through its own API.

Physical IDs deterministically use `aspire/resource/{native.Name}`, scoped by the selected
deployment manifest. The manifest identity derives its facility/profile identities with
`/facilities` and `/profile`; the target is `aspire/native-association/v1`, and callers select
the environment variant explicitly. These are authoring conventions, not a second catalog.
Source maps retain caller attribution. Only the canonical manifest carries portable data.

This adapter projects an exact `InfrastructureLocalRealizationDocument` into an inspectable Aspire resource graph. `Cohesive.Infra.Local` remains the semantic authority: the projection retains the exact physical-realization reference, local-realization fingerprint, environment policy, effective configuration attribution, canonical services, endpoints, health, readiness, operations, and target-specific decisions. Those decisions use the shared `InfrastructureLocalTargetDecision` evidence contract and target-neutral concern identities so differential conformance can compare Aspire with other lifecycle interpreters without inventing another capability catalog.

`AspireLocalCompiler.Compile` is pure and deterministic. It performs no Aspire, Docker, filesystem, network, or secret I/O. `AddCohesiveLocalInfrastructure` is the separate runtime application boundary that turns a successful projection into AppHost resources.

Foreign-managed services become Aspire `ExternalServiceResource` instances only when every declared endpoint has a
concrete host-loopback address. The representative endpoint supplies the external-service URI; a representative
`Uri` environment value is bound through Aspire's resource reference, while secondary endpoints and alternate
encodings use their exact resolved host URIs. Missing host exposure or effective host-port configuration fails before
projection, so the adapter never substitutes an Aspire resource name for a service that lives outside Aspire's
network.

`AspireInfrastructureObservations.CaptureCurrent` is the runtime observation boundary. It reads Aspire's current `ResourceNotificationService` snapshots for the exact resources returned by `AddCohesiveLocalInfrastructure`, verifies their physical identity and projection fingerprints, and normalizes Aspire lifecycle and health evidence into canonical `InfrastructureResourceObservation` values. Services for which Aspire has not published an event remain absent so `InfrastructureReadinessEvaluator` can emit its provider-neutral missing-observation diagnostic. `AssessCurrent` composes the same capture with that evaluator after verifying the exact physical-realization fence; applications do not implement state normalization or readiness computation.

Aspire considers only a `Running` resource with `Healthy` aggregate health ready. `Degraded` and `Unhealthy` health, known non-running lifecycle states, unrecognized states, and identity-fence failures retain structured adapter diagnostics and exact Aspire/local-projection provenance. Health-report exception text is intentionally not copied into portable diagnostics.

The current stable Aspire health API natively represents HTTP endpoint probes, but not container command probes or per-resource polling cadence. Command probes therefore fail closed unless compilation receives an exact `AspireCommandHealthOverride` with service, executable, arguments, replacement endpoint, rationale, and source references. Polling timing remains retained in the service projection and is declared as a constrained target decision rather than silently discarded.

Literal and effective-configuration-backed listener ports are resolved from the same canonical Infra port value used by service environment and endpoint declarations. This preserves services such as the Cosmos emulator whose internal listener and externally advertised port must move together in parallel worktrees.

Persistent local profiles use deterministic named container volumes, so ordinary AppHost stops do not remove data. Ephemeral isolated profiles use anonymous volumes and enforce the canonical maximum lifetime in the AppHost. Environment mutations remain lifecycle-controlled; read-only and application-mutation host operations are exposed as stable Aspire resource commands visible to both dashboard and API clients.

The harness AppHost keeps its resource service and dashboard on HTTPS while disabling automatic export of the host developer-certificate private key. DCP instead uses its supported ephemeral self-signed TLS identity, which avoids headless macOS Keychain export stalls without permitting unsecured transport.

No Aspire type is referenced by `Cohesive.Infra`.

## Native project associations

A local project may declare only its stable `InfrastructureLocalProjectId` and launch profile.
At application construction, supply `AspireProjectAssociation.Create<Projects.MyApi>(project.Id)`
in `AspireLocalApplicationOptions.projects`. The adapter invokes native `AddProject<TProject>`;
Aspire's generated metadata owns the project path and launch metadata. No generated type, absolute
checkout path, or executable callback enters the canonical document or its fingerprint.

Associations must exactly cover path-free sources before any Aspire resource is added. Missing,
unused, duplicate, and path-overriding associations fail. Existing explicit repository-path sources
remain supported and use the operation working directory; a project with a declared path cannot
also receive a native association. Host-operation working directories remain explicit and independent.
Docker Compose continues to reject project services rather than inventing an execution model.

For example, renaming a solution or omitting cloud YAML from a local checkout no longer affects a
metadata-associated workload. Its project location follows the AppHost's `ProjectReference` instead
of a second repository-path declaration. Tests construct the native project from a temporary working
directory and verify metadata/Infra identity preservation and rejection before resource mutation.

`ProjectPath` is now nullable: source consumers must handle the native-association case. Existing
path-bearing JSON remains readable; path-free documents require a consumer supporting this contract.
