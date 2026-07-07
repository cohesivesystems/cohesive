namespace Cohesive.Api;

/// <summary>
/// Marks a static member as an exported API definition for tooling discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method)]
public sealed class ApiDefinitionAttribute : Attribute;
