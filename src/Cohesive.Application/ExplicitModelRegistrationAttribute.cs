namespace Cohesive.Application;

/// <summary>Requires an explicit model registration instead of automatic dependency construction.</summary>
/// <remarks>Use for environment-selected policy values. Registered instances, factories and types remain supported.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ExplicitModelRegistrationAttribute : Attribute;
