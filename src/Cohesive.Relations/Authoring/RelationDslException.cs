namespace Cohesive.Relations.Authoring;

/// <summary>
/// Error thrown for invalid relation DSL expressions.
/// </summary>
public sealed class RelationDslException(string message) : Exception(message);
