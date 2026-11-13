using System.Runtime.CompilerServices;

// Semantic blocks share the portable execution mechanism without making reference-interpreter internals public API.
[assembly: InternalsVisibleTo("Cohesive.Processes")]
[assembly: InternalsVisibleTo("Cohesive.Api.Execution")]
[assembly: InternalsVisibleTo("Cohesive.Relations")]
[assembly: InternalsVisibleTo("Cohesive.Transitions")]
[assembly: InternalsVisibleTo("Cohesive.Storage")]
// Native preparation shares owner-scoped success-only slots with semantic preparation.
[assembly: InternalsVisibleTo("Cohesive.Adapters.Postgres")]

[assembly: InternalsVisibleTo("Cohesive.Tests")]
[assembly: InternalsVisibleTo("Cohesive.Relations.Benchmarks")]

// Shared expression compiler conformance includes framework-owned parameter marker fixtures.
[assembly: InternalsVisibleTo("Cohesive.Relations.Tests")]

// Assurance interprets claim predicates through the same portable reference algebra.
[assembly: InternalsVisibleTo("Cohesive.Assurance")]
