# Package documentation

Package documentation has two layers.

- `README.md` is the package entry point for application developers. It explains the package's purpose, installation,
  ordinary conventions-driven authoring surface, current support boundary, and the next useful documents.
- `INTERNALS.md` and topic guides explain canonical IR, validation, lowering, capability evidence, execution,
  persistence, diagnostics, migration, and adapter implementation details.

The README is included in the NuGet package and should remain useful without requiring readers to understand the
compiler architecture first. Length is an outcome rather than a hard rule, but a block README should normally fit in
roughly 80-180 lines and an adapter README in roughly 60-140 lines.

## README shape

Use this order unless the package needs a materially different introduction:

1. One short statement of purpose.
2. Installation.
3. The ordinary use cases and current maturity or capability boundary.
4. One small conventions-driven C# example.
5. A compact explanation of what the example produces or enables.
6. Links to getting-started, internals, diagnostics, capabilities, migration, and related packages as applicable.

Keep support claims and important limitations in the README. Those are product-facing facts, not implementation
details. Move exhaustive operator inventories, wire formats, fingerprints, recovery algorithms, compiler phases,
provider evidence, and conformance rationale to the deeper documents.

## Authoring examples

Examples should use the same typed C# surface expected in application code. Prefer conventions for local structural
identity, naming, shape discovery, binding, and other deterministic defaults. Expose an identity only when the caller
owns it as an evolution or compatibility boundary. In particular, ordinary examples should not enumerate canonical
node IDs or construct fingerprints by hand.

Canonical IR still retains stable identities. Hiding convention-derived identities from ordinary authoring does not
remove them from persisted definitions, diagnostics, source maps, or explain output.

Where practical, an executable test or checked source excerpt owns each README example. Direct IR construction and
fully explicit identity authoring belong in `INTERNALS.md` unless the package itself is a low-level authoring or
compiler API.

## Website synchronization

The `cohesive` repository owns implemented API and capability claims. The sibling `cohesive-website` repository
projects those claims into narrative building-block pages. When a package README changes materially, review the
matching website overview, getting-started guide, and internals page in the same work item.

Shared code examples use named synchronization markers and are copied from `cohesive` into the website rather than
maintained independently. The website sync command and check are documented in its repository README.

Website prose may be more explanatory and visual, but it must not claim a broader capability closure, a different
authoring API, or a stronger guarantee than the package documentation and executable conformance evidence.
