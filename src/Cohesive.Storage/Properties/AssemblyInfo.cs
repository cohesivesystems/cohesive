using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Cohesive.Tests")]

[assembly: InternalsVisibleTo("Cohesive.Adapters.SQLite.Tests")]

// Optional simulation interpretation decorates targets without expanding the production API.
[assembly: InternalsVisibleTo("Cohesive.Simulation.Transitions")]
