# Cohesive Frontend

This workspace contains framework-independent presentation runtime packages and
framework/design-system adapters.

Package boundaries should preserve the semantic model:

- `@cohesivesystems/presentation-contracts` exports generated TypeScript contracts for
  `Cohesive.Presentation`.
- `@cohesivesystems/processes` exports generated canonical Process definitions, inspect results, execution status,
  explanation and trace contracts, closed node/clause unions, and runtime discriminator inventories. Shared portable
  model contracts come from `@cohesivesystems/relations` rather than being regenerated.
- `@cohesivesystems/processes-presentation` projects an exact canonical Process document into a deterministic,
  immutable semantic graph and joins exact status/trace evidence into a separate immutable runtime overlay. It owns
  no layout or renderer state and retains canonical identities, references, source evidence, disclosure state, and
  structured projection diagnostics for downstream operator interfaces.
- `@cohesivesystems/presentation-core` contains pure TypeScript projection/runtime
  logic. It must not import React, router, query, table, design-system, editor,
  or product-specific modules.
- `@cohesivesystems/presentation-react` contains React runtime integration.
- `@cohesivesystems/presentation-react-shadcn` contains React shadcn renderers.
- `@cohesivesystems/presentation-react-mui` contains React MUI renderers.
- `@cohesivesystems/presentation-tailwind` contains framework-neutral Tailwind styles
  and tokens.
- `@cohesivesystems/presentation-monaco` contains framework-neutral Monaco projection
  model helpers.
