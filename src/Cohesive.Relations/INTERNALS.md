# Cohesive.Relations internals

The [package README](README.md) is the application entry point. These documents retain the detailed semantic model,
compiler architecture, advanced authoring surfaces, execution contracts, and design rationale.

Read only the part relevant to the work at hand:

- [Semantic model](docs/internals/SEMANTIC_MODEL.md) explains facts, relationships, derivations, Relations, Queries,
  hosted Queries, and the primary C# entry point.
- [Relations and Queries](docs/internals/RELATIONS_AND_QUERIES.md) covers logical operators, valid-time joins,
  portable drafts, GraphQL boundaries, and external mutation/workflow relationships.
- [DTO mapping](docs/internals/DTO_MAPPING.md) covers expression and structural authoring, canonical evaluation,
  compiled mapping, PostgreSQL projection, federation, and dependency analysis.
- [Diagnostics and execution](docs/internals/DIAGNOSTICS_AND_EXECUTION.md) covers derivability, runtime evidence,
  requirement gaps, and the canonical in-memory interpreter.
- [Interpretations and use cases](docs/internals/INTERPRETATIONS_AND_USE_CASES.md) surveys execution and non-execution
  interpretations and the application problems supported by the shared semantic model.
- [Compilation and realization](docs/internals/COMPILATION_AND_REALIZATION.md) covers architectural principles,
  demand-driven compilation, capability matching, feasibility, binding, explain artifacts, physical planning, and
  materialization.
- [Portability and status](docs/internals/PORTABILITY_AND_STATUS.md) covers missing-data semantics, conventions,
  provenance, portable documents, query authority, current implementation status, and direction.

Task-oriented guides remain separate:

- [Getting started](docs/GETTING_STARTED.md)
- [Execution and adapters](docs/EXECUTION_AND_ADAPTERS.md)
- [Diagnostics](docs/DIAGNOSTICS.md)
- [Generated capability reference](docs/CAPABILITIES.md)
- [Migration](docs/MIGRATION.md)
