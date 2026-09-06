using System.Runtime.CompilerServices;

// Semantic blocks share the portable execution mechanism without making reference-interpreter internals public API.
[assembly: InternalsVisibleTo("Cohesive.Processes")]
[assembly: InternalsVisibleTo("Cohesive.Relations")]
[assembly: InternalsVisibleTo("Cohesive.Transitions")]
