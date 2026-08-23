# Cohesive.Adapters.DockerCompose

Deterministic Docker Compose projection of exact `Cohesive.Infra.Local` realization documents.

The adapter consumes a validated, fingerprinted local realization and emits reviewable Compose YAML plus a canonical
JSON provenance manifest. Compose service names, endpoint URIs, health commands, mounts, configs, and lifecycle metadata
are derived artifacts; the local Infra realization remains their semantic authority.

Compilation performs no Docker I/O. It emits pinned images, commands, environment and external-secret references,
loopback ports, named volumes, generated configs, ready dependencies, exact-status health checks and timing, graceful
termination, and a manifest retaining operations plus environment retention/isolation policy. Invalid local input,
unsupported value/probe semantics, invalid or colliding names, missing configuration, and non-representable duration
values produce structured diagnostics and no artifact.

The YAML fingerprint covers exact UTF-8/LF bytes. The manifest also fences the physical and local realizations, compiler
version, environment and lifecycle authority, attributed effective configuration, resource/name and endpoint mappings,
operation intent, optional maximum lifetime, and `InfrastructureLocalTargetDecision` evidence. Decisions use the same
target-neutral concern identities as other local lifecycle interpreters, making native, composed, constrained, and
override differences machine-comparable. Secret payloads are lifecycle inputs and never appear in either
canonical configuration or the manifest. A lifecycle runner must fence previews, execution, receipts, deadline
enforcement, and observations to the emitted artifact fingerprint.
