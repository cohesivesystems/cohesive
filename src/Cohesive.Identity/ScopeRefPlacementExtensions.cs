namespace Cohesive.Identity;

/// <summary>
/// Placement helpers for physical interpretations of identity scopes.
/// </summary>
public static class ScopeRefPlacementExtensions
{
    extension(ScopeRef scope)
    {
        /// <summary>
        /// Resolves the physical partition key associated with this scope.
        /// </summary>
        public string ResolvePartitionKey()
        {
            ArgumentNullException.ThrowIfNull(scope);
            return string.IsNullOrWhiteSpace(scope.PartitionKey)
                ? Guard.RequireNotNullOrWhiteSpace(scope.Id).Trim()
                : scope.PartitionKey.Trim();
        }
    }
}
