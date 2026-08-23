# Cohesive.Adapters.Aspire

This adapter projects an exact `InfrastructureLocalRealizationDocument` into an inspectable Aspire resource graph. `Cohesive.Infra.Local` remains the semantic authority: the projection retains the exact physical-realization reference, local-realization fingerprint, environment policy, effective configuration attribution, canonical services, endpoints, health, readiness, operations, and target-specific decisions. Those decisions use the shared `InfrastructureLocalTargetDecision` evidence contract and target-neutral concern identities so differential conformance can compare Aspire with other lifecycle interpreters without inventing another capability catalog.

`AspireLocalCompiler.Compile` is pure and deterministic. It performs no Aspire, Docker, filesystem, network, or secret I/O. `AddCohesiveLocalInfrastructure` is the separate runtime application boundary that turns a successful projection into AppHost resources.

The current stable Aspire health API natively represents HTTP endpoint probes, but not container command probes or per-resource polling cadence. Command probes therefore fail closed unless compilation receives an exact `AspireCommandHealthOverride` with service, executable, arguments, replacement endpoint, rationale, and source references. Polling timing remains retained in the service projection and is declared as a constrained target decision rather than silently discarded.

Literal and effective-configuration-backed listener ports are resolved from the same canonical Infra port value used by service environment and endpoint declarations. This preserves services such as the Cosmos emulator whose internal listener and externally advertised port must move together in parallel worktrees.

Persistent local profiles use deterministic named container volumes, so ordinary AppHost stops do not remove data. Ephemeral isolated profiles use anonymous volumes and enforce the canonical maximum lifetime in the AppHost. Environment mutations remain lifecycle-controlled; read-only and application-mutation host operations are exposed as stable Aspire resource commands visible to both dashboard and API clients.

The harness AppHost keeps its resource service and dashboard on HTTPS while disabling automatic export of the host developer-certificate private key. DCP instead uses its supported ephemeral self-signed TLS identity, which avoids headless macOS Keychain export stalls without permitting unsecured transport.

No Aspire type is referenced by `Cohesive.Infra`.
