# Connected application composition

## Intent and authority

Compose an inspectable graph from API contracts, queries, transitions, processes, domain entities,
physical storage mappings and native infrastructure associations. Authoring objects are producers and
references, not a new portable semantic IR. Existing DomainModelDefinition, query documents, API definitions,
Process documents and infrastructure realization remain their respective authorities.

Plain C# types optionally organize declarations. Inline builders remain equally supported. Stable semantic
IDs and revisions are explicit; a class or property rename must not silently retarget a durable contract.
Partial contributions must ultimately resolve into one validated model rather than independently maintained catalogs.

## Implemented foundation

- `DomainModel` wraps DomainModelBuilder and supplies an instance-owned, once-built Definition. Entity handles
  are the exact objects passed to mappings and queries. Relationship handles still use the existing Relations
  documents imported by queries; DomainModelDefinition does not newly claim to serialize those relationships.
- `QueryModel` collects the actual compilation requests while returning the original typed handles. Definition
  is an immutable request collection, not a second query IR or a provider execution plan.
- `ApiModel` supplies a frozen Definition from ApiBuilder. Existing ApiDefinition.Combine composes separately
  declared contracts and rejects duplicate endpoint IDs. This does not yet validate all HTTP route conflicts
  or provide partial implementation registration.
- PostgreSQL's existing offline bindings gain inline mapping callbacks and a frozen Define recipe. Native table,
  column, key and partition choices stay with the adapter. Bind retains native data-source ownership at the host.
- `[Process]` on `ProcessModel<TInput, TResult>` generates metadata, provenance and cached definition lowering
  from Execute. The same generator still supports the standalone GenerateProcessDefinition factory. Model
  factories are private so callers cannot override the type's declared ID/revision through a second entry point.
- The running Aspire-first example creates one FulfillmentApplication per program. AppHost associations and
  worker runtime bindings consume the same construction recipe. Independent instances have equal portable
  query/process identities without sharing mutable catalogs or credentials.

`ApplicationModelBuilder` registers the finite set of offline declaration types. Constructor dependencies resolve
once regardless of registration order; missing dependencies and cycles fail during Build. Completion freezes
registered authoring models automatically. Canonical execution documents pass through the existing
ExecutionDefinitionDocumentCatalog, which rejects duplicate identity/revision pairs (including differing fingerprints).
Constructor configuration is therefore allowed, but two variants cannot silently occupy one revision in a composition.
There is no global identity registry; separate independently deployed compositions must still govern revision changes.

`AuthoringState<T>` owns the shared freeze-once protocol: edits serialize with preparation, freezing starts before
validation, and both successful results and failures are cached. Query order uses an explicit insertion list with a
separate duplicate-ID set. PostgreSQL's constructor is private; Define is the sole contribution boundary, including
failure cleanup of captured builders.

Construction happens before publication to other threads. Definition reads finalize catalogs. Process definition
creation uses Lazy execution-and-publication semantics and caches failures; no virtual authoring runs in its base
constructor. Query/result execution and request context are not cached by these classes. No connection is opened
by offline construction. PostgreSQL validation and preparation retain the existing compiler paths.

## Remaining dependency-ordered composition work

1. Add application service composition with typed instance endpoint selectors, repository resolution through
   registered entity contracts, and optional extracted modules. Keep unrelated public queries outside a process's
   dependency closure. Validate missing/duplicate implementations after all contributions are assembled. Reject
   ambiguity; registration order must not pick a winner. Preserve separate contract/implementation authoring.
2. Consolidate HTTP mapping and host lifetime/failure logging in the shared adapter. Implementations bind queries,
   entity operations, transitions and processes. API completion policy constrains selected runtime policy.
3. Add explicit accepted-execution receipts and ResultOf-linked polling, then separately consider observation
   streams and callback contracts. Neither SignalR nor SSE implies ordering, replay, retention or durable recovery.
4. Attach registered storage/execution bindings to native Aspire resources and derive required infrastructure
   capabilities from prepared definitions. Preserve native-only resources and target configuration. Keep declared,
   validated and observed evidence distinct. Do not introduce a second infrastructure inventory.

The example remains ephemeral, with atomic entity commits and receipts, explicit cross-entity compensation,
and native/composed queries. This is not an event-authoritative state model or proof of durable recovery.

## Decisions

Use/extend existing builders and generator rather than introduce a universal semantic registry, reflection scanning
of static properties, provider-option wrappers, or a competing graph format. Constructor arguments make ownership
visible; optional base types supply actual freeze/cache contracts rather than empty marker types. Storage configuration
uses an ordinary method on a C# class; a new configuration interface is unnecessary until a concrete resolver consumes it.

Validation covers late additions, duplicate semantic identities, mapping isolation, independent equivalent program
models, generated instance dependencies and compile-time errors. Existing PostgreSQL/HTTP/process tests exercise
native behavior; deterministic warm-access allocation checks protect the once-prepared process definition. No
startup-speed or end-to-end performance improvement is claimed.

The optional host composition resolves only explicitly registered types via existing .NET constructor activation.
It neither scans members nor guesses semantic dependencies. It removes hand-ordered construction and forced
Definition reads. Generated process attachments now remove the repeated transition/lookup registration list;
the full API implementation registry and unified endpoint mapping above remain separate work.

## Generated process attachments

The existing generator records typed transition and EntityPointRead handles that Execute actually invokes.
ProcessModel.Dependencies prepares those attachments once. ServiceDependencies.Process(model) checks each
attachment against the exact canonical graph reference before using the existing transition and lookup binding
paths. Repeated aliases to one declaration are idempotent; competing recipes for the same exact reference fail
with structured diagnostics. Raw references and arbitrary hosted queries still need explicit registrations;
missing dependencies fail at host binding before repository IO. Separately declared services remain supported.

EntityPointRead retains an existing observational HostedQuery, the exact entity, a key selector and a result
projection. The query document remains portable authority; these delegates are explicit native host attachments,
not serialized semantics or a proof of portable keyed-read lowering. They must be thread-safe and their implementation
contract must be revised when their behavior changes. The repository supplies the trusted partition. No read runs
while composing or generating dependencies. Typed Process transitions accept POCOs, matching Transition itself.

Independent public SQL query selections remain explicit, with revision/bounds/consistency, and are not inferred
as dependencies of Fulfill. Generated attachment inference does not complete API-to-service composition, durable
execution, or capability evidence for arbitrary native callbacks.
