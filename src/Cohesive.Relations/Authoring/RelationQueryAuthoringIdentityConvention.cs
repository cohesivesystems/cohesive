using System.Globalization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Versioned deterministic identities used by structural relation/query authoring.</summary>
/// <remarks>
/// Ordinals are scoped to their construct kind and count successfully committed declarations,
/// including declarations that carry explicit overrides. A failed declaration does not consume an
/// ordinal, while adding a valid explicit override does not renumber later convention-derived peers
/// of the same kind.
/// </remarks>
public static class RelationQueryAuthoringIdentityConvention
{
    /// <summary>Current structural-authoring identity convention version.</summary>
    public const string Version = "relation-query-authoring-identity/v1";

    /// <summary>Prefix reserved for convention-derived logical node identities.</summary>
    public const string NodePrefix = "relation-query-authoring:v1:node:";

    /// <summary>Prefix reserved for convention-derived value-binding identities.</summary>
    public const string BindingPrefix = "relation-query-authoring:v1:binding:";

    /// <summary>Prefix reserved for convention-derived parameter identities.</summary>
    public const string ParameterPrefix = "relation-query-authoring:v1:parameter:";

    /// <summary>Prefix reserved for convention-derived assignment identities.</summary>
    public const string AssignmentPrefix = "relation-query-authoring:v1:assignment:";

    /// <summary>Prefix reserved for convention-derived result identities.</summary>
    public const string ResultPrefix = "relation-query-authoring:v1:result:";

    /// <summary>Creates a deterministic logical node identity.</summary>
    /// <param name="kind">Stable logical-node kind.</param>
    /// <param name="ordinal">One-based declaration ordinal scoped to <paramref name="kind"/>.</param>
    /// <returns>A convention-derived logical node identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kind"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static QueryNodeId CreateNodeId(string kind, int ordinal) =>
        new(NodePrefix + Segment(kind, nameof(kind)) + ":" + Ordinal(ordinal, nameof(ordinal)));

    /// <summary>Creates a deterministic value-binding identity introduced by a node.</summary>
    /// <param name="node">Identity of the introducing logical node.</param>
    /// <param name="role">Stable binding role within the node.</param>
    /// <returns>A convention-derived value-binding identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="role"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty or white space.</exception>
    public static ValueBindingId CreateBindingId(QueryNodeId node, string role) =>
        new(BindingPrefix + Required(node.Value, nameof(node)) + ":" + Segment(role, nameof(role)));

    /// <summary>Creates a deterministic query-parameter identity.</summary>
    /// <param name="ordinal">One-based parameter declaration ordinal.</param>
    /// <returns>A convention-derived query-parameter identity.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static QueryParameterId CreateParameterId(int ordinal) =>
        new(ParameterPrefix + Ordinal(ordinal, nameof(ordinal)));

    /// <summary>Creates a deterministic assignment identity within a logical node.</summary>
    /// <param name="node">Identity of the owning projection or aggregate node.</param>
    /// <param name="role">Stable assignment role within the node.</param>
    /// <param name="ordinal">One-based assignment ordinal scoped to the role.</param>
    /// <returns>A convention-derived assignment identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="role"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static QueryAssignmentId CreateAssignmentId(QueryNodeId node, string role, int ordinal) =>
        new(
            AssignmentPrefix
            + Required(node.Value, nameof(node))
            + ":"
            + Segment(role, nameof(role))
            + ":"
            + Ordinal(ordinal, nameof(ordinal)));

    /// <summary>Creates a deterministic named-result identity.</summary>
    /// <param name="kind">Stable query-result kind.</param>
    /// <param name="ordinal">One-based declaration ordinal scoped to <paramref name="kind"/>.</param>
    /// <returns>A convention-derived query-result identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kind"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static QueryResultId CreateResultId(string kind, int ordinal) =>
        new(ResultPrefix + Segment(kind, nameof(kind)) + ":" + Ordinal(ordinal, nameof(ordinal)));

    static string Segment(string value, string parameterName) =>
        Required(value, parameterName).Replace(':', '-');

    static string Required(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An identity-convention segment is required.", parameterName);
        return value;
    }

    static string Ordinal(int value, string parameterName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "An identity ordinal must be positive.");
        return value.ToString("D4", CultureInfo.InvariantCulture);
    }
}
