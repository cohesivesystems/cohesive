# Cohesive Frontend

This workspace contains framework-independent presentation runtime packages and
framework/design-system adapters.

Package boundaries should preserve the semantic model:

- `@cohesivesystems/presentation-contracts` exports generated TypeScript contracts for
  `Cohesive.Presentation`.
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
