# Fixture references for articles and tests

Use an explicit fixture scope to remove repeated URI, revision, fingerprint and catalog wiring:

```csharp
using Cohesive.Assurance;

var refs = ClaimReference.Fixtures("example://fulfillment", revision: "1");

var inventoryRef = refs.Define(
    "subjects/inventory",
    "Inventory with decimal OnHand and Reserved quantities.");

var fulfillmentRef = refs.Define(
    "subjects/fulfillment",
    "Order fulfillment relating payment, shipment, cancellation, and release.");

var observationRef = refs.Define(
    "contracts/observations",
    """
    Inventory states bind State.
    Successful reservations bind Before, Command, and After.
    Events retain OrderId in EventPayload; evaluation binds it to Trigger or Witness inputs.
    Observations carry semantic steps and monotone time in 100 ns ticks.
    Source coverage and finite-scope closure are explicit.
    """);

var sourceRef = refs.Define(
    "sources/article-claims",
    "The four claim declarations in the order fulfillment walkthrough.");

var configRef = refs.Define(
    "configuration/authoring",
    "Walkthrough fixture using typed claim authoring with decimal quantities.");

var inventoryPopulationRef = refs.Define(
    "selectors/inventory",
    "Inventory states in the selected fulfillment scope.");

var reserveTransitionRef = refs.Define(
    "selectors/reserve",
    "Successful inventory reservation commits.");

var shippedRef = refs.Define(
    "selectors/shipped",
    "Shipment-dispatched events carrying OrderId.");

var capturedRef = refs.Define(
    "selectors/captured",
    "Successful payment-capture events carrying OrderId.");

var cancellationWithReservationRef = refs.Define(
    "selectors/cancelled-with-reservation",
    "Order cancellations requiring release of an existing reservation.");

var inventoryReleasedRef = refs.Define(
    "selectors/inventory-released",
    "Completed inventory-release events carrying OrderId.");

// After authoring the claims:
var validation = claim.ValidateReferences(refs.References);
```

The declaration text stays explicit because it supplies the fixture's meaning. The scope owns the
namespace and revision, computes a versioned canonical content fingerprint through the core helper,
and collects references. `References` returns an immutable, canonically ordered snapshot: take it
after the declarations you intend to validate. A reference added later does not alter old snapshots.

Declaring the same path and exact text again is idempotent. Different text at an already-declared
path throws; create a new fixture revision or a distinct identity instead. Whitespace and line
endings in definitions are significant. Paths must be relative slash-separated identities with no
dot/parent segments or URI query, fragment or escape syntax. A trailing slash on the root is optional.
The scope is single-threaded authoring state, not a service registry.

These references identify **fixture declarations**. Their fingerprints do not attest that a real
entity, transition, selector, observation adapter or policy implements the supplied prose. For actual
Cohesive definitions, use owning-model references such as `ClaimReference.FromExecution(...)`.
`ValidateReferences` checks exact catalog membership, not the truth of declarations.

This uses a versioned canonical JSON content hash, not the previous article's raw UTF-8 text hash.
Existing hand-authored fixture fingerprints therefore change when adopting this helper; update the
fixture source and regenerate its derived claims together rather than rewriting retained evidence.
