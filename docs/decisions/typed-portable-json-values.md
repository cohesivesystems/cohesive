# Typed CLR document values use explicit portable JSON contracts

## Decision

A CLR type whose semantic representation is one canonical JSON value may declare
`[PortableJsonValue(JsonTypeKind)]`. Shared CLR contract inference maps every occurrence of that type to
`JsonTypeRef`. CLR-to-observation projection serializes the declared type as JSON, while existing typed runtime
dispatch materializes the retained JSON into the declared CLR type.

The type's JSON Schema and semantic validator remain authority for its internal structure. The attribute owns only
the execution-contract boundary and its guaranteed root JSON kind. A value that serializes to a different kind is
rejected by ordinary portable-value validation.

`ShapeGraphDocument` declares an object-valued JSON contract. Its document schema and semantic validator continue
to define the graph payload; canonical Process and Transition contracts do not duplicate that schema through CLR
reflection.

The same alignment rule applies to closed enum contracts. A CLR enum that declares the standard
`JsonStringEnumConverter` is inferred from its canonical wire member names, including
`JsonStringEnumMemberNameAttribute` overrides, because observation projection honors those declarations. Plain
enums retain CLR member names. A custom enum converter falls back to a diagnostic opaque type because Cohesive
cannot truthfully infer its complete output domain from reflection alone.

## Why

Portable documents often contain recursive types, polymorphic unions, dictionaries, and extension values. Inferring
their CLR implementation as an execution object either produces opaque runtime types or makes the execution
contract a second, incomplete document schema. Treating the value as untyped `JsonElement` at every application
boundary would be portable but would discard idiomatic typed C# handlers and field access.

An explicit declaration keeps the boundary visible and deterministic. Unmarked unsupported CLR types retain the
existing diagnostic opaque fallback; the mapper does not infer JSON intent from a class name, a serializer's
ambient settings, or failure to infer structure.

## Rejected alternatives

- Hard-code known document types in the mapper. This couples the execution kernel to product and adapter types.
- Automatically treat every structurally unsupported type as JSON. This hides inference failures and silently
  weakens contracts.
- Replace typed document fields with `JsonElement` throughout product code. This leaks execution representation into
  domain APIs and creates repetitive manual conversion.
- Expand recursive and polymorphic document schemas into CLR-derived `ObjectTypeRef` graphs. That is useful for
  genuinely structural domain objects, but duplicates the authority already held by portable document schemas.

## Consequences

The declaration must be reviewed as semantic contract metadata. Serializer behavior for a declared type must be
deterministic, and changing its root JSON kind is a contract change. Consumers may inspect or replace the complete
JSON value in portable expressions, but internal document semantics stay behind the document's own schema and
validator unless explicitly projected into a separate structural contract.


## Public discriminator projection

A portable object may also be a case in a native `JsonPolymorphic` contract. Public TypeScript generation must
retain that serializer's flat discriminator layout even while the case's internal document schema remains opaque.
For example, a native `RelationQueryDocument` relation case serializes with `$definition: "relation"` beside the
relation fields. Its conservative generated type is the discriminator intersected with `Record<string, unknown>`;
a synthetic `value` property would describe a different wire format. Scalar and other non-object union payloads
retain their existing envelope convention.

`PortableDocumentContractTests` qualifies this layout against actual serialization and generates native draft and
query documents through the existing graph builder. Native expression cases retain their declared discriminators.
This does not yet provide the full public relation schema: separating execution admission from an explicitly
selected public document contract remains part of the declarative service plan. Expanding every portable value
through CLR reflection would still violate the authority boundary described above.

## Explicit public record contracts

`ClrShapeGraphBuilder.UsePublicJsonContracts(options)` selects a public representation interpretation of
serializer-backed records. The normal builder and `DefaultClrTypeRefMapper` retain portable JSON admission.
The public builder snapshots serializer options, reuses the existing recursive named-type and polymorphic union
projection, and expands a portable value only when its actual serializer metadata describes an object. An
incompatible declared root kind fails early. Converter-defined portable values remain opaque; CLR implementation
properties are not used to guess their serialized representation. Custom object metadata that cannot be described
by the readable CLR properties is rejected rather than silently approximated.

The canonical-JSON contracts assembly loader uses this interpretation for both discovery validation and final
projection. This makes a record such as `Review(RelationDraftDocument Draft, RelationQueryDocument? Relation)`
publicly typed, including the native `RelationDefinition` case, without changing its execution/storage contract or
adding a parallel review/document model. The regenerated relations artifact is produced by the existing CLI.

OpenAPI accepts the same explicit serializer options. Its existing schema registry consumes native object metadata
for names, presence, nullable properties, dictionaries and explicit polymorphic discriminators. Recursive references
remain component references. Scalar/converter schemas use the platform exporter and the existing System.Text.Json
shape metadata for known wrappers/enums. Constructor default values are deliberately not emitted as wire defaults:
they may be internal normalization inputs, and native schema export cannot serialize some defaults such as
`default(ImmutableArray<T>)`. Semantic document validators and strict serializers continue to own acceptance.

This initial OpenAPI public profile rejects property-specific converters/number-handling overrides and
polymorphic cases without explicit discriminators, rather than claiming an unsupported contract. Converter-defined
values without a known public scalar representation remain opaque. The default OpenAPI profile is unchanged.
GraphQL public document expansion is not included in this change.

Qualification includes native draft/query documents, recursive records and expressions, nullable document fields,
unchanged execution inference, actual JSON discriminator layout, converter opacity, contradictory root kinds,
unsupported object metadata, deterministic generation, valid OpenAPI references and TypeScript compilation.

## Native documents inside foreign envelopes

`RelationDraftDocumentJsonConverter` and `RelationQueryDocumentJsonConverter` are native document-boundary
adapters in Cohesive.Relations. Register them on an envelope's serializer options, or on an individual property.
They retain the native format's own options rather than inheriting outer naming/enum policies. Reads delegate to
the owning serializer, including version dispatch, duplicate-property detection, semantic validation and
fingerprint checks. Writes stream using frozen options created by that owner. No replacement document type or
protocol-specific model is introduced.

Each adapter belongs to its native format because their admission contracts differ; the query serializer also owns
its specialized query-parameter converter. A generic web naming converter is not sufficient. Read adaptation retains
the complete nested JSON for the existing string-based native admission API; this is one boundary operation, not a
row-loop projection. Registering these adapters only on API options keeps persisted aggregate restoration and its
existing diagnostics unchanged.
