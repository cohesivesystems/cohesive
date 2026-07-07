namespace Cohesive.Adapters.GraphQL;

/// <summary>
/// GraphQL schema views emitted from a Cohesive API definition.
/// </summary>
public sealed record GraphQLSchemaEmission
{
    /// <summary>
    /// Creates a schema emission.
    /// </summary>
    public GraphQLSchemaEmission(string sdl, string introspectionJson)
    {
        Sdl = Cohesive.Prelude.Guard.RequireNotNull(sdl);
        IntrospectionJson = Cohesive.Prelude.Guard.RequireNotNull(introspectionJson);
    }

    /// <summary>
    /// GraphQL schema definition language.
    /// </summary>
    public string Sdl { get; init; }

    /// <summary>
    /// GraphQL introspection result JSON.
    /// </summary>
    public string IntrospectionJson { get; init; }
}
