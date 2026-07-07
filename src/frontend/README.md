# Cohesive Frontend

This workspace contains framework-independent presentation runtime packages and
framework/design-system adapters.

Package boundaries should preserve the semantic model:

- `@cohesive/presentation-contracts` exports generated TypeScript contracts for
  `Cohesive.Presentation`.
- `@cohesive/presentation-core` contains pure TypeScript projection/runtime
  logic. It must not import React, router, query, table, design-system, editor,
  or product-specific modules.
- `@cohesive/presentation-react` contains React runtime integration.
- `@cohesive/presentation-react-shadcn` contains React shadcn renderers.
- `@cohesive/presentation-react-mui` contains React MUI renderers.
- `@cohesive/presentation-tailwind` contains framework-neutral Tailwind styles
  and tokens.
- `@cohesive/presentation-monaco` contains framework-neutral Monaco projection
  model helpers.
