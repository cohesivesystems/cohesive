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
