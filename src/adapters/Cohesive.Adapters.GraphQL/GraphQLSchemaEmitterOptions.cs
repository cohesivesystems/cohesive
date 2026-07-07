namespace Cohesive.Adapters.GraphQL;

/// <summary>
/// Options for GraphQL schema emission.
/// </summary>
public sealed record GraphQLSchemaEmitterOptions
{
    /// <summary>
    /// Generated SDL file name.
    /// </summary>
    public string SchemaFileName { get; init; } = "schema.graphql";

    /// <summary>
    /// Generated introspection JSON file name.
    /// </summary>
    public string IntrospectionFileName { get; init; } = "schema.introspection.json";

    /// <summary>
    /// Human-readable schema name used in generated comments.
    /// </summary>
    public string SchemaName { get; init; } = "Cohesive API";

    /// <summary>
    /// Whether to include Cohesive operation-binding directives in the SDL.
    /// </summary>
    public bool IncludeCohesiveDirectives { get; init; } = true;

    /// <summary>
    /// Whether to emit indented introspection JSON.
    /// </summary>
    public bool WriteIndented { get; init; } = true;
}
