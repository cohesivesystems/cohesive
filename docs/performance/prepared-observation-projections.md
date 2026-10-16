# Prepared hosted observation projections

A hosted Query used to admit its portable value, write the entire observation tree into pooled JSON,
and deserialize the whole CLR envelope on every call. Preview comparison, persistence planning and
preview output rebuilt graph documents even when they referenced identical immutable observation storage.
Removing only the temporary JSON document did not solve this amplification in the earlier Ari experiment.

The existing observation materializer now also prepares the framework's exact hosted CLR contract.
Conventional records, arrays and mutable property bags read their fields directly. The adapter owns the
frozen serializer policy; Core's materializer does not depend on execution serialization policy. Selected
JSON constructors are honored, computed getter-only properties are ignored on read, and strict unknown
members are checked through the existing allocation-free field traversal. Custom type/member converters,
callbacks, required-member contracts and unsupported shapes retain their owning serializer path.
Constructor member converters retain canonical field order. Exact enum literals are prepared through the
existing wire-member catalog and the actual serializer; other forms retain that serializer's reader and
rejection rules, avoiding one JSON allocation per enum field on the common path. Cross-kind numeric reads use stack-only JSON
numeric tokens where a CLR cast would change rounding, retaining decimal scale and signed-zero semantics.
The existing admission walk detects inputs requiring whole-payload JSON depth, byte-policy or malformed
UTF-16 normalization; those use the original serializer path, including ignored fields. Observation-backed
bindings retain their existing native-value semantics. This adds no second traversal or success-path allocation. This is
an extension of the existing materializer, not another CLR decoder or an Ari-local conversion mechanism.
Member codecs interpret their own payload. Codecs requiring enclosing JSON context must be declared on
that owning envelope, which retains the whole-value serializer path.

`IImmutableObservationProjection` explicitly promises deep immutability and deterministic, context-free
conversion. `ShapeGraphDocument` opts in. When an object is decoded, its exact immutable field-storage
identity owns its projections. Each frozen serializer-options instance has a weakly held profile scope;
projection type and binary encoding distinguish entries inside it. Default options use the framework default
profile. Empty objects and mutable options are not shared. A successful projection is published once with
`PreparationSlot`; exceptions leave the same slot retryable. Neither discarded payloads nor discarded
profiles are retained. No content hash, request key or tenant result is used as an alternative authority.

The owning portable value remains authoritative. Immutable reuse conveys no contextual validation,
authorization, source-token or deployment-admission evidence. Hosted admission still runs before every
projection. Mutable outer envelopes, arrays and caller projections remain independent; only explicitly
immutable children are shared. Imported/persisted observations have independent storage and are admitted
normally. Existing execution-definition projections still use their owning execution document and their
separate compact-definition codec; this does not replace that boundary.

Framework marked graphs receive the existing reflection audit for settable properties, arrays and mutable
collections. Consumer implementations and converter purity remain explicit contracts, not runtime-proven
facts. Differential tests compare prepared admission to the exact owning serializer, including failures.
Other tests cover constructor/converter selection, case/unknown-member rejection, mutation isolation,
profile/encoding identity, concurrent first use, zero warm read allocations, retryable failures and weak
payload/profile lifetimes. Opt-in `PreparedHostedProjectionBenchmarks` measures the real adapter, including
fresh admission, for a shared 128-field immutable schema and 1/1,024 mutable rows.

## Ari qualification

Ari #225 and #226 are merged; the paired control is the frozen published-alpha.141 harness containing
#226's validation reuse. The source-qualified candidate changes Core/Relations and opts the existing
protocol document/carrier into the explicit immutable contract. No package was published from this branch.
Both X12 graph digests must remain identical. Counter runs are isolated from builds and tests. Measurements
are cumulative managed allocations, not retained heap; caching may retain one typed interpretation as long
as its owning observation remains live. No application or CI latency claim is made.

The consumer dependency order is: merge this capability, publish it, then upgrade Ari and adopt the protocol
markers. Source-assembly substitution is qualification only, using the existing durable-bookkeeping target;
it is not a production dependency or an Ari-local fallback. The companion Ari branch records the exact
commands and paired allocation evidence.
