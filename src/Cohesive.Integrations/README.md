# Cohesive.Integrations

This experimental block declares bounded ingestion flows and lowers it to canonical
Cohesive.Processes. It is a composition compiler, not an ingestion service or a new execution runtime.
It does **not yet qualify a physical realization**. Do not interpret successful lowering as evidence
that an adapter can atomically publish data and coverage or safely retry an external operation.
Successful lowering emits `integrations.ingestion.realization.unqualified` as a structured warning;
`IsValid` means the declaration can be lowered, not that it is production-ready.

## The declaration

```csharp
// Exact RequestContractReferences resolved from an InteractionContractCatalog.
var definition = new IngestionDefinition(
    Acquire: acquireRequest,
    Publish: publishRequest,
    Settle: settleRequest);
var document = IngestionDefinitionDocuments.Create(
    new("ingestion/prices-to-research-repository"),
    new("revision/1"),
    definition,
    provenance);
var validation = IngestionDefinitionDocuments.TryLower(document, contracts, out var process);
```

The document identity names a stable source-to-destination application binding. Different consumers
or independently advancing destinations need distinct identities. Its revision and fingerprint pin the
meaning of the flow; changing a definition must not silently reset or reuse an incompatible ledger.
Exact Request references pin each operation's identity, revision, and fingerprint.

| Operation | Successful result means | Responsibility of its contract and implementation |
| --- | --- | --- |
| Acquire | One bounded, complete unit of work is available for application | Carry source identity, selected range or opaque continuation, stable work identity, completeness evidence, replayable input or retained artifact references, and eventual settlement information. |
| Publish | Destination effects, application coverage, and original operation evidence have been accepted together | Validate and normalize domain input, enforce scope/order/CAS rules, and retain a receipt for exact retry reconciliation. An existing domain publication operation can implement this boundary. |
| Settle | The source obligation is settled after publication | Acknowledge the source where applicable, or return a declared successful no-op for a pull API with no acknowledgment protocol. Repeating settlement must be safe within the declared recovery horizon. |

Request schemas own application payloads. The ledger contracts below own only progress and receipt metadata. The
successful result **schema and schema revision** must equal the next Request's input schema.
A shape-compatible revision is insufficient; mappings must be explicit inside an operation or its
application Process. Each operation declares exactly one successful result. All other declared terminal
outcomes stop the flow. The graph returns `true` on completion and fails with `false` on a terminal
failure; that Boolean is completion status, never rollback evidence. Detailed outcome bindings and
publication receipts remain part of Process state/evidence and the operation's receipt store.

The compiler requires stable retry identity, ambiguity reconciliation, and reuse of duplicate outcomes
from every Request. These are **requirements on implementations**. It does not invent an exactly-once
guarantee, deduplication retention, completeness evidence, or a source snapshot. In particular, absence
of a receipt under weak visibility is not evidence that publication failed. Unknown publication must
remain unresolved/reconciling; an adapter must not convert it into a terminal rejection or authorize a
fresh differently identified publication.

## Existing authorities

- Core execution documents own persistence, canonical serialization, revisions, fingerprints, and provenance.
- Core Request contracts own payload schemas, response outcomes, and recovery obligations.
- Processes owns durable boundaries, continuation, request identity, dispatch, reply correlation, and recovery.
- The domain publication operation owns destination invariants and application coverage.
- Storage and its adapters own physical commit validation, native boundaries, receipts, and reconciliation.

Atomic-profile lowering produces three existing Request nodes and up to two terminal nodes. It performs no I/O. The derived
Process retains the exact source document reference and maps each Request node to its ingestion role.
Its size is linear in the declared terminal outcomes; no new runtime worker, scheduler, serializer,
transaction manager, or ambient dependency is introduced. Unsupported semantic extensions are rejected.

Direct immutable declarations plus the shared JSON envelope are the initial authoring surface. Agents
can persist, inspect, revise, diff, lower, and validate these documents through the same APIs as humans.
An expression/fluent projection can follow concrete domain uses; it must lower to this authority and
cannot retain executable closures. There is no AI dependency at runtime.

## Deliberate limits and next proof

The original atomic profile is restricted to **atomic publication followed by settlement**. The
separate-ledger profile below models an additional ledger commit. Neither profile models source
acknowledgment before application, fan-out, compensation, long-running
transactions, or automatic loops over pages. It also does not select the next missing range: an
application/source contract selects bounded work from ledger evidence. Range and opaque cursor inputs
pass unchanged through the same composition; their distinct ordering, completeness, expiry, overlap,
and revision semantics remain source-owned. The tests' string payloads are a transport-level example,
not an implementation of those semantics.

The next COH-107 slice must bind a concrete domain publication boundary and qualify target capabilities
and recovery evidence before execution is advertised as ingestion-ready. That includes atomic placement,
scoped idempotency, retention horizons, exact-operation receipts, source completeness, safe advancement,
and unknown-outcome handling. The existing Storage executor cannot automatically enlist arbitrary
pre-existing application tables. Preserve an existing safe domain transaction until an explicit mapping
has been demonstrated. A real bounded provider command can ship independently of this compiler.

COH-108 tracks the recoverable **separate-store** protocol and native crash matrix. The reference contract below begins that work. Physical guarantees require
an explicit realization; they cannot be obtained by weakening this atomic profile or retrying an upsert
that might overwrite newer destination data. Broker-specific adapters and broad Integrations features
remain later work.

## Verification

`Cohesive.Tests/Integrations/IngestionDefinitionTests.cs` exercises canonical reopening and deterministic
lowering, exact schema revisions and contract fingerprints, missing contracts, recovery requirements,
and the existing Process reference interpreter. It verifies request payload propagation, stable
re-evaluation identities, post-publication settlement, and stopping on each stage's failure. It does
not claim database crash recovery or actual external-effect deduplication; those need adapter proofs.

## Separate ingestion ledger profile

A flow may now declare `AdvanceLedger` as an additional exact Request:

```csharp
var definition = new IngestionDefinition(
    Acquire: acquireRequest,
    Publish: publishRequest,
    Settle: settleRequest,
    AdvanceLedger: advanceLedgerRequest);
```

This produces `integration.ingestion.separate-ledger.v1`, lowered through the same compiler to
**acquire → publish → advanceLedger → settle**. The existing atomic kind and its three-step wire remain
unchanged when the optional field is absent. Reinterpreting an old document as a different protocol is
rejected; reauthor a separate definition revision for a deliberate migration. Exact adjacent result/input
contracts still have to match. Acquire owns retaining exact work, expected ledger revision and publication
identity before publication; Publish returns verified original sink evidence; AdvanceLedger commits
progress with its own receipt; Settle acts only after that success. Unknown effects remain unresolved
Requests, not declared terminal failures. A definite ledger conflict stops before source settlement;
it does not roll back sink data or automatically rebase the ledger.

### Ownership and persistence

`IngestionLedgerAddress` scopes progress by flow, source, destination and partition. Two consumers of
the same source and destination use distinct flow identities. Exact definition revision/fingerprint is
retained in the entry rather than made part of its address: a new semantic revision cannot silently
create an empty ledger and restart ingestion. Position and expected revision are captured during work
selection and must not be refreshed because an acknowledgment was lost.

- `IngestionCursorPosition` retains an exact opaque next token, its source format/revision and optional
 expiry. Null token explicitly means source exhaustion. No entry means no committed progress. Values
 are neither trimmed nor sorted; the source handles cycles, token validity, resumption and completeness.
- `IngestionDateRangePosition` records a half-open forward date window. Its exclusive end is the next
 forward boundary. Successive windows must abut; calendar coverage, confirmed gaps, source date-range
 conversion, overlap reads and historical backfill are not inferred. Use a separately scoped flow for
 a backfill. This first range representation uses dates, not arbitrary intraday timestamp windows.
- The sink retains acquired evidence, domain coverage and original publication receipts. The ingestion
 ledger records source progress. The Process owns outstanding work and recovery. These contracts do
 not require separate physical databases.

`IngestionLedgerAdvance` is immutable control data, not a second envelope for application payloads.
It binds the ledger address, exact flow definition, original expected revision, next position and
`IngestionPublicationReceipt`. A receipt declares the original publication identity, prepared-content
fingerprint and receipt locator. The publisher owns the fingerprint algorithm and the receipt's native
representation. Trusted adapters must verify this binding and scope before calling the ledger. Merely
constructing this record does not prove that any sink committed; unknown outcomes cannot be promoted
to this evidence.

`IngestionLedgerDocuments` uses the existing execution document envelope, canonicalization, strict
projection, fingerprints and provenance. No serializer or patch system is added. Its document identity
encodes all four scope components independently; its revision identifies the publication. Persist the
original work/expected revision before sink dispatch, and the completed advancement document before
ledger dispatch. A publication handler's retained result can carry this document to the next Request.
Direct C# records are draft producers; `Create`/`TryRead` validate the materialized document. An expression
DSL is deliberately deferred because this surface currently consists only of immutable data.

### Reference contract and crash behavior

`IIngestionLedger` provides current progress observation and exact `AdvanceAsync`. Each advance must
commit the new entry **and an independent original-result receipt atomically**. `IngestionLedgerReduction`
owns the decision rules so adapters need not independently implement another replay/CAS algorithm:

1. Check the original receipt under `(ledger address, publication ID)` **before** inspecting current
  progress. Exact input returns that receipt, even after later work; different input is identity conflict.
2. Require the originally captured revision (zero means initial absence).
3. Require the pinned definition and supported position transition; no implicit migration, skipped date
  windows, cursor-format change or reset after source exhaustion.
4. Propose the next revision and original result. A physical interpreter must atomically persist both
  before exposing `Advanced`. It must never apply that result on `Replayed` or `Conflict`.

The reducer requires a definitive, atomic view. Weak visibility with an absent receipt is inconclusive:
return `Unknown` without treating it as a precondition conflict. The storage adapter must serialize the
read/evaluate/write boundary or supply equivalent native CAS/transaction semantics. Arbitrary constructor
values, current snapshots or an in-memory lock are not evidence of physical qualification.

| Interruption | Required recovery |
| --- | --- |
| After acquisition, before publication | Resume retained exact work and identity. |
| Sink committed, acknowledgment lost | Reconcile/retry the same sink operation; advance no ledger while unknown. |
| Sink confirmed, before ledger commit | Retry the exact retained advancement; do not republish with a new identity. |
| Ledger committed, acknowledgment lost | Recover the original ledger receipt before current revision checks. |
| Later work advances the ledger before an old retry | Return the old original receipt without regressing current progress. |
| Ledger conflict after publication | Preserve the sink result and stop; require explicit recovery/replanning, not compensation by default. |
| Source settlement interrupted | Retry its stable Request only after confirmed ledger success. |

`InMemoryIngestionLedger` is a thread-safe reference interpreter retaining receipts for its lifetime.
It is **not process-durable**, has no retention eviction, and must not be used as a production ledger.
Tests model both acknowledgment-loss boundaries and reference Process sequencing; native database
crash recovery is deferred to the next SQLite adoption slice. The per-advance cost includes canonical
projection/validation; it is intended for bounded acquisition units, not individual market ticks. Identity
fields are limited to 1024 UTF-8 bytes and cursor tokens to 16384. Retained receipt growth is explicit.

`IngestionRecoveryCapabilities.Validate` emits stable diagnostics for missing prepared input, unsafe
publication retry, non-atomic ledger/receipt writes, unsafe receipt visibility, and insufficient or unknown
input/sink/ledger retention horizons. It validates declared evidence for those obligations, not the
complete physical realization. Source completeness, settlement and native adapter conformance remain
required; successful lowering still emits `integrations.ingestion.realization.unqualified`.

Storage's generic commit executor can eventually realize a ledger transaction, but does not own source
position semantics or automatically enlist pre-existing domain tables. The next adoption step is an
explicit SQLite mapping and Process binding, preserving domain publication receipts and keeping
combined atomic sink/ledger transactions as a separate optional realization. Cosmos, generic UoW,
compensation, scheduling and native broker transports are outside this PR.

## Retained acquisition and prepared work (ITO-27, first extraction)

`IngestionWorkDocuments` defines `integration.ingestion.work.v1` in the existing canonical execution
container. It separates three immutable facts:

1. `IngestionAcquisitionRequest`: flow scope, original operation and attempt, exact flow and transformation
  references, source-selection content, original optional ledger revision, and selection observation.
2. `IngestionAcquisitionReceipt`: exact request reference, acquired content and receipt observation time.
3. `IngestionPreparedPublication`: exact acquisition and transformation references, authoritative sink
  input, proposed optional source position, and preparation observation.

`IngestionContentReference` binds an opaque locator, exact SHA-256, byte length and media type. `Describe`
does not store bytes. `Matches` checks exact bytes after resolution; it does not infer metadata, parse
payloads, fetch a locator, or establish source completeness. Descriptors are bounded, but adapters must
apply their own payload/manifest limits **before allocation**. Secrets and expiring signed URLs do not
belong in locators or retained requests. An acquired manifest must close over separately verified source
payloads; validating only the manifest does not validate its children.

`Create` / `TryRead` / `TryDeserialize` use the existing canonical serializer, fingerprint and structured
diagnostics. `ValidateChain` verifies exact lineage, chronology, the pinned transformation, and agreement
between the ledger expectation and proposed position. Its input is a retained prefix; a missing acquisition
receipt is **not** permission to reissue a possibly completed source operation. A source adapter must
reconcile ambiguous acquisition with the same identity or surface uncertainty. Once preparation is
retained, the host must reuse its exact bytes and skip acquisition and transformation. Fingerprints detect
inconsistent evidence, not malicious replacement of a whole trusted store; store ownership and write-once
admission remain physical obligations.

After a trusted sink handler confirms the original receipt, `CreateLedgerAdvance` binds its publication
identity and exact prepared-byte digest to the frozen request and proposed position. It invokes the existing
ledger document authority; no second CAS or receipt-before-revision algorithm is introduced. Inconclusive
publication cannot supply that receipt. Atomic-profile work has no separate ledger advancement. The
application's native atomic publication can still include its own domain coverage guard in prepared bytes.

This is the contract extraction, **not completion of ITO-27**. It adds no scheduler or parallel Process
state machine. Bounded inline write-once retention is available through `SqliteIngestionWorkStore`. Durable dispatch,
ambiguous acquisition reconciliation and application recovery parity still need the Ito conformance proof described in
[the extraction plan](../../docs/architecture/ingestion-acquisition-extraction.md). The existing unqualified
realization diagnostic remains in force. Records are draft producers; their persisted, validated documents
are the inspectable authority. A fluent projection is unnecessary for these data-only boundaries.

## Authoring contract properties and provider bindings

`Cohesive.Contracts` implements the structural property coordinates from the
[System Model §§3.5–3.5.1](https://app.notion.com/p/4098cf7881f981aeb90acf6750031641).
A `ContractPropertyDefinition` declares kind, overlapping predicate classifications,
exact domain predicate, structured scope, parameters and assumptions. A quantitative
bound can reference an application context describing its limit, units, measurement
interval and time basis. Property and context interpretation belongs to the declaring
language and its qualified interpreters.

`ContractProperty` supplies a role and an exact assertion reference. Requires, Provides,
Effects, Coeffects, Guarantees, Assumptions and Evidence reuse this one assertion model.
Property kinds use an open stable vocabulary. The built-in vocabulary includes temporal,
resource, authority, epistemic and data-integrity kinds. Classifications support Safety,
Liveness, Invariant, Aggregate and Hyperproperty. Invariants also declare Safety.

```csharp
var coverage = ContractPropertyDocuments.Declare(new("historical/accepted-coverage"), revision,
    new(Kind: ContractPropertyKinds.DataIntegrity,
        Classification: [ContractPredicateClassification.Safety, ContractPredicateClassification.Invariant],
        Predicate: coveragePredicate.Reference,
        Scope: new(["instrument/SPY"], "instrument/session-date", "publication-commit",
            interval.Reference, "exchange-session valid time"),
        Parameters: selection.Reference,
        Assumptions: assumptions.Reference), provenance);

var contract = new IngestionContract(ingestion.Reference,
    [new(ContractRole.Requires, coverage.Reference)]);
var source = contract.Bind(provider.Reference, resource: "tiingo/research-account",
    consumer: new("research/eod", "tiingo", "research/bars", "SPY"));
var binding = IngestionProviderBindingDocuments.DeclareBinding(
    new("research/spy"), revision, source, provenance);
```

`DeclarePredicate` materializes a domain-owned structural predicate. `DeclareContext`
materializes parameters, intervals, assumptions or bound annotations. `Declare` normalizes
subject and classification ordering and persists source maps through the shared execution
document envelope. Inspection resolves every exact reference and validates classification,
role and scope structure. Domain predicate evaluation, implication between differing
assertions, evidence admissibility and contract discharge require subsequent interpreters.

The provider profile's `Properties` uses the same `ContractProperty` references. The example
requires both accepted coverage and retained response bytes. Its acquisition handler offers
retained bytes. The publication Request owns the coverage obligation. The binding's inspected
properties retain these roles and the source predicate references for explanation and planning.

The consumer address scopes independent progress. The resource identifies the external
account and quota binding. Canonical documents carry all declared semantics across reopening.
Physical runtime qualification remains explicit in the ingestion inspection diagnostics.
This draft replaces the earlier coverage-specific binding schema; existing retained draft
bindings require their original interpreter or an explicit migration.

### Executable historical-data example

[HistoricalMarketDataExamples](../Cohesive.Examples/Integrations/HistoricalMarketDataExamples.cs)
authors a two-session SPY scope, the acquire/publish/ledger/settle Requests, the two properties,
and a Tiingo binding shared by independent consumers. It persists and reopens the entire
dependency closure, then inspects the exact property uses. The calendar, normalizer and
predicate declarations are illustrative domain fixtures. The example exercises structural
inspection and leaves predicate execution and physical qualification to domain interpreters.

```sh
dotnet test src/Cohesive.Examples/Cohesive.Examples.csproj \
 --filter FullyQualifiedName~HistoricalMarketDataExamples
```

### Fluent application authoring

`Cohesive.Integrations.Authoring.IntegrationModel<TSelection>` is the C# source surface.
An application subclasses it, declares typed Requests, properties and an ingestion interaction
in its constructor, and exposes the handles it wants callers to bind. The historical example's
`HistoricalMarketData` class demonstrates the full declaration without manually assembling
execution documents, reference catalogs or fingerprints.

```csharp
var model = new HistoricalMarketData();
var tiingo = model.Collect
    .Provider("tiingo-daily", operation: "GET /tiingo/daily/{symbol}/prices", medium: "HTTPS")
    .Provides(model.RetainedResponse);

var compiled = model.Collect.For(selection)
    .Use(tiingo)
    .WithAccount("tiingo/research-account")
    .ForConsumer(new("research/eod", "tiingo", "research/bars", "SPY"))
    .Compile();
```

Inside the model the composition is:

```csharp
Collect = Ingestion("collect")
    .Acquire(acquire).Publish(publish).AdvanceProgress(advance).Settle(settle)
    .Requires(AcceptedCoverage).Requires(RetainedResponse);
```

The base class owns source identity and lifecycle. Existing Request authoring, Contract property
documents, binding inspection and ingestion-to-Process lowering remain the semantic authorities.
`Compile()` returns the complete canonical document closure and structured diagnostics. Applications
should check `IsSuccessful` and inspect `Validation` before using its binding or Process.

The model is authored on one thread. `For(selection)` snapshots the selection and freezes all model
builders. Predicate and assumption values are snapshotted when declared. Provider and consumer
refinements return independent immutable source values. A frozen instance can be compiled repeatedly
or concurrently, with identical documents for identical inputs. Agents can inspect persisted documents,
compare exact references and fingerprints, and revise the C# source. A separate portable unbound
source projection or patch protocol is not implemented yet.

Conventions are deliberately small and explicit:

- Declaration identities are model-relative; semantic revision comes from the base constructor.
- Payload schema identities default to the CLR full name and model revision. `Payload<T>(id, revision)`
 supplies stable application-owned identities; the historical example uses these overrides.
- Selection-scoped property and provider identities incorporate the selection fingerprint. The full
 selection becomes property parameters, retaining calendar, normalizer and expected-session evidence.
- Direct and nested property selectors use the canonical JSON naming convention and `JsonPropertyName`.
 Selectors lower to field paths and resolved values; executable selector expressions are rejected.
- The current interval convenience supports `DateOnly` boundaries, with explicit end inclusion.
 `Boundary(request)` identifies that exact Request's successful result.
- The bounded Request profile observes late results, rejects stale results, escalates unresolved
 outcomes and does not declare timeout or cancellation outcomes. Stable retry identity, ambiguity
 reconciliation, duplicate disposition reuse and positive retention are explicit required choices.

`.Requires(...)` and `.Provides(...)` retain distinct contract roles. Account/provider choices and
naming conventions cannot remove requirements. This increment uses explicit provider selection;
hierarchical defaults and enforced selection policy resolution remain separate future work.

This first fluent surface supports the existing bounded ingestion profile. It performs structural
validation and preserves the physical-realization warning. Predicate evaluation, offer implication,
adapter execution and evidence qualification still require their owning interpreters. In particular,
`.Provides(...)` records a claim; it does not establish that the provider has satisfied it.

### Referencing an existing ingestion protocol

An `IntegrationModel<TSelection>` may call `Ingestion(name, definition, dependencies)` to declare
properties and provider bindings around a pinned protocol. The original Request/Reply documents and
Process identities are preserved, and protocol-step mutation on this handle is rejected. Its
`AcquisitionRequest` and `PublicationRequest` handles identify exact property boundaries.

This supports incremental adoption by durable applications such as Ito, whose existing Requests
carry retained-work locators while the model selection carries domain configuration. The dependency
closure must validate under the existing ingestion compiler. It is a reference to existing authority,
not a second reconstruction of its protocol. Requirements and binding are authored normally.

Subject selectors accept custom scalar identity types whose canonical JSON representation is a
string. Select the identity itself (for example `s => s.InstrumentId`); CLR members hidden by a
custom JSON converter do not become portable nested field paths.

### Offline coverage and evidence walkthrough

[TiingoCoverageEvidenceExamples](../Cohesive.Examples/Integrations/TiingoCoverageEvidenceExamples.cs)
reuses the fluent historical-data model and demonstrates source coverage, retained-byte integrity,
publication receipt attribution and replayable ledger advancement. Complete, partial, empty and
invalid responses are executable cases. See the [examples guide](../Cohesive.Examples/Integrations/README.md)
for the run command and the explicit simulated source/sink boundaries.

### Operation profiles, connections and runtime implementations

`ProviderOperationProfile` describes a versioned provider operation and its medium, supported
authentication, required entitlements, batch default and hard batch bound. `IntegrationConnection`
describes configured access: provider, shared resource/account identity, endpoint, authentication
reference, declared entitlements and hard connection bound. A resource can have several connections;
one connection can serve several compatible profiles. These are immutable authoring sources lowering
to exact canonical documents, with source maps and fingerprints. Secret values never belong in them.

```csharp
var logical = ConnectionReference.Named("market-data");
var connections = new ConnectionBindings().Bind(logical, connection);
var compiled = model.Collect.For(selection)
    .Use(model.Collect.Provider(profile).Provides(model.RetainedResponse))
    .Through(logical)
    .ForConsumer(consumer)
    .Compile(connections);

// Host composition: the adapter constructs its client from the configured connection.
var runtime = new IntegrationRuntime().Register(profile, connection, handler);
var selectedHandler = runtime.Resolve<IMarketDataProvider>(compiled);
```

`IMarketDataProvider` above denotes an application-owned port. See the executable
[HTTP runtime example](../Cohesive.Examples/Integrations/ConnectionRuntimeExamples.cs) for a concrete
client and offline request. The host owns clients, lifetimes and secret resolution. The registry
returns an implementation; Requests and Processes still own interaction execution and recovery.
Registration is an explicit implementation claim, not automatic proof of adapter conformance.

Compilation checks provider and medium identity, authentication compatibility, entitlement inclusion,
and both batch bounds. `.WithBatchSize(n)` overrides the profile default but cannot exceed either
hard limit. No clamping or provider fallback occurs. The resolution retains the exact profile and
connection plus whether the effective batch value came from an explicit choice or the profile default.
Hosts/interpreters must realize that admitted value; the registry itself does not batch requests.

Runtime lookup uses exact references from the validated canonical closure, never a new lookup of the
logical name. A changed connection requires a new admission; retained work needs its original binding
or explicit migration. Rotating a secret value behind an unchanged reference does not alter semantic
configuration. Host authorization/revocation checks remain separate from semantic fingerprinting.

Explicit binding remains available. The scoped resolver below adds bounded hierarchical defaults and
enforced policy checks. COH-135 owns broader offer qualification and physical capability proof.
Authentication and medium names are open adapter identifiers, compared ordinally. Declaring FTP, Kafka,
RPC or OAuth does not install an interpreter. The current concrete application path is Tiingo HTTPS
with an API-key locator. Entitlements are host assertions; a matching label is not remote attestation.
Legacy account-only declarations remain supported, without connection-backed runtime resolution.

The source builders are the edit surface and canonical IR is derived. Agents can inspect and compare
persisted revisions and structured diagnostics; a portable unbound authoring/patch surface remains
explicitly deferred. Shared Configuration factories contain executable host behavior and are not copied
into this semantic model. Contract properties continue to own behavioral requirements and evidence.

### Scoped defaults and mandatory policy

See [ScopedHistoricalDataExamples](../Cohesive.Examples/Integrations/ScopedHistoricalDataExamples.cs).
The example starts with a system default, adds a tenant premium connection and an analyst preference,
and keeps inherited provider, account, entitlement and batch restrictions enforced throughout.

```csharp
using Layer = Cohesive.Configuration.SelectionLayer<IntegrationChoice, IntegrationSelectionPolicy>;

var system = Layer.Define("system/market-data", "v1", "platform-admin", new())
    .Prefer(IntegrationChoice.Select(profile, standardConnection))
    .Enforce(new(AllowedProviders: ["tiingo"], MaximumBatchSize: 8));
var tenant = Layer.Define("research/market-data", "v1", "research-admin", new("research"))
    .Prefer(IntegrationChoice.Select(profile, premiumConnection, batchSize: 4))
    .Enforce(new(MaximumBatchSize: 6));

var selected = IntegrationSelection.Resolve(logical, new("research", "analyst"),
    [system.Document, tenant.Document], authenticatedGrants, profiles, connections);
// Check selected.Resolution.Validation before using the selected source.
var compiled = model.Collect.For(selection)
    .Use(model.Collect.Provider(selected.Profile!).Provides(model.RetainedResponse))
    .Through(logical).ForConsumer(consumer).Compile(selected.Bindings!);
```

The canonical connection resolution references the admitted configuration snapshot, and its dependency
closure retains applicable layers and available profile/connection documents. Changing the profile or
batch after scoped admission fails; express a different local choice through an authorized Local layer.
Allowed sets intersect by conjunction, required entitlements accumulate, and every numeric bound holds.
Null allowed sets are unrestricted; an empty set denies all choices. Sets normalize deterministically.
A disallowed preference fails rather than falling back to a different provider. Contract requirements
remain independent; entitlement labels are not evidence that historical coverage has been satisfied.

At each new external-effect boundary use `runtime.ResolveForDispatch<THandler>(compiled, currentLayers,
grants)`. It validates the original binding and current restrictions before returning the original
handler. The host owns policy freshness and synchronization with actual dispatch. Ordinary `Resolve`
is available for composition and reconciliation; it does not grant permission for new effects.
Later defaults never select a replacement connection for retained work. Revocation can block new effects
without invalidating the identity needed to reconcile prior outcomes.

The policy vocabulary is deliberately bounded: provider/resource allow-lists, required entitlements and
batch bounds. Authentication, medium and profile/connection physical limits still use the original
eligibility validator. No authorized policy-exception mechanism is implemented; all ordinary local
choices remain subject to policy. Hosts supply authenticated publication grants rather than allowing
an untrusted request to grant itself permission. No secret values enter contribution or snapshot data.

### Capability qualification and runtime evidence

`IntegrationQualification.Qualify` consumes an existing configured compilation, an exact resource offer,
a versioned domain interpreter and an explicit UTC assessment time. It reuses binding inspection,
Contract properties and the existing ingestion-to-Process compiler. It does not discover providers,
reconstruct Requests, or invent another workflow engine.

```csharp
var admission = IntegrationQualification.Qualify(compiled, resourceOffer, evaluator, atUtc);
if (!admission.IsAdmissible)
    return; // Report admission.Validation and its per-property Decisions.

var plan = admission.Compilation!;
var handler = runtime.ResolveQualifiedForDispatch<IMarketDataProvider>(plan, dispatchAtUtc);
```

For a scoped configuration use `ResolveForDispatch(plan, currentPolicies, grants, dispatchAtUtc)` instead;
that path requires both current mandatory policy and fresh qualification. Reconciliation can resolve
an original handler after an offer expires; that does not authorize a new effect. The host remains
responsible for clock/policy freshness and synchronizing checks with actual dispatch.

An offer pins the actual profile and connection, a domain capability schema and declaration, supporting
evidence, and a half-open UTC validity interval. Capability data and evidence remain attributable source
artifacts. Missing/expired evidence, a changed connection, an unknown schema, incomplete decisions or
unsupported requirements prevent admission. Interpreter implementation stays outside IR; its exact
semantic identity/revision is retained. Hosts must trust the selected interpreter and evidence issuer.
A structurally valid evidence document is not automatically a remote attestation or proof.

The admission plan references the original binding and Process, exact offer/evidence and evaluator
revision, and every required Contract property. It is attached to `IntegrationCompilation.Qualification`
and retained in that compilation's canonical closure. The original binding stays unchanged, avoiding a
circular reference between binding and qualification. Native/composed/constrained classification reuses
`CapabilityRealizationKind`. Status separately distinguishes Supported, RequiresRuntimeEvidence and
Unsupported. Plans preserve existing physical-realization warnings. Structural reopening validates
lineage and complete decisions; it does not reconstruct unavailable interpreter code or certify an
arbitrarily supplied evaluator's correctness.

`IntegrationCandidateSelection` implements Exactly, ExactlyOne and All over explicitly supplied,
already configured candidates. Exactly never substitutes another binding. ExactlyOne diagnoses zero
or multiple admissible candidates. All preserves every result and fails if any instance is unsupported;
it does not silently drop failures. No preference ranking or authorization is inferred by the selector.

The [historical qualification example](../Cohesive.Examples/Integrations/HistoricalQualificationExamples.cs)
uses two offline resource offers for the same scoped predicates, including an incompatible adjusted-only
fixture. Its bounded evaluator checks instrument/symbol, venue, currency, date envelope, raw/adjusted
basis, exact calendar/normalizer, Request evidence retention, complete-response support and recovery.
Both accepted coverage and retained-response properties remain RequiresRuntimeEvidence. The existing
coverage example now starts from this admission and still blocks partial/empty results and requires a
confirmed publication receipt before ledger progress.

The capability and evidence records in the example belong to that domain fixture, not to the reusable
Integrations vocabulary. The fixture's availability interval, 400-day limit and conformance evidence
are declared test inputs, not live Tiingo service/account guarantees. Production resource discovery,
remote evidence adapters and evidence trust policy remain follow-up work. Ito's durable Tiingo path
now supplies an adapter-owned offer/evaluator from its actual client and normalizer, retaining the
qualification closure at original admission. Its existing acquisition handler checks freshness before
HTTP and retains a local rejection on expiry; reconciliation reuses original results after expiry.
This is implementation qualification, not a live access or physical durability attestation. See
[COH-135](https://linear.app/cohesive-ari/issue/COH-135) for cross-repository validation.

## Local lifecycle admission and draining (COH-136 first slice)

`InMemoryIntegrationLifecycle` controls admission for one stable `IngestionLedgerAddress`. It starts
Paused. Active permits new sessions; Paused stops them and drains existing work. There is no separate
Deactivated state until a distinct behavior requires one. Commands include a stable identity, expected
revision, UTC observation and host-authenticated authorization evidence. The host callback authenticates
and authorizes each lifecycle action; `ProcessControlAuthorizationContext` records that decision.

Bind each immutable compilation once. The resulting controller-owned token is reused across admissions
and cannot be used with another controller. A rejected binding throws `IntegrationBindingException`;
its `Validation` property preserves every diagnostic from the failing validation stage (including
locations and evidence), and its message includes all reported codes and messages. Multiple validated bindings can share one consumer's lifecycle,
so new coverage scopes or provider revisions do not reset the admission fence or history.

```csharp
var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(consumer, authorizeLifecycleAction);
var binding = control.Bind(compiled); // Validate catalogs, exact binding and Process lowering once.
var activated = control.Change(new(
    "activate/1", ExpectedRevision: 0, IntegrationDesiredState.Active, authorization, now));
if (!activated.IsAccepted) return;

var request = new IntegrationAdmissionRequest(
    binding, continuation, activated.Receipt.Revision, authorization, now);
var admitted = control.Admit(request);
switch (admitted.Outcome)
{
    case IntegrationAdmissionOutcome.RevisionConflict:
        // Inspect current control revision and reconcile intent before retrying.
        return;
    case IntegrationAdmissionOutcome.AdmissionStopped:
        // A fresh revision alone is insufficient; await an authorized resume.
        return;
}
if (!admitted.IsAccepted) return;
var admission = admitted.Receipt;
// Start/resume the exact Process; still check current policy and qualification at new effects.
// After a trusted successful Process decision:
var completed = control.Complete(new(admission, decision, authorization));
```

All lifecycle methods take one request and return `IntegrationLifecycleResult<TOutcome, TReceipt>`.
Change, Admit and Complete use `IntegrationChangeOutcome`, `IntegrationAdmissionOutcome` and
`IntegrationCompletionOutcome` respectively; each enum contains only that operation's reachable cases. Applied and
replay outcomes carry a non-null `Receipt`; rejection has a typed outcome. Accessing Receipt on rejection
throws rather than returning null. Change receipts contain only original command and resulting revision;
call `Inspect()` separately for a current pending-work snapshot. Exact command retries return their
original receipt, never a historical pending array. Changed intent under the same identity is rejected.

Admission and pause use the same lock. A race either retains an already-admitted session or rejects the
new one. Admission uses the bound token and constant-time identity/fence checks, with no catalog rebuilding
or lowering. Retrieving an existing admission after pause or completion returns the original receipt and
never permits repeating external effects. Pause stops **new sessions**, not steps of an admitted Process.
Existing work drains and remains subject to dispatch policy. Individual Process suspension/cancellation
still belongs to the existing Process control APIs. A new Process attempt needs a new admission.

`Inspect()` visits and sorts only pending sessions, independent of completed history. Completion moves an
admission from the pending index into retained completion history. `InspectCompletion` returns original
execution and authorization evidence. A second authorized recorder receives
`ReplayedWithOriginalRecorder` and the original receipt; attribution is never overwritten. Neither Active
nor Draining claims execution liveness, provider health or complete data coverage. Failure, cancellation
or ambiguity remains pending because terminal status alone cannot prove external effects are resolved.
Explicit failed-session resolution is described below; cancellation remains pending.

This is a **local reference controller**, not a durable/distributed fence. Hosts must route new sessions
through it and protect inspection at their API boundary. Authorization freshness remains host-owned.
Binding tokens are local validated handles, not portable authoring documents. Runtime lifecycle intent
and observations remain separate from immutable integration/Process IR. Original change, completion and failed-session resolution
receipts are retained for the controller lifetime: memory is linear in changes + admissions, not changes
× pending sessions. Persistent storage, cleanup, cancelled-session resolution, bounded
backfill and API/CLI projection remain follow-up work. Do not reset forward progress for backfill.

Runbook: inspect → issue an authorized revision-checked pause → inspect pending sessions → let their
Processes finish/reconcile → record exact successful completion or verify and explicitly resolve a failed
session → inspect until Draining is false.
Leave unresolved work visible; do not clear it or blindly retry. Resume with an authorized Active command
at the current revision.

## Evidence-based health inspection (COH-136)

The prevalidated binding owns inspection: `binding.Inspect(now, evidence)` reads its own controller's
lifecycle snapshot. Callers cannot pair a binding with another lifecycle or pass arbitrary pending data.
No public consumer accessor or repeated validation of already-admitted sessions is needed.

```csharp
var evidence = new IntegrationHealthEvidence(
    binding.Binding, adapterObservation, validUntilUtc: expiresAt);
var view = binding.Inspect(now, evidence);
// ProviderHealth/ProviderReadiness describe current provider evidence only.
// Lifecycle.Accepting/Draining describe admission control, not provider health.
// ConsumerPendingWork describes the whole consumer; BindingPendingWork describes only this exact binding.
```

`IntegrationInspectionHost` is an **internal experiment**, accessible only to this assembly and its
friend test/example assemblies. It exercises evidence-store reads, TimeProvider and cancellable JSON
export, but has no production consumer yet and is not a supported public hosting API. The historical
example uses it to test composition rather than prescribing setup for an external application.

The current binding owns a concrete `InMemoryIntegrationLifecycle`. Neither public bound inspection nor
the experimental host supports a durable lifecycle store. A real durable adapter/host must establish the
snapshot, ownership and storage contract before a corresponding abstraction is introduced. No speculative
lifecycle-store interface is added by this slice; Ito remains out of scope.

The experimental reader returns null for missing evidence and propagates failures rather than disguising
them as health. Cancellation is passed through and output streams remain caller-owned. Its clock is sampled
after evidence retrieval. A future real host must authenticate its evidence source and authorize inspection
access; the experiment installs no route or access policy. Public inspection remains `binding.Inspect(...)`.

Evidence belongs to the exact binding (scope/revision/fingerprint), reuses `ExecutionHealthObservation`
and shared health/readiness enums, and is current only in `[ObservedAtUtc, ValidUntilUtc)`. Missing,
future or expired evidence gives Unknown while retaining the original evidence. Host policy selects the
current observation and expiry. Authentication/capability failures stay in the original structured
provider diagnostics, without a parallel failure enum. Expired unhealthy evidence means Unknown, not
recovered/healthy. Health does not authorize an operation or establish accepted coverage.

Clock skew is an observation, not an inspection failure. Future pending timestamps remain visible and
produce `integrations.inspection.admission-clock-skew`; each pending summary's `OldestAdmissionAge` clamps a negative age to zero.
Future provider evidence produces `integrations.inspection.provider-clock-skew` and remains Unknown.
Original admission and provider timestamps are never rewritten. Missing UTC offsets or foreign-binding
evidence remain invalid caller input. Pending timestamps are scanned once to compute age/skew, not
revalidated as independent caller-supplied admissions.

`IntegrationHealthEvidence` deliberately uses reference identity (a sealed class, not a record).
The shared observation contains collection-bearing nested evidence, so object equality must not be used
for content deduplication across deserialization. When comparing persisted content, serialize both complete
evidence objects under the same `InteractionEnvelopeJsonSerializer.CreateOptions()` conventions. The
round-trip tests compare the whole wire representation, including diagnostics and provenance. This slice
documents that boundary instead of introducing a second structural comparer for shared execution types.

Provider readiness and lifecycle acceptance remain independent. A paused integration may have a healthy
provider; an active integration may have unknown/unhealthy evidence. Pending work has two explicit scopes:

- `ConsumerPendingWork`: all unresolved sessions for `Lifecycle.Consumer`, including other providers,
  connection revisions and requested scopes. Do not attribute this population to the inspected provider.
- `BindingPendingWork`: only admissions whose exact binding reference equals `Binding`. Its population
  matches the scope of `ProviderEvidence`; an empty population has count zero and null time/age.

Each object reports SessionCount, OldestAdmissionAtUtc and OldestAdmissionAge. Both are computed in one
pass over the same controller snapshot, and the JSON export keeps them as separate named objects. The
old ambiguous top-level pending fields are removed. Neither population counts queued jobs or ambiguous
effects, and even binding-specific pending work does not prove its provider caused the delay. Determining
a session's cause still requires Process/effect inspection.

Coverage stays domain-owned: the historical view reports source rows and unresolved expected sessions,
accepted sessions from digest-checked published bytes, publication receipt and source ledger progress
independently. Healthy-but-empty or partial responses do not establish acceptance. Expiring health does
not erase publication/ledger evidence. Source and sink remain deterministic local fixtures.

Use bounded enum dimensions for metrics, such as health/readiness and desired lifecycle state. Report
pending count/age as values. Binding fingerprints, symbols, sessions, actors and evidence references belong
in inspection/traces, not labels. Deployment wiring, cancelled-session resolution, bounded backfill and
history cleanup remain follow-up work; no durable or distributed store is introduced here.


### Explicit failed-session resolution

`ResolveFailure` closes lifecycle pending work after a host proves the exact admitted Process attempt
failed and **every dispatched effect is accounted for**. A failed Process, missing acknowledgement,
healthy provider or empty destination alone cannot establish this. The host supplies a trusted
`verifyFailureResolution` callback when creating `InMemoryIntegrationLifecycle`; without it the
operation returns `EvidenceRejected`. The callback resolves an immutable evidence reference and
returns the authoritative failed decision only when effect accounting is complete. The controller
checks the exact Process definition and continuation as well as its failed disposition. Authenticity,
terminal-state freshness and domain-specific effect accounting remain the verifier's responsibility.

```csharp
var command = new IntegrationFailureResolutionRequest(
    Id: "resolve/session-1",
    Admission: admission,
    ExpectedRevision: control.Inspect().Revision,
    EvidenceReference: "evidence/coverage-failure/1",
    Authorization: operatorAuthorization,
    AtUtc: now);
var result = control.ResolveFailure(command);
if (result.IsAccepted)
{
    var originalEvidence = result.Receipt.Command.EvidenceReference;
    var failedDecision = result.Receipt.Decision;
}
```

The verifier runs outside the lock, after authorization and cheap command/admission checks. It may
be called concurrently, must not dispatch effects, and must fail closed for missing or ambiguous
evidence. Exceptions propagate without controller mutation. The controller rechecks the revision
and admission after verification, so a concurrent pause/resume, completion or resolution cannot be
silently overwritten. Host policy freshness is the same host-owned boundary as other lifecycle actions.

Accepted resolution increments the shared **control revision**, as does a desired-state change.
Admission and successful-completion recording do not increment it. `LastChange` remains the last
desired-state command; `InspectFailureResolution` supplies resolution attribution. Resolution is
allowed while paused and does not change the desired state. It neither grants redispatch permission
nor advances coverage or the ledger. Successful completion and verified failed resolution are
separate retained facts; `InspectCompletion` remains null for a resolved failure. Cancelled or still
running decisions cannot be resolved by this operation.

Exact command retry—including a serialized/reopened command—rechecks authorization and returns
the original receipt without another verifier call, even after subsequent control changes. Changed
intent or attribution under the same command identity is rejected. An admission retry still returns
its original receipt after resolution; it cannot create new pending work or authorize another effect.
Receipts remain in memory without automatic eviction to preserve these guarantees. This does not add
durable/distributed storage, a production endpoint or an IR authoring surface; these are explicit
runtime commands over an already-derived binding, not a second integration definition language.

The historical example proves complete effect accounting for a coverage rejection: the source read
returned, the Process followed its failure edge before publication, and no ledger advance occurred.
It also tests stale/unauthorized/forged commands, unavailable or invalid evidence, exact concurrent
replay, and a lifecycle change during verification. Trustworthy host evidence is a prerequisite;
these local tests do not qualify a production evidence issuer.


### Checking progress before new work

Before dispatching a new acquisition or publication, read the intended consumer's ledger and call
`IngestionLedgerReduction.ValidateSelection(current, retainedRequest, selectedPosition)` before acquisition.
Before publication use `ValidatePublication(current, retainedRequest, retainedAcquisition, retainedPreparation)`.
The request owns the address, definition and frozen revision; the linked preparation owns `NextPosition`.
The five-scalar helper is internal. Invalid documents/identities/revisions/positions and progress conflicts
all return diagnostics. Publication validation also checks lineage, so callers cannot silently substitute
a preparation from another acquisition. Freeze the request before acquisition and reuse the selected
position when preparing the publication; do not rebuild it separately at dispatch.
An older, overlapping or skipped forward window, stale revision, foreign scope or changed definition
produces the same progress diagnostic as commit evaluation. The returned validation is not a reservation:
concurrent changes still require commit CAS and target-specific fencing. Unknown ledger reads must
fail closed rather than be represented as an absent entry. Proposed progress is distinct from acquisition
lookback scope; backfill uses a separate flow. Original retained-result reconciliation continues to use
receipt-before-CAS semantics and must not be rejected by a new-work precheck.

The historical backfill example checks before acquisition and again before publication. Tests prove
known rewinds cause zero provider calls and zero publications, and that progress changing after
acquisition rejects publication without silently refreshing the selected revision. The local cross-flow group
pause below implements the initial [COH-163](https://linear.app/cohesive-ari/issue/COH-163) control scope.


These methods are explicit host dispatch boundaries, not automatic interception of arbitrary clients.
The reference example consumes them for new effects and retains separate acquisition/publication validation
results. Retained-result reconciliation bypasses new-dispatch checks. Automatic integration with a general
Process runtime is not claimed by this API; that runtime does not own domain request handlers or ledger I/O.


### Group admission across forward and backfill consumers

`InMemoryIntegrationAdmissionGroup` supplies one local control scope for independently progressing
consumers. Create grouped members through `Register`; do not create separate standalone controllers
for those consumers. Both the group and each new member start Paused. The group policy and consumer
policy must authorize registration. A consumer may be registered once; exact registration replay
returns the original controller and attribution. Changing registration intent cannot replace it.

```csharp
var ownership = new InMemoryIntegrationLifecycleScope();
var group = ownership.CreateGroup(
    "research/tiingo/SPY", authorizeGroup, authorizeConsumer);
var forward = group.Register(new(forwardAddress, 0, actor, now)).Receipt.Controller;
var backfill = group.Register(new(backfillAddress, 1, actor, now)).Receipt.Controller;
forward.Change(new("forward-active", 0, IntegrationDesiredState.Active, actor, now));
backfill.Change(new("backfill-active", 0, IntegrationDesiredState.Active, actor, now));
group.Change(new("partition-active", 2, IntegrationDesiredState.Active, actor, now));

var intent = new IntegrationAdmissionRequest(
    forward.Bind(forwardCompilation), session,
    ExpectedRevision: 1, Authorization: actor, AtUtc: now);
var admitted = forward.Admit(intent);
group.Change(new("partition-pause", 3, IntegrationDesiredState.Paused, actor, now));
```

Group desired-state changes, registration and every member admission linearize on one shared lock.
New membership increments the operator control revision, but does not invalidate member admission requests.
Registration during a pause is allowed, but its controller remains governed by that pause even after
local activation or binding a new provider. Local resume cannot bypass group pause; group resume does
not override a local pause. Authorize callbacks run outside the lock; hosts own authentication and
policy freshness and must authorize membership for the intended tenant/source/partition.

Admission requires only the local expected revision. Under the shared admission lock the member reads
current group policy itself; a paused group returns `GroupAdmissionStopped`. Registration of unrelated
members cannot invalidate an in-flight admission request. The accepted receipt retains the group ID and
the group revision actually observed at admission. Group and membership commands remain revision-fenced
for operators. Exact admission replay remains available after group changes and rechecks consumer
authorization; replay retrieves evidence and never authorizes repeated effects.

Member inspection atomically returns local desired state plus a compact Group snapshot. `Accepting`
requires both Active; `Draining` also reflects a group pause. Group inspection returns a cached immutable snapshot via a volatile read. A snapshot is allocated only
when group state or membership changes; member inspection reuses it. Member ledger addresses and accepted coverage remain independent.
Successful completion, verified failure resolution and retained-result reconciliation remain available
while the group is paused; pause gates new sessions, not execution of already-admitted sessions.

The implementation retains membership and replay receipts for its lifetime. Membership is append-only:
no detach, controller transfer, nested groups or distributed/durable coordination is implemented.
Public construction is through `InMemoryIntegrationLifecycleScope.CreateConsumer/CreateGroup`; controller
constructors are internal. The scope rejects duplicate standalone creation and cross-group membership,
including concurrent attempts, so a grouped consumer cannot acquire a second unfenced controller in that
host scope. Use one scope per host control namespace; independent scopes intentionally represent separate
authorities. No process-global registry or cross-host exclusivity is claimed.
This is runtime control intent over existing canonical bindings, not a new integration authoring IR.
The new internal desired-state helper centralizes Change semantics for groups and members.


Concurrency/performance boundary: member Admit, Complete, ResolveFailure and Inspect still share the
group lock. This reference deliberately retains a simple atomic pause/admission order. It is not the
locking blueprint for a durable adapter, and no contention/throughput benchmark has been established.
Cached group snapshots remove repeated group-snapshot allocation but do not remove member-lock contention.
Replay returns one small `IntegrationLifecycleResult` class; it is not a
zero-allocation API. The group regression ceiling is now 64 bytes/call, allowing object-layout headroom
while ruling out an additional snapshot, and is exercised with zero and 1,000 other members.
