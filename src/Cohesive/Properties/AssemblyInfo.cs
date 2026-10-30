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

[assembly: InternalsVisibleTo("Cohesive.Machines")]
